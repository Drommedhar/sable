# Sable Sample Plugin

A complete, minimal reference plugin for Sable. Use it as a template.

It declares the capabilities `document.read`, `command.register`, `ui.menu_command`,
`export.provider` and contributes:

- a **command** (`Report Active Document`) in the Ctrl+K palette,
- a **menu item** under **Plugins ▸ Sample**,
- a **PPM (P6) export format** (appears in the Export and Export Assets dialogs).

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
