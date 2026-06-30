# Sable Sample Plugin

A complete, minimal reference plugin for Sable. Use it as a template.

It declares `document.read`, `layer.read`, `layer.write.basic`, `selection.read`, `pixel.read`,
`undo.transaction`, `command.register`, `ui.menu_command`, `export.provider`, `import.provider`
and contributes:

- **Report Active Document** (Ctrl+K palette + **Plugins ▸ Sample**) — reports document size,
  selection, and composite size via the read APIs,
- **Halve All Layer Opacities** — a multi-layer edit grouped into ONE undo step via
  `host.Transactions.Run` (demonstrates `undo.transaction` + `layer.write.basic`),
- a **PPM (P6) export format** (Export / Export Assets dialogs) **and a matching PPM import
  format** (Open dialog) — so you can export a `.ppm` and re-open it through the plugin.

Each registration is guarded with `?.` so the plugin still loads if the host withholds a
capability — the recommended robust pattern.

## Build & try it

```
dotnet build -c Release
```

Copy the output `Sable.SamplePlugin.dll` and `manifest.json` into:

```
%AppData%/Sable/plugins/sample/
├── manifest.json
└── Sable.SamplePlugin.dll
```

Then enable plugins in **Preferences ▸ Performance ▸ Enable plugins** and check
**Plugins ▸ Manage Plugins…**.

## Learn more

Full authoring guide: [`docs/plugin/AUTHORING.md`](../../docs/plugin/AUTHORING.md).

Key files here:

- [`manifest.json`](manifest.json) — the plugin manifest (capabilities, entrypoint, permissions).
- [`SamplePlugin.cs`](SamplePlugin.cs) — the `IPlugin` entrypoint + a `PpmExportProvider`.
- [`Sable.SamplePlugin.csproj`](Sable.SamplePlugin.csproj) — note `Private="false"` /
  `CopyLocalLockFileAssemblies=false`: a plugin ships **only its own DLL**; the host provides the SDK.
