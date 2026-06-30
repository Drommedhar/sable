using System;
using System.Collections.Generic;

namespace Sable.Plugins;

/// <summary>
/// Fan-out for document change notifications to subscribed plugins (capability
/// <c>document.events</c>). Handlers are kept per owning plugin id so they can be dropped on
/// disable/uninstall (a handler delegate roots the plugin's load context — see the contribution
/// model). The host raises the three signals; the app decides WHEN (it polls doc/selection
/// versions + fires on tab switch). Pure + engine-free, so it's headless-testable.
/// </summary>
public sealed class DocumentEventHub
{
    private readonly Dictionary<string, List<Action>> _doc = new();
    private readonly Dictionary<string, List<Action>> _sel = new();
    private readonly Dictionary<string, List<Action>> _active = new();

    public void OnDocumentChanged(string owner, Action handler) => Add(_doc, owner, handler);
    public void OnSelectionChanged(string owner, Action handler) => Add(_sel, owner, handler);
    public void OnActiveDocumentChanged(string owner, Action handler) => Add(_active, owner, handler);

    public void RaiseDocumentChanged() => Fire(_doc);
    public void RaiseSelectionChanged() => Fire(_sel);
    public void RaiseActiveDocumentChanged() => Fire(_active);

    /// <summary>Drop every handler a plugin registered (called on disable/uninstall).</summary>
    public void RemoveOwner(string owner)
    {
        _doc.Remove(owner);
        _sel.Remove(owner);
        _active.Remove(owner);
    }

    public void Clear()
    {
        _doc.Clear();
        _sel.Clear();
        _active.Clear();
    }

    /// <summary>True when at least one handler is registered (lets the host skip the version poll).</summary>
    public bool HasSubscribers => _doc.Count > 0 || _sel.Count > 0 || _active.Count > 0;

    private static void Add(Dictionary<string, List<Action>> map, string owner, Action handler)
    {
        if (handler is null) return;
        if (!map.TryGetValue(owner, out var list)) map[owner] = list = new List<Action>();
        list.Add(handler);
    }

    private static void Fire(Dictionary<string, List<Action>> map)
    {
        // snapshot — a handler could (in)directly mutate the map
        foreach (var list in new List<List<Action>>(map.Values))
            foreach (var h in new List<Action>(list))
                h();
    }
}
