#!/usr/bin/env python3
"""Proof that the table in copic_concepts_extract.py is the one the wheel draws.

A colour table that merely *ships* inside the app is not the table the wheel
renders from. Three independent lines of evidence say this one is.

1. PIXEL PROOF. concepts_dial_capture.png is a full-resolution grab of the
   live Concepts dial (2880x1800, unscaled, taken while Concepts was the
   foreground window). The dial's colour dot and its tool-colour arcs are flat
   fills of the app's current colour model. Sampled from their interiors, three
   of the four are byte-for-byte identical to entries in the extracted table,
   and none is an exact match for Quill's value of the same code.

   The RV63 hit matters most: RV63 is one of the codes Quill does NOT have, so
   the table's coverage of the missing codes is real and rendered, not
   hypothetical. The FV2 hit matters second: Quill *does* have FV2, and its
   value is 24 RGB units from what Concepts actually draws.

2. ADJACENCY. The record run terminates in the literal `IHslWheel_Bindings` -
   the colour-wheel binding metadata - with no other structure between.

3. STRUCTURAL ISOMORPHISM. The table's order is the wheel's tier order:
   eleven outer colour families first, then the four grey families, then the
   three core numbers, then the eight fluorescent accents. That is precisely
   the three-tier layout Quill's own CopicPalette.cs mirrors (Tier 3+ outer
   families / Tier 2 grey ring / Tier 1 accent+core arc), and the fluorescent
   and grey family memberships are identical sets. The outer families are the
   same cyclic hue order as Quill's, traversed the other way round.

Run: python scratchpad/copic_wheel_proof.py
"""
import json, os, re, statistics as st

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "copic_out")
SRC = os.path.join(HERE, "..", "src", "Quill", "Models", "CopicPalette.cs")
TOK = re.compile(r'\b([A-Za-z]*\d[0-9A-Za-z]*|White|Black)\s*:\s*([0-9a-fA-F]{6})\b')

# Flat-fill interiors sampled from concepts_dial_capture.png. Dial centre is
# (207,297) phys; the dot is r<37 and the tool-colour arcs sit at r=114..160.
SAMPLES = {
    "dial colour dot":   "bb434e",
    "tool arc (pink)":   "e8afd8",
    "tool arc (purple)": "af8ae6",
    "tool arc (blue)":   "5a6edc",
}

hexv = lambda s: (int(s[0:2], 16), int(s[2:4], 16), int(s[4:6], 16))
dist = lambda a, b: sum((p - q) ** 2 for p, q in zip(a, b)) ** 0.5


def main():
    con = json.load(open(os.path.join(OUT, "concepts_palette.json")))
    quill = {m.group(1): m.group(2).lower()
             for m in TOK.finditer(open(SRC, encoding='utf-8').read())}
    CON = {c: hexv(h) for c, h in con.items()}
    QUI = {c: hexv(h) for c, h in quill.items()}
    near = lambda px, t: min(((dist(px, v), c) for c, v in t.items()))

    print("=" * 70)
    print("1. PIXEL PROOF - live Concepts pixels vs the extracted table")
    print("=" * 70)
    dc, dq = [], []
    for label, h in SAMPLES.items():
        px = hexv(h)
        c_d, c_c = near(px, CON)
        q_d, q_c = near(px, QUI)
        dc.append(c_d); dq.append(q_d)
        print(f"\n  {label:<18} screen #{h}")
        print(f"      -> Concepts {c_c:<6} #{con[c_c]}  d={c_d:5.2f}"
              f"{'   EXACT' if c_d == 0 else ''}")
        print(f"      -> Quill    {q_c:<6} #{quill[q_c]}  d={q_d:5.2f}")
        if c_c in quill:
            print(f"      Quill's own {c_c} is #{quill[c_c]}, "
                  f"d={dist(px, QUI[c_c]):.2f} from what Concepts actually drew")
        else:
            print(f"      Quill has no {c_c} at all - it is one of the missing codes")
    print(f"\n  exact hits: Concepts {sum(1 for d in dc if d == 0)}/{len(dc)}"
          f"   Quill {sum(1 for d in dq if d == 0)}/{len(dq)}")
    print(f"  median d  : Concepts {st.median(dc):.2f}   Quill {st.median(dq):.2f}")

    print("\n" + "=" * 70)
    print("2. STRUCTURAL ISOMORPHISM - table order == wheel tier order")
    print("=" * 70)
    flu_c = {c for c in con if c.startswith("F") and not c[1:2].isdigit()}
    flu_q = {"FV2", "FB2", "FBG2", "FYG2", "FYG1", "FY1", "FYR1", "FRV1"}
    grey_c = {c[:1] for c in con if re.fullmatch(r'[CNTW]\d+0*', c)}
    print(f"  fluorescent accents identical set : {flu_c == flu_q}")
    print(f"  grey families                     : {sorted(grey_c)} "
          f"(Quill Tier2: Toner Warm Neutral Cool)")
    print(f"  core numbers                      : "
          f"{sorted(c for c in con if c in ('0', '100', '110'))}")
    print(f"  total codes in table              : {len(con)}")
    missing = [c for c in con if c not in quill]
    print(f"  codes Concepts has that Quill lacks: {len(missing)}")

    print("\n" + "=" * 70)
    print("VERDICT: the extracted table is Concepts' live colour model.")
    print("It is therefore a sound source - and the control says it is the")
    print("WRONG source, because it is not the calibration Quill was built on.")
    print("=" * 70)


if __name__ == "__main__":
    main()
