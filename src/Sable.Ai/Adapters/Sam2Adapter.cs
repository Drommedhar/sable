using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Sable.Ai.Backends;
using Sable.Ai.Imaging;
using Sable.Core.Ai;

namespace Sable.Ai.Adapters;

/// <summary>
/// SAM / SAM2 prompted segmentation (PHASE8_AI §8.3): an image ENCODER (run once) produces embeddings,
/// a DECODER turns embeddings + point/box prompts into a mask. Two ONNX files
/// (<c>Files[0]</c>=encoder, <c>Files[1]</c>=decoder). The image embedding is cached by image bytes so
/// repeated prompts on the same image skip re-encoding.
///
/// NOTE: SAM2 export I/O varies a lot (tensor names/shapes differ between samexporter / official /
/// vendor exports). This wires the COMMON layout (name-matched encoder→decoder + point_coords/labels/
/// mask_input/has_mask_input/orig_im_size) and is best-effort — a specific export may need a tweak.
/// The pure prompt geometry (<see cref="Sam2Ops"/>) is unit-tested independent of any of this.
/// </summary>
public sealed class Sam2Adapter : IMaskModel
{
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private readonly OnnxBackend _backend;
    private readonly string _encoderPath;
    private readonly string _decoderPath;
    private readonly int _size;
    private bool _cpu;

    /// <summary>Run on the CPU EP from the start (skip the GPU entirely). Set when a prior run found this
    /// GPU can't run SAM2 (it TDRs), so we never poison the process's DML device again.</summary>
    public bool ForceCpu { get => _cpu; set => _cpu = value; }

    /// <summary>True if a GPU attempt hit a device-lost this run and we fell back to CPU — the caller
    /// persists this so future runs go straight to CPU (<see cref="ForceCpu"/>).</summary>
    public bool FellBackToCpu { get; private set; }

    // cache: last encoded image (keyed by a cheap content hash) → encoder outputs by name
    private int _cachedKey;
    private Dictionary<string, DenseTensor<float>>? _cachedEmbed;
    private int _cachedW, _cachedH;

    public Sam2Adapter(OnnxBackend backend, string encoderPath, string decoderPath, int inputSize = 1024)
    {
        _backend = backend;
        _encoderPath = encoderPath;
        _decoderPath = decoderPath;
        _size = inputSize > 0 ? inputSize : 1024;
    }

    public Task<AiMask> SegmentAsync(AiImage img, IReadOnlyList<AiPrompt> prompts, CancellationToken ct = default)
        => Task.Run(() => WithCpuFallback(() => Run(img, prompts, ct)), ct);

    /// <summary>Run a GPU SAM2 op; on a device-lost (GPU TDR) drop the poisoned GPU sessions, switch to the
    /// CPU EP, and retry once. The CPU path is slow but won't hang the GPU. No retry if already on CPU.</summary>
    private T WithCpuFallback<T>(Func<T> op)
    {
        try { return op(); }
        catch (Exception ex) when (!_cpu && OnnxBackend.IsDeviceLost(ex))
        {
            _cpu = true;
            FellBackToCpu = true;
            _cachedEmbed = null; _cachedKey = 0;   // re-encode on CPU
            _backend.ResetGpuSessions();
            return op();
        }
    }

    private AiMask Run(AiImage img, IReadOnlyList<AiPrompt> prompts, CancellationToken ct)
    {
        var embed = Encode(img, ct);
        ct.ThrowIfCancellationRequested();
        var (coords, labels) = Sam2Ops.BuildPrompts(
            prompts.Count > 0 ? prompts : Sam2Ops.CentrePoint(img.Width, img.Height), img.Width, img.Height, _size);

        var dec = _backend.GetSession(_decoderPath, _cpu);
        var (plane, mw, mh, _) = DecodeBest(embed, dec, coords, labels, img);
        var maskSmall = ImageOps.MaskFromFloat(plane, mw, mh, sigmoid: true);
        ImageOps.AutoLevelsMask(maskSmall);
        var mask = ImageOps.ResizeGray(maskSmall, mw, mh, img.Width, img.Height);
        return new AiMask(mask, img.Width, img.Height);
    }

    /// <summary>Run the decoder for one prompt set; return the best (highest-IoU) mask plane + its score.</summary>
    private (float[] plane, int mw, int mh, float score) DecodeBest(
        Dictionary<string, DenseTensor<float>> embed, InferenceSession dec, float[] coords, float[] labels, AiImage img)
    {
        int n = labels.Length;
        var inputs = new List<NamedOnnxValue>();
        foreach (var name in dec.InputMetadata.Keys)
        {
            DenseTensor<float>? t = name switch
            {
                "point_coords" => new DenseTensor<float>(coords, new[] { 1, n, 2 }),
                "point_labels" => new DenseTensor<float>(labels, new[] { 1, n }),
                "mask_input" => new DenseTensor<float>(new float[1 * 1 * 256 * 256], new[] { 1, 1, 256, 256 }),
                "has_mask_input" => new DenseTensor<float>(new[] { 0f }, new[] { 1 }),
                "orig_im_size" => new DenseTensor<float>(new[] { (float)img.Height, img.Width }, new[] { 2 }),
                _ => embed.TryGetValue(name, out var e) ? e : null,
            };
            if (t is null && embed.Count == 1 && (name.Contains("embed") || name.Contains("image")))
                t = embed.Values.First();
            if (t is not null) inputs.Add(NamedOnnxValue.CreateFromTensor(name, t));
        }

        using var results = dec.Run(inputs);
        var masksOut = results.FirstOrDefault(r => r.Name.Contains("mask")) ?? results.First();
        var mt = masksOut.AsTensor<float>();
        var dims = mt.Dimensions;               // [1, M, mh, mw] or [1,1,mh,mw]
        int mh = dims[^2], mw = dims[^1];
        int stride = mh * mw;
        var arr = mt.ToArray();

        int best = 0;
        var iou = results.FirstOrDefault(r => r.Name.Contains("iou"))?.AsTensor<float>()?.ToArray();
        if (iou is { Length: > 1 }) for (int i = 1; i < iou.Length; i++) if (iou[i] > iou[best]) best = i;
        var plane = new float[stride];
        System.Array.Copy(arr, best * stride, plane, 0, stride);
        return (plane, mw, mh, iou is { Length: > 0 } ? iou[best] : 1f);
    }

