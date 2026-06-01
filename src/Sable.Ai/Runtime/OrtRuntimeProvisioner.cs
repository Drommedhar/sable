using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Sable.Core.Ai;

namespace Sable.Ai.Runtime;

/// <summary>
/// Downloads + installs a Sable-published, arch-matched CUDA ONNX Runtime build (<see cref="GpuRuntimeArtifact"/>)
/// into <see cref="OrtCudaRuntime.RuntimeDir"/> so the CUDA EP works on GPUs prebuilt ORT can't run
/// (e.g. sm_120). Streams the archive with progress, extracts the <c>libonnxruntime*.so</c> set, and
/// hands them to <see cref="OrtCudaRuntime.InstallFromDirectory"/>. The libs link the system CUDA
/// toolkit/cuDNN (present on a typical CUDA-enabled box); a separate CUDA-runtime-redist provisioner
/// is a follow-up for users who have the driver but no toolkit.
/// </summary>
public sealed class OrtRuntimeProvisioner
{
    private readonly HttpClient _http;

    public OrtRuntimeProvisioner(HttpClient? http = null) =>
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Download + extract + install the artifact. Throws if it has no published URL yet.</summary>
    public async Task ProvisionAsync(GpuRuntimeArtifact art, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!art.HasUrl)
            throw new InvalidOperationException(
                "No published CUDA runtime URL for this GPU yet. Build it locally with tools/build-ort-cuda.sh " +
                "and install via 'Install from folder', or set the artifact URL in GpuRuntimeCatalog.");

        var tmp = Path.Combine(Path.GetTempPath(), "sable-ort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var archive = Path.Combine(tmp, "ort" + ArchiveExt(art.Url));
            await Download(art.Url, archive, progress, ct).ConfigureAwait(false);

            var extract = Path.Combine(tmp, "x");
            Directory.CreateDirectory(extract);
            Extract(archive, extract);

            // the libs may sit in a subfolder of the archive — find the dir containing libonnxruntime.so*
            var src = FindRuntimeDir(extract)
                ?? throw new InvalidOperationException("Archive contains no libonnxruntime.so.");
            OrtCudaRuntime.InstallFromDirectory(src);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* temp cleanup best-effort */ }
        }
    }

    /// <summary>Install from an already-built/extracted directory (maintainer/dev, or 'Install from folder').</summary>
    public static void InstallLocal(string dir) => OrtCudaRuntime.InstallFromDirectory(dir);

    private static string ArchiveExt(string url) =>
        url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";

    private async Task Download(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[1 << 16];
        long read = 0; int n;
        while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total > 0) progress?.Report((double)read / total);
        }
    }

    private static void Extract(string archive, string into)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, into, overwriteFiles: true);
        }
        else
        {
            using var fs = File.OpenRead(archive);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gz, into, overwriteFiles: true);
        }
    }

    private static string? FindRuntimeDir(string root)
    {
        if (File.Exists(Path.Combine(root, "libonnxruntime.so"))) return root;
        return Directory.EnumerateFiles(root, "libonnxruntime.so*", SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(d => d is not null);
    }
}
