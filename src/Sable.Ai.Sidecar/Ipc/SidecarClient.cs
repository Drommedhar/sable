using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Sable.Core.Ai;

namespace Sable.Ai.Sidecar.Ipc;

/// <summary>
/// Localhost HTTP client for the generative sidecar (PHASE8_AI_SIDECAR §3.4). JSON control over
/// <c>127.0.0.1</c> with a per-process bearer token. S2 covers <c>health</c> + <c>vram</c>; load/generate
/// endpoints arrive with S3/S4. Unit-tested against an in-process mock <c>HttpListener</c> (no Python).
/// </summary>
public sealed class SidecarClient : IDisposable
{
    // web defaults (camelCase + case-insensitive) PLUS string enums, so PipelineKind serializes as
    // "SingleFile"/"Pretrained"/"Assembled" — the strings the Python server compares against.
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    public Uri BaseUri { get; }

    public SidecarClient(Uri baseUri, string token, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>GET /health → ok? Never throws (returns a not-ok health on any failure).</summary>
    public async Task<SidecarHealth> HealthAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<SidecarHealth>("health", ct).ConfigureAwait(false);
            return r ?? new SidecarHealth(false);
        }
        catch { return new SidecarHealth(false); }
    }

    /// <summary>GET /vram → memory report; throws on transport/parse failure (caller decides).</summary>
    public async Task<VramReport> VramAsync(CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync<VramReport>("vram", ct).ConfigureAwait(false);
        return r ?? new VramReport(0, 0);
    }

    /// <summary>POST /load_model → construct the pipeline from resolved component paths (§3.5); returns the
    /// actual peak VRAM or a structured error. Never throws — transport failure becomes a not-ok result.</summary>
    public async Task<LoadModelResult> LoadModelAsync(LoadModelRequest req, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("load_model", req, J, ct).ConfigureAwait(false);
            var r = await resp.Content.ReadFromJsonAsync<LoadModelResult>(J, ct).ConfigureAwait(false);
            return r ?? new LoadModelResult(false, Error: "empty response");
        }
        catch (Exception ex) { return new LoadModelResult(false, Error: ex.Message); }
    }

    /// <summary>POST /{endpoint} (inpaint/outpaint/txt2img) with a <see cref="GenRequest"/> → result image (§4).</summary>
    public async Task<GenResult> GenerateAsync(string endpoint, GenRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(endpoint, req, J, ct).ConfigureAwait(false);
        var r = await resp.Content.ReadFromJsonAsync<GenResult>(J, ct).ConfigureAwait(false);
        return r ?? new GenResult(System.Array.Empty<byte>(), 0, 0, Error: "empty response");
    }

    /// <summary>Poll /health until ok or the deadline; true if the server came up.</summary>
    public async Task<bool> WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if ((await HealthAsync(ct).ConfigureAwait(false)).Ok) return true;
            try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { return false; }
        }
        return false;
    }

    public void Dispose() => _http.Dispose();
}
