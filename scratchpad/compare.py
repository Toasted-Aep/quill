import re, csv, collections
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
cur = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+):([0-9a-fA-F]{6})', tok)
        if m: cur[m.group(1)] = m.group(2).lower()

rows = list(csv.DictReader(open('scratchpad/meodai_copic.csv', encoding='utf-8')))
rows = [{k.strip():(v or '').strip() for k,v in r.items()} for r in rows]
print("csv rows:", len(rows))
ds = {r['number']: r for r in rows}
print("csv unique codes:", len(ds))

exact_hex = sum(1 for c in cur if c in ds and ds[c]['hex'].lstrip('#').lower()==cur[c])
exact_ext = sum(1 for c in cur if c in ds and ds[c]['extractedColor'].lstrip('#').lower()==cur[c])
shared = [c for c in cur if c in ds]
print(f"shared codes: {len(shared)}  exact match on 'hex': {exact_hex}  on 'extractedColor': {exact_ext}")

# mean distance
def d(a,b):
    a=[int(a[i:i+2],16) for i in (0,2,4)]; b=[int(b[i:i+2],16) for i in (0,2,4)]
    return sum((x-y)**2 for x,y in zip(a,b))**0.5
for col in ('hex','extractedColor'):
    ds_ = [d(cur[c], ds[c][col].lstrip('#').lower()) for c in shared if len(ds[c][col].lstrip('#'))==6]
    ds_.sort()
    print(f"  {col}: mean dist {sum(ds_)/len(ds_):.1f}  median {ds_[len(ds_)//2]:.1f}  max {ds_[-1]:.1f}")

missing = [c for c in ds if c not in cur]
extra   = [c for c in cur if c not in ds]
print("\nIn CSV but NOT in palette:", len(missing))
def fam(c):
    i=0
    while i<len(c) and c[i].isalpha(): i+=1
    return c[:i] or '#'
def key(c):
    f=fam(c); n=c[len(f):] if f!='#' else c
    return (f, len(n), n)
byf=collections.defaultdict(list)
for c in missing: byf[fam(c)].append(c)
for f in sorted(byf): print(f"  {f:5s} {len(byf[f]):3d}  {' '.join(sorted(byf[f], key=key))}")
print("\nIn palette but NOT in CSV:", len(extra), sorted(extra, key=key))
