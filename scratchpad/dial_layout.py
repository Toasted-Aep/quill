#!/usr/bin/env python3
"""16.5 / 16.4: measure the inner disc's readout boxes against each other.

16.5 is explicit that the constants are not to be trusted - "measure the
result against the arrows' boxes rather than trusting the constants ...
14.1 named that bound correctly and then picked a number that violated
it".  So this reproduces ToolWheel.cs's arithmetic in one place and prints
every clearance the layout has to satisfy.

    python scratchpad/dial_layout.py

Everything is in DIP, in the wheel's own frame: origin at the disc centre,
+x right, +y down.
"""
from __future__ import annotations

import math

# ---- ToolWheel.cs, verbatim ---------------------------------------------
R = 98.0
RingIn = 0.58 * R
DiscR = RingIn                      # 56.84
DotR = 0.195 * R                    # 19.11

SatSize = 21.0
SatX = 0.28 * DiscR
SatY = 0.68 * DiscR

SetBox = 16.8                       # the three property glyphs
ValueSize = 9.0
ValueW = 56.0                       # the value TextBlock's fixed width
ReadoutSize = 10.0

Row1Y = -0.62 * DiscR               # size row, enabled

# 16.5 - stability and opacity move UP and OUTWARD, values following.
ColX = 0.70 * DiscR
ColY = -0.22 * DiscR
ValueX = 0.62 * DiscR
ValueY = 0.11 * DiscR

# 16.4 - a disabled readout centres in its section.
SectionR = (DotR + DiscR) / 2
DisGlyphDy = -7.00
DisValueDy = 9.25

# 14.1's numbers, kept so the collision the user reported stays reproducible
# rather than becoming a story.  16.5 supersedes both.
OldValueX = 0.50 * DiscR
OldValueY = 0.52 * DiscR

# Icons.Mark draws the authored 24 grid scaled by size/24 from the box's
# top-left corner, so grid coordinates map straight onto DIP down the box.
# UndoRound's ink (measured on the committed literal by head_symmetry.py)
# spans grid y 4.70..21.01, so inside a SatSize box the ink starts this far
# below the top edge - the box overstates how close anything gets to the mark.
UNDO_INK_TOP = 4.70 / 24.0 * SatSize        # 4.11 DIP

# A TextBlock's default line box at this size, and the widest string each
# value ever holds.  Segoe UI Variable semibold: digits advance ~0.55 em,
# '%' ~0.85 em.  Rounded UP, so every clearance below is pessimistic.
LINE = ValueSize * 4.0 / 3.0        # 12.0
INK_W = {"100%": 3 * 0.55 * ValueSize + 0.85 * ValueSize,
         "0%": 0.55 * ValueSize + 0.85 * ValueSize,
         "-": 0.4 * ValueSize}


def box(cx, cy, w, h):
    return (cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2)


def value_box(dx, dy, text):
    """PutValue centres a ValueW-wide TextBlock and tops it at dy - 0.65*size,
    so the INK is the centred string inside that box."""
    top = dy - ValueSize * 0.65
    w = INK_W[text]
    return (dx - w / 2, top, dx + w / 2, top + LINE)


def overlap(a, b):
    ox = min(a[2], b[2]) - max(a[0], b[0])
    oy = min(a[3], b[3]) - max(a[1], b[1])
    return (ox, oy) if ox > 0 and oy > 0 else None


def gap(a, b):
    """Separation between two boxes on each axis: positive is clear air,
    negative is how far they overlap on that axis.

    This used to be written inline as `b[1] - a[1] if b[3] < a[1] else
    a[1] - b[3]`, whose two branches are the wrong way round: for a value box
    ABOVE an arrow it returned top-to-top (-27.75) rather than the gap
    (+15.75), and it printed the miss as the clearance.  16.11 records the
    corrected figures; this is the arithmetic behind them.
    """
    return (max(a[0] - b[2], b[0] - a[2]),
            max(a[1] - b[3], b[1] - a[3]))


def corner_r(b):
    return max(math.hypot(x, y) for x in (b[0], b[2]) for y in (b[1], b[3]))


def bearing(x, y):
    """ToolWheel's convention: 0 at twelve o'clock, positive clockwise."""
    return math.degrees(math.atan2(x, -y)) % 360


def show(name, b):
    print(f"  {name:<22} x {b[0]:7.2f}..{b[2]:7.2f}   y {b[1]:7.2f}..{b[3]:7.2f}"
          f"   max r {corner_r(b):6.2f}")


