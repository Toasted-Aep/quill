#!/usr/bin/env python3
"""Measure UndoRound's chevron head against its own shaft (ref 16.6).

The mark is three subpaths: [0] the 270-degree ring band, [1] and [2] the two
barbs of the open chevron.  Each is a flattened capsule outline - a centreline
offset by a half-width, with sampled round caps.  Recovering the centreline is
therefore just pairing each point on one side with its opposite number.

What 16.6 asks is whether the two barbs are symmetric ABOUT THE SHAFT, i.e.
whether they make equal angles with the band's tangent at the head, and are of
equal length.
"""
from __future__ import annotations

import math
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from verify_icons import ICONS, literals, tokenise  # noqa: E402


def subpaths(d: str):
    subs, cur = [], []
    for cmd, args in tokenise(d):
        if cmd.upper() == "M":
            if cur:
                subs.append(cur)
            cur = [(args[0], args[1])]
            for i in range(2, len(args), 2):
                cur.append((args[i], args[i + 1]))
        elif cmd.upper() == "L":
            for i in range(0, len(args), 2):
                cur.append((args[i], args[i + 1]))
        elif cmd.upper() == "Z":
            pass
    if cur:
        subs.append(cur)
    return subs


def circle_fit(pts):
    """Least-squares circle through pts -> (cx, cy, r)."""
    n = len(pts)
    sx = sum(p[0] for p in pts); sy = sum(p[1] for p in pts)
    sxx = sum(p[0] * p[0] for p in pts); syy = sum(p[1] * p[1] for p in pts)
    sxy = sum(p[0] * p[1] for p in pts)
    sxxx = sum(p[0] ** 3 for p in pts); syyy = sum(p[1] ** 3 for p in pts)
    sxyy = sum(p[0] * p[1] * p[1] for p in pts); sxxy = sum(p[0] * p[0] * p[1] for p in pts)
    a = [[2 * (sxx - sx * sx / n), 2 * (sxy - sx * sy / n)],
         [2 * (sxy - sx * sy / n), 2 * (syy - sy * sy / n)]]
    b = [sxxx + sxyy - sx * (sxx + syy) / n, syyy + sxxy - sy * (sxx + syy) / n]
    det = a[0][0] * a[1][1] - a[0][1] * a[1][0]
    cx = (b[0] * a[1][1] - b[1] * a[0][1]) / det
    cy = (a[0][0] * b[1] - a[1][0] * b[0]) / det
    r = math.sqrt(sum((p[0] - cx) ** 2 + (p[1] - cy) ** 2 for p in pts) / n)
    return cx, cy, r


