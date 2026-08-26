#!/usr/bin/env python3
"""CONCEPTS-REF 17.1 acceptance check: THE MEASUREMENT MENU'S TWO PADLOCKS.

There was no checker over this section, and that is how a completely dead
control shipped. A visual pass on the built app found BOTH padlocks inert -
pressed repeatedly, including at the centre UI Automation itself reported, and
every press fell through to the canvas and selected whatever was underneath.
The sibling preset chips worked first time.

UIA said why. The chips reported real bounds; the padlocks reported
ControlType.Group, 21x27 physical - the MARK, not the 26 DIP box the source
asks for - and NEITHER an Invoke nor a Toggle pattern. The cause was one line:

    var b = new ContentControl { Width = 26, Height = 26,
                                 Background = new SolidColorBrush(Colors.Transparent) };

The comment above it argued that Transparent hit-tests where null does not,
which is true - for something that DRAWS. A ContentControl has no default
template, so there is no element bound to Background, nothing is painted, and
nothing can be hit. That is the FOURTH time a transparent or null background has
silently killed a hit target in this codebase, so the rule that catches the
class of bug is asserted here and not just the instance.

None of this is a screenshot question. A capture of the panel looks identical
whether the padlock is a live control or a picture of one, which is exactly why
it survived to a visual pass. So the assertions below are structural, and the
one thing they cannot settle - that a real press really does land - is named as
unsettled rather than implied.

Run:  python tools/measurement_menu_check.py
Exit: 0 all assertions held, 1 otherwise.
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Quill")
MENU = os.path.join(SRC, "Controls", "MeasurementMenu.cs")
BARS = os.path.join(SRC, "Controls", "ChromeBars.cs")
CONTROLS = os.path.join(SRC, "Controls")

FAILURES = []
NOTES = []


def check(label, ok, detail=""):
    NOTES.append(("PASS" if ok else "FAIL", label, detail))
    if not ok:
        FAILURES.append(label)
    return ok


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))


def body_of(src, signature):
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


def const_of(src, name):
    """The numeric value of a `public const double NAME = x` in Metrics."""
    m = re.search(r"\b%s\s*=\s*([0-9.]+)" % name, src)
    return float(m.group(1)) if m else None


# ===========================================================================
# 1. THE RULE THAT HAS NOW BITTEN FOUR TIMES
# ===========================================================================
# A Background is a hit target only on something that draws one. Border, Grid,
# Panel and any templated Control do. ContentControl does NOT: it ships with no
# default template at all, so the brush is inert and so is the control.

def background_rule():
    offenders = []
    for name in sorted(os.listdir(CONTROLS)):
        if not name.endswith(".cs"):
            continue
        src = strip_comments(read(os.path.join(CONTROLS, name)))
        for m in re.finditer(r"new ContentControl\s*\{(.*?)\}", src, flags=re.S):
            block = m.group(1)
            if "Background" not in block:
                continue
            # A ContentControl explicitly taken OUT of hit testing is not
            # claiming to be a target and is not an offender.
            if "IsHitTestVisible = false" in block:
                continue
            offenders.append("%s: %s" % (name, " ".join(block.split())[:70]))
    check("a bare ContentControl is never given a Background as though it were "
          "a hit target - it has no template, so the brush paints nothing and "
          "hit-tests nothing",
          not offenders, "; ".join(offenders) or "no offender in src/Quill/Controls")


# ===========================================================================
# 2. THE PADLOCKS THEMSELVES
# ===========================================================================

def padlocks():
    src = read(MENU)
    stripped = strip_comments(src)
    lock = strip_comments(body_of(src, "private ToggleButton LockButton("))

    check("17.1 - the padlock is a real templated control, and a TOGGLE one: a "
          "padlock is two-state and this is the control whose automation peer "
          "says so",
          "new ToggleButton" in lock,
          "ToggleButton" if "new ToggleButton" in lock else "still a ContentControl")
    check("17.1 - and the fields that hold them are typed to it, so nothing can "
          "quietly hand back an untemplated control again",
          re.search(r"private ToggleButton _zoomLock", stripped) is not None and
          re.search(r"private ToggleButton _tiltLock", stripped) is not None)

    # The 26 DIP box has to SURVIVE measure. ToggleButton's default style carries
    # a MinWidth and MinHeight well above 26, and measure clamps a Width UP to
    # MinWidth - so Width alone would not have been the shipped size.
    box = const_of(src, "PadlockBox")
    check("17.1 - the target is a named metric rather than a literal, and it is "
          "the 26 DIP the row is tall",
          box == 26 and "Width = Metrics.PadlockBox" in lock
          and "Height = Metrics.PadlockBox" in lock,
          "PadlockBox = %s" % box)
    check("17.1 - MinWidth and MinHeight are zeroed, or the default style's "
          "minimums would clamp the 26 back up and the shipped box would not be "
          "the measured one",
          "MinWidth = 0" in lock and "MinHeight = 0" in lock)
    check("17.1 - the mark is smaller than its target: a 16 DIP glyph in a 26 "
          "DIP box, which is the whole distinction that was missed",
          const_of(src, "PadlockSize") == 16 and box > const_of(src, "PadlockSize"),
          "%s DIP mark in a %s DIP box" % (const_of(src, "PadlockSize"), box))

    # UI-REFERENCE 1.1: the panel is BARE. Making the control real must not drag
    # Fluent's card in with it - especially the checked state, which by default
    # is a solid accent fill and would read as a selected chip.
    strip = strip_comments(body_of(src, "private static void StripToggleChrome("))
    check("1.1 - the panel stays BARE: the rest background is transparent in "
          "BOTH the checked and unchecked states, so a locked padlock does not "
          "fill in like a selected chip",
          re.search(r'\("ToggleButtonBackground",\s*clear\)', strip) is not None and
          re.search(r'\("ToggleButtonBackgroundChecked",\s*clear\)', strip) is not None,
          "ToggleButtonBackground / ToggleButtonBackgroundChecked")
    check("1.1 - and the borders are cleared in every state, checked included",
          len(re.findall(r'\("ToggleButtonBorderBrush\w*",\s*clear\)', strip)) == 8,
          "%d border states cleared" % len(re.findall(r'\("ToggleButtonBorderBrush\w*",\s*clear\)', strip)))
    check("1.1 - it is done by overriding brushes rather than by replacing the "
          "ControlTemplate, so the template that makes it hittable and "
          "automatable is the framework's own",
          "ControlTemplate" not in stripped and "b.Resources[key]" in strip)
    check("17.1 - press still gives feedback: hover and press are Quill's own "
          "wash rather than nothing at all",
          "ChromeUi.Wash(0x14)" in strip and "ChromeUi.Wash(0x24)" in strip)

    # State has to travel BOTH ways, and the loop has to be broken.
    check("17.1 - a press reaches the host",
          "b.Checked +=" in lock and "b.Unchecked +=" in lock
          and "LockToggled" in lock)
    toggled = strip_comments(body_of(src, "private void LockToggled("))
    check("17.1 - and the repaint that follows does NOT press it again: moving "
          "IsChecked raises Checked exactly as a finger does",
          "if (_paintingLocks) return;" in toggled and "set(on);" in toggled)
    paint = strip_comments(body_of(src, "private void PaintLock("))
    check("17.1 - the host's answer is written back to IsChecked, which is what "
          "publishes ToggleState to UI Automation - the property that finally "
          "makes the two locks READABLE from outside the app",
          "host.IsChecked = locked" in paint and "_paintingLocks = true" in paint
          and "finally" in paint)
    check("17.1 - the lock state is still told the way 17.1 draws it, by the "
          "mark and its weight, not by a fill",
          "Icons.LockClosed : Icons.LockOpen" in paint
          and "ChromeUi.Ink : ChromeUi.Dim" in paint)
    check("17.1 - each padlock still names itself for a screen reader",
          'AutomationProperties.SetName(b, "Lock " + what)' in lock)

    # The (i) mark had the same defect in milder form: its whole job is to be
    # hovered, and on a bare ContentControl only the glyph's strokes could do it.
    build = strip_comments(body_of(src, "private void Build()"))
    check("17.1 - the (i) mark is a Border too, so its tooltip has a box to be "
          "found in rather than sixteen points of glyph",
          re.search(r"var info = new Border", build) is not None
          and "Width = Metrics.PadlockBox" in build)


# ===========================================================================
# 3. TWO INDEPENDENT LOCKS
# ===========================================================================
# 4.7: "Concepts uses two independent locks, one per value, not a single shared
# lock." This has never been verifiable on screen because neither lock worked.
# It is verifiable HERE, and always was.

def two_locks():
    src = read(BARS)
    stripped = strip_comments(src)
    # Brace-matched, not regex-bounded: a lazy `.*?` up to the next `},` runs
    # straight past this lambda and into the next member of the initialiser,
    # which makes a correct implementation look like it touches both flags.
    setz = body_of(stripped, "SetZoomLocked = on =>")
    sett = body_of(stripped, "SetTiltLocked = on =>")
    check("4.7 - locking ZOOM does not touch the tilt flag",
          setz != "" and "_zoomLocked = on" in setz and "_tiltLocked" not in setz,
          " ".join(setz.split())[:80] or "MISSING")
    check("4.7 - locking TILT does not touch the zoom flag",
          sett != "" and "_tiltLocked = on" in sett and "_zoomLocked" not in sett,
          " ".join(sett.split())[:80] or "MISSING")
    check("4.7 - and they are two separate fields, not two views of one",
          "private bool _zoomLocked;" in stripped and "private bool _tiltLocked;" in stripped)


# ===========================================================================
# 4. "LOCKING TILT SHIFTS THE ZOOM READOUT SIDEWAYS"
# ===========================================================================
# Recorded as an observation of Concepts and never once reproducible here,
# because the lock that would cause it could not be pressed. It does not need a
# screen: it is a consequence of four facts in the layout, and it can be
# COMPUTED from the same numbers the layout uses.
#
#   * the right cluster is anchored to the RIGHT edge of the window;
#   * inside it the ZOOM cell is built before the TILT cell, so zoom is to the
#     LEFT of tilt;
#   * the stadium Border wraps its row and is given no Width, so it grows;
#   * locking inserts a 12 DIP padlock mark into a row whose Spacing is 4.
#
# Anchored right, growth at the tilt cell can only be taken out of the left -
# so everything left of it, which is the zoom readout, moves left by that width.

def sideways_shift():
    src = read(BARS)
    stripped = strip_comments(src)

    check("17.1 - the right cluster is anchored to the RIGHT edge, so any growth "
          "inside it is taken out of the LEFT",
          re.search(r"_right = new Border\s*\{[^}]*?HorizontalAlignment\s*=\s*"
                    r"HorizontalAlignment\.Right", stripped, flags=re.S) is not None)
    readout = strip_comments(body_of(src, "private IEnumerable<FrameworkElement> BuildViewReadout()"))
    zoom_at = readout.find("_zoomText, _zoomLocked")
    tilt_at = readout.find("_tiltText, _tiltLocked")
    check("17.1 - the ZOOM cell is built before the TILT cell, so zoom sits to "
          "tilt's left and is what a growing tilt cell displaces",
          zoom_at != -1 and tilt_at != -1 and zoom_at < tilt_at,
          "zoom at %d, tilt at %d" % (zoom_at, tilt_at))

    cell = strip_comments(body_of(src, "private FrameworkElement ReadoutCell("))
    check("17.1 - the stadium is sized to its content: it wraps the row and is "
          "given a Height but no Width, which is what lets a padlock widen it",
          "var stadium = new Border" in cell
          and re.search(r"var stadium = new Border\s*\{[^}]*?Width\s*=", cell, flags=re.S) is None)
    check("17.1 - and the padlock is inserted BEFORE the value, matching the "
          "hover pill's own `[lock] 10%  [lock] 0deg` order",
          re.search(r"if \(locked\) row\.Children\.Add\(Icons\.Mark\(Icons\.LockClosed[^\n]*\);\s*\n\s*row\.Children\.Add\(value\);",
                    cell) is not None)

    mark = re.search(r"Icons\.Mark\(Icons\.LockClosed,\s*ChromeUi\.Ink,\s*([0-9.]+)\)", cell)
    spacing = re.search(r"Spacing\s*=\s*([0-9.]+)", cell)
    mark = float(mark.group(1)) if mark else None
    spacing = float(spacing.group(1)) if spacing else None
    shift = (mark + spacing) if (mark is not None and spacing is not None) else None
    check("17.1 - SO: locking tilt DOES shift the zoom readout sideways, and the "
          "shift is the padlock's width plus the row's gap",
          shift == 16,
          "%s DIP mark + %s DIP spacing = %s DIP left, = %s physical at 2x"
          % (mark, spacing, shift, None if shift is None else shift * 2))
    check("17.1 - and locking ZOOM does NOT move the tilt readout: tilt is to "
          "its right, and right of the growth nothing moves against a "
          "right-anchored cluster",
          zoom_at < tilt_at)

    NOTES.append(("INFO", "the shift above is COMPUTED from the layout, not "
                          "captured; a screen run should measure 32 physical px",
                  ""))


def main():
    background_rule()
    padlocks()
    two_locks()
    sideways_shift()

    width = max(len(l) for _s, l, _d in NOTES)
    for status, label, detail in NOTES:
        line = "%-4s %s" % (status, label.ljust(width))
        if detail:
            line += "   -- " + detail
        print(line)
    print()
    if FAILURES:
        print("FAILED %d check(s):" % len(FAILURES))
        for f in FAILURES:
            print("  - " + f)
        return 1
    print("OK - %d checks held." % sum(1 for s, _l, _d in NOTES if s == "PASS"))
    print("STILL UNSETTLED HERE: that a press LANDS. That needs the app up and a")
    print("UIA probe reading ControlType, BoundingRectangle and TogglePattern off")
    print("the live padlocks. Everything above is what can be known without one.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
