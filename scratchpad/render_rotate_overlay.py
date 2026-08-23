#!/usr/bin/env python3
"""Rasterise CONCEPTS-REF 17.11a's rotate interface OFFLINE, and measure it.

    python scratchpad/render_rotate_overlay.py

Writes scratchpad/rotate_out/rotate_<angle>deg_<zoom>x.png plus a contact sheet,
and asserts the five properties 17.11a is specific about.  Exit 0 = every
assertion held.

WHY THIS EXISTS.  The overlay is Win2D drawn onto a CanvasVirtualControl, so
the only way to see it is to run the app - and the user is at the machine, so
the screen is not available.  render_icons.py made the same argument for the
24-grid marks and it caught a mark that read as a balloon.  This is that
argument for an overlay: the geometry and the falloff are checkable without a
device, so they are checked without one.

WHAT IT DOES NOT PROVE.  This is a transcription of the DRAW MATH, not of
Win2D.  It reads the constants out of InkSurface.cs so the numbers cannot
drift, but the arc primitive, the CanvasCommandList and the GaussianBlurEffect
are modelled here rather than executed.  Whether Win2D composites the two blur
passes on screen the way numpy composites them here is the one thing left for a
human eye - see the report that shipped with this branch.

THE FIVE PROPERTIES, in 17.11a's own words:

  1. "the centre itself empty"  - no ink inside the crosshair's gap.
  2. "four short strokes pointing outward"  - ink in all four tick bands.
  3. "spanning the full canvas width"  - the line reaches both edges of the
     frame at every angle tested.
  4. "convex away from the pivot"  - every point of the arc is further from the
     pivot than the chord joining its ends, and the arc crosses the line
     exactly at the handle.
  5. "a soft halo outside the stroke, not a hard edge"  - radial intensity
     outside the arc's stroke falls off monotonically over many pixels rather
     than stepping to zero, and "a donut, not a dot" - the handle's centre is
     darker than its ring.
"""
from __future__ import annotations

import math
import os
import re
import sys

import numpy as np
from PIL import Image

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INK = os.path.join(ROOT, "src", "Quill", "Controls", "InkSurface.cs")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "rotate_out")

SS = 3          # supersample per axis for the crisp marks
W, H = 720, 460  # the frame these are measured in, in screen pixels

FAILURES: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> bool:
    print(("  PASS " if ok else "  FAIL ") + label + (("   -- " + detail) if detail else ""))
    if not ok:
        FAILURES.append(label)
    return ok


# ---------------------------------------------------------------------------
# The constants, READ FROM THE SOURCE so this file cannot drift from it
# ---------------------------------------------------------------------------

def constants() -> dict[str, float]:
    src = open(INK, encoding="utf-8").read()
    want = [
        "RotateLineWidthPx", "RotateCrossGapPx", "RotateCrossTickPx",
        "RotateCrossWidthPx", "RotateArcWidthPx", "RotateArcHalfSpanDeg",
        "RotateHandleRingPx", "RotateHandleRingWidthPx",
        "RotateGlowTightPx", "RotateGlowWidePx",
        "RotateGlowTightAlpha", "RotateGlowWideAlpha",
        "RotateRadiusMinPx",
    ]
    out: dict[str, float] = {}
    for name in want:
        m = re.search(r"const\s+(?:float|byte)\s+%s\s*=\s*([0-9.]+)f?\s*;" % name, src)
        if m is None:
            print("MISSING CONSTANT: %s" % name)
            sys.exit(2)
        out[name] = float(m.group(1))
    m = re.search(r"RotateInk\s*=\s*Color\.FromArgb\(\s*255\s*,\s*0x([0-9A-Fa-f]{2})\s*,"
                  r"\s*0x([0-9A-Fa-f]{2})\s*,\s*0x([0-9A-Fa-f]{2})", src)
    if m is None:
        print("MISSING CONSTANT: RotateInk")
        sys.exit(2)
    out["InkR"], out["InkG"], out["InkB"] = (float(int(g, 16)) for g in m.groups())
    return out