def main():
    text = open(ICONS, "r", encoding="utf-8").read()
    data = dict((n, v) for n, v, _ in literals(text))["UndoRound"]
    subs = subpaths(data)
    print(f"UndoRound: {len(subs)} subpaths, sizes {[len(s) for s in subs]}")

    # --- the band -------------------------------------------------------
    band = subs[0]
    # Take only points far from the caps: fit on everything, the caps are few.
    cx, cy, _ = circle_fit(band)
    radii = sorted(math.hypot(p[0] - cx, p[1] - cy) for p in band)
    outer = sum(radii[-60:]) / 60
    inner = sum(radii[:60]) / 60
    print(f"band centre ({cx:.3f}, {cy:.3f})  outer r={outer:.3f}  inner r={inner:.3f}"
          f"  mid r={(outer+inner)/2:.3f}  width={outer-inner:.3f}")

    # --- the barbs ------------------------------------------------------
    # Each barb subpath is: side A (2 pts), cap (n pts), side B (2 pts), cap...
    # The centreline endpoints are the two cap centres.  Recover them as the
    # mean of the cap arcs.
    def capsule(sub):
        """Return (end0, end1, halfwidth) for a flattened capsule outline."""
        # The two straight sides are the two longest consecutive segments.
        segs = [(math.dist(sub[i], sub[(i + 1) % len(sub)]), i) for i in range(len(sub))]
        segs.sort(reverse=True)
        (l0, i0), (l1, i1) = segs[0], segs[1]
        a0, a1 = sub[i0], sub[(i0 + 1) % len(sub)]
        b0, b1 = sub[i1], sub[(i1 + 1) % len(sub)]
        # a runs one way, b the other; pair a0<->b1 and a1<->b0.
        e0 = ((a0[0] + b1[0]) / 2, (a0[1] + b1[1]) / 2)
        e1 = ((a1[0] + b0[0]) / 2, (a1[1] + b0[1]) / 2)
        hw = (math.dist(a0, b1) + math.dist(a1, b0)) / 4
        return e0, e1, hw, (l0 + l1) / 2

    # 16.6 fused the two barbs into ONE closed outline, so this pairwise
    # analysis no longer applies to the committed mark; head_symmetry.py
    # measures that one directly.  Say so rather than unpacking a short list.
    if len(subs) != 3:
        print(f"\nThis script wants 3 subpaths (band + 2 barbs); this literal "
              f"has {len(subs)}.\nSince 16.6 the head is one closed outline - "
              f"use scratchpad/head_symmetry.py.")
        return

    caps = [capsule(s) for s in subs[1:]]
    for i, (e0, e1, hw, ln) in enumerate(caps):
        print(f"barb {i}: ({e0[0]:.3f},{e0[1]:.3f}) -> ({e1[0]:.3f},{e1[1]:.3f})"
              f"  halfwidth={hw:.3f}  side length={ln:.3f}")

    # The shared vertex: whichever pair of endpoints coincide.
    (a0, a1, ahw, _), (b0, b1, bhw, _) = caps
    best = None
    for pa in (a0, a1):
        for pb in (b0, b1):
            d = math.dist(pa, pb)
            if best is None or d < best[0]:
                best = (d, pa, pb)
    d, pa, pb = best
    V = ((pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2)
    tipA = a1 if pa == a0 else a0
    tipB = b1 if pb == b0 else b0
    print(f"\nshared vertex ({V[0]:.3f}, {V[1]:.3f})  mismatch {d:.4f}")

    lenA = math.dist(V, tipA)
    lenB = math.dist(V, tipB)
    print(f"barb A: tip ({tipA[0]:.3f},{tipA[1]:.3f})  centreline len {lenA:.3f}"
          f"  + cap {ahw:.3f} = INK {lenA + ahw:.3f}")
    print(f"barb B: tip ({tipB[0]:.3f},{tipB[1]:.3f})  centreline len {lenB:.3f}"
          f"  + cap {bhw:.3f} = INK {lenB + bhw:.3f}")

    # --- the shaft ------------------------------------------------------
    # The band's tangent AT THE VERTEX.  V sits at the band's mid radius, so the
    # tangent is perpendicular to (V - centre).
    rad = math.atan2(V[1] - cy, V[0] - cx)
    print(f"vertex bearing from band centre: {math.degrees(rad):.2f} deg,"
          f"  |V-C| = {math.hypot(V[0]-cx, V[1]-cy):.3f}")
    # Two candidate tangent directions; the shaft points back ALONG the band,
    # i.e. the one whose dot with (band interior direction) is positive.
    for sgn in (+1, -1):
        tx, ty = -math.sin(rad) * sgn, math.cos(rad) * sgn
        angA = math.degrees(math.atan2(
            (tipA[0] - V[0]) * ty - (tipA[1] - V[1]) * tx,
            (tipA[0] - V[0]) * tx + (tipA[1] - V[1]) * ty))
        angB = math.degrees(math.atan2(
            (tipB[0] - V[0]) * ty - (tipB[1] - V[1]) * tx,
            (tipB[0] - V[0]) * tx + (tipB[1] - V[1]) * ty))
        print(f"  shaft dir ({tx:+.3f},{ty:+.3f}): barb A {angA:+.2f} deg, "
              f"barb B {angB:+.2f} deg   |A|-|B| = {abs(angA)-abs(angB):+.2f}")

    # --- radial reading: which barb is OUTER? ---------------------------
    for nm, tip in (("A", tipA), ("B", tipB)):
        r = math.hypot(tip[0] - cx, tip[1] - cy)
        print(f"barb {nm} tip radius from band centre: {r:.3f}"
              f"  ({'OUTER' if r > (outer+inner)/2 else 'inner'})")


if __name__ == "__main__":
    main()
