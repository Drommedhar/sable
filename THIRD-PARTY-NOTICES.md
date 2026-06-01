# Third-Party Notices

Sable itself is licensed under the MIT License — see [LICENSE](LICENSE).

This product bundles or depends on the third-party components listed below.
Each is distributed under its own license, reproduced or linked here per its
redistribution terms. Versions reflect the current build; re-check on upgrade.

---

## Runtime dependencies (shipped in releases)

### Avalonia — MIT
UI framework (chrome, controls, theming). Includes `Avalonia`,
`Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`,
`Avalonia.Controls.ColorPicker`. Version 12.0.4.
<https://github.com/AvaloniaUI/Avalonia> · MIT License.

### Inter typeface — SIL Open Font License 1.1
Bundled by `Avalonia.Fonts.Inter` and used as the UI font.
Copyright The Inter Project Authors. <https://github.com/rsms/inter>
<https://openfontlicense.org>

### CommunityToolkit.Mvvm — MIT
MVVM source generators / helpers (.NET Community Toolkit). Version 8.4.2.
Copyright (c) .NET Foundation and Contributors.
<https://github.com/CommunityToolkit/dotnet> · MIT License.

### Silk.NET (WebGPU bindings) — MIT
`Silk.NET.WebGPU`, `Silk.NET.WebGPU.Extensions.WGPU`,
`Silk.NET.WebGPU.Native.WGPU`. Version 2.23.0.
Copyright (c) .NET Foundation and Contributors.
<https://github.com/dotnet/Silk.NET> · MIT License.

### wgpu-native — MIT OR Apache-2.0
The native WebGPU implementation redistributed by
`Silk.NET.WebGPU.Native.WGPU` (gfx-rs / wgpu). Dual-licensed MIT or
Apache-2.0; used here under the MIT option.
<https://github.com/gfx-rs/wgpu-native>

### SkiaSharp — MIT
2D graphics for image codecs / rasterisation. `SkiaSharp`,
`SkiaSharp.NativeAssets.Linux`. Version 3.119.x. Copyright (c) Microsoft
Corporation and contributors. <https://github.com/mono/SkiaSharp> · MIT License.

### Skia — BSD-3-Clause
The native graphics library underlying SkiaSharp.
Copyright (c) Google LLC. <https://skia.org> · BSD-3-Clause.

### Microsoft.ML.OnnxRuntime.DirectML — MIT
ONNX Runtime with the DirectML execution provider, for the local AI light tier.
Version 1.24.4. Copyright (c) Microsoft Corporation.
<https://github.com/microsoft/onnxruntime> · MIT License.

### DirectML — Microsoft redistributable
`DirectML.dll`, redistributed via the ONNX Runtime DirectML package and used as
the GPU execution provider on Windows. Provided under the Microsoft DirectML
redistribution terms.
<https://learn.microsoft.com/windows/ai/directml/dml>

---

## Packaging tools (build-time only, not shipped in the app)

- **Inno Setup** (Windows installer compiler) — used in CI to build the Windows
  installer. Inno Setup license (free for commercial and non-commercial use).
  <https://jrsoftware.org/isinfo.php>
- **appimagetool** (Linux AppImage) — MIT. <https://github.com/AppImage/appimagetool>
- **xUnit.net** — Apache-2.0. Test framework; not distributed with the app.
  <https://github.com/xunit/xunit>

---

## AI model weights — user-provided, not bundled

Sable does **not** ship any AI model weights. The model manager lists curated
download *pointers* and shows each model's licence before download; the user
chooses whether to download. Downloaded weights remain under their own licenses,
for example:

| Model | Task | License (verify at download) |
| --- | --- | --- |
| RMBG-1.4 / BiRefNet | Background removal | BRIA RMBG-1.4 — non-commercial; commercial requires a BRIA license |
| Real-ESRGAN x4plus | Upscale | BSD-3-Clause (export terms may apply) |
| SAM 2 (Hiera) | Smart selection | Apache-2.0 (Meta) |
| LaMa | Object removal / inpaint | Apache-2.0 |

You are responsible for complying with the license of any model you download
and use.

---

If you believe a component is missing or mis-attributed here, please open an
issue.