# ---------------------------------------------------------------------------
# Rasterising, in screen pixels.  The C# multiplies every px by 1/ViewZoom and
# then ds.Transform scales by ViewZoom, so on SCREEN every number below is the
# constant itself - which is the whole point of authoring them in px, and is
# why this model can work in screen space and still be the same drawing.
# ---------------------------------------------------------------------------

def coverage_segments(segs, width, w=W, h=H, ss=SS):
    """Antialiased coverage (0..1) of a set of FLAT-CAPPED line segments.

    Flat, not round, because that is what the source draws: ds.DrawLine with no
    CanvasStrokeStyle takes the default one, whose StartCap and EndCap are Flat.
    Modelling round caps here made the ticks bulge 1.2px INTO the gap the
    crosshair's empty centre is measured in - the model was failing a test the
    code passes, which is the more embarrassing direction for a proof to be
    wrong in."""
    ys, xs = np.mgrid[0:h * ss, 0:w * ss]
    px = (xs + 0.5) / ss
    py = (ys + 0.5) / ss
    inside = np.zeros(px.shape, dtype=bool)
    for (ax, ay), (bx, by) in segs:
        dx, dy = bx - ax, by - ay
        L = math.hypot(dx, dy)
        if L <= 0:
            continue
        ux, uy = dx / L, dy / L
        along = (px - ax) * ux + (py - ay) * uy          # 0..L inside the cap
        perp = np.abs((px - ax) * (-uy) + (py - ay) * ux)
        inside |= (perp <= width / 2.0) & (along >= 0) & (along <= L)
    cov = inside.astype(np.float32)
    return cov.reshape(h, ss, w, ss).mean(axis=(1, 3))


def coverage_arc(centre, radius, a0, a1, width, w=W, h=H, ss=SS):
    """Coverage of a circular arc - the distance field of a circle, clipped to
    the swept angular wedge, which is exactly what a stroked arc is."""
    cx, cy = centre
    ys, xs = np.mgrid[0:h * ss, 0:w * ss]
    px = (xs + 0.5) / ss
    py = (ys + 0.5) / ss
    d = np.abs(np.hypot(px - cx, py - cy) - radius)
    ang = np.arctan2(py - cy, px - cx)
    mid = (a0 + a1) / 2.0
    half = abs(a1 - a0) / 2.0
    delta = np.abs(np.mod(ang - mid + math.pi, 2 * math.pi) - math.pi)
    cov = ((d <= width / 2.0) & (delta <= half)).astype(np.float32)
    return cov.reshape(h, ss, w, ss).mean(axis=(1, 3))


def coverage_disc(centre, radius, w=W, h=H, ss=SS):
    cx, cy = centre
    ys, xs = np.mgrid[0:h * ss, 0:w * ss]
    d = np.hypot((xs + 0.5) / ss - cx, (ys + 0.5) / ss - cy)
    cov = (d <= radius).astype(np.float32)
    return cov.reshape(h, ss, w, ss).mean(axis=(1, 3))


def coverage_ring(centre, radius, width, w=W, h=H, ss=SS):
    cx, cy = centre
    ys, xs = np.mgrid[0:h * ss, 0:w * ss]
    d = np.abs(np.hypot((xs + 0.5) / ss - cx, (ys + 0.5) / ss - cy) - radius)
    cov = (d <= width / 2.0).astype(np.float32)
    return cov.reshape(h, ss, w, ss).mean(axis=(1, 3))


def gaussian(a: np.ndarray, sigma: float) -> np.ndarray:
    """Separable Gaussian.  Win2D's BlurAmount IS the standard deviation."""
    if sigma <= 0:
        return a
    n = max(1, int(math.ceil(sigma * 3)))
    x = np.arange(-n, n + 1, dtype=np.float64)
    k = np.exp(-(x * x) / (2 * sigma * sigma))
    k /= k.sum()
    out = np.apply_along_axis(lambda m: np.convolve(m, k, mode="same"), 1, a.astype(np.float64))
    out = np.apply_along_axis(lambda m: np.convolve(m, k, mode="same"), 0, out)
    return out


