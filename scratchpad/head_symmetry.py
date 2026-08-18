#!/usr/bin/env python3
"""Final verification for 16.6: measure UndoRound AS COMMITTED, from Icons.cs.

This reads the literal out of Icons.cs itself - not out of gen_undo.py - so a
line break that fused two coordinates, or a hand edit that never made it back
into the generator, shows up here.  The fill rule is READ OFF THE LITERAL: a
leading `F1` means nonzero, anything else is XAML's default even-odd.

Everything that can be computed EXACTLY is computed exactly, on the polygon,
because a raster answers a symmetry question to about a pixel and the number
under test is a hundredth of one.  Rasters are still written, at the size the
mark actually draws, because that is the other half of what 16.6 asks for.

  Q1  Does the mark render, and does the fill rule matter?  Winding of every
      subpath, and the area even-odd would XOR away that nonzero keeps.

  Q2  Is the head a mirror about its own shaft?  The axis of a mirror-symmetric
      region passes through its centroid, so the only free parameter is the
      angle; solve for it, report the residual of the reflected outline against
      the original, and compare the angle found to the band's tangent AT THE
      ARC'S END - which is what "its own shaft" means.

  Q3  Do the barbs balance radially?  "The arrow tip is longer on the outer
      side": how far past the band's outer edge the ink reaches, against how
      far inside its inner edge, both exact.

Run:  python scratchpad/head_symmetry.py [--name UndoRound] [--size 21]
"""
from __future__ import annotations

import argparse
import math
import os
import sys

import numpy as np
from PIL import Image, ImageDraw

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from verify_icons import ICONS, literals, tokenise, FILL  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
G = 24.0                                  # the icon grid


# ---------------------------------------------------------------- parsing ---
def parse(name, path=None):
    text = open(path or ICONS, "r", encoding="utf-8").read()
    data = dict((n, v) for n, v, _ in literals(text))[name]
    m = FILL.match(data)
    nonzero = bool(m) and m.group(0).strip() == "F1"
    subs, cur = [], []
    for cmd, args in tokenise(data):
        u = cmd.upper()
        if u == "M":
            if cur:
                subs.append(cur)
            cur = [(args[0], args[1])]
            for i in range(2, len(args), 2):
                cur.append((args[i], args[i + 1]))
        elif u == "L":
            for i in range(0, len(args), 2):
                cur.append((args[i], args[i + 1]))
        elif u == "Z":
            pass
        else:
            raise SystemExit(f"{name}: unhandled command {cmd!r}; this "
                             f"measurement only flattens M/L/Z")
    if cur:
        subs.append(cur)
    return data, nonzero, subs


def signed_area(sub):
    s = 0.0
    for i in range(len(sub)):
        x0, y0 = sub[i]
        x1, y1 = sub[(i + 1) % len(sub)]
        s += x0 * y1 - x1 * y0
    return s / 2.0


def centroid(sub):
    """Exact area centroid of a simple closed polygon."""
    a = cx = cy = 0.0
    for i in range(len(sub)):
        x0, y0 = sub[i]
        x1, y1 = sub[(i + 1) % len(sub)]
        cr = x0 * y1 - x1 * y0
        a += cr
        cx += (x0 + x1) * cr
        cy += (y0 + y1) * cr
    a /= 2.0
    return cx / (6 * a), cy / (6 * a)


def circle_fit(pts):
    n = len(pts)
    sx = sum(p[0] for p in pts); sy = sum(p[1] for p in pts)
    sxx = sum(p[0] * p[0] for p in pts); syy = sum(p[1] * p[1] for p in pts)
    sxy = sum(p[0] * p[1] for p in pts)
    sxxx = sum(p[0] ** 3 for p in pts); syyy = sum(p[1] ** 3 for p in pts)
    sxyy = sum(p[0] * p[1] * p[1] for p in pts)
    sxxy = sum(p[0] * p[0] * p[1] for p in pts)
    a = [[2 * (sxx - sx * sx / n), 2 * (sxy - sx * sy / n)],
         [2 * (sxy - sx * sy / n), 2 * (syy - sy * sy / n)]]
    b = [sxxx + sxyy - sx * (sxx + syy) / n,
         syyy + sxxy - sy * (sxx + syy) / n]
    det = a[0][0] * a[1][1] - a[0][1] * a[1][0]
    cx = (b[0] * a[1][1] - b[1] * a[0][1]) / det
    cy = (a[0][0] * b[1] - a[1][0] * b[0]) / det
    r = math.sqrt(sum((p[0] - cx) ** 2 + (p[1] - cy) ** 2 for p in pts) / n)
    return cx, cy, r


