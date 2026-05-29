using Sable.Engine.Layers;

namespace Sable.Engine.Compositing;

/// <summary>A live brush dab to composite into the stack for preview (doc-space center/radius).</summary>
public readonly record struct PreviewDab(
    Layer Layer, float Cx, float Cy, float Radius, float Hardness,
    byte R, byte G, byte B, bool Erase,
    bool IsClone = false, int CloneOffX = 0, int CloneOffY = 0);
