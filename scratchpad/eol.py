#!/usr/bin/env python3
"""Force a file's line endings back to what that file uses.

Line endings in this tree are PER FILE - docs are LF, MainWindow.xaml.cs is
CRLF, ToolWheel.cs is LF - and an editor that rewrites a whole file with one
convention turns a three-line change into a whole-file diff.  Run this after
any whole-file rewrite:

    python scratchpad/eol.py src/Quill/Helpers/Icons.cs crlf
    python scratchpad/eol.py --check <file>          # just report
"""
import sys


def counts(b):
    crlf = b.count(b"\r\n")
    return crlf, b.count(b"\n") - crlf


def main(a):
    if a[1] == "--check":
        for p in a[2:]:
            c, l = counts(open(p, "rb").read())
            print(f"{p}: CRLF={c} LF={l}")
        return 0
    path, want = a[1], a[2].lower()
    b = open(path, "rb").read()
    flat = b.replace(b"\r\n", b"\n")
    out = flat.replace(b"\n", b"\r\n") if want == "crlf" else flat
    if out != b:
        open(path, "wb").write(out)
    c, l = counts(out)
    print(f"{path}: CRLF={c} LF={l}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
