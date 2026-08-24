import re, csv, math, colorsys, collections, random
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
def shift(rgb,dh,dsat,dval):
    h,s,v=colorsys.rgb_to_hsv(*rgb)
    h=(h+dh/360.0)%1.0; s=min(1,max(0,s+dsat)); v=min(1,max(0,v+dval))
    return colorsys.hsv_to_rgb(h,s,v)
def dist(a,b): return math.dist([x*255 for x in a],[y*255 for y in b])

FAMS={'B','BG','BV','G','R','RV','Y','YG','YR'}
tot_raw=[]; tot_cor=[]
print(f"{'fam':4s} {'rawCV':>7s} {'corrCV':>7s}  verdict")
for f in sorted(FAMS):
    cs=[c for c in cur if fam(c)==f and c in off and len(off[c]['extractedColor'].lstrip('#'))==6]
    raw=[];cor=[]
    for held in cs:                      # leave-one-out
        tr=[c for c in cs if c!=held]
        dh=[];dsv=[];dvv=[]
        for c in tr:
            a=h2r(cur[c]); b=h2r(off[c]['extractedColor'])
            h1,s1,v1=colorsys.rgb_to_hsv(*a); h2_,s2,v2=colorsys.rgb_to_hsv(*b)
            d=(h1-h2_)*360; d=(d+180)%360-180
            dh.append(d); dsv.append(s1-s2); dvv.append(v1-v2)
        n=len(tr)
        mh,ms,mv=sum(dh)/n,sum(dsv)/n,sum(dvv)/n
        tgt=h2r(cur[held]); srcc=h2r(off[held]['extractedColor'])
        raw.append(dist(srcc,tgt)); cor.append(dist(shift(srcc,mh,ms,mv),tgt))
    r=sum(raw)/len(raw); c_=sum(cor)/len(cor)
    tot_raw+=raw; tot_cor+=cor
    print(f"{f:4s} {r:7.1f} {c_:7.1f}  {'corrected better' if c_<r-0.5 else ('raw better' if r<c_-0.5 else 'tie')}")
print(f"\nALL  {sum(tot_raw)/len(tot_raw):7.1f} {sum(tot_cor)/len(tot_cor):7.1f}")
