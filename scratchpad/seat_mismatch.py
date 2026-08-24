"""Which pen inks survive a contrast test aimed at the WRONG surface.

Two sites decide whether a pen's mark keeps the pen's own colour by the same
rule - "fall back to the foreground ink only when the contrast genuinely
collapses" - written as

    Math.Abs(Lum(ink) - Lum(seat)) < 0.14 ? fg : ink

and both name a seat the mark is not always standing on:

  PenBar.Art          seat = PageTheme.Surface, always. But BuildCell fills the
                      ACTIVE cell with Mix(Surface, OnSurface, 0.16) whenever
                      PageTheme.IsDark - 9.1 wants a filled, lighter cell on a
                      dark ground - so on that one cell the mark sits 0.16 of
                      the way to the ink, not on Surface.

  ToolWheel.PenStroke seat = PageTheme.Surface off the popped sector. That is
                      the INNER DISC's token; a ring mark is never on the disc,
                      and on a dark ground Section 7 takes the ring's fill away
                      so the mark sits on the page. (Being fixed on
                      dial-and-colour under 17.4 - measured here for comparison,
                      not to be fixed twice.)

The test's own threshold is the yardstick: an ink is judged legible if it is
0.14 of Lum's scale away from its seat. This reports how much of that 0.14 the
surviving inks actually have against the surface they are really on.

Run:  python scratchpad/seat_mismatch.py
"""

from hover_wash import theme, luminance, GROUNDS, hexs

THRESH = 0.14
ACTIVE_MIX = 0.16   # PenBar.BuildCell's filled active cell
SEAT_ALPHA = 60     # the floor PenStrokeMark clamps a pen's opacity to


def naive_lum(c):
    """PenBar.Lum and ToolWheel.Lum, byte-weighted and NOT gamma-corrected.

    Both are private copies of the same three coefficients. This is deliberately
    not PageTheme.Luminance: the 0.14 threshold was calibrated against these
    numbers, so measuring the hole in a different space would not describe the
    code that ships.
    """
    return (0.2126 * c[1] + 0.7152 * c[2] + 0.0722 * c[3]) / 255.0


def mix255(a, b, t):
    return (255,) + tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in (1, 2, 3))


def ratio(a, b):
    la, lb = luminance(a), luminance(b)
    hi, lo = max(la, lb), min(la, lb)
    return (hi + 0.05) / (lo + 0.05)


def grey_at(target):
    """The neutral whose naive Lum is nearest `target` - a stand-in pen ink."""
    best, bd = None, 9e9
    for v in range(256):
        c = (255, v, v, v)
        d = abs(naive_lum(c) - target)
        if d < bd:
            best, bd = c, d
    return best


def penbar():
    print("PenBar.Art - the ACTIVE cell, dark grounds only (BuildCell's `filled`)")
    print("%-12s %-9s %-9s %6s   %-9s %7s %7s   %6s %6s" %
          ("ground", "tested", "real seat", "gap", "worst ink", "have", "need", "C real", "C test"))
    print("-" * 100)
    worst = None
    for name, g in GROUNDS:
        t = theme(g)
        if not t["dark"]:
            continue
        surface, on = t["surface"], t["onSurface"]
        seat = mix255(surface, on, ACTIVE_MIX)
        ls, lk = naive_lum(surface), naive_lum(seat)
        gap = lk - ls

        # The survivors sit at least THRESH from `surface`. The ones that lose
        # most by being measured there are on the side the seat moved toward:
        # the closest survivor above surface is exactly surface + THRESH.
        ink = grey_at(ls + THRESH)
        have = abs(naive_lum(ink) - lk)
        c_real, c_test = ratio(ink, seat), ratio(ink, surface)
        print("%-12s %-9s %-9s %+6.4f   %-9s %7.4f %7.4f   %5.2f:1 %5.2f:1" %
              (name, hexs(surface), hexs(seat), gap, hexs(ink), have, THRESH, c_real, c_test))
        if worst is None or have < worst[1]:
            worst = (name, have, c_real, c_test)
    print()
    print("  worst: %s keeps its ink with %.4f of the %.2f its own test demands "
          "(%.0f%% of it), at %.2f:1 against the seat it is on versus %.2f:1 "
          "against the one it was measured on." %
          (worst[0], worst[1], THRESH, worst[1] / THRESH * 100, worst[2], worst[3]))


def toolwheel():
    print()
    print("ToolWheel.PenStrokeMark - a ring sector, dark grounds only (Section 7: no ring fill)")
    print("%-12s %-9s %-9s %6s   %-9s %7s %7s   %6s %6s" %
          ("ground", "tested", "real seat", "gap", "worst ink", "have", "need", "C real", "C test"))
    print("-" * 100)
    worst = None
    for name, g in GROUNDS:
        t = theme(g)
        if not t["dark"]:
            continue
        surface = t["surface"]
        seat = g                       # ringFill is Transparent: the mark is on the page
        ls, lk = naive_lum(surface), naive_lum(seat)
        gap = lk - ls
        # Here the seat is DARKER than the token, so the survivors that lose out
        # are the ones just below surface.
        ink = grey_at(ls - THRESH)
        have = abs(naive_lum(ink) - lk)
        c_real, c_test = ratio(ink, seat), ratio(ink, surface)
        print("%-12s %-9s %-9s %+6.4f   %-9s %7.4f %7.4f   %5.2f:1 %5.2f:1" %
              (name, hexs(surface), hexs(seat), gap, hexs(ink), have, THRESH, c_real, c_test))
        if worst is None or have < worst[1]:
            worst = (name, have, c_real, c_test)
    print()
    print("  worst: %s keeps its ink with %.4f of the %.2f its own test demands "
          "(%.0f%% of it), at %.2f:1 against the page versus %.2f:1 against the disc." %
          (worst[0], worst[1], THRESH, worst[1] / THRESH * 100, worst[2], worst[3]))
    print("  and the mark is then painted at the pen's own alpha, floor %d/255 = %.0f%%,"
          % (SEAT_ALPHA, SEAT_ALPHA / 255 * 100))
    print("  so what is left of that contrast is composited onto the page as well.")


if __name__ == "__main__":
    penbar()
    toolwheel()
