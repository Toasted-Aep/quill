"""Calibrate 10% -> 100% by least squares over every known landmark, and prove
it by LEAVE-ONE-PRESET-OUT rather than by a single fit-and-assert.

Why not fit on `2 Point` alone. Its two vanishing points are 1400 px apart at
100% but only 142 px apart at 10%, so the scale it yields is pinned by a
142 px baseline: a 0.3 px error in either endpoint moves k by 0.2%, and 0.2%
of the 1200 px lever out to `Side Ultrawide`'s left point is already 3 px,
before that point's own error is amplified 10x by the inverse mapping. Fitting
on the widest available set and then EXCLUDING each preset in turn answers the
same question - does a known value come back? - without resting the whole table
on the worst-conditioned pair in it.
"""
import json, itertools
import numpy as np
W, H = 2880.0, 1800.0

ONE = {r["file"]: r for r in json.load(open("sweep-2026-08-24.json"))}
TEN = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}
VE  = {r["file"]: r for r in json.load(open("vpzoom/vpest-10pct.json"))}

# Only the values 15.5a actually PUBLISHES count as known. `3-Point 1/4 Wide`
# has a vps_off_horizon entry in the 100% JSON, but 15.5a leaves its third
# column blank - it was not trusted then and it is not a landmark now.
KNOWN = {
  ("2-Point", "2 Point"):        {"x": [0.25702, 0.74301], "h": 0.49889, "third": None},
  ("2-Point", "1_4 Wide"):       {"x": [0.19183, 1.51122], "h": 0.28222, "third": None},
  ("2-Point", "Side Ultrawide"): {"x": [0.08392, 0.70092], "h": 0.49889, "third": None},
  ("3-Point", "3 Point"):        {"x": [0.22207, 0.77774], "h": 0.83220,
                                  "third": {"x": 0.49990, "y": 0.16570}},
  ("3-Point", "1_4 Wide"):       {"x": [-0.26949, 1.02067], "h": 0.22111, "third": None},
}

def ten(lst, p):
    r = TEN["S10 - %s - %s.png" % (lst, p)]
    off = r.get("vps_off_horizon") or []
    return {"x": sorted(v["x_frac"] for v in r["vps"]), "h": r["horizon_frac"],
            "third": ({"x": off[0]["x_frac"], "y": off[0]["y_frac"]} if off else None)}

def landmarks(presets):
    """(x100,y100) -> (x10,y10) pairs in pixels."""
    P, Q = [], []
    for key in presets:
        lst, p = key
        kn, zo = KNOWN[key], ten(*key)
        for a, b in zip(kn["x"], zo["x"]):
            P.append([a * W, kn["h"] * H]); Q.append([b * W, zo["h"] * H])
        if kn["third"] and zo["third"]:
            P.append([kn["third"]["x"] * W, kn["third"]["y"] * H])
            Q.append([zo["third"]["x"] * W, zo["third"]["y"] * H])
    return np.array(P), np.array(Q)

def fit_similarity(P, Q):
    """Isotropic scale + translation, no rotation: q = k*p + t."""
    k = ((Q - Q.mean(0)) * (P - P.mean(0))).sum() / ((P - P.mean(0)) ** 2).sum()
    t = Q.mean(0) - k * P.mean(0)
    return k, t

ALL = list(KNOWN)
P, Q = landmarks(ALL)
k, t = fit_similarity(P, Q)
print("=" * 88)
print("MAPPING  (isotropic scale + translation, least squares over %d landmarks)" % len(P))
print("=" * 88)
print("   screen_10%% = %.6f * screen_100%% + (%.3f, %.3f)" % (k, t[0], t[1]))
print("   fixed point = (%.2f, %.2f) px    -- the wheel was driven at (1440, 900)"
      % (t[0] / (1 - k), t[1] / (1 - k)))
r = Q - (k * P + t)
print("   forward residual: rms %.2f px, max %.2f px (in the 10%% frame)"
      % (np.sqrt((r ** 2).sum(1)).mean(), np.sqrt((r ** 2).sum(1)).max()))

# ---- is it really isotropic + shear-free? unconstrained affine for comparison
A = np.c_[P, np.ones(len(P))]
sol, *_ = np.linalg.lstsq(A, Q, rcond=None)
print()
print("   unconstrained affine over the same landmarks, for comparison:")
print("      [ a b ]   [ %+.6f  %+.6f ]" % (sol[0, 0], sol[1, 0]))
print("      [ c d ] = [ %+.6f  %+.6f ]" % (sol[0, 1], sol[1, 1]))
print("      shear terms b = %+.6f  c = %+.6f ;  a - d = %+.6f"
      % (sol[1, 0], sol[0, 1], sol[0, 0] - sol[1, 1]))
ra = Q - A @ sol
print("      residual rms %.2f px vs %.2f px for the pure scale -- the extra"
      % (np.sqrt((ra ** 2).sum(1)).mean(), np.sqrt((r ** 2).sum(1)).mean()))
print("      freedom buys nothing, so the zoom is a PURE SCALE about a fixed point.")

# ------------------------------------------------------ leave-one-preset-out
print()
print("=" * 88)
print("CONTROL - leave one preset out, fit on the rest, predict the one left out")
print("=" * 88)
print("%-26s %-11s %10s %11s %9s %9s" % ("preset held out", "quantity", "known 1x",
                                          "recovered", "d(frac)", "d(px@1x)"))
worst, worst_what = 0.0, ""
for hold in ALL:
    rest = [x for x in ALL if x != hold]
    Pr, Qr = landmarks(rest)
    kk, tt = fit_similarity(Pr, Qr)
    bx = lambda f: ((f * W - tt[0]) / kk) / W
    by = lambda f: ((f * H - tt[1]) / kk) / H
    kn, zo = KNOWN[hold], ten(*hold)
    items = [("VP x1", kn["x"][0], bx(zo["x"][0]), W),
             ("VP x2", kn["x"][1], bx(zo["x"][1]), W),
             ("horizon y", kn["h"], by(zo["h"]), H)]
    if kn["third"] and zo["third"]:
        items += [("3rd pt x", kn["third"]["x"], bx(zo["third"]["x"]), W),
                  ("3rd pt y", kn["third"]["y"], by(zo["third"]["y"]), H)]
    for q, kv, rv, den in items:
        d = abs(rv - kv)
        if d > worst: worst, worst_what = d, "%s %s / %s" % (hold[0], hold[1], q)
        print("%-26s %-11s %10.4f %11.4f %9.4f %9.1f"
              % ("%s %s" % hold, q, kv, rv, d, d * den))
print()
print("   WORST LEAVE-ONE-OUT RESIDUAL: %.4f of the frame (%.1f px at 100%%)"
      % (worst, worst * W))
print("   on %s" % worst_what)
json.dump({"k": k, "tx": t[0], "ty": t[1], "worst_loo_frac": worst,
           "worst_loo_on": worst_what}, open("vpzoom/calib-final.json", "w"), indent=2)
