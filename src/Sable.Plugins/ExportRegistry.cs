using System;
using System.Collections.Generic;
using System.Linq;
using Sable.Plugin.Sdk.Export;

namespace Sable.Plugins;

/// <summary>
/// Host-side registry of export-format providers (PLUGIN_SDK_PLAN §29 / boundary-map "best first
/// seam"). Implements the SDK <see cref="IExportApi"/> so a plugin granted <c>export.provider</c>
/// can contribute a format; the app also registers its built-in formats here and drives the export
/// UI from <see cref="Providers"/>. Registration is idempotent (re-registering the same id replaces
/// the prior provider), so a plugin reload doesn't duplicate rows.
/// </summary>
public sealed class ExportRegistry : IExportApi
{
    private readonly List<IExportProvider> _providers = new();

    /// <summary>All registered providers in registration order (built-ins first when the app seeds them).</summary>
    public IReadOnlyList<IExportProvider> Providers => _providers;

    public void Register(IExportProvider provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("export provider Id is required", nameof(provider));

        int i = _providers.FindIndex(p => p.Id == provider.Id);
        if (i >= 0) _providers[i] = provider;   // replace in place (idempotent re-register)
        else _providers.Add(provider);
    }

    /// <summary>Remove a provider by id; returns true if one was removed.</summary>
    public bool Unregister(string id) => _providers.RemoveAll(p => p.Id == id) > 0;

    public IExportProvider? ById(string id) => _providers.FirstOrDefault(p => p.Id == id);

    /// <summary>First provider for a file extension (case-insensitive, leading dot tolerated).</summary>
    public IExportProvider? ByExtension(string ext)
    {
        var e = ext.TrimStart('.');
        return _providers.FirstOrDefault(p => string.Equals(p.Extension, e, StringComparison.OrdinalIgnoreCase));
    }
}
