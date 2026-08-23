#!/usr/bin/env python3
"""Generates the Help mark - CONCEPTS-REF section 5's `?` - on the 24-unit grid.

    python scratchpad/gen_help_icon.py

Prints one C# `public const string`, the literal on ONE LINE, ready to paste
into Helpers/Icons.cs.

WHY A GENERATOR.  Same three reasons scratchpad/gen_selection_icons.py gives,
and this mark hits all of them at once:

  * `PathIcon` FILLS its Data and cannot stroke, and the top bar draws its marks
    through `Icons.Filled`.  A question mark IS a stroke, so it has to be
    emitted as that stroke's OUTLINE.  The bowl is a 215 degree turn running
    into a tail, and hand-authoring that outline is exactly where a backwards
    arc-sweep flag comes from.  Here the offset curve is SAMPLED, so no `A`
    command survives into the output and there is no flag left to get wrong.
  * A literal split across source lines renders BLANK if the break lands
    between a coordinate's x and its y.  The literal is emitted on one line.
  * The mark is fitted and centred by the shared `fit()`, so it carries the
    same optical extent as the gear it sits beside.

AND ONE REASON SPECIFIC TO THIS MARK.  The bowl's tail and the dot are two
separate pieces of ink with a GAP between them, and that gap is the only thing
that stops a `?` reading as a `J`.  A gap wide enough on the 24 grid can still
close up under antialiasing at the 16 DIP the top bar actually draws at, which
is the failure 11.23 records for the palette's wells.  So the gap is stated as
a number here, and scratchpad/render_icons.py is what confirms it survives.

Run scratchpad/verify_icons.py after pasting, and
`python scratchpad/render_icons.py Help` to look at it at its real size.
"""
from __future__ import annotations

import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from gen_selection_icons import arc, bounds, dedupe, disc, emit, f, fit, line, wire


def bezier(p0, p1, p2, p3, n=20):
    """Samples a cubic, EXCLUDING the start point - the same convention
    `line` and `arc` use, so runs concatenate without a duplicate vertex."""
    out = []
    for i in range(1, n + 1):
        t = i / n
        u = 1 - t
        out.append((u * u * u * p0[0] + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t * t * t * p3[0],
                    u * u * u * p0[1] + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t * t * t * p3[1]))
    return out


# ---------------------------------------------------------------------------
# THE MARK
#
# A question mark is one wire and one dot.  The wire is a bowl that turns into
# a tail; the dot sits under it with air between them.
#
# BOWL.  Centre (12, 7.6), radius 4.0, swept from 160 to 375 degrees - so it
# starts at the lower LEFT, climbs the left side, crosses the top and comes
# down the right.  215 degrees rather than a full circle is what leaves the
# opening at the bottom left that says "question" rather than "zero".
#
# TAIL.  A cubic from where the bowl stops, bending inward and down onto the
# grid's centre line, ending vertical - so the tail reads as a stem hanging
# under the bowl rather than as the bowl's own overshoot.  The first control
# point continues the bowl's tangent, which is what keeps the join smooth: the
# arc leaves its last vertex heading (-sin, cos) of the final angle.
#
# WEIGHT.  Half-width 1.45 - a 2.9 unit stroke on the 24 grid, which is 1.93 px
# at the 16 DIP `ChromeBars.Metrics.GlyphSize` draws.  The rings already in the
# file run thinner (Zoom 2.0 units, Precision 1.8, History 1.8), and they are
# rings: an outline that encloses an area reads at a weight a bare glyph does
# not.  A `?` at those weights is a hairline at 16 px.
# ---------------------------------------------------------------------------

CX, CY, R = 12.0, 7.6, 4.0
A0, A1 = 160.0, 375.0
HW = 1.45

end = (CX + R * math.cos(math.radians(A1)), CY + R * math.sin(math.radians(A1)))
# The bowl's tangent at A1, in the direction of the sweep.
tan = (-math.sin(math.radians(A1)), math.cos(math.radians(A1)))
LEAD = 3.1

stem_foot = (12.0, 14.4)

path = [(CX + R * math.cos(math.radians(A0)), CY + R * math.sin(math.radians(A0)))]
path += arc(CX, CY, R, A0, A1, 30)
path += bezier(end,
               (end[0] + tan[0] * LEAD, end[1] + tan[1] * LEAD),
               (12.0, 12.3),
               stem_foot,
               22)

# THE DOT and THE GAP.  The stem's round cap reaches stem_foot.y + HW = 15.85;
# the dot's top edge is DOT_Y - DOT_R.  The clear air between them is stated
# rather than derived so it can be read off this file, and `fit()` scales the
# whole mark by k = 19 / span afterwards, which multiplies the gap by the same
# k - reported below so the number that matters is the one AFTER fitting.
DOT_Y, DOT_R = 19.5, 1.8
GAP = (DOT_Y - DOT_R) - (stem_foot[1] + HW)

polys = [wire(path, HW), disc(12.0, DOT_Y, DOT_R, 24)]
fitted = fit(polys)


if __name__ == "__main__":
    x0, y0, x1, y1 = bounds(polys)
    k = 19.0 / max(x1 - x0, y1 - y0)
    fx0, fy0, fx1, fy1 = bounds(fitted)
    print(f"#  authored extent x {x0:.2f}..{x1:.2f}  y {y0:.2f}..{y1:.2f}   fit k = {k:.4f}")
    print(f"#  stem-to-dot gap {GAP:.2f} authored -> {GAP * k:.2f} fitted "
          f"= {GAP * k * 16 / 24:.2f} px at 16 DIP")
    print(f"#  stroke {2 * HW:.2f} authored -> {2 * HW * k:.2f} fitted "
          f"= {2 * HW * k * 16 / 24:.2f} px at 16 DIP")
    print()
    print("    public const string Help =")
    print('        "' + emit(fitted) + '";')
    print()
    print(f"#  fitted extent x {fx0:.2f}..{fx1:.2f}  y {fy0:.2f}..{fy1:.2f}")
    _ = (dedupe, line, f)