def main():
    undo = box(-SatX, SatY, SatSize, SatSize)
    redo = box(+SatX, SatY, SatSize, SatSize)
    print(f"DiscR {DiscR:.2f}  DotR {DotR:.2f}  SectionR {SectionR:.2f}\n")
    print("undo / redo, the bound everything else is measured against:")
    show("undo", undo)
    show("redo", redo)

    print("\n--- 16.5 ENABLED: up and outward ------------------------------")
    og = box(+ColX, ColY, SetBox, SetBox)
    sg = box(-ColX, ColY, SetBox, SetBox)
    ov = value_box(+ValueX, ValueY, "100%")
    sv = value_box(-ValueX, ValueY, "0%")
    show("opacity glyph", og)
    show("opacity value 100%", ov)
    show("stability glyph", sg)
    show("stability value 0%", sv)
    sizerow = (-30.0, Row1Y - SetBox / 2, 30.0, Row1Y + SetBox / 2)
    show("size row (widest)", sizerow)

    print()
    for nm, b in (("opacity value", ov), ("stability value", sv),
                  ("opacity glyph", og), ("stability glyph", sg)):
        for an, a in (("undo", undo), ("redo", redo)):
            o = overlap(a, b)
            gx, gy = gap(a, b)
            print(f"  {nm:<16} vs {an:<5} "
                  + (f"OVERLAP {o[0]:.2f} x {o[1]:.2f} DIP  <<< FAIL"
                     if o else
                     f"clear: {gy:+.2f} DIP vertically, {gx:+.2f} horizontally"
                     + ("  (they DO overlap in x, so the vertical figure is "
                        "the whole of the clearance)" if gx < 0 else "")))
    print(f"  value ink bottom {ov[3]:+.2f} -> arrow BOX top {undo[1]:+.2f}"
          f"  = {undo[1] - ov[3]:.2f} DIP")
    print(f"  value ink bottom {ov[3]:+.2f} -> arrow INK top "
          f"{undo[1] + UNDO_INK_TOP:+.2f}  = {undo[1] + UNDO_INK_TOP - ov[3]:.2f} DIP")

    print("\n  what 14.1 did, for comparison (ValueX 0.50 r, ValueY 0.52 r):")
    for nm, sgn, txt in (("opacity value", +1, "100%"), ("stability value", -1, "0%")):
        old = value_box(sgn * OldValueX, OldValueY, txt)
        show("  " + nm + " (14.1)", old)
        for an, a in (("undo", undo), ("redo", redo)):
            o = overlap(a, old)
            if o:
                print(f"    vs {an}: OVERLAP {o[0]:.2f} x {o[1]:.2f} DIP"
                      f"  <<< the defect 16.5 was raised for")

    print()
    for nm, b in (("opacity glyph", og), ("opacity value", ov)):
        print(f"  {nm:<16} inside disc by {DiscR - corner_r(b):5.2f} DIP; "
              f"bearings "
              + " ".join(f"{bearing(x, y):.0f}" for x in (b[0], b[2])
                         for y in (b[1], b[3]))
              + "  (opacity section is 45..135)")
    print(f"  glyph vs size row: {og[1] - sizerow[3]:+.2f} DIP vertical, "
          f"{og[0] - sizerow[2]:+.2f} horizontal")
    print(f"  opacity glyph bottom {og[3]:+.2f} -> value ink top {ov[1]:+.2f}"
          f"  gap {ov[1] - og[3]:.2f} DIP")
    print(f"  value ink left {ov[0]:+.2f} vs colour dot edge {DotR:+.2f}"
          f"  gap {ov[0] - DotR:.2f} DIP")

    print("\n--- 16.4 DISABLED: centred in the section ---------------------")
    for nm, cx, cy in (("opacity", +SectionR, 0.0), ("stability", -SectionR, 0.0)):
        g = box(cx, cy + DisGlyphDy, SetBox, SetBox)
        v = value_box(cx, cy + DisValueDy, "-")
        show(nm + " glyph", g)
        show(nm + " value -", v)
        stack = (min(g[0], v[0]), g[1], max(g[2], v[2]), v[3])
        print(f"    stack y {stack[1]:.2f}..{stack[3]:.2f}, centre "
              f"{(stack[1] + stack[3]) / 2:+.2f} against section centre "
              f"{cy:+.2f}  (error {(stack[1] + stack[3]) / 2 - cy:+.2f} DIP)")
        print(f"    inside disc by {DiscR - corner_r(stack):.2f} DIP")
        for an, a in (("undo", undo), ("redo", redo)):
            o = overlap(a, stack)
            print(f"    vs {an}: " + ("OVERLAP <<< FAIL" if o else "clear"))
    sc = -SectionR
    print(f"  size row disabled: centred at y {sc:+.2f} "
          f"(was {Row1Y:+.2f}), glyph box y {sc - SetBox / 2:+.2f}.."
          f"{sc + SetBox / 2:+.2f}, inside disc by "
          f"{DiscR - corner_r((-13.4, sc - SetBox / 2, 13.4, sc + SetBox / 2)):.2f} DIP")


if __name__ == "__main__":
    main()
