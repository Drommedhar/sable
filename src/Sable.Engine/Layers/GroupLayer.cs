namespace Sable.Engine.Layers;

/// <summary>
/// A layer group (PLAN §4): a tree node holding child layers. The compositor
/// composites the children into their own buffer, then blends that result into the
/// parent with the group's blend mode / opacity / mask (isolated grouping;
/// pass-through is a follow-up). Children are bottom→top like the document.
/// </summary>
public sealed class GroupLayer : Layer
{
    public GroupLayer(string name = "Group")
    {
        Name = name;
    }

    // Children live on the base Layer now (any layer can hold children). The base
    // Clone() deep-copies them, so CreateClone just makes the typed shell.
    protected override Layer CreateClone() => new GroupLayer(Name);
}
