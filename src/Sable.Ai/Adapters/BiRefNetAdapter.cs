using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sable.Ai.Backends;
using Sable.Ai.Imaging;
using Sable.Core.Ai;

namespace Sable.Ai.Adapters;

/// <summary>
/// Whole-image matting / background removal (PHASE8_AI §8.1) over a BiRefNet/RMBG/U²-Net ONNX model:
/// resize → ImageNet-normalize CHW → run → sigmoid mask → resize back to the source. No prompts.
/// The pre/post math lives in <see cref="ImageOps"/> (unit-tested); this wires it to a session.
/// </summary>
public sealed class BiRefNetAdapter : IMaskModel
{
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly OnnxBackend _backend;
    private readonly string _modelPath;
    private readonly int _inputSize;

    public BiRefNetAdapter(OnnxBackend backend, string modelPath, int inputSize = 1024)
    {
        _backend = backend;
        _modelPath = modelPath;
        _inputSize = inputSize > 0 ? inputSize : 1024;
    }

    public Task<AiMask> SegmentAsync(AiImage img, IReadOnlyList<AiPrompt> prompts, CancellationToken ct = default)
        => Task.Run(() => Run(img, ct), ct);

    private AiMask Run(AiImage img, CancellationToken ct)
    {
        var sess = _backend.GetSession(_modelPath);
        int s = _inputSize;

        var resized = ImageOps.ResizeRgba(img.Rgba, img.Width, img.Height, s, s);
        var chw = ImageOps.ToChwFloat(resized, s, s, Mean, Std);
        ct.ThrowIfCancellationRequested();

        var input = new DenseTensor<float>(chw, new[] { 1, 3, s, s });
        string inName = sess.InputMetadata.Keys.First();
        using var results = sess.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });

        var outArr = results.First().AsTensor<float>().ToArray();
        int hw = s * s;
        // models emit 1×1×S×S (sometimes a deep-supervision stack) → take the final H×W plane
        float[] plane = outArr.Length > hw ? outArr[^hw..] : outArr;

        var maskSmall = ImageOps.MaskFromFloat(plane, s, s, sigmoid: NeedsSigmoid(plane));
        var mask = ImageOps.ResizeGray(maskSmall, s, s, img.Width, img.Height);
        return new AiMask(mask, img.Width, img.Height);
    }

    /// <summary>If any value escapes [0,1] the model emitted logits → apply sigmoid.</summary>
    private static bool NeedsSigmoid(float[] v)
    {
        foreach (var x in v) if (x < -0.01f || x > 1.01f) return true;
        return false;
    }
}
