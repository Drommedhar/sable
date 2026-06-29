# PSD fixture corpus

These fixtures exercise the PSD importer (`Sable.Format.PsdReader`) against the
compatibility matrix in [`docs/compat/psd_matrix.md`](../../docs/compat/psd_matrix.md).

## Approach

Sable has no Photoshop license and no bundled .psd generator. The fixtures are therefore
**synthetic valid PSD byte streams** built by `PsdFixtures` (in
`tests/Sable.Tests/PsdFixtures.cs`), reusing the same PSD-section builder helpers that
`PsdReaderTests` already proved against the importer. Each fixture is a named factory method
returning a `byte[]` that `PsdReader.Load` consumes exactly as a real file would.

This keeps the corpus:

* **deterministic** — no binary blobs in git, no generation drift
* **self-documenting** — the builder code shows exactly which PSD records/channels/tags are present
* **zero-dependency** — no fixture files to ship or keep in sync with the importer

When a real-world PSD is donated for regression testing, drop the .psd file into this
directory and add a test that loads it via `File.ReadAllBytes`; the synthetic fixtures below
remain the structural-canonical set.

## Fixture list (mapped to `psd_matrix.md` sections)

| Fixture | Covers | Matrix section |
|---|---|---|
| `BasicRasterStack` | 2 raster layers, opacity, blend, offset | §4, §5 |
| `NestedGroupPassThrough` | open folder + pass-through + nested | §6 |
| `ClippingChain` | 3 clip-to-below layers | §6 (clipping) |
| `LayerMask` | raster mask with default colour | §7 |
| `VectorMaskRasterised` | vmsk bezier → mask coverage + warning | §7 |
| `SolidFillShape` | SoCo + single closed vmsk → editable PathLayer | §10 |
| `SolidFillMultiContour` | SoCo + multi-contour vmsk → rasterised fallback | §10 |
| `TextPoint` | TySh point text → editable TextLayer | §9 |
| `DropShadowAndOverlay` | lfx2 drop shadow + colour overlay | §12 |
| `SixteenBitFlattened` | 16-bit composite → 8-bit + warning | §2 |
| `UnsupportedModeCmyk` | CMYK → rejected with clear error | §2 |
| `SmartObjectRasterised` | SoLd tagged block → rasterised warning | §13 |
| `AdjustmentSkipped` | phfl photo filter → skipped warning | §11 |
| `AdjustmentBrightnessContrast` | brit → editable AdjustmentLayer | §11 |
| `AdjustmentCurves` | curv → editable Curves layer | §11 |
| `AdjustmentInvert` | nvrt → editable Invert layer | §11 |
