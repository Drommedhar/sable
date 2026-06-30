using System;
using System.Collections.Generic;
using Sable.Plugin.Sdk.Import;
using Sable.Plugins;

namespace Sable.Tests;

/// <summary>Host import-provider registry (mirror of the export seam).</summary>
public sealed class ImportRegistryTests
{
    private sealed class FakeProvider : IImportProvider
    {
        public FakeProvider(string id, params string[] ext) { Id = id; Extensions = ext; }
        public string Id { get; }
        public string Label => Id;
        public IReadOnlyList<string> Extensions { get; }
        public ImportImage Decode(byte[] data) => new() { Width = 1, Height = 1, Rgba = new byte[4] };
    }

    [Fact]
    public void Looks_up_by_extension_case_insensitively()
    {
        var r = new ImportRegistry();
        r.Register(new FakeProvider("exr", "exr"));
        r.Register(new FakeProvider("hdr", "hdr", "pic"));

        Assert.Equal("exr", r.ByExtension(".EXR")!.Id);
        Assert.Equal("hdr", r.ByExtension("pic")!.Id);
        Assert.Null(r.ByExtension("png"));
        Assert.Equal(new[] { "exr", "hdr", "pic" }, r.AllExtensions());
    }

    [Fact]
    public void Reregister_replaces_and_unregister_removes()
    {
        var r = new ImportRegistry();
        r.Register(new FakeProvider("exr", "exr"));
        r.Register(new FakeProvider("exr", "exr2"));
        Assert.Single(r.Providers);
        Assert.Equal("exr2", r.Providers[0].Extensions[0]);

        Assert.True(r.Unregister("exr"));
        Assert.Empty(r.Providers);
    }

    [Fact]
    public void Rejects_empty_id()
        => Assert.Throws<ArgumentException>(() => new ImportRegistry().Register(new FakeProvider("", "x")));
}
