# Internationalization (i18n)

**Status: IMPLEMENTED.** Full i18n infrastructure is live; Sable ships
**English-only content** (`en.json`) for v1. The system is Novalist's i18n,
ported. Every user-facing `.axaml` string is localized and a build gate keeps it
that way.

## How to add / change a UI string

1. Use the markup extension in XAML: `Header="{loc:Loc menu.file.open}"` (auto
   `xmlns:loc="clr-namespace:Sable.App.Localization"` on the file). In code-behind
   use `Loc.T("key")` / `Loc.T("key", arg0)`.
2. Add the key + English value to `src/Sable.App/Assets/Locales/en.json` (nested
   objects flatten to dotted keys).
3. **A literal user-facing string in `.axaml` fails the build** — the
   `LocaleDoctor` MSBuild target runs `tools/locale-doctor.py --strict` and errors
   on unlocalized literals or missing keys. Run it directly to see what's flagged:
   `python tools/locale-doctor.py --list-literals`.
4. Adding a language = drop `Locales/<code>.json` (same keys) with a
   `language.name`; it appears in the Settings language picker automatically.

The original migration was done by `tools/i18n-migrate.py` (one-shot sweep).

## Decision (rationale, retained)

1. **v1 is English-only content** — only `en.json` ships; the machinery is full.
2. **The mechanism is Novalist's i18n system, ported verbatim** — a JSON-locale
   model, not `.resx`. Concretely:
   - **Locale files**: `src/Sable.App/Assets/Locales/en.json` (+ `de.json`, …),
     nested objects flattened to dotted keys (`menu.file.open`,
     `tool.brush.name`), with `{0}`/`{1}` positional format args. `en` is the
     always-loaded fallback; the active language overlays it (missing key → en).
     Copied to output via `<Content ... CopyToOutputDirectory="PreserveNewest">`.
   - **`Loc` singleton** (`Sable.App/Localization/Loc.cs`),
     `INotifyPropertyChanged` + a `LanguageChanged` event:
     - `Loc.Initialize(localesDir, language)` once at startup.
     - `Loc.T("key")` / `Loc.T("key", arg0, arg1)` for code-behind.
     - indexer `Loc.Instance["key"]` for compiled `{Binding}`s.
     - `CurrentLanguage` setter reloads + fires the change notification so every
       binding refreshes live (no restart).
   - **Markup extension** `{loc:Loc menu.file.open}` (`LocExtension.cs`) — a
     weak-ref binding that auto-refreshes on `LanguageChanged` and never roots
     the visual tree. This is how XAML consumes strings.
   - **Language picker** in Settings (Preferences ▸ UI ▸ Language), persisted in
     `SableSettings`; sets `Loc.CurrentLanguage`.
   - **Key-audit tool** ported from Novalist's `tools/locale-doctor.py` (finds
     dead / missing / placeholder-drift keys; exit 1 on drift → CI gate).
3. **Format/culture data** (numbers, dates, file sizes): `InvariantCulture` for
   serialized values, UI culture only for display — already the practice in the
   codebase (e.g. `VramBadge`).

## Why JSON over .resx (Novalist's rationale, adopted)

- Shared workflow + tooling with Novalist (`locale-doctor`, contributor docs).
- Plain files are diff-friendly and editable by translators without an IDE.
- A future plugin/extension tier can ship its own `Locales/*.json` merged into
  the host catalog — exactly how Novalist's `ExtensionLocalizationService` works.

## What shipped

1. `Sable.App/Localization/Loc.cs` (singleton, `Loc.T` / indexer / `LanguageChanged`)
   + `LocExtension.cs` (`{loc:Loc key}`). `Loc.Initialize` called in `App` before
   the main window loads.
2. `src/Sable.App/Assets/Locales/en.json` (csproj `Content`, copied next to the
   exe). All user-facing `.axaml` strings migrated (`tools/i18n-migrate.py`).
3. `SableSettings.Language` (default `en`); `App` sets `Loc` to it at startup.
   A live Settings language picker (sets `Loc.CurrentLanguage`, no restart) is
   trivial to add once a 2nd locale ships — pending, single-language now.
4. `tools/locale-doctor.py` — missing/dead/drift + unlocalized-literal scan; wired
   into the build as the `LocaleDoctor` MSBuild target (`--strict`, fails on drift).

## Remaining (optional, not blocking v1)

- Code-behind literals (`ConfirmWindow.Ask`, dynamically-built rows, dialog
  messages) are not yet swept — the gate covers `.axaml` only. Migrate via
  `Loc.T(...)` opportunistically.
- Ship a real second locale (e.g. `de.json`) when translation is wanted.
