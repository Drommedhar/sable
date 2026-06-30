using System.Collections.Generic;
using Sable.Plugin.Sdk.Manifest;
using Sable.Plugins;

namespace Sable.Tests;

/// <summary>User-consent fingerprinting for plugins (PLUGIN_SDK_PLAN §12).</summary>
public sealed class PluginConsentTests
{
    private static PluginManifest Manifest(string caps, bool network = false)
        => ManifestParser.Parse($$"""
        {
          "id": "com.example.c", "name": "C", "version": "1.0.0", "sdk_version": "1", "entrypoint": "X.Y",
          "capabilities": {{caps}},
          "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": {{(network ? "true" : "false")}}, "gpu": false }
        }
        """).Manifest!;

    [Fact]
    public void Fingerprint_is_order_independent()
    {
        var a = PluginConsent.Fingerprint(Manifest("""["document.read","command.register"]"""));
        var b = PluginConsent.Fingerprint(Manifest("""["command.register","document.read"]"""));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fingerprint_changes_when_access_widens()
    {
        var narrow = PluginConsent.Fingerprint(Manifest("""["document.read"]"""));
        var wider = PluginConsent.Fingerprint(Manifest("""["document.read","layer.write.basic"]"""));
        Assert.NotEqual(narrow, wider);

        var net = PluginConsent.Fingerprint(Manifest("""["document.read"]""", network: true));
        Assert.NotEqual(narrow, net);   // a new permission also changes it
    }

    [Fact]
    public void IsApproved_requires_an_exact_match()
    {
        var m = Manifest("""["document.read"]""");
        var approved = new Dictionary<string, string> { [m.Id] = PluginConsent.Fingerprint(m) };
        Assert.True(PluginConsent.IsApproved(approved, m));

        // same plugin id but now asks for more → no longer approved (must re-consent)
        var wider = Manifest("""["document.read","layer.write.basic"]""");
        Assert.False(PluginConsent.IsApproved(approved, wider));
        Assert.False(PluginConsent.IsApproved(new Dictionary<string, string>(), m));
    }

    [Fact]
    public void DescribeRequest_lists_capabilities_and_permissions()
    {
        var text = PluginConsent.DescribeRequest(Manifest("""["document.read"]""", network: true));
        Assert.Contains("document.read", text);
        Assert.Contains("network", text);
    }
}
