"""Crop one family out of a rendered wheel, so its swatches are readable.

The full render is 4086px; downscaled to fit a screen the individual codes stop
being legible, which is exactly what the approval gate needs to see. This cuts
the wedge belonging to one family out of both the before and after renders at
full resolution and stacks them side by side.

  python scratchpad/copic_crop.py blue-green
  python scratchpad/copic_crop.py red-violet
"""
import math, os, sys
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import copic_audit as A
import copic_render as R

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'copic_out')


def bbox(a0, a1, r0, r1, cx, cy, sc):
    xs, ys = [], []
    n = max(8, int(abs(a1 - a0)))
    for i in range(n + 1):
        a = math.radians(a0 + (a1 - a0) * i / n)
        for r in (r0, r1):
            xs.append(cx + r * sc * math.cos(a))
            ys.append(cy + r * sc * math.sin(a))
    return min(xs), min(ys), max(xs), max(ys)


def main():
    fam_id = sys.argv[1] if len(sys.argv) > 1 else 'blue-green'
    sc = 1.8

    secs = A.sectors()
    col0, target = 0, None
    for sid, name, sl in secs:
        if sid == fam_id:
            target = (col0, len(sl), name)
            break
        col0 += len(sl)
    if not target:
        sys.exit(f"no such family: {fam_id}  (try {[s[0] for s in secs]})")
    start, ncols, name = target

    u = 1.0 * R.CELL_SCALE
    band = 21.0 * u
    r1_out = R.R1_IN + band
    r2_out = r1_out + 5.0 * u + band
    r_out_base = r2_out + band * R.SPINE_GAP
    r_out = r_out_base + 17 * band
    size = int(2 * r_out * sc) + 52
    cx = cy = size / 2

    a0 = R.OUTER_START + start * R.COL_STEP + R.ROT
    a1 = a0 + ncols * R.COL_STEP
    pad = 26
    x0, y0, x1, y1 = bbox(a0, a1, r_out_base - band * 1.2, r_out, cx, cy, sc)
    box = (max(0, int(x0 - pad)), max(0, int(y0 - pad)),
           min(size, int(x1 + pad)), min(size, int(y1 + pad)))

    panels = []
    for tag in ('before', 'after_marked'):
        p = os.path.join(OUT, f'wheel_{tag}.png')
        panels.append((tag, Image.open(p).crop(box)))

    w = max(p.width for _, p in panels)
    h = max(p.height for _, p in panels)
    cap = 54
    out = Image.new('RGB', (w * 2 + 30, h + cap), R.BG)
    d = ImageDraw.Draw(out)
    f = ImageFont.truetype(R.FONT_BOLD, 26)
    fs = ImageFont.truetype(R.FONT, 19)
    for i, (tag, p) in enumerate(panels):
        out.paste(p, (i * (w + 30), cap))
        d.text((i * (w + 30) + 8, 10), tag.replace('_marked', ' (added ringed)').upper(),
               font=f, fill=(238, 238, 238))
    d.text((8, h + cap - 26), f"{name} - added swatches outlined in white",
           font=fs, fill=(150, 150, 150))
    dest = os.path.join(OUT, f'detail_{fam_id}.png')
    out.save(dest, optimize=True)
    print(f"{dest}  {out.width}x{out.height}")


if __name__ == '__main__':
    main()
