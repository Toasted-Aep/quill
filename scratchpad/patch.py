#!/usr/bin/env python3
"""Line-ending-preserving single-occurrence patcher for the Quill tree.

Edit rewrites a whole file with LF endings; most of this repo is CRLF and
ToolWheel.cs is LF, so every patch here goes through this helper instead:
read binary, assert the old text occurs EXACTLY once, splice, write back with
newline='' so whatever the file already used survives untouched.

    python scratchpad/patch.py <file> <old-file> <new-file>

old/new are read as UTF-8 text with their own line endings normalised to the
target file's.
"""
import sys


def dominant_newline(data: bytes) -> str:
    crlf = data.count(b"\r\n")
    lf = data.count(b"\n") - crlf
    return "\r\n" if crlf >= lf else "\n"


def main() -> int:
    target, oldf, newf = sys.argv[1], sys.argv[2], sys.argv[3]
    raw = open(target, "rb").read()
    nl = dominant_newline(raw)
    text = raw.decode("utf-8")
    old = open(oldf, "r", encoding="utf-8", newline="").read().replace("\r\n", "\n")
    new = open(newf, "r", encoding="utf-8", newline="").read().replace("\r\n", "\n")
    if nl == "\r\n":
        old = old.replace("\n", "\r\n")
        new = new.replace("\n", "\r\n")
    n = text.count(old)
    if n != 1:
        print(f"FAIL {target}: old text occurs {n} times, want exactly 1")
        return 1
    open(target, "w", encoding="utf-8", newline="").write(text.replace(old, new))
    print(f"ok {target} ({'CRLF' if nl == chr(13)+chr(10) else 'LF'})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
