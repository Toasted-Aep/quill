"""Convert the 10% measurements back to fractions of the 100% frame."""
import json
W, H = 2880.0, 1800.0
cal = json.load(open("vpzoom/calib-10pct.json"))
k, tx, ky, ty = cal["k"], cal["tx"], cal["ky"], cal["ty"]
CTRL = cal["worst_control_frac"]

mv = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}
ve = {r["file"]: r for r in json.load(open("vpzoom/vpest-10pct.json"))}

ORDER = ["2 Point", "1_2 Narrow", "1_4 Narrow", "Side Narrow", "1_2 Wide",
         "1_4 Wide", "Side Wide", "1_2 Wide Below", "Side Ultrawide"]
KNOWN = {"2 Point": (0.2570, 0.7430, 0.4989),
         "1_4 Wide": (0.1918, 1.5112, 0.2822),
         "Side Ultrawide": (0.0839, 0.7009, 0.4989)}

def bx(f): return ((f * W - tx) / k) / W
def by(f): return ((f * H - ty) / ky) / H

print("%-16s | %-8s | %-17s | %-17s | %s"
      % ("preset", "horizon", "VP x1", "VP x2", "two-estimator spread"))
print("-" * 96)
out = {}
for n in ORDER:
    f = "S10 - 2-Point - %s.png" % n
    r = mv[f]
    xs = sorted(v["x_frac"] for v in r["vps"])
    hy = r["horizon_frac"]
    # the same two points as vpest sees them, for an independent cross-check
    fams = [q for q in ve[f]["families"] if q["n"] >= 25]
    seen, vx = [], []
    for q in sorted(fams, key=lambda q: -q["n"]):
        if all(abs(q["x_frac"] - s) > 0.01 for s in seen):
            seen.append(q["x_frac"]); vx.append(q["x_frac"])
    vx = sorted(vx)[:2]
    spread = max(abs(a - b) for a, b in zip(xs, vx)) if len(vx) == 2 else None
    c = (bx(xs[0]), bx(xs[1]), by(hy))
    out[n] = {"h": c[2], "x1": c[0], "x2": c[1],
              "zoom": {"h": hy, "x1": xs[0], "x2": xs[1]},
              "est_spread_frac": spread}
    tag = ""
    if n in KNOWN:
        kx1, kx2, kh = KNOWN[n]
        tag = "  KNOWN 1x: %.4f / %.4f  h %.4f   dx=%.4f,%.4f dh=%.4f" % (
            kx1, kx2, kh, abs(c[0] - kx1), abs(c[1] - kx2), abs(c[2] - kh))
    print("%-16s | %8.4f | %8.4f (%.4f) | %8.4f (%.4f) | %s%s"
          % (n, c[2], c[0], xs[0], c[1], xs[1],
             ("%.4f" % spread) if spread is not None else "  -  ", tag))
json.dump(out, open("vpzoom/converted-2point.json", "w"), indent=2)
print()
print("control residual carried on every row: +/- %.4f of the frame (%.1f px at 100%%)"
      % (CTRL, CTRL * W))
