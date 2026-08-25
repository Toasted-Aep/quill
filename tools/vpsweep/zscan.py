import json
ten = json.load(open("vpzoom/measure-10pct.json"))
one = json.load(open("sweep-2026-08-24.json"))
print("%-42s %-28s %s" % ("frame", "horizon row-scan", "tilt(vp pair)"))
for tag, d in (("100%", one), ("10%", ten)):
    for r in d:
        if "2-Point" not in r["file"]:
            continue
        s = r.get("horizon_scan")
        sd = ("y=%7.1f count=%4d span=%.2f" % (s["y"], s["count"], s["span"])) if s else "none (rule off-frame)"
        print("%-5s %-36s %-28s %s" % (tag, r["file"][:36], sd, r.get("horizon_tilt_deg")))
    print()
