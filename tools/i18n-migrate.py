#!/usr/bin/env python3
"""
ONE-SHOT i18n migrator (see docs/i18n-decision.md). Sweeps every Sable.App .axaml,
replaces literal user-facing attribute strings (Header/Text/Content/Title/
ToolTip.Tip/Watermark/PlaceholderText) with {loc:Loc <key>}, injects the loc
namespace, and writes the keys + English values into en.json.

Keys are namespaced per window/control (file stem) with a slug from the value;
identical values within a file share a key. The hand-curated menu.* keys are
already {loc:Loc ...} so they are left untouched.

Usage:
    python tools/i18n-migrate.py            # dry run: report only
    python tools/i18n-migrate.py --apply    # rewrite the .axaml files + en.json
"""
from __future__ import annotations

import argparse
import json
import re
from collections import OrderedDict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
APP = REPO_ROOT / "src" / "Sable.App"
EN_PATH = APP / "Assets" / "Locales" / "en.json"
LOC_NS = 'xmlns:loc="clr-namespace:Sable.App.Localization"'

LOCALIZABLE_ATTRS = ("Header", "Text", "Content", "Title", "ToolTip.Tip",
                     "Watermark", "PlaceholderText")
ATTR_RE = re.compile(r'\b(' + "|".join(a.replace(".", r"\.") for a in LOCALIZABLE_ATTRS) + r')="([^"]*)"')
ALLOWED_SYMBOLS = {"×", "✕", "→", "←", "+", "-", "...", "·", "—", "fx", "◑", "⊟"}
SAFE_LITERAL_RE = re.compile(r'^[\s\d.,:%/×✕→←+\-–—°x*]*$')


def translatable(value: str) -> bool:
    v = value.strip()
    if not v or v.startswith("{") or v in ALLOWED_SYMBOLS:
        return False
    if SAFE_LITERAL_RE.match(v):
        return False
    return bool(re.search(r"[A-Za-z]{2,}", v))


def stem_ns(path: Path) -> str:
    s = path.stem  # e.g. MainWindow
    return s[0].lower() + s[1:]


def slugify(value: str) -> str:
    v = value.replace("_", " ").replace("...", " ").replace("&amp;", " ")
    words = re.findall(r"[A-Za-z0-9]+", v)
    if not words:
        return "x"
    words = words[:5]
    return words[0].lower() + "".join(w[:1].upper() + w[1:] for w in words[1:])


def ensure_loc_ns(text: str) -> str:
    if "xmlns:loc=" in text:
        return text
    # insert right after the first xmlns:x="..." declaration
    m = re.search(r'xmlns:x="[^"]*"', text)
    if not m:
        return text
    i = m.end()
    return text[:i] + "\n        " + LOC_NS + text[i:]


def migrate_file(path: Path):
    text = path.read_text(encoding="utf-8-sig")
    ns = stem_ns(path)
    value_to_key: dict[str, str] = {}
    used_keys: set[str] = set()
    added: "OrderedDict[str, str]" = OrderedDict()
    count = 0

    def make_key(value: str) -> str:
        if value in value_to_key:
            return value_to_key[value]
        base = f"{ns}.{slugify(value)}"
        key = base
        n = 2
        while key in used_keys:
            key = f"{base}{n}"
            n += 1
        used_keys.add(key)
        value_to_key[value] = key
        added[key] = value
        return key

    out_lines = []
    for line in text.splitlines(keepends=True):
        if "<!--" in line and "-->" in line:   # skip whole-line comments
            out_lines.append(line)
            continue

        def repl(m: re.Match) -> str:
            nonlocal count
            attr, value = m.group(1), m.group(2)
            if not translatable(value):
                return m.group(0)
            count += 1
            return f'{attr}="{{loc:Loc {make_key(value)}}}"'

        out_lines.append(ATTR_RE.sub(repl, line))

    new_text = "".join(out_lines)
    if count:
        new_text = ensure_loc_ns(new_text)
    return new_text if count else None, count, added


def set_flat_key(root: dict, dotted: str, value: str):
    """Store under a per-namespace object so en.json stays grouped + readable."""
    parts = dotted.split(".", 1)
    if len(parts) == 1:
        root[dotted] = value
        return
    head, rest = parts
    node = root.setdefault(head, {})
    if isinstance(node, dict):
        node[rest] = value
    else:
        root[dotted] = value


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--apply", action="store_true")
    args = ap.parse_args()

    en = json.loads(EN_PATH.read_text(encoding="utf-8-sig"))
    total = 0
    files_changed = 0
    all_added: "OrderedDict[str, str]" = OrderedDict()

    for path in sorted(APP.rglob("*.axaml")):
        if "bin" in path.parts or "obj" in path.parts:
            continue
        new_text, count, added = migrate_file(path)
        if count:
            files_changed += 1
            total += count
            print(f"  {count:4d}  {path.relative_to(REPO_ROOT).as_posix()}")
            for k, v in added.items():
                all_added[k] = v
            if args.apply and new_text is not None:
                path.write_text(new_text, encoding="utf-8")

    print(f"\n{total} literals across {files_changed} files; {len(all_added)} unique keys.")

    if args.apply:
        for k, v in all_added.items():
            set_flat_key(en, k, v)
        EN_PATH.write_text(json.dumps(en, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"Wrote {len(all_added)} keys into {EN_PATH.relative_to(REPO_ROOT).as_posix()}")
    else:
        print("Dry run. Re-run with --apply to rewrite files + en.json.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
