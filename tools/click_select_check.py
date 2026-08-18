#!/usr/bin/env python3
"""CONCEPTS-REF 16.10 acceptance check: CLICK TO SELECT, WITHOUT DRAGGING.

"make just holding selection button on pen and clicking (not dragging to
select) select the stroke."

Proves the gesture without driving the UI, the way tools/canvas_infinite_check.py
proves the view transform. Three halves:

  1. STRUCTURAL. Reads src/Quill/Controls/InkSurface.cs and asserts the
     machinery exists where it has to: one screen-space slop constant shared
     with the barrel button's own tap-vs-drag test, a hit test that pads by the
     stroke's own size and walks the page backwards for the topmost stroke, the
     three arming sites where selection is the active modality, a release path
     that tests the click BEFORE the barrel context menu, and - the trap this
     file has fallen into before - not one extra `e.Handled = true` in
     OnPointerPressed.

  2. NUMERIC. Two models of the press/move/release machine: the one `main`
     ships (transcribed from the untouched source, quoted below) and the one
     this branch ships. A press-release within tolerance of a stroke selects on
     this branch and does nothing on main; a press-release past tolerance
     selects on neither; a press-move-release lassoes on both, unchanged.

  3. ZOOM. The same physical hand movement and the same physical miss distance
     are classified identically at 0.1x and at 16x, which is the whole reason
     the threshold and the reach live in screen space. A world-space threshold
     is run alongside to show what it would have got wrong.

Run:  python tools/click_select_check.py
Exit: 0 all assertions held, 1 otherwise.
"""

import math
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INK = os.path.join(ROOT, "src", "Quill", "Controls", "InkSurface.cs")

FAILURES = []
NOTES = []


def check(label, ok, detail=""):
    NOTES.append(("PASS" if ok else "FAIL", label, detail))
    if not ok:
        FAILURES.append(label)
    return ok


def body_of(src, signature):
    """Text of a method body, brace-matched from its signature."""
    i = src.index(signature)
    i = src.index("{", i)
    depth, j = 0, i
    while j < len(src):
        if src[j] == "{":
            depth += 1
        elif src[j] == "}":
            depth -= 1
            if depth == 0:
                return src[i + 1:j]
        j += 1
    raise AssertionError("unbalanced braces after " + signature)


def safe_body(src, signature):
    """body_of, but an absent method reads as an empty body so the check that
    wanted it FAILS with a message instead of blowing the script up. This
    script must run - and fail - against main, where none of 16.10 exists."""
    try:
        return body_of(src, signature)
    except ValueError:
        return ""


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))


# ---------------------------------------------------------------------------
# 1. Structural
# ---------------------------------------------------------------------------
# e.Handled = true appears exactly this many times in OnPointerPressed on main.
# 16.10 adds a click-to-select route through the press handler and must not add
# a twelfth: a PointerPressed marked handled has silently killed input in this
# file before (the pen row's right-click never once fired).
PRESSED_HANDLED_ON_MAIN = 11


