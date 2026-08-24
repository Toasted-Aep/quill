import re, csv, math, collections
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
cur = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+):([0-9a-fA-F]{6})', tok)
        if m: cur[m.group(1)] = m.group(2).lower()
def h2r(h):
    h=h.lstrip('#'); return [int(h[i:i+2],16) for i in (0,2,4)]
def fam(c):
    i=0
    while i<len(c) and c[i].isalpha(): i+=1
    return c[:i]
def num(c):
    n=c[len(fam(c)):]
    # copic ordering: 0000 < 000 < 00 < 01 < 02 ... ; sort by (leading zeros desc, value)
    return (-len(n) if n.startswith('0') and len(n)>1 else 0, int(n))
rows = [{k.strip():(v or '').strip() for k,v in r.items()}
        for r in csv.DictReader(open('scratchpad/meodai_copic.csv', encoding='utf-8'))]
FLU = {'FV':'FV2','FB':'FB2','FBG':'FBG2','FRV':'FRV1','FY':'FY1','FYR':'FYR1','FG':'FYG1','FYG':'FYG2'}
off = {FLU.get(r['number'].replace('-',''),r['number'].replace('-','')): r for r in rows}
palette = {c for c in cur}
missing = sorted(set(off)-palette, key=lambda c:(fam(c),num(c)))

# within-family consecutive step sizes in the palette
byf=collections.defaultdict(list)
for c in cur:
    if fam(c): byf[fam(c)].append(c)
steps=[]
for f,cs in byf.items():
    cs=sorted(cs,key=num)
    for a,b in zip(cs,cs[1:]): steps.append(math.dist(h2r(cur[a]),h2r(cur[b])))
steps.sort()
print(f"palette within-family consecutive step: median {steps[len(steps)//2]:.1f}  mean {sum(steps)/len(steps):.1f}  p25 {steps[len(steps)//4]:.1f}")

# for each missing code: does it have same-family palette neighbours bracketing it?
print(f"\n{'code':8s} {'family cells in palette':>22s}  bracketed?")
for f in sorted({fam(c) for c in missing}):
    ms=[c for c in missing if fam(c)==f]
    present=sorted([c for c in cur if fam(c)==f], key=num)
    br=[]
    for m in ms:
        lo=[p for p in present if num(p)<num(m)]; hi=[p for p in present if num(p)>num(m)]
        br.append(f"{m}{'*' if (lo and hi) else ''}")
    print(f"{f:5s} n_missing={len(ms):2d} n_present={len(present):2d}  {' '.join(br)}")
print("\n* = has calibrated palette neighbours on both sides within its family")
