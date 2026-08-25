P = ("C:/Users/irony/Downloads/Quill Gem - Fable/quill-sweep2/"
     "docs/CONCEPTS-REF-2026-08-07.md")
raw = open(P, "rb").read()
assert raw.count(b"\n") == raw.count(b"\r\n")
lines = raw.decode("utf-8").split("\r\n")

anchor = "frames, which bounds any shear below `1/2880`."
i = lines.index(anchor)
lines[i + 1:i + 1] = [
    "",
    "**The zoom is exactly reversible, which is a fourth check on the pure-scale",
    "finding.** After the sweep the viewport was wheeled back \u2014 24 clicks up at the",
    "same `(1440, 900)` \u2014 and the readout returned to `100% 0\u00b0`. Comparing that",
    "frame against the 2026-08-24 100% capture of the preset then showing",
    "(3-Point `3/4 Ultrawide`): outside the radial dial, the top bar and the bottom",
    "mode bar, which held different states between the two runs, **63 pixels of",
    "4.4 million differ**. A scale that composes to the identity over 48 wheel",
    "steps has no hidden translation in it.",
]

open(P, "wb").write("\r\n".join(lines).encode("utf-8"))
print("added after line %d" % (i + 1))
