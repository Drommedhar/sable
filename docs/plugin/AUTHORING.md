# Sable Plugin Authoring Guide

How to write, build, and install a plugin for Sable.

Sable plugins are **.NET assemblies** loaded in-process. A plugin contributes
**commands**, **menu items**, and **export formats**, and can read/edit the active
document — all gated by **capabilities** it declares in a manifest. The platform is
**opt-in**: a user must enable plugins in Preferences before any are loaded.

> Reference example: [`samples/Sable.SamplePlugin`](../../samples/Sable.SamplePlugin) —
> a complete, minimal plugin that registers a command, a menu item, and a PPM exporter.
> The architecture/host-side design lives in [PLUGIN_SDK_PLAN.md](../../plans/PLUGIN_SDK_PLAN.md)
> and [boundary_map.md](boundary_map.md).

---

## 1. How it works (in 60 seconds)

1. You build a class library that references **`Sable.Plugin.Sdk`** (BCL-only; no engine types).
2. It implements **`IPlugin`** and ships next to a **`manifest.json`**.
3. The user drops the folder into `…/Sable/plugins/<your-plugin>/` and enables plugins.
4. On launch, the host **discovers → validates the manifest → loads the assembly into an
   isolated `AssemblyLoadContext` → calls `Initialize`**.
5. In `Initialize` you receive an **`IHostContext`**. Each API on it is **non-null only if you
   declared the matching capability** — that is the entire permission model for the API surface.
6. You register commands / menus / export providers. The user runs them; the host routes
   everything through its undo stack and a per-plugin crash boundary.

There is **no IPC and no separate process** in this tier — plugins run inside the app with
full CLR trust. Only enable plugins you trust. (An out-of-process sandbox is a future tier.)

---

## 2. Quick start

### 2.1 The project file

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <!-- Ship ONLY your own DLL. The host already provides the SDK assembly at load time;
         bundling a second copy can cause type-identity mismatches. -->
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <!-- Private="false" => the SDK is a compile-time reference, not copied to your output. -->
    <ProjectReference Include="path/to/Sable.Plugin.Sdk.csproj" Private="false" />
    <!-- (or a future NuGet: <PackageReference Include="Sable.Plugin.Sdk" ... />) -->
  </ItemGroup>

  <ItemGroup>
    <None Include="manifest.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

### 2.2 The manifest (`manifest.json`)

```json
{
  "id": "com.example.myplugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "sdk_version": "1",
  "entrypoint": "Example.MyPlugin",
  "capabilities": ["document.read", "command.register"],
  "permissions": { "filesystem_read": "none", "filesystem_write": "none", "network": false, "gpu": false },
  "author": "You",
  "website": "https://example.com"
}
```

### 2.3 The entrypoint

```csharp
using Sable.Plugin.Sdk;
using Sable.Plugin.Sdk.Commands;
using Sable.Plugin.Sdk.Host;

namespace Example;

public sealed class MyPlugin : IPlugin
{
    public void Initialize(IHostContext host)
    {
        host.Logger.Info("MyPlugin loaded.");

        // Commands is null unless "command.register" was declared.
        host.Commands?.Register(new PluginCommand
        {
            Id = "hello",
            Title = "Say Hello",
            Category = "My Plugin",
            Run = () => host.Logger.Info("Hello from MyPlugin!"),
        });
    }

    public void Shutdown() { }
}
```

The manifest `entrypoint` is the **fully-qualified type name** (`Example.MyPlugin`) and the
type must implement `IPlugin` with a public parameterless constructor.

### 2.4 Build & install

```
dotnet build -c Release
```

Copy the build output into a folder under the Sable plugins directory:

```
%AppData%/Sable/plugins/myplugin/
├── manifest.json
└── MyPlugin.dll
```

