import re, csv, collections
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
cur = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+|White|Black):([0-9a-fA-F]{6})', tok)
        if m: cur[m.group(1)] = m.group(2).lower()

rows = [{k.strip():(v or '').strip() for k,v in r.items()}
        for r in csv.DictReader(open('scratchpad/meodai_copic.csv', encoding='utf-8'))]
official = {r['number'].replace('-',''): r for r in rows}   # C-5 -> C5
# fluorescent alias: official (new) -> palette (old)
FLU = {'FV':'FV2','FB':'FB2','FBG':'FBG2','FRV':'FRV1','FY':'FY1','FYR':'FYR1',
       'FG':'FYG1','FYG':'FYG2'}
official = { FLU.get(k,k): v for k,v in official.items() }

print("official codes:", len(official))
palette_markers = {k for k in cur if k not in ('White','Black')}
print("palette marker codes:", len(palette_markers))

missing = sorted(set(official) - palette_markers)
fake    = sorted(palette_markers - set(official))
def fam(c):
    i=0
    while i<len(c) and c[i].isalpha(): i+=1
    return c[:i] or '#'
def key(c):
    f=fam(c); n=c[len(f):]
    return (f, len(n), n)
print(f"\nMISSING real Sketch codes: {len(missing)}")
byf=collections.defaultdict(list)
for c in missing: byf[fam(c)].append(c)
for f in sorted(byf):
    print(f"  {f:4s} {len(byf[f]):2d}  " + '  '.join(f"{c}({official[c]['name']})" for c in sorted(byf[f], key=key)))
print(f"\nNOT-REAL codes currently in the palette: {len(fake)}")
for c in sorted(fake, key=key): print(f"  {c}  {cur[c]}")
print(f"\ncheck: {len(palette_markers)} - {len(fake)} + {len(missing)} = {len(palette_markers)-len(fake)+len(missing)}")
