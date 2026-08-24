import re, collections
WEB = r"C:/Users/irony/Downloads/New folder (4)/Concepts/src/utils/copicColors.js"
web = open(WEB, encoding='utf-8').read()
w = {}
for code, hexv in re.findall(r"code:\s*'([^']+)'\s*,\s*hex:\s*'#([0-9a-fA-F]{6})'", web):
    if code in w and w[code] != hexv.lower(): print("web dup mismatch", code)
    w[code] = hexv.lower()
src = open('src/Quill/Models/CopicPalette.cs', encoding='utf-8').read()
c = {}
for t in re.findall(r'"([^"]*)"', src):
    if ':' not in t: continue
    for tok in t.split():
        m = re.fullmatch(r'([A-Za-z]*\d+|White|Black):([0-9a-fA-F]{6})', tok)
        if m: c[m.group(1)] = m.group(2).lower()
print("web codes:", len(w), " cs codes:", len(c))
print("in web not cs:", sorted(set(w)-set(c)))
print("in cs not web:", sorted(set(c)-set(w)))
bad = {k:(w[k],c[k]) for k in set(w)&set(c) if w[k]!=c[k]}
print("hex mismatches:", len(bad), bad)