(`%AppData%` = `C:\Users\<you>\AppData\Roaming` on Windows; the equivalent app-data dir on
macOS/Linux.) Then in Sable: **Preferences ▸ Performance ▸ Enable plugins**, and use
**Plugins ▸ Manage Plugins…** to confirm it loaded (and to see any errors).

---

## 3. The manifest, field by field

Keys are **snake_case**. The parser collects **every** error at once, so a bad manifest tells
you all its problems in the Manage-Plugins error list.

| Field | Required | Notes |
|---|---|---|
| `id` | yes | Reverse-DNS identifier, e.g. `com.example.plugin`. Must contain a dot; letters/digits/`.`/`_`/`-` only. Used as the plugin's stable key. |
| `name` | yes | Human-readable display name. |
| `version` | yes | Your plugin's version string (semver recommended; not enforced). |
| `sdk_version` | yes | SDK **major** you built against, e.g. `"1"`. Must be compatible with the host (see §9). |
| `entrypoint` | yes | Fully-qualified `IPlugin` type name. |
| `capabilities` | yes | Non-empty array of capability ids (see §4). Unknown or duplicate ids fail validation. |
| `permissions` | no | Object (see §5). Absent = deny-all. |
| `author` | no | Display string. |
| `website` | no | URL. |
| `support` | no | Support URL / contact. |
| `min_host_version` | no | Minimum Sable version; compared lexically by the host. |

A manifest that omits a required field, declares an **unknown capability**, declares **zero
capabilities**, has a malformed `sdk_version`, or whose `id` isn't reverse-DNS will fail to load
and appear in Manage Plugins with the reason.

---

## 4. Capabilities

A capability is a string id you list in `capabilities`. **Declaring a capability is what makes
the matching host API non-null.** If you call an API you didn't declare, it's simply `null` —
guard every optional API with `?.`.

> Grant model today: a loaded plugin receives **every capability it declares** (the user's trust
> decision is the global *Enable plugins* toggle plus per-plugin enable/disable in Manage
> Plugins). There is not yet a per-capability approval prompt.

### Implemented (usable now)

| Capability | Unlocks | API |
|---|---|---|
| `document.read` | Read the active document's size/dpi/depth/selection/ICC | `host.Document` → `IDocumentApi` |
| `layer.read` | Read the layer tree (flattened, with ids) | `host.Layers` → `ILayerApi` |
| `layer.write.basic` | Name/opacity/fill/blend/visibility + add/remove/move layers, each undoable | `host.LayerWrites` → `ILayerWriteApi` |
| `command.register` | Add commands to the Ctrl+K command palette | `host.Commands` → `ICommandApi` |
| `ui.menu_command` | Add items under the **Plugins** menu | `host.Menus` → `IMenuApi` |
| `export.provider` | Contribute a file-export format | `host.Export` → `IExportApi` |
| `import.provider` | Contribute a file-open format | `host.Import` → `IImportApi` |
| `selection.read` | Read the active selection (bounds + mask) | `host.Selection` → `ISelectionApi` |
| `pixel.read` | Read active-layer + composite pixels (RGBA8) | `host.Pixels` → `IPixelApi` |
| `undo.transaction` | Group several edits into one undo step | `host.Transactions` → `ITransactionApi` |

### Declared but not yet surfaced on `IHostContext`

These ids are **known to the validator** (so manifests using them load) but the current app host
does not yet expose an API for them — declaring them grants nothing usable yet:

