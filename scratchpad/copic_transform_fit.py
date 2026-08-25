#!/usr/bin/env python3
"""Last resort: can a LEARNED transform carry Concepts into Quill's calibration?

copic_control.py shows Concepts is a different calibration from Quill's. That
alone does not close the door, because a different calibration is still usable
if the difference is *systematic* - if the user applied some consistent recipe
(the calibration commit says outright that Earth was darkened wholesale), then
308 paired codes are plenty to learn that recipe and apply it to the absent 49.

copic_famfit.py asked a related but weaker question: how far a source sits from
the calibrated hexes RAW. This asks whether the residual can be fitted away.

Method: per family, an affine map (3x4) from Concepts RGB to Quill RGB, fitted
by least squares and scored by 5-fold cross-validation, so every residual is
measured on codes the fit never saw. A fit that only works in-sample would be
memorising 308 hand-picked colours, which is exactly the thing that must not be
mistaken for a calibration.

Result: it does not work. Pooled cross-validated residual falls from 35.9 to
29.4 RGB units - still half a marker step - and three families (C, W, Y) get
WORSE under the fit than under no fit at all. The transform is not systematic
because there is no transform: copicColors.js was assembled colour by colour.

Run from the repo root:  python scratchpad/copic_transform_fit.py
"""
import json, os, re, statistics as st
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "src", "Quill", "Models", "CopicPalette.cs")
CON = os.path.join(HERE, "copic_out", "concepts_palette.json")
TOK = re.compile(r'\b([A-Za-z]*\d[0-9A-Za-z]*|White|Black)\s*:\s*([0-9a-fA-F]{6})\b')
STEP = 58.1     # median RGB distance between adjacent Concepts markers

vec = lambda h: np.array([int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16)], float)
fam = lambda c: (re.match(r'^[A-Z]*', c).group(0) or 'Core')
aug = lambda A: np.hstack([A, np.ones((len(A), 1))])


def cv(X, Y, idx, k=5, seed=0):
    idx = np.array(idx)
    if len(idx) < 6:
        return None
    np.random.default_rng(seed).shuffle(idx)
    res = []
    for f in range(k):
        te = idx[f::k]
        tr = np.array([i for i in idx if i not in set(te.tolist())])
        if len(tr) < 4:
            return None
        M, *_ = np.linalg.lstsq(aug(X[tr]), Y[tr], rcond=None)
        res += list(np.linalg.norm(aug(X[te]) @ M - Y[te], axis=1))
    return res


def main():
    con = json.load(open(CON))
    quill = {m.group(1): m.group(2).lower()
             for m in TOK.finditer(open(SRC, encoding='utf-8').read())}
    sh = sorted(set(con) & set(quill))
    X = np.array([vec(con[c]) for c in sh])
    Y = np.array([vec(quill[c]) for c in sh])
    F = [fam(c) for c in sh]

    print("Cross-validated affine fit, Concepts -> Quill (residual on unseen codes)\n")
    print(f"  {'fam':<5} {'n':>4} {'raw':>8} {'fitted':>8} {'change':>8}")
    raw_all, fit_all, worse = [], [], []
    for f in sorted(set(F)):
        idx = [i for i, x in enumerate(F) if x == f]
        raw = [float(np.linalg.norm(X[i] - Y[i])) for i in idx]
        raw_all += raw
        r = cv(X, Y, idx)
        if r is None:
            print(f"  {f:<5} {len(idx):>4} {st.median(raw):>8.1f} {'n/a':>8}")
            continue
        fit_all += r
        delta = st.median(raw) - st.median(r)
        if delta < 0:
            worse.append(f)
        print(f"  {f:<5} {len(idx):>4} {st.median(raw):>8.1f} "
              f"{st.median(r):>8.1f} {delta:>+8.1f}")

    g = cv(X, Y, list(range(len(sh))))
    print(f"\n  no fit (raw Concepts)        : {st.median(raw_all):6.1f}"
          f"   = {st.median(raw_all)/STEP:.2f} marker steps")
    print(f"  single global affine fit     : {st.median(g):6.1f}"
          f"   = {st.median(g)/STEP:.2f} marker steps")
    print(f"  per-family fits, pooled      : {st.median(fit_all):6.1f}"
          f"   = {st.median(fit_all)/STEP:.2f} marker steps")
    print(f"\n  families made WORSE by fitting: {worse}")
    print("\n  A usable fit would have to land near the ~5-10 RGB units that "
          "separate\n  Quill's closest agreements. It lands at 29.4. There is no "
          "recipe to\n  recover because the palette was assembled by hand, "
          "colour by colour.")


if __name__ == "__main__":
    main()
