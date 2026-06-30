using System.Text.Json;
using Sable.Plugin.Sdk.Capabilities;
using Sable.Plugin.Sdk.Permissions;

namespace Sable.Plugin.Sdk.Manifest;

/// <summary>
/// Parses + validates a plugin manifest JSON document (PLUGIN_SDK_PLAN.md §16).
/// Pure: no filesystem, no reflection — feed it the raw JSON text. Collects ALL errors
/// (missing required fields, bad SDK version, unknown capabilities, malformed permissions)
/// rather than failing on the first, so plugin authors get a complete report.
///
/// Manifest keys are snake_case to match the plan's example schema.
/// </summary>
public static class ManifestParser
{
    public static ManifestParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ManifestParseResult.Failure("manifest is empty");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return ManifestParseResult.Failure($"manifest is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ManifestParseResult.Failure("manifest root must be a JSON object");

            var errors = new List<string>();

            var id = ReqString(root, "id", errors);
            var name = ReqString(root, "name", errors);
            var version = ReqString(root, "version", errors);
            var sdkVersionRaw = ReqString(root, "sdk_version", errors);
            var entrypoint = ReqString(root, "entrypoint", errors);

            // SDK version: must parse to a major and be compatible with this host SDK.
            int sdkMajor = 0;
            if (sdkVersionRaw is not null)
            {
                if (!SdkVersion.TryParseMajor(sdkVersionRaw, out sdkMajor))
                    errors.Add($"sdk_version '{sdkVersionRaw}' is not a valid version");
                else if (!SdkVersion.IsCompatible(sdkMajor))
                    errors.Add($"sdk_version {sdkMajor} is incompatible with host SDK {SdkVersion.Current} " +
                               $"(supported {SdkVersion.MinSupportedMajor}..{SdkVersion.Current})");
            }

            var capabilities = ParseCapabilities(root, errors);
            var permissions = ParsePermissions(root, errors);

            // Identity sanity (cheap, catches obvious copy-paste manifests).
            if (id is not null && !LooksLikeId(id))
                errors.Add($"id '{id}' should be a reverse-DNS identifier (e.g. com.example.plugin)");

            if (errors.Count > 0)
                return ManifestParseResult.Failure(errors);

            var manifest = new PluginManifest
            {
                Id = id!,
                Name = name!,
                Version = version!,
                SdkVersion = sdkVersionRaw!,
                SdkMajor = sdkMajor,
                Entrypoint = entrypoint!,
                Capabilities = capabilities,
                Permissions = permissions,
                Author = OptString(root, "author"),
                Website = OptString(root, "website"),
                Support = OptString(root, "support"),
                MinHostVersion = OptString(root, "min_host_version"),
            };
            return ManifestParseResult.Success(manifest);
        }
    }

    private static IReadOnlyList<string> ParseCapabilities(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("capabilities", out var caps))
        {
            errors.Add("missing required field: capabilities");
            return Array.Empty<string>();
        }
        if (caps.ValueKind != JsonValueKind.Array)
        {
            errors.Add("capabilities must be an array of strings");
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var el in caps.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String)
            {
                errors.Add("capabilities entries must be strings");
                continue;
            }
            var cap = el.GetString()!;
            if (!Capability.IsKnown(cap))
                errors.Add($"unknown capability: '{cap}'");
            else if (list.Contains(cap))
                errors.Add($"duplicate capability: '{cap}'");
            else
                list.Add(cap);
        }
        if (list.Count == 0 && errors.Count == 0)
            errors.Add("a plugin must declare at least one capability");
        return list;
    }

    private static PluginPermissions ParsePermissions(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("permissions", out var perms))
            return PluginPermissions.None; // permissions are optional; absence = deny-all
        if (perms.ValueKind != JsonValueKind.Object)
        {
            errors.Add("permissions must be an object");
            return PluginPermissions.None;
        }

        return new PluginPermissions
        {
            FilesystemRead = Scope(perms, "filesystem_read", errors),
            FilesystemWrite = Scope(perms, "filesystem_write", errors),
            Network = Flag(perms, "network", errors),
            Gpu = Flag(perms, "gpu", errors),
            Clipboard = Flag(perms, "clipboard", errors),
            ExternalProcess = Flag(perms, "external_process", errors),
            DocumentMetadata = Flag(perms, "document_metadata", errors),
        };
    }

    private static PermissionScope Scope(JsonElement perms, string key, List<string> errors)
    {
        if (!perms.TryGetProperty(key, out var v)) return PermissionScope.None;
        string? text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
        if (text is null || !PluginPermissions.TryParseScope(text, out var scope))
        {
            errors.Add($"permission '{key}' must be one of: none, scoped, full");
            return PermissionScope.None;
        }
        return scope;
    }

    private static bool Flag(JsonElement perms, string key, List<string> errors)
    {
        if (!perms.TryGetProperty(key, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default:
                errors.Add($"permission '{key}' must be a boolean");
                return false;
        }
    }

    private static string? ReqString(JsonElement root, string key, List<string> errors)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String)
        {
            errors.Add($"missing required field: {key}");
            return null;
        }
        var s = v.GetString();
        if (string.IsNullOrWhiteSpace(s))
        {
            errors.Add($"field '{key}' must not be empty");
            return null;
        }
        return s;
    }

    private static string? OptString(JsonElement root, string key)
        => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static bool LooksLikeId(string id)
        => id.Contains('.') && !id.StartsWith('.') && !id.EndsWith('.')
           && id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');
}
