"""COPIC palette audit — the factual picture, recomputed from source every run.

Run from the repo root:  python scratchpad/copic_audit.py

Answers, in order:
  1. Is CopicPalette.cs still a byte-exact port of the user's web copicColors.js?
     (The calibration commit 99d8330 says the 316 were lifted verbatim from it,
     so this is the check that the calibration route is still intact.)
  2. Which real Sketch codes are absent, and which present codes are not real?
  3. How well does each candidate hex source agree with the calibrated set?
     This is the question that decides whether a source may be used for the
     absent codes: a source that disagrees with the calibrated 316 will sit
     visibly wrong beside them, however defensible its hex is in isolation.
  4. What is the wheel's extent (MaxRings = deepest column), and how much
     headroom does each column have before it becomes the deepest?
"""
import csv, re, math, collections, os, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PAL = os.path.join(ROOT, 'src', 'Quill', 'Models', 'CopicPalette.cs')
WEB = r"C:/Users/irony/Downloads/New folder (4)/Concepts/src/utils/copicColors.js"
CSV = os.path.join(ROOT, 'scratchpad', 'meodai_copic.csv')
AJ = os.path.join(ROOT, 'scratchpad', 'aj_index.js')

TOKEN = re.compile(r'([A-Za-z]*\d+|White|Black):([0-9a-fA-F]{6})')


def read(p):
    with open(p, encoding='utf-8') as f:
        return f.read()


def palette_codes():
    """code -> hex, in file order, from the C# source."""
    out = {}
    for t in re.findall(r'"([^"]*)"', read(PAL)):
        if ':' not in t:
            continue
        for tok in t.split():
            m = TOKEN.fullmatch(tok)
            if m:
                out[m.group(1)] = m.group(2).lower()
    return out


def sectors():
    """[(id, name, [(a0, a1, [(code, hex), ...]), ...]), ...] — the outer tiers."""
    m = re.search(r'SectorsRaw\s*=\s*\{(.*?)\n    \};', read(PAL), re.S)
    out = []
    for sid, name, slices in re.findall(
            r'\("([^"]+)",\s*"([^"]+)",\s*new \(double, double, string\)\[\]\s*\{(.*?)\n        \}\)',
            m.group(1), re.S):
        sl = [(float(a0), float(a1), [tuple(t.split(':')) for t in data.split()])
              for a0, a1, data in re.findall(r'\(([-\d.]+),\s*([-\d.]+),\s*"([^"]*)"\)', slices)]
        out.append((sid, name, sl))
    return out


def web_codes():
    out = {}
    for code, hexv in re.findall(r"code:\s*'([^']+)'\s*,\s*hex:\s*'#([0-9a-fA-F]{6})'", read(WEB)):
        out[code] = hexv.lower()
    return out


# The fluorescents are numbered differently by Copic (FV) and by the wheel (FV2).
FLU = {'FV': 'FV2', 'FB': 'FB2', 'FBG': 'FBG2', 'FRV': 'FRV1',
       'FY': 'FY1', 'FYR': 'FYR1', 'FG': 'FYG1', 'FYG': 'FYG2'}


def official():
    rows = [{k.strip(): (v or '').strip() for k, v in r.items()}
            for r in csv.DictReader(open(CSV, encoding='utf-8'))]
    out = {}
    for r in rows:
        k = r['number'].replace('-', '')
        out[FLU.get(k, k)] = r
    return out


def aj_codes():
    js = read(AJ)
    return {FLU.get(k, k): v.lower()
            for k, v in re.findall(r"code: '([^']+)',[\s\S]*?hex: '([0-9A-Fa-f]{6})'", js)}


def fam(c):
    i = 0
    while i < len(c) and c[i].isalpha():
        i += 1
    return c[:i] or 'Core'


def numkey(c):
    n = c[len(fam(c)):] if fam(c) != 'Core' else c
    if not n.isdigit():
        return (0, 0)
    return (-len(n) if n.startswith('0') and len(n) > 1 else 0, int(n))


def h2r(h):
    h = h.lstrip('#')
    return [int(h[i:i + 2], 16) for i in (0, 2, 4)]


def dist(a, b):
    return math.dist(h2r(a), h2r(b))


