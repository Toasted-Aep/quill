#!/usr/bin/env python3
"""vpest.py - projective vanishing-point estimator with an honesty metric.

Why a second estimator exists at all: measure_vp.py's clustering works in
PIXELS, and a pixel tolerance is meaningless for a point 30,000 px off-frame -
two lines that differ by a hair's breadth in angle intersect wildly far apart.
This one works in the PROJECTIVE space where the problem actually lives, and,
more importantly, it reports the CONDITIONING of each fit:

    a fan of lines spanning an angle `spread`, each carrying angular noise
    `resid`, locates its apex to about  d * resid / spread  along the fan axis.

That ratio is what decides whether a number is a measurement or a guess. It is
reported for every point, and a caller that ignores it deserves what it gets.

Lines come from measure_vp.py's Hough (theta, rho), so the two estimators share
an ink mask and a line set and differ only in how they converge them.
"""
import json, math, os, sys
import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import measure_vp as M


def lines_of(frame, baseline, min_votes=120, max_lines=220, max_lum=170):
    """Grid ink only, with the one filter differencing cannot supply.

    Differencing removes chrome that is IDENTICAL in both frames. It does
    nothing about chrome that appeared between them - a dial menu that opened,
    a mode bar that came up - and those arrive as long bright straight edges,
    i.e. as perfect Hough lines that belong to no vanishing point. Concepts
    draws the grid at low opacity (grey ~30-70 on black) while UI text and
    icons run 200+, so an upper luminance cap separates them cleanly and
    costs no real grid ink.
    """
    mask, shape = M.build_mask(frame, baseline)
    if max_lum is not None:
        mask = mask & (M.load_gray(frame) <= max_lum)
    ho = M.hough(mask)
    if ho is None:
        return [], shape, mask
    return M.peak_lines(ho, min_votes=min_votes, max_lines=max_lines), shape, mask


def _hom(lines, shape, s=1000.0):
    """Homogeneous line vectors in a centred, scaled frame, unit-normalised."""
    h, w = shape
    cx, cy = 0.5 * w, 0.5 * h
    L = []
    for ln in lines:
        t, r = ln["theta"], ln["rho"]
        v = np.array([s * math.cos(t), s * math.sin(t),
                      cx * math.cos(t) + cy * math.sin(t) - r], dtype=np.float64)
        L.append(v / np.linalg.norm(v))
    return np.array(L), (cx, cy, s)


def _to_px(p, meta):
    cx, cy, s = meta
    if abs(p[2]) < 1e-12:
        return None                      # point at infinity
    return (cx + s * p[0] / p[2], cy + s * p[1] / p[2])