`automation.batch` (an `IBatchApi` contract exists in the SDK but isn't wired into the host),
`pixel.write.layer_output`, `ui.panel`, `document.events`, `filter.node`, `generator.node`,
`gpu.compute`, `external_tool.bridge`.

Declare only what you use — it keeps the trust surface honest and future-proof.

---

## 5. Permissions

Permissions are declared in the `permissions` object. They describe what the plugin *intends* to
do with host-mediated resources and are surfaced for the user's trust decision.

| Key | Type | Meaning |
|---|---|---|
| `filesystem_read` | `none` / `scoped` / `full` | Read files (`scoped` = a plugin-owned dir only). |
| `filesystem_write` | `none` / `scoped` / `full` | Write files. |
| `network` | bool | Network access. |
| `gpu` | bool | GPU compute. |
| `clipboard` | bool | Clipboard access. |
| `external_process` | bool | Launch external processes. |
| `document_metadata` | bool | Read document metadata. |

> **Security note:** the user must **approve** your declared capabilities + permissions before the
> plugin runs (consent prompt). However, permissions are currently **declarative**, not
> sandbox-enforced — once approved, a plugin runs in-process with full CLR trust and *can*
> technically do more than it declared. Consent + enforcement together arrive with the future
> out-of-process tier. Declare only what you use, and users: only approve plugins you trust.

---

## 6. The host APIs

`IHostContext` is your single handle to the host. `Manifest`, `Logger`, and `Settings` are always
present; the rest are capability-gated (null when not granted).

**Threading:** every API is called on the **host UI thread**. `Initialize` and your
command/menu callbacks run there too — **do not block**. For long work, start your own
`Task`/thread and marshal results back; the host serialises edits.

### 6.1 Logger — `host.Logger` (always available)

```csharp
host.Logger.Info("message");
host.Logger.Warn("careful");
host.Logger.Error("failed", exception);
host.Logger.Debug("detail");
```

Entries are tagged with your plugin id and shown in **Manage Plugins ▸ Log**. Log here rather
than to `Console`.

### 6.2 Settings — `host.Settings` (always available)

A per-plugin key/value store, persisted in a namespace private to your plugin (other plugins and
the host can't read it). Values are strings — encode structured data as JSON yourself.

```csharp
host.Settings.Set("apiKey", value);
var v = host.Settings.Get("apiKey");        // null if absent
if (host.Settings.Contains("apiKey")) { … }
host.Settings.Remove("apiKey");
host.Settings.Save();                         // flush (host also saves on shutdown)
```

### 6.3 Document read — `host.Document` (`document.read`)

```csharp
DocumentInfo? doc = host.Document?.Active;    // null when no document is open
// doc.Width, doc.Height, doc.Dpi, doc.Depth ("8"/"16"/"32"),
// doc.LayerCount, doc.IccProfileName,
// doc.HasSelection + doc.Selection{X,Y,Width,Height}
```

### 6.4 Layer read — `host.Layers` (`layer.read`)

```csharp
IReadOnlyList<LayerInfo> all = host.Layers!.All();   // flattened depth-first, bottom→top
LayerInfo? sel = host.Layers!.Selected();            // single selection, else null
LayerInfo? l   = host.Layers!.ById(someId);
```

`LayerInfo` is a read-only snapshot: `Id` (opaque, stable for the session — use it with the write
API), `Name`, `Kind` (`"pixel" | "adjustment" | "filter" | "group" | "shape" | "text" | "path"`),
`Opacity`/`FillOpacity` (0..1), `Blend` (`SdkBlendMode`), `Visible`, `Clipped`, the three
`Lock*` flags, `ColorTag`, `OffsetX/Y`, `HasMask`, `HasEffects`, `ParentId` (null at root),
`ChildIds`, and tight content `Bounds{X,Y,Width,Height}`.

### 6.5 Layer write — `host.LayerWrites` (`layer.write.basic`)

Every method is **one undoable step** on the document's undo stack. Ids come from `ILayerApi`;
an unknown/stale id throws.

```csharp
host.LayerWrites!.SetName(id, "New name");
host.LayerWrites!.SetOpacity(id, 0.5f);       // clamped 0..1
host.LayerWrites!.SetFillOpacity(id, 0.8f);   // clamped 0..1
host.LayerWrites!.SetBlend(id, SdkBlendMode.Multiply);
host.LayerWrites!.SetVisible(id, false);

string newId = host.LayerWrites!.AddPixelLayer("Layer", parentId: null, index: -1); // -1 = top
host.LayerWrites!.Move(id, +1);               // reorder within current parent
host.LayerWrites!.Remove(id);
```

### 6.6 Commands — `host.Commands` (`command.register`)

```csharp
host.Commands?.Register(new PluginCommand
{
    Id = "do-thing",                 // unique within your plugin
    Title = "Do The Thing",
    Category = "My Plugin",          // optional palette grouping
    Run = () => { /* on the UI thread */ },
});
```

Registered commands appear in the **Ctrl+K command palette**.

### 6.7 Menus — `host.Menus` (`ui.menu_command`)

```csharp
host.Menus?.AddCommand(new MenuContribution
{
    Id = "do-thing",
    Title = "Do The Thing",
    MenuPath = "Tools",              // optional "A/B" nested sub-menus under Plugins
    Run = () => { … },
});
```

Items land under the host **Plugins** menu; `MenuPath = "Export/Batch"` creates/uses nested
sub-menus.

### 6.8 Export — `host.Export` (`export.provider`)

Implement `IExportProvider` and register it; your format then appears in the **Export** and
**Export Assets** dialogs alongside the built-in formats. The host renders the (scaled) composite
and calls your `Encode`.

```csharp
public sealed class MyExporter : IExportProvider
{
    public string Id => "myfmt";
    public string Label => "My Format";
    public string Extension => "myf";       // no dot
    public bool SupportsAlpha => true;

    public byte[] Encode(ExportImage image, ExportOptions options)
    {
        // image.Width, image.Height, image.Rgba (RGBA8, straight alpha, row-major, length W*H*4)
        // options.Quality (0..100), options.IccProfile/IccProfileName, options.Progress, options.Cancellation
        return Encode(image.Rgba, image.Width, image.Height);
    }
}

host.Export?.Register(new MyExporter());
```

The host flattens/scales the composite before calling you; honour `options.Cancellation`.

### 6.8b Import — `host.Import` (`import.provider`)

Mirror of export: contribute an **open** format. Your extensions appear in the Open dialog, and a
matching file is decoded through your provider into a new single-layer document.

```csharp
public sealed class MyImporter : IImportProvider
{
    public string Id => "myfmt";
    public string Label => "My Format";
    public IReadOnlyList<string> Extensions => new[] { "myf" };   // lowercase, no dot

    public ImportImage Decode(byte[] data)   // throw on a malformed file
        => new() { Width = w, Height = h, Rgba = DecodeToRgba8(data) };
}

host.Import?.Register(new MyImporter());
```

### 6.9 Selection read — `host.Selection` (`selection.read`)

```csharp
var sel = host.Selection?.Current;          // null when no document
if (sel is { HasSelection: true })
{
    // sel.X / sel.Y / sel.Width / sel.Height  (doc px bounds)
    byte[]? mask = sel.Mask;                 // doc-sized coverage (255=in, 0=out), or null for a plain rect
}
```

### 6.10 Pixel read — `host.Pixels` (`pixel.read`)

```csharp
var layer = host.Pixels?.ActiveLayer();     // active pixel layer (its own size), or null
var comp  = host.Pixels?.Composite();        // flattened doc composite, or null when unavailable
// buffer.Width / buffer.Height / buffer.Rgba (RGBA8, straight alpha). Copies — safe to read.
```

### 6.11 Transactions — `host.Transactions` (`undo.transaction`)

Group several layer-write calls so the user undoes them as **one** step:

```csharp
host.Transactions?.Run("Recolour layers", () =>
{
    foreach (var l in host.Layers!.All())
        host.LayerWrites!.SetOpacity(l.Id, l.Opacity * 0.5f);
});   // one history entry; undo reverts the whole batch. If the body throws, nothing is recorded.
```

Without the capability, fall back to making the writes directly (each becomes its own undo step).

---

## 7. Blend modes

`SdkBlendMode` mirrors the engine's blend list 1:1 (`Normal=0, Multiply=1, Screen=2, …, Glow=29`).
Use it with `ILayerWriteApi.SetBlend` and read it from `LayerInfo.Blend`. The enum is SDK-owned —
never reference engine types.

---

## 8. Lifecycle & safety

```
Discovered → (manifest valid) → Loaded → (user approves) → (Initialize ok) → Active
                   │ invalid          │ not approved              │ throws
                   ▼                  ▼                           ▼
                 Failed         NeedsConsent              (caught; may quarantine)
```

- **Consent.** A plugin loads but does **not run** until the user approves the exact set of
  capabilities + permissions it requests (shown in a prompt on install, and an **Approve…** button
  on its card). If a later version asks for *more* access, the user is re-prompted — a plugin can't
  silently widen its reach.

- **Crash isolation.** Every call into your plugin (`Initialize`, command/menu callbacks,
  `Shutdown`) runs behind a try/catch. A throw is logged, not propagated to the host.
- **Quarantine.** A plugin that throws repeatedly (3 strikes) is quarantined for the session.
- **Shutdown.** `Shutdown()` is called on disable/unload and app close — release resources there.
- **Disable/enable** at runtime via Manage Plugins; **Reload** re-scans the folder for newly
  added plugins.

---

## 9. SDK versioning

The SDK has a single integer **major** version (currently **1**). Your manifest's `sdk_version`
must be compatible: the host loads a plugin when `MinSupportedMajor ≤ pluginMajor ≤ Current`
(P0 policy = exact match on `1`). A breaking contract change bumps the major; additive changes
(new capabilities/APIs) do not.

---

## 10. Installing & managing

- **Location:** `…/Sable/plugins/<folder>/` containing your `manifest.json` + DLL(s).
- **Enable:** Preferences ▸ Performance ▸ *Enable plugins* (off by default; takes effect live).
- **Manage:** Preferences ▸ **Plugins** — install (folder or .zip), **approve** a plugin's
  requested access, enable/disable, uninstall, reload, and open the plugins folder.
- **Per-plugin settings** live in `…/Sable/plugin-settings/<id>.json`.

---

## 11. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| Plugin not in Manage Plugins | Wrong folder, or no `manifest.json` beside the DLL. |
| Listed as **Failed** with errors | Read the error rows: missing field, `unknown capability`, bad `sdk_version`, non-reverse-DNS `id`, or `entrypoint … not found`. |
| `entrypoint … not found` | Manifest `entrypoint` must equal the type's **fully-qualified** name and implement `IPlugin`. |
| A `host.Xxx` API is `null` | You didn't declare that capability — add it to `capabilities`. |
| Works once then disappears | Quarantined after repeated throws — check the log; fix the exception. |
| Type-load / cast errors | You bundled your own copy of `Sable.Plugin.Sdk` — set `Private="false"` / `CopyLocalLockFileAssemblies=false` and ship only your DLL. |

---

## 12. Full worked example

See [`samples/Sable.SamplePlugin`](../../samples/Sable.SamplePlugin):

- [`manifest.json`](../../samples/Sable.SamplePlugin/manifest.json) — declares
  `document.read`, `command.register`, `ui.menu_command`, `export.provider`.
- [`SamplePlugin.cs`](../../samples/Sable.SamplePlugin/SamplePlugin.cs) — registers a command and
  a menu item (both report the active document's size via `host.Document`), and a `PpmExportProvider`
  that writes a binary PPM (P6) image. Every registration is guarded with `?.` so the plugin still
  loads if a capability is withheld.

Build it, copy `Sable.SamplePlugin.dll` + `manifest.json` into
`…/Sable/plugins/sample/`, enable plugins, and you'll find **Report Active Document** in the
command palette and under **Plugins ▸ Sample**, plus **Portable Pixmap (PPM)** as a choice in the
Export and Export Assets dialogs.
