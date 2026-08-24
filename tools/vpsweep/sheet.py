import os, sys
import numpy as np
from PIL import Image, ImageDraw

V = os.path.dirname(os.path.abspath(__file__))
base = np.asarray(Image.open(os.path.join(V, 'BASELINE-nogrid.png')).convert('L')).astype(np.int16)

ORDER = [
    ('1-Point', ['1 Point']),
    ('2-Point', ['2 Point', '1/2 Narrow', '1/4 Narrow', 'Side Narrow', '1/2 Wide',
                 '1/4 Wide', 'Side Wide', '1/2 Wide Below', 'Side Ultrawide']),
    ('3-Point', ['3 Point', '3/4 Narrow', '1/2 Narrow', '3/4 Wide', '1/4 Wide',
                 'Side Wide Below', '1/4 Wide Below', '3/4 Ultrawide Below',
                 '3/4 Ultrawide']),
]
items = [(l, n) for l, ns in ORDER for n in ns]

CW, CH = 480, 300          # cell size
COLS = 4
ROWS = (len(items) + COLS - 1) // COLS
sheet = Image.new('RGB', (COLS * CW, ROWS * (CH + 18)), (0, 0, 0))
dr = ImageDraw.Draw(sheet)

for k, (lst, n) in enumerate(items):
    f = os.path.join(V, '%s - %s.png' % (lst, n.replace('/', '_')))
    g = np.asarray(Image.open(f).convert('L')).astype(np.int16)
    m = (g - base) >= 6                       # the grid ink only
    h, w = m.shape
    # max-pool so 1px lines survive the downscale
    fy, fx = h // CH, w // CW
    m = m[:CH * fy, :CW * fx].reshape(CH, fy, CW, fx).max(axis=(1, 3))
    img = Image.fromarray((m * 255).astype(np.uint8)).convert('RGB')
    x0, y0 = (k % COLS) * CW, (k // COLS) * (CH + 18)
    sheet.paste(img, (x0, y0 + 18))
    dr.text((x0 + 4, y0 + 4), '%s  %s' % (lst, n), fill=(255, 210, 90))
    # frame edge
    dr.rectangle([x0, y0 + 18, x0 + CW - 1, y0 + 18 + CH - 1], outline=(60, 60, 60))

sheet.save(os.path.join(V, 'out', 'contact-sheet.png'))
print('wrote out/contact-sheet.png', sheet.size)
