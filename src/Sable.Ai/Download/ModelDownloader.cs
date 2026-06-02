using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sable.Ai.Models;
using Sable.Core.Ai;

namespace Sable.Ai.Download;

/// <summary>
/// Downloads model weights the user chooses — a curated <see cref="RecommendedModel"/> or an arbitrary
/// URL / HuggingFace <c>owner/repo/path</c> shorthand (PHASE8_AI §4). Streams to
/// <c>models/&lt;id&gt;/</c> with progress, then writes the drafted <c>model.json</c> and registers it.
/// We fetch from the source the user picked; we never bundle or redistribute weights.
/// </summary>
public sealed class ModelDownloader
{
    private readonly ModelRegistry _registry;
    private readonly HttpClient _http;

    public ModelDownloader(ModelRegistry registry, HttpClient? http = null)
    {
        _registry = registry;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Direct http(s) URL → unchanged. <c>owner/repo/path/file</c> → HF resolve URL (main).</summary>
    public static string ResolveUrl(string urlOrShorthand)
    {
        var s = urlOrShorthand.Trim();
        if (s.StartsWith("http://") || s.StartsWith("https://")) return s;
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            throw new ArgumentException($"Not a URL or HuggingFace 'owner/repo/file': '{urlOrShorthand}'");
        var repo = parts[0] + "/" + parts[1];
        var path = string.Join('/', parts.Skip(2));
        return $"https://huggingface.co/{repo}/resolve/main/{path}";
    }

    public static string FileNameFromUrl(string url)
    {
        var u = url.Split('?', '#')[0].TrimEnd('/');
        var name = u[(u.LastIndexOf('/') + 1)..];
        return string.IsNullOrEmpty(name) ? "model.bin" : name;
    }

    /// <summary>Download a curated recommended model (all its parts in order); returns the registered manifest.</summary>
    public async Task<ModelManifest> DownloadAsync(RecommendedModel rec, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = _registry.ModelDir(rec.Id);
        Directory.CreateDirectory(dir);
        var locals = new List<string>();
        int parts = rec.Downloads.Count;
        for (int i = 0; i < parts; i++)
        {
            var part = rec.Downloads[i];
            int idx = i;
            var partProgress = new Progress<double>(p => progress?.Report((idx + p) / parts));
            locals.Add(await DownloadFile(dir, ResolveUrl(part.Url), part.FileName, partProgress, ct).ConfigureAwait(false));
        }
        var manifest = rec.ToManifest(locals);
        _registry.Save(manifest);
        return manifest;
    }

    /// <summary>Download a set of recommended models in one go (skips already-installed); combined progress.</summary>
    public async Task<int> DownloadSetAsync(
        IReadOnlyList<RecommendedModel> models, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var todo = models.Where(m => !_registry.IsInstalled(m.Id)).ToList();
        int installed = 0;
        for (int i = 0; i < todo.Count; i++)
        {
            int idx = i;
            var sub = new Progress<double>(p => progress?.Report((idx + p) / todo.Count));
            await DownloadAsync(todo[i], sub, ct).ConfigureAwait(false);
            installed++;
        }
        return installed;
    }

    /// <summary>Download an arbitrary URL/HF shorthand; draft a manifest from the filename.</summary>
    public async Task<ModelManifest> DownloadAsync(string urlOrShorthand, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var url = ResolveUrl(urlOrShorthand);
        var file = FileNameFromUrl(url);
        var id = Path.GetFileNameWithoutExtension(file);
        var dir = _registry.ModelDir(id);
        Directory.CreateDirectory(dir);
        var local = await DownloadFile(dir, url, file, progress, ct).ConfigureAwait(false);
        var manifest = ModelRegistry.DraftFromFile(local);
        _registry.Save(manifest);
        return manifest;
    }

    /// <summary>Stream one file into <paramref name="dir"/>; if it's a .zip, extract + return the .onnx inside.</summary>
    private async Task<string> DownloadFile(string dir, string url, string fileName, IProgress<double>? progress, CancellationToken ct)
    {
        var dest = Path.Combine(dir, fileName);
        var tmp = dest + ".part";

        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                await using var srcStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                var buf = new byte[1 << 16];
                long read = 0; int n;
                while ((n = await srcStream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (total > 0) progress?.Report((double)read / total);
                }
            }
            File.Move(tmp, dest, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }   // don't orphan the .part on cancel/failure
            throw;
        }

        // many model releases ship the .onnx inside a .zip — extract + locate it
        if (dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var extractDir = Path.Combine(dir, "extracted");
            Directory.CreateDirectory(extractDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(dest, extractDir, overwriteFiles: true);
            var onnx = Directory.EnumerateFiles(extractDir, "*.onnx", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("The downloaded zip contains no .onnx file.");
            try { File.Delete(dest); } catch { /* keep the zip if locked */ }
            return onnx;
        }
        return dest;
    }
}