def seg_dist(p, a, b):
    ax, ay = a; bx, by = b; px, py = p
    dx, dy = bx - ax, by - ay
    L = dx * dx + dy * dy
    t = 0.0 if L == 0 else max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / L))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def poly_dist(p, sub):
    return min(seg_dist(p, sub[i], sub[(i + 1) % len(sub)])
               for i in range(len(sub)))


def inside(p, sub):
    x, y = p
    c = False
    for i in range(len(sub)):
        x0, y0 = sub[i]
        x1, y1 = sub[(i + 1) % len(sub)]
        if (y0 > y) != (y1 > y):
            xin = x0 + (y - y0) * (x1 - x0) / (y1 - y0)
            if x < xin:
                c = not c
    return c


# ------------------------------------------------------------ rasterising ---
def fill(subs, n, nonzero, mirror=False):
    acc = np.zeros((n, n), dtype=bool)
    for sub in subs:
        img = Image.new("1", (n, n), 0)
        pts = [((G - x if mirror else x) * n / G, y * n / G) for x, y in sub]
        ImageDraw.Draw(img).polygon(pts, fill=1)
        a = np.array(img, dtype=bool)
        acc = (acc | a) if nonzero else (acc ^ a)
    return acc


def grey(mask, px, ss, path, zoom=12):
    a = mask.reshape(px, ss, px, ss).mean(axis=(1, 3))
    arr = (255 - 255 * a).clip(0, 255).astype(np.uint8)
    Image.fromarray(arr, "L").resize((px * zoom, px * zoom), Image.NEAREST).save(path)
    return a


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", default="UndoRound")
    ap.add_argument("--size", type=float, default=21.0)
    ap.add_argument("--file", default=None, help="an alternative Icons.cs")
    ap.add_argument("--tag", default="committed")
    a = ap.parse_args()

    data, nonzero, subs = parse(a.name, a.file)
    print(f"=== {a.name}  (read from {os.path.relpath(a.file or ICONS)}) ===")
    print(f"reassembled {len(data)} chars; fill rule "
          f"{'NONZERO (F1)' if nonzero else 'EVEN-ODD (XAML default)'}")
    print(f"{len(subs)} subpaths, point counts {[len(s) for s in subs]}")

    # ---- Q1  winding and fill rule --------------------------------------
    areas = [signed_area(s) for s in subs]
    for i, A in enumerate(areas):
        print(f"  subpath {i}: signed area {A:+9.4f} -> "
              f"{'CW' if A > 0 else 'CCW'} (y down)")
    same = len({A > 0 for A in areas}) == 1
    print(f"Q1  all subpaths wound the SAME way: {same}"
          f"   ({'nonzero unions them' if same else 'OPPOSITE - nonzero would punch a hole'})")

    N = 3072
    nz = fill(subs, N, True)
    eo = fill(subs, N, False)
    a_nz, a_eo = nz.mean() * G * G, eo.mean() * G * G
    print(f"    ink area nonzero {a_nz:8.4f} grid^2, even-odd {a_eo:8.4f} grid^2")
    print(f"    even-odd would XOR away {a_nz - a_eo:.4f} grid^2 "
          f"({100 * (a_nz - a_eo) / a_nz:.2f}%) -> F1 is "
          f"{'LOAD-BEARING' if a_nz - a_eo > 0.05 else 'cosmetic'}")
    if a_nz < 1e-6:
        raise SystemExit("BLANK - the literal filled nothing.")

    # ---- band: fit outer and inner edges separately ----------------------
    # The ROUND CAPS at the two ends sweep through every radius between the
    # edges, so a split at the mid radius files half of each cap with the outer
    # edge and half with the inner.  That drags both fits toward the middle
    # (width came out 1.068 against the authored 1.12) and, worse, puts the
    # apparent end of the arc a cap-width round the corner - atan(0.56/8.15) =
    # 3.9 deg, which is exactly the "off the shaft" figure it then produced.
    # So select the two edges by PROXIMITY TO THEIR OWN RADIUS, not by side.
    band = subs[0]
    cx, cy, _ = circle_fit(band)
    tol = 0.03
    for _ in range(12):
        rs = [math.hypot(p[0] - cx, p[1] - cy) for p in band]
        hi, lo_ = max(rs), min(rs)
        out = [p for p, r in zip(band, rs) if r > hi - tol]
        inn = [p for p, r in zip(band, rs) if r < lo_ + tol]
        ox, oy, orad = circle_fit(out)
        ix, iy, irad = circle_fit(inn)
        cx, cy = (ox + ix) / 2, (oy + iy) / 2
    R = (orad + irad) / 2
    print(f"\nband: centre ({cx:.4f},{cy:.4f})   inner {irad:.4f}  outer {orad:.4f}"
          f"  mid {R:.4f}  width {orad - irad:.4f}")
    print(f"      edge samples: {len(out)} outer, {len(inn)} inner; "
          f"centre disagreement {math.hypot(ox - ix, oy - iy):.5f}")

    # The arc's ENDS, from the CAPS rather than from extreme bearings.  A round
    # cap is a semicircle of the half-width centred on the centreline's end, and
    # its own tangential extreme sits a further 1.3 deg round the corner at the
    # outer radius, so reading the end off the outermost vertex overshoots.
    # Instead: the cap centre lies ON the mid-radius circle, so sweep its
    # bearing and take the one that makes every cap vertex equidistant.
    hw = (orad - irad) / 2
    rs = [math.hypot(p[0] - cx, p[1] - cy) for p in band]
    kind = ["O" if abs(r - orad) < 0.02 else "I" if abs(r - irad) < 0.02 else "C"
            for r in rs]
    runs, n = [], len(band)
    i = 0
    while i < n:
        if kind[i] == "C":
            j = i
            while j < n and kind[j] == "C":
                j += 1
            runs.append(list(range(i, j)))
            i = j
        else:
            i += 1
    if kind[0] == "C" and kind[-1] == "C" and len(runs) > 1:
        runs[0] = runs[-1] + runs[0]
        runs.pop()
    ends = []
    for run in runs:
        pts = [band[k] for k in run]

        def spread(beta):
            px, py = cx + R * math.cos(beta), cy + R * math.sin(beta)
            d = [math.dist(q, (px, py)) for q in pts]
            mu = sum(d) / len(d)
            return sum((x - mu) ** 2 for x in d) / len(d)

        b0 = math.atan2(sum(p[1] for p in pts) / len(pts) - cy,
                        sum(p[0] for p in pts) / len(pts) - cx)
        lo_b, hi_b = b0 - 0.25, b0 + 0.25
        for _ in range(80):
            m1, m2 = lo_b + (hi_b - lo_b) / 3, hi_b - (hi_b - lo_b) / 3
            if spread(m1) < spread(m2):
                hi_b = m2
            else:
                lo_b = m1
        beta = (lo_b + hi_b) / 2
        px, py = cx + R * math.cos(beta), cy + R * math.sin(beta)
        rad = sum(math.dist(q, (px, py)) for q in pts) / len(pts)
        ends.append((math.degrees(beta) % 360, rad, len(pts)))
    for b, rad, k in sorted(ends):
        print(f"      cap centre at bearing {b:8.3f} deg, cap radius {rad:.4f} "
              f"(half-width {hw:.4f}), from {k} vertices")
    end_a, end_b = ends[0][0], ends[1][0]
    print(f"      -> the band sweeps {(end_a - end_b) % 360:.2f} deg; "
          f"the open gap is {(end_b - end_a) % 360:.2f} deg")

    # ---- Q2  is the head a mirror, and about what? -----------------------
    if len(subs) != 2:
        # Before 16.6 the head was TWO separate barb subpaths, and a mirror
        # test run on one capsule only recovers that capsule's own long
        # axis - it says nothing about the head.  Q3 is the figure that is
        # comparable across the two revisions; do not read Q2 as one.
        print(f"\nQ2  NOT MEANINGFUL HERE: the head is not a single closed"
              f" outline ({len(subs)} subpaths), so what follows measures"
              f" subpath 1 alone.  Q3 is the cross-revision figure.")
    head = subs[1]
    hx, hy = centroid(head)
    print(f"\nQ2  head: {len(head)} points, area {abs(signed_area(head)):.4f}, "
          f"centroid ({hx:.4f},{hy:.4f})")

    def residual(theta):
        """RMS / max distance of the head's vertices, reflected in the line
        through its centroid at `theta`, back to the head's own outline."""
        c, s = math.cos(2 * theta), math.sin(2 * theta)
        ds = []
        for px, py in head:
            dx, dy = px - hx, py - hy
            rx = hx + c * dx + s * dy
            ry = hy + s * dx - c * dy
            ds.append(poly_dist((rx, ry), head))
        return math.sqrt(sum(d * d for d in ds) / len(ds)), max(ds)

    lo, hi = 0.0, math.pi
    best = min(((residual(lo + (hi - lo) * i / 3600)[0], lo + (hi - lo) * i / 3600)
                for i in range(3600)))
    t0 = best[1]
    lo, hi = t0 - math.pi / 3600, t0 + math.pi / 3600
    for _ in range(60):
        m1, m2 = lo + (hi - lo) / 3, hi - (hi - lo) / 3
        if residual(m1)[0] < residual(m2)[0]:
            hi = m2
        else:
            lo = m1
    th = (lo + hi) / 2
    rms, mx = residual(th)
    axis = math.degrees(th) % 180
    print(f"    best mirror axis {axis:.4f} deg through the centroid")
    print(f"    reflected outline vs original:  RMS {rms:.5f}  max {mx:.5f} "
          f"grid units   ({mx * a.size / G:.5f} px at {a.size:g} DIP)")

    # what SHOULD the axis be?  the band's tangent at the arc end nearest the
    # head, which is what "symmetric about its own shaft" names.
    hb = math.degrees(math.atan2(hy - cy, hx - cx)) % 360
    endp = min((end_a, end_b), key=lambda e: min(abs(e - hb), 360 - abs(e - hb)))
    tang = (endp + 90) % 180
    off = min(abs(axis - tang), 180 - abs(axis - tang))
    print(f"    head sits off the arc end at bearing {endp:.3f} deg; "
          f"the shaft (tangent there) is {tang:.4f} deg")
    print(f"    -> the head's mirror axis is {off:.4f} deg off its own shaft")

    # raster cross-check of the same claim, gather-sampled so the reflection
    # leaves no resampling holes
    HN = 2048
    hm = fill([head], HN, True)
    YY, XX = np.mgrid[0:HN, 0:HN]
    gx, gy = hx * HN / G, hy * HN / G
    c, s = math.cos(2 * th), math.sin(2 * th)
    dx, dy = XX - gx, YY - gy
    sx = np.rint(gx + c * dx + s * dy).astype(np.int64)
    sy = np.rint(gy + s * dx - c * dy).astype(np.int64)
    ok = (sx >= 0) & (sx < HN) & (sy >= 0) & (sy < HN)
    ref = np.zeros_like(hm)
    ref[ok] = hm[sy[ok], sx[ok]]            # GATHER, not scatter
    iou = (hm & ref).sum() / (hm | ref).sum()
    print(f"    raster cross-check: mirror self-overlap IoU {iou:.5f} "
          f"({100 * (1 - iou):.3f}% of head ink unmatched)")

    # ---- Q3  radial balance, exact --------------------------------------
    allpts = [p for s in subs for p in s]
    rmax = max(math.hypot(p[0] - cx, p[1] - cy) for p in allpts)
    rmin = min(poly_dist((cx, cy), s) for s in subs)
    if any(inside((cx, cy), s) for s in subs):
        rmin = 0.0
    print(f"\nQ3  whole-mark ink radius {rmin:.4f} .. {rmax:.4f} (exact, on the polygon)")
    out_reach = rmax - orad
    in_reach = irad - rmin
    print(f"    reaches {out_reach:+.4f} PAST the outer edge")
    print(f"    reaches {in_reach:+.4f} INSIDE the inner edge")
    imb = abs(out_reach - in_reach)
    print(f"    IMBALANCE {imb:.4f} grid units = {imb * a.size / G:.4f} px "
          f"at {a.size:g} DIP  ({imb / max(out_reach, in_reach) * 100:.2f}% of a barb)")

    # per-barb along the head's own axis
    ux, uy = math.cos(th), math.sin(th)
    plus = [p for p in head if (p[0] - hx) * -uy + (p[1] - hy) * ux > 0]
    minus = [p for p in head if (p[0] - hx) * -uy + (p[1] - hy) * ux < 0]
    for tag, pts in (("+", plus), ("-", minus)):
        if not pts:
            continue
        rr = [math.hypot(p[0] - cx, p[1] - cy) for p in pts]
        tip = max(pts, key=lambda p: (p[0] - hx) ** 2 + (p[1] - hy) ** 2)
        print(f"    barb {tag}: {len(pts)} pts, tip ({tip[0]:.3f},{tip[1]:.3f}), "
              f"|tip-centroid| {math.dist(tip,(hx,hy)):.4f}, "
              f"radius {min(rr):.3f}..{max(rr):.3f} "
              f"({'OUTER' if max(rr) > R else 'inner'})")

    # ---- rasters at the size it draws -----------------------------------
    px = int(round(a.size))
    grey(fill(subs, px * 16, nonzero), px, 16,
         os.path.join(HERE, f"{a.name}_{a.tag}_{px}dip.png"))
    grey(fill(subs, px * 16, nonzero, mirror=True), px, 16,
         os.path.join(HERE, f"{a.name}_{a.tag}_{px}dip_redo.png"))
    grey(fill(subs, 240 * 4, nonzero), 240, 4,
         os.path.join(HERE, f"{a.name}_{a.tag}_240.png"), zoom=1)
    grey(fill(subs, 240 * 4, False), 240, 4,
         os.path.join(HERE, f"{a.name}_evenodd_240.png"), zoom=1)
    print(f"\nwrote {a.name}_{a.tag}_{px}dip.png (undo), "
          f"{a.name}_{a.tag}_{px}dip_redo.png (ScaleX=-1, as BindTopBar "
          f"and ToolWheel draw it), {a.name}_{a.tag}_240.png, "
          f"{a.name}_evenodd_240.png")


if __name__ == "__main__":
    main()
