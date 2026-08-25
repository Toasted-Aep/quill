import json
W, H = 2880.0, 1800.0
cal = json.load(open("vpzoom/calib-final.json"))
k, tx, ty = cal["k"], cal["tx"], cal["ty"]
TEN = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}
BX = lambda f: ((f * W - tx) / k) / W
BY = lambda f: ((f * H - ty) / k) / H

names = ["3 Point", "3_4 Narrow", "1_2 Narrow", "3_4 Wide", "1_4 Wide",
         "Side Wide Below", "1_4 Wide Below", "3_4 Ultrawide Below", "3_4 Ultrawide"]
print("3-Point: the third point, and how far it sits from the horizon")
print("%-22s %9s %9s %9s %9s" % ("preset", "horizon", "3rd x", "3rd y", "|3rd-h|"))
rows = []
for p in names:
    r = TEN["S10 - 3-Point - %s.png" % p]
    off = r["vps_off_horizon"][0]
    h = BY(r["horizon_frac"]); tx_ = BX(off["x_frac"]); ty_ = BY(off["y_frac"])
    rows.append((p, h, tx_, ty_, abs(ty_ - h)))
for p, h, a, b, d in rows:
    print("%-22s %9.4f %9.4f %9.4f %9.3f" % (p.replace("_", "/"), h, a, b, d))
print()
print("sorted by |third - horizon|  (the 3-point angle-of-view measure):")
for p, h, a, b, d in sorted(rows, key=lambda r: r[4]):
    print("   %-24s %8.3f" % (p.replace("_", "/"), d))
