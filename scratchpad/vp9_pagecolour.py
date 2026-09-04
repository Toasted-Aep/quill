"""Pick a page background that is NOT a Copic swatch and is far from every one
of them, so 'page showing through' can never be confused with a real tile.
Run 10 lost two readings to #E10619, which is R29 exactly."""
import re, os

root = r'C:\Users\irony\Downloads\Quill Gem - Fable\Quill'
src = os.path.join(root, 'src', 'Quill', 'Models', 'CopicPalette.cs')
txt = open(src, 'r', encoding='utf-8', errors='replace').read()

hexes = set()
for m in re.finditer(r'([A-Za-z]{1,2}\d{1,5})\s*:\s*#?([0-9a-fA-F]{6})', txt):
    hexes.add(m.group(2).lower())
print('palette hexes found:', len(hexes))


def rgb(h):
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16))


cands = ['ff00ff', '00ff00', '00ffff', 'ff0080', '7f00ff', '00ff80', 'e10619']
for c in cands:
    cr = rgb(c)
    best, bestcode = 10**9, None
    for h in hexes:
        hr = rgb(h)
        d = sum((a - b) ** 2 for a, b in zip(cr, hr))
        if d < best:
            best, bestcode = d, h
    # find the code for that hex
    code = '?'
    m = re.search(r'([A-Za-z]{1,2}\d{1,5})\s*:\s*#?' + bestcode, txt, re.I)
    if m:
        code = m.group(1)
    print('#%s  exact-in-palette=%-5s  nearest %s (#%s) dist=%.1f'
          % (c.upper(), c in hexes, code, bestcode.upper(), best ** 0.5))
