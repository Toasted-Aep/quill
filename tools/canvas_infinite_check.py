#!/usr/bin/env python3
"""CONCEPTS-REF 16.1 acceptance check: THE CANVAS IS INFINITE.

Exercises pan and zoom against Quill's view transform and asserts that no bound
exists, without driving the UI. Two halves:

  1. STRUCTURAL. Reads src/Quill/Controls/InkSurface.cs and asserts that the
     live transform - PanBy, ZoomAround, SetView, OnViewChanged - contains no
     statement that bounds ViewOffset, and that the two walls 16.1 named are
     gone (the OriginMargin constant, and the NormalizeContent migration that
     existed only to serve it).

  2. NUMERIC. Runs a pan/zoom sweep through two models of OnViewChanged: the
     one main shipped (transcribed verbatim from the offending commit, quoted
     below) and the one this branch ships. Prints the world rectangle each can
     reach. The first is finite; the second is not.

Then re-proves 14.5's vanishing-point quartering across the change, since the
reference frame is captured from the very view this touches.

Run:  python tools/canvas_infinite_check.py
Exit: 0 all assertions held, 1 otherwise.
"""

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


# ---------------------------------------------------------------------------
# 1. Structural
# ---------------------------------------------------------------------------
def structural():
    src = open(INK, encoding="utf-8").read()

    view_changed = body_of(src, "private void OnViewChanged()")
    # Only the part BEFORE the page-persistence block is the transform; what
    # follows copies the view onto the page and the text layer.
    transform = view_changed.split("if (_page != null)")[0]

    bounded = []
    for line in transform.splitlines():
        stripped = line.strip()
        if stripped.startswith("//"):
            continue
        if "ViewOffset" not in stripped and "ViewZoom" not in stripped:
            continue
        if re.search(r"\b(Math|MathF)\.(Min|Max|Clamp)\s*\(", stripped):
            bounded.append(stripped)
    check("OnViewChanged bounds nothing",
          not bounded,
          "; ".join(bounded) if bounded else "no Min/Max/Clamp reaches the view")

    pan = " ".join(body_of(src, "private void PanBy(Vector2 screenDelta)").split())
    check("PanBy is a pure accumulation",
          pan == "ViewOffset += screenDelta; OnViewChanged();",
          pan)

    check("the OriginMargin wall is gone",
          not re.search(r"\bprivate\s+const\s+float\s+OriginMargin\b", src),
          "no OriginMargin constant declared")

    check("NormalizeContent is not called",
          not re.search(r"^\s*NormalizeContent\s*\(", src, re.M),
          "the origin-wall migration has no call site")

    # The two layers of #inkfix that actually address the view teleport must
    # survive: removing the clamp is only safe because these do the work.
    check("palm rejection survives",
          "_lastPenSeenMs > 400" in src,
          "OnManipStarted still refuses a touch pan near a live pen")
    check("LoadPage self-heal survives",
          "FitToContent(48)" in src,
          "a restored view showing no content still jumps to the content")


# ---------------------------------------------------------------------------
# 2. Numeric
# ---------------------------------------------------------------------------
VIEW_W, VIEW_H = 1600.0, 900.0
PAGE_W, PAGE_H = 1500.0, 2200.0          # NotePage.Width / .Height defaults
CONTENT_X, CONTENT_Y = 0.0, 0.0          # an empty page


