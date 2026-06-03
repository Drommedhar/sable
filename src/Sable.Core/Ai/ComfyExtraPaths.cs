using System.IO;

namespace Sable.Core.Ai;

/// <summary>
/// Minimal parser for ComfyUI's <c>extra_model_paths.yaml</c> (PHASE8_AI_SIDECAR §2.3). The file is a flat
/// 2-level YAML: top-level config names, each with a <c>base_path</c> and per-role relative path(s) (a role
/// value may be a single line or a <c>|</c> block of several lines). We only need that flat shape, so this is
/// a tolerant hand-rolled line parser — no YAML dependency. Pure: <see cref="Parse"/> takes the text, callers
/// resolve roots against the filesystem. Roles map straight to <see cref="ComfyLayout.RoleFor"/> keys.
/// </summary>
public static class ComfyExtraPaths
{
    /// <summary>One named config block (e.g. <c>comfyui:</c>, <c>a1111:</c>).</summary>
    public sealed record Config(string Name, string? BasePath, IReadOnlyDictionary<string, IReadOnlyList<string>> Roles);

    /// <summary>A resolved scan location: the role key + an absolute directory to walk for that role.</summary>
    public sealed record ExtraRoot(string Role, string AbsDir);

    private static readonly System.Collections.Generic.HashSet<string> NonRoleKeys =
        new(System.StringComparer.OrdinalIgnoreCase) { "base_path", "is_default", "download_model_base" };

    /// <summary>Parse the yaml text into its config blocks. Comments (<c>#</c>) and blank lines are ignored.</summary>
    public static IReadOnlyList<Config> Parse(string yaml)
    {
        var configs = new List<Config>();
        if (string.IsNullOrWhiteSpace(yaml)) return configs;

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        string? curName = null;
        string? basePath = null;
        var roles = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);

        string? blockKey = null;            // active "key: |" multi-line scalar
        List<string>? blockVals = null;
        int blockIndent = -1;

        void Flush()
        {
            if (curName is not null)
                configs.Add(new Config(curName, basePath, new Dictionary<string, IReadOnlyList<string>>(roles, System.StringComparer.OrdinalIgnoreCase)));
            basePath = null;
            roles = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.OrdinalIgnoreCase);
            blockKey = null; blockVals = null; blockIndent = -1;
        }

        void CloseBlock()
        {
            if (blockKey is not null && blockVals is not null && blockVals.Count > 0)
                roles[blockKey] = blockVals;
            blockKey = null; blockVals = null; blockIndent = -1;
        }

        foreach (var raw in lines)
        {
            var noComment = StripComment(raw);
            if (noComment.Trim().Length == 0) continue;
            int indent = IndentOf(noComment);
            var trimmed = noComment.Trim();

            // collecting a "|" block: deeper-indented lines are values
            if (blockKey is not null)
            {
                if (indent > blockIndent) { blockVals!.Add(trimmed); continue; }
                CloseBlock();
            }

            if (indent == 0)
            {
                // top-level config name: "comfyui:"
                Flush();
                curName = trimmed.TrimEnd(':').Trim();
                continue;
            }

            if (curName is null) continue;   // stray indented line before any config

            // "key: value" or "key: |"
            int colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var key = trimmed.Substring(0, colon).Trim();
            var val = trimmed.Substring(colon + 1).Trim();

            if (val == "|" || val == "|-" || val == ">")
            {
                blockKey = key; blockVals = new List<string>(); blockIndent = indent;
                continue;
            }

            if (NonRoleKeys.Contains(key))
            {
                if (string.Equals(key, "base_path", System.StringComparison.OrdinalIgnoreCase))
                    basePath = Unquote(val);
                continue;
            }

            if (val.Length > 0) roles[key] = new[] { Unquote(val) };
        }

        CloseBlock();
        Flush();
        return configs;
    }

    /// <summary>
    /// Resolve a config's role paths to absolute scan directories: each role path is joined onto
    /// <see cref="Config.BasePath"/> when relative (already-absolute role paths are used as-is).
    /// </summary>
    public static IEnumerable<ExtraRoot> ResolveRoots(Config cfg)
    {
        foreach (var (role, paths) in cfg.Roles)
            foreach (var p in paths)
            {
                var abs = Path.IsPathRooted(p) || cfg.BasePath is null ? p : Path.Combine(cfg.BasePath, p);
                yield return new ExtraRoot(role, abs);
            }
    }

    private static string StripComment(string line)
    {
        int h = line.IndexOf('#');
        return h < 0 ? line : line.Substring(0, h);
    }

    private static int IndentOf(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ') i++;
        return i;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s.Substring(1, s.Length - 2);
        return s;
    }
}