def structural():
    src = open(INK, encoding="utf-8").read()

    slop = re.search(r"private\s+const\s+float\s+ClickSlopPx\s*=\s*([0-9.]+)f\s*;", src)
    check("a screen-space slop constant exists",
          slop is not None,
          "ClickSlopPx = %s px" % (slop.group(1) if slop else "MISSING"))

    pad = re.search(r"private\s+const\s+float\s+ClickHitPadPx\s*=\s*([0-9.]+)f\s*;", src)
    check("a screen-space hit pad constant exists",
          pad is not None,
          "ClickHitPadPx = %s px" % (pad.group(1) if pad else "MISSING"))

    # One definition, not three: the barrel button's own tap-vs-drag test must
    # now read the same constant rather than repeating the literal 8f.
    moved = strip_comments(safe_body(src, "private void OnPointerMoved("))
    barrel_line = [l.strip() for l in moved.splitlines() if "_barrelStartScreen" in l]
    click_line = [l.strip() for l in moved.splitlines() if "_clickSelectStartScreen" in l]
    check("the barrel's tap-vs-drag test reads the shared constant",
          bool(barrel_line) and all("ClickSlopPx" in l for l in barrel_line),
          "; ".join(barrel_line) or "no barrel movement test found")
    check("click-vs-drag is decided in SCREEN space",
          bool(click_line) and
          all("ClickSlopPx" in l and "ViewZoom" not in l for l in click_line),
          "; ".join(click_line) or "no click movement test found")
    check("click-vs-drag is decided by movement, not by a clock",
          bool(click_line) and
          not any(re.search(r"TickCount|_holdTimer|Stopwatch|DateTime", l) for l in click_line),
          "no timer term reaches the test - a held press that never moves is a click")

    hit = strip_comments(safe_body(src, "private PenStroke? HitStrokeForClick("))
    check("the hit test pads by the stroke's own size",
          re.search(r"\breach\s*\+\s*s\.Size\b", hit) is not None,
          "pad = reach + s.Size, the rule FindStrokeNear already uses")
    check("the hit test's reach is screen pixels turned into world units",
          "ClickHitPadPx / ViewZoom" in hit,
          "reach = ClickHitPadPx / ViewZoom")
    check("overlap resolves to the TOPMOST stroke",
          re.search(r"for\s*\(\s*int\s+i\s*=\s*_page\.Strokes\.Count\s*-\s*1\s*;\s*i\s*>=\s*0\s*;\s*i--\s*\)", hit)
          is not None,
          "the page is walked backwards, so the last-painted stroke answers first")

    # Armed wherever selection is the active modality: the Select tool, the pen
    # barrel button, and the mouse's Select mode.
    arms = re.findall(r"ArmClickSelect\(", src)
    check("click-to-select is armed from three modality sites",
          len(arms) == 4,   # 1 declaration + 3 call sites
          "%d call sites (Select tool, pen barrel, MouseMode.Select)" % (len(arms) - 1))

    pressed = safe_body(src, "private void OnPointerPressed(")
    mousepress = safe_body(src, "private void HandleMousePress(")
    check("the Select tool arms it",
          "ArmClickSelect(" in pressed and "case ToolType.Select:" in pressed,
          "OnPointerPressed's Select arm")
    check("the pen barrel button arms it",
          pressed.count("ArmClickSelect(") == 2,
          "barrel arm + Select-tool arm both live in OnPointerPressed")
    check("the mouse's Select mode arms it",
          "ArmClickSelect(" in mousepress,
          "HandleMousePress's rubber-band tail")

    check("no extra e.Handled in OnPointerPressed",
          len(re.findall(r"e\.Handled\s*=\s*true", pressed)) == PRESSED_HANDLED_ON_MAIN,
          "%d, same as main - nothing downstream lost its event"
          % len(re.findall(r"e\.Handled\s*=\s*true", pressed)))

    commit = strip_comments(safe_body(src, "private void CommitGesture()"))
    i_click = commit.find("_clickSelect && !_clickSelectMoved")
    i_barrel = commit.find("_barrelGesture && !_barrelMoved")
    check("the click is tested before the barrel context menu",
          i_click != -1 and i_barrel != -1 and i_click < i_barrel,
          "a barrel tap ON a stroke selects it; on empty canvas the menu still opens")

    reset = strip_comments(safe_body(src, "private void ResetGesture()"))
    check("the gesture reset disarms it",
          "_clickSelect" in reset,
          "no click state survives into the next gesture")

    # The lasso is untouched: a drag still ends in SelectWithLasso.
    check("a drag still lassoes",
          "SelectWithLasso(_lasso)" in commit and "_lasso is { Count: > 2 }" in commit,
          "the drag path in CommitGesture is exactly as main left it")


# ---------------------------------------------------------------------------
# 2. Numeric - the gesture machine
# ---------------------------------------------------------------------------
CLICK_SLOP_PX = 8.0        # InkSurface.ClickSlopPx
CLICK_HIT_PAD_PX = 10.0    # InkSurface.ClickHitPadPx


