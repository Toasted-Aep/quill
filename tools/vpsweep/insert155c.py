"""Insert 15.5c/15.5d into the reference doc, matching its line endings.

Do NOT trust `grep -c $'\\r$'` to tell you which they are - it reported 0 CRLF
lines on a file that is entirely CRLF. Read the bytes and count.
"""
import io

DOC = ("C:/Users/irony/Downloads/Quill Gem - Fable/quill-sweep2/"
       "docs/CONCEPTS-REF-2026-08-07.md")
NEW = ("C:/Users/irony/AppData/Local/Temp/claude/C--Users-irony/"
       "5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9/scratchpad/sec155c.md")

raw = open(DOC, "rb").read()
crlf = raw.count(b"\r\n")
lf_only = raw.count(b"\n") - crlf
print("doc line endings: %d CRLF, %d bare LF" % (crlf, lf_only))
assert crlf == 0 or lf_only == 0, "doc has MIXED endings - stopping rather than guessing"
eol = "\r\n" if crlf else "\n"

s = raw.decode("utf-8")
assert "15.5c" not in s, "15.5c already present - refusing to insert twice"

new = io.open(NEW, encoding="utf-8", newline="").read()
new = new.replace("\r\n", "\n").replace("\n", eol)      # match the doc exactly

anchor = ("`\u2026/5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9/scratchpad/vpsweep/`." + eol)
n = s.count(anchor)
assert n == 1, "anchor found %d times, need exactly 1" % n

s = s.replace(anchor, anchor + new)
open(DOC, "wb").write(s.encode("utf-8"))

chk = open(DOC, "rb").read()
print("wrote. now %d CRLF, %d bare LF, %d bytes"
      % (chk.count(b"\r\n"), chk.count(b"\n") - chk.count(b"\r\n"), len(chk)))