def fit(frame, baseline, eps_deg=0.25, min_inliers=6, max_fams=5,
        min_votes=120):
    lines, shape, mask = lines_of(frame, baseline, min_votes=min_votes)
    h, w = shape
    if len(lines) < 4:
        return {"file": os.path.basename(frame), "error": "too few lines",
                "nlines": len(lines)}
    L, meta = _hom(lines, shape)
    thetas = np.array([ln["theta"] for ln in lines])
    votes = np.array([ln["votes"] for ln in lines], dtype=float)
    eps = math.radians(eps_deg)
    n = len(L)
    used = np.zeros(n, dtype=bool)
    fams = []

    for _ in range(max_fams):
        avail = np.where(~used)[0]
        if len(avail) < min_inliers:
            break
        best = None
        # exhaustive over pairs of available lines - n is a couple of hundred
        for a_i in range(len(avail)):
            i = avail[a_i]
            for b_i in range(a_i + 1, len(avail)):
                j = avail[b_i]
                # skip near-parallel pairs: their cross product is ill-conditioned
                d = abs(math.sin(thetas[j] - thetas[i]))
                if d < math.sin(math.radians(1.5)):
                    continue
                p = np.cross(L[i], L[j])
                nrm = np.linalg.norm(p)
                if nrm < 1e-12:
                    continue
                p = p / nrm
                r = np.abs(L[avail] @ p)
                inl = avail[r < eps]
                if len(inl) < min_inliers:
                    continue
                score = votes[inl].sum()
                if best is None or score > best[0]:
                    best = (score, p, inl)
        if best is None:
            break
        _, p, inl = best
        # refine: smallest singular vector of the inlier line matrix, twice
        for _ in range(3):
            _, _, vt = np.linalg.svd(L[inl])
            p = vt[-1]
            r = np.abs(L @ p)
            inl = np.where((r < eps) & (~used | np.isin(np.arange(n), inl)))[0]
            inl = np.array([k for k in inl if not used[k] or k in set(inl)])
            if len(inl) < min_inliers:
                break
        if len(inl) < min_inliers:
            break
        r = np.abs(L[inl] @ p)
        resid = float(np.sqrt((r ** 2).mean()))          # radians, angular
        # spread: how much of a fan this really is. thetas of a fan through a
        # near point sweep widely; a near-parallel family barely sweeps at all.
        # theta lives on a CIRCLE of period pi, so a plain max-min is wrong the
        # moment a family straddles 0/pi - it reported 570 deg of "spread" on a
        # 2 Point fan that spans about 100. Take the extent as pi minus the
        # largest empty gap, which is the arc the family actually occupies.
        th = np.sort(np.mod(thetas[inl], math.pi))
        if len(th) < 2:
            spread = 0.0
        else:
            gaps = np.diff(th)
            wrap = (th[0] + math.pi) - th[-1]
            spread = float(math.pi - max(gaps.max(), wrap))
        pt = _to_px(p, meta)
        cx, cy, _s = meta
        d = math.hypot(pt[0] - cx, pt[1] - cy) if pt else float("inf")
        # positional uncertainty along the fan axis
        sigma = (d * resid / spread) if spread > 1e-9 else float("inf")
        fams.append({
            "x_px": None if pt is None else round(pt[0], 1),
            "y_px": None if pt is None else round(pt[1], 1),
            "x_frac": None if pt is None else round(pt[0] / w, 5),
            "y_frac": None if pt is None else round(pt[1] / h, 5),
            "n": int(len(inl)),
            "votes": int(votes[inl].sum()),
            "resid_deg": round(math.degrees(resid), 4),
            "spread_deg": round(math.degrees(spread), 3),
            "dist_px": round(d, 1),
            "sigma_px": None if sigma == float("inf") else round(sigma, 1),
            "at_infinity": pt is None,
        })
        used[inl] = True

    fams.sort(key=lambda f: -f["votes"])
    return {"file": os.path.basename(frame), "image": [w, h],
            "nlines": len(lines), "families": fams}


def main():
    import argparse
    ap = argparse.ArgumentParser()
    ap.add_argument("--baseline")
    ap.add_argument("--frame", action="append", default=[])
    ap.add_argument("--dir")
    ap.add_argument("--json")
    ap.add_argument("--eps-deg", type=float, default=0.25)
    ap.add_argument("--min-votes", type=int, default=120)
    a = ap.parse_args()
    frames = list(a.frame)
    if a.dir:
        for f in sorted(os.listdir(a.dir)):
            if f.lower().endswith(".png") and "nogrid" not in f.lower() \
               and not f.lower().startswith("strip"):
                frames.append(os.path.join(a.dir, f))
    out = []
    for f in frames:
        r = fit(f, a.baseline, eps_deg=a.eps_deg, min_votes=a.min_votes)
        out.append(r)
        print(r["file"])
        for fam in r.get("families", []):
            print("   x=%9s y=%9s  n=%3d resid=%6.4f deg spread=%7.3f deg"
                  "  d=%9.1f  sigma=%s"
                  % (fam["x_frac"], fam["y_frac"], fam["n"], fam["resid_deg"],
                     fam["spread_deg"], fam["dist_px"], fam["sigma_px"]))
        sys.stdout.flush()
    if a.json:
        json.dump(out, open(a.json, "w"), indent=2)
        print("wrote", a.json)


if __name__ == "__main__":
    main()