def dist_seg(p, a, b):
    """GeometryUtil.DistToSegment."""
    abx, aby = b[0] - a[0], b[1] - a[1]
    len2 = abx * abx + aby * aby
    if len2 < 1e-6:
        return math.dist(p, a)
    t = max(0.0, min(1.0, ((p[0] - a[0]) * abx + (p[1] - a[1]) * aby) / len2))
    return math.dist(p, (a[0] + abx * t, a[1] + aby * t))


def hit_stroke_for_click(strokes, p, zoom):
    """InkSurface.HitStrokeForClick: topmost stroke within reach + its own size.

        float reach = ClickHitPadPx / ViewZoom;
        for (int i = _page.Strokes.Count - 1; i >= 0; i--)
        {
            var s = _page.Strokes[i];
            float pad = reach + s.Size;
            ... DistToSegment(p, ...) <= pad -> return s;
        }
    """
    reach = CLICK_HIT_PAD_PX / zoom
    for idx in range(len(strokes) - 1, -1, -1):
        s = strokes[idx]
        pts = s["points"]
        if not pts:
            continue
        pad = reach + s["size"]
        xs = [q[0] for q in pts]
        ys = [q[1] for q in pts]
        if p[0] < min(xs) - pad or p[0] > max(xs) + pad:
            continue
        if p[1] < min(ys) - pad or p[1] > max(ys) + pad:
            continue
        if len(pts) == 1:
            if math.dist(p, pts[0]) <= pad:
                return s["name"]
            continue
        for j in range(1, len(pts)):
            if dist_seg(p, pts[j - 1], pts[j]) <= pad:
                return s["name"]
    return None


def to_world(screen, offset, zoom):
    return ((screen[0] - offset[0]) / zoom, (screen[1] - offset[1]) / zoom)


def gesture_branch(strokes, path_screen, zoom, offset=(0.0, 0.0), barrel=False):
    """click-select: the Select modality's press/move/release, this branch.

    Press arms the click, every move past ClickSlopPx of the press point (in
    SCREEN pixels) disarms it into a drag, release decides.
    """
    start = path_screen[0]
    moved = any(math.dist(pt, start) > CLICK_SLOP_PX for pt in path_screen[1:])
    if not moved:
        who = hit_stroke_for_click(strokes, to_world(start, offset, zoom), zoom)
        if who is not None:
            return ("select", who)
        # No stroke under the click. The barrel keeps its context menu (#44);
        # the Select tool deselects, which the press already did.
        return ("menu",) if barrel else ("deselect",)
    poly = [to_world(pt, offset, zoom) for pt in path_screen]
    return ("lasso", tuple(sorted(lasso_catch(strokes, poly))))


def gesture_main(strokes, path_screen, zoom, offset=(0.0, 0.0), barrel=False):
    """The same machine on main, transcribed from the untouched source:

        case ToolType.Select:
            if (_barrelGesture && !_barrelMoved)
            {
                _lasso = null;
                ContextMenuRequested?.Invoke(...);   // a barrel tap: menu only
                break;
            }
            ...
            else if (_lasso is { Count: > 2 })
                SelectWithLasso(_lasso);

    A click selects nothing, because nothing looks under it: the lasso needs
    three points and the barrel tap goes straight to the menu.
    """
    start = path_screen[0]
    moved = any(math.dist(pt, start) > CLICK_SLOP_PX for pt in path_screen[1:])
    if not moved:
        return ("menu",) if barrel else ("deselect",)
    poly = [to_world(pt, offset, zoom) for pt in path_screen]
    return ("lasso", tuple(sorted(lasso_catch(strokes, poly))))


def point_in_polygon(p, poly):
    """GeometryUtil.PointInPolygon."""
    inside = False
    n = len(poly)
    j = n - 1
    for i in range(n):
        if (poly[i][1] > p[1]) != (poly[j][1] > p[1]) and \
           p[0] < (poly[j][0] - poly[i][0]) * (p[1] - poly[i][1]) / \
                  (poly[j][1] - poly[i][1]) + poly[i][0]:
            inside = not inside
        j = i
    return inside


