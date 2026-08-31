#!/usr/bin/env python3
"""17.17b / 17.20: does the HSL+RGB arc ladder fit the dock the dial is in?

Reproduces, in one place, the arithmetic that decides where the ladder lands:
ToolWheel.AnchorPoint (which of the eight docks puts the dial's centre where)
and ColorWheel.Layout (which turns that centre into arc radii, knob radii and
value-box rectangles).  16.5's rule applies here too - measure the result, do
not trust the constants - so every clearance the ladder has to satisfy is
printed rather than asserted.

    python scratchpad/ladder_fit.py            # both models, all eight docks
    python scratchpad/ladder_fit.py 1920 1080  # at another viewport
    python scratchpad/ladder_fit.py --sweep    # several viewports, summary only

Everything is in DIP, in the wheel's own frame: origin at the window's
top-left, +x right, +y DOWN.  Bearings are radians, 0 at +x, increasing
CLOCKWISE on screen, which is what ColorWheel.At() means by an angle.

THE LADDER IN REFERENCE UNITS.  One unit is `_ui * Elem` DIP.  The radial
budget past the hole splits into ELEMENTS (things that carry information and
whose size is legibility) and GAPS (air):

    element   arc half-width          9      x2 arcs on the HSL face
    element   knob over the arc       4.5
    element   value box              62 x 30
    gap       hole -> inner arc      40
    gap       inner arc -> outer     72
    gap       arc -> value box       11

    extent past the hole  =  u * (42*el + 123*gap)
    box-clears-next-arc   =  gap >= (30/61) * el          [11.15 item 6]

so the ladder is solved for the largest element scale the dock can hold, with
the air squeezed FIRST because air carries nothing.
"""
from __future__ import annotations

import math
import sys

# ---- ToolWheel.cs, verbatim ---------------------------------------------
R = 98.0
RingOut = 1.00 * R
PopOut = 1.19 * R                      # 116.62
Half = PopOut + 16                     # 132.62
RestingRimTop = Half + 10 - RingOut    # 44.62
DialRimTop = 52.0                      # ChromeBars.Metrics
TopInset = max(0.0, DialRimTop - RestingRimTop)
# BottomMenu.Metrics: BottomInset + CellHeight + 10
BottomReserve = 14.0 + (30 + 2 * 5) * 1.40 + 10.0     # 80.0
PAD = 10.0
SCALE = 1.0                            # TouchMode off

DOCKS = ["TopLeft", "TopCentre", "TopRight", "RightCentre",
         "BottomRight", "BottomCentre", "BottomLeft", "LeftCentre"]


def anchor_point(dock: str, w: float, h: float) -> tuple[float, float]:
    half = Half * SCALE
    left, right = half + PAD, w - half - PAD
    top = half + PAD + TopInset
    bottom = h - half - PAD - BottomReserve
    if dock in ("TopLeft", "LeftCentre", "BottomLeft"):
        cx = left
    elif dock in ("TopRight", "RightCentre", "BottomRight"):
        cx = right
    else:
        cx = w / 2
    if dock in ("TopLeft", "TopCentre", "TopRight"):
        cy = top
    elif dock in ("BottomLeft", "BottomCentre", "BottomRight"):
        cy = bottom
    else:
        cy = h / 2
    cx = max(half, min(cx, max(half, w - half)))
    cy = max(half, min(cy, max(half, h - half)))
    return cx, cy


# ---- ColorWheel.cs, verbatim --------------------------------------------
EdgeMargin = 18.0
Tier2OuterRef = 328.0
Tier1InnerRef = 285.0
HubRoom = 140.0
Elem = 0.80 * 0.80                      # TextScale * SurfaceScale = 0.64
ArcRoll = 0.26
SPAN = 0.86                             # half the fan's angular span
SegGap = 0.16
HubClearance = PopOut * SCALE            # what the dial fills of the hole

EL_REF = 42.0                            # element units past the hole
GAP_REF = 123.0                          # gap units past the hole
GAP_MIN = 30.0 / 61.0                    # gap floor per unit of element scale
# The element scale may not take the value type below the floor this class
# already sets for the palette's own code text - Math.Clamp(_band*0.5, 7, 14)
# * Elem - so the floor is 7 reference units of type, not a new number.
# 15 * _ui * Elem * el >= 7 * Elem   ->   el >= 7 / (15 * _ui)
CODE_FLOOR = 7.0
BUBBLE_REF = 15.0


