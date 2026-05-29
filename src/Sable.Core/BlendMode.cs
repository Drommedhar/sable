namespace Sable.Core;

/// <summary>
/// Layer blend modes. Integer values are the contract with the WGSL compositor
/// (composite.wgsl switches on these). Keep in sync. Full Affinity set lands
/// incrementally (PLAN §5A.4); this is the M1 core subset.
/// </summary>
public enum BlendMode
{
    Normal = 0,
    Multiply = 1,
    Screen = 2,
    Overlay = 3,
    Darken = 4,
    Lighten = 5,
    Add = 6,
}
