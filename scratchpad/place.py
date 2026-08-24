import re, csv, collections, json
SRC='src/Quill/Models/CopicPalette.cs'
src=open(SRC,encoding='utf-8').read()
m=re.search(r'SectorsRaw\s*=\s*\{(.*?)\n    \};', src, re.S)
sectors=[]
for sid,name,slices in re.findall(r'\("([^"]+)",\s*"([^"]+)",\s*new \(double, double, string\)\[\]\s*\{(.*?)\n        \}\)', m.group(1), re.S):
    sl=[[float(a0),float(a1),[t.split(':') for t in data.split()]]
        for a0,a1,data in re.findall(r'\(([-\d.]+),\s*([-\d.]+),\s*"([^"]*)"\)', slices)]
    sectors.append([sid,name,sl])
rows=[{k.strip():(v or '').strip() for k,v in r.items()}
      for r in csv.DictReader(open('scratchpad/meodai_copic.csv',encoding='utf-8'))]
off={r['number'].replace('-',''):r for r in rows}

def parse(code):
    i=0
    while i<len(code) and code[i].isalpha(): i+=1
    f,d=code[:i],code[i:]
    rest=d[1:]
    br = 0 if rest=='' else (-len(rest) if set(rest)=={'0'} else int(rest))
    return f,int(d[0]),br
def ckey(c):
    _,s,b=parse(c); return (s,b)
def lum(h):
    r,g,b=[int(h[i:i+2],16)/255 for i in (0,2,4)]
    f=lambda c: c/12.92 if c<=0.04045 else ((c+0.055)/1.055)**2.4
    return 0.2126*f(r)+0.7152*f(g)+0.0722*f(b)

# column assignment: family -> slice index (0-based within that sector)
NEW = {
 'B99':('blue',2),'B97':('blue',2),'B95':('blue',2),'B93':('blue',2),'B91':('blue',2),
 'BG09':('blue-green',0),'BG13':('blue-green',0),'BG23':('blue-green',0),
 'BG07':('blue-green',1),'BG11':('blue-green',1),'BG18':('blue-green',1),
 'BG05':('blue-green',2),'BG10':('blue-green',2),'BG15':('blue-green',2),
 'BG01':('blue-green',3),'BG02':('blue-green',3),'BG000':('blue-green',3),'BG0000':('blue-green',3),
 'BV13':('blue-violet',1),'BV11':('blue-violet',1),
 'G29':('green',1),'G40':('green',2),'G82':('green',0),'G85':('green',0),'G94':('green',0),
 'R05':('red',1),'R02':('red',2),'R30':('red',2),
 'RV55':('red-violet',0),'RV52':('red-violet',0),'RV42':('red-violet',0),
 'RV34':('red-violet',1),'RV32':('red-violet',1),'RV21':('red-violet',1),
 'RV69':('red-violet',2),'RV66':('red-violet',2),'RV63':('red-violet',2),
 'RV99':('red-violet',2),'RV95':('red-violet',2),'RV93':('red-violet',2),'RV91':('red-violet',2),
 'Y32':('yellow',2),
 'YG61':('yellow-green',0),'YG06':('yellow-green',2),
 'YR61':('yellow-red',0),
 'YR12':('yellow-red',1),'YR14':('yellow-red',1),
 'YR20':('yellow-red',2),'YR21':('yellow-red',2),
}
assert len(NEW)==49, len(NEW)
byslice=collections.defaultdict(list)
for code,(sid,si) in NEW.items(): byslice[(sid,si)].append(code)
sec_by_id={s[0]:s for s in sectors}
for (sid,si) in byslice:
    assert sid in sec_by_id, sid
    assert si < len(sec_by_id[sid][2]), (sid,si,len(sec_by_id[sid][2]))

def inversions(L):
    return sum(1 for i in range(len(L)-1) if L[i]>L[i+1])

inserted=0
for sid,name,sl in sectors:
    for si,(a0,a1,cells) in enumerate(sl):
        add=byslice.get((sid,si))
        if not add: continue
        # insert deepest-Copic first so same-family runs settle in order
        for code in sorted(add, key=ckey, reverse=True):
            hexv=off[code]['extractedColor'].lstrip('#').lower()
            assert len(hexv)==6,(code,hexv)
            L=[lum(h) for _,h in cells]; base=inversions(L)
            best=None
            for pos in range(len(cells)+1):
                cand=L[:pos]+[lum(hexv)]+L[pos:]
                inv=inversions(cand)-base
                # tiebreak: keep same-letter-family members in Copic order
                same=[(i,c) for i,(c,_) in enumerate(cells) if parse(c)[0]==parse(code)[0]]
                bad=sum(1 for i,c in same if (i<pos and ckey(c)<ckey(code)) or (i>=pos and ckey(c)>ckey(code)))
                sc=(inv, bad, abs(pos-len(cells)/2))
                if best is None or sc<best[0]: best=(sc,pos)
            cells.insert(best[1],[code,hexv]); inserted+=1
assert inserted==49, inserted
depth=max(len(c) for _,_,sl in sectors for _,_,c in sl)
print("deepest column after additions:", depth, "(was 17)")
allcodes=[c for _,_,sl in sectors for _,_,cc in sl for c,_ in cc]
assert len(allcodes)==len(set(allcodes))
for sid,name,sl in sectors:
    if not any((sid,si) in byslice for si in range(len(sl))): continue
    print(f"\n{name}:")
    for si,(a0,a1,cells) in enumerate(sl):
        print(f"  ({a0:g},{a1:g}) n={len(cells):2d}  " + ' '.join((f"[{c}]" if c in NEW else c) for c,_ in cells))
json.dump(sectors, open('scratchpad/sectors_after.json','w'))
