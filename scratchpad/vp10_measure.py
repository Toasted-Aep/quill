"""Measure a panel's ground and its ink off a capture: the modal ground colour
in a box, the darkest/lightest ink in it, and the WCAG contrast between them."""
import sys
from collections import Counter

from PIL import Image


def lum(rgb):
    def li(v):
        v /= 255.0
        return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4
    r, g, b = rgb
    return 0.2126 * li(r) + 0.7152 * li(g) + 0.0722 * li(b)


def contrast(a, b):
    la, lb = lum(a), lum(b)
    return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)


def hexs(c):
    return '#%02X%02X%02X' % c


def report(path, boxes):
    img = Image.open(path).convert('RGB')
    px = img.load()
    for name, (x0, y0, x1, y1) in boxes.items():
        cnt = Counter()
        for y in range(y0, y1):
            for x in range(x0, x1):
                cnt[px[x, y]] += 1
        ground, gn = cnt.most_common(1)[0]
        total = sum(cnt.values())
        # the ink is the extreme-luminance pixel that is not a stray antialias:
        # take the darkest colour holding at least 0.4% of the box
        big = [(c, n) for c, n in cnt.items() if n >= total * 0.004]
        dark = min(big, key=lambda cn: lum(cn[0]))[0]
        light = max(big, key=lambda cn: lum(cn[0]))[0]
        print('%-28s ground %s (%4.1f%%)  darkest %s  lightest %s' %
              (name, hexs(ground), 100.0 * gn / total, hexs(dark), hexs(light)))
        print('%-28s contrast ground:darkest = %.2f:1   ground:lightest = %.2f:1' %
              ('', contrast(ground, dark), contrast(ground, light)))


if __name__ == '__main__':
    path = sys.argv[1]
    boxes = {}
    for a in sys.argv[2:]:
        n, x0, y0, x1, y1 = a.split(':')
        boxes[n] = (int(x0), int(y0), int(x1), int(y1))
    report(path, boxes)
