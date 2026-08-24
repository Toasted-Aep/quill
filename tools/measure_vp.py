#!/usr/bin/env python3
"""
measure_vp.py - measure the vanishing points and horizon of a Concepts
perspective grid from a full-screen PNG capture.

Written for docs/CONCEPTS-REF-2026-08-07.md section 15.5 (steps 5-7 of the
section 15.2 procedure). Nothing here is measured by eye: the whole point is
that a re-measure on a different monitor re-derives the same FRACTIONS from
different pixels.

Why it is built this way
------------------------
* Concepts draws the grid as a fan of ~`Density` lines through each vanishing
  point. The horizon is the line joining the two horizontal vanishing points.
* `Side` presets put a vanishing point OUTSIDE the frame, so a detector that
  only looks for a dot inside the image cannot work. Convergence of the fan is
  the primary measurement; the drawn dot is only a cross-check where visible.
* Chrome (top bar, radial dial, panels) is removed by differencing against a
  `No Grid` baseline captured with identical chrome, rather than by hardcoding
  exclusion rectangles that rot the moment a panel moves.

Method
------
1. mask   = |frame - baseline| above a small threshold, i.e. grid ink only.
2. lines  = Hough transform over the mask, peaks picked with non-max
            suppression.
3. VPs    = pairwise intersections of those lines, clustered; a cluster
            holding many lines is a vanishing point.
4. horizon= the strong near-horizontal Hough line, cross-checked against the
            y of the two vanishing points that lie off to the sides.
5. dots   = small bright blobs in the mask, used only to confirm that a
            convergence really is where Concepts drew its handle.

Usage
-----
    python measure_vp.py --baseline nogrid.png --frame "2 Point.png"
    python measure_vp.py --baseline nogrid.png --dir captures/ --json out.json
"""

import argparse
import json
import math
import os
import sys

import numpy as np
from PIL import Image


# --------------------------------------------------------------------------
# 1. mask
# --------------------------------------------------------------------------

def load_gray(path):
    return np.asarray(Image.open(path).convert("L")).astype(np.int16)


def build_mask(frame_path, baseline_path=None, thresh=6, lo=5, hi=200):
    """Grid ink only.

    With a baseline the mask is everything the grid ADDED to the chrome, which
    is exactly what we want and needs no knowledge of where the panels are.
    Without one, fall back to a brightness band: the grid is drawn at low
    opacity so it is much darker than text and icons but well above the pure
    black page.
    """
    g = load_gray(frame_path)
    if baseline_path and os.path.exists(baseline_path):
        b = load_gray(baseline_path)
        if b.shape != g.shape:
            raise SystemExit("baseline %s is %s, frame %s is %s"
                             % (baseline_path, b.shape, frame_path, g.shape))
        m = (g - b) >= thresh
    else:
        m = (g >= lo) & (g <= hi)
    return m, g.shape


# --------------------------------------------------------------------------
# 2. Hough
# --------------------------------------------------------------------------

def hough(mask, ntheta=1080, rho_step=2.0, max_points=90000, seed=0):
    """Standard (theta, rho) accumulator, chunked over theta to stay in RAM."""
    ys, xs = np.nonzero(mask)
    if len(xs) == 0:
        return None
    if len(xs) > max_points:
        rng = np.random.default_rng(seed)
        idx = rng.choice(len(xs), max_points, replace=False)
        xs, ys = xs[idx], ys[idx]
    xs = xs.astype(np.float32)
    ys = ys.astype(np.float32)

    h, w = mask.shape
    rho_max = math.hypot(w, h)
    nrho = int(2 * rho_max / rho_step) + 1
    acc = np.zeros((ntheta, nrho), dtype=np.int32)

    thetas = np.linspace(0.0, math.pi, ntheta, endpoint=False)
    for i, t in enumerate(thetas):
        r = xs * math.cos(t) + ys * math.sin(t)
        bins = ((r + rho_max) / rho_step).astype(np.int32)
        np.clip(bins, 0, nrho - 1, out=bins)
        acc[i] = np.bincount(bins, minlength=nrho)
    return acc, thetas, rho_max, rho_step


