"""Sable generative sidecar (PHASE8_AI_SIDECAR section 3.4).

A thin local HTTP server around (eventually) HuggingFace Diffusers. Slice S2 implements only the boundary:
health + vram. Model loading and generation land in S3/S4. Stdlib only here so `health` works even before
torch is importable; torch is imported lazily inside `vram`.

Run:  python main.py --port <p> --token <hex>
Bound to 127.0.0.1; every request must send `Authorization: Bearer <token>` (except the unauthenticated
liveness probe is still token-gated to keep it simple).
"""
import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

VERSION = "sable-sidecar/0.1"
TOKEN = ""
HOLDER = None  # lazily-created PipelineHolder (avoids importing torch until first load)


def _vram():
    """(total, free, device) in bytes; (0, 0, 'cpu') when no GPU torch is available."""
    try:
        import torch
        if torch.cuda.is_available():
            free, total = torch.cuda.mem_get_info()
            return int(total), int(free), torch.cuda.get_device_name(0)
        mps = getattr(torch.backends, "mps", None)
        if mps and mps.is_available():
            # unified memory: no discrete VRAM number from torch
            return 0, 0, "mps"
    except Exception:
        pass
    return 0, 0, "cpu"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # silence default stderr logging
        pass

    def _authed(self):
        auth = self.headers.get("Authorization", "")
        return auth == f"Bearer {TOKEN}"

    def _send(self, code, obj):
        body = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if not self._authed():
            self._send(401, {"error": "unauthorized"})
            return
        if self.path.rstrip("/") == "/health":
            _, _, device = _vram()
            self._send(200, {"ok": True, "version": VERSION, "device": device})
        elif self.path.rstrip("/") == "/vram":
            total, free, device = _vram()
            self._send(200, {"totalBytes": total, "freeBytes": free, "device": device})
        else:
            self._send(404, {"error": "not found"})

    def _read_json(self):
        n = int(self.headers.get("Content-Length", 0))
        raw = self.rfile.read(n) if n > 0 else b"{}"
        return json.loads(raw.decode("utf-8"))

    def do_POST(self):
        if not self._authed():
            self._send(401, {"error": "unauthorized"})
            return
        path = self.path.rstrip("/")
        if path == "/shutdown":
            self._send(200, {"ok": True})
            import threading
            threading.Thread(target=self.server.shutdown, daemon=True).start()
        elif path == "/load_model":
            global HOLDER
            try:
                req = self._read_json()
                if HOLDER is None:
                    from pipeline import PipelineHolder
                    HOLDER = PipelineHolder()
                self._send(200, HOLDER.load(req))
            except Exception as ex:
                self._send(200, {"ok": False, "error": str(ex)})
        elif path in ("/inpaint", "/outpaint", "/txt2img"):
            try:
                req = self._read_json()
                if HOLDER is None or HOLDER.pipe is None:
                    self._send(200, {"rgba": "", "width": 0, "height": 0, "error": "no model loaded"})
                    return
                self._send(200, HOLDER.generate(path.strip("/"), req))
            except Exception as ex:
                self._send(200, {"rgba": "", "width": 0, "height": 0, "error": str(ex)})
        else:
            self._send(404, {"error": "not found"})


def main():
    global TOKEN
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--token", type=str, required=True)
    args = ap.parse_args()
    TOKEN = args.token

    httpd = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
