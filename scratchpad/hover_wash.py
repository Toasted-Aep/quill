"""What the dial's hover tint actually does to a sector, on every ground.

ToolWheel paints a hovered sector

    Mix(ringFill, onSurface, dark ? 0.10 : 0.07)

and Section 7 gives the ring NO FILL on a dark ground - ringFill is
Colors.Transparent. ToolWheel.Mix interpolates the ALPHA channel too, so on that
side the expression mixes from ARGB(0,0,0,0): the result keeps Transparent's
BLACK primaries and only picks up a tenth of the ink's alpha. The sector goes
DARKER under the pointer on a page whose ink is white.

This script ports PageTheme.Apply and ToolWheel.Mix exactly, composites the
hovered sector over what is really behind it, and reports the step the user
sees - as relative luminance, and again as L*, because a percentage of Y is
meaningless on a page whose Y is 0.003. It then reports what the same 10% of ink
does when it is spent on alpha instead of on a mix from a transparent black,
which is the fix.

Run:  python scratchpad/hover_wash.py
"""

# ---------------------------------------------------------------- colour maths


def lin(v):
    v /= 255.0
    return v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4


def luminance(c):
    r, g, b = c[1], c[2], c[3]
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b)


def to_lab(c):
    r, g, bl = lin(c[1]), lin(c[2]), lin(c[3])
    x = (0.4124 * r + 0.3576 * g + 0.1805 * bl) / 0.95047
    y = 0.2126 * r + 0.7152 * g + 0.0722 * bl
    z = (0.0193 * r + 0.1192 * g + 0.9505 * bl) / 1.08883

    def f(t):
        return t ** (1 / 3) if t > 0.008856 else (7.787 * t) + (16.0 / 116.0)

    fx, fy, fz = f(x), f(y), f(z)
    return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz))


def from_lab(L, a, b):
    L = max(0.0, min(100.0, L))
    fy = (L + 16) / 116
    fx = fy + a / 500
    fz = fy - b / 200

    def g(t):
        return t ** 3 if t ** 3 > 0.008856 else (t - 16.0 / 116.0) / 7.787

    x, y, z = g(fx) * 0.95047, g(fy), g(fz) * 1.08883
    r = 3.2406 * x - 1.5372 * y - 0.4986 * z
    gg = -0.9689 * x + 1.8758 * y + 0.0415 * z
    bb = 0.0557 * x - 0.2040 * y + 1.0570 * z

    def s(v):
        v = 12.92 * v if v <= 0.0031308 else 1.055 * max(v, 0) ** (1 / 2.4) - 0.055
        return int(max(0, min(255, round(v * 255))))

    return (255, s(r), s(gg), s(bb))


def hexs(c):
    return "#%02X%02X%02X%02X" % c


# ------------------------------------------------------ PageTheme.Apply, ported


def theme(ground):
    is_dark = luminance(ground) < 0.5
    L, a, b = to_lab(ground)
    sl = L - 15 if L > 80 else L + 18
    surface = from_lab(sl, a * 0.55, b * 0.55)
    on_surface = (255, 0xF2, 0xF2, 0xF2) if is_dark else (255, 0x14, 0x14, 0x14)
    return {"ground": ground, "dark": is_dark, "surface": surface, "onSurface": on_surface}


# ------------------------------------------------------- ToolWheel.Mix, ported
# NOTE the alpha term. PenBar.Mix, ValuePopover.Mix and SettingsWindow.Mix all
# force A = 255; ToolWheel's is the one that interpolates it, and that is what
# lets Colors.Transparent reach the result.

TRANSPARENT = (0, 0, 0, 0)


def mix(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(4))


def over(src, dst):
    """src composited onto an opaque dst."""
    al = src[0] / 255.0
    return (255,) + tuple(int(round(src[i] * al + dst[i] * (1 - al))) for i in (1, 2, 3))


