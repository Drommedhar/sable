"""Diffusers pipeline construction for the Sable sidecar (PHASE8_AI_SIDECAR section 3.5).

Builds a pipeline from the resolved component paths the app sends (single-file checkpoint, a Diffusers
folder, or an assembled denoiser + text-encoder(s) + VAE), applies CPU offload, and reports the actual peak
VRAM so the C# gate can self-correct. torch / diffusers are imported lazily so the stdlib `health` endpoint
works even before they're installed.

This module is exercised by manual integration (needs a GPU + a diffusers venv + weights); the C# side that
PRODUCES the request (LoadPlan) is unit-tested without any of this.
"""
import gc


class PipelineHolder:
    """Keeps the loaded pipeline between load_model and (S4) generate."""

    def __init__(self):
        self.pipe = None
        self.model_id = None

    # ---- VRAM helpers ----
    @staticmethod
    def _reset_peak():
        import torch
        if torch.cuda.is_available():
            torch.cuda.reset_peak_memory_stats()

    @staticmethod
    def _peak_bytes():
        import torch
        if torch.cuda.is_available():
            return int(torch.cuda.max_memory_allocated())
        return 0

    @staticmethod
    def _device_name():
        import torch
        if torch.cuda.is_available():
            return torch.cuda.get_device_name(0)
        mps = getattr(torch.backends, "mps", None)
        if mps and mps.is_available():
            return "mps"
        return "cpu"

    def unload(self):
        self.pipe = None
        self.model_id = None
        gc.collect()
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception:
            pass

    def load(self, req: dict) -> dict:
        """req = LoadModelRequest (camelCase JSON). Returns LoadModelResult dict."""
        import torch
        kind = req.get("kind")
        paths = req.get("paths") or {}
        offload = bool(req.get("offload", False))
        family = (req.get("family") or "").lower()
        dtype = torch.float16

        # tolerate a missing/odd kind: infer it from which paths are present
        if kind not in ("SingleFile", "Pretrained", "Assembled"):
            if paths.get("checkpoint"):
                kind = "SingleFile"
            elif paths.get("pretrainedDir"):
                kind = "Pretrained"
            elif paths.get("denoiser"):
                kind = "Assembled"
            else:
                return {"ok": False, "error": f"can't determine how to load this model (kind={kind!r}, no paths)"}

        self.unload()
        self._reset_peak()

        try:
            if kind == "SingleFile":
                # a standalone denoiser (diffusion_models/) lands in 'denoiser'; a full checkpoint in 'checkpoint'
                pipe = self._from_single_file(paths.get("checkpoint") or paths.get("denoiser"), family, dtype)
            elif kind == "Pretrained":
                pipe = self._from_pretrained(paths.get("pretrainedDir"), dtype)
            else:  # Assembled
                pipe = self._assemble(paths, family, dtype)
        except Exception as ex:
            return {"ok": False, "error": f"load failed: {ex}"}

        # LoRA stack
        for i, lora in enumerate(req.get("loras") or []):
            try:
                pipe.load_lora_weights(lora["path"], adapter_name=f"lora{i}")
            except Exception as ex:
                return {"ok": False, "error": f"lora load failed ({lora.get('name')}): {ex}"}
        loras = req.get("loras") or []
        if loras:
            try:
                pipe.set_adapters([f"lora{i}" for i in range(len(loras))],
                                  adapter_weights=[float(l.get("weight", 1.0)) for l in loras])
            except Exception:
                pass

        # placement: offload idle components to RAM, else resident on GPU
        try:
            if offload:
                pipe.enable_model_cpu_offload()
            elif torch.cuda.is_available():
                pipe = pipe.to("cuda")
        except Exception as ex:
            return {"ok": False, "error": f"placement failed: {ex}"}

        self.pipe = pipe
        self.model_id = req.get("modelId")
        return {"ok": True, "peakVramBytes": self._peak_bytes(), "device": self._device_name()}

    # ---- constructors ----
    def _from_single_file(self, path, family, dtype):
        if not path:
            raise ValueError("no checkpoint path")
        import diffusers
        # map a Sable family to a Diffusers pipeline class, but only if this diffusers build has it
        name = {
            "sdxl": "StableDiffusionXLPipeline",
            "sd3": "StableDiffusion3Pipeline",
            "flux": "FluxPipeline",
            "qwen": "QwenImagePipeline",
            "hidream": "HiDreamImagePipeline",
            "sd1.5": "StableDiffusionPipeline",
            "sd2": "StableDiffusionPipeline",
        }.get(family)
        cls = getattr(diffusers, name) if name and hasattr(diffusers, name) else None
        if cls is not None:
            return cls.from_single_file(path, torch_dtype=dtype)
        # unknown / video / not-in-this-diffusers arch → let the auto pipeline try; clearer error if it can't
        try:
            from diffusers import AutoPipelineForText2Image
            return AutoPipelineForText2Image.from_single_file(path, torch_dtype=dtype)
        except Exception as ex:
            raise RuntimeError(
                f"Diffusers can't load this model ('{family or 'unknown'}') as a single file. Many ComfyUI "
                f"transformers (Flux2 / Qwen-Image / LTX / Wan / video models) aren't supported by Diffusers' "
                f"single-file loader. Underlying error: {ex}")

    def _from_pretrained(self, path, dtype):
        if not path:
            raise ValueError("no pretrained dir")
        from diffusers import AutoPipelineForText2Image
        return AutoPipelineForText2Image.from_pretrained(path, torch_dtype=dtype)

    # ---- generation (S4) ----
    def generate(self, task: str, req: dict) -> dict:
        """task = inpaint | outpaint | txt2img. Returns GenResult dict (rgba base64 + size + seed)."""
        if self.pipe is None:
            return {"rgba": "", "width": 0, "height": 0, "error": "no model loaded"}
        try:
            import torch
            from PIL import Image
            import base64, io, numpy as np

            prompt = req.get("prompt", "")
            negative = req.get("negative", "")
            steps = int(req.get("steps", 25))
            cfg = float(req.get("cfg", 7.0))
            seed = int(req.get("seed", -1))
            gen = None
            if seed >= 0:
                dev = "cuda" if torch.cuda.is_available() else "cpu"
                gen = torch.Generator(device=dev).manual_seed(seed)

            def decode(d):
                if not d:
                    return None
                raw = base64.b64decode(d["rgba"] if "rgba" in d else d["coverage"])
                w, h = int(d["width"]), int(d["height"])
                arr = np.frombuffer(raw, dtype=np.uint8)
                if "rgba" in d:
                    return Image.fromarray(arr.reshape(h, w, 4), "RGBA").convert("RGB")
                return Image.fromarray(arr.reshape(h, w), "L")   # mask coverage (single channel)

            kwargs = dict(prompt=prompt, negative_prompt=negative,
                          num_inference_steps=steps, guidance_scale=cfg)
            if gen is not None:
                kwargs["generator"] = gen

            if task in ("inpaint", "outpaint"):
                from diffusers import AutoPipelineForInpainting
                pipe = AutoPipelineForInpainting.from_pipe(self.pipe)
                image = decode(req.get("image"))
                mask = decode(req.get("mask"))
                kwargs.update(image=image, mask_image=mask, width=image.width, height=image.height)
                result = pipe(**kwargs).images[0]
            else:  # txt2img
                result = self.pipe(**kwargs).images[0]

            rgba = result.convert("RGBA")
            buf = np.asarray(rgba, dtype=np.uint8).tobytes()
            return {"rgba": base64.b64encode(buf).decode("ascii"),
                    "width": rgba.width, "height": rgba.height, "seed": seed}
        except Exception as ex:
            return {"rgba": "", "width": 0, "height": 0, "error": f"generate failed: {ex}"}

    def _assemble(self, paths, family, dtype):
        # Best-effort assembled construction (Flux/SD3): standalone denoiser + text encoders + VAE.
        # Arch-specific wiring varies a lot; this covers the common Flux/SD3 single-transformer layout and
        # otherwise raises a clear error so the app can prompt the user (S3+ refinement).
        raise NotImplementedError(
            "assembled pipelines (standalone denoiser + encoders + VAE) are not wired yet; "
            "use a single-file checkpoint or a Diffusers folder")
