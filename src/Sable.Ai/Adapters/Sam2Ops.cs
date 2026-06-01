using Sable.Core.Ai;

namespace Sable.Ai.Adapters;

/// <summary>
/// Pure prompt geometry for SAM/SAM2 (PHASE8_AI §8.3): turn document-pixel click/box prompts into the
/// model's point_coords / point_labels tensors (scaled to the encoder's square input). SAM label
/// convention: 1 = positive point, 0 = negative point, 2 = box top-left, 3 = box bottom-right. No ONNX
/// dependency → unit-testable without weights (the encoder/decoder run is in <see cref="Sam2Adapter"/>).
/// </summary>
public static class Sam2Ops
{
    /// <summary>
    /// Build flat point_coords (N×2, model space) + point_labels (N) from doc-pixel prompts.
    /// A box prompt expands to two labelled corner points.
    /// </summary>
    public static (float[] coords, float[] labels) BuildPrompts(
        IReadOnlyList<AiPrompt> prompts, int srcW, int srcH, int modelSize)
    {
        float sx = (float)modelSize / System.Math.Max(1, srcW);
        float sy = (float)modelSize / System.Math.Max(1, srcH);
        var coords = new List<float>();
        var labels = new List<float>();
        foreach (var p in prompts)
        {
            if (p.Kind == AiPromptKind.Box)
            {
                coords.Add(p.X0 * sx); coords.Add(p.Y0 * sy); labels.Add(2f);
                coords.Add(p.X1 * sx); coords.Add(p.Y1 * sy); labels.Add(3f);
            }
            else
            {
                coords.Add(p.X0 * sx); coords.Add(p.Y0 * sy);
                labels.Add(p.Positive ? 1f : 0f);
            }
        }
        return (coords.ToArray(), labels.ToArray());
    }

    /// <summary>A single positive point at the centre of the image (the "Select Subject" default prompt).</summary>
    public static IReadOnlyList<AiPrompt> CentrePoint(int srcW, int srcH)
        => new[] { new AiPrompt(AiPromptKind.Point, srcW * 0.5f, srcH * 0.5f, 0, 0, Positive: true) };
}
