"""Calibrate the 10% viewport against the 100% one, and test the mapping.

The whole zoomed pass stands or falls on this file. A mapping fitted on one
preset and merely ASSERTED for the rest would put a decimal point on every one
of the thirteen blanks and be wrong in the same direction for all of them. So:
fit on `2 Point` alone, then predict `Side Ultrawide` and `1/4 Wide`, which
were measured independently at 100%, and report how far off the prediction is.
`1/4 Wide` is the demanding one - its right-hand point sits at 1.5112, i.e.
1470 px OUTSIDE the frame at 100%, which is exactly the regime the thirteen
blanks live in.
"""
import json
import numpy as np

W, H = 2880.0, 1800.0

# --- 100% values, from the committed sweep JSON (measure_vp, same estimator) --
one = json.load(open("../../../../../../Downloads/Quill Gem - Fable/quill-sweep2/"
                     "tools/vpsweep/sweep-2026-08-24.json")) \
      if False else json.load(open("sweep-2026-08-24.json"))
ONE = {r["file"]: r for r in one}

def one_of(name):
    r = ONE["2-Point - %s.png" % name] if ("2-Point - %s.png" % name) in ONE else ONE[name]
    return ([v["x_frac"] * W for v in r["vps"]], r["horizon_frac"] * H,
            r.get("horizon_tilt_deg"))

ten = json.load(open("vpzoom/measure-10pct.json"))
TEN = {r["file"]: r for r in ten}

def ten_of(name):
    r = TEN["S10 - 2-Point - %s.png" % name]
    return ([v["x_frac"] * W for v in r["vps"]], r["horizon_frac"] * H,
            r.get("horizon_tilt_deg"))

KNOWN = ["2 Point", "Side Ultrawide", "1_4 Wide"]
NAME1 = {"2 Point": "2 Point", "Side Ultrawide": "Side Ultrawide", "1_4 Wide": "1_4 Wide"}

print("=" * 78)
print("LANDMARKS  (px in the 2880x1800 frame)")
print("=" * 78)
lm = []
for n in KNOWN:
    x1, y1, t1 = one_of(NAME1[n]); x10, y10, t10 = ten_of(n)
    print("%-16s 100%%: x=%9.2f %9.2f  hy=%8.2f tilt=%.3f" % (n, x1[0], x1[1], y1, t1))
    print("%-16s  10%%: x=%9.2f %9.2f  hy=%8.2f tilt=%.3f" % ("", x10[0], x10[1], y10, t10))
    for a, b in zip(x1, x10):
        lm.append((a, y1, b, y10))

# ---------------------------------------------------------------- fit on one
print()
print("=" * 78)
print("FIT ON `2 Point` ALONE  (two x landmarks + one horizon)")
print("=" * 78)
(ax1, ay1, ax10, ay10), (bx1, by1, bx10, by10) = lm[0], lm[1]
k = (bx10 - ax10) / (bx1 - ax1)
tx = ax10 - k * ax1
print("  k  = %.6f      (readout says 10%%; 0.91^24 = %.4f)" % (k, 0.91 ** 24))
print("  tx = %.3f px" % tx)
print("  x fixed point = tx/(1-k) = %.2f px   (frame centre is %.1f; we wheeled at 1440)"
      % (tx / (1 - k), W / 2))

# y needs two distinct horizons: 2 Point and 1/4 Wide
_, cy1, _, cy10 = lm[4]
ky = (ay10 - cy10) / (ay1 - cy1)
ty = ay10 - ky * ay1
print("  ky = %.6f   (from the `2 Point` and `1/4 Wide` horizons)" % ky)
print("  ty = %.3f px" % ty)
print("  y fixed point = %.2f px   (frame centre is %.1f; we wheeled at 900)"
      % (ty / (1 - ky), H / 2))
print("  |k - ky| = %.6f  -> scale is %s within %.2f%%"
      % (abs(k - ky), "ISOTROPIC" if abs(k - ky) < 0.002 else "ANISOTROPIC",
         100 * abs(k - ky) / k))

def back_x(x10): return (x10 - tx) / k
def back_y(y10): return (y10 - ty) / ky

# ------------------------------------------------------------- the control
print()
print("=" * 78)
print("CONTROL - predict the two presets NOT used in the fit")
print("=" * 78)
print("%-16s %-10s %10s %10s %9s %9s" % ("preset", "quantity", "known 1x",
                                          "recovered", "d(frac)", "d(px@1x)"))
worst = 0.0
rows = []
for n in ["Side Ultrawide", "1_4 Wide"]:
    x1, y1, _ = one_of(NAME1[n]); x10, y10, _ = ten_of(n)
    for i, (a, b) in enumerate(zip(x1, x10)):
        rec = back_x(b)
        d = abs(rec - a)
        rows.append((n, "VP x%d" % (i + 1), a / W, rec / W, d / W, d))
        worst = max(worst, d / W)
    rec = back_y(y10); d = abs(rec - y1)
    rows.append((n, "horizon y", y1 / H, rec / H, d / H, d))
    worst = max(worst, d / H)
for n, q, a, r, df, dp in rows:
    print("%-16s %-10s %10.4f %10.4f %9.4f %9.1f" % (n, q, a, r, df, dp))
print()
print("  WORST CONTROL RESIDUAL: %.4f of the frame  (%.1f px at 100%%)"
      % (worst, worst * W))

# ------------------------------------------------- is the mapping affine?
print()
print("=" * 78)
print("IS THE ZOOM AFFINE, OR A PURE SCALE ABOUT A FIXED CENTRE?")
print("=" * 78)
P = np.array([[a, b, 1.0] for a, b, _, _ in lm])
Q = np.array([[c, d] for _, _, c, d in lm])
sol, *_ = np.linalg.lstsq(P, Q, rcond=None)
a_, c_ = sol[0]; b_, d_ = sol[1]; tx_, ty_ = sol[2]
res = Q - P @ sol
print("  unconstrained affine from all %d landmarks:" % len(lm))
print("     [ a b ]   [ %+.6f  %+.6f ]        tx = %+9.3f" % (a_, b_, tx_))
print("     [ c d ] = [ %+.6f  %+.6f ]        ty = %+9.3f" % (c_, d_, ty_))
print("  residual rms = %.2f px   max = %.2f px" % (np.sqrt((res ** 2).sum(1)).mean(),
                                                    np.sqrt((res ** 2).sum(1)).max()))
print("  off-diagonal (shear/rotation) terms: b = %+.6f, c = %+.6f" % (b_, c_))
print("  a - d = %+.6f" % (a_ - d_))
print()
print("  NOTE ON LEVERAGE: the six landmarks span %.0f px in x but only %.0f px"
      % (max(p[0] for p in P) - min(p[0] for p in P),
         max(p[1] for p in P) - min(p[1] for p in P)))
print("  in y, so b and c are the weakly determined terms. An independent check")
print("  on shear is the HORIZON TILT: a shear would tip a level line.")
for n in KNOWN:
    _, _, t1 = one_of(NAME1[n]); _, _, t10 = ten_of(n)
    print("     %-16s tilt 100%% = %.3f deg   tilt 10%% = %.3f deg" % (n, t1, t10))

json.dump({"k": k, "tx": tx, "ky": ky, "ty": ty,
           "worst_control_frac": worst,
           "affine": {"a": a_, "b": b_, "c": c_, "d": d_, "tx": tx_, "ty": ty_}},
          open("vpzoom/calib-10pct.json", "w"), indent=2)
print()
print("wrote vpzoom/calib-10pct.json")