def lasso_catch(strokes, poly):
    """SelectWithLasso with SelectPartial = true (the default)."""
    if len(poly) <= 2:
        return []
    return [s["name"] for s in strokes
            if any(point_in_polygon(q, poly) for q in s["points"])]


# A hairline and a fat marker, crossing. Later in the list = painted on top.
HAIRLINE = {"name": "hairline", "size": 1.0,
            "points": [(200.0, 400.0), (600.0, 400.0)]}
MARKER = {"name": "marker", "size": 24.0,
          "points": [(400.0, 200.0), (400.0, 600.0)]}
PAGE = [HAIRLINE, MARKER]


def press_release(at_screen, jitter=()):
    """A press and a release at the same place, with optional hand jitter."""
    return [at_screen] + [(at_screen[0] + dx, at_screen[1] + dy) for dx, dy in jitter] \
           + [at_screen]


def numeric():
    print()
    print("  The gesture, at 1x, on a page holding a 1px hairline and a 24px marker")
    print()
    print("  %-44s  %-22s  %s" % ("gesture", "main (6010ae4)", "click-select"))

    rows = []

    def run(label, path, zoom=1.0, barrel=False, offset=(0.0, 0.0)):
        m = gesture_main(PAGE, path, zoom, offset, barrel)
        b = gesture_branch(PAGE, path, zoom, offset, barrel)
        rows.append((label, m, b))
        print("  %-44s  %-22s  %s" % (label, m[0] + (":" + str(m[1]) if len(m) > 1 else ""),
                                      b[0] + (":" + str(b[1]) if len(b) > 1 else "")))
        return m, b

    # 1. Dead on the hairline, no movement at all.
    m, b = run("click exactly on the hairline", press_release((300.0, 400.0)))
    check("a click on a stroke selects it",
          b == ("select", "hairline"), "press and release at (300, 400), zero movement")
    check("main selects nothing from that click",
          m == ("deselect",), "main has no click route - the lasso needs 3 points")

    # 2. Held still but jittering, as a stylus always does.
    m, b = run("held press, 5px of stylus jitter",
               press_release((300.0, 400.0), [(3, 2), (-4, 1), (5, -2), (0, 3)]))
    check("a HELD press that never really moves is still a click",
          b == ("select", "hairline"), "5px of jitter stays under the 8px slop")

    # 3. Just off the path - inside tolerance.
    m, b = run("click 8px off the hairline's path", press_release((300.0, 408.0)))
    check("a near miss on a hairline still selects it",
          b == ("select", "hairline"),
          "8 world units out; tolerance is 10/zoom + size = 11")

    # 4. Well off the path - outside tolerance.
    m, b = run("click 40px off the hairline's path", press_release((300.0, 440.0)))
    check("a click past tolerance selects nothing",
          b == ("deselect",), "40 world units out, tolerance 11 - empty canvas")

    # 5. A real drag still lassoes, exactly as before.
    box = [(180.0, 380.0), (640.0, 380.0), (640.0, 420.0), (180.0, 420.0), (180.0, 380.0)]
    m, b = run("drag a lasso round the hairline", box)
    check("a drag still lassoes, unchanged",
          b == m and b[0] == "lasso" and b[1] == ("hairline",),
          "both models catch exactly the hairline")

    # 6. A drag that only just qualifies.
    m, b = run("drag 9px, past the slop", press_release((300.0, 400.0), [(9, 0)]))
    check("9px of travel is a drag, not a click",
          b[0] == "lasso" and b == m, "past ClickSlopPx, so the lasso path runs")

    # 7. Overlap: the crossing point belongs to the topmost stroke.
    m, b = run("click where hairline and marker cross", press_release((400.0, 400.0)))
    check("overlap resolves to the topmost stroke",
          b == ("select", "marker"),
          "marker is later in Strokes, so it paints on top and answers first")

    # 8. Tolerance really does follow the stroke's own size.
    off_marker = hit_stroke_for_click(PAGE, (420.0, 300.0), 1.0)
    off_hairline = hit_stroke_for_click(PAGE, (300.0, 420.0), 1.0)
    check("a fat stroke is hittable across its own width",
          off_marker == "marker" and off_hairline is None,
          "20 units off a 24px marker hits; 20 off a 1px hairline does not")

    # 9. The barrel button's existing gesture survives on empty canvas.
    m, b = run("barrel TAP on empty canvas", press_release((900.0, 900.0)), barrel=True)
    check("a barrel tap on empty canvas still opens the menu",
          b == m == ("menu",), "#44 is intact - the click only claims strokes")
    m, b = run("barrel TAP on the hairline", press_release((300.0, 400.0)), barrel=True)
    check("a barrel tap ON a stroke selects instead of opening the menu",
          b == ("select", "hairline") and m == ("menu",),
          "this is 16.10's headline case: hold the pen's select button and click")


