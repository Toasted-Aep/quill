"""Measure the dial's seats and disc off a capture, by EXACT colour.

29's arithmetic says, on a #000000 page:
    disc  PagePlate.Of(#000000, 0.40)   = #2D2D2D
    seat  PagePlate.Seat(#000000)       = #3F403F   (SeatFloor 2.0)
    old seat (before 29)                = #2D2D2D   - same as the disc

So the question "did the floor reach the screen" is answered by whether
#3F403F pixels exist at all, and the question "is the disc unchanged" by
whether the disc still reads #2D2D2D.  Both are exact-match counts, not
impressions.

Usage: python vp11_seats.py <png> [x0 y0 x1 y1]
"""
import sys
from collections import Counter
from PIL import Image

path = sys.argv[1]
im = Image.open(path).convert('RGB')
W, H = im.size
if len(sys.argv) > 5:
    x0, y0, x1, y1 = (int(v) for v in sys.argv[2:6])
else:
    x0, y0, x1, y1 = 0, 0, W, H
px = im.load()

hits = Counter()
pos = {}
for y in range(y0, y1):
    for x in range(x0, x1):
        c = px[x, y]
        h = '#%02X%02X%02X' % c
        hits[h] += 1
        pos.setdefault(h, []).append((x, y))

print('capture %dx%d  region %d,%d..%d,%d' % (W, H, x0, y0, x1, y1))
print()
print('TOP 18 COLOURS IN REGION')
for h, n in hits.most_common(18):
    xs = [p[0] for p in pos[h]]
    ys = [p[1] for p in pos[h]]
    print('  %s  %7d px   x %4d..%-4d  y %4d..%-4d' % (h, n, min(xs), max(xs), min(ys), max(ys)))

print()
print('THE TWO COLOURS 29 PREDICTS')
for tag, h in (('disc  Of(#000000,.40)', '#2D2D2D'),
               ('seat  Seat(#000000)  ', '#3F403F'),
               ('seat  2.25 floor     ', '#464746'),
               ('seat  2.50 floor     ', '#4D4E4D')):
    n = hits.get(h, 0)
    if n:
        xs = [p[0] for p in pos[h]]
        ys = [p[1] for p in pos[h]]
        print('  %s %s  %7d px   x %4d..%-4d y %4d..%-4d' % (tag, h, n, min(xs), max(xs), min(ys), max(ys)))
    else:
        print('  %s %s        0 px   ABSENT' % (tag, h))


def clusters(h, gap=14):
    """Group the pixels of one colour into blobs, so ten seats read as ten."""
    pts = sorted(pos.get(h, []))
    out = []
    for p in pts:
        for c in out:
            if any(abs(p[0] - q[0]) <= gap and abs(p[1] - q[1]) <= gap for q in c[-40:]):
                c.append(p)
                break
        else:
            out.append([p])
    # merge blobs that touch
    merged = True
    while merged:
        merged = False
        for i in range(len(out)):
            for j in range(i + 1, len(out)):
                if any(abs(a[0] - b[0]) <= gap and abs(a[1] - b[1]) <= gap
                       for a in out[i][::7] for b in out[j][::7]):
                    out[i] += out.pop(j)
                    merged = True
                    break
            if merged:
                break
    return out


for tag, h in (('SEAT #3F403F', '#3F403F'), ('DISC #2D2D2D', '#2D2D2D')):
    cs = clusters(h)
    cs = [c for c in cs if len(c) > 25]
    print()
    print('%s -> %d blob(s) over 25 px' % (tag, len(cs)))
    for c in sorted(cs, key=lambda c: -len(c))[:14]:
        xs = [p[0] for p in c]
        ys = [p[1] for p in c]
        print('   %6d px   centre %7.1f,%-7.1f   box %dx%d' %
              (len(c), sum(xs) / len(xs), sum(ys) / len(ys),
               max(xs) - min(xs) + 1, max(ys) - min(ys) + 1))
