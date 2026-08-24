import re, csv, math, random, collections
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
off = {}
for r in rows:
    k = r['number'].replace('-','')
    k = FLU.get(k,k)
    off[k] = r
def h2r(h):
    h=h.lstrip('#')
    return [int(h[i:i+2],16) for i in (0,2,4)]
def d(a,b): return math.dist(a,b)

pairs_hex = [(h2r(off[c]['hex']), h2r(cur[c]), c) for c in cur if c in off and len(off[c]['hex'].lstrip('#'))==6]
pairs_ext = [(h2r(off[c]['extractedColor']), h2r(cur[c]), c) for c in cur if c in off and len(off[c]['extractedColor'].lstrip('#'))==6]
print("pairs:", len(pairs_hex))

def fit_linear(pairs, idxs):
    # least squares 3x4 (RGB + bias) via normal equations, no numpy
    n=4
    ATA=[[0.0]*n for _ in range(n)]; ATb=[[0.0]*3 for _ in range(n)]
    for i in idxs:
        x,y,_=pairs[i]
        v=[x[0],x[1],x[2],1.0]
        for a in range(n):
            for b_ in range(n): ATA[a][b_]+=v[a]*v[b_]
            for ch in range(3): ATb[a][ch]+=v[a]*y[ch]
    # gaussian elim
    M=[ATA[i][:]+ATb[i][:] for i in range(n)]
    for col in range(n):
        p=max(range(col,n),key=lambda r:abs(M[r][col])); M[col],M[p]=M[p],M[col]
        pv=M[col][col]
        if abs(pv)<1e-9: continue
        M[col]=[v/pv for v in M[col]]
        for r in range(n):
            if r!=col and M[r][col]:
                f=M[r][col]; M[r]=[a-f*b_ for a,b_ in zip(M[r],M[col])]
    return [[M[i][4+ch] for i in range(n)] for ch in range(3)]

def apply(W,x):
    v=[x[0],x[1],x[2],1.0]
    return [min(255,max(0,sum(W[ch][i]*v[i] for i in range(4)))) for ch in range(3)]

for name,pairs in (('copic.jp hex',pairs_hex),('swatch-extracted',pairs_ext)):
    base=sorted(d(x,y) for x,y,_ in pairs)
    print(f"\n== {name} ==  raw: mean {sum(base)/len(base):5.1f}  median {base[len(base)//2]:5.1f}")
    # 5-fold CV global linear
    random.seed(7); idx=list(range(len(pairs))); random.shuffle(idx)
    errs=[]
    for f in range(5):
        te=set(idx[f::5]); tr=[i for i in idx if i not in te]
        W=fit_linear(pairs,tr)
        for i in te: errs.append(d(apply(W,pairs[i][0]),pairs[i][1]))
    errs.sort()
    print(f"   global linear CV: mean {sum(errs)/len(errs):5.1f}  median {errs[len(errs)//2]:5.1f}  p90 {errs[int(.9*len(errs))]:5.1f}")
    # per-family CV
    byf=collections.defaultdict(list)
    def fam(c):
        i=0
        while i<len(c) and c[i].isalpha(): i+=1
        return c[:i]
    for i,(x,y,c) in enumerate(pairs): byf[fam(c)].append(i)
    errs=[]; small=[]
    for f,ii in byf.items():
        if len(ii)<8: small.append(f); continue
        jj=ii[:]; random.shuffle(jj)
        for k in range(4):
            te=set(jj[k::4]); tr=[i for i in jj if i not in te]
            if len(tr)<5: continue
            W=fit_linear(pairs,tr)
            for i in te: errs.append(d(apply(W,pairs[i][0]),pairs[i][1]))
    errs.sort()
    print(f"   per-family CV  : mean {sum(errs)/len(errs):5.1f}  median {errs[len(errs)//2]:5.1f}  p90 {errs[int(.9*len(errs))]:5.1f}  (skipped tiny fams {small})")
