# Third-Party Notices

Sable is licensed under the MIT License. It bundles / depends on the third-party
components below. This file is a starting point — run a licence-audit step in CI
before the first release and keep it current (PLAN §12).

| Component | Licence | Use | Notes |
| --- | --- | --- | --- |
| AvaloniaUI | MIT | UI framework | |
| Avalonia.Themes.Fluent / Fonts.Inter | MIT | UI theme/font | |
| SkiaSharp | MIT / BSD | codec decode/encode, text rasterisation | |
| wgpu-native (via Silk.NET.WebGPU) | MIT / Apache-2.0 | GPU backend | DX12 / Vulkan / Metal |
| Silk.NET.WebGPU (+ Native.WGPU, Extensions.WGPU) | MIT | wgpu binding | |
| CommunityToolkit.Mvvm | MIT | MVVM | |
| .NET runtime / BCL | MIT | runtime | |

Watch / TODO:

- **SixLabors.ImageSharp** (if adopted): Six Labors Split License — free for OSS but a
  commercial licence is required above a revenue threshold. Sable currently uses
  SkiaSharp for codecs and does **not** depend on ImageSharp; revisit if added.
- **libraw** (RAW decode, if added): LGPL-2.1 / CDDL — dynamic-link only, or ship as an
  optional plugin, to stay MIT-friendly.
- **lcms2** (ICC, when the colour pipeline lands): MIT.
- **ONNX Runtime** (when AI light tier lands): MIT.
- **AI model weights**: user-provided; licence is the user's responsibility (not bundled).
- **Diffusers / torch** (generative sidecar, when added): Apache-2.0 / BSD — installed at
  runtime on opt-in, not bundled in the distribution.

Full licence texts for bundled components are reproduced at release time alongside the binaries.