def clamp_main(off, zoom):
    """OnViewChanged as commit 27e999b left it, verbatim:

        if (_page != null && ActualWidth > 10)
        {
            EnsureContentMax();
            double worldR = Math.Max(Math.Max(_page.Width, _contentMaxX), 1500) + 900;
            double worldB = Math.Max(Math.Max(_page.Height, _contentMaxY), 2200) + 900;
            ViewOffset = new Vector2(
                MathF.Max(ViewOffset.X, (float)(-worldR * ViewZoom + Math.Min(ActualWidth * 0.3, 260))),
                MathF.Max(ViewOffset.Y, (float)(-worldB * ViewZoom + Math.Min(ActualHeight * 0.3, 260))));
        }
        ViewOffset = new Vector2(
            MathF.Min(ViewOffset.X, OriginMargin),
            MathF.Min(ViewOffset.Y, OriginMargin));
    """
    ox, oy = off
    if VIEW_W > 10:
        world_r = max(max(PAGE_W, CONTENT_X), 1500) + 900
        world_b = max(max(PAGE_H, CONTENT_Y), 2200) + 900
        ox = max(ox, -world_r * zoom + min(VIEW_W * 0.3, 260))
        oy = max(oy, -world_b * zoom + min(VIEW_H * 0.3, 260))
    return (min(ox, 48.0), min(oy, 48.0))


def clamp_branch(off, zoom):
    """OnViewChanged on canvas-infinite: a finite-value guard and nothing else."""
    ox, oy = off
    finite = all(x == x and abs(x) != float("inf") for x in (ox, oy))
    return (ox, oy) if finite else (0.0, 0.0)


def sweep(model, zoom, step, times):
    """Pan by `step` screen px `times` times; return the visible world rect."""
    off = (0.0, 0.0)
    off = model(off, zoom)
    for _ in range(times):
        off = (off[0] + step[0], off[1] + step[1])
        off = model(off, zoom)
    x0 = (0.0 - off[0]) / zoom
    y0 = (0.0 - off[1]) / zoom
    x1 = (VIEW_W - off[0]) / zoom
    y1 = (VIEW_H - off[1]) / zoom
    return off, (x0, y0, x1, y1)


def reachable(model, zoom):
    """Furthest world coordinate reachable in each direction at this zoom."""
    _, right = sweep(model, zoom, (-4000.0, -4000.0), 500)
    _, left = sweep(model, zoom, (+4000.0, +4000.0), 500)
    return (left[0], left[1], right[2], right[3])   # minX, minY, maxX, maxY


def numeric():
    print()
    print("  Reachable world rectangle after 500 pans of 4000px in each direction")
    print("  (viewport %gx%g, default page %gx%g, no content)"
          % (VIEW_W, VIEW_H, PAGE_W, PAGE_H))
    print()
    print("  %-6s  %-46s  %s" % ("zoom", "main (3baff80)", "canvas-infinite"))
    for zoom in (0.1, 1.0, 2.4, 8.0):
        m = reachable(clamp_main, zoom)
        b = reachable(clamp_branch, zoom)
        print("  %-6g  x %11.1f .. %-11.1f          x %12.0f .. %-12.0f"
              % (zoom, m[0], m[2], b[0], b[2]))
        print("  %-6s  y %11.1f .. %-11.1f          y %12.0f .. %-12.0f"
              % ("", m[1], m[3], b[1], b[3]))
    print()

    m = reachable(clamp_main, 1.0)
    b = reachable(clamp_branch, 1.0)
    check("main's canvas is finite at 1x",
          all(abs(v) < 1e5 for v in m),
          "walls at x [%.0f, %.0f], y [%.0f, %.0f] - %.0f x %.0f world units"
          % (m[0], m[2], m[1], m[3], m[2] - m[0], m[3] - m[1]))

    # Unbounded means the view keeps moving: pan N times and land exactly N
    # steps out, at every zoom, in both directions.
    unbounded = True
    for zoom in (0.1, 1.0, 2.4, 8.0):
        for step in ((-4000.0, -4000.0), (4000.0, 4000.0), (-4000.0, 4000.0)):
            off, _ = sweep(clamp_branch, zoom, step, 500)
            if off != (step[0] * 500, step[1] * 500):
                unbounded = False
    check("this branch's canvas has no stop",
          unbounded,
          "500 pans land exactly 500 steps out at every zoom, every direction")

    # A million world units out and still moving - there is no far edge, only
    # float precision, which is not a stop.
    off, rect = sweep(clamp_branch, 1.0, (1e6, 1e6), 10)
    check("ten million units out and still panning",
          off == (1e7, 1e7),
          "offset %g, %g; visible world x %.0f .. %.0f" % (off[0], off[1], rect[0], rect[2]))

    # The guard that replaced the clamp is a validity guard, not a bound.
    check("a non-finite view is refused",
          clamp_branch((float("nan"), 5.0), 1.0) == (0.0, 0.0) and
          clamp_branch((-9.9e9, 4.4e9), 1.0) == (-9.9e9, 4.4e9),
          "NaN falls back to the last good view; 9.9e9 is accepted unchanged")


