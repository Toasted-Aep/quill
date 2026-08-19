#!/usr/bin/env python3
"""17.4: measure the KNOCKOUTS in every mark the radial dial paints.

WHY.  On a dark page the dial's tool and pen marks "render transparent".  The
fills are opaque - PageTheme.OnSurface is #F2F2F2 on a dark ground - so the
transparency is not in the ink.  It is in the geometry: most of these marks are
authored with EVEN-ODD subpaths that punch counters out of themselves (the A's
bowl, the eraser's worn face, Mix's lens, the selection ring's centre).  A hole
shows whatever is BEHIND the mark, and ToolWheel paints the ring's sector fill
as Colors.Transparent on a dark ground - section 7 - so behind the mark is the
raw page, texture, grid and all.

This measures the hole for each mark at the size the dial actually draws it
(MarkBox = 23 DIP), by rastering the same geometry twice: once under the fill
rule the literal declares, once forced to nonzero, which unions every subpath
wound the same way.  The difference IS the see-through area.

    python scratchpad/mark_holes.py

Reuses render_icons.py's parser and scanline filler, so what is measured here is
what XamlReader builds.
"""
from __future__ import annotations

import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from render_icons import ICONS, literals, polygons, raster  # noqa: E402

MARK_BOX = 23          # ToolWheel.MarkBox - a dial sector's mark
SET_BOX = 16.8         # ToolWheel.SetBox  - the three inner-disc glyphs

# ToolWheel's own geometry, so the seat can be checked against the two things it
# has to fit between rather than against prose.
RING_IN = 0.58 * 98            # 56.84
ARC_STROKE = 2.6
ARC_OUT = RING_IN + ARC_STROKE            # the colour arcs' outer edge
MARK_R = RING_IN + ARC_STROKE + 1.2 + MARK_BOX / 2
LABEL_TOP = RING_IN + ARC_STROKE + 1.2 + MARK_BOX + 1.4
SEAT_SIZE = 26                 # ToolWheel.SeatSize

# Everything Icons.Tool() can return, plus the pen-stroke silhouettes
# PenStrokeMark() draws, plus the command marks CmdArt() draws.
TOOLS = ["Pen", "Text", "Select", "Eraser", "FreeSpace", "Fill",
         "Eyedropper", "Ruler", "Mix"]
PENS = ["StrokeRound", "StrokeTaper", "StrokeChisel", "StrokeGrain"]
CMDS = ["UndoRound", "Mouse", "Plus"]
GLYPHS = ["Size", "Opacity"]          # Smoothness is stroked, so it has no fill


def measure(data, size):
    """(ink, silhouette, declared-rule) in grid units squared.

    The silhouette is the UNION of the subpaths, taken by rastering each closed
    subpath on its own and maxing the coverage.  Forcing the whole path to
    nonzero would not do: nonzero unions only subpaths wound the SAME way, and a
    counter wound against its outline - which is how a hole is normally
    authored - is knocked out under nonzero exactly as it is under even-odd.
    That false negative is what a first pass at this measurement produced.
    """
    polys, nonzero = polygons(data)
    n = int(round(size))
    declared = raster(polys, nonzero, n)
    union = None
    for p in polys:
        one = raster([p], True, n)
        union = one if union is None else np.maximum(union, one)
    px = size / 24.0
    solid = 0.0 if union is None else union.sum() / (px * px)
    return declared.sum() / (px * px), solid, nonzero


def main():
    text = open(ICONS, encoding="utf-8").read()
    lit = dict(literals(text))
    rows = []
    for group, names, box in (("tool", TOOLS, MARK_BOX),
                              ("pen", PENS, MARK_BOX),
                              ("cmd", CMDS, MARK_BOX),
                              ("glyph", GLYPHS, SET_BOX)):
        for n in names:
            if n not in lit:
                print(f"  !! {n} not found in Icons.cs")
                continue
            ink, solid, nz = measure(lit[n], box)
            rows.append((group, n, ink, solid, solid - ink, nz))

    print(f"{'group':6} {'mark':13} {'rule':8} {'ink':>8} {'silhouette':>11} "
          f"{'HOLE':>8}  {'hole %':>7}")
    print("-" * 72)
    worst = 0.0
    for group, n, ink, solid, hole, nz in rows:
        pct = 100.0 * hole / solid if solid else 0.0
        worst = max(worst, pct)
        flag = "  <-- see-through" if pct > 0.5 else ""
        print(f"{group:6} {n:13} {'nonzero' if nz else 'even-odd':8} "
              f"{ink:8.2f} {solid:11.2f} {hole:8.2f} {pct:6.1f}%{flag}")
    print("-" * 72)
    print("areas are grid units squared, measured on the raster at the size the")
    print("dial draws each mark (tool/pen/cmd 23 DIP, glyph 16.8 DIP)")
    print(f"worst hole: {worst:.1f}% of the mark's own silhouette")

    # ---- how big a round plate has to be to seat every slot mark ----------
    # 17.4's ground has to cover the mark's INK, and the dial has very little
    # room to grow into: the colour arcs sit at radius 58.1 and the size labels
    # start at 85.05, with the mark's own box spanning 60.6..83.6.
    print()
    print("ink reach from the mark box's centre, in grid units (24 grid, "
          "corner = 16.97):")
    worst_r = 0.0
    for n in TOOLS + PENS + CMDS:
        if n not in lit:
            continue
        polys, nonzero = polygons(lit[n])
        n_px = 24 * 8
        cov = raster(polys, nonzero, n_px)
        ys, xs = np.nonzero(cov > 0.02)
        if len(xs) == 0:
            print(f"  {n:13} (no fill)")
            continue
        gx, gy = (xs + 0.5) * 24 / n_px, (ys + 0.5) * 24 / n_px
        r = np.hypot(gx - 12.0, gy - 12.0).max()
        worst_r = max(worst_r, r)
        print(f"  {n:13} {r:6.2f}   -> plate diameter {2 * r * MARK_BOX / 24:6.2f} DIP")
    print(f"  worst {worst_r:.2f} grid units = "
          f"{2 * worst_r * MARK_BOX / 24:.2f} DIP across")

    # ---- does the seat ToolWheel actually ships cover all of that? --------
    need = 2 * worst_r * MARK_BOX / 24
    print()
    print(f"ToolWheel.SeatSize = {SEAT_SIZE}, mark ink needs {need:.2f}")
    if SEAT_SIZE + 1e-9 < need:
        print(f"  FAIL - a mark overhangs its seat by "
              f"{(need - SEAT_SIZE) / 2:.2f} DIP")
        return 1
    inner, outer = MARK_R - SEAT_SIZE / 2, MARK_R + SEAT_SIZE / 2
    print(f"  seat spans radius {inner:.2f} .. {outer:.2f}")
    print(f"  colour arcs reach {ARC_OUT:.2f}   overlap "
          f"{max(0.0, ARC_OUT - inner):.2f} DIP (seat is underneath)")
    print(f"  label box starts {LABEL_TOP:.2f}   overlap "
          f"{max(0.0, outer - LABEL_TOP):.2f} DIP (seat is underneath)")
    gap = 2 * MARK_R * np.sin(np.radians(22.5)) - SEAT_SIZE
    print(f"  gap to the next slot's seat {gap:.2f} DIP")
    if gap <= 0:
        print("  FAIL - neighbouring seats touch")
        return 1
    print("OK - every mark's ink is seated, and nothing collides.")
    return 0


if __name__ == "__main__":
    sys.exit(main() or 0)
