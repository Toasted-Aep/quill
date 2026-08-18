#!/usr/bin/env python3
"""Regenerate Icons.UndoRound on the 24-unit grid (ref 16.6).

The band is recovered exactly from the shipped literal and is NOT touched:

    band centre (12, 12.3)   mid radius 8.15   half-width 0.56
    swept from -52 deg to 232 deg, i.e. +284 degrees, screen-clockwise

Only the HEAD is re-authored.

------------------------------------------------------------------------
16.6: "the arrow tip is longer on the outer side ... make the head
symmetric about its own shaft."
------------------------------------------------------------------------

There are two symmetries in play and the shipped mark satisfies one at the
cost of the other:

  * SHAPE symmetry - the head is an isoceles chevron, both arms the same
    length at the same angle from the shaft.  The shipped head has this:
    two 4.9-long arms swept back 54 degrees from the tangent.

  * PLACEMENT symmetry - the two tips sit the same distance from the ring.
    The shipped head does NOT have this, and that is what the user sees.
    Because the shaft is an ARC, a chevron pinned at the arc's end throws
    its outer tip further out than its inner tip goes in:

        r_out + r_in - 2R  =  the imbalance, in grid units

    1.233 units for the shipped head, 1.08 px at the 21 DIP the dial draws
    it - exactly "longer on the outer side".

The predecessor's two attempts ("unrolled", "balanced") bought placement
symmetry by BREAKING shape symmetry - they left the arms at 37 and 71
degrees from the shaft - and read worse, because the eye reads a chevron
by its arm angles first.  Both are gone.

THE FIX.  Keep the chevron a perfect isoceles mirror about the shaft's
tangent, and slide it ALONG THE RADIUS until the two tips are equidistant
from the ring's mid circle.  Both symmetries at once.  Solve

    f(Rv) = sqrt((Rv+h)^2 + s^2) + sqrt((Rv-h)^2 + s^2) = 2R

for the vertex radius Rv, where h = L sin(phi) and s = L cos(phi).  f is
monotonic, so a bisection nails it; the vertex ends up a fraction of a
unit inside the band's centreline, well inside the round cap's own reach,
so the join stays solid.

The head is also SMALLER than the shipped one.  Arms of 4.9 against a ring
of radius 8.15 made a head 60% of the ring's own size, and at 108 degrees
included it splayed across the disc and read as a tick rather than an
arrow.  See --sweep for the comparison strip that settled the numbers.

TWO DEFECTS FIXED IN THE OUTPUT ITSELF, both of which shipped once:

  * WINDING.  The literal is now F1 (nonzero), and the band and the head
    are forced to the same winding.  Wound opposite under nonzero their
    overlap sums to zero and punches a hole through the join.  Under the
    old even-odd default the two arm capsules overlapped at the vertex and
    XOR'd a white notch out of it - visible in the 240 px render.
  * LINE WRAP.  Breaks land only between WHOLE COMMANDS and every chunk
    keeps its trailing space, so no join can weld an x to a y.  A break
    inside `L20.71 12.12` fuses to `L20.7112.12`, still compiles, renders
    BLANK.  Run scratchpad/verify_icons.py after any edit.

Usage:
    python scratchpad/gen_undo.py                # print the C# literal
    python scratchpad/gen_undo.py --report       # the symmetry numbers
    python scratchpad/gen_undo.py --sweep        # candidate strip at 21 DIP
"""
from __future__ import annotations

import argparse
import math

# ---- the band, recovered from the shipped literal -----------------------
CX, CY = 12.0, 12.3
RMID = 8.15
W = 0.56                     # half-width of every stroke in the mark
A0, A1 = -52.0, 232.0        # tail -> head, increasing = screen-clockwise

# ---- the head, re-authored (16.6) ---------------------------------------
# Settled off the 21 DIP strip (scratchpad/sweep_undo.py), not off a 240 px
# preview.  Two things the strip decides that arithmetic cannot:
#
#   * NARROWER IS NOT BETTER.  A 68-degree included head reads beautifully
#     at 120 px and turns into an unreadable blob at 21, where each arm is
#     three pixels and the two of them merge.  Anything under ~95 degrees
#     included loses the head entirely at the size the dial draws it.
#   * SHORTER IS NOT BETTER EITHER.  Below ~4 units the head stops being
#     visible against a band that is one pixel wide.
#
# 4.4 at 52 degrees (104 included) is therefore deliberately close to the
# shipped 4.9 at 54: the defect 16.6 reports is the PLACEMENT, not the
# character of the mark, and the smallest change that fixes the placement
# is the right one.  It also costs the least vertex pull-in, 0.570 units,
# so the shaft's own cap sits 0.351 units proud of the outer arm - 0.31 px
# at 21 DIP, which is the shaft passing under the barb and reads as such.
ARM = 4.4                    # arm centreline length
SWEEP = 52.0                 # arm sweep back from the shaft, degrees

