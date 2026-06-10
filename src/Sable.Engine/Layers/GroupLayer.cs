namespace Sable.Engine.Layers;

/// <summary>
/// A layer group (PLAN §4): a tree node holding child layers. Isolated by default —
/// the compositor composites the children into their own buffer, then blends that
/// result into the parent with the group's blend mode / opacity / mask. With
/// <see cref="PassThrough"/> the children composite straight onto the backdrop
/// (Photoshop's default group mode): adjustments/filters inside affect everything
/// below the group, and the group's own blend mode is ignored. Children are
/// bottom→top like the document.
/// </summary>
public sealed class GroupLayer : Layer
{
    public GroupLayer(string name = "Group")
    {
        Name = name;
    }

    /// <summary>Composite children directly onto the backdrop (no isolation buffer).
    /// Opacity and mask still apply as a crossfade between backdrop and result.</summary>
    public bool PassThrough { get; set; }

    // Children live on the base Layer now (any layer can hold children). The base
    // Clone() deep-copies them, so CreateClone just makes the typed shell.
    protected override Layer CreateClone() => new GroupLayer(Name) { PassThrough = PassThrough };
}
