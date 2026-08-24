"""Offline render of the COPIC wheel, straight from CopicPalette.cs.

Draws the wheel without launching Quill and without touching the screen, so a
before/after can be reviewed while someone else is using the machine.

  python scratchpad/copic_render.py --out before.png
  python scratchpad/copic_render.py --out after.png  --additions scratchpad/copic_additions.json
  python scratchpad/copic_render.py --out after_marked.png --additions ... --mark

The geometry mirrors Controls/ColorWheel.cs rather than approximating it:

  CellScale 1.9764, one reference unit u = s * CellScale, band = 21u
  r1In 285 -> r1Out +band -> r2In +5u -> r2Out +band -> rOutBase +0.9 band
  rOut  = rOutBase + MaxRings * band          (11.21: an ACCUMULATION, not a target)
  Tier 1  144 deg arc from -128, 3 groups, 4.5 deg dividers, none after the last
  Tier 2  full circle from -90, 4 groups, 5.5 deg divider after every group
  Tier 3+ 36 columns of 10 deg from -90, FamGap 1.7 deg off each family's last
  rotation 100 deg; labels at midAngle - 90, the reference's fixed upright offset

This is a geometry-faithful redraw, NOT a screenshot: it does not reproduce
Win2D's antialiasing, the hub chrome, the recents row or the entrance cascade.
It is for judging colour and placement, which is what the approval gate needs.
"""
import argparse, json, math, os, sys
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A

# ---- constants lifted from ColorWheel.cs --------------------------------
CELL_SCALE = 1.9764
SPINE_GAP = 0.90
FAM_GAP = 1.7           # degrees, off the trailing edge of a family's last column
COL_STEP = 10.0         # degrees
OUTER_START = -90.0     # degrees
ROT = 100.0             # degrees, reference default
TEXT_ELEM = 0.80 * 0.80  # TextScale * SurfaceScale
R1_IN = 285.0
T1_START, T1_SPAN, T1_GAP = -128.0, 144.0, 4.5
T2_START, T2_SPAN, T2_GAP = -90.0, 360.0, 5.5

BG = (0x23, 0x23, 0x23)
FONT = "C:/Windows/Fonts/segoeui.ttf"
FONT_BOLD = "C:/Windows/Fonts/segoeuib.ttf"


def h2rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def lum(rgb):
    def f(c):
        c /= 255
        return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * f(rgb[0]) + 0.7152 * f(rgb[1]) + 0.0722 * f(rgb[2])


def build_inner_cells(cats, start, span, gap, gap_after_last):
    """Mirror of ColorWheel.BuildInnerCells: uniform chip width, gaps between groups."""
    n = sum(len(c.colors) for c in cats)
    ngap = len(cats) if gap_after_last else len(cats) - 1
    width = (span - ngap * gap) / n
    cells, a = [], start
    for c in cats:
        for sw in c.colors:
            cells.append((a, a + width, sw))
            a += width
        a += gap
    return cells, width


class Cat:
    def __init__(self, name, colors):
        self.name, self.colors = name, colors


def parse_tier(raw_name):
    """Pull Tier1Raw / Tier2Raw out of CopicPalette.cs as [(name, [(code, hex)])]."""
    import re
    src = A.read(A.PAL)
    m = re.search(raw_name + r'\s*=\s*\{(.*?)\n    \};', src, re.S)
    out = []
    for name, data in re.findall(r'\("([^"]+)",\s*"([^"]*)"\)', m.group(1)):
        out.append(Cat(name, [tuple(t.split(':')) for t in data.split()]))
    return out


def annulus(dr, cx, cy, r0, r1, a0, a1, fill, outline=None, w=0):
    """Filled annular sector, a0/a1 in degrees (screen: 0 = east, clockwise)."""
    steps = max(3, int(abs(a1 - a0) / 1.2) + 3)
    pts = []
    for i in range(steps + 1):
        a = math.radians(a0 + (a1 - a0) * i / steps)
        pts.append((cx + r1 * math.cos(a), cy + r1 * math.sin(a)))
    for i in range(steps, -1, -1):
        a = math.radians(a0 + (a1 - a0) * i / steps)
        pts.append((cx + r0 * math.cos(a), cy + r0 * math.sin(a)))
    dr.polygon(pts, fill=fill, outline=outline, width=w)