STEP = 3.0                   # arc flattening step, degrees
CAPN = 10                    # segments per round cap


def P(th_deg: float, r: float):
    t = math.radians(th_deg)
    return (CX + r * math.cos(t), CY + r * math.sin(t))


def cap(centre, from_pt, to_pt, n=CAPN):
    """Sample a round cap from from_pt to to_pt about centre, the short way."""
    cx, cy = centre
    a0 = math.atan2(from_pt[1] - cy, from_pt[0] - cx)
    a1 = math.atan2(to_pt[1] - cy, to_pt[0] - cx)
    d = (a1 - a0 + math.pi) % (2 * math.pi) - math.pi
    r = math.hypot(from_pt[0] - cx, from_pt[1] - cy)
    return [(cx + r * math.cos(a0 + d * i / n), cy + r * math.sin(a0 + d * i / n))
            for i in range(1, n)]


def band():
    """The 284 degree stroke, as one closed outline with round caps."""
    n = max(2, int(round(abs(A1 - A0) / STEP)))
    ths = [A0 + (A1 - A0) * i / n for i in range(n + 1)]
    outer = [P(t, RMID + W) for t in ths]
    inner = [P(t, RMID - W) for t in ths]
    head_c, tail_c = P(A1, RMID), P(A0, RMID)
    pts = outer[:]                                  # tail -> head, outer edge
    pts += cap(head_c, outer[-1], inner[-1])        # round the head end
    pts += inner[::-1]                              # head -> tail, inner edge
    pts += cap(tail_c, inner[0], outer[0])          # round the tail end
    return pts


def vertex_radius(arm: float = ARM, sweep: float = SWEEP) -> float:
    """The radius at which an isoceles chevron's two tips reach equally.

    f(Rv) = |tip_out| + |tip_in| is strictly increasing in Rv, so bisect
    it against 2*RMID.  At Rv = RMID the surplus IS the shipped defect.
    """
    h = arm * math.sin(math.radians(sweep))
    s = arm * math.cos(math.radians(sweep))

    def f(rv):
        return (math.hypot(rv + h, s) + math.hypot(rv - h, s)) - 2 * RMID

    lo, hi = h + 1e-6, RMID
    if f(lo) > 0:                       # cannot be balanced; keep it on-shaft
        return RMID
    for _ in range(200):
        mid = (lo + hi) / 2
        if f(mid) < 0:
            lo = mid
        else:
            hi = mid
    return (lo + hi) / 2


def head_points(arm: float = ARM, sweep: float = SWEEP, on_shaft: bool = False):
    """(vertex, outer tip, inner tip) for the chevron, in grid coordinates.

    Exact mirror about the shaft's tangent at A1: both arms `arm` long,
    both swept back `sweep` degrees.  `on_shaft` pins the vertex to the
    band's centreline instead of balancing it - that is the shipped
    construction, kept only so --report can quote it.
    """
    rv = RMID if on_shaft else vertex_radius(arm, sweep)
    v = P(A1, rv)
    # Unit radial and unit tangent (direction of travel, theta increasing).
    t = math.radians(A1)
    ur = (math.cos(t), math.sin(t))
    ut = (-math.sin(t), math.cos(t))
    back = (-ut[0], -ut[1])             # back along the shaft
    c, s = math.cos(math.radians(sweep)), math.sin(math.radians(sweep))

    def tip(sign):
        dx = back[0] * c + ur[0] * s * sign
        dy = back[1] * c + ur[1] * s * sign
        return (v[0] + arm * dx, v[1] + arm * dy)

    return v, tip(+1), tip(-1)


def chevron(arm: float = ARM, sweep: float = SWEEP, on_shaft: bool = False):
    """The head as ONE closed outline: outer tip -> vertex -> inner tip, with
    round caps at the tips and a round join at the vertex.

    One subpath, not two overlapping capsules.  Two capsules meeting at the
    vertex overlap, and under even-odd that overlap XOR'd a white notch out
    of the join; under nonzero it is fine but the notch risk returns the
    moment the fill rule is touched.  A single outline cannot do that to
    itself under either rule."""
    v, tip_o, tip_i = head_points(arm, sweep, on_shaft)

    def unit(a, b):
        dx, dy = b[0] - a[0], b[1] - a[1]
        L = math.hypot(dx, dy)
        return dx / L, dy / L

    uo, ui = unit(v, tip_o), unit(v, tip_i)
    no, ni = (-uo[1], uo[0]), (-ui[1], ui[0])       # left-hand normals

    def off(p, n, s):
        return (p[0] + n[0] * W * s, p[1] + n[1] * W * s)

    pts = [off(v, no, +1), off(tip_o, no, +1)]
    pts += cap(tip_o, pts[-1], off(tip_o, no, -1))
    pts += [off(tip_o, no, -1), off(v, no, -1)]
    # round the vertex from the outer arm's -normal to the inner arm's +normal
    pts += cap(v, pts[-1], off(v, ni, +1))
    pts += [off(v, ni, +1), off(tip_i, ni, +1)]
    pts += cap(tip_i, pts[-1], off(tip_i, ni, -1))
    pts += [off(tip_i, ni, -1), off(v, ni, -1)]
    pts += cap(v, pts[-1], pts[0])
    return pts


