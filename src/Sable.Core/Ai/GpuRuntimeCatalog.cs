namespace Sable.Core.Ai;

/// <summary>
/// A prebuilt, arch-targeted ONNX Runtime CUDA build that Sable publishes and the app downloads on
/// Linux (PHASE8_AI Linux / Phase 9). Prebuilt upstream ORT has no kernels for newer NVIDIA archs
/// (e.g. sm_120 / RTX 5090), so Sable compiles ORT from source with the needed
/// <c>CMAKE_CUDA_ARCHITECTURES</c> (see <c>tools/build-ort-cuda.sh</c>) and hosts the resulting
/// <c>libonnxruntime*.so</c> set. The archive (.tar.gz or .zip) contains those three libs.
///
/// Pure metadata — like <see cref="RecommendedModel"/>, the app ships only the pointer + licence,
/// never redistributes binaries inside the app package.
/// </summary>
public sealed record GpuRuntimeArtifact(
    string OrtVersion,                 // ORT version of the build, must match the managed package (e.g. "1.24.4")
    IReadOnlyList<string> Archs,       // CUDA compute caps this build covers, no dot (e.g. ["89","90","120"])
    string CudaMajor,                  // CUDA toolkit major it links (e.g. "13"); informs the runtime-redist need
    string Url,                        // archive of libonnxruntime*.so; empty until the maintainer publishes
    long SizeBytes,
    string License = "MIT (ONNX Runtime)",
    string LicenseUrl = "https://raw.githubusercontent.com/microsoft/onnxruntime/main/LICENSE",
    string Sha256 = "")
{
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool Covers(string computeArch) => Archs.Contains(computeArch);
}

/// <summary>
/// Curated list of Sable-published CUDA ORT builds, resolved by the GPU's compute capability.
///
/// MAINTAINER WORKFLOW: build with <c>tools/build-ort-cuda.sh &lt;archs&gt;</c>, upload the archive
/// (e.g. to the Sable GitHub release), then set <see cref="GpuRuntimeArtifact.Url"/> (+ Sha256/Size)
/// for the matching entry here. Until a URL is set, the app falls back to offering a local build /
/// manual install. Keep <c>OrtVersion</c> in lockstep with the managed <c>Microsoft.ML.OnnxRuntime</c>
/// package version.
/// </summary>
public static class GpuRuntimeCatalog
{
    /// <summary>ORT version Sable's managed package targets — published builds must match.</summary>
    public const string OrtVersion = "1.24.4";

    public static readonly IReadOnlyList<GpuRuntimeArtifact> All = new[]
    {
        // One build covering Ada (sm_89), Hopper (sm_90) and Blackwell (sm_120), linked against CUDA 13.
        // URL left blank: set it once the artifact is published (see MAINTAINER WORKFLOW above).
        new GpuRuntimeArtifact(
            OrtVersion: OrtVersion,
            Archs: new[] { "89", "90", "120" },
            CudaMajor: "13",
            Url: "",
            SizeBytes: 130_000_000),
    };

    /// <summary>The published artifact covering <paramref name="computeArch"/> (e.g. "120"), or null.</summary>
    public static GpuRuntimeArtifact? ResolveFor(string? computeArch) =>
        computeArch is null ? null : All.FirstOrDefault(a => a.OrtVersion == OrtVersion && a.Covers(computeArch));
}
