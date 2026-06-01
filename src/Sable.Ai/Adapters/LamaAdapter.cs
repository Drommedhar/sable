using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sable.Ai.Backends;
using Sable.Ai.Imaging;
using Sable.Core.Ai;

namespace Sable.Ai.Adapters;

/// <summary>
/// LaMa object removal / inpainting (PHASE8_AI §8.4): given the image + a mask of the region to erase,
/// fills it with plausible background — non-generative, no prompt. Inputs found by channel count
/// (3 = image, 1 = mask); the common Carve export is 512×512 fixed and outputs already-×255, so the
/// output range is auto-detected. Only the masked region is replaced (unmasked pixels stay exact).
/// </summary>
public sealed class LamaAdapter : IRasterModel
{
    private static readonly float[] Zero = { 0f, 0f, 0f };
    private static readonly float[] One = { 1f, 1f, 1f };

    private readonly OnnxBackend _backend;
    private readonly string _modelPath;

    public LamaAdapter(OnnxBackend backend, string modelPath)
    {
        _backend = backend;
        _modelPath = modelPath;
    }

    public Task<AiImage> ApplyAsync(AiImage img, AiMask? mask, AiParams p, CancellationToken ct = default)
        => Task.Run(() => Run(img, mask, ct), ct);

    private AiImage Run(AiImage img, AiMask? mask, CancellationToken ct)
    {
        if (mask is null) return img;   // nothing to remove
        // LaMa's Fast-Fourier convolutions don't run on DirectML → CPU provider (narrow exception, it's light)
        var sess = _backend.GetCpuSession(_modelPath);

        // identify the image (3-channel) and mask (1-channel) inputs + the model's fixed size
        string? imgName = null, maskName = null;
        int fh = 0, fw = 0;
        foreach (var kv in sess.InputMetadata)
        {
            var d = kv.Value.Dimensions;
            int ch = d.Length >= 3 ? d[^3] : 0;
            if (ch == 3) { imgName = kv.Key; fh = d[^2]; fw = d[^1]; }
            else if (ch == 1) maskName = kv.Key;
        }
        imgName ??= sess.InputMetadata.Keys.First();
        bool fixedShape = fh > 0 && fw > 0;
        int rw = fixedShape ? fw : img.Width;
        int rh = fixedShape ? fh : img.Height;

        var imgRgba = ImageOps.ResizeRgba(img.Rgba, img.Width, img.Height, rw, rh);
        var chw = ImageOps.ToChwFloat(imgRgba, rw, rh, Zero, One);   // 0..1
        var maskCov = ImageOps.ResizeGray(mask.Coverage, mask.Width, mask.Height, rw, rh);
        var maskT = new float[rw * rh];
        for (int i = 0; i < maskT.Length; i++) maskT[i] = maskCov[i] > 127 ? 1f : 0f;   // 1 = inpaint
        ct.ThrowIfCancellationRequested();

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(imgName, new DenseTensor<float>(chw, new[] { 1, 3, rh, rw })),
        };
        if (maskName is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(maskName, new DenseTensor<float>(maskT, new[] { 1, 1, rh, rw })));

        using var results = sess.Run(inputs);
        var outT = results.First().AsTensor<float>();
        var od = outT.Dimensions;
        int oh = od[^2], ow = od[^1];
        var outArr = outT.ToArray();

        // detect range: LaMa exports often output already-×255 (0..255); else 0..1
        float max = 0f; foreach (var v in outArr) if (v > max) max = v;
        float scale = max > 1.5f ? 1f : 255f;

        var inpaint = ChwToRgba(outArr, ow, oh, scale);                       // model-res inpaint
        var inpaintDoc = ImageOps.ResizeRgba(inpaint, ow, oh, img.Width, img.Height);

        // composite: keep original pixels outside the mask, take inpaint inside it (alpha preserved)
        var result = (byte[])img.Rgba.Clone();
        for (int i = 0; i < img.Width * img.Height; i++)
            if (mask.Coverage[i] > 127)
            {
                result[i * 4] = inpaintDoc[i * 4];
                result[i * 4 + 1] = inpaintDoc[i * 4 + 1];
                result[i * 4 + 2] = inpaintDoc[i * 4 + 2];
            }
        return new AiImage(result, img.Width, img.Height);
    }

    private static byte[] ChwToRgba(float[] chw, int w, int h, float scale)
    {
        var rgba = new byte[w * h * 4];
        int plane = w * h;
        for (int i = 0; i < plane; i++)
        {
            rgba[i * 4] = Q(chw[i] * scale);
            rgba[i * 4 + 1] = Q(chw[plane + i] * scale);
            rgba[i * 4 + 2] = Q(chw[2 * plane + i] * scale);
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static byte Q(float v) => (byte)System.Math.Clamp(v + 0.5f, 0, 255);
}
