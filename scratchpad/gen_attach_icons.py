#!/usr/bin/env python3
"""Generates the two WIRE marks for CONCEPTS-REF 16.2's attachment bar.

Both are drawings of a bent wire of constant thickness.  PathIcon FILLS its
Data and cannot stroke, so a wire has to be emitted as the OUTLINE of the wire
- the centreline offset by +/- half the thickness, with round caps - not as a
stroked polyline.  Building that outline by hand is where fused coordinates and
backwards arc-sweep flags come from (see Icons.UndoRound's remarks), so it is
sampled numerically here instead: no `A` commands survive into the output, so
there are no flags left to get wrong.

    python scratchpad/gen_attach_icons.py

Prints the two literals, each on ONE line, ready to paste into Icons.cs.
"""
from __future__ import annotations

import math

GRID = 24.0


def line(p0, p1, n=2):
    """Samples a straight run, excluding the start point."""
    return [(p0[0] + (p1[0] - p0[0]) * i / n, p0[1] + (p1[1] - p0[1]) * i / n)
            for i in range(1, n + 1)]


def arc(cx, cy, r, a0, a1, n=24):
    """Samples a circular run in DEGREES, excluding the start point.

    Screen coordinates: y grows downward, so the angle is applied as
    (cx + r cos a, cy + r sin a) and a positive sweep turns clockwise on
    screen.  That is the whole convention; there is no sweep flag.
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
        # Every joint in these two marks is TANGENT (a straight leg meets its
        # turn at the turn's own tangent point), so the averaged normal is the
        # exact one and no miter compensation is needed. A sampled arc has no
        # corners of its own either.
        out.append((vx / L, vy / L))
    return out


def cap(centre, frm, to, n=10):
    """Half-circle from `frm` to `to` around `centre`, excluding the start."""
    a0 = math.atan2(frm[1] - centre[1], frm[0] - centre[0])
    a1 = math.atan2(to[1] - centre[1], to[0] - centre[0])
    # always take the sweep that passes on the far side of the wire
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


def emit(poly, extra_subpaths=()):
    def f(v):
        s = f"{v:.2f}".rstrip("0").rstrip(".")
        return s if s not in ("-0", "") else "0"
    parts = ["M " + f(poly[0][0]) + " " + f(poly[0][1])]
    for p in poly[1:]:
        parts.append("L " + f(p[0]) + " " + f(p[1]))
    parts.append("Z")
    for sub in extra_subpaths:
        parts.append("M " + f(sub[0][0]) + " " + f(sub[0][1]))
        for p in sub[1:]:
            parts.append("L " + f(p[0]) + " " + f(p[1]))
        parts.append("Z")
    return " ".join(parts)


def bounds(polys):
    xs = [p[0] for poly in polys for p in poly]
    ys = [p[1] for poly in polys for p in poly]
    return min(xs), min(ys), max(xs), max(ys)


# ---------------------------------------------------------------------------
# PAPERCLIP.  Three straight legs joined by two half-turns at opposite ends,
# which is what a paperclip IS: one wire folded back on itself twice.  The
# outer leg is short at the bottom and the inner leg runs the full height, so
# the mark reads as a clip rather than as a hairpin.
# ---------------------------------------------------------------------------
clip_centre = [(5.8, 7.2)]
clip_centre += line((5.8, 7.2), (5.8, 16.0), 4)
clip_centre += arc(8.4, 16.0, 2.6, 180, 0, 20)            # bottom turn, left -> middle
clip_centre += line((11.0, 16.0), (11.0, 6.0), 5)
clip_centre += arc(13.9, 6.0, 2.9, 180, 360, 20)          # top turn, middle -> right
clip_centre += line((16.8, 6.0), (16.8, 15.2), 4)
CLIP = wire(clip_centre, 1.05)

# ---------------------------------------------------------------------------
# ROTATE.  A 285 degree band about the grid centre with a solid triangular
# head, plus the pivot dot.  Deliberately NOT UndoRound: that mark is a THIN
# round-capped ring with an open chevron, and the two sit within a few DIP of
# each other on screen once the dial and this bar are both up.  A filled band
# with a solid head and a pivot reads as "this object turns", not as "step
# back through history".
# ---------------------------------------------------------------------------
CX, CY = 12.0, 12.4
R_OUT, R_IN = 8.6, 6.6
A_START, A_END = -50.0, 235.0                              # clockwise on screen
band = arc(CX, CY, R_OUT, A_START, A_END, 60)
band = [(CX + R_OUT * math.cos(math.radians(A_START)),
         CY + R_OUT * math.sin(math.radians(A_START)))] + band
band += [(CX + R_IN * math.cos(math.radians(A_END)),
          CY + R_IN * math.sin(math.radians(A_END)))]
band += arc(CX, CY, R_IN, A_END, A_START, 60)
# the head: a triangle straddling the band at its start angle
hx, hy = math.cos(math.radians(A_START)), math.sin(math.radians(A_START))
tx, ty = -hy, hx                                           # tangent, direction of travel
head = [(CX + hx * (R_OUT + 2.4), CY + hy * (R_OUT + 2.4)),
        (CX + hx * (R_IN - 2.4), CY + hy * (R_IN - 2.4)),
        (CX + hx * ((R_OUT + R_IN) / 2) - tx * 5.4,
         CY + hy * ((R_OUT + R_IN) / 2) - ty * 5.4)]
pivot = [(CX + 1.7 * math.cos(math.radians(a)), CY + 1.7 * math.sin(math.radians(a)))
         for a in range(0, 360, 18)]

ROTATE = emit(band, [head, pivot])

bx0,by0,bx1,by1 = bounds([CLIP]); print("Paperclip bounds  x %.2f..%.2f  y %.2f..%.2f" % (bx0,bx1,by0,by1))
print('    public const string Paperclip =')
print('        "' + emit(CLIP) + '";')
print()
rx0,ry0,rx1,ry1 = bounds([band, head, pivot]); print("Rotate bounds     x %.2f..%.2f  y %.2f..%.2f" % (rx0,rx1,ry0,ry1))
print('    public const string Rotate =')
print('        "F1 ' + ROTATE + '";')