def label(img, text, cx, cy, r, amid, font, colour):
    """Code text, rotated by the reference's fixed upright offset (midA - 90)."""
    if not text:
        return
    tmp = Image.new('RGBA', (int(font.size * len(text) * 1.4) + 8, int(font.size * 1.9) + 8),
                    (0, 0, 0, 0))
    d = ImageDraw.Draw(tmp)
    d.text((tmp.width / 2, tmp.height / 2), text, font=font, fill=colour, anchor='mm')
    rot = tmp.rotate(-(amid - 90.0), expand=True, resample=Image.BICUBIC)
    a = math.radians(amid)
    px = cx + r * math.cos(a) - rot.width / 2
    py = cy + r * math.sin(a) - rot.height / 2
    img.alpha_composite(rot, (int(px), int(py)))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', required=True)
    ap.add_argument('--additions')
    ap.add_argument('--mark', action='store_true',
                    help='outline the added swatches so they can be found')
    ap.add_argument('--scale', type=float, default=1.8, help='pixels per reference DIP')
    ap.add_argument('--title', default='')
    args = ap.parse_args()

    # ---- columns: 36 of them, in wheel order --------------------------
    secs = A.sectors()
    columns, ends_family = [], []
    for _, _, sl in secs:
        for i, (_, _, cells) in enumerate(sl):
            columns.append([list(c) for c in cells])
            ends_family.append(i == len(sl) - 1)

    added = set()
    if args.additions:
        plan = json.load(open(args.additions))
        cols_after = plan['columns_after']
        rebuilt = []
        for sid, _, sl in secs:
            for ci in range(len(sl)):
                rebuilt.append([list(x) for x in cols_after[sid][ci]])
        columns = rebuilt
        added = {a['code'] for a in plan['additions']}

    rings = max(len(c) for c in columns)

    # ---- radii --------------------------------------------------------
    s = 1.0
    u = s * CELL_SCALE
    band = 21.0 * u
    r1_in = R1_IN * s
    r1_out = r1_in + band
    r2_in = r1_out + 5.0 * u
    r2_out = r2_in + band
    r_out_base = r2_out + band * SPINE_GAP
    r_out = r_out_base + rings * band

    SC = args.scale
    margin = 26
    size = int(2 * r_out * SC) + 2 * margin
    cx = cy = size / 2

    img = Image.new('RGBA', (size, size), BG + (255,))
    dr = ImageDraw.Draw(img)

    fs = max(7.0, min(14.0, band * 0.5)) * TEXT_ELEM * SC
    font = ImageFont.truetype(FONT, max(7, int(round(fs))))
    font_b = ImageFont.truetype(FONT_BOLD, max(7, int(round(fs))))

    def R(x):
        return x * SC

    def ink(rgb):
        return (17, 17, 17, 255) if lum(rgb) > 0.42 else (245, 245, 245, 255)

    # ---- Tier 3+: the 36 columns --------------------------------------
    for col, cells in enumerate(columns):
        a_base = OUTER_START + col * COL_STEP + ROT
        span = COL_STEP - FAM_GAP if ends_family[col] else COL_STEP
        for ring, (code, hexv) in enumerate(cells):
            rgb = h2rgb(hexv)
            r0 = r_out_base + ring * band
            new = code in added
            annulus(dr, cx, cy, R(r0), R(r0 + band), a_base, a_base + span, rgb + (255,),
                    outline=(255, 255, 255, 255) if (new and args.mark) else None,
                    w=max(2, int(SC * 1.6)) if (new and args.mark) else 0)
            label(img, code, cx, cy, R(r0 + band / 2), a_base + span / 2,
                  font_b if new else font, ink(rgb))

    # ---- Tier 2: the grey ring ----------------------------------------
    t2, _ = build_inner_cells(parse_tier('Tier2Raw'), T2_START, T2_SPAN, T2_GAP, True)
    for a0, a1, sw in t2:
        rgb = h2rgb(sw[1])
        annulus(dr, cx, cy, R(r2_in), R(r2_out), a0 + ROT, a1 + ROT, rgb + (255,))
        label(img, sw[0], cx, cy, R((r2_in + r2_out) / 2), (a0 + a1) / 2 + ROT, font, ink(rgb))

    # ---- Tier 1: the accent arc ---------------------------------------
    t1, _ = build_inner_cells(parse_tier('Tier1Raw'), T1_START, T1_SPAN, T1_GAP, False)
    for a0, a1, sw in t1:
        rgb = h2rgb(sw[1])
        annulus(dr, cx, cy, R(r1_in), R(r1_out), a0 + ROT, a1 + ROT, rgb + (255,))
        label(img, sw[0], cx, cy, R((r1_in + r1_out) / 2), (a0 + a1) / 2 + ROT, font, ink(rgb))

    # ---- caption in the hole ------------------------------------------
    if args.title:
        tf = ImageFont.truetype(FONT_BOLD, int(30 * SC / 1.8))
        sf = ImageFont.truetype(FONT, int(19 * SC / 1.8))
        head, *rest = args.title.split('|')
        dr.text((cx, cy - 22 * SC / 1.8), head.strip(), font=tf, fill=(238, 238, 238, 255),
                anchor='mm')
        for i, line in enumerate(rest):
            dr.text((cx, cy + (10 + i * 24) * SC / 1.8), line.strip(), font=sf,
                    fill=(170, 170, 170, 255), anchor='mm')

    img.convert('RGB').save(args.out, optimize=True)
    n = sum(len(c) for c in columns) + len(t1) + len(t2)
    print(f"{args.out}  {size}x{size}px  rings={rings}  outerR={r_out:.0f} ref-DIP  "
          f"swatches={n}  added={len(added)}")


if __name__ == '__main__':
    main()
