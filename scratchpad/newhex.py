import csv
rows=[{k.strip():(v or '').strip() for k,v in r.items()}
      for r in csv.DictReader(open('scratchpad/meodai_copic.csv',encoding='utf-8'))]
off={r['number'].replace('-',''):r for r in rows}
CODES="""B91 B93 B95 B97 B99
BG0000 BG000 BG01 BG02 BG05 BG07 BG09 BG10 BG11 BG13 BG15 BG18 BG23
BV11 BV13
G29 G40 G82 G85 G94
R02 R05 R30
RV21 RV32 RV34 RV42 RV52 RV55 RV63 RV66 RV69 RV91 RV93 RV95 RV99
Y32
YG06 YG61
YR12 YR14 YR20 YR21 YR61""".split()
def lum(h):
    h=h.lstrip('#'); r,g,b=[int(h[i:i+2],16)/255 for i in (0,2,4)]
    f=lambda c: c/12.92 if c<=0.04045 else ((c+0.055)/1.055)**2.4
    return 0.2126*f(r)+0.7152*f(g)+0.0722*f(b)
for c in CODES:
    r=off[c]
    print(f"{c:8s} {r['extractedColor']:8s} L={lum(r['extractedColor']):.3f}   (chart {r['hex']:8s})  {r['name']}")
