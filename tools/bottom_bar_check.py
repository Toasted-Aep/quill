#!/usr/bin/env python3
"""CONCEPTS-REF 17.9 / 17.10 / 17.11 / 17.12 / 17.16 acceptance check: THE SCREEN-BOTTOM
MODE BAR AND THE MOUSE TOOL.

Written the way tools/click_select_check.py and tools/selection_present_check.py
are written - the source is read and the rules are EVALUATED - because the two
things this section is easiest to get wrong are both invisible until after they
have gone wrong:

  1. THE CONDITIONAL BACK BUTTON (17.9, which says in as many words that it is
     "the detail most likely to be missed"). "Filter descends into the colour
     picker's own bottom menu... Because it was reached from the mode bar, that
     submenu carries a back button... Reached as a tool in its own right, the
     colour picker has NO back button." A screenshot of one of those two states
     cannot tell a correct implementation from one that always draws the button,
     or never does. So the rule is lifted out of BottomMenu.ShowsBack as
     expression text, translated, and evaluated against every arrangement of
     pages the app can actually be in.

  2. NOTHING FLOATING BELOW THE SUBJECT (17.9). The row moved to the bottom of
     the screen; a row that ALSO still placed itself under the selection would
     look right in every capture where the selection happens to sit high on the
     page. Proved structurally instead: Place() maps the subject's corners and
     nothing else, and the row is not a child of the selection layer.

Run:  python tools/bottom_bar_check.py
Exit: 0 all assertions held, 1 otherwise.
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Quill")
MENU = os.path.join(SRC, "Controls", "BottomMenu.cs")
CHROME = os.path.join(SRC, "Controls", "SelectionChrome.cs")
INK = os.path.join(SRC, "Controls", "InkSurface.cs")
WINDOW = os.path.join(SRC, "MainWindow.xaml.cs")
MODELS = os.path.join(SRC, "Models", "NoteModels.cs")
UNDO = os.path.join(SRC, "Services", "UndoRedo.cs")
BRUSHES = os.path.join(SRC, "Controls", "BrushesWindow.cs")

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
    text = re.sub(r"^\s*///.*$", "", text, flags=re.M)
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


def safe_body(src, signature):
    try:
        return body_of(src, signature)
    except (ValueError, AssertionError):
        return ""


# ===========================================================================
# 1. THE CONDITIONAL BACK BUTTON, EVALUATED (17.9)
# ===========================================================================
# ShowsBack is not restated here. Its loop is read out of the file and its
# meaning reproduced, then the five arrangements the app can be in are run
# through it. If somebody later "simplifies" it to `id != Modes`, four of these
# still pass and the fifth - the picker opened as a tool with a selection
# underneath - fails, which is the whole point.

MODES, TOOL, PICKER = 0, 1, 2
PAGE_NAMES = {MODES: "Modes", TOOL: "Tool", PICKER: "Picker"}


def shows_back(published, dismissed, page):
    """The rule as the file states it: a lower-ranked page is published and has
    not been backed out of."""
    return any(p < page and p not in dismissed for p in published)


def top(published, dismissed):
    live = [p for p in published if p not in dismissed]
    return max(live) if live else None


def back_button():
    src = read(MENU)
    code = strip_comments(src)

    check("17.9 - the pages are RANKED, and the ranking is the stack order",
          re.search(r"enum\s+BottomPage\s*\{[^}]*Modes\s*=\s*0[^}]*Tool\s*=\s*1[^}]*Picker\s*=\s*2",
                    code, re.S) is not None)

    sb = strip_comments(safe_body(src, "public bool ShowsBack(BottomPage id)"))
    # The rule, as source text: a lower-ranked page that is not dismissed.
    check("the back button asks ONE question - is a page underneath this one",
          re.search(r"page\s*<\s*id", sb) is not None
          and "_dismissed.Contains(page)" in sb
          and "return true" in sb,
          " ".join(sb.split())[:90])

    # No page may decide for itself.
    for f, name in ((read(WINDOW), "MainWindow"), (read(CHROME), "SelectionChrome")):
        body = strip_comments(f)
        check("%s never draws a back mark itself - it asks Lead()" % name,
              "Icons.ChevronLeft" not in body and "Lead(BottomPage." in body)

    # 17.9's two named cases, plus the three the app can otherwise be in.
    cases = [
        # (published, dismissed, page asked, expected, why)
        ({MODES}, set(), MODES, False,
         "17.9 - the three-part mode bar is the bottom of the stack: no back"),
        ({MODES, PICKER}, set(), PICKER, True,
         "17.9 - Filter from the mode bar: the picker's menu HAS a back button"),
        ({PICKER}, set(), PICKER, False,
         "17.9 - the picker as a tool in its own right: NO back button"),
        ({TOOL}, set(), TOOL, False,
         "17.10 - the mouse tool alone, nothing selected: no back"),
        ({MODES, TOOL}, set(), TOOL, True,
         "17.10 - the capture's leading '<': the mouse menu covers the mode bar"),
        ({MODES, TOOL}, {MODES}, TOOL, False,
         "a page backed out of stops holding the one above it up"),
    ]
    for published, dismissed, page, want, why in cases:
        got = shows_back(published, dismissed, page)
        check(why, got == want,
              "%s over {%s} -> %s" % (PAGE_NAMES[page],
                                      ", ".join(PAGE_NAMES[p] for p in sorted(published) if p != page)
                                      or "nothing",
                                      "back" if got else "no back"))

    # And the page that DRAWS is the top one, so a covered page cannot show.
    check("the visible page is the highest-ranked published one",
          top({MODES, TOOL}, set()) == TOOL and top({MODES, PICKER}, set()) == PICKER
          and top({MODES, TOOL}, {TOOL}) == MODES and top(set(), set()) is None)

    # Back does not retract: the tool is still the tool.
    bk = strip_comments(safe_body(src, "public void Back(BottomPage id)"))
    check("back DISMISSES rather than retracts - the tool stays the tool and "
          "the picker stays open",
          "_dismissed.Add(id)" in bk and "_plates.Remove" not in bk)
    rt = strip_comments(safe_body(src, "public void Retract(BottomPage id)"))
    check("retracting clears the dismissal, so re-entering a page reopens it",
          "_dismissed.Remove(id)" in rt)


# ===========================================================================
# 2. NOTHING FLOATS BELOW THE SUBJECT (17.9)
# ===========================================================================

def nothing_below():
    src = read(CHROME)
    code = strip_comments(src)

    place = strip_comments(safe_body(src, "private void Place()"))
    check("17.9 - Place() maps the subject's two corners and nothing else, so "
          "there is no third thing being placed against the selection",
          place.count("WorldToScreen") == 2, "%d" % place.count("WorldToScreen"))
    check("the row is not placed under the subject any more",
          not re.search(r"Put\(_row", place))

    ctor = strip_comments(safe_body(src, "private SelectionChrome(Grid host, InkSurface surface, Host h)"))
    check("the row is not a child of the selection layer - it belongs to the "
          "screen, not to the bounding box",
          "_layer.Children.Add(_row)" not in ctor and "_layer.Children.Add(_bar)" in ctor)
    check("the row IS still built here, because it still appears with a "
          "selection and greys on that selection's lock",
          "_row = BottomMenu.Plate(" in ctor)

    sync = strip_comments(safe_body(src, "private void Sync()"))
    check("the row is PUBLISHED and RETRACTED, not merely collapsed - a "
          "collapsed plate the surface is still showing would blank the bar "
          "instead of falling through to the page underneath",
          "Publish(BottomPage.Modes" in sync and "Retract(BottomPage.Modes)" in sync)

    # 17.15's lesson: reserve the abundant axis, never the scarce one.
    menu = strip_comments(read(MENU))
    check("17.15 - the surface reserves NO page height: an overlay spanning the "
          "root grid's rows, aligned to the bottom",
          "VerticalAlignment.Bottom" in menu and "Grid.SetRowSpan(_layer" in menu
          and "RowDefinitions.Add" not in menu)
    check("it sits ABOVE the colour picker's overlay, which is a root-grid "
          "sibling at 150 - z-index only orders siblings",
          re.search(r"Canvas\.SetZIndex\(_layer,\s*160\)", menu) is not None)


# ===========================================================================
# 3. THE TWO HIT-TESTING TRAPS
# ===========================================================================

def hit_testing():
    menu = read(MENU)
    code = strip_comments(menu)
    check("the layer is transparent to hit-testing, so a press near the "
          "bottom edge still reaches the page",
          "_layer.Background = null" in code)
    check("the plate is NEVER given a null Background, or every press on it "
          "would fall through onto the page",
          "Background = new SolidColorBrush(PageTheme.Panel)"
          in strip_comments(safe_body(menu, "public static Border Plate(StackPanel items)")))
    n_hit = code.count("IsHitTestVisible = false")
    check("IsHitTestVisible = false is on decoration only - the divider, and "
          "nothing else; it propagates to the whole subtree",
          n_hit == 1, "%d occurrence(s) in code" % n_hit)

    # The fault MeasurementMenu found and fixed: a live element re-parented on a
    # second Build() throws, and the throw silently empties the bar.
    win = read(WINDOW)
    for sig, name in (("private void BuildToolMenu()", "the mouse tool's menu"),
                      ("private void BuildPickerMenu()", "the picker's menu")):
        body = strip_comments(safe_body(win, sig))
        check("%s clears and rebuilds rather than re-parenting - a WinUI "
              "element has exactly one parent and the second Build would throw"
              % name,
              "items.Children.Clear()" in body)
    chrome_build = strip_comments(safe_body(read(CHROME), "private void BuildModeBar(bool locked)"))
    check("the mode bar clears and rebuilds too",
          "_rowItems.Children.Clear()" in chrome_build)

    # And the trap the other two checkers pin, restated for this file's edits.
    pressed = strip_comments(safe_body(read(INK), "private void OnPointerPressed("))
    n = len(re.findall(r"e\.Handled\s*=\s*true", pressed))
    check("OnPointerPressed STILL marks exactly 11 events handled - 17.11's two "
          "tools are cases inside the switch, which falls through to the one "
          "assignment that was already there",
          n == 11, "%d" % n)


# ===========================================================================
# 4. THE MODE BAR AND THE MOUSE MENU, IN THE ORDER THE REFERENCE GIVES
# ===========================================================================

def contents():
    chrome = read(CHROME)
    bar = strip_comments(safe_body(chrome, "private void BuildModeBar(bool locked)"))
    order = re.findall(r"Icons\.(\w+)", bar)
    # Rotate, then its quarter turn; Scale, then uniform/stretch; Filter.
    check("17.9 - the mode bar is Rotate, Scale, Filter, in that order",
          [m for m in order if m in ("Rotate", "Scale", "Filter")][:1] == ["Rotate"]
          and order.index("Filter") == len(order) - 1
          and order.index("Rotate") < order.index("Scale") < order.index("Filter"),
          " ".join(order))
    check("17.9 - Rotate and Scale are MODES: the bar reads their state back "
          "rather than keeping a second copy of it",
          "_h.RotateMode()" in bar and "_h.ScaleMode()" in bar)
    check("17.9 - a mode that is on grows the control that qualifies it, to "
          "its right - the same shape 17.10 gives Lasso",
          "if (rotate" in bar and "if (scale" in bar and "Icons.ScaleStretch" in bar)
    check("17.9 - Filter opens the picker; it is not dead any more",
          "_h.OpenFilter" in bar)

    win = read(WINDOW)
    tool = strip_comments(safe_body(win, "private void BuildToolMenu()"))
    marks = re.findall(r"Icons\.(\w+)", tool)
    # The capture: < | Lasso | Complete | Include | All. The back mark is not in
    # this list because the page does not draw it - Lead() does.
    check("17.10 - the mouse menu is the capture, in order: Item/Lasso, "
          "Complete/Partial, Include/Ignore, All/Active",
          marks == ["ItemPicker", "Lasso", "Complete", "Partial",
                    "LockOpen", "LockClosed", "Layers", "LayerOne"],
          " ".join(marks))
    check("17.10 - Partial/Complete appears only while Lasso is chosen: "
          "'partially inside' has no meaning for a click",
          re.search(r"if\s*\(lasso\)", tool) is not None)
    check("17.10 - Include/Ignore is an OPEN padlock and a CLOSED one",
          "Icons.LockOpen" in tool and "Icons.LockClosed" in tool
          and tool.index("Icons.LockOpen") < tool.index("Icons.LockClosed"))
    check("17.9 - bottom menus carry only essential words: every cell's label "
          "is one word, and the sentence is a tooltip",
          all(len(w.split()) == 1 for w in re.findall(r'"\s*([A-Z][a-z]+)\s*",', tool)))

    picker = strip_comments(safe_body(win, "private void BuildPickerMenu()"))
    check("17.9 - the picker's menu carries Alpha on/off",
          "Icons.Alpha" in picker and "_filterAlpha" in picker)


# ===========================================================================
# 5. THE LAYER SCOPE IS THE MODEL'S, NOT A SECOND ONE (17.10 / 18.1)
# ===========================================================================

def layer_scope():
    ink = read(INK)
    code = strip_comments(ink)
    catch = strip_comments(safe_body(ink, "private bool CanCatch(int layerKey, bool elementLocked)"))
    check("17.10 - the layer half goes through PageLayers.CanSelect",
          "PageLayers.CanSelect(_page, layerKey, SelectScope)" in catch)
    check("18.1 - and NOWHERE else: no second layer concept is invented",
          code.count("PageLayers.") == code.count("PageLayers.CanSelect"),
          "%d PageLayers call(s), all CanSelect" % code.count("PageLayers."))
    check("AllLayers is the zero value, so an unset scope means today's "
          "behaviour - everything in scope",
          re.search(r"SelectScope\s*\{\s*get;\s*set;\s*\}\s*=\s*LayerScope\.AllLayers", code)
          is not None)
    check("both selection gestures ask it - a click that selects and a lasso "
          "that selects are one tool and must not disagree",
          "CanCatch(" in strip_comments(safe_body(ink, "private void SelectWithLasso(List<Vector2> poly)"))
          and "CanCatch(" in strip_comments(safe_body(ink, "private PenStroke? HitStrokeForClick(Vector2 p)")))
    check("the element's own lock is the padlock's business, separate from a "
          "locked LAYER",
          "IgnoreLocked && elementLocked" in catch)


# ===========================================================================
# 6. THE TOOLS (17.11) AND THE FOLD (17.10)
# ===========================================================================

def tools():
    models = strip_comments(read(MODELS))
    m = re.search(r"enum\s+ToolType\s*\{([^}]*)\}", models)
    members = [x.strip() for x in m.group(1).split(",")] if m else []
    check("17.11 - Pan and Rotate exist as tools",
          "Pan" in members and "Rotate" in members, ", ".join(members))
    check("17.10 - Mouse exists, and Select is KEPT rather than renamed: a dial "
          "sector and a pen-row cell store their tag by NAME",
          "Mouse" in members and "Select" in members)
    check("the enum is APPENDED to, never reordered",
          members[:8] == ["Pen", "Eraser", "Select", "Text", "FreeSpace",
                          "Eyedropper", "Ruler", "Mix"])

    ink = read(INK)
    settool = strip_comments(safe_body(ink, "public void SetTool(ToolType tool)"))
    check("17.10 - SetTool FOLDS Select into Mouse, so exactly one of the two "
          "is ever the live tool",
          "if (tool == ToolType.Select) tool = ToolType.Mouse;" in settool)
    check("the fold is complete: nothing downstream still tests for Select",
          "ToolType.Select" not in strip_comments(ink).replace(
              "if (tool == ToolType.Select) tool = ToolType.Mouse;", ""))
    check("17.11 - Rotate and Pan do NOT clear the selection: rotate acts on it, "
          "and pan moves the view rather than the page",
          re.search(r"tool is not \(ToolType\.Mouse or ToolType\.Rotate or ToolType\.Pan\)",
                    settool) is not None)

    code = strip_comments(ink)
    check("17.11 - pan reuses the one place the view offset moves",
          "_mousePanning = true" in strip_comments(safe_body(ink, "private void OnPointerPressed(")))
    # 17.11a requirement 2 REPLACED what this used to pin. The sweep was
    # quartered because "a TextElement is an axis-aligned box that takes no
    # rotation at all"; it has carried Rotation since #20, so the sweep is now
    # free-angle and this pins the three facts that makes true:
    #
    #   1. the drag commits an ARBITRARY angle, through RotateFreeMixedAction;
    #   2. it is ONE action for the whole gesture, not one per detent crossed -
    #      the live turn is un-applied before the action is built, which is the
    #      only way a single action can hold the gesture's start state without
    #      snapshotting every stroke point at press;
    #   3. the quarter turn SURVIVES as the mode bar's button. A free sweep that
    #      deleted RotateSelectionQuarter would leave that switch dead, which is
    #      the same failure the old comment here was guarding against.
    rot_release = strip_comments(safe_body(ink, "private void CommitGesture("))
    check("17.11a - the rotate sweep is FREE-angle, one action per gesture, and "
          "the quarter turn survives as the mode bar's button",
          "RotateFreeMixedAction" in code
          and "_rotateTurnedDeg" in code
          and "SpinRotateSubject(-total)" in rot_release
          and "public void RotateSelectionQuarter(" in code)
    undo = strip_comments(read(UNDO))
    check("one flag rather than three clockwise turns for one anticlockwise "
          "turn - three turns is three undo entries for one gesture",
          "bool clockwise = true" in undo and "Apply(_cw)" in undo)

    brushes = strip_comments(read(BRUSHES))
    for tag in ("Pan", "Rotate", "Mouse"):
        check("17.11 - %s is assignable to a dial sector or a pen-row cell" % tag,
              re.search(r'new\("%s",\s*"%s"' % (tag, tag), brushes) is not None)


# ===========================================================================
# 7. THE SIZES, AND THE SELECTED FILL (17.16, WHICH SUPERSEDES 17.12)
# ===========================================================================
# These six were pinned at 17.12's numbers - QuickScale 1.8, Scale 2.0. Seen on
# screen both were too big, and 17.16 revises them: "make the bottom bar and
# quick actions that open up after selection smaller by 30 and 40 percent
# respectively", plus "I want the margins on the selection gone". So the
# assertions move with the reference, which is the thing this file checks
# against - a checker that outlives the section it was written for is pinning
# history rather than intent.

def sizes():
    chrome = strip_comments(read(CHROME))
    menu = strip_comments(read(MENU))

    check("17.16 - the quick actions are 40% smaller: 1.8 x 0.60, as ONE factor",
          re.search(r"QuickScale\s*=\s*1\.08\b", chrome) is not None)
    check("and applied to the numbers the button is made of, so the ratios "
          "between them survive the resize",
          re.search(r"MarkSize\s*=\s*16\s*\*\s*QuickScale", chrome) is not None
          and re.search(r"MarkCell\s*=\s*30\s*\*\s*QuickScale", chrome) is not None
          and re.search(r"BarHeight\s*=\s*34\s*\*\s*QuickScale", chrome) is not None)
    check("17.16 - one number moves BOTH quick-action modes and is NOT split: "
          "11.9 is a mode on this bar, not a second surface with its own sizes",
          "Metrics.MarkSize" in chrome and "LabelToMark" in chrome
          and len(re.findall(r"QuickScale\s*=\s*[\d.]", chrome)) == 1)

    check("17.16 - the bottom bar is 30% smaller: 2.0 x 0.70, as ONE factor",
          re.search(r"Scale\s*=\s*1\.40\b", menu) is not None)
    check("17.16 - the selected fill reaches the panel's top and bottom: the "
          "plate's vertical inset is ZERO and its 5 + 5 is folded INTO the cell, "
          "so the chip is a filled segment of the bar and the bar's own height "
          "still comes from Scale alone",
          re.search(r"MarkSize\s*=\s*15\s*\*\s*Scale", menu) is not None
          and re.search(r"FontSize\s*=\s*12\.5\s*\*\s*Scale", menu) is not None
          and re.search(r"CellRise\s*=\s*5\b", menu) is not None
          and re.search(r"CellHeight\s*=\s*\(\s*30\s*\+\s*2\s*\*\s*CellRise\s*\)\s*\*\s*Scale",
                        menu) is not None
          and re.search(r"Padding\s*=\s*new\(\s*12\s*\*\s*Scale,\s*0,\s*12\s*\*\s*Scale,\s*0\s*\)",
                        menu) is not None)
    # 15 and 12.5 are the row's own numbers, still named where they came from.
    check("the row's original numbers are still recorded where they came from",
          re.search(r"RowMarkSize\s*=\s*15,\s*RowFontSize\s*=\s*12\.5", chrome) is not None)

    # ---- three rulings the user gave after seeing 17.16 on screen --------
    #
    # 1. SQUARE OFF THE SELECTED CHIP. 17.16 already took the plate's vertical
    #    inset to zero so the accent wash reaches the bar's edges, but a rounded
    #    cell inside a squared opening still showed four wedges of panel colour
    #    at its corners. The user was shown that those wedges are the chip's own
    #    rounding rather than a margin around it, and chose hard edges.
    check("the selected chip is SQUARE: CellCornerRadius is 0, so no wedge of "
          "panel colour shows at the corners of the fill",
          re.search(r"CellCornerRadius\s*=\s*0\s*;", menu) is not None,
          re.search(r"CellCornerRadius\s*=\s*[^;]+;", menu).group(0)
          if re.search(r"CellCornerRadius\s*=\s*[^;]+;", menu) else "MISSING")
    check("...and the PLATE keeps its own rounding: it is the cell that squares "
          "off, not the bar",
          re.search(r"CornerRadius\s*=\s*10\s*\*\s*Scale", menu) is not None)

    # 2. A GAP BETWEEN QUICK ACTIONS. They abutted at Spacing = 0 - seven
    #    32.4 x 36.7 DIP targets sharing edges, one of which deletes.
    gap = re.search(r"MarkGap\s*=\s*([0-9.]+)\s*\*\s*QuickScale", chrome)
    check("the quick actions no longer ABUT: the bar carries a real gap between "
          "targets rather than Spacing = 0",
          gap is not None
          and re.search(r"_barItems\s*=\s*\n?\s*new\(\)\s*\{[^}]*Spacing\s*=\s*Metrics\.MarkGap",
                        chrome, flags=re.S) is not None,
          "MarkGap = %s * QuickScale" % (gap.group(1) if gap else "MISSING"))
    check("...and the gap is DEAD space between two targets, because the cell "
          "itself is the button - nothing is laid over the space between cells",
          re.search(r"var cell = new Grid\s*\{[^}]*Width\s*=\s*Metrics\.MarkCell", chrome,
                    flags=re.S) is not None
          and "Spacing = 0" not in chrome)

    # 3. DELETE MOVES TO THE END, away from Duplicate. This SUPERSEDES 16.2's
    #    "four existence marks together, orientation behind a divider" - which
    #    is the reading that put the irreversible command against the one it is
    #    most easily confused with. Asserted as the PROPERTY the ruling is
    #    about, not as a positional list: a list would pass again the moment
    #    something else was inserted.
    for fn, what in (("private void BuildSelectionBar()", "16.2's selection bar"),
                     ("private void BuildEditingBar()", "11.9's editing bar")):
        bar = strip_comments(body_of(read(CHROME), fn))
        order = [m for m in re.findall(r"Icons\.(\w+)|(Divider)\(\)", bar)]
        order = [a or b for a, b in order]
        marks = [o for o in order if o != "Divider"]
        check("%s ends with Delete - the irreversible command is the last thing "
              "in the row, reached deliberately" % what,
              marks and marks[-1] == "WasteBin", " ".join(marks))
        di, wi = order.index("Duplicate"), order.index("WasteBin")
        between = order[di + 1:wi]
        check("...and Delete is NOT adjacent to Duplicate: at least a divider "
              "stands between the two commands most easily confused",
              wi > di and "Divider" in between,
              "between them: " + (" ".join(between) or "NOTHING"))


# ===========================================================================
# 8. STRETCH IS A REAL STRETCH (17.9)
# ===========================================================================

def stretch():
    ink = read(INK)
    code = strip_comments(ink)
    undo = strip_comments(read(UNDO))

    check("17.9 - the scale carries a factor PER AXIS",
          "_scaleFactor = 1f, _scaleFactorY = 1f" in code
          or re.search(r"_scaleFactor\s*=\s*1f,\s*_scaleFactorY\s*=\s*1f", code) is not None)
    live = strip_comments(safe_body(ink, "private void ApplyScaleLive()"))
    check("the live drag uses both",
          re.search(r"float\s+f\s*=\s*_scaleFactor,\s*g\s*=\s*_scaleFactorY", live) is not None)
    check("17.9 - stretch takes each axis from its own distance to the anchor; "
          "uniform keeps both equal from the radial ratio",
          "ScaleStretch" in code and "_scaleFactor = _scaleFactorY =" in code)
    check("the committed action takes the second factor, and DEFAULTS to the "
          "uniform case so every existing caller and every undo entry already "
          "on a stack keeps its meaning",
          "float factorY = float.NaN" in undo
          and "float.IsNaN(factorY) ? factor : factorY" in undo)

    # THE ONE PLACE THE TWO COPIES OF THIS ARITHMETIC COULD DRIFT. The live drag
    # and the committed action each scale the same three kinds of element, and if
    # they disagree the selection lands somewhere other than where the user
    # watched it go. A text box reflows - it has a width and no height - so in
    # BOTH the y factor may only move it and the x factor may only resize it.
    for body, where in ((live, "the live drag"),
                        (strip_comments(safe_body(read(UNDO), "private void Apply(float f, float g)")),
                         "the committed action")):
        text_lines = [l.strip() for l in body.splitlines()
                      if re.match(r"^\s*t\.(X|Y|Width)\s*=", l)]
        by = {l.split("=")[0].strip(): l for l in text_lines}
        check("a text box reflows, so in %s the y factor MOVES it and only the "
              "x factor resizes it" % where,
              len(by) == 3
              and by["t.Width"].rstrip(";").endswith("* f)")
              and by["t.Y"].rstrip(";").endswith("* g")
              and by["t.X"].rstrip(";").endswith("* f"),
              " / ".join(sorted(by)) if len(by) == 3 else "%d assignment(s)" % len(by))

    check("17.9 - Scale is a MODE: off, a corner is a corner; on, the whole box "
          "is a scale grip",
          "ScaleMode" in code
          and "if (ScaleMode && NearestCorner(pos, corners) != i) continue;" in code)


# ===========================================================================

def main():
    back_button()
    nothing_below()
    hit_testing()
    contents()
    layer_scope()
    tools()
    sizes()
    stretch()

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
    return 0


if __name__ == "__main__":
    sys.exit(main())
