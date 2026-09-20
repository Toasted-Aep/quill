"""Append a scratch .md to a docs file, matching the TARGET's line endings.

Line endings flip per file and under a branch checkout, so the target is
MEASURED here, immediately before the write, rather than remembered.
"""
import io
import sys

src, dst = sys.argv[1], sys.argv[2]

raw = io.open(dst, "rb").read()
crlf = raw.count(b"\r\n")
bare = raw.count(b"\n") - crlf
if crlf and bare:
    raise SystemExit("MIXED endings in %s (crlf=%d bare=%d) - refusing" % (dst, crlf, bare))
nl = "\r\n" if crlf else "\n"

add = io.open(src, encoding="utf-8").read()
add = add.replace("\r\n", "\n").replace("\r", "\n")

if not raw.endswith(b"\n"):
    add = "\n" + add

with io.open(dst, "a", encoding="utf-8", newline=nl) as f:
    f.write(add)

after = io.open(dst, "rb").read()
c2 = after.count(b"\r\n")
b2 = after.count(b"\n") - c2
print("%s: %d -> %d bytes   CRLF %d -> %d   bare-LF %d -> %d   (wrote %r)"
      % (dst, len(raw), len(after), crlf, c2, bare, b2, nl))
if b2 and c2:
    raise SystemExit("WRITE INTRODUCED MIXED ENDINGS")
