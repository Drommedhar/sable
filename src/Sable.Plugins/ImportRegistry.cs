using System;
using System.Collections.Generic;
using System.Linq;
using Sable.Plugin.Sdk.Import;

namespace Sable.Plugins;

/// <summary>
/// Host-side registry of import-format providers (mirror of <see cref="ExportRegistry"/>).
/// Implements the SDK <see cref="IImportApi"/> so a plugin granted <c>import.provider</c> can add an
/// open-format; the app consults it (by file extension) in the Open path before its built-in
/// decoders. Registration is idempotent (re-registering the same id replaces the prior provider).
/// </summary>
public sealed class ImportRegistry : IImportApi
{
    private readonly List<IImportProvider> _providers = new();

    public IReadOnlyList<IImportProvider> Providers => _providers;

    public void Register(IImportProvider provider)
    {
        if (provider is null) throw new ArgumentNullException(nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.Id))
            throw new ArgumentException("import provider Id is required", nameof(provider));

        int i = _providers.FindIndex(p => p.Id == provider.Id);
        if (i >= 0) _providers[i] = provider;
        else _providers.Add(provider);
    }

    public bool Unregister(string id) => _providers.RemoveAll(p => p.Id == id) > 0;

    public IImportProvider? ById(string id) => _providers.FirstOrDefault(p => p.Id == id);

    /// <summary>First provider that handles a file extension (case-insensitive, leading dot tolerated).</summary>
    public IImportProvider? ByExtension(string ext)
    {
        var e = ext.TrimStart('.');
        return _providers.FirstOrDefault(p => p.Extensions.Any(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>All handled extensions across providers (lowercase, no dot) — for the Open dialog filter.</summary>
    public IReadOnlyList<string> AllExtensions()
        => _providers.SelectMany(p => p.Extensions).Select(e => e.ToLowerInvariant()).Distinct().ToList();
}
