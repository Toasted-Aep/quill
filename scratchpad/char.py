import re, csv, math, colorsys, collections
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
cur = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+):([0-9a-fA-F]{6})', tok)
        if m: cur[m.group(1)] = m.group(2).lower()
rows = [{k.strip():(v or '').strip() for k,v in r.items()}
        for r in csv.DictReader(open('scratchpad/meodai_copic.csv', encoding='utf-8'))]
FLU = {'FV':'FV2','FB':'FB2','FBG':'FBG2','FRV':'FRV1','FY':'FY1','FYR':'FYR1','FG':'FYG1','FYG':'FYG2'}
off = {FLU.get(r['number'].replace('-',''),r['number'].replace('-','')): r for r in rows}
def h2r(h):
    h=h.lstrip('#'); return tuple(int(h[i:i+2],16)/255 for i in (0,2,4))
def hsv(h): return colorsys.rgb_to_hsv(*h2r(h))
shared=[c for c in cur if c in off]
for src_name,fld in (('official hex','hex'),('swatch extract','extractedColor')):
    ds=[]; dv=[]; dh=[]
    for c in shared:
        o=off[c][fld]
        if len(o.lstrip('#'))!=6: continue
        h1,s1,v1=hsv(cur[c]); h2,s2,v2=hsv(o)
        ds.append(s1-s2); dv.append(v1-v2)
        d=(h1-h2)*360
        d=(d+180)%360-180
        dh.append(d)
    n=len(ds)
    print(f"{src_name:15s} n={n}  palette-minus-source:  dSat {sum(ds)/n:+.3f}   dVal {sum(dv)/n:+.3f}   dHue {sum(dh)/n:+.1f}deg")
print()
# sample family table
for f in ('B','BG','RV'):
    cs=sorted([c for c in cur if re.fullmatch(rf'{f}\d+',c)], key=lambda c:(len(c),c))[:8]
    print(f"-- {f} --   {'palette':>9s} {'official':>9s} {'extract':>9s}")
    for c in cs:
        o=off.get(c,{})
        print(f"   {c:7s} {'#'+cur[c]:>9s} {o.get('hex','-'):>9s} {o.get('extractedColor','-'):>9s}")
