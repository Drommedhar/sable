"""Env capability probe (PHASE8_AI_SIDECAR section 3.2).

Prints one JSON line describing this interpreter: torch / diffusers versions + which GPU accelerators are
available. The C# EnvProbe runs the SAME logic inline via `python -c`; this file is the readable reference
and can be invoked directly for debugging:  python probe.py
"""
import json
import platform

d = {
    "os": platform.system(),
    "python": platform.python_version(),
    "torch": "",
    "diffusers": "",
    "cuda": "",
    "cuda_avail": False,
    "mps": False,
    "rocm": False,
    "directml": False,
}

try:
    import torch
    d["torch"] = torch.__version__
    d["cuda"] = getattr(torch.version, "cuda", None) or ""
    d["cuda_avail"] = bool(torch.cuda.is_available())
    d["rocm"] = bool(getattr(torch.version, "hip", None))
    mps = getattr(torch.backends, "mps", None)
    d["mps"] = bool(mps and mps.is_available())
except Exception:
    pass

try:
    import torch_directml  # noqa: F401
    d["directml"] = True
except Exception:
    pass

try:
    import diffusers
    d["diffusers"] = diffusers.__version__
except Exception:
    pass

print(json.dumps(d))