# ------------------------------------------------------------------ the grounds
# PaperGrain.GroundRgb, plus the two pinned theme grounds MainWindow.ResolveGround
# can hand PageTheme when ThemeSource is Manual.
GROUNDS = [
    ("Plain White", (255, 0xFC, 0xFC, 0xFC)),
    ("Lightweight", (255, 0xF5, 0xF3, 0xEE)),
    ("Rippled", (255, 0xF3, 0xF0, 0xE8)),
    ("Crumpled", (255, 0xF0, 0xEC, 0xE3)),
    ("Heavyweight", (255, 0xE9, 0xE4, 0xD9)),
    ("Blueprint", (255, 0x2E, 0x80, 0xC2)),
    ("Brown Paper", (255, 0xA9, 0x71, 0x3F)),
    ("Darkprint", (255, 0x26, 0x2B, 0x31)),
    ("pinned Dark", (255, 0x0F, 0x0E, 0x10)),
    ("OLED black", (255, 0x00, 0x00, 0x00)),
]

# ToolWheel.ShadowBrush: ARGB(46,0,0,0) flat out to 98% of the ring's radius, so
# the whole ring sits on the page darkened by 18%. The sectors are added to the
# canvas after _shadow, so this - not the bare page - is a sector's backdrop.
SHADOW = (46, 0, 0, 0)

HOVER_T_DARK = 0.10
HOVER_T_LIGHT = 0.07


def report():
    print("%-12s %-4s %-9s %-9s   %-9s %-9s %7s   %7s %7s" %
          ("ground", "dark", "ringFill", "backdrop", "rest", "hover", "dY", "d%", "dL*"))
    print("-" * 101)
    rows = []
    for name, g in GROUNDS:
        t = theme(g)
        dark = t["dark"]
        ring = TRANSPARENT if dark else mix(t["surface"], g, 0.62)
        backdrop = over(SHADOW, g)

        hover = mix(ring, t["onSurface"], HOVER_T_DARK if dark else HOVER_T_LIGHT)

        rest_px = over(ring, backdrop)
        hover_px = over(hover, backdrop)

        y0, y1 = luminance(rest_px), luminance(rest_px if False else hover_px)
        d = y1 - y0
        pct = (d / y0 * 100) if y0 > 0 else float("inf") if d else 0.0
        dl = to_lab(hover_px)[0] - to_lab(rest_px)[0]
        rows.append((name, dark, ring, backdrop, rest_px, hover_px, d, pct, dl))
        print("%-12s %-4s %-9s %-9s   %-9s %-9s %+7.4f   %+6.1f%% %+7.2f" %
              (name, "yes" if dark else "no", hexs(ring), hexs(backdrop),
               hexs(rest_px), hexs(hover_px), d, pct, dl))
    return rows


def report_fix():
    """The same 10% of ink, spent as ALPHA on the ink's own colour.

    WithAlpha(onSurface, 26) is the token the sector is really painted over -
    the ink the page contrasts with - and 26/255 is 10.2%, the same weight the
    dark branch already asks for. On the light side nothing changes: ringFill is
    opaque there, so a mix and an alpha wash of the same weight land on the same
    pixel to within a rounding step.
    """
    print()
    print("with the wash carried on the INK's own colour instead")
    print("%-12s %-4s %-9s %-9s %7s   %7s %7s" %
          ("ground", "dark", "rest", "hover", "dY", "d%", "dL*"))
    print("-" * 71)
    for name, g in GROUNDS:
        t = theme(g)
        dark = t["dark"]
        ring = TRANSPARENT if dark else mix(t["surface"], g, 0.62)
        backdrop = over(SHADOW, g)
        wash = (26, t["onSurface"][1], t["onSurface"][2], t["onSurface"][3]) if dark \
            else mix(ring, t["onSurface"], HOVER_T_LIGHT)
        rest_px = over(ring, backdrop)
        hover_px = over(wash, backdrop)
        y0, y1 = luminance(rest_px), luminance(hover_px)
        d = y1 - y0
        pct = (d / y0 * 100) if y0 > 0 else float("inf")
        dl = to_lab(hover_px)[0] - to_lab(rest_px)[0]
        print("%-12s %-4s %-9s %-9s %+7.4f   %+6.1f%% %+7.2f" %
              (name, "yes" if dark else "no", hexs(rest_px), hexs(hover_px), d, pct, dl))


def check_panel_table():
    """Self-check: PageTheme's Surface for the five section 7 proof points."""
    print()
    print("section 7 check - the inner disc's fill, which must stay opaque and hued")
    for name, g in GROUNDS:
        t = theme(g)
        print("  %-12s ground %s -> surface %s  (dark=%s)" %
              (name, hexs(g), hexs(t["surface"]), t["dark"]))


if __name__ == "__main__":
    report()
    report_fix()
    check_panel_table()