# ---------------------------------------------------------------------------
# 3. Zoom - why screen space, over a 0.1x .. 16x canvas
# ---------------------------------------------------------------------------
WORLD_SLOP = 8.0   # the same number, had it been left in world units


def zoom_half():
    print()
    print("  The same physical gesture at every zoom the canvas allows")
    print()
    print("  %-6s  %-28s  %-18s  %s" % ("zoom", "5 screen px of jitter",
                                        "reach on a hairline", "world-space slop would say"))

    screen_ok, world_ok = True, True
    reaches = []
    for zoom in (0.1, 0.25, 1.0, 4.0, 16.0):
        # A hand that wobbles 5 screen px, wherever the zoom happens to be.
        path = press_release((300.0, 400.0), [(3, 4)])
        # Place the stroke under that screen point at this zoom. The page holds
        # the hairline alone: this half is about the CLASSIFICATION of the
        # gesture, and a second stroke would only ask the separate question of
        # which one is nearest.
        offset = (300.0 - 300.0 * zoom, 400.0 - 400.0 * zoom)
        b = gesture_branch([HAIRLINE], path, zoom, offset)
        if b != ("select", "hairline"):
            screen_ok = False
        # The same wobble measured in world units, against a world threshold.
        world_jitter = 5.0 / zoom
        world_says = "drag" if world_jitter > WORLD_SLOP else "click"
        if world_says != "click":
            world_ok = False
        # How far off the path, in SCREEN pixels, a hairline still answers.
        reach_world = CLICK_HIT_PAD_PX / zoom + HAIRLINE["size"]
        reaches.append(reach_world * zoom)
        print("  %-6g  %-28s  %-18s  %s"
              % (zoom, b[0], "%.1f screen px" % (reach_world * zoom), world_says))

    check("the same hand jitter is a click at every zoom",
          screen_ok, "0.1x through 16x, all click - the threshold is screen px")
    check("a world-space threshold would have got this wrong",
          not world_ok,
          "5 screen px is %.0f world units at 0.1x, which a world slop of %g calls a drag"
          % (5.0 / 0.1, WORLD_SLOP))
    check("the reach under the tip barely moves across 160x of zoom",
          max(reaches) - min(reaches) < 20.0,
          "%.1f .. %.1f screen px (the spread is the stroke's own painted width)"
          % (min(reaches), max(reaches)))


def main():
    print()
    print("CONCEPTS-REF 16.10 - click to select, without dragging")
    print("=" * 74)
    structural()
    numeric()
    zoom_half()
    print()
    for state, label, detail in NOTES:
        print("  [%s] %-48s %s" % (state, label, detail))
    print()
    if FAILURES:
        print("FAILED: " + ", ".join(FAILURES))
        return 1
    print("All %d assertions held." % len(NOTES))
    return 0


if __name__ == "__main__":
    sys.exit(main())
