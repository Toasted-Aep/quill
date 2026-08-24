import re
src=open('src/Quill/Models/CopicPalette.cs',encoding='utf-8').read()
m=re.search(r'SectorsRaw\s*=\s*\{(.*?)\n    \};', src, re.S)
def parse(code):
    i=0
    while i<len(code) and code[i].isalpha(): i+=1
    fam, digits = code[:i], code[i:]
    sat=int(digits[0]); rest=digits[1:]
    if rest=='' : br=0
    elif set(rest)=={'0'}: br=-len(rest)
    else: br=int(rest)
    return fam,sat,br
ok=0; tot=0
for sec,name,slices in re.findall(r'\("([^"]+)",\s*"([^"]+)",\s*new \(double, double, string\)\[\]\s*\{(.*?)\n        \}\)', m.group(1), re.S):
    for a0,a1,data in re.findall(r'\(([-\d.]+),\s*([-\d.]+),\s*"([^"]*)"\)', slices):
        codes=[t.split(':')[0] for t in data.split()]
        fams={parse(c)[0] for c in codes}
        keys=[(parse(c)[1],parse(c)[2]) for c in codes]
        desc = all(keys[i]>=keys[i+1] for i in range(len(keys)-1))
        # allow the family-mixing columns to be judged per dominant family
        tot+=1
        if desc: ok+=1
        else:
            main=max(fams,key=lambda f:sum(1 for c in codes if parse(c)[0]==f))
            sub=[c for c in codes if parse(c)[0]==main]
            k2=[(parse(c)[1],parse(c)[2]) for c in sub]
            d2=all(k2[i]>=k2[i+1] for i in range(len(k2)-1))
            a2=all(k2[i]<=k2[i+1] for i in range(len(k2)-1))
            tag = 'DESC(main only)' if d2 else ('ASC(main only)' if a2 else 'ad hoc')
            print(f"  {name:12s} [{a0:>4},{a1:>4}) {tag:16s} {' '.join(codes)}")
print(f"\n{ok}/{tot} columns are exactly descending Copic order over ALL members")