def over(dst_rgb, dst_a, src_cov, src_rgb, src_alpha):
    """Source-over, premultiplied, on a float RGB + alpha pair."""
    a = src_cov * src_alpha
    for i in range(3):
        dst_rgb[..., i] = src_rgb[i] * a + dst_rgb[..., i] * (1 - a)
    return dst_rgb, np.clip(a + dst_a * (1 - a), 0, 1)


# ---------------------------------------------------------------------------
# One frame, exactly as DrawRotateInterface draws it
# ---------------------------------------------------------------------------

def frame(C, angle_deg, radius_px, pivot, ground=(0.086, 0.086, 0.094)):
    ink = (C["InkR"] / 255.0, C["InkG"] / 255.0, C["InkB"] / 255.0)
    rgb = np.zeros((H, W, 3), dtype=np.float64)
    rgb[:] = ground
    alpha = np.zeros((H, W), dtype=np.float64)

    rad = math.radians(angle_deg)
    dirv = (math.cos(rad), math.sin(rad))
    perp = (-dirv[1], dirv[0])
    px, py = pivot
    gap = C["RotateCrossGapPx"]
    tip = C["RotateCrossGapPx"] + C["RotateCrossTickPx"]
    reach = float(W + H)
    handle = (px + dirv[0] * radius_px, py + dirv[1] * radius_px)

    def along(d, t):
        return (px + d[0] * t, py + d[1] * t)

    # the line, broken at the pivot
    line = [(along(dirv, gap), along(dirv, reach)),
            (along((-dirv[0], -dirv[1]), gap), along((-dirv[0], -dirv[1]), reach))]
    cov = coverage_segments(line, C["RotateLineWidthPx"])
    rgb, alpha = over(rgb, alpha, cov, ink, 1.0)

    # the four ticks around the gap
    ticks = [(along(d, gap), along(d, tip)) for d in
             (dirv, (-dirv[0], -dirv[1]), perp, (-perp[0], -perp[1]))]
    cov = coverage_segments(ticks, C["RotateCrossWidthPx"])
    rgb, alpha = over(rgb, alpha, cov, ink, 1.0)

    # the arc
    half = math.radians(C["RotateArcHalfSpanDeg"])
    arc_cov = coverage_arc(pivot, radius_px, rad - half, rad + half, C["RotateArcWidthPx"])
    ring_cov = coverage_ring(handle, C["RotateHandleRingPx"], C["RotateHandleRingWidthPx"])
    glow_src = np.maximum(arc_cov, ring_cov)

    # two blurred passes, wide then tight, exactly as DrawRotateGlow is called
    for radius, a8 in ((C["RotateGlowWidePx"], C["RotateGlowWideAlpha"]),
                       (C["RotateGlowTightPx"], C["RotateGlowTightAlpha"])):
        g = gaussian(glow_src * (a8 / 255.0), radius)
        rgb, alpha = over(rgb, alpha, g, ink, 1.0)

    # the crisp arc over its own halo
    rgb, alpha = over(rgb, alpha, arc_cov, ink, 1.0)

    # the donut: dark fill to the ring's inner edge, then the ring
    core_r = C["RotateHandleRingPx"] - C["RotateHandleRingWidthPx"] / 2.0
    rgb, alpha = over(rgb, alpha, coverage_disc(handle, core_r), (20 / 255.0, 20 / 255.0, 19 / 255.0), 1.0)
    rgb, alpha = over(rgb, alpha, ring_cov, ink, 1.0)

    return rgb, dict(pivot=pivot, handle=handle, dirv=dirv, perp=perp,
                     radius=radius_px, rad=rad, arc_cov=arc_cov, glow_src=glow_src,
                     ink=ink, ground=ground)


def inkiness(rgb, ground):
    """How far each pixel has moved from the ground, 0..1 - the measure that
    does not care WHICH mark put ink there."""
    g = np.array(ground).reshape(1, 1, 3)
    return np.clip(np.abs(rgb - g).max(axis=2) / max(1e-6, max(abs(c - b) for c, b in
                   zip((0.749, 0.239, 0.220), ground))), 0, 1)


