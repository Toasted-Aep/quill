import re
src=open('src/Quill/Models/CopicPalette.cs',encoding='utf-8').read()
m=re.search(r'SectorsRaw\s*=\s*\{(.*?)\n    \};', src, re.S)
body=m.group(1)
def lum(h):
    r,g,b=[int(h[i:i+2],16)/255 for i in (0,2,4)]
    f=lambda c: c/12.92 if c<=0.04045 else ((c+0.055)/1.055)**2.4
    return 0.2126*f(r)+0.7152*f(g)+0.0722*f(b)
bad=0; tot=0
for sec,name,slices in re.findall(r'\("([^"]+)",\s*"([^"]+)",\s*new \(double, double, string\)\[\]\s*\{(.*?)\n        \}\)', body, re.S):
    for a0,a1,data in re.findall(r'\(([-\d.]+),\s*([-\d.]+),\s*"([^"]*)"\)', slices):
        cells=[t.split(':') for t in data.split()]
        L=[lum(h) for _,h in cells]
        inc = all(L[i]<=L[i+1] for i in range(len(L)-1))
        tot+=1
        if not inc:
            bad+=1
            viol=[f"{cells[i][0]}>{cells[i+1][0]}" for i in range(len(L)-1) if L[i]>L[i+1]]
            print(f"  {name:12s} [{a0},{a1}) n={len(cells):2d} NOT monotonic: {' '.join(viol)}")
print(f"\n{tot-bad}/{tot} columns are strictly dark->light outward")
