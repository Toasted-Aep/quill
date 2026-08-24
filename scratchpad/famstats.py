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
def fam(c):
    i=0
    while i<len(c) and c[i].isalpha(): i+=1
    return c[:i]
MISSING_FAMS={'B','BG','BV','G','R','RV','Y','YG','YR'}
print(f"{'fam':4s} {'n':>3s} {'dSat':>7s} {'dVal':>7s} {'dHue':>7s} {'meanDist':>9s}")
for f in sorted(MISSING_FAMS):
    ds=[];dv=[];dh=[];dd=[]
    for c in cur:
        if fam(c)!=f or c not in off: continue
        o=off[c]['extractedColor']
        if len(o.lstrip('#'))!=6: continue
        a=h2r(cur[c]); b=h2r(o)
        h1,s1,v1=colorsys.rgb_to_hsv(*a); h2,s2,v2=colorsys.rgb_to_hsv(*b)
        ds.append(s1-s2); dv.append(v1-v2)
        d=(h1-h2)*360; d=(d+180)%360-180; dh.append(d)
        dd.append(math.dist([x*255 for x in a],[x*255 for x in b]))
    n=len(ds)
    print(f"{f:4s} {n:3d} {sum(ds)/n:+7.3f} {sum(dv)/n:+7.3f} {sum(dh)/n:+7.1f} {sum(dd)/n:9.1f}")