def ccw(pts):
    """Force one winding on every subpath.

    The mark is filled NONZERO (the `F1` below), so the band and the head
    must wind the SAME way.  Wound opposite, their overlap sums to zero and
    nonzero punches a hole through exactly the join this rewrite exists to
    make solid."""
    s = 0.0
    for i in range(len(pts)):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % len(pts)]
        s += x1 * y2 - x2 * y1
    return pts if s > 0 else pts[::-1]


def commands(pts):
    """The path as a list of WHOLE commands - 'M17.36 5.44', 'L17.71 5.73' ..."""
    def n(x):
        s = f"{x:.2f}".rstrip("0").rstrip(".")
        return "0" if s in ("-0", "", "-") else s
    out = [f"M{n(pts[0][0])} {n(pts[0][1])}"]
    out += [f"L{n(x)} {n(y)}" for x, y in pts[1:]]
    out.append("Z")
    return out


def tokens(arm: float = ARM, sweep: float = SWEEP, on_shaft: bool = False):
    """The whole path as a list of INDIVISIBLE tokens - 'F1', 'M17.36 5.44',
    'L17.71 5.73', 'Z'.  Nothing downstream may split one of these."""
    return (["F1"] + commands(ccw(band()))
            + commands(ccw(chevron(arm, sweep, on_shaft))))


def path_data(arm: float = ARM, sweep: float = SWEEP, on_shaft: bool = False) -> str:
    return " ".join(tokens(arm, sweep, on_shaft))


def literal(width: int = 76) -> str:
    """The path, wrapped into C# chunks that CANNOT fuse.

    Breaks land only between WHOLE COMMANDS and every chunk keeps its
    trailing space, so no join can ever weld an x to a y.  Splitting on
    spaces instead would happily break `L20.71 12.12` in half; that is the
    blank-icon bug the header warns about, and the trailing space is the
    only thing standing between it and shipping."""
    lines, cur = [], ""
    for t in tokens():
        if cur and len(cur) + len(t) + 1 > width:
            lines.append(cur + " ")
            cur = t
        else:
            cur = (cur + " " + t) if cur else t
    if cur:
        lines.append(cur)
    body = " +\n        ".join(f'"{l}"' for l in lines)
    return "        " + body + ";"


def measure(arm: float, sweep: float, on_shaft: bool):
    """(reach past outer edge, reach past inner edge, imbalance)."""
    v, o, i = head_points(arm, sweep, on_shaft)
    ro = math.hypot(o[0] - CX, o[1] - CY)
    ri = math.hypot(i[0] - CX, i[1] - CY)
    return ro - (RMID + W), (RMID - W) - ri, ro + ri - 2 * RMID


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--report", action="store_true")
    ap.add_argument("--sweep", action="store_true")
    ap.add_argument("--arm", type=float, default=ARM)
    ap.add_argument("--angle", type=float, default=SWEEP)
    a = ap.parse_args()

    if a.report:
        rows = [("shipped 4.9/54 on-shaft", 4.9, 54.0, True),
                ("shipped size, balanced", 4.9, 54.0, False),
                (f"CHOSEN {ARM}/{SWEEP:.0f} balanced", ARM, SWEEP, False),
                (f"CHOSEN size, on-shaft", ARM, SWEEP, True)]
        print(f"{'head':<26} {'Rv':>6} {'out':>7} {'in':>7} {'imbal':>7} "
              f"{'px@21':>7}  arms")
        for name, arm, sw, on in rows:
            rv = RMID if on else vertex_radius(arm, sw)
            out, inn, imb = measure(arm, sw, on)
            v, o, i = head_points(arm, sw, on)
            print(f"{name:<26} {rv:6.3f} {out:7.3f} {inn:7.3f} {imb:7.3f} "
                  f"{imb*21/24:7.3f}  {math.dist(v,o):.3f} / {math.dist(v,i):.3f}")
        return

    if a.sweep:
        import sweep_undo          # noqa: F401  (does its own work on import)
        return

    print(literal())


if __name__ == "__main__":
    main()
