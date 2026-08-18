#!/usr/bin/env python3
"""Generates every new mark CONCEPTS-REF 16.2 / 16.9's selection presentation
needs, on the app's 24-unit grid.

    python scratchpad/gen_selection_icons.py

Prints one C# `public const string` per mark, each literal on ONE LINE, ready
to paste into Helpers/Icons.cs.

WHY A GENERATOR AND NOT HAND-EDITING.  Three defects this codebase has already
shipped all come from authoring path data by hand:

  * `PathIcon` FILLS its Data and cannot stroke, so a mark that IS a wire has to
    be emitted as the wire's OUTLINE.  Building that outline by hand is where
    backwards arc-sweep flags come from - one wrong flag and the cap bulges
    inward into a bowtie.  Here the offset curve is SAMPLED, so no `A` command
    survives into the output and there is no flag left to get wrong.
  * A literal split across source lines renders BLANK if the break lands between
    a coordinate's x and its y.  Every literal below is emitted on one line.
  * A mark authored off-centre draws off-centre, because `Icons.Mark` uses
    Stretch.None and honours the authored coordinates exactly.  `fit()` below
    centres every mark on (12, 12) and gives the whole set one optical extent,
    so no mark in the bar is visibly larger than its neighbours.

Run scratchpad/verify_icons.py after pasting, and scratchpad/render_icons.py to
look at the result at the size it actually draws.
"""
from __future__ import annotations

import math

GRID = 24.0
# The extent every mark is fitted to.  19 of 24 leaves ~2.5 units of air on the
# long axis, which is the margin the shipped marks (Settings' gear at 1.69..22.31,
# Layers at 1.8..22.2) already sit inside.
EXTENT = 19.0


# ---------------------------------------------------------------------------
# sampling primitives
# ---------------------------------------------------------------------------

def line(p0, p1, n=2):
    """Samples a straight run, EXCLUDING the start point."""
    return [(p0[0] + (p1[0] - p0[0]) * i / n, p0[1] + (p1[1] - p0[1]) * i / n)
            for i in range(1, n + 1)]


def arc(cx, cy, r, a0, a1, n=24):
    """Samples a circular run in DEGREES, excluding the start point.

    Screen coordinates: y grows downward, so the angle is applied as
    (cx + r cos a, cy + r sin a) and a positive sweep turns clockwise on screen.
    That is the whole convention; there is no sweep flag.
    """
    return [(cx + r * math.cos(math.radians(a0 + (a1 - a0) * i / n)),
             cy + r * math.sin(math.radians(a0 + (a1 - a0) * i / n)))
            for i in range(1, n + 1)]


def dedupe(pts, eps=1e-6):
    out = [pts[0]]
    for p in pts[1:]:
        if abs(p[0] - out[-1][0]) > eps or abs(p[1] - out[-1][1]) > eps:
            out.append(p)
    return out


def normals(pts):
    """Unit left-normal per vertex, averaged across the joint."""
    n = len(pts)
    segn = []
    for i in range(n - 1):
        dx, dy = pts[i + 1][0] - pts[i][0], pts[i + 1][1] - pts[i][1]
        L = math.hypot(dx, dy) or 1.0
        segn.append((-dy / L, dx / L))
    out = []
    for i in range(n):
        a = segn[max(0, i - 1)]
        b = segn[min(i, n - 2)]
        vx, vy = a[0] + b[0], a[1] + b[1]
        L = math.hypot(vx, vy) or 1.0
        # Every joint in these marks is TANGENT - a straight leg meets its turn
        # at the turn's own tangent point - so the averaged normal is the exact
        # one and no miter compensation is needed.
        out.append((vx / L, vy / L))
    return out


def cap(centre, frm, to, n=10):
    """Half-circle from `frm` to `to` around `centre`, excluding the start."""
    a0 = math.atan2(frm[1] - centre[1], frm[0] - centre[0])
    a1 = math.atan2(to[1] - centre[1], to[0] - centre[0])
    while a1 - a0 > 0:
        a1 -= 2 * math.pi
    r = math.hypot(frm[0] - centre[0], frm[1] - centre[1])
    return [(centre[0] + r * math.cos(a0 + (a1 - a0) * i / n),
             centre[1] + r * math.sin(a0 + (a1 - a0) * i / n))
            for i in range(1, n + 1)]


def wire(pts, hw):
    """Closed outline of a round-capped wire of half-width `hw`."""
    pts = dedupe(pts)
    nrm = normals(pts)
    left = [(p[0] + v[0] * hw, p[1] + v[1] * hw) for p, v in zip(pts, nrm)]
    right = [(p[0] - v[0] * hw, p[1] - v[1] * hw) for p, v in zip(pts, nrm)]
    out = left[:]
    out += cap(pts[-1], left[-1], right[-1])
    out += list(reversed(right[:-1]))
    out += cap(pts[0], right[0], left[0])
    return dedupe(out)


