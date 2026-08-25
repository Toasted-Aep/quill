P = ("C:/Users/irony/Downloads/Quill Gem - Fable/quill-sweep2/"
     "docs/CONCEPTS-REF-2026-08-07.md")
raw = open(P, "rb").read()
assert raw.count(b"\n") == raw.count(b"\r\n")
lines = raw.decode("utf-8").split("\r\n")

BAD = "other measurement back into frame fractions. **This was done \u2014 \u00a715.5c.**"
TAIL = "captures are comparable, and an explicitly measured mapping restores exactly that."
i = lines.index(BAD)
lines[i] = "other measurement back into frame fractions. That calibration is what makes a"
j = lines.index(TAIL, i)
lines[j] = TAIL + " **This was done \u2014 \u00a715.5c.**"

open(P, "wb").write("\r\n".join(lines).encode("utf-8"))
print("fixed")
for l in lines[i - 1:j + 2]:
    print("   " + l)
