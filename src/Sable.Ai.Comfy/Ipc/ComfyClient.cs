using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sable.Ai.Comfy.Ipc;

/// <summary>A ComfyUI output image reference (from a WS <c>executed</c> frame or <c>/history</c>).</summary>
public sealed record ComfyImageRef(string Filename, string Subfolder, string Type);

public enum ComfyEventKind { Status, Progress, Executing, Executed, Error, Other }

/// <summary>A decoded ComfyUI WebSocket event (PHASE8_AI_COMFY §2.2).</summary>
public sealed record ComfyWsEvent(
    ComfyEventKind Kind,
    double Value = 0, double Max = 0,
    string? Node = null,
    IReadOnlyList<ComfyImageRef>? Images = null,
    string? Message = null);

/// <summary>
/// Drives a headless ComfyUI over its automation API (PHASE8_AI_COMFY §2.2): queue a graph at <c>/prompt</c>,
/// follow progress on the <c>/ws</c> WebSocket, pull the result from <c>/view</c>. The WS-frame parser
/// (<see cref="ParseEvent"/>) is pure + unit-tested; the live flow is manual-integration. Returns the result
/// as PNG bytes (the App decodes to RGBA — keeps this project free of an image codec dep).
/// </summary>
public sealed class ComfyClient : IDisposable
{
    private readonly HttpClient _http;
    public Uri BaseUri { get; }
    public string ClientId { get; } = Guid.NewGuid().ToString("N");