class Ladder:
    """mode: 'before' = as 17.17 shipped it; 'fit' = 17.20's radial shrink;
    'roll' = 17.22, the roll zeroed in the viewport's bottom half."""

    def __init__(self, cx, cy, w, h, mode: str = "before"):
        self.cx, self.cy, self.w, self.h = cx, cy, w, h
        self.mode = mode
        lean = (w * 0.5 - cx, h * 0.5 - cy)
        self.base = 0.0 if lean[0] ** 2 + lean[1] ** 2 < 4 else math.atan2(lean[1], lean[0])
        roll_sign = -1.0 if cx > w * 0.5 else 1.0

        reach = min(min(cx, w - cx), min(cy, h - cy)) - EdgeMargin
        s = min(max(max(max(0.0, reach) / Tier2OuterRef,
                        (HubClearance + HubRoom) / Tier1InnerRef), 0.5), 1.0)
        self.r1in = 285.0 * s
        lo = min(HubClearance, max(0.0, self.r1in - 70.0))
        self.ui = min(max((self.r1in - lo) / 100.0, 0.80), 1.10)
        self.u = self.ui * Elem

        # 17.22. ArcRoll exists to push the ladder's ANTICLOCKWISE end DOWN,
        # off the top chrome bar. In the viewport's bottom half that same push
        # drives the CLOCKWISE end into the bottom edge instead, which is the
        # whole of 17.17b's overrun. Asked on Y exactly as rollSign is asked on
        # X - the only form of the question this class can put.
        self.roll = 0.0 if (mode in ("roll", "both") and cy > h * 0.5) \
            else ArcRoll * roll_sign
        self.top = self.base + self.roll + SPAN
        self.bot = self.base + self.roll - SPAN

        self.el, self.gap = (self.solve() if mode in ("fit", "both") else (1.0, 1.0))

        self.arcw = 9.0 * self.u * self.el
        self.knob = self.arcw + 4.5 * self.u * self.el
        self.pitch = self.arcw * 2.0 + 72.0 * self.u * self.gap
        self.arc0 = self.r1in + 40.0 * self.u * self.gap
        self.boxw = 62.0 * self.u * self.el
        self.boxh = 30.0 * self.u * self.el
        self.tail = self.arcw + 11.0 * self.u * self.gap + self.boxh * 0.5
        self.font = 15.0 * self.u * self.el

    # --- extremes of sin / cos over the bearing range the fan occupies -----
    def _extreme(self, f):
        lo, hi = min(self.bot, self.top), max(self.bot, self.top)
        vals = [f(lo), f(hi)]
        k = math.floor(lo / (math.pi / 2))
        while k * (math.pi / 2) <= hi + 1e-9:
            a = k * (math.pi / 2)
            if lo - 1e-9 <= a <= hi + 1e-9:
                vals.append(f(a))
            k += 1
        return min(vals), max(vals)

    def edges(self):
        """(coef, slack, halfRef) per window edge.  The value box is always the
        binding element - it reaches 39 element units past its arc against the
        knob's 13.5, on every bearing - so only the box is checked."""
        smin, smax = self._extreme(math.sin)
        cmin, cmax = self._extreme(math.cos)
        return [
            (smax, self.h - self.cy, 15.0),   # bottom   half-height
            (-smin, self.cy, 15.0),           # top
            (cmax, self.w - self.cx, 31.0),   # right    half-width
            (-cmin, self.cx, 31.0),           # left
        ]

    def solve(self) -> tuple[float, float]:
        """Largest element scale the dock can hold, air squeezed first."""
        el = gap = 1.0
        if self.fits(el, gap):
            return el, gap
        # Regime A - keep every element at full size, take it out of the air.
        gap = 1.0
        for coef, slack, half in self.edges():
            if coef <= 1e-6:
                continue
            room = (slack - half * self.u - self.r1in * coef) / coef
            gap = min(gap, (room / self.u - EL_REF) / GAP_REF)
        if gap >= GAP_MIN:
            return 1.0, max(gap, GAP_MIN)
        # Regime B - the air is spent; the elements give too, at the gap floor,
        # so every clearance INSIDE the ladder is preserved exactly.
        el = 1.0
        span = EL_REF + GAP_REF * GAP_MIN     # 102.4918 units per unit of el
        for coef, slack, half in self.edges():
            if coef <= 1e-6:
                continue
            denom = self.u * (span * coef + half)
            el = min(el, (slack - self.r1in * coef) / denom)
        el = min(1.0, max(CODE_FLOOR / (BUBBLE_REF * self.ui), el))
        return el, GAP_MIN * el

    def fits(self, el, gap) -> bool:
        rf = self.r1in + self.u * (EL_REF * el + GAP_REF * gap)
        for coef, slack, half in self.edges():
            if coef > 1e-6 and rf * coef + half * self.u * el > slack + 1e-6:
                return False
        return True

    # --- what the ladder actually occupies ---------------------------------
    def outer(self, face):
        return self.arc0 + self.pitch

    def field_r(self, face):
        return self.outer(face) + self.tail

    def overrun(self, face):
        rf = self.field_r(face)
        worst, side = -1e9, "-"
        for (coef, slack, half), name in zip(self.edges(),
                                             ("bottom", "top", "right", "left")):
            v = rf * coef + half * self.u * self.el - slack
            if v > worst:
                worst, side = v, name
        return worst, side

    def travel(self, face):
        span = abs(self.top - self.bot)
        return self.arc0 * span if face == "HSL" \
            else self.outer(face) * (span - SegGap * 2.0) / 3.0

    def shortest(self, face):
        span = abs(self.top - self.bot)
        return self.outer(face) * ((span - SegGap) * 0.5 if face == "HSL"
                                   else (span - SegGap * 2.0) / 3.0)