    /// <summary>
    /// Automatic mask generation (PHASE8_AI §8.3b): encode once, run the decoder over an n×n seed-point
    /// grid, build object masks at a bounded working resolution, and NMS-dedupe — the precomputed
    /// objects the hover-to-select tool picks from. Reports progress 0..1 across the grid.
    /// </summary>
    public Task<List<ObjectMask>> SegmentEverythingAsync(
        AiImage img, int grid = 32, int maxSide = 384, IProgress<double>? progress = null, CancellationToken ct = default)
        => Task.Run(() => WithCpuFallback(() => SegmentEverything(img, grid, maxSide, progress, ct)), ct);

    private List<ObjectMask> SegmentEverything(AiImage img, int grid, int maxSide, IProgress<double>? progress, CancellationToken ct)
    {
        var embed = Encode(img, ct);
        var dec = _backend.GetSession(_decoderPath, _cpu);

        float scale = System.Math.Min(1f, (float)maxSide / System.Math.Max(img.Width, img.Height));
        int ww = System.Math.Max(1, (int)(img.Width * scale));
        int wh = System.Math.Max(1, (int)(img.Height * scale));
        int minArea = System.Math.Max(4, ww * wh / 2000);     // drop specks
        int maxArea = (int)(ww * wh * 0.95);                  // drop near-whole-image masks

        var pts = AmgOps.GridPoints(img.Width, img.Height, grid);
        var objs = new List<ObjectMask>(pts.Length);
        for (int i = 0; i < pts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (px, py) = pts[i];
            var (coords, labels) = Sam2Ops.BuildPrompts(
                new[] { new AiPrompt(AiPromptKind.Point, px, py, 0, 0, true) }, img.Width, img.Height, _size);
            var (plane, mw, mh, score) = DecodeBest(embed, dec, coords, labels, img);
            var small = ImageOps.MaskFromFloat(plane, mw, mh, sigmoid: true);
            ImageOps.AutoLevelsMask(small);
            var cov = ImageOps.ResizeGray(small, mw, mh, ww, wh);
            int area = 0, minx = ww, miny = wh, maxx = -1, maxy = -1;
            for (int yy = 0; yy < wh; yy++)
                for (int xx = 0; xx < ww; xx++)
                    if (cov[yy * ww + xx] > 127)
                    {
                        area++;
                        if (xx < minx) minx = xx; if (xx > maxx) maxx = xx;
                        if (yy < miny) miny = yy; if (yy > maxy) maxy = yy;
                    }
            if (area >= minArea && area <= maxArea)
                objs.Add(new ObjectMask(cov, ww, wh, area, score, minx, miny, maxx - minx + 1, maxy - miny + 1));
            // reserve the last 3% of the bar for NMS so 100% doesn't sit while it dedupes
            progress?.Report((double)(i + 1) / pts.Length * 0.97);
        }
        var kept = AmgOps.Nms(objs, 0.7f, minArea);
        progress?.Report(1.0);
        return kept;
    }

    private Dictionary<string, DenseTensor<float>> Encode(AiImage img, CancellationToken ct)
    {
        int key = ContentKey(img);
        if (_cachedEmbed is not null && key == _cachedKey && _cachedW == img.Width && _cachedH == img.Height)
            return _cachedEmbed;

        var enc = _backend.GetSession(_encoderPath, _cpu);
        var resized = ImageOps.ResizeRgba(img.Rgba, img.Width, img.Height, _size, _size);
        var chw = ImageOps.ToChwFloat(resized, _size, _size, Mean, Std);
        ct.ThrowIfCancellationRequested();

        var input = new DenseTensor<float>(chw, new[] { 1, 3, _size, _size });
        string inName = enc.InputMetadata.Keys.First();
        using var results = enc.Run(new[] { NamedOnnxValue.CreateFromTensor(inName, input) });

        var outs = new Dictionary<string, DenseTensor<float>>();
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            outs[r.Name] = new DenseTensor<float>(t.ToArray(), t.Dimensions.ToArray());   // clone (results dispose)
        }
        _cachedEmbed = outs; _cachedKey = key; _cachedW = img.Width; _cachedH = img.Height;
        return outs;
    }

    // cheap content hash so re-prompting the same image reuses the embedding (sample a stride of bytes)
    private static int ContentKey(AiImage img)
    {
        var b = img.Rgba;
        int h = 17 ^ b.Length;
        int step = System.Math.Max(1, b.Length / 4096);
        for (int i = 0; i < b.Length; i += step) h = h * 31 + b[i];
        return h;
    }
}
