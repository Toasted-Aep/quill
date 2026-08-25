"""Add forward references so 15.5a/15.5b do not read as still-open questions.

Works LINE BY LINE. Anchors that span a wrap are a trap in a CRLF file: the
same sentence matches on screen and not in the bytes.
"""
P = ("C:/Users/irony/Downloads/Quill Gem - Fable/quill-sweep2/"
     "docs/CONCEPTS-REF-2026-08-07.md")
raw = open(P, "rb").read()
assert raw.count(b"\n") == raw.count(b"\r\n"), "not pure CRLF"
lines = raw.decode("utf-8").split("\r\n")

def after(anchor_line, block):
    """Insert `block` (list of lines) after the unique line == anchor_line."""
    hits = [i for i, l in enumerate(lines) if l == anchor_line]
    assert len(hits) == 1, "anchor %r found %d times" % (anchor_line[:50], len(hits))
    i = hits[0]
    lines[i + 1:i + 1] = block

def replace_line(old, new):
    hits = [i for i, l in enumerate(lines) if l == old]
    assert len(hits) == 1, "line %r found %d times" % (old[:50], len(hits))
    lines[hits[0]] = new

after("plausible-looking decimal.**", [
    "",
    "**SUPERSEDED 2026-08-25 \u2014 see \u00a715.5c. All thirteen are now measured**, at a",
    "10% viewport calibrated against these six rows, with a worst leave-one-out",
    "control residual of 0.0084 of the frame. The reasoning above stays as the",
    "record of why they were blanked: it was a correct statement about those",
    "frames, in which the fans span 50\u00b0\u201380\u00b0 and nothing converges on screen. It",
    "was never a statement about the presets.",
])

replace_line(
    "other measurement back into frame fractions. That calibration is what makes a",
    "other measurement back into frame fractions. **This was done \u2014 \u00a715.5c.**",
)

after("**`Below` is not the horizon axis; it is one of several things that move the", [])
replace_line(
    "horizon.** Nor are the pairs symmetric about the frame centre: `2 Point` and",
    "horizon.** (Sharpened in \u00a715.5d.3: `Below` ITSELF is a pure vertical"
    " translation \u2014 it",
)
after("horizon.** (Sharpened in \u00a715.5d.3: `Below` ITSELF is a pure vertical"
      " translation \u2014 it", [
    "moves the horizon 1.453 frame heights and leaves both vanishing points where",
    "they were. What \u00a715.5d.4 adds is that a POSITION name can do the same thing.)",
    "Nor are the pairs symmetric about the frame centre: `2 Point` and",
])

replace_line(
    "seen close up with both points and the horizon off-frame entirely. Keeping both",
    "seen close up with both points and the horizon off-frame entirely \u2014 measured in",
)
after("seen close up with both points and the horizon off-frame entirely \u2014 measured in", [
    "\u00a715.5c as `-0.2300 / 2.5563` on a horizon at `-0.7231`, a separation of 2.786",
    "against `2 Point`'s 0.486. Keeping both",
])

open(P, "wb").write("\r\n".join(lines).encode("utf-8"))
chk = open(P, "rb").read()
print("ok: %d CRLF, %d bare LF, %d bytes"
      % (chk.count(b"\r\n"), chk.count(b"\n") - chk.count(b"\r\n"), len(chk)))
