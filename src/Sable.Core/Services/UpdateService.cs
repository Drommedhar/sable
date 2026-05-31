using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Sable.Core.Services;

/// <summary>Details of an available newer release (resolved per-platform asset).</summary>
public sealed class UpdateInfo
{
    public string Version { get; init; } = string.Empty;
    public string TagName { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string AssetName { get; init; } = string.Empty;
    public long AssetSize { get; init; }
}

public interface IUpdateService
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);
    Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default);
    void LaunchInstaller(string installerPath);
}

/// <summary>
/// Checks GitHub Releases for a newer build, downloads the per-OS asset, and launches the
/// installer (PLAN §2.4, Novalist-style). Network/IO failures are swallowed by callers — the
/// check is best-effort and never blocks the app.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Drommedhar/sable/releases/latest";

    private static readonly HttpClient SharedHttp = CreateHttpClient();

    private readonly HttpClient _http;
    private readonly string _downloadDir;

    // OS-detection seams so per-platform asset selection is unit-testable from any host OS.
    internal static Func<OSPlatform, bool> IsOsPlatform { get; set; } = RuntimeInformation.IsOSPlatform;
    internal static Func<Architecture> OsArchitecture { get; set; } = () => RuntimeInformation.OSArchitecture;

    public UpdateService(HttpClient? http = null, string? downloadDir = null)
    {
        _http = http ?? SharedHttp;
        _downloadDir = downloadDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sable", "Updates");
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sable-UpdateCheck");
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var release = await _http.GetFromJsonAsync<GitHubRelease>(ReleasesApiUrl, ct);
        if (release is null || string.IsNullOrEmpty(release.TagName))
            return null;

        var remoteVersion = release.TagName.TrimStart('v', 'V');
        var currentVersion = StripPreRelease(VersionInfo.Version);
        if (!IsNewer(remoteVersion, currentVersion))
            return null;

        var asset = FindPlatformAsset(release);
        if (asset is null)
            return null;

        return new UpdateInfo
        {
            Version = remoteVersion,
            TagName = release.TagName,
            HtmlUrl = release.HtmlUrl ?? string.Empty,
            Body = release.Body ?? string.Empty,
            DownloadUrl = asset.BrowserDownloadUrl ?? string.Empty,
            AssetName = asset.Name ?? string.Empty,
            AssetSize = asset.Size,
        };
    }

    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_downloadDir);
        var filePath = Path.Combine(_downloadDir, update.AssetName);

        // reuse an already-downloaded asset of the expected size
        if (File.Exists(filePath))
        {
            if (new FileInfo(filePath).Length == update.AssetSize) { progress?.Report(1.0); return filePath; }
            File.Delete(filePath);
        }

        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.AssetSize;
        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long read = 0; int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
        progress?.Report(1.0);
        return filePath;
    }

    [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
    public void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath)) return;

        var psi = new System.Diagnostics.ProcessStartInfo { FileName = installerPath, UseShellExecute = true };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))   // open the DMG via the system handler
        {
            psi.FileName = "open";
            psi.Arguments = $"\"{installerPath}\"";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            psi.FileName = "xdg-open";   // AppImage: hand off to the file manager (in-place swap is a later refinement)
            psi.Arguments = $"\"{installerPath}\"";
            psi.UseShellExecute = false;
        }
        System.Diagnostics.Process.Start(psi);
    }

    private static GitHubReleaseAsset? FindPlatformAsset(GitHubRelease release)
    {
        if (release.Assets is null || release.Assets.Length == 0) return null;

        if (IsOsPlatform(OSPlatform.Windows))
            return release.Assets.FirstOrDefault(a =>
                a.Name != null && a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (IsOsPlatform(OSPlatform.OSX))
        {
            var arch = OsArchitecture() == Architecture.Arm64 ? "arm64" : "x64";
            var mac = release.Assets.Where(a =>
                a.Name != null && a.Name.Contains("macos", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)).ToArray();
            return mac.FirstOrDefault(a => a.Name!.Contains(arch, StringComparison.OrdinalIgnoreCase)) ?? mac.FirstOrDefault();
        }

        if (IsOsPlatform(OSPlatform.Linux))
            return release.Assets.FirstOrDefault(a =>
                a.Name != null && a.Name.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase));

        return null;
    }

    private static string StripPreRelease(string version)
    {
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }

    /// <summary>True if <paramref name="remote"/> is a higher semver than <paramref name="current"/>.</summary>
    public static bool IsNewer(string remote, string current)
    {
        var r = ParseParts(remote);
        var c = ParseParts(current);
        for (var i = 0; i < 3; i++)
        {
            var rp = i < r.Length ? r[i] : 0;
            var cp = i < c.Length ? c[i] : 0;
            if (rp > cp) return true;
            if (rp < cp) return false;
        }
        return false;
    }

    private static int[] ParseParts(string version)
    {
        var parts = version.Split('.');
        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++) int.TryParse(parts[i], out result[i]);
        return result;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public GitHubReleaseAsset[]? Assets { get; set; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
