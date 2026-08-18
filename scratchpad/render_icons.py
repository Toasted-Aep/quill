#!/usr/bin/env python3
"""Rasterise marks out of Helpers/Icons.cs at the size they ACTUALLY DRAW.

    python scratchpad/render_icons.py Paperclip Duplicate WasteBin ...
    python scratchpad/render_icons.py --all

Writes scratchpad/icons_out/<Name>.png - one strip per mark showing it at every
size in SIZES, each blown up 6x with nearest-neighbour so the antialiased pixel
pattern is legible, plus a 1:1 copy at the left.

WHY.  11.23 records two marks (the palette's wells, the star's arms) that looked
correct at preview size and lost their read entirely at the size the app draws
them.  A mark is not finished until it has been seen at its real size, and this
renders offline so checking one does not require taking the screen.

The parser implements the subset of the path mini-language Icons.cs uses -
M L H V C A Z, absolute and relative - and both fill rules, so what is drawn
here is what XamlReader will build.
"""
from __future__ import annotations

import math
import os
import re
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ICONS = os.path.join(ROOT, "src", "Quill", "Helpers", "Icons.cs")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "icons_out")

# The sizes these marks are actually asked for.  16 is the selection bar's mark,
# 15 the fullscreen strip's, 18 the bottom row's, 22 a dial slot.
SIZES = [14, 15, 16, 18, 22, 30]
SS = 4                  # supersampling factor per axis

NUM = re.compile(r"[-+]?(?:\d+\.\d+|\d+\.|\.\d+|\d+)(?:[eE][-+]?\d+)?")


# ---------------------------------------------------------------------------
# reassembling the literals (same walk verify_icons.py does)
# ---------------------------------------------------------------------------

def literals(text: str):
    decl = re.compile(r"public\s+const\s+string\s+(\w+)\s*=", re.M)
    for m in decl.finditer(text):
        name = m.group(1)
        chunks, j = [], m.end()
        while j < len(text):
            c = text[j]
            if c == '"':
                k, buf = j + 1, []
                while k < len(text) and text[k] != '"':
                    if text[k] == "\\":
                        buf.append(text[k:k + 2]); k += 2; continue
                    buf.append(text[k]); k += 1
                chunks.append("".join(buf))
                j = k + 1
                continue
            if c == "/" and text[j:j + 2] == "//":
                j = text.find("\n", j)
                if j < 0:
                    break
                continue
            if c == ";":
                break
            j += 1
        yield name, "".join(chunks)


# ---------------------------------------------------------------------------
# path -> polygons
# ---------------------------------------------------------------------------

def tokenise(d):
    out, i, n = [], 0, len(d)
    cmd, args = None, []
    while i < n:
        c = d[i]
        if c in " ,\t\r\n":
            i += 1; continue
        if c.isalpha():
            if cmd is not None:
                out.append((cmd, args))
            cmd, args = c, []
            i += 1; continue
        m = NUM.match(d, i)
        if not m:
            raise ValueError(f"not a number at {i}: {d[i:i+20]!r}")
        args.append(float(m.group(0)))
        i = m.end()
    if cmd is not None:
        out.append((cmd, args))
    return out


def bezier(p0, p1, p2, p3, n=18):
    return [(
        (1 - t) ** 3 * p0[0] + 3 * (1 - t) ** 2 * t * p1[0] + 3 * (1 - t) * t * t * p2[0] + t ** 3 * p3[0],
        (1 - t) ** 3 * p0[1] + 3 * (1 - t) ** 2 * t * p1[1] + 3 * (1 - t) * t * t * p2[1] + t ** 3 * p3[1],
    ) for t in [(i + 1) / n for i in range(n)]]


