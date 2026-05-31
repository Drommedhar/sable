using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sable.Ai.Backends;
using Sable.Ai.Imaging;
using Sable.Core.Ai;

namespace Sable.Ai.Adapters;

/// <summary>
/// Single-pass super-resolution (PHASE8_AI §8.2) over a Real-ESRGAN/SwinIR ONNX model: RGB 0..1 in,
/// RGB 0..1 out at the model's fixed scale (x2/x4). Operates on whatever image it's handed — the
/// caller (<see cref="Tiling.TileInference"/>) splits big images into tiles and feather-merges, so
/// this stays a plain upscaler. Pre/post math is in <see cref="ImageOps"/> (unit-tested).
/// </summary>
public sealed class EsrganAdapter : IRasterModel
{
    private static readonly float[] Zero = { 0f, 0f, 0f };
    private static readonly float[] One = { 1f, 1f, 1f };

    private readonly OnnxBackend _backend;
    private readonly string _modelPath;

    public EsrganAdapter(OnnxBackend backend, string modelPath)
    {
        _backend = backend;
        _modelPath = modelPath;
    }

    public Task<AiImage> ApplyAsync(AiImage img, AiMask? mask, AiParams p, CancellationToken ct = default)
        => Task.Run(() => Run(img, ct), ct);

    /// <summary>
    /// The model's fixed square input size (e.g. 128 for the Qualcomm AI-Hub export), or 0 if the input
    /// is dynamic. The upscaler should feed tiles of this size so no quality-killing down/up resize is
    /// needed. Loads the session.
    /// </summary>
    public int PreferredInputTile()
    {
        var dims = _backend.GetSession(_modelPath).InputMetadata.Values.First().Dimensions;   // [1,3,H,W]
        int ih = dims[^2], iw = dims[^1];
        return (ih > 0 && iw > 0 && ih == iw) ? ih : 0;
    }

    private AiImage Run(AiImage img, CancellationToken ct)
    {
        var sess = _backend.GetSession(_modelPath);
        var inDims = sess.InputMetadata.Values.First().Dimensions;   // [1,3,H,W]; <=0 = dynamic
        int fh = inDims[^2], fw = inDims[^1];
        bool fixedShape = fh > 0 && fw > 0;

        // for a fixed-shape model, resize the tile to the model's input; otherwise run it as-is
        int rw = fixedShape ? fw : img.Width;
        int rh = fixedShape ? fh : img.Height;
        var inRgba = fixedShape ? ImageOps.ResizeRgba(img.Rgba, img.Width, img.Height, rw, rh) : img.Rgba;
        var chw = ImageOps.ToChwFloat(inRgba, rw, rh, Zero, One);   // 0..1, no normalization
        ct.ThrowIfCancellationRequested();

        var input = new DenseTensor<float>(chw, new[] { 1, 3, rh, rw });
        string inName = sess.InputMetadata.Keys.First();
        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });

        var outT = results.First().AsTensor<float>();
        var dims = outT.Dimensions;            // [1,3,rh*f,rw*f]
        int oh = dims[^2], ow = dims[^1];
        var outRgba = ImageOps.ChwFloatToRgba(outT.ToArray(), ow, oh);

        if (!fixedShape) return new AiImage(outRgba, ow, oh);   // dynamic: output is already img×scale

        // fixed: rescale the model output (rw·f × rh·f) back to the original tile × scale
        int scale = System.Math.Max(1, ow / rw);
        int finalW = img.Width * scale, finalH = img.Height * scale;
        var scaled = ImageOps.ResizeRgba(outRgba, ow, oh, finalW, finalH);
        return new AiImage(scaled, finalW, finalH);
    }
}
