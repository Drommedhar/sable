using System;
using System.Collections.Generic;
using System.Threading;
using Sable.Engine;
using Sable.Plugin.Sdk.Automation;

namespace Sable.Plugins.Engine;

/// <summary>
/// Engine-backed <see cref="IBatchApi"/> for one batch run (capability <c>automation.batch</c>).
/// Open/save are injected by the host (the app provides format dispatch + the GPU compositor); this
/// class just tracks the active document and routes the plugin's calls. Opening a document makes it
/// the host's "active document" via <paramref name="setActive"/>, so the layer/pixel/selection APIs
/// the plugin uses during the run target the batch document, not the on-screen tab. Headless-testable:
/// inject in-memory open/save delegates.
/// </summary>
public sealed class BatchContext : IBatchApi
{
    private readonly Func<string, Document?> _open;
    private readonly Func<Document, string, bool> _save;
    private readonly Action<Document?> _setActive;
    private readonly IProgress<(double Fraction, string? Status)>? _progress;
    private Document? _active;

    public BatchContext(
        IReadOnlyList<string> inputFiles, CancellationToken cancellation,
        Func<string, Document?> open, Func<Document, string, bool> save,
        Action<Document?> setActive, IProgress<(double, string?)>? progress = null)
    {
        InputFiles = inputFiles;
        Cancellation = cancellation;
        _open = open;
        _save = save;
        _setActive = setActive;
        _progress = progress;
    }

    public IReadOnlyList<string> InputFiles { get; }
    public CancellationToken Cancellation { get; }

    public bool OpenDocument(string path)
    {
        var doc = _open(path);
        if (doc is null) return false;
        _active = doc;
        _setActive(doc);
        return true;
    }

    public bool SaveDocument(string path) => _active is { } d && _save(d, path);

    public void CloseDocument()
    {
        _active = null;
        _setActive(null);
    }

    public void Report(double fraction, string? status = null)
        => _progress?.Report((Math.Clamp(fraction, 0, 1), status));
}
