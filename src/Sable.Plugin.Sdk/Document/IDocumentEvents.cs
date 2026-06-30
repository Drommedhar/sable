using System;

namespace Sable.Plugin.Sdk.Document;

/// <summary>
/// Subscribe to document change notifications (capability <c>document.events</c>). Null on
/// <see cref="Host.IHostContext.Events"/> when not granted. Register handlers in
/// <c>Initialize</c>; the host invokes them on the UI thread (coalesced — you get "something
/// changed", not a diff). Read the current state via the document/layer/selection APIs in response.
/// Handlers are dropped automatically when the plugin is disabled/uninstalled.
/// </summary>
public interface IDocumentEvents
{
    /// <summary>The active document's content or layer structure changed (an edit, undo, or redo).</summary>
    void OnDocumentChanged(Action handler);

    /// <summary>The active document's selection changed.</summary>
    void OnSelectionChanged(Action handler);

    /// <summary>A different document became active (tab switch / open / close).</summary>
    void OnActiveDocumentChanged(Action handler);
}
