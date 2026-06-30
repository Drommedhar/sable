namespace Sable.Plugin.Sdk.Document;

/// <summary>
/// Read access to the active document (capability <c>document.read</c>). Available on
/// <see cref="Host.IHostContext.Document"/> only when granted, else that property is null.
/// </summary>
public interface IDocumentApi
{
    /// <summary>Snapshot of the active document, or null when no document is open.</summary>
    DocumentInfo? Active { get; }
}
