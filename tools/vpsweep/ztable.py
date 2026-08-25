import json
W, H = 2880.0, 1800.0
cal = json.load(open("vpzoom/calib-final.json"))
k, tx, ty, LOO = cal["k"], cal["tx"], cal["ty"], cal["worst_loo_frac"]
TEN = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}
VE  = {r["file"]: r for r in json.load(open("vpzoom/vpest-10pct.json"))}
BX = lambda f: ((f * W - tx) / k) / W
BY = lambda f: ((f * H - ty) / k) / H

KNOWN_1X = {
 ("1-Point", "1 Point"):        (0.4989, [0.4998, None], None),
 ("2-Point", "2 Point"):        (0.4989, [0.2570, 0.7430], None),
 ("2-Point", "1_4 Wide"):       (0.2822, [0.1918, 1.5112], None),
 ("2-Point", "Side Ultrawide"): (0.4989, [0.0839, 0.7009], None),
 ("3-Point", "3 Point"):        (0.8322, [0.2221, 0.7777], (0.5004, 0.1657)),
 ("3-Point", "1_4 Wide"):       (0.2211, [-0.2695, 1.0207], None),
}
LISTS = [("1-Point", ["1 Point"]),
         ("2-Point", ["2 Point", "1_2 Narrow", "1_4 Narrow", "Side Narrow", "1_2 Wide",
                      "1_4 Wide", "Side Wide", "1_2 Wide Below", "Side Ultrawide"]),
         ("3-Point", ["3 Point", "3_4 Narrow", "1_2 Narrow", "3_4 Wide", "1_4 Wide",
                      "Side Wide Below", "1_4 Wide Below", "3_4 Ultrawide Below",
                      "3_4 Ultrawide"])]

rows = []
for lst, names in LISTS:
    for p in names:
        key = (lst, p)
        if key in KNOWN_1X:
            h, xs, th = KNOWN_1X[key]
            rows.append((lst, p, h, xs[0], xs[1], th, "100%"))
            continue
        r = TEN["S10 - %s - %s.png" % (lst, p)]
        xs = sorted(v["x_frac"] for v in r["vps"])
        off = r.get("vps_off_horizon") or []
        th = (BX(off[0]["x_frac"]), BY(off[0]["y_frac"])) if off else None
        rows.append((lst, p, BY(r["horizon_frac"]), BX(xs[0]), BX(xs[1]), th, "10%"))

print("| list | preset | horizon y | VP x | VP x | third point | sep | from |")
print("|---|---|---|---|---|---|---|---|")
for lst, p, h, x1, x2, th, src in rows:
    t = ("%.4f @ y %.4f" % th) if th else "—"
    sep = ("%.3f" % (x2 - x1)) if x2 is not None else "—"
    print("| %s | `%s` | %.4f | %.4f | %s | %s | %s | %s |"
          % (lst, p.replace("_", "/"), h, x1,
             ("%.4f" % x2) if x2 is not None else "—", t, sep, src))
print()
print("worst leave-one-out control residual: %.4f of the frame (%.1f px at 100%%)"
      % (LOO, LOO * W))
print()
print("separations, sorted (extends 15.5b):")
for lst, p, h, x1, x2, th, src in sorted(
        [r for r in rows if r[4] is not None], key=lambda r: r[4] - r[3]):
    print("   %-8s %-22s %.3f" % (lst, p.replace("_", "/"), x2 - x1))
