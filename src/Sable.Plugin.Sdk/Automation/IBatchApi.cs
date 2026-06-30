namespace Sable.Plugin.Sdk.Automation;

/// <summary>
/// Headless batch execution context (capability <c>automation.batch</c>). The host invokes a
/// plugin's batch handler with this; the plugin iterates <see cref="InputFiles"/>, opens each
/// via the host, edits through the layer/command APIs, and reports progress. Honour
/// <see cref="Cancellation"/> between items (PLUGIN_SDK_PLAN.md §4 host requirements).
/// </summary>
public interface IBatchApi
{
    /// <summary>Files the user queued for this batch run (absolute paths).</summary>
    IReadOnlyList<string> InputFiles { get; }

    /// <summary>Open a document file headlessly; returns true on success.</summary>
    bool OpenDocument(string path);

    /// <summary>Save the active document to <paramref name="path"/>; returns true on success.</summary>
    bool SaveDocument(string path);

    /// <summary>Close the active document without saving.</summary>
    void CloseDocument();

    void Report(double fraction, string? status = null);

    CancellationToken Cancellation { get; }
}