def peak_lines(hough_out, min_votes=120, nms_theta=6, nms_rho=6, max_lines=200):
    """Pick line peaks with a simple non-maximum suppression sweep."""
    acc, thetas, rho_max, rho_step = hough_out
    a = acc.copy()
    lines = []
    while len(lines) < max_lines:
        i, j = np.unravel_index(np.argmax(a), a.shape)
        v = a[i, j]
        if v < min_votes:
            break
        t = thetas[i]
        r = j * rho_step - rho_max
        lines.append({"theta": float(t), "rho": float(r), "votes": int(v)})
        i0, i1 = max(0, i - nms_theta), min(a.shape[0], i + nms_theta + 1)
        j0, j1 = max(0, j - nms_rho), min(a.shape[1], j + nms_rho + 1)
        a[i0:i1, j0:j1] = 0
        # theta wraps at pi with rho negated - suppress the alias too
        if i < nms_theta or i >= a.shape[0] - nms_theta:
            ai = (i + a.shape[0] // 2) % a.shape[0]
            aj = a.shape[1] - 1 - j
            a[max(0, ai - nms_theta):ai + nms_theta + 1,
              max(0, aj - nms_rho):aj + nms_rho + 1] = 0
    return lines


# --------------------------------------------------------------------------
# 3. convergence
# --------------------------------------------------------------------------

def intersect(l1, l2, min_angle_deg=2.0):
    t1, r1 = l1["theta"], l1["rho"]
    t2, r2 = l2["theta"], l2["rho"]
    d = math.sin(t2 - t1)
    if abs(d) < math.sin(math.radians(min_angle_deg)):
        return None
    x = (r1 * math.sin(t2) - r2 * math.sin(t1)) / d
    y = (r2 * math.cos(t1) - r1 * math.cos(t2)) / d
    return x, y


def cluster_vps(lines, shape, tol=14.0, span=3.0, min_lines=5):
    """Cluster pairwise intersections; a vanishing point is where many
    DISTINCT lines meet, not merely where many intersections pile up."""
    h, w = shape
    pts = []
    n = len(lines)
    for i in range(n):
        for j in range(i + 1, n):
            p = intersect(lines[i], lines[j])
            if p is None:
                continue
            x, y = p
            # allow a generous margin outside the frame: Side presets live there
            if -span * w <= x <= (1 + span) * w and -span * h <= y <= (1 + span) * h:
                pts.append((x, y, i, j))
    if not pts:
        return []

    used = np.zeros(len(pts), dtype=bool)
    P = np.array([(p[0], p[1]) for p in pts])
    clusters = []
    order = np.arange(len(pts))
    for k in order:
        if used[k]:
            continue
        d = np.hypot(P[:, 0] - P[k, 0], P[:, 1] - P[k, 1])
        sel = (~used) & (d <= tol)
        if sel.sum() < 3:
            continue
        # refine centre once, then re-select
        cx, cy = P[sel, 0].mean(), P[sel, 1].mean()
        d = np.hypot(P[:, 0] - cx, P[:, 1] - cy)
        sel = (~used) & (d <= tol)
        members = set()
        for idx in np.nonzero(sel)[0]:
            members.add(pts[idx][2])
            members.add(pts[idx][3])
        if len(members) < min_lines:
            continue
        used |= sel
        votes = sum(lines[m]["votes"] for m in members)
        clusters.append({
            "x": float(P[sel, 0].mean()),
            "y": float(P[sel, 1].mean()),
            "nlines": len(members),
            "votes": int(votes),
            "lines": sorted(members),
        })
    clusters.sort(key=lambda c: (-c["nlines"], -c["votes"]))
    # Merge pass. A dense smear (the radial dial, a panel's rounded corners)
    # throws several centres a few px apart; keep the strongest of each group.
    # CORRECTED 2026-08-24: the merge radius was a fixed 4*tol = 56 px, which
    # is far too tight for a vanishing point that sits off the frame. A distant
    # convergence is inherently smeared - small angular error in a spoke throws
    # the intersection a long way - so a single `Side` point arrived as three
    # or four clusters 100-150 px apart and the caller then reported the same
    # point several times over as if they were different vanishing points.
    # Scale the radius with the frame, and let the strongest member set the
    # centre rather than averaging a smear.
    radius = max(4 * tol, 0.04 * w)
    merged = []
    for c in clusters:
        hit = None
        for m in merged:
            if math.hypot(c["x"] - m["x"], c["y"] - m["y"]) <= radius:
                hit = m
                break
        if hit is None:
            c = dict(c)
            c["lines"] = set(c["lines"])
            merged.append(c)
        else:
            # absorb: union the supporting lines, keep the denser centre
            hit["lines"] = set(hit["lines"]) | set(c["lines"])
            hit["votes"] += c["votes"]
            if c["nlines"] > hit["nlines"]:
                hit["x"], hit["y"] = c["x"], c["y"]
            hit["nlines"] = len(hit["lines"])
    for m in merged:
        m["lines"] = sorted(m["lines"])
    merged.sort(key=lambda c: (-c["nlines"], -c["votes"]))
    return merged


# --------------------------------------------------------------------------
# 3b. choosing WHICH convergences are the vanishing points
# --------------------------------------------------------------------------

def select_vps(clusters, shape, horizon_y=None, rel=0.55, overlap_max=0.5,
               level_frac=0.01):
    """Pick the REAL vanishing points out of the convergence clusters.

    A count threshold alone does not work. The clusterer returns dozens of
    convergences with respectable line counts, because a fan's own spokes
    re-intersect each other slightly off the true point: for `2 Point` the
    genuine points carry 107 and 93 lines, but coincidences at 79 and 78 sit
    only ~120 px away, and no threshold separates those from the second real
    point at 93.

    What does separate them is WHICH lines support them. A coincidence is made
    of the same spokes as the vanishing point it shadows, so its supporting set
    is largely contained in one already accepted. A genuinely different
    vanishing point is a different fan and overlaps only in the few lines the
    two families share. So: walk the clusters strongest-first, and reject any
    whose support is more than `overlap_max` contained in one already taken,
    or which simply sits within a frame-scaled radius of one.
    """
    h, w = shape
    radius = max(56.0, 0.05 * w)
    accepted = []
    for c in sorted(clusters, key=lambda c: -c["nlines"]):
        if accepted and c["nlines"] < rel * accepted[0]["nlines"]:
            break
        cl = set(c["lines"])
        if not cl:
            continue
        drop = False
        for a in accepted:
            if math.hypot(c["x"] - a["x"], c["y"] - a["y"]) <= radius:
                drop = True
                break
            if len(cl & set(a["lines"])) / float(len(cl)) > overlap_max:
                drop = True
                break
        if not drop:
            accepted.append(c)

    if not accepted:
        return [], [], None

    hy = horizon_y
    if hy is None:
        # no drawn rule in frame: the horizon is the level shared by the two
        # widest-separated accepted points (every `Below` preset lands here)
        pair, best_dx = None, -1.0
        for i in range(len(accepted)):
            for j in range(i + 1, len(accepted)):
                a, b = accepted[i], accepted[j]
                if abs(a["y"] - b["y"]) > level_frac * h:
                    continue
                dx = abs(a["x"] - b["x"])
                if dx > best_dx:
                    best_dx, pair = dx, (a, b)
        hy = 0.5 * (pair[0]["y"] + pair[1]["y"]) if pair else accepted[0]["y"]

    on = [c for c in accepted if abs(c["y"] - hy) <= level_frac * h]
    off = [c for c in accepted if abs(c["y"] - hy) > level_frac * h]
    on.sort(key=lambda c: c["x"])
    off.sort(key=lambda c: c["y"])
    return on, off, hy


# --------------------------------------------------------------------------
# 3c. vanishing points AS CROSSINGS OF THE HORIZON
# --------------------------------------------------------------------------
#
# ADDED 2026-08-24, and this is now the primary measurement.
#
# Clustering pairwise intersections in 2-D fails badly for a vanishing point
# that sits far outside the frame, which is the whole point of the `Side` and
# `Below` presets. A spoke's angle is only known to the Hough grid's
# resolution, and that angular error is multiplied by the distance to the
# convergence, so a point 1500 px off the left edge arrives as a smear 400 px
# wide. It then splits into several clusters, each too weak to survive a
# threshold, and `1/2 Wide Below` reported one vanishing point where a 2-Point
# grid must have two.
#
# Constraining to the horizon removes the degree of freedom that smears. Every
# spoke of a fan crosses the horizon at the SAME x - the vanishing point - so
# clustering those 1-D crossings is both sharper and exactly what section 15.2
# asks to be reported: x on the horizon, as a fraction of frame width.

def _cross_x(line, y):
    """Where this Hough line crosses the horizontal y = const."""
    t, r = line["theta"], line["rho"]
    c = math.cos(t)
    if abs(c) < 1e-9:
        return None
    return (r - y * math.sin(t)) / c


def crossings(lines, y, min_angle_deg=2.0):
    """Crossing x of every line steep enough to define one. Lines within a
    couple of degrees of the horizon are the horizon itself and its near
    neighbours; their crossing is numerically meaningless."""
    out = []
    for i, l in enumerate(lines):
        dev = abs(math.degrees(l["theta"]) - 90.0)
        if dev <= min_angle_deg:
            continue
        x = _cross_x(l, y)
        if x is None or abs(x) > 60000:
            continue
        out.append((x, l["votes"], i))
    return out


def cluster_crossings(cross, shape, min_lines=6):
    """Agglomerate crossings into fans. The tolerance grows with distance from
    frame centre, because a distant point's crossings stay slightly more
    scattered even after the horizon constraint."""
    h, w = shape
    if not cross:
        return []
    cross = sorted(cross)
    groups = []
    for x, votes, i in cross:
        tol = max(30.0, 0.025 * abs(x - 0.5 * w))
        if groups and (x - groups[-1]["last"]) <= tol:
            g = groups[-1]
            g["xs"].append(x); g["votes"] += votes; g["lines"].add(i); g["last"] = x
        else:
            groups.append({"xs": [x], "votes": votes, "lines": {i}, "last": x})
    out = []
    for g in groups:
        if len(g["lines"]) < min_lines:
            continue
        out.append({"x": float(np.median(g["xs"])), "n": len(g["lines"]),
                    "votes": int(g["votes"])})
    out.sort(key=lambda g: -g["n"])
    return out


def fit_horizon(lines, shape, y0, span=420.0, step=3.0):
    """Find the horizon when it is off-frame and there is no rule to scan.

    The right y is the one at which the fans are SHARPEST - at the true
    horizon every spoke of a fan crosses at one x, and away from it the
    crossings spread out. So sweep y and keep the value whose two best
    crossing-clusters hold the most lines between them.
    """
    best = (None, -1)
    y = y0 - span
    while y <= y0 + span:
        g = cluster_crossings(crossings(lines, y), shape)
        score = sum(c["n"] for c in g[:2])
        if score > best[1]:
            best = (y, score)
        y += step
    return best[0]


# --------------------------------------------------------------------------
# 3d. RANSAC on angular residual - the primary vanishing-point finder
# --------------------------------------------------------------------------
#
# ADDED 2026-08-24, after looking at what the sweep actually captured.
#
# Half the presets do not put anything inside the frame to converge. Every
# `Narrow` preset renders as a ground plane seen close up: two families of
# near-parallel lines, horizon off the top, both vanishing points far outside.
# There are only a dozen or so lines in frame and no rule to scan. Both earlier
# methods fail there - 2-D clustering smears, and crossing-the-horizon needs a
# horizon it does not have.
#
# What survives is the definition itself: a vanishing point is the point that
# the most lines POINT AT. Measuring that by perpendicular distance is wrong,
# because a point 8000 px away is "far" from every line in absolute terms. The
# scale-free residual is the ANGLE a line misses the point by, seen from the
# frame - which is exactly the quantity that stays constant as a point recedes.

def _perp(line, x, y):
    return abs(x * math.cos(line["theta"]) + y * math.sin(line["theta"]) - line["rho"])


def _tolerance(x, y, cx, cy, tol_px, tol_ang_deg):
    """How far a line may miss the point and still count as pointing at it.

    A pure angular tolerance is WRONG for a point inside the frame: the lever
    arm is the distance from the frame centre to the point, so a vanishing
    point AT the centre has lever zero and the angular test then rejects every
    line - which is exactly how the 1-Point fan came back unfound. A pure pixel
    tolerance is wrong the other way: a point 8000 px out cannot be held to six
    pixels when the Hough grid quantises angle at a sixth of a degree. Take
    whichever is larger.
    """
    lever = math.hypot(x - cx, y - cy)
    return max(tol_px, math.tan(math.radians(tol_ang_deg)) * lever)


def _fit_point(lines, idx):
    """Least-squares point closest to a set of lines (perpendicular sense)."""
    A = np.array([[math.cos(lines[k]["theta"]), math.sin(lines[k]["theta"])] for k in idx])
    b = np.array([lines[k]["rho"] for k in idx])
    sol, *_ = np.linalg.lstsq(A, b, rcond=None)
    return float(sol[0]), float(sol[1])


def ransac_vps(lines, shape, tol_px=6.0, tol_ang=0.35, min_inliers=6,
               max_points=4, max_coord=400000.0):
    """Find up to `max_points` vanishing points, strongest first."""
    h, w = shape
    cx, cy = 0.5 * w, 0.5 * h
    remaining = list(range(len(lines)))
    found = []

    def inliers(x, y, pool):
        t = _tolerance(x, y, cx, cy, tol_px, tol_ang)
        return [k for k in pool if _perp(lines[k], x, y) <= t]

    for _ in range(max_points):
        best_inl, best_p = [], None
        n = len(remaining)
        for a in range(n):
            for b in range(a + 1, n):
                p = intersect(lines[remaining[a]], lines[remaining[b]], min_angle_deg=0.7)
                if p is None:
                    continue
                x, y = p
                if abs(x) > max_coord or abs(y) > max_coord:
                    continue
                inl = inliers(x, y, remaining)
                if len(inl) > len(best_inl):
                    best_inl, best_p = inl, (x, y)
        if best_p is None or len(best_inl) < min_inliers:
            break
        # refine, then re-collect, twice - the first fit pulls the point onto
        # the fan and the second picks up spokes the seed pair just missed
        x, y = best_p
        for _ in range(2):
            try:
                x, y = _fit_point(lines, best_inl)
            except Exception:
                break
            got = inliers(x, y, remaining)
            if len(got) < min_inliers:
                break
            best_inl = got
        found.append({"x": x, "y": y, "n": len(best_inl),
                      "votes": int(sum(lines[k]["votes"] for k in best_inl)),
                      "lines": sorted(best_inl)})
        drop = set(best_inl)
        remaining = [k for k in remaining if k not in drop]
        if len(remaining) < min_inliers:
            break

    # Merge fans that came back in two passes. Two points are the same
    # vanishing point when they are closer together than the tolerance that
    # defines "pointing at" them in the first place.
    merged = []
    for p in sorted(found, key=lambda p: -p["n"]):
        hit = None
        for m in merged:
            t = _tolerance(m["x"], m["y"], cx, cy, tol_px, tol_ang)
            if math.hypot(p["x"] - m["x"], p["y"] - m["y"]) <= max(3 * t, 24.0):
                hit = m
                break
        if hit is None:
            merged.append(dict(p))
        else:
            idx = sorted(set(hit["lines"]) | set(p["lines"]))
            try:
                hit["x"], hit["y"] = _fit_point(lines, idx)
            except Exception:
                pass
            hit["lines"], hit["n"] = idx, len(idx)
            hit["votes"] += p["votes"]
    merged.sort(key=lambda p: -p["n"])
    return merged


def horizon_pair(points, shape, max_tilt_deg=3.0):
    """Which of the found points share a horizon.

    Concepts draws every preset's horizon level, so the horizon pair is the
    widest-separated pair whose connecting line is within a few degrees of
    horizontal. A 3-Point grid's zenith is then whatever is left over.
    """
    h, w = shape
    best, best_dx = None, -1.0
    for i in range(len(points)):
        for j in range(i + 1, len(points)):
            a, b = points[i], points[j]
            dx = abs(a["x"] - b["x"])
            if dx < 1e-6:
                continue
            tilt = abs(math.degrees(math.atan2(b["y"] - a["y"], b["x"] - a["x"])))
            tilt = min(tilt, 180.0 - tilt)
            if tilt > max_tilt_deg:
                continue
            if dx > best_dx:
                best_dx, best = dx, (a, b)
    return best


# --------------------------------------------------------------------------
# 4. horizon + drawn dots
# --------------------------------------------------------------------------

def horizon_scan(mask, min_span=0.85, min_fill=0.5):
    """The horizon, per section 15.2 step 5: the ONE full-width horizontal rule.

    CORRECTED 2026-08-24. This used to be horizontal_rule(), which took "the
    strongest near-horizontal Hough line". That is wrong and it produced
    garbage - horizons scattered between 0.07 and 1.20 of frame height across
    the sweep. The reason is structural, not a tuning problem: each fan
    contains many lines within a degree of horizontal, and collectively they
    outvote the single true rule, so the Hough peak lands on a fan spoke.

    A row-scan cannot make that mistake. The horizon is the only row that is
    BOTH densely inked and spans essentially the whole width; a fan spoke at
    even half a degree climbs 25 px across 2880 and so lays only ~115 px into
    any one row. Returns None when the horizon is off-frame - which is exactly
    what every `Below` preset does - and the caller then falls back to the line
    through the vanishing points themselves.
    """
    h, w = mask.shape
    counts = mask.sum(axis=1)
    best = None
    for y in np.nonzero(counts > min_fill * w)[0]:
        xs = np.nonzero(mask[y])[0]
        span = (xs.max() - xs.min() + 1) / float(w)
        if span < min_span:
            continue
        if best is None or counts[y] > best["count"]:
            best = {"y": float(y), "count": int(counts[y]), "span": round(float(span), 4)}
    if best is None:
        return None
    # sub-pixel: the rule is drawn antialiased across 2-3 rows, so take the
    # intensity centroid of the neighbourhood rather than the peak row alone.
    y0 = int(best["y"])
    lo, hi = max(0, y0 - 2), min(h, y0 + 3)
    seg = counts[lo:hi].astype(float)
    if seg.sum() > 0:
        best["y"] = float((np.arange(lo, hi) * seg).sum() / seg.sum())
    return best


def horizontal_rule_hough(lines, shape, tol_deg=1.0):
    """The old Hough-peak horizon, kept ONLY as a cross-check and never as the
    answer. See horizon_scan for why it cannot be trusted on its own."""
    h, w = shape
    best = None
    for l in lines:
        # theta near pi/2 means the normal is vertical, i.e. the line is horizontal
        dev = abs(math.degrees(l["theta"]) - 90.0)
        if dev <= tol_deg:
            y = l["rho"] / math.sin(l["theta"])
            if best is None or l["votes"] > best["votes"]:
                best = {"y": float(y), "votes": l["votes"], "dev_deg": float(dev)}
    return best


def find_dots(mask, shape, min_px=6, max_px=900):
    """Small isolated blobs - Concepts draws a handle dot at each on-screen
    vanishing point. Flood fill with an explicit stack (no scipy here)."""
    h, w = shape
    seen = np.zeros(mask.shape, dtype=bool)
    out = []
    ys, xs = np.nonzero(mask)
    # only bother with blobs, so pre-thin by requiring a filled 3x3 neighbourhood
    m = mask
    dense = (m[1:-1, 1:-1] & m[:-2, 1:-1] & m[2:, 1:-1]
             & m[1:-1, :-2] & m[1:-1, 2:]
             & m[:-2, :-2] & m[2:, 2:] & m[:-2, 2:] & m[2:, :-2])
    dys, dxs = np.nonzero(dense)
    dys += 1
    dxs += 1
    for sy, sx in zip(dys, dxs):
        if seen[sy, sx]:
            continue
        stack = [(sy, sx)]
        seen[sy, sx] = True
        comp = []
        while stack:
            y, x = stack.pop()
            comp.append((y, x))
            if len(comp) > max_px:
                break
            for ny, nx in ((y - 1, x), (y + 1, x), (y, x - 1), (y, x + 1)):
                if 0 <= ny < h and 0 <= nx < w and not seen[ny, nx] and m[ny, nx]:
                    seen[ny, nx] = True
                    stack.append((ny, nx))
        if min_px <= len(comp) <= max_px:
            cy = sum(c[0] for c in comp) / len(comp)
            cx = sum(c[1] for c in comp) / len(comp)
            hs = max(c[0] for c in comp) - min(c[0] for c in comp) + 1
            ws = max(c[1] for c in comp) - min(c[1] for c in comp) + 1
            # round-ish and compact
            if hs <= 40 and ws <= 40 and abs(hs - ws) <= max(6, 0.5 * max(hs, ws)):
                out.append({"x": float(cx), "y": float(cy), "px": len(comp)})
    return out


# --------------------------------------------------------------------------
# driver
# --------------------------------------------------------------------------

def measure(frame_path, baseline_path=None, frame_rect=None, verbose=False):
    mask, shape = build_mask(frame_path, baseline_path)
    h, w = shape
    fw, fh = (frame_rect if frame_rect else (w, h))

    ho = hough(mask)
    if ho is None:
        return {"file": os.path.basename(frame_path), "error": "empty mask"}
    lines = peak_lines(ho)
    clusters = cluster_vps(lines, shape)
    scan = horizon_scan(mask)
    # Pass the drawn rule in when we have it, so on/off-horizon is decided by
    # the horizon Concepts actually drew rather than by the points themselves.
    on, off, hy_vp = select_vps(clusters, shape,
                                horizon_y=(scan["y"] if scan else None))
    hough_rule = horizontal_rule_hough(lines, shape)
    dots = find_dots(mask, shape)

    # The horizon, and the cross-check section 15.2 step 5 asks for. The drawn
    # rule is authoritative WHEN IT IS IN FRAME; when it is not (every `Below`
    # preset) the vanishing points' own level defines it. Disagreement between
    # the two is reported rather than silently averaged - it would mean the
    # row-scan latched onto something that is not the horizon.
    # PRIMARY measurement: RANSAC on angular residual. Works whether the
    # vanishing points are inside the frame or thousands of pixels outside it.
    pts = ransac_vps(lines, shape)
    pair = horizon_pair(pts, shape)

    # The horizon. The drawn rule, when Concepts put one in frame, is the
    # authority; otherwise the vanishing points define it.
    hy_free = 0.5 * (pair[0]["y"] + pair[1]["y"]) if pair else None
    if scan is not None:
        horiz = scan["y"]
    elif hy_free is not None:
        horiz = hy_free
    else:
        horiz = pts[0]["y"] if pts else None

    # Cross-check: the drawn rule against the points' own level.
    agree = None
    if scan is not None and hy_free is not None:
        agree = round(abs(scan["y"] - hy_free), 1)

    tilt = None
    if pair:
        t = math.degrees(math.atan2(pair[1]["y"] - pair[0]["y"],
                                    pair[1]["x"] - pair[0]["x"]))
        tilt = round(min(abs(t), 180.0 - abs(t)), 3)

    keep = sorted(pair, key=lambda p: p["x"]) if pair else (pts[:1] if pts else [])
    others = [p for p in pts if p not in keep]

    def dot_near(cx, cy):
        if not dots:
            return None
        d = min(dots, key=lambda p: math.hypot(p["x"] - cx, p["y"] - cy))
        return {"x_px": round(d["x"], 1), "y_px": round(d["y"], 1),
                "dist_px": round(math.hypot(d["x"] - cx, d["y"] - cy), 1),
                "px": d["px"]}

    def pack(c):
        return {
            "x_px": round(c["x"], 1),
            "y_px": round(c["y"], 1),
            "x_frac": round(c["x"] / fw, 5),
            "y_frac": round(c["y"] / fh, 5),
            "nlines": c["nlines"],
            "votes": c["votes"],
            "in_frame": bool(0 <= c["x"] < w and 0 <= c["y"] < h),
            # cross-check: Concepts draws a handle dot at every ON-SCREEN
            # vanishing point. A convergence with no dot near it, when the
            # convergence is inside the frame, means we found the wrong thing.
            "dot": dot_near(c["x"], c["y"]),
        }

    res = {
        "file": os.path.basename(frame_path),
        "image": [w, h],
        "frame": [fw, fh],
        "horizon_px": round(horiz, 1) if horiz is not None else None,
        "horizon_frac": round(horiz / fh, 5) if horiz is not None else None,
        "horizon_source": ("row-scan" if scan else
                           ("vanishing points (rule off-frame)" if hy_vp is not None else None)),
        "horizon_scan": scan,
        "horizon_from_vps": round(hy_free, 1) if hy_free is not None else None,
        "horizon_agreement_px": agree,
        "horizon_tilt_deg": tilt,
        "hough_rule_rejected": hough_rule,
        "vps": [{
            "x_px": round(f["x"], 1),
            "y_px": round(f["y"], 1),
            "x_frac": round(f["x"] / fw, 5),
            "y_frac": round(f["y"] / fh, 5),
            "nlines": f["n"],
            "votes": f["votes"],
            "in_frame": bool(0 <= f["x"] < w and 0 <= f["y"] < h),
            "dot": dot_near(f["x"], f["y"]),
        } for f in keep],
        "vps_off_horizon": [{
            "x_px": round(f["x"], 1),
            "y_px": round(f["y"], 1),
            "x_frac": round(f["x"] / fw, 5),
            "y_frac": round(f["y"] / fh, 5),
            "nlines": f["n"],
            "in_frame": bool(0 <= f["x"] < w and 0 <= f["y"] < h),
            "dot": dot_near(f["x"], f["y"]),
        } for f in others],
        "dots_on_horizon": [
            {"x_px": round(d["x"], 1), "y_px": round(d["y"], 1),
             "x_frac": round(d["x"] / fw, 5), "px": d["px"]}
            for d in sorted(dots, key=lambda d: d["x"])
            if horiz is not None and abs(d["y"] - horiz) <= 0.01 * h
        ],
        "nlines": len(lines),
    }
    if verbose:
        print(json.dumps(res, indent=2))
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--frame", action="append", default=[])
    ap.add_argument("--dir")
    ap.add_argument("--baseline")
    ap.add_argument("--frame-rect", help="WxH of the stored reference frame; "
                                         "defaults to the image size")
    ap.add_argument("--json")
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()

    rect = None
    if a.frame_rect:
        rect = tuple(int(v) for v in a.frame_rect.lower().split("x"))

    frames = list(a.frame)
    if a.dir:
        for f in sorted(os.listdir(a.dir)):
            if f.lower().endswith(".png") and "nogrid" not in f.lower():
                frames.append(os.path.join(a.dir, f))

    out = []
    for f in frames:
        r = measure(f, a.baseline, rect, a.verbose)
        out.append(r)
        vs = ", ".join("%.4f" % v["x_frac"] for v in r.get("vps", []))
        off = ", ".join("%.4f@y%.4f" % (v["x_frac"], v["y_frac"])
                        for v in r.get("vps_off_horizon", []))
        print("%-34s horizon=%s [%s]  vps_x=[%s]%s"
              % (r["file"],
                 ("%.4f" % r["horizon_frac"]) if r.get("horizon_frac") is not None else "none",
                 r.get("horizon_source") or "-",
                 vs,
                 ("  off-horizon=[%s]" % off) if off else ""))
        sys.stdout.flush()

    if a.json:
        with open(a.json, "w") as fh:
            json.dump(out, fh, indent=2)
        print("wrote", a.json)


if __name__ == "__main__":
    main()
