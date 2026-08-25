"""Do the two estimators agree on each point, and is each well conditioned?"""
import json
W, H = 2880.0, 1800.0
TEN = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}
VE  = {r["file"]: r for r in json.load(open("vpzoom/vpest-10pct.json"))}

def dedup(fams, tol=0.012):
    out = []
    for q in sorted(fams, key=lambda q: -q["n"]):
        if all(abs(q["x_frac"] - o["x_frac"]) > tol or abs(q["y_frac"] - o["y_frac"]) > tol
               for o in out):
            out.append(q)
    return out

for f in sorted(TEN):
    r = TEN[f]
    pts = [(v["x_frac"], v["y_frac"], "VP") for v in r["vps"]]
    off = r.get("vps_off_horizon") or []
    if off:
        pts.append((off[0]["x_frac"], off[0]["y_frac"], "3rd"))
    fams = dedup(VE[f]["families"])
    print(f[6:-4])
    for x, y, tag in pts:
        best, bd = None, 9e9
        for q in fams:
            d = ((q["x_frac"] - x) ** 2 + (q["y_frac"] - y) ** 2) ** 0.5
            if d < bd: bd, best = d, q
        if best is None:
            print("    %-4s x=%8.4f y=%8.4f   no vpest family" % (tag, x, y)); continue
        print("    %-4s x=%8.4f y=%8.4f | vpest x=%8.4f y=%8.4f  gap=%.4f "
              "n=%3d spread=%6.1f sigma=%s"
              % (tag, x, y, best["x_frac"], best["y_frac"], bd,
                 best["n"], best["spread_deg"], best["sigma_px"]))
