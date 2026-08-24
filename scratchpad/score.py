import re, csv, json, sys, collections

src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
cur = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+):([0-9a-fA-F]{6})', tok)
        if m: cur[m.group(1)] = m.group(2).lower()

def norm(c):
    return c.replace('-','').upper()

sets = {}
# meodai
rows = [{k.strip():(v or '').strip() for k,v in r.items()} for r in csv.DictReader(open('scratchpad/meodai_copic.csv', encoding='utf-8'))]
sets['meodai.hex']       = {norm(r['number']): r['hex'].lstrip('#').lower() for r in rows}
sets['meodai.extracted'] = {norm(r['number']): r['extractedColor'].lstrip('#').lower() for r in rows}
# alexandrejunqueira
js = open('scratchpad/aj_index.js', encoding='utf-8').read()
aj = dict(re.findall(r"code: '([^']+)',[\s\S]*?hex: '([0-9A-Fa-f]{6})'", js))
sets['aj.hex'] = {norm(k): v.lower() for k,v in aj.items()}

def dist(a,b):
    A=[int(a[i:i+2],16) for i in (0,2,4)]; B=[int(b[i:i+2],16) for i in (0,2,4)]
    return sum((x-y)**2 for x,y in zip(A,B))**0.5

curn = {norm(k):v for k,v in cur.items()}
for name, ds in sets.items():
    shared = [c for c in curn if c in ds and len(ds[c])==6]
    exact = sum(1 for c in shared if ds[c]==curn[c])
    dl = sorted(dist(curn[c], ds[c]) for c in shared)
    print(f"{name:18s} shared={len(shared):3d} exact={exact:3d} mean={sum(dl)/len(dl):6.1f} median={dl[len(dl)//2]:6.1f} <10={sum(1 for x in dl if x<10):3d}")
