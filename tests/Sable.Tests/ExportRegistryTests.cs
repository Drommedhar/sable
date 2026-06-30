using System;
using Sable.Plugin.Sdk.Export;
using Sable.Plugins;

namespace Sable.Tests;

/// <summary>Host export-provider registry (PLUGIN_SDK_PLAN §29 export seam).</summary>
public sealed class ExportRegistryTests
{
    private sealed class FakeProvider : IExportProvider
    {
        public FakeProvider(string id, string ext) { Id = id; Extension = ext; }
        public string Id { get; }
        public string Label => Id;
        public string Extension { get; }
        public bool SupportsAlpha => true;
        public byte[] Encode(ExportImage image, ExportOptions options) => Array.Empty<byte>();
    }

    [Fact]
    public void Register_keeps_order_and_looks_up_by_id_and_extension()
    {
        var r = new ExportRegistry();
        r.Register(new FakeProvider("exr", "exr"));
        r.Register(new FakeProvider("avif", "avif"));

        Assert.Equal(new[] { "exr", "avif" }, r.Providers.Select(p => p.Id));
        Assert.Equal("avif", r.ById("avif")!.Id);
        Assert.Equal("exr", r.ByExtension(".EXR")!.Id);   // dot + case tolerant
        Assert.Null(r.ById("nope"));
        Assert.Null(r.ByExtension("png"));
    }

    [Fact]
    public void Reregistering_same_id_replaces_in_place()
    {
        var r = new ExportRegistry();
        r.Register(new FakeProvider("exr", "exr"));
        r.Register(new FakeProvider("exr", "exr2"));   // same id, new instance

        Assert.Single(r.Providers);
        Assert.Equal("exr2", r.Providers[0].Extension);
    }

    [Fact]
    public void Unregister_removes()
    {
        var r = new ExportRegistry();
        r.Register(new FakeProvider("exr", "exr"));
        Assert.True(r.Unregister("exr"));
        Assert.False(r.Unregister("exr"));
        Assert.Empty(r.Providers);
    }

    [Fact]
    public void Register_rejects_empty_id()
    {
        var r = new ExportRegistry();
        Assert.Throws<ArgumentException>(() => r.Register(new FakeProvider("", "x")));
    }
}
