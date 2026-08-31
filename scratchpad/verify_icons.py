#!/usr/bin/env python3
"""Reassemble every string literal in Icons.cs and check it parses as path data.

THE DEFECT THIS CATCHES.  The icon literals are long, so they are written as
`"..." + "..." + "..."` split over many source lines.  If a break lands between
a coordinate's x and y and neither chunk carries a space across the join, the
two numbers FUSE into one - `L2.43 3.99` + `L2.12` becomes `...3.992.12...` -
which is still a legal C# expression, still compiles, and renders BLANK.  Only
reassembling the concatenation and re-parsing it finds that.

Run after ANY edit to Icons.cs:

    python scratchpad/verify_icons.py

Exit status 0 = every literal reassembles and parses.  Non-zero = a fused
coordinate, a bad command letter, or a wrong argument count, with the icon
named and the offending token shown.
"""
from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICONS = os.path.join(ROOT, "src", "Quill", "Helpers", "Icons.cs")
SRC_ROOT = os.path.join(ROOT, "src", "Quill")

# Text-source file types under src/Quill/ - deliberately excludes binary
# formats (.png, .ico, .dll, ...) that legitimately contain 0x00 bytes.
NUL_CHECK_EXTS = {
    ".cs", ".xaml", ".csproj", ".manifest", ".appxmanifest", ".resw", ".svg",
}
NUL_CHECK_SKIP_DIRS = {"bin", "obj", ".vs"}

# Argument counts per SVG path command, from the SVG 1.1 grammar.
ARGC = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "S": 4, "Q": 4, "T": 2, "A": 7, "Z": 0}

# A number as XAML's parser accepts it.  Deliberately strict: no second decimal
# point, which is exactly what a fused pair produces ("3.992.12").
NUM = re.compile(r"[-+]?(?:\d+\.\d+|\d+\.|\.\d+|\d+)(?:[eE][-+]?\d+)?")


def literals(text: str):
    """Yield (name, reassembled_value, line) for every `const string NAME = "..."`.

    Handles the `"a" + "b" + "c";` concatenation form, skips // comments between
    the chunks, and stops at the terminating semicolon.
    """
    decl = re.compile(r'public\s+const\s+string\s+(\w+)\s*=', re.M)
    for m in decl.finditer(text):
        name = m.group(1)
        line = text.count("\n", 0, m.start()) + 1
        i, depth_end = m.end(), None
        # Walk forward to the semicolon that ends the declaration, collecting
        # the contents of every double-quoted chunk on the way.
        chunks, j = [], i
        while j < len(text):
            c = text[j]
            if c == '"':
                k = j + 1
                buf = []
                while k < len(text) and text[k] != '"':
                    if text[k] == "\\":       # escape - keep both chars
                        buf.append(text[k:k + 2]); k += 2; continue
                    buf.append(text[k]); k += 1
                chunks.append("".join(buf))
                j = k + 1
                continue
            if c == "/" and text[j:j + 2] == "//":
                j = text.find("\n", j)
                if j < 0:
                    break
                continue
            if c == ";":
                depth_end = j
                break
            j += 1
        if depth_end is None:
            continue
        yield name, "".join(chunks), line


FILL = re.compile(r"^\s*F[01]\s*")


def tokenise(d: str):
    """Split path data into (command, [numbers]) or raise ValueError."""
    d = FILL.sub("", d)                 # the optional fill-rule prefix
    out, i, n = [], 0, len(d)
    cmd, args = None, []
    while i < n:
        c = d[i]
        if c in " ,\t\r\n":
            i += 1
            continue
        if c.isalpha():
            if cmd is not None:
                out.append((cmd, args))
            if c.upper() not in ARGC:
                raise ValueError(f"unknown command {c!r} at offset {i}")
            cmd, args = c, []
            i += 1
            continue
        m = NUM.match(d, i)
        if not m:
            raise ValueError(f"not a number at offset {i}: {d[i:i + 24]!r}")
        # THE FUSE CHECK.  A well-formed literal never has a digit-dot-digit-dot
        # run; a fused x/y pair always does.  NUM stops at the second dot, so if
        # the very next char continues the number, the pair fused.
        end = m.end()
        if end < n and (d[end] == "." or d[end].isdigit()):
            raise ValueError(
                f"FUSED COORDINATE at offset {i}: {d[max(0,i-12):i+24]!r} - "
                "a line break landed between an x and its y")
        if cmd is None:
            raise ValueError(f"number before any command at offset {i}")
        args.append(float(m.group(0)))
        i = end
    if cmd is not None:
        out.append((cmd, args))
    return out


def check(name: str, d: str) -> list[str]:
    errs = []
    if not d.strip():
        return errs
    if FILL.sub("", d).lstrip()[:1].upper() != "M":
        errs.append(f"{name}: path does not start with M")
    try:
        toks = tokenise(d)
    except ValueError as e:
        return errs + [f"{name}: {e}"]
    for cmd, args in toks:
        k = ARGC[cmd.upper()]
        if k == 0:
            if args:
                errs.append(f"{name}: {cmd} takes no arguments, got {len(args)}")
        elif len(args) == 0 or len(args) % k:
            errs.append(f"{name}: {cmd} wants a multiple of {k} arguments, got {len(args)}")
    return errs


def check_no_nul_bytes() -> tuple[int, list[str]]:
    """Every text source file under src/Quill/ must be free of 0x00 bytes.

    A stray NUL inside a string literal (typed/pasted where the escape "\\0"
    was meant) makes grep/ripgrep classify the whole file as binary and
    silently drop matching lines from search results - ToolWheel.cs's
    `_taken` sentinel has reintroduced exactly this twice already.
    """
    scanned, bad = 0, []
    for dirpath, dirnames, filenames in os.walk(SRC_ROOT):
        dirnames[:] = [d for d in dirnames if d not in NUL_CHECK_SKIP_DIRS]
        for fn in filenames:
            if os.path.splitext(fn)[1].lower() not in NUL_CHECK_EXTS:
                continue
            path = os.path.join(dirpath, fn)
            scanned += 1
            with open(path, "rb") as f:
                if b"\x00" in f.read():
                    bad.append(f"  {os.path.relpath(path, ROOT)}: contains a 0x00 byte")
    return scanned, bad


def main() -> int:
    text = open(ICONS, "r", encoding="utf-8").read()
    total, bad = 0, []
    for name, value, line in literals(text):
        # Only geometry literals; a const string that is not path data has no
        # command letters and no digits to fuse.  The optional F0/F1 fill-rule
        # prefix counts as path data - leaving it out of this test silently
        # EXCLUDED Opacity and UndoRound, the two literals that carry one.
        if not re.match(r"^\s*(?:F[01]\s*)?[Mm][\s\d\-+.]", value):
            continue
        total += 1
        for e in check(name, value):
            bad.append(f"  Icons.cs:{line}  {e}")
    print(f"verify_icons: {total} path literals reassembled")

    nul_scanned, nul_bad = check_no_nul_bytes()
    print(f"verify_icons: {nul_scanned} source files scanned for 0x00 bytes")
    bad += nul_bad

    if bad:
        print("FAILED:")
        print("\n".join(bad))
        return 1
    print("OK - every literal parses; no fused coordinates; no NUL bytes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
