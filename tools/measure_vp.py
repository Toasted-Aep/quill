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
    merged = []
    for c in clusters:
        if any(math.hypot(c["x"] - m["x"], c["y"] - m["y"]) <= 4 * tol
               for m in merged):
            continue
        merged.append(c)
    return merged


# --------------------------------------------------------------------------
# 4. horizon + drawn dots
# --------------------------------------------------------------------------

def horizontal_rule(lines, shape, tol_deg=1.0):
    """The drawn horizon: the strongest near-horizontal Hough line."""
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
    vps = cluster_vps(lines, shape)
    rule = horizontal_rule(lines, shape)
    dots = find_dots(mask, shape)

    # Horizon: prefer the drawn rule; fall back to the mean y of the two
    # vanishing points that sit furthest apart horizontally.
    horiz = None
    if rule:
        horiz = rule["y"]
    side_vps = sorted(vps[:4], key=lambda c: c["x"])
    if horiz is None and len(side_vps) >= 2:
        horiz = 0.5 * (side_vps[0]["y"] + side_vps[-1]["y"])

    res = {
        "file": os.path.basename(frame_path),
        "image": [w, h],
        "frame": [fw, fh],
        "horizon_px": horiz,
        "horizon_frac": (horiz / fh) if horiz is not None else None,
        "rule": rule,
        "vps": [],
        "dots": dots,
        "nlines": len(lines),
    }
    for c in vps[:5]:
        res["vps"].append({
            "x_px": round(c["x"], 1),
            "y_px": round(c["y"], 1),
            "x_frac": round(c["x"] / fw, 5),
            "y_frac": round(c["y"] / fh, 5),
            "nlines": c["nlines"],
            "votes": c["votes"],
            # cross-check: is there a drawn dot at this convergence?
            "dot_dist_px": round(min(
                [math.hypot(d["x"] - c["x"], d["y"] - c["y"]) for d in dots]
            ), 1) if dots else None,
        })
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
        print("%-34s horizon=%s  vps_x_frac=[%s]"
              % (r["file"],
                 ("%.4f" % r["horizon_frac"]) if r.get("horizon_frac") is not None else "none",
                 vs))
        sys.stdout.flush()

    if a.json:
        with open(a.json, "w") as fh:
            json.dump(out, fh, indent=2)
        print("wrote", a.json)


if __name__ == "__main__":
    main()
