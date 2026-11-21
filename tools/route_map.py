#!/usr/bin/env python3
"""Harvest HTTP route / URL literals from a decompiled C# tree.

usage: python route_map.py <decompiled_root> <out.md>

Produces a markdown inventory: literal | kind | first seens (file:line).
Only string literals that look like HTTP paths or absolute URLs survive the filter.
"""
import os
import re
import sys
from collections import defaultdict

STR_LIT = re.compile(r'"((?:[^"\\]|\\.){2,200})"')

ROUTE_HINTS = (
    "/api/",
    "/sessions",
    "/commands",
    "/lobbies",
    "/queue",
    "/spectate",
    "/tournament",
    "/dev/",
    "/static",
    "/health",
    "/events",
)

URL_RE = re.compile(r"https?://[^\s\"<>]+")

SKIP_PARTS = ("obj\\", "obj/", ".csproj", "Properties\\")

# things that are clearly filesystem paths or code fragments
BAD_PREFIX = ("C:", "D:", "E:", "/mnt/", "/Volumes/", "Assets/", "Packages/")


def classify(lit):
    if URL_RE.match(lit):
        return "abs-url"
    if any(lit.startswith(p) for p in BAD_PREFIX):
        return None
    if lit.startswith("/") or any(h in lit for h in ROUTE_HINTS):
        if lit.endswith(".cs") or ".dll" in lit:
            return None
        return "route"
    return None


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    root, out = sys.argv[1], sys.argv[2]
    hits = defaultdict(list)  # literal -> [(assembly, relpath, line)]
    for dirpath, _dirnames, filenames in os.walk(root):
        for fn in filenames:
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, root)
            if any(p in rel for p in SKIP_PARTS):
                continue
            assembly = rel.split(os.sep)[0]
            try:
                with open(full, "r", encoding="utf-8", errors="ignore") as fh:
                    for lineno, line in enumerate(fh, 1):
                        for m in STR_LIT.finditer(line):
                            lit = m.group(1)
                            kind = classify(lit)
                            if kind and len(hits[lit]) < 4:
                                hits[lit].append((assembly, rel, lineno))
            except OSError:
                continue

    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    rows = sorted(hits.items(), key=lambda kv: (classify(kv[0]), kv[0]))
    with open(out, "w", encoding="utf-8") as fh:
        fh.write("# Route / URL literal inventory\n\n")
        fh.write(f"Source tree: `{root}`  \nLiterals: **{len(rows)}**\n\n")
        fh.write("| literal | kind | used in |\n|---|---|---|\n")
        for lit, uses in rows:
            first = "<br>".join(f"`{a}` {p}:{n}" for a, p, n in uses[:2])
            safe = lit.replace("|", "\\|")
            fh.write(f"| `{safe}` | {classify(lit)} | {first} |\n")
    print(f"wrote {out}: {len(rows)} literals")
    return 0


if __name__ == "__main__":
    sys.exit(main())