def svg_arc(p0, rx, ry, phi_deg, large, sweep, p1, n=24):
    """Endpoint-parameterised elliptical arc, per the SVG 1.1 implementation notes."""
    if abs(p0[0] - p1[0]) < 1e-12 and abs(p0[1] - p1[1]) < 1e-12:
        return []
    rx, ry = abs(rx), abs(ry)
    if rx == 0 or ry == 0:
        return [p1]
    phi = math.radians(phi_deg)
    cp, sp = math.cos(phi), math.sin(phi)
    dx2, dy2 = (p0[0] - p1[0]) / 2, (p0[1] - p1[1]) / 2
    x1p, y1p = cp * dx2 + sp * dy2, -sp * dx2 + cp * dy2
    lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry)
    if lam > 1:
        s = math.sqrt(lam); rx, ry = rx * s, ry * s
    num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
    den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
    co = math.sqrt(max(0.0, num / den)) if den else 0.0
    if large == sweep:
        co = -co
    cxp, cyp = co * rx * y1p / ry, -co * ry * x1p / rx
    cx = cp * cxp - sp * cyp + (p0[0] + p1[0]) / 2
    cy = sp * cxp + cp * cyp + (p0[1] + p1[1]) / 2

    def ang(ux, uy, vx, vy):
        d = (ux * vx + uy * vy) / (math.hypot(ux, uy) * math.hypot(vx, vy))
        a = math.acos(max(-1.0, min(1.0, d)))
        return -a if ux * vy - uy * vx < 0 else a

    t1 = ang(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
    dt = ang((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
    if not sweep and dt > 0:
        dt -= 2 * math.pi
    elif sweep and dt < 0:
        dt += 2 * math.pi
    pts = []
    for i in range(1, n + 1):
        t = t1 + dt * i / n
        x, y = rx * math.cos(t), ry * math.sin(t)
        pts.append((cp * x - sp * y + cx, sp * x + cp * y + cy))
    return pts


def polygons(d):
    """Returns (list of closed polygons, nonzero: bool)."""
    nonzero = False
    d = d.strip()
    if d[:2].upper() == "F1":
        nonzero, d = True, d[2:]
    elif d[:2].upper() == "F0":
        d = d[2:]
    polys, cur = [], []
    cx = cy = sx = sy = 0.0
    for cmd, args in tokenise(d):
        up, rel = cmd.upper(), cmd.islower()
        k = {"M": 2, "L": 2, "H": 1, "V": 1, "C": 6, "A": 7, "Z": 0}[up]
        if up == "Z":
            if len(cur) > 2:
                polys.append(cur)
            cur, cx, cy = [], sx, sy
            continue
        for j in range(0, len(args), k):
            a = args[j:j + k]
            if up == "M":
                x, y = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
                if len(cur) > 2:
                    polys.append(cur)
                cur = [(x, y)]
                cx, cy = sx, sy = x, y
                up = "L"          # subsequent pairs of an M are implicit L
            elif up == "L":
                x, y = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
                cur.append((x, y)); cx, cy = x, y
            elif up == "H":
                x = cx + a[0] if rel else a[0]
                cur.append((x, cy)); cx = x
            elif up == "V":
                y = cy + a[0] if rel else a[0]
                cur.append((cx, y)); cy = y
            elif up == "C":
                p1 = (cx + a[0], cy + a[1]) if rel else (a[0], a[1])
                p2 = (cx + a[2], cy + a[3]) if rel else (a[2], a[3])
                p3 = (cx + a[4], cy + a[5]) if rel else (a[4], a[5])
                cur += bezier((cx, cy), p1, p2, p3)
                cx, cy = p3
            elif up == "A":
                p1 = (cx + a[5], cy + a[6]) if rel else (a[5], a[6])
                cur += svg_arc((cx, cy), a[0], a[1], a[2], int(a[3]) != 0, int(a[4]) != 0, p1)
                cx, cy = p1
    if len(cur) > 2:
        polys.append(cur)
    return polys, nonzero


# ---------------------------------------------------------------------------
# scanline fill
# ---------------------------------------------------------------------------

def raster(polys, nonzero, size, ss=SS):
    """Coverage array at `size` DIP, drawn the way Icons.Mark does: authored
    24-grid coordinates scaled by size/24, no stretch, no clip."""
    n = size * ss
    k = n / 24.0
    cov = np.zeros((n, n), dtype=np.float32)
    edges = []
    for poly in polys:
        m = len(poly)
        for i in range(m):
            x0, y0 = poly[i][0] * k, poly[i][1] * k
            x1, y1 = poly[(i + 1) % m][0] * k, poly[(i + 1) % m][1] * k
            if y0 != y1:
                edges.append((x0, y0, x1, y1))
    if not edges:
        return cov
    E = np.array(edges, dtype=np.float64)
    x0, y0, x1, y1 = E[:, 0], E[:, 1], E[:, 2], E[:, 3]
    ylo, yhi = np.minimum(y0, y1), np.maximum(y0, y1)
    wind = np.where(y1 > y0, 1, -1)
    for row in range(n):
        yc = row + 0.5
        hit = (ylo <= yc) & (yhi > yc)
        if not hit.any():
            continue
        t = (yc - y0[hit]) / (y1[hit] - y0[hit])
        xs = x0[hit] + t * (x1[hit] - x0[hit])
        w = wind[hit]
        order = np.argsort(xs, kind="stable")
        xs, w = xs[order], w[order]
        if nonzero:
            acc, spans, start = 0, [], None
            for xv, wv in zip(xs, w):
                was = acc
                acc += wv
                if was == 0 and acc != 0:
                    start = xv
                elif was != 0 and acc == 0 and start is not None:
                    spans.append((start, xv)); start = None
        else:
            spans = list(zip(xs[0::2], xs[1::2]))
        for a, b in spans:
            ia, ib = int(math.ceil(a - 0.5)), int(math.ceil(b - 0.5))
            ia, ib = max(0, ia), min(n, ib)
            if ib > ia:
                cov[row, ia:ib] = 1.0
    return cov.reshape(size, ss, size, ss).mean(axis=(1, 3))


def strip(name, data, sizes=SIZES, zoom=6):
    polys, nonzero = polygons(data)
    pads, labels = [], []
    for s in sizes:
        c = raster(polys, nonzero, s)
        one = np.clip(255.0 * (1.0 - c), 0, 255).astype(np.uint8)
        big = np.repeat(np.repeat(one, zoom, axis=0), zoom, axis=1)
        pads.append((one, big))
        labels.append(s)
    H = max(b.shape[0] for _, b in pads) + 4
    W = sum(b.shape[1] + o.shape[1] + 14 for o, b in pads) + 14
    sheet = np.full((H, W), 255, dtype=np.uint8)
    x = 7
    for one, big in pads:
        sheet[2:2 + one.shape[0], x:x + one.shape[1]] = one
        x += one.shape[1] + 7
        sheet[2:2 + big.shape[0], x:x + big.shape[1]] = big
        x += big.shape[1] + 7
    os.makedirs(OUT, exist_ok=True)
    p = os.path.join(OUT, name + ".png")
    Image.fromarray(sheet, "L").save(p)
    # ink coverage per size, the number that says whether a fine feature survived
    return p, [(s, float(raster(polys, nonzero, s).sum())) for s in sizes]


def main(argv):
    text = open(ICONS, "r", encoding="utf-8").read()
    table = dict(literals(text))
    want = argv[1:]
    if not want or want == ["--all"]:
        want = [n for n, v in table.items() if re.match(r"^\s*(?:[Ff][01]\s*)?[Mm][\s\d\-+.]", v)]
    for n in want:
        if n not in table:
            print(f"  ?? no literal named {n}")
            continue
        try:
            p, ink = strip(n, table[n])
        except Exception as e:
            print(f"  !! {n}: {e}")
            continue
        print(f"  {n:<16} -> {os.path.relpath(p, ROOT)}   ink px " +
              "  ".join(f"{s}:{v:.1f}" for s, v in ink))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
