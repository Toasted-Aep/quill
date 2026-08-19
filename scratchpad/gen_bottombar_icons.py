#!/usr/bin/env python3
"""Author the marks CONCEPTS-REF 17.9-17.12 needs, on the 24-unit grid.

Nine new literals, each emitted as ONE C# line however wide that makes it:
a path literal split across lines renders BLANK if a break lands between a
coordinate's x and y (the two numbers fuse, and it still compiles). Run
scratchpad/verify_icons.py after inserting, and scratchpad/render_icons.py to
see each mark at the size the bottom menu actually draws it.

Emits scratchpad/bottombar_icons.txt: name -> path data, ready to paste.
"""
import math
import os

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "bottombar_icons.txt")


def n(v):
    """Two decimals, no trailing '.00' noise beyond what the file already uses."""
    return ("%.2f" % v).rstrip("0").rstrip(".") if abs(v - round(v, 2)) < 1e-9 else "%.2f" % v


def poly(pts, close=True):
    s = "M" + " ".join("%s %s" % (n(x), n(y)) for x, y in pts)
    return s + (" Z" if close else "")


def rect(x0, y0, x1, y1):
    """Clockwise in screen coordinates (y down)."""
    return "M%s %s H%s V%s H%s Z" % (n(x0), n(y0), n(x1), n(y1), n(x0))


def circle(cx, cy, r, ccw=False):
    """Two 180-degree arcs. sweep 0 = counter-clockwise on screen, 1 = clockwise."""
    sw = 0 if ccw else 1
    return "M%s %s A%s %s 0 1 %d %s %s A%s %s 0 1 %d %s %s Z" % (
        n(cx - r), n(cy), n(r), n(r), sw, n(cx + r), n(cy),
        n(r), n(r), sw, n(cx - r), n(cy))


def ellipse(cx, cy, rx, ry, ccw=False):
    sw = 0 if ccw else 1
    return "M%s %s A%s %s 0 1 %d %s %s A%s %s 0 1 %d %s %s Z" % (
        n(cx - rx), n(cy), n(rx), n(ry), sw, n(cx + rx), n(cy),
        n(rx), n(ry), sw, n(cx - rx), n(cy))


marks = {}

# --- back: the chevron ChevronDown's twin, turned to point at what it returns to.
marks["ChevronLeft"] = "M15.5 4.5 L8 12 L15.5 19.5"

# --- Item picker: one object, and the cursor that takes it. The square is a
#     RING (outer + reversed inner, even-odd) and the arrow is a third subpath
#     placed clear of it, so no fill rule can eat either.
sq = rect(2.6, 2.6, 13.4, 13.4)
sq_in = "M4.4 4.4 V11.6 H11.6 V4.4 Z"           # reversed: punches the hole
ARROW = [(6, 2.6), (6, 19.8), (10.3, 15.8), (13.1, 21.6), (15.8, 20.3), (13, 14.6), (18.7, 14.3)]
def arrow_at(x, y, s):
    ox, oy = ARROW[0]
    return poly([(x + (px - ox) * s, y + (py - oy) * s) for px, py in ARROW])
marks["ItemPicker"] = sq + " " + sq_in + " " + arrow_at(13.8, 11.5, 0.55)

# --- Lasso: the loop and its dangling tail. STROKED - a lasso is a line, and a
#     line drawn as an outline at 18 DIP closes up into a blob.
#     The tail sweeps out to the LEFT and down rather than hanging straight: a
#     short vertical stub under a circle reads as a balloon, which is what the
#     first cut of this mark drew.
marks["Lasso"] = ("M12 3.4 C17.7 3.4 21.4 6.5 21.4 10.4 C21.4 14.3 17.7 17.4 12 17.4 "
                  "C6.3 17.4 2.6 14.3 2.6 10.4 C2.6 6.5 6.3 3.4 12 3.4 Z "
                  "M8.6 17 C8 18.8 6.6 20.1 4.4 21")

# --- Partial / Complete: the SAME square in both, and a disc that straddles its
#     edge or sits wholly within it. Same square, so the pair reads as one
#     control in two states; each composition centred in its own grid, because
#     Icons.Mark does not stretch and an off-centre mark sits off-centre in its
#     cell. STROKED, so the overlap Partial IS cannot punch a hole in itself.
#     The disc is r 3.6 in a 15.2 square, not r 4.2 in a 13.4: at 14 DIP the
#     tighter pair left 1.5 px between two 2-unit strokes and closed into a blob.
marks["Complete"] = rect(4.4, 4.4, 19.6, 19.6) + " " + circle(12, 12, 3.6)
marks["Partial"] = rect(3, 4.4, 18.2, 19.6) + " " + circle(18.2, 12, 3.6)

# --- Alpha: the checkerboard that stands for transparency everywhere. A ring
#     plus two quadrants; even-odd fills the quadrants inside the ring's hole.
#     Drawn at 16.8 units rather than 18.8: a checkerboard is mostly ink, and at
#     the full grid it read a third heavier than every mark beside it.
marks["Alpha"] = (rect(3.6, 3.6, 20.4, 20.4) + " M5.2 5.2 V18.8 H18.8 V5.2 Z " +
                  rect(5.2, 5.2, 12, 12) + " " + rect(12, 12, 18.8, 18.8))

# --- Pan: one closed 24-point outline - a cross with four heads. Being ONE
#     subpath, no fill rule can break it, which is the same reason Filter is a
#     single polygon.
half, hw, hl, tip = 1.8, 4.0, 5.0, 1.6
c = 12.0
pan = [
    (c, tip), (c + hw, tip + hl), (c + half, tip + hl), (c + half, c - half),
    (c + hl, c - half), (c + hl, c - hw), (24 - tip, c),
    (c + hl, c + hw), (c + hl, c + half), (c + half, c + half),
    (c + half, 24 - tip - hl), (c + hw, 24 - tip - hl), (c, 24 - tip),
    (c - hw, 24 - tip - hl), (c - half, 24 - tip - hl), (c - half, c + half),
    (c - hl, c + half), (c - hl, c + hw), (tip, c),
    (c - hl, c - hw), (c - hl, c - half), (c - half, c - half),
    (c - half, tip + hl), (c - hw, tip + hl),
]
marks["Pan"] = poly(pan)

# --- The layer scope. All = the shipped Layers stack; Active = one plate, the
#     same diamond at the stack's own width, alone.
marks["LayerOne"] = "M12 5.2 22.2 12 12 18.8 1.8 12 Z"

# --- Stretch: two jambs and a double arrow between them - width changing while
#     height does not. F1 (nonzero) because each head OVERLAPS the shaft; all
#     five subpaths are wound clockwise, so nonzero unions them.
stretch = " ".join([
    rect(2.6, 4, 4.8, 20),
    rect(19.2, 4, 21.4, 20),
    rect(8.6, 11, 15.4, 13),
    poly([(5.8, 12), (10.2, 8.4), (10.2, 15.6)]),
    poly([(18.2, 12), (13.8, 15.6), (13.8, 8.4)]),
])
# NOT "Stretch": a member of Icons called Stretch hides Microsoft.UI.Xaml.Media.
# Stretch inside the class, and every factory at the foot of that file sets
# Stretch.Uniform or Stretch.None. It compiles as a mark and breaks all three.
marks["ScaleStretch"] = "F1 " + stretch

with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    for k, v in marks.items():
        f.write("%s\t%s\n" % (k, v))
        print("%-12s %s" % (k, v))
