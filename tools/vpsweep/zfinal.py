"""Calibrate on `2 Point` alone, control on four presets, convert all 18."""
import json
import numpy as np
W, H = 2880.0, 1800.0

ONE = {r["file"]: r for r in json.load(open("sweep-2026-08-24.json"))}
TEN = {r["file"]: r for r in json.load(open("vpzoom/measure-10pct.json"))}

def one(lst, p):
    r = ONE["%s - %s.png" % (lst, p)]
    third = r.get("vps_off_horizon") or []
    third = ({"x": third[0]["x_frac"], "y": third[0]["y_frac"]}
             if (lst == "3-Point" and third) else None)
    return {"x": sorted(v["x_frac"] for v in r["vps"]), "h": r["horizon_frac"],
            "third": third}

def ten(lst, p):
    r = TEN["S10 - %s - %s.png" % (lst, p)]
    off = r.get("vps_off_horizon") or []
    third = {"x": off[0]["x_frac"], "y": off[0]["y_frac"]} if off else None
    return {"x": sorted(v["x_frac"] for v in r["vps"]), "h": r["horizon_frac"],
            "third": third}

# ------------------------------------------------- fit: `2 Point` and nothing else
a = one("2-Point", "2 Point"); b = ten("2-Point", "2 Point")
x1a, x2a = [v * W for v in a["x"]]; x1b, x2b = [v * W for v in b["x"]]
k  = (x2b - x1b) / (x2a - x1a)
tx = x1b - k * x1a
ty = b["h"] * H - k * (a["h"] * H)          # isotropy ASSUMED here, TESTED below
print("=" * 86)
print("MAPPING, fitted on `2 Point` alone (its two VPs give k and tx; its horizon gives ty)")
print("=" * 86)
print("   screen_10%% = %.6f * screen_100%% + (%.3f, %.3f)" % (k, tx, ty))
print("   fixed point = (%.2f, %.2f) px   -- the wheel was driven at (1440, 900)"
      % (tx / (1 - k), ty / (1 - k)))

def BX(f): return ((f * W - tx) / k) / W
def BY(f): return ((f * H - ty) / ky_used) / H
ky_used = k

# ------------------------------------------------------------------- control
CTRL = [("2-Point", "Side Ultrawide"), ("2-Point", "1_4 Wide"),
        ("3-Point", "3 Point"), ("3-Point", "1_4 Wide")]
print()
print("=" * 86)
print("CONTROL - four presets measured independently at 100%%, none used in the fit")
print("=" * 86)
print("%-24s %-11s %10s %11s %9s %9s" % ("preset", "quantity", "known 1x", "recovered",
                                          "d(frac)", "d(px@1x)"))
worst = 0.0; worst_what = ""
for lst, p in CTRL:
    kn, zo = one(lst, p), ten(lst, p)
    items = [("VP x1", kn["x"][0], BX(zo["x"][0]), W),
             ("VP x2", kn["x"][1], BX(zo["x"][1]), W),
             ("horizon y", kn["h"], BY(zo["h"]), H)]
    if kn["third"] and zo["third"]:
        items += [("3rd pt x", kn["third"]["x"], BX(zo["third"]["x"]), W),
                  ("3rd pt y", kn["third"]["y"], BY(zo["third"]["y"]), H)]
    for q, kv, rv, den in items:
        d = abs(rv - kv)
        if d > worst: worst, worst_what = d, "%s %s %s" % (lst, p, q)
        print("%-24s %-11s %10.4f %11.4f %9.4f %9.1f"
              % ("%s %s" % (lst, p), q, kv, rv, d, d * den))
print()
print("   WORST CONTROL RESIDUAL: %.4f of the frame (%.1f px at 100%%)  on %s"
      % (worst, worst * W, worst_what))

# ---------------------------------------------------- isotropy, measured not assumed
c = one("2-Point", "1_4 Wide"); d_ = ten("2-Point", "1_4 Wide")
ky = (b["h"] * H - d_["h"] * H) / (a["h"] * H - c["h"] * H)
print()
print("   isotropy check: kx = %.6f from the x landmarks, ky = %.6f from two"
      % (k, ky))
print("   independent horizons -> differ by %.6f (%.2f%%)." % (abs(k - ky), 100 * abs(k - ky) / k))

# ------------------------------------------------------------------ the table
LISTS = {
  "1-Point": ["1 Point"],
  "2-Point": ["2 Point", "1_2 Narrow", "1_4 Narrow", "Side Narrow", "1_2 Wide",
              "1_4 Wide", "Side Wide", "1_2 Wide Below", "Side Ultrawide"],
  "3-Point": ["3 Point", "3_4 Narrow", "1_2 Narrow", "3_4 Wide", "1_4 Wide",
              "Side Wide Below", "1_4 Wide Below", "3_4 Ultrawide Below", "3_4 Ultrawide"],
}
KNOWN_AT_1X = {("2-Point", "2 Point"), ("2-Point", "1_4 Wide"),
               ("2-Point", "Side Ultrawide"), ("3-Point", "3 Point"),
               ("3-Point", "1_4 Wide"), ("1-Point", "1 Point")}
print()
print("=" * 86)
print("CONVERTED TABLE - fractions of the ORIGINAL 2880x1800 frame")
print("=" * 86)
print("%-8s %-22s %9s %9s %9s   %s" % ("list", "preset", "horizon", "VP x1", "VP x2", "third"))
out = {}
for lst, names in LISTS.items():
    for p in names:
        if lst == "1-Point":
            continue
        zo = ten(lst, p)
        row = {"h": BY(zo["h"]), "x1": BX(zo["x"][0]), "x2": BX(zo["x"][1])}
        if zo["third"]:
            row["tx"] = BX(zo["third"]["x"]); row["ty"] = BY(zo["third"]["y"])
        row["sep"] = row["x2"] - row["x1"]
        row["known_1x"] = (lst, p) in KNOWN_AT_1X
        out["%s|%s" % (lst, p)] = row
        t = ("%.4f @ y %.4f" % (row["tx"], row["ty"])) if "tx" in row else "-"
        print("%-8s %-22s %9.4f %9.4f %9.4f   %s%s"
              % (lst, p.replace("_", "/"), row["h"], row["x1"], row["x2"], t,
                 "   <- already known at 1x" if row["known_1x"] else ""))
json.dump({"k": k, "tx": tx, "ty": ty, "ky_independent": ky,
           "worst_control_frac": worst, "worst_control_on": worst_what,
           "rows": out}, open("vpzoom/final-10pct.json", "w"), indent=2)
print()
print("wrote vpzoom/final-10pct.json")