MODES = (("BEFORE - 17.17 as shipped", "before"),
         ("FIT    - 17.20's radial shrink alone", "fit"),
         ("ROLL   - 17.22's zeroed roll alone", "roll"),
         ("BOTH   - 17.22 shipped: roll zeroed, solve kept as backstop", "both"))


def table(w, h):
    print(f"viewport {w:.0f} x {h:.0f}   dial scale {SCALE}   "
          f"topInset {TopInset:.2f}   bottomReserve {BottomReserve:.1f}")
    hdr = (f"{'dock':<13}{'centre':>17}  {'face':<4}"
           f"{'roll':>7}{'el':>6}{'gap':>6}{'arc0':>9}{'outer':>9}{'boxC':>9}"
           f"{'over':>8} {'edge':<7}{'travel':>9}{'short':>9}")
    for tag, mode in MODES:
        print(f"\n=== {tag} " + "=" * max(0, len(hdr) - len(tag) - 5))
        print(hdr)
        for dock in DOCKS:
            cx, cy = anchor_point(dock, w, h)
            L = Ladder(cx, cy, w, h, mode)
            for face in ("HSL", "RGB"):
                over, side = L.overrun(face)
                print(f"{dock:<13}{cx:8.2f},{cy:7.2f}  {face:<4}"
                      f"{L.roll:7.2f}{L.el:6.3f}{L.gap:6.3f}"
                      f"{L.arc0:9.2f}{L.outer(face):9.2f}"
                      f"{L.field_r(face):9.2f}{over:8.2f} "
                      f"{side if over > 0.005 else '-':<7}"
                      f"{L.travel(face):9.2f}{L.shortest(face):9.2f}")

    print("\n=== does the ROLL fix cost anything? every dock, against BEFORE ===")
    print(f"{'dock':<13}{'over b':>9}{'over roll':>11}{'boxC':>9}{'type':>7}"
          f"{'hueTrav':>9}{'segTrav':>9}  verdict")
    for dock in DOCKS:
        cx, cy = anchor_point(dock, w, h)
        b = Ladder(cx, cy, w, h, "before")
        r = Ladder(cx, cy, w, h, "roll")
        ob, _ = b.overrun("HSL")
        orr, _ = r.overrun("HSL")
        same = abs(r.roll - b.roll) < 1e-9
        if same:
            verdict = "UNTOUCHED - identical geometry"
        elif abs(orr - ob) < 0.005:
            verdict = "rotated, clearance BIT-IDENTICAL"
        else:
            verdict = f"fixed, {ob - orr:+.2f} DIP of overrun removed"
        print(f"{dock:<13}{ob:9.2f}{orr:11.2f}{r.field_r('HSL'):9.2f}"
              f"{r.font:7.2f}{r.travel('HSL'):9.2f}{r.shortest('RGB'):9.2f}  {verdict}")


def sweep():
    """Every dock at every viewport that overruns under ANY model."""
    print(f"{'viewport':>12}{'dock':>14}{'before':>9}{'fit':>9}{'fit el':>8}"
          f"{'fit type':>9}{'roll':>9}{'roll type':>10}{'both':>8}"
          f"{'both el':>9}{'both type':>10}")
    for w, h in [(1024, 700), (1280, 800), (1440, 900), (1600, 900),
                 (1920, 1080), (2560, 1440), (1366, 768), (900, 640),
                 (1920, 1200), (3440, 1440), (2560, 1080), (3840, 1080)]:
        for dock in DOCKS:
            cx, cy = anchor_point(dock, w, h)
            b = Ladder(cx, cy, w, h, "before")
            f = Ladder(cx, cy, w, h, "fit")
            r = Ladder(cx, cy, w, h, "roll")
            t = Ladder(cx, cy, w, h, "both")
            ob = b.overrun("HSL")[0]
            of = f.overrun("HSL")[0]
            orr = r.overrun("HSL")[0]
            ot = t.overrun("HSL")[0]
            if max(ob, of, orr, ot) <= 0.005:
                continue
            print(f"{f'{w}x{h}':>12}{dock:>14}{ob:9.2f}{of:9.2f}{f.el:8.3f}"
                  f"{f.font:9.2f}{orr:9.2f}{r.font:10.2f}{ot:8.2f}"
                  f"{t.el:9.3f}{t.font:10.2f}")
    print("\n(before / fit / roll are the worst overrun in DIP; "
          "negative or absent = clears)")


if __name__ == "__main__":
    if "--sweep" in sys.argv:
        sweep()
    else:
        args = [a for a in sys.argv[1:] if not a.startswith("-")]
        table(float(args[0]) if len(args) > 1 else 1440.0,
              float(args[1]) if len(args) > 1 else 900.0)
