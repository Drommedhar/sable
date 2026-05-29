namespace Sable.Engine.Layers;

/// <summary>
/// A layer group (PLAN §4): a tree node holding child layers. The compositor
/// composites the children into their own buffer, then blends that result into the
/// parent with the group's blend mode / opacity / mask (isolated grouping;
/// pass-through is a follow-up). Children are bottom→top like the document.
/// </summary>
public sealed class GroupLayer : Layer
{
    public List<Layer> Children { get; } = new();

    public GroupLayer(string name = "Group")
    {
        Name = name;
    }
}