def sample(a, x, y):
    """Bilinear, not nearest.  Rounding to the nearest pixel moved a probe by up
    to 0.7px, which at a 7px gap is a tenth of the thing being measured."""
    x -= 0.5
    y -= 0.5
    x0, y0 = math.floor(x), math.floor(y)
    fx, fy = x - x0, y - y0
    tot = 0.0
    for dx, dy, wgt in ((0, 0, (1 - fx) * (1 - fy)), (1, 0, fx * (1 - fy)),
                        (0, 1, (1 - fx) * fy), (1, 1, fx * fy)):
        xi, yi = x0 + dx, y0 + dy
        if 0 <= xi < a.shape[1] and 0 <= yi < a.shape[0]:
            tot += wgt * float(a[yi, xi])
    return tot


# ---------------------------------------------------------------------------

def detail(img, centre, name, half=44, zoom=6):
    cx, cy = int(round(centre[0])), int(round(centre[1]))
    box = (max(0, cx - half), max(0, cy - half),
           min(img.width, cx + half), min(img.height, cy + half))
    crop = img.crop(box)
    crop = crop.resize((crop.width * zoom, crop.height * zoom), Image.NEAREST)
    crop.save(os.path.join(OUT, name))


def main() -> int:
    os.makedirs(OUT, exist_ok=True)
    C = constants()
    print("constants read from InkSurface.cs:")
    for k in sorted(C):
        print("    %-24s %s" % (k, C[k]))
    print()

    # The pivot is LEFT OF CENTRE, which is where the capture puts it and what
    # decision 1 says it means.
    # 0 is the capture's own state; 34 is a plain free angle; -63 crosses into
    # the negative half the wrap is there for; 90 is straight down.  118 is the
    # short radius a hand-in drag leaves; 48 is RotateRadiusMinPx itself, where
    # the handle's reach and the pivot's very nearly touch and the crosshair's
    # gap is the tightest thing on screen.
    cases = [(0.0, 190.0), (34.0, 190.0), (-63.0, 118.0), (90.0, 175.0),
             (140.0, C["RotateRadiusMinPx"])]
    pivot = (W * 0.34, H * 0.52)
    sheets = []

    for angle, radius in cases:
        rgb, m = frame(C, angle, radius, pivot)
        img = Image.fromarray((np.clip(rgb, 0, 1) * 255).astype(np.uint8))
        name = "rotate_%+.0fdeg_r%.0f.png" % (angle, radius)
        img.save(os.path.join(OUT, name))
        sheets.append(img)

        # Blown up 6x with nearest neighbour, the way render_icons.py shows a
        # mark at the size it actually draws: a crosshair and a donut are small,
        # and "reads as four ticks round a gap" is not answerable at 1:1.
        detail(img, pivot, "detail_pivot_%+.0fdeg.png" % angle)
        detail(img, m["handle"], "detail_handle_%+.0fdeg.png" % angle)
        print("%s  (pivot %.0f,%.0f  handle %.0f,%.0f)" %
              (name, pivot[0], pivot[1], m["handle"][0], m["handle"][1]))

        ink = inkiness(rgb, m["ground"])
        px, py = pivot
        dirv, perp = m["dirv"], m["perp"]
        gap = C["RotateCrossGapPx"]

        # 1. the centre is EMPTY
        inner = max(0.0, gap - C["RotateCrossWidthPx"] / 2.0 - 0.75)
        hot = 0.0
        for t in np.linspace(0, inner, 12):
            for a in np.linspace(0, 2 * math.pi, 48, endpoint=False):
                hot = max(hot, sample(ink, px + t * math.cos(a), py + t * math.sin(a)))
        check("1. the crosshair's centre is empty inside the %.1fpx gap" % gap,
              hot < 0.06, "hottest sample %.3f" % hot)

        # 2. four ticks, all four present
        mid = gap + C["RotateCrossTickPx"] / 2.0
        vals = [sample(ink, px + d[0] * mid, py + d[1] * mid) for d in
                (dirv, (-dirv[0], -dirv[1]), perp, (-perp[0], -perp[1]))]
        check("2. all four ticks carry ink at the middle of the tick band",
              min(vals) > 0.75, "min %.3f of %s" % (min(vals), ["%.2f" % v for v in vals]))

        # 3. the line spans the frame - ink on the line beyond both far edges
        far = []
        for s in (+1, -1):
            t = 1.0
            while True:
                x, y = px + s * dirv[0] * t, py + s * dirv[1] * t
                if x < 0 or x >= W or y < 0 or y >= H:
                    break
                t += 1.0
            far.append(sample(ink, px + s * dirv[0] * (t - 2), py + s * dirv[1] * (t - 2)))
        check("3. the line still carries ink where it leaves the frame, both ways",
              min(far) > 0.5, "ends %s" % ["%.2f" % v for v in far])

        # 4a. the arc crosses the line AT the handle
        hx, hy = m["handle"]
        check("4a. the arc crosses the line at the handle",
              sample(m["arc_cov"], hx, hy) > 0.9 and
              abs(math.hypot(hx - px, hy - py) - radius) < 0.51,
              "arc coverage %.2f, |handle-pivot| %.2f vs radius %.2f"
              % (sample(m["arc_cov"], hx, hy), math.hypot(hx - px, hy - py), radius))

        # 4b. convex AWAY from the pivot: the arc's midpoint is further out than
        # the chord joining its ends.
        half = math.radians(C["RotateArcHalfSpanDeg"])
        e0 = (px + radius * math.cos(m["rad"] - half), py + radius * math.sin(m["rad"] - half))
        e1 = (px + radius * math.cos(m["rad"] + half), py + radius * math.sin(m["rad"] + half))
        chord_mid = ((e0[0] + e1[0]) / 2.0, (e0[1] + e1[1]) / 2.0)
        d_chord = math.hypot(chord_mid[0] - px, chord_mid[1] - py)
        check("4b. the arc bulges AWAY from the pivot - its midpoint is further "
              "out than its own chord",
              radius - d_chord > 1.0, "arc %.1f vs chord midpoint %.1f" % (radius, d_chord))

        # 5a. the halo is SOFT.  Probed along a ray at 60% of the arc's half
        # span - inside the arc, well clear of BOTH the line and the donut, so
        # the only thing between the arc's stroke and the empty page is the
        # halo.  (Probing along the line itself measured the line, and probing
        # through the handle measured the donut's dark core: two ways of
        # discovering that a full-width line leaves nowhere on itself to see a
        # glow against.)
        pa = m["rad"] + math.radians(C["RotateArcHalfSpanDeg"]) * 0.6
        pd = (math.cos(pa), math.sin(pa))
        start = radius + C["RotateArcWidthPx"] / 2.0 + 1.0
        prof = [sample(ink, px + pd[0] * t, py + pd[1] * t)
                for t in np.arange(start, start + 30, 1.0)]
        spread = sum(1 for v in prof if v > 0.02)
        drops = all(prof[i] >= prof[i + 1] - 0.02 for i in range(len(prof) - 1))
        check("5a. the halo is soft - ink persists %d px past the arc's stroke "
              "and falls off monotonically, rather than stepping to nothing"
              % spread, spread >= 10 and drops,
              "first eight outward samples %s" % ["%.3f" % v for v in prof[:8]])

        # 5b. a DONUT, not a dot
        centre_v = sample(ink, hx, hy)
        ring_v = max(sample(ink, hx + C["RotateHandleRingPx"] * math.cos(a),
                            hy + C["RotateHandleRingPx"] * math.sin(a))
                     for a in np.linspace(0, 2 * math.pi, 32, endpoint=False))
        check("5b. the handle is a donut - its centre is far darker than its ring",
              ring_v - centre_v > 0.6, "centre %.3f, ring %.3f" % (centre_v, ring_v))
        print()

    # contact sheet
    cols = 2
    rows = (len(sheets) + cols - 1) // cols
    sheet = Image.new("RGB", (W * cols, H * rows), (12, 12, 14))
    for i, im in enumerate(sheets):
        sheet.paste(im, ((i % cols) * W, (i // cols) * H))
    sheet.save(os.path.join(OUT, "contact.png"))
    print("wrote %s" % os.path.join(OUT, "contact.png"))

    if FAILURES:
        print("\nFAILED: %d" % len(FAILURES))
        for f in FAILURES:
            print("  " + f)
        return 1
    print("\nOK - the interface draws what 17.11a describes.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