def main():
    cur = palette_codes()
    web = web_codes()

    print("=" * 72)
    print("1. CALIBRATION ROUTE — is the palette still the web file, verbatim?")
    print("=" * 72)
    only_web = sorted(set(web) - set(cur))
    only_cs = sorted(set(cur) - set(web))
    bad = {k: (web[k], cur[k]) for k in set(web) & set(cur) if web[k] != cur[k]}
    print(f"  web codes {len(web)}   palette codes {len(cur)}")
    print(f"  in web not palette: {only_web or 'none'}")
    print(f"  in palette not web: {only_cs or 'none'}")
    print(f"  hex mismatches:     {len(bad)}  {bad if bad else ''}")
    intact = not only_web and not only_cs and not bad
    print(f"  => calibration route {'INTACT (byte-exact port)' if intact else 'BROKEN'}")

    off = official()
    markers = {k for k in cur if k not in ('White', 'Black')}
    missing = sorted(set(off) - markers, key=lambda c: (fam(c), numkey(c)))
    notreal = sorted(markers - set(off), key=lambda c: (fam(c), numkey(c)))

    print()
    print("=" * 72)
    print("2. CODE COVERAGE against the 358 Sketch range")
    print("=" * 72)
    print(f"  official Sketch codes      {len(off)}")
    print(f"  palette marker codes       {len(markers)}   (excludes White/Black)")
    print(f"  of those, real Sketch      {len(markers) - len(notreal)}")
    print(f"  ABSENT real codes          {len(missing)}")
    print(f"  present but NOT real       {len(notreal)}")
    print(f"  check: {len(markers)} - {len(notreal)} + {len(missing)} = "
          f"{len(markers) - len(notreal) + len(missing)}")
    byf = collections.defaultdict(list)
    for c in missing:
        byf[fam(c)].append(c)
    print("\n  absent, by family:")
    for f in sorted(byf):
        print(f"    {f:4s} {len(byf[f]):2d}  " +
              '  '.join(f"{c}({off[c]['name']})" for c in byf[f]))
    print("\n  present but not a real Sketch code:")
    for c in notreal:
        print(f"    {c:8s} #{cur[c]}")

    print()
    print("=" * 72)
    print("3. SOURCE AGREEMENT with the calibrated set")
    print("=" * 72)
    print("   A source may only supply absent codes if it agrees with the 316")
    print("   already calibrated. Compare each candidate against the palette's")
    print("   own within-family step: two ADJACENT markers in a family differ by")
    print("   this much, so an error near it means a swatch lands on top of its")
    print("   neighbour.")
    steps = []
    byfam_cur = collections.defaultdict(list)
    for c in markers:
        byfam_cur[fam(c)].append(c)
    for f, cs in byfam_cur.items():
        cs = sorted(cs, key=numkey)
        for a, b in zip(cs, cs[1:]):
            steps.append(dist(cur[a], cur[b]))
    steps.sort()
    step_med = steps[len(steps) // 2]
    print(f"\n   palette within-family adjacent step: median {step_med:.1f} RGB units\n")

    cands = {
        'meodai .hex': {k: v['hex'].lstrip('#').lower() for k, v in off.items()},
        'meodai .extractedColor': {k: v['extractedColor'].lstrip('#').lower() for k, v in off.items()},
        'alexandrejunqueira': aj_codes(),
    }
    print(f"   {'source':24s} {'shared':>6s} {'exact':>6s} {'median':>7s} {'mean':>7s} "
          f"{'<10':>5s} {'>half-step':>11s}")
    for name, ds in cands.items():
        shared = [c for c in markers if c in ds and len(ds[c]) == 6]
        if not shared:
            continue
        dl = sorted(dist(cur[c], ds[c]) for c in shared)
        exact = sum(1 for c in shared if ds[c] == cur[c])
        over = sum(1 for x in dl if x > step_med / 2)
        print(f"   {name:24s} {len(shared):6d} {exact:6d} {dl[len(dl) // 2]:7.1f} "
              f"{sum(dl) / len(dl):7.1f} {sum(1 for x in dl if x < 10):5d} "
              f"{100 * over / len(dl):10.0f}%")
    print("\n   'exact' is the number of codes where the source reproduces the")
    print("   calibrated hex. A source that IS the calibration origin would show")
    print("   exact == shared.")

    print()
    print("=" * 72)
    print("4. WHEEL EXTENT — the outer edge is an accumulation of MaxRings")
    print("=" * 72)
    secs = sectors()
    depths = [(name, a0, a1, len(cells)) for _, name, sl in secs for a0, a1, cells in sl]
    mx = max(d for _, _, _, d in depths)
    print(f"  MaxRings (deepest column) = {mx}")
    print("  ColorWheel.cs sizes the wheel from MaxRings, so the extent grows ONLY")
    print("  if a column passes the current deepest. Depth per column:\n")
    for name, a0, a1, d in depths:
        flag = '  <-- deepest' if d == mx else ''
        print(f"    {name:14s} [{a0:>5g},{a1:>5g})  {d:2d}{flag}")
    print(f"\n  headroom before the extent moves: a column may reach {mx} without")
    print("  changing the wheel's outer radius at all.")


if __name__ == '__main__':
    main()