def disc(cx, cy, r, n=20):
    return [(cx + r * math.cos(math.radians(a)), cy + r * math.sin(math.radians(a)))
            for a in [i * 360.0 / n for i in range(n)]]


# ---------------------------------------------------------------------------
# fitting and emission
# ---------------------------------------------------------------------------

def bounds(polys):
    xs = [p[0] for poly in polys for p in poly]
    ys = [p[1] for poly in polys for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


def fit(polys, extent=EXTENT):
    """Uniform-scale about the mark's own bounding box and centre it on (12,12).

    `Icons.Mark` draws at the authored coordinates with Stretch.None, so a mark
    that is off-centre on the grid is off-centre in its cell.  This is the one
    place that is guaranteed, for every mark at once.
    """
    x0, y0, x1, y1 = bounds(polys)
    span = max(x1 - x0, y1 - y0)
    k = extent / span if span > 0 else 1.0
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return [[(12 + (p[0] - cx) * k, 12 + (p[1] - cy) * k) for p in poly]
            for poly in polys]


def f(v):
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    return s if s not in ("-0", "") else "0"


def emit(polys, rule=""):
    parts = []
    for poly in polys:
        parts.append("M " + f(poly[0][0]) + " " + f(poly[0][1]))
        for p in poly[1:]:
            parts.append("L " + f(p[0]) + " " + f(p[1]))
        parts.append("Z")
    return (rule + " " if rule else "") + " ".join(parts)


MARKS: list[tuple[str, list, str, str]] = []   # name, polys, fill rule, doc


def mark(name, polys, rule="", doc="", refit=True):
    MARKS.append((name, fit(polys) if refit else polys, rule, doc))


def band(x0, y0, x1, y1, t):
    """A hollow rectangle as outer + inner subpaths (even-odd punches it)."""
    return ([(x0, y0), (x1, y0), (x1, y1), (x0, y1)],
            [(x0 + t, y0 + t), (x1 - t, y0 + t), (x1 - t, y1 - t), (x0 + t, y1 - t)])


def dashes(a, b, across0, across1, count, vertical):
    """`count` evenly spaced dashes along a..b, `across0..across1` wide."""
    out = []
    step = (b - a) / (count * 2 - 1)
    for i in range(count):
        s, e = a + 2 * i * step, a + (2 * i + 1) * step
        out.append([(across0, s), (across1, s), (across1, e), (across0, e)] if vertical
                   else [(s, across0), (s, across1), (e, across1), (e, across0)])
    return out


# ===========================================================================
# THE BAR - paperclip, padlock, duplicate, waste bin | flip H, flip V
# The padlock is NOT generated: Icons.LockClosed already IS that mark, and a
# second padlock is exactly the drift Icons.cs exists to prevent.
# ===========================================================================

# PAPERCLIP.  Three straight legs joined by two half-turns at opposite ends,
# which is what a paperclip IS: one wire folded back on itself twice.
clip = [(5.8, 7.2)]
clip += line((5.8, 7.2), (5.8, 16.0), 4)
clip += arc(8.4, 16.0, 2.6, 180, 0, 20)             # bottom turn, left -> middle
clip += line((11.0, 16.0), (11.0, 6.0), 5)
clip += arc(13.9, 6.0, 2.9, 180, 360, 20)           # top turn, middle -> right
clip += line((16.8, 6.0), (16.8, 15.2), 4)
mark("Paperclip", [wire(clip, 1.05)],
     doc="the wire folded back on itself twice, as an OUTLINE")

# DUPLICATE.  A sheet with a second sheet peeking out behind it.  The one behind
# is an L rather than a whole square, so the pair cannot be mistaken for
# Icons.Objects - which is literally two equal hollow squares.
mark("Duplicate", [
    *band(8.4, 8.4, 20.6, 20.6, 1.7),
    [(4.0, 4.0), (16.2, 4.0), (16.2, 5.7), (5.7, 5.7), (5.7, 16.2), (4.0, 16.2)],
], doc="one sheet, and the corner of a second behind it")

# WASTE BIN.  Lid, squared handle, tapered hollow body, two rules inside it.
# The rules sit inside the body's punched hole, so under the even-odd rule they
# are three levels deep - odd - and fill.  Nothing overlaps anything.
mark("WasteBin", [
    [(3.4, 5.4), (20.6, 5.4), (20.6, 7.2), (3.4, 7.2)],
    [(9.2, 2.6), (14.8, 2.6), (14.8, 5.4), (13.1, 5.4), (13.1, 4.4), (10.9, 4.4), (10.9, 5.4), (9.2, 5.4)],
    [(5.4, 8.6), (18.6, 8.6), (17.5, 21.4), (6.5, 21.4)],
    [(7.1, 10.3), (16.9, 10.3), (16.0, 19.7), (8.0, 19.7)],
    [(10.2, 12.0), (11.5, 12.0), (11.5, 18.0), (10.2, 18.0)],
    [(12.5, 12.0), (13.8, 12.0), (13.8, 18.0), (12.5, 18.0)],
], doc="lid, handle, tapered body, two rules")

# FLIP HORIZONTAL.  A dashed MIRROR LINE with a solid wing on one side and a
# hollow wing on the other: the solid is the object, the hollow is its image.
# Same mark rotated a quarter turn for the vertical - the pair must read as one
# control in two axes, so they are generated from the same numbers.
mark("FlipHorizontal", [
    *dashes(2.4, 21.6, 11.3, 12.7, 5, vertical=True),
    [(9.6, 4.6), (9.6, 19.4), (2.4, 12.0)],
    [(14.4, 4.6), (14.4, 19.4), (21.6, 12.0)],
    [(16.1, 8.2), (16.1, 15.8), (19.6, 12.0)],
], doc="a dashed mirror line, the object solid and its image hollow")

mark("FlipVertical", [
    *dashes(2.4, 21.6, 11.3, 12.7, 5, vertical=False),
    [(4.6, 9.6), (19.4, 9.6), (12.0, 2.4)],
    [(4.6, 14.4), (19.4, 14.4), (12.0, 21.6)],
    [(8.2, 16.1), (15.8, 16.1), (12.0, 19.6)],
], doc="the same mark through a quarter turn")


# ===========================================================================
# THE BOTTOM ROW - Rotate, Scale, Filter
# ===========================================================================

# ROTATE.  A 285 degree band with a solid triangular head and a pivot dot.
# Deliberately NOT Icons.UndoRound: that is a THIN round-capped ring with an
# open chevron, and with the dial up the two sit within a few DIP of each other
# on screen.  A filled band with a solid head and a pivot reads as "this object
# turns", not as "step back through history".
CX, CY = 12.0, 12.4
R_OUT, R_IN = 8.6, 6.6
A0, A1 = -50.0, 235.0                                  # clockwise on screen
rot_band = [(CX + R_OUT * math.cos(math.radians(A0)), CY + R_OUT * math.sin(math.radians(A0)))]
rot_band += arc(CX, CY, R_OUT, A0, A1, 60)
rot_band += [(CX + R_IN * math.cos(math.radians(A1)), CY + R_IN * math.sin(math.radians(A1)))]
rot_band += arc(CX, CY, R_IN, A1, A0, 60)
hx, hy = math.cos(math.radians(A0)), math.sin(math.radians(A0))
tx, ty = -hy, hx                                       # tangent = direction of travel
rot_head = [(CX + hx * (R_OUT + 2.4), CY + hy * (R_OUT + 2.4)),
            (CX + hx * (R_IN - 2.4), CY + hy * (R_IN - 2.4)),
            (CX + hx * ((R_OUT + R_IN) / 2) - tx * 5.4,
             CY + hy * ((R_OUT + R_IN) / 2) - ty * 5.4)]
mark("Rotate", [rot_band, rot_head, disc(CX, CY, 1.7)], rule="F1",
     doc="a swept band with a solid head and a pivot")

# SCALE.  A diagonal double arrow - the corner-drag gesture itself.  F1, because
# each head OVERLAPS the shaft and the even-odd rule would punch a hole exactly
# at the two joins; all three subpaths are wound the same way so nonzero unions
# them rather than cancelling.
D = 1.0 / math.sqrt(2)
hwx, hwy = -0.95 * D, 0.95 * D                         # perpendicular to (1,1)
mark("Scale", [
    [(8.4 + hwx, 8.4 + hwy), (20.0 + hwx, 20.0 + hwy), (20.0 - hwx, 20.0 - hwy), (8.4 - hwx, 8.4 - hwy)],
    [(21.8, 21.8), (21.8, 16.4), (16.4, 21.8)],
    [(6.6, 6.6), (6.6, 12.0), (12.0, 6.6)],
], rule="F1", doc="the corner-drag arrow, both heads")

# FILTER.  A funnel: what a filter does to what passes through it.  One closed
# polygon, so no fill rule can break it.
mark("Filter", [
    [(2.6, 3.4), (21.4, 3.4), (13.9, 12.4), (13.9, 20.0), (10.1, 21.8), (10.1, 12.4)],
], doc="a funnel")


# ===========================================================================
if __name__ == "__main__":
    for name, polys, rule, doc in MARKS:
        x0, y0, x1, y1 = bounds(polys)
        print(f"    /// <summary>{doc}. Generated by scratchpad/gen_selection_icons.py;")
        print(f"    /// regenerate rather than hand-edit. Extent x {x0:.2f}..{x1:.2f}, "
              f"y {y0:.2f}..{y1:.2f} on the 24 grid.</summary>")
        print(f"    public const string {name} =")
        print('        "' + emit(polys, rule) + '";')
        print()
