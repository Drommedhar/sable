namespace Sable.Plugin.Sdk;

/// <summary>Base for exceptions a plugin may legitimately encounter from the host.</summary>
public class PluginException : Exception
{
    public PluginException(string message) : base(message) { }
    public PluginException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by the host when a plugin invokes an API for a capability it did not declare /
/// was not granted. Distinct type so the host can log it as a manifest mistake, not a bug.
/// </summary>
public sealed class PluginCapabilityException : PluginException
{
    public PluginCapabilityException(string capability)
        : base($"plugin used an API requiring capability '{capability}', which it did not request")
        => Capability = capability;

    public string Capability { get; }
}

/// <summary>Thrown when a plugin attempts an action its granted permissions forbid.</summary>
public sealed class PluginPermissionException : PluginException
{
    public PluginPermissionException(string permission)
        : base($"plugin attempted an action requiring permission '{permission}', which was not granted")
        => Permission = permission;

    public string Permission { get; }
}