    public ComfyClient(Uri baseUri, HttpMessageHandler? handler = null)
    {
        BaseUri = baseUri;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = baseUri;
        _http.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>GET /system_stats → true if ComfyUI answered (health), plus free VRAM bytes (0 if unknown).</summary>
    public async Task<(bool Ok, long FreeVram)> SystemStatsAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await _http.GetFromJsonAsync<JsonDocument>("system_stats", ct).ConfigureAwait(false);
            long free = 0;
            if (doc!.RootElement.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array && devs.GetArrayLength() > 0)
                if (devs[0].TryGetProperty("vram_free", out var vf)) free = vf.GetInt64();
            return (true, free);
        }
        catch { return (false, 0); }
    }

    /// <summary>POST /prompt → prompt_id. Throws with detail if ComfyUI REJECTS the graph (missing node /
    /// bad inputs) — otherwise a rejected prompt has no prompt_id and the WS would hang forever.</summary>
    public async Task<string> QueuePromptAsync(IReadOnlyDictionary<string, object> graph, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["prompt"] = graph, ["client_id"] = ClientId };
        var resp = await _http.PostAsJsonAsync("prompt", body, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("prompt_id", out var id) && id.ValueKind == JsonValueKind.String)
            return id.GetString()!;

        // error shape: { "error":{"message":…}, "node_errors": { "<id>": { "class_type":…, "errors":[{message,details}] } } }
        var msg = "ComfyUI rejected the workflow";
        if (root.TryGetProperty("error", out var err))
            msg += ": " + (err.TryGetProperty("message", out var em) ? em.GetString() : err.ToString());
        if (root.TryGetProperty("node_errors", out var ne) && ne.ValueKind == JsonValueKind.Object)
        {
            foreach (var node in ne.EnumerateObject())
            {
                var cls = node.Value.TryGetProperty("class_type", out var ct2) ? ct2.GetString() : node.Name;
                if (node.Value.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                    foreach (var e in errs.EnumerateArray())
                    {
                        var detail = e.TryGetProperty("message", out var m2) ? m2.GetString() : "";
                        if (e.TryGetProperty("details", out var d2) && !string.IsNullOrEmpty(d2.GetString())) detail += " — " + d2.GetString();
                        msg += $"\n• {cls}: {detail}";
                    }
            }
        }
        throw new InvalidOperationException(msg);
    }

    /// <summary>GET /view?filename=…&amp;subfolder=…&amp;type=… → the image bytes (PNG).</summary>
    public async Task<byte[]> ViewImageAsync(ComfyImageRef img, CancellationToken ct = default)
        => await _http.GetByteArrayAsync(
            $"view?filename={Uri.EscapeDataString(img.Filename)}&subfolder={Uri.EscapeDataString(img.Subfolder)}&type={Uri.EscapeDataString(img.Type)}",
            ct).ConfigureAwait(false);

    /// <summary>POST /upload/image (multipart) → the stored filename to reference in a graph's LoadImage.</summary>
    public async Task<string> UploadImageAsync(byte[] png, string name, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(png);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(part, "image", name);
        form.Add(new StringContent("true"), "overwrite");
        var resp = await _http.PostAsync("upload/image", form, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
    }

    /// <summary>POST /interrupt — cancel the running prompt.</summary>
    public async Task InterruptAsync(CancellationToken ct = default)
    {
        try { await _http.PostAsync("interrupt", new StringContent(""), ct).ConfigureAwait(false); } catch { }
    }

    /// <summary>
    /// Queue a graph and follow it to completion over the WS, reporting 0..1 progress; returns the first
    /// output image's PNG bytes. Manual-integration (needs a live ComfyUI).
    /// </summary>
    public async Task<byte[]> RunPromptAsync(IReadOnlyDictionary<string, object> graph, IProgress<double>? progress, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        var wsUri = new Uri((BaseUri.Scheme == "https" ? "wss://" : "ws://") + BaseUri.Authority + "/ws?clientId=" + ClientId);
        await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false);

        var promptId = await QueuePromptAsync(graph, ct).ConfigureAwait(false);

        ComfyImageRef? result = null;
        var buf = new byte[1 << 16];
        while (ws.State == WebSocketState.Open)
        {
            var seg = new ArraySegment<byte>(buf);
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult r;
            do { r = await ws.ReceiveAsync(seg, ct).ConfigureAwait(false); ms.Write(buf, 0, r.Count); } while (!r.EndOfMessage);
            if (r.MessageType != WebSocketMessageType.Text) continue;

            var ev = ParseEvent(Encoding.UTF8.GetString(ms.ToArray()));
            if (ev.Kind == ComfyEventKind.Progress && ev.Max > 0) progress?.Report(ev.Value / ev.Max);
            else if (ev.Kind == ComfyEventKind.Error) throw new InvalidOperationException("ComfyUI error: " + (ev.Message ?? "execution failed"));
            else if (ev.Kind == ComfyEventKind.Executed && ev.Images is { Count: > 0 }) { result = ev.Images[0]; break; }
            else if (ev.Kind == ComfyEventKind.Executing && ev.Node is null) break;   // null node = run finished
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false); } catch { }

        if (result is null) throw new InvalidOperationException("ComfyUI produced no image (check the workflow / model).");
        return await ViewImageAsync(result, ct).ConfigureAwait(false);
    }

    /// <summary>Parse one ComfyUI WS frame into a typed event. Pure → unit-tested with canned frames.</summary>
    public static ComfyWsEvent ParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var data = root.TryGetProperty("data", out var d) ? d : default;
            switch (type)
            {
                case "progress":
                    return new ComfyWsEvent(ComfyEventKind.Progress,
                        data.TryGetProperty("value", out var v) ? v.GetDouble() : 0,
                        data.TryGetProperty("max", out var m) ? m.GetDouble() : 0);
                case "executing":
                    string? node = data.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    return new ComfyWsEvent(ComfyEventKind.Executing, Node: node);
                case "executed":
                    var imgs = new List<ComfyImageRef>();
                    if (data.TryGetProperty("output", out var outp) && outp.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var im in arr.EnumerateArray())
                            imgs.Add(new ComfyImageRef(
                                im.TryGetProperty("filename", out var f) ? f.GetString() ?? "" : "",
                                im.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "",
                                im.TryGetProperty("type", out var ty) ? ty.GetString() ?? "output" : "output"));
                    return new ComfyWsEvent(ComfyEventKind.Executed,
                        Node: data.TryGetProperty("node", out var en) ? en.GetString() : null, Images: imgs);
                case "execution_error":
                    string em = data.TryGetProperty("exception_message", out var x) ? x.GetString() ?? "" : "";
                    string en2 = data.TryGetProperty("node_type", out var nt) ? nt.GetString() ?? "" : "";
                    return new ComfyWsEvent(ComfyEventKind.Error, Message: string.IsNullOrEmpty(en2) ? em : $"{en2}: {em}");
                case "status":
                    return new ComfyWsEvent(ComfyEventKind.Status);
                default:
                    return new ComfyWsEvent(ComfyEventKind.Other);
            }
        }
        catch { return new ComfyWsEvent(ComfyEventKind.Other); }
    }

    public void Dispose() => _http.Dispose();
}