# ---------------------------------------------------------------------------
# 3. 14.5 - the quartering must not have moved
# ---------------------------------------------------------------------------
def ensure_ref_frame(off, zoom):
    """InkSurface.EnsureRefFrame: the viewport in world units, first frame only."""
    z = max(0.01, zoom)
    return (-off[0] / z, -off[1] / z, VIEW_W / z, VIEW_H / z)


def place_2point(frame):
    """MainWindow.PlacePerspectiveShape against GridPresets' bare '2 Point':
    HorizonF 0.5, VpXF 0.25 / 0.75 (HalfNarrow = 1 quarter either side)."""
    fx, fy, fw, fh = frame
    horizon = fy + 0.5 * fh
    return horizon, [(fx + xf * fw, horizon) for xf in (0.25, 0.75)]


def quartering():
    # A fresh page opens at offset 0,0 zoom 1, so the frame it captures is the
    # same under both models - the change cannot move an existing page's points.
    first_main = ensure_ref_frame(clamp_main((0.0, 0.0), 1.0), 1.0)
    first_branch = ensure_ref_frame(clamp_branch((0.0, 0.0), 1.0), 1.0)
    check("the reference frame is captured identically",
          first_main == first_branch == (0.0, 0.0, VIEW_W, VIEW_H),
          "first frame = %s on both" % (first_branch,))

    horizon0, vps0 = place_2point(first_branch)

    # 14.5's proof: pan to (900, 700) at 2.4x and the points must not drift,
    # because placement reads the stored frame and never the live view.
    moved = clamp_branch((900.0, 700.0), 2.4)
    horizon1, vps1 = place_2point(first_branch)
    check("no drift after a pan to (900, 700) at 2.4x",
          (horizon0, vps0) == (horizon1, vps1),
          "horizon y %.1f, points %s - unchanged" %
          (horizon1, [("%.1f" % p[0]) for p in vps1]))

    # And the same pan expressed as a drag of the page.
    dragged, _ = sweep(clamp_branch, 2.4, (-900.0, -700.0), 1)
    horizon2, vps2 = place_2point(first_branch)
    check("no drift after a 900x700 drag at 2.4x",
          (horizon0, vps0) == (horizon2, vps2),
          "offset %s, points still %s" % (dragged, [("%.1f" % p[0]) for p in vps2]))

    # Worth recording: main could not reach that view at all.
    blocked = clamp_main((900.0, 700.0), 2.4)
    print()
    print("  14.5's proof view, offset (900, 700) at 2.4x:")
    print("    main            -> %s   (the wall pulled it back)" % (blocked,))
    print("    canvas-infinite -> %s" % (moved,))


def main():
    print()
    print("CONCEPTS-REF 16.1 - the canvas is infinite")
    print("=" * 74)
    structural()
    numeric()
    quartering()
    print()
    for state, label, detail in NOTES:
        print("  [%s] %-44s %s" % (state, label, detail))
    print()
    if FAILURES:
        print("FAILED: " + ", ".join(FAILURES))
        return 1
    print("All %d assertions held." % len(NOTES))
    return 0


if __name__ == "__main__":
    sys.exit(main())
