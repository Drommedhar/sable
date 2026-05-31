#!/usr/bin/env python3
"""
locale-doctor — i18n health check for Sable (see docs/i18n-decision.md).

Three checks:
  1. MISSING keys  — referenced in code/XAML ({loc:Loc x} / Loc.T("x")) but absent
     from en.json. Always a real bug -> exit 1.
  2. DEAD keys     — present in en.json but never referenced. Reported; prunable.
  3. UNLOCALIZED literals — literal user-facing strings in .axaml that should be
     {loc:Loc ...} (Header=, Text=, Content=, Title=, ToolTip.Tip=, Watermark=,
     PlaceholderText=). Reported; --strict makes them fail (flip on once the
     migration sweep is complete so CI enforces full coverage).

Run from repo root:
    python tools/locale-doctor.py
    python tools/locale-doctor.py --strict          # also fail on unlocalized literals
    python tools/locale-doctor.py --list-literals    # print every unlocalized literal + location
    python tools/locale-doctor.py --prune            # remove dead keys (dry-run)
    python tools/locale-doctor.py --prune --apply
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
APP = REPO_ROOT / "src" / "Sable.App"
LOCALES_DIR = APP / "Assets" / "Locales"

# Source roots scanned for key references (Loc.T / {loc:Loc}) and XAML literals.
SCAN_ROOTS = [REPO_ROOT / "src"]
SCAN_EXTS = {".cs", ".axaml"}

# Keys built dynamically at runtime / read directly (not via Loc.T) — never flag/prune.
DYNAMIC_PREFIXES: set[str] = {"language."}   # language.name read by GetLanguageDisplayName

KEY_REF_PATTERNS = [
    re.compile(r'Loc\.T\("([^"]+)"'),
    re.compile(r'Loc\.Instance\["([^"]+)"\]'),
    re.compile(r'\{loc:Loc\s+([\w.]+)\}'),
    re.compile(r'\{loc:Loc\s+Key=([\w.]+)\}'),
]

PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')

# XAML attributes whose literal value is user-facing text that ought to be localized.
LOCALIZABLE_ATTRS = ("Header", "Text", "Content", "Title", "ToolTip.Tip",
                     "Watermark", "PlaceholderText")
ATTR_RE = re.compile(r'\b(' + "|".join(a.replace(".", r"\.") for a in LOCALIZABLE_ATTRS) + r')="([^"]*)"')

# Literal values that are NOT translatable text -> ignore (symbols, numbers, glyphs).
ALLOWED_SYMBOLS = {"×", "✕", "→", "←", "+", "-", "...", "·", "—", "fx", "◑", "⊟"}
SAFE_LITERAL_RE = re.compile(r'^[\s\d.,:%/×✕→←+\-–—°x*]*$')


def flatten(prefix: str, value, out: dict[str, str]) -> None:
    if isinstance(value, dict):
        for k, v in value.items():
            flatten(f"{prefix}.{k}" if prefix else k, v, out)
    elif isinstance(value, list):
        out[prefix] = "[]"
    else:
        out[prefix] = str(value)


def load_locale(path: Path) -> dict[str, str]:
    with path.open(encoding="utf-8-sig") as f:
        data = json.load(f)
    flat: dict[str, str] = {}
    flatten("", data, flat)
    return flat


def iter_source_files():
    for root in SCAN_ROOTS:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if path.suffix not in SCAN_EXTS:
                continue
            if "bin" in path.parts or "obj" in path.parts:
                continue
            # the Loc engine itself carries {loc:Loc ...} / Loc.T("key") examples in doc comments
            if "Localization" in path.parts:
                continue
            yield path


def scan_key_references() -> set[str]:
    refs: set[str] = set()
    for path in iter_source_files():
        try:
            text = path.read_text(encoding="utf-8-sig")
        except Exception:
            continue
        for pat in KEY_REF_PATTERNS:
            for m in pat.finditer(text):
                refs.add(m.group(1))
    return refs


def is_translatable_literal(value: str) -> bool:
    v = value.strip()
    if not v:
        return False
    if v.startswith("{"):          # binding / markup extension — already handled
        return False
    if v in ALLOWED_SYMBOLS:
        return False
    if SAFE_LITERAL_RE.match(v):   # only digits / punctuation / symbols
        return False
    if not re.search(r"[A-Za-z]{2,}", v):  # no real word in it
        return False
    return True


def scan_unlocalized_literals() -> list[tuple[str, int, str, str]]:
    """Returns (relpath, line, attr, value) for each literal user-facing XAML string."""
    found: list[tuple[str, int, str, str]] = []
    for path in iter_source_files():
        if path.suffix != ".axaml":
            continue
        try:
            lines = path.read_text(encoding="utf-8-sig").splitlines()
        except Exception:
            continue
        rel = path.relative_to(REPO_ROOT).as_posix()
        for i, line in enumerate(lines, 1):
            for m in ATTR_RE.finditer(line):
                attr, value = m.group(1), m.group(2)
                if is_translatable_literal(value):
                    found.append((rel, i, attr, value))
    return found


def is_dynamic(key: str) -> bool:
    return any(key.startswith(p) for p in DYNAMIC_PREFIXES)


def placeholder_set(s: str) -> set[str]:
    return set(PLACEHOLDER_RE.findall(s))


def remove_key(data: dict, dotted: str) -> bool:
    parts = dotted.split(".")
    parent = data
    for p in parts[:-1]:
        if not isinstance(parent, dict) or p not in parent:
            return False
        parent = parent[p]
    if isinstance(parent, dict) and parts[-1] in parent:
        del parent[parts[-1]]
        return True
    return False


def prune_empty(data):
    if not isinstance(data, dict):
        return data
    for k in list(data.keys()):
        child = prune_empty(data[k])
        if isinstance(child, dict) and not child:
            del data[k]
    return data


def main() -> int:
    parser = argparse.ArgumentParser(description="Locale doctor for Sable.")
    parser.add_argument("--prune", action="store_true", help="Remove dead keys.")
    parser.add_argument("--apply", action="store_true", help="Actually write files (with --prune).")
    parser.add_argument("--no-fail-on-dead", action="store_true", help="Report dead keys but don't fail.")
    parser.add_argument("--strict", action="store_true", help="Also fail when unlocalized literals remain.")
    parser.add_argument("--list-literals", action="store_true", help="Print every unlocalized literal.")
    args = parser.parse_args()

    en_path = LOCALES_DIR / "en.json"
    if not en_path.exists():
        print(f"ERROR: {en_path} not found", file=sys.stderr)
        return 2

    en = load_locale(en_path)
    other_locales = {p.stem: load_locale(p) for p in LOCALES_DIR.glob("*.json") if p != en_path}
    refs = scan_key_references()

    dead, dynamic_kept = [], []
    for key in sorted(en.keys()):
        if en[key] == "[]" or key in refs:
            continue
        if is_dynamic(key):
            dynamic_kept.append(key)
        else:
            dead.append(key)

    missing = sorted(refs - set(en.keys()))

    placeholder_drift = []
    for lang, locale in other_locales.items():
        for key, en_value in en.items():
            if key in locale and placeholder_set(en_value) != placeholder_set(locale[key]):
                placeholder_drift.append(
                    f"{lang}::{key}  en={sorted(placeholder_set(en_value))} {lang}={sorted(placeholder_set(locale[key]))}")

    literals = scan_unlocalized_literals()

    print("=== Sable locale doctor ===")
    print(f"en.json keys:          {len(en)}")
    for lang, locale in other_locales.items():
        print(f"{lang}.json keys:        {len(locale)}")
    print(f"key references:        {len(refs)}")
    print(f"dead keys:             {len(dead)}")
    print(f"missing keys:          {len(missing)}")
    print(f"placeholder drift:     {len(placeholder_drift)}")
    print(f"unlocalized literals:  {len(literals)}")

    # locale parity: keys present in en but missing from a translation (en fallback covers them at runtime)
    locale_gaps = {lang: sorted(set(en) - set(loc)) for lang, loc in other_locales.items()}
    for lang, gaps in locale_gaps.items():
        if gaps:
            print(f"\n-- {lang}.json missing {len(gaps)} key(s) (falls back to en) --")
            for k in gaps[:30]:
                print(f"  {k}")
            if len(gaps) > 30:
                print(f"  ... +{len(gaps) - 30} more")

    if missing:
        print("\n-- MISSING keys (referenced but absent from en.json) --")
        for k in missing:
            print(f"  {k}")

    if placeholder_drift:
        print("\n-- PLACEHOLDER DRIFT --")
        for line in placeholder_drift:
            print(f"  {line}")

    if dead:
        print("\n-- DEAD keys (in en.json, never referenced) --")
        for k in dead[:50]:
            print(f"  {k}")
        if len(dead) > 50:
            print(f"  ... +{len(dead) - 50} more")

    if literals:
        if args.list_literals:
            print("\n-- UNLOCALIZED literals (.axaml) --")
            for rel, line, attr, value in literals:
                print(f"  {rel}:{line}  {attr}=\"{value}\"")
        else:
            by_file: dict[str, int] = {}
            for rel, _, _, _ in literals:
                by_file[rel] = by_file.get(rel, 0) + 1
            print("\n-- UNLOCALIZED literals per file (top 20; --list-literals for all) --")
            for rel, n in sorted(by_file.items(), key=lambda kv: -kv[1])[:20]:
                print(f"  {n:4d}  {rel}")

    if args.prune and dead:
        print(f"\n-- PRUNING {len(dead)} dead keys --")
        for path in LOCALES_DIR.glob("*.json"):
            with path.open(encoding="utf-8-sig") as f:
                data = json.load(f)
            removed = sum(1 for k in dead if remove_key(data, k))
            data = prune_empty(data)
            if args.apply:
                path.write_text(json.dumps(data, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
                print(f"  {path.name}: removed {removed} keys, written")
            else:
                print(f"  {path.name}: would remove {removed} keys (dry-run; --apply to write)")

    fail = bool(missing) or bool(placeholder_drift)
    fail = fail or (bool(dead) and not args.no_fail_on_dead)
    fail = fail or (bool(literals) and args.strict)
    return 1 if fail else 0


if __name__ == "__main__":
    sys.exit(main())
