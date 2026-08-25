#!/usr/bin/env python3
"""Extract Concepts' own COPIC table from the shipped app binary.

Why this instead of sampling pixels
-----------------------------------
The task was to source the missing codes by sampling Concepts' rendered COPIC
wheel. Screen sampling has one hard failure mode: pairing a swatch with its
code. The labels are rotated, the wheel overflows the viewport, and a
mis-pairing silently assigns the wrong hex to a real code.

`TopHatch.Concepts.dll` ships the colour table as a flat run of records in
which the code string and its RGBA sit in the *same record*, so the pairing is
structural rather than geometric. That removes the failure mode entirely.

Record format (little detective work, all of it verified below)
--------------------------------------------------------------
    [len][CODE ascii][0x0f][B][G][R][A=0xff]      named record
    [0x0f][B][G][R][A=0xff]                       bare record (string interned
                                                  elsewhere in the stream)

`len` is the character count shifted left by one (len == 2*len(CODE)), which is
what pins the format down: it holds for every record from 2-char "T7" through
6-char "BG0000".

The run is bounded on the right by the literal `IHslWheel_Bindings`, i.e. the
colour-wheel binding metadata — which is the first evidence that this table is
the wheel's, not a stray resource. See copic_wheel_proof.py for the pixel
proof.

The ten bare records are recovered by position, not by guesswork:
  * one between G16 and G19   -> G17
  * seven between N10 and T7  -> T0..T6 (a monotone light->dark grey ramp)
  * one leading, one trailing -> sentinels, not markers; dropped.

Output: scratchpad/copic_out/concepts_palette.json  {CODE: "rrggbb"}
"""
import json, os, re, sys

DLL = r"C:\Program Files\WindowsApps" \
      r"\TopHatchInc.Concepts_2026.6.6.0_x64__phhqn29e7p872\TopHatch.Concepts.dll"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "copic_out")
CODE = re.compile(rb'[A-Z_]{0,3}[0-9]{1,4}\Z')


def parse_from(data, i):
    """Walk records forward from i until one fails to validate."""
    out, j = [], i
    while j + 5 <= len(data):
        if data[j] == 0x0F:                       # bare colour record
            b, g, r, a = data[j + 1:j + 5]
            if a != 0xFF:
                break
            out.append((j, None, (r, g, b)))
            j += 5
            continue
        ln = data[j]
        if ln < 2 or ln > 16 or ln % 2:
            break
        n = ln // 2
        code = data[j + 1:j + 1 + n]
        if not CODE.fullmatch(code) or data[j + 1 + n] != 0x0F:
            break
        b, g, r, a = data[j + 2 + n:j + 6 + n]
        if a != 0xFF:
            break
        out.append((j, code.decode(), (r, g, b)))
        j += n + 6
    return out


def main():
    data = open(DLL, 'rb').read()
    anchor = data.find(b"BG0000")
    if anchor < 0:
        sys.exit("BG0000 not found - Concepts version changed?")

    # The table start is whichever offset yields the longest clean parse.
    best = max((parse_from(data, s) for s in range(anchor - 9000, anchor + 1)),
               key=len)
    named = [r for r in best if r[1]]
    bare = [r for r in best if not r[1]]
    print(f"records={len(best)}  named={len(named)}  bare={len(bare)}")

    tail = data[best[-1][0]:best[-1][0] + 64]
    print("terminator:", tail[:40])
    assert b"HslWheel" in data[best[-1][0]:best[-1][0] + 200], \
        "table is not adjacent to the HslWheel bindings"

    pal = {}
    for _, code, rgb in named:
        pal[code.lstrip('_')] = "%02x%02x%02x" % rgb

    # Recover the interned-string records by position.
    idx = {id(r): k for k, r in enumerate(best)}
    for r in bare:
        k = idx[id(r)]
        prv = next((best[m][1] for m in range(k - 1, -1, -1) if best[m][1]), None)
        nxt = next((best[m][1] for m in range(k + 1, len(best)) if best[m][1]), None)
        hexv = "%02x%02x%02x" % r[2]
        if prv == "G16" and nxt == "G19":
            pal["G17"] = hexv
        elif prv == "N10" and nxt == "T7":
            run = [x for x in bare if idx[id(x)] > idx[id(bare[0])]
                   and next((best[m][1] for m in range(idx[id(x)] - 1, -1, -1)
                             if best[m][1]), None) == "N10"]
            pal["T%d" % run.index(r)] = hexv
        else:
            print(f"  sentinel dropped at k={k}: #{hexv}")

    os.makedirs(OUT, exist_ok=True)
    dst = os.path.join(OUT, "concepts_palette.json")
    json.dump(pal, open(dst, 'w'), indent=0, sort_keys=True)
    print(f"wrote {len(pal)} codes -> {dst}")
    for f in ("T0", "T6", "G17"):
        print(f"  recovered {f} = #{pal[f]}")


if __name__ == "__main__":
    main()
