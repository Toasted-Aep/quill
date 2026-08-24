import re, sys, collections
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
toks = re.findall(r'"([^"]*)"', src)
codes = []
for t in toks:
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+|White|Black):([0-9a-fA-F]{6})', tok)
        if m:
            codes.append((m.group(1), m.group(2)))
seen = collections.Counter(c for c,_ in codes)
dups = [c for c,n in seen.items() if n>1]
print("total tokens:", len(codes), "unique:", len(seen))
print("dups:", dups)
def fam(c):
    i=0
    while i<len(c) and c[i].isalpha(): i+=1
    return c[:i] or 'Core'
byfam = collections.defaultdict(list)
for c,h in codes: byfam[fam(c)].append(c)
for f in sorted(byfam):
    print(f"{f:6s} {len(byfam[f]):3d}  {' '.join(sorted(set(byfam[f])))}")
