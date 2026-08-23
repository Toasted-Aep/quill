#!/usr/bin/env python3
"""CONCEPTS-REF 11.9 acceptance check: TEXT-MODE QUICK ACTIONS.

A SEPARATE FILE FROM tools/selection_present_check.py ON PURPOSE. That script's
count (66) is quoted in commit messages and in the orchestration notes, and
several agents are working this tree at once; folding four more assertions into
it would change a number other people are checking against. The two scripts read
the same files and share no state.

WHAT THIS PINS, and why each one is invisible on screen until after it is wrong:

  1. THE STATE 11.9 BELONGS TO. "Quick actions above the text bubble" is easy to
     read as "the selection bar, for text", and that reading is wrong in a way a
     screenshot cannot show: a text box tapped into for editing publishes NO
     selection, so the selection bar never appears for it at all. The tell is in
     11.9's own words - "Cancel Editing" - and the proof is in the code: nothing
     writes _selTexts on focus. Asserted below by walking every writer of
     _selTexts in InkSurface.cs.

  2. ONE BAR, NOT TWO. The whole point of folding 11.9 into SelectionChrome is
     that one object draws one bar, so the two states cannot both be on screen.
     That property is a single field (_mode) and a single Wanted(); a later
     change that gave the editing state its own Border would break it silently.

  3. THE FOCUS TRAP. The subject of the editing bar IS the focused RichEditBox.
     A Button takes focus when clicked, so without AllowFocusOnInteraction the
     bar would blur its own subject and vanish from under the pointer before its
     Click ever ran. This one WOULD show on screen - as a bar that flickers and
     does nothing - but only for whoever happened to click Duplicate.

  4. THE EDITING COMMANDS ARE SEPARATE FROM THE SELECTION COMMANDS. Delete and
     Ctrl+D reach DeleteSelection and DuplicateSelection from the keyboard. If
     those had been given a "fall back to the focused text box" branch instead,
     pressing Delete mid-sentence would throw the text box away.

Run:  python tools/text_quick_actions_check.py
Exit: 0 all assertions held, 1 otherwise.
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Quill")
INK = os.path.join(SRC, "Controls", "InkSurface.cs")
CHROME = os.path.join(SRC, "Controls", "SelectionChrome.cs")
WINDOW = os.path.join(SRC, "MainWindow.xaml.cs")
ICONS = os.path.join(SRC, "Helpers", "Icons.cs")
BARS = os.path.join(SRC, "Controls", "ChromeBars.cs")

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


def strip_comments(src):
    """Drops // and /* */ so an assertion counts CODE, not the prose around it.
    These files explain their own traps at length and name them while doing so."""
    src = re.sub(r"/\*.*?\*/", "", src, flags=re.S)
    return re.sub(r"//[^\n]*", "", src)


def body_of(src, signature):
    """The braced body following `signature`, brace-matched."""
    i = src.index(signature)
    j = src.index("{", i)
    depth, k = 0, j
    while k < len(src):
        if src[k] == "{":
            depth += 1
        elif src[k] == "}":
            depth -= 1
            if depth == 0:
                return src[j:k + 1]
        k += 1
    raise ValueError(signature)


# ===========================================================================
# 1. 11.9 IS THE EDITING STATE, NOT THE SELECTED ONE
# ===========================================================================

def which_state():
    ink = strip_comments(read(INK))

    # Every line that adds to _selTexts. If focusing a box put one here, the
    # selection bar would already cover 11.9 and this whole feature would be a
    # duplicate.
    writers = [l.strip() for l in ink.splitlines() if "_selTexts.Add(" in l]
    check("_selTexts is written by the lasso, paste and the table selectors - "
          "and by nothing that reacts to FOCUS, so a bubble being typed in "
          "publishes no selection",
          len(writers) > 0 and all("Focus" not in w and "GotFocus" not in w for w in writers),
          "%d writer(s)" % len(writers))

    # BuildTextUi attaches several GotFocus handlers - the table cell's row
    # snapshot, the scroll-mode toggle, the grip reveal. The one that matters is
    # whichever assigns ActiveTextBox, so it is found by that rather than by
    # position, which would silently start testing a different handler the next
    # time one is added above it.
    raw_ink = read(INK)
    anchor = raw_ink.index("ActiveTextBox = box;")
    got = raw_ink[raw_ink.rindex("box.GotFocus", 0, anchor):
                  raw_ink.index("};", anchor)]
    check("...confirmed at the other end: the handler that sets ActiveTextBox "
          "raises ActiveTextChanged and touches no selection",
          "ActiveTextChanged?.Invoke(box)" in got and "_selTexts" not in got
          and "Select" not in got)

    # The editing subject exists, is asked per-property, and excludes cells.
    pair = body_of(read(INK), "private (TextElement Text, Grid Container)? EditingPair()")
    check("a TABLE CELL is never 11.9's subject - its TextElement IS the cell, "
          "and removing one leaves the cell untypeable (#cellfix)",
          "TableId: null" in pair)

    for name in ("CancelTextEditing", "DuplicateEditingText",
                 "ToggleEditingTextLock", "DeleteEditingText"):
        check("InkSurface.%s exists" % name,
              re.search(r"public void %s\(\)" % name, ink) is not None)

    # 4: the keyboard must not have been rerouted.
    dele = strip_comments(body_of(read(INK), "public void DeleteSelection()"))
    dupe = strip_comments(body_of(read(INK), "public void DuplicateSelection()"))
    check("DeleteSelection has NO fall-back to the focused text box - the Delete "
          "key pressed mid-sentence must not throw the box away",
          "EditingText" not in dele and "ActiveTextBox" not in dele)
    check("...and neither does DuplicateSelection", "EditingText" not in dupe)

    # The waste bin refuses while locked, which is 16.2's rule reapplied.
    dele_edit = strip_comments(body_of(read(INK), "public void DeleteEditingText()"))
    check("16.2's padlock holds for the editing bar too: a locked bubble is not "
          "deleted even if the mark is somehow pressed",
          "t.Locked" in dele_edit)

    # Cancel keeps the text.
    cancel = strip_comments(body_of(read(INK), "public void CancelTextEditing()"))
    check("Cancel Editing CANCELS EDITING, NOT THE TEXT - it flushes and blurs, "
          "and pushes no action that could remove anything",
          "FlushTexts()" in cancel and "PushAction" not in cancel
          and "Remove" not in cancel)


# ===========================================================================
# 2. ONE BAR, TWO TRIGGERS
# ===========================================================================

def one_bar():
    raw = read(CHROME)
    code = strip_comments(raw)

    check("11.9 is a MODE of the selection presentation, not a second surface",
          "private enum Mode { None, Selection, Editing }" in raw)
    # One plate for the bar. Two Borders exist - the bar and the bottom row -
    # and no more; a third would be the fourth floating strip this avoided.
    n_plate = len(re.findall(r"Plate\(", code))
    check("there is exactly ONE bar plate and one row plate, built by the one "
          "Plate() factory - no separate editing plate",
          n_plate == 3, "%d Plate( occurrence(s) incl. the factory" % n_plate)

    wanted = strip_comments(body_of(raw, "private Mode Wanted()"))
    check("blocked beats both states (the COPIC wheel and an export)",
          "IsBlocked()" in wanted)
    check("SELECTION WINS when a box is both lassoed and tapped into - the "
          "selected reading is the one with handles, guides and flips",
          wanted.index("SelectionState.Current.Any") < wanted.index("EditingText"))
    check("the editing state is refused until the bubble has real bounds, so no "
          "bar is placed off an empty rect for a frame",
          "EditingTextBoundsWorld.IsEmpty" in wanted)

    sync = strip_comments(body_of(raw, "private void Sync()"))
    check("guides, corner circles and the bottom row stand down while editing - "
          "11.9 asks for quick actions above the bubble and nothing else",
          "_guides" in sync and "_handles" in sync and "_row.Visibility" in sync)

    place = strip_comments(body_of(raw, "private void Place()"))
    check("the bar is placed off the BUBBLE while editing and off the SUBJECT "
          "while selected, through the one WorldToScreen either way",
          "EditingTextBoundsWorld" in place and "SubjectBoundsWorld" in place
          and place.count("WorldToScreen") == 2)

    # 3: the focus trap.
    press = strip_comments(body_of(raw, "private Button Press("))
    check("THE FOCUS TRAP: every bar button refuses focus on interaction, or "
          "pressing one would blur the RichEditBox that IS the subject and the "
          "bar would vanish before its Click ran",
          "AllowFocusOnInteraction = false" in press)

    # The trap the existing checker pins, restated for this file's own edits.
    n_hit = code.count("IsHitTestVisible = false")
    check("IsHitTestVisible = false is still on decoration only (guides, corner "
          "circles, divider) - it propagates to the whole subtree",
          n_hit == 3, "%d occurrence(s) in code" % n_hit)

    # 11.9's contents, in 16.2's order, with the red X in front.
    bar = strip_comments(body_of(raw, "private void BuildEditingBar()"))
    order = [m for m in re.findall(r"Icons\.(\w+)", bar)]
    check("11.9's bar is: red X, divider, then 16.2's four in 16.2's order",
          order[:1] == ["Close"]
          and [o for o in order if o in ("Paperclip", "LockClosed", "Duplicate", "WasteBin")]
              == ["Paperclip", "LockClosed", "Duplicate", "WasteBin"],
          " ".join(order))
    check("the paperclip is present and DEAD - only an attachment has a file to "
          "replace, and a text box is not one",
          re.search(r"Icons\.Paperclip[^;]*?,\s*false\s*,", bar, re.S) is not None)
    check("the flips are NOT on it - 11.9 does not list them, and a mirrored "
          "text box is unreadable",
          "FlipHorizontal" not in bar and "FlipVertical" not in bar)
    check("the red is a measured PAIR, not one constant that fails at one end "
          "of the panel ramp",
          "CancelRedLight" in raw and "CancelRedDark" in raw
          and "PageTheme.IsDark ?" in raw)

    win = read(WINDOW)
    for d in ("CancelEditing", "DuplicateEditingText",
              "ToggleEditingTextLock", "DeleteEditingText"):
        check("the editing bar's %s is wired to something real" % d,
              re.search(r"\b%s\s*=\s*\S" % d, win) is not None)


# ===========================================================================
# 3. THE GRIP'S DELETE ✕ IS GONE
# ===========================================================================

def no_second_delete():
    build = strip_comments(body_of(read(INK), "private void BuildTextUi(TextElement t)"))
    check("the grip's close X is gone: it deleted the box under the SAME visibility "
          "condition the new bar's waste bin runs under, and a second X beside "
          "one that means 'cancel' is a trap this change would have built",
          "close.Click" not in build and 'Content = "✕"' not in build)
    check("...and the bar's waste bin is what replaced it",
          "DeleteEditingText" in read(CHROME))
    check("the bubble tells the chrome when it moves or grows - neither "
          "ViewChanged nor ActiveTextChanged fires for either",
          "container.SizeChanged += (_, _) => RaiseEditingGeometry(box)" in build
          and "RaiseEditingGeometry(box)" in body_of(read(INK), "grip.ManipulationDelta += (_, e) =>"))


# ===========================================================================
# 4. SECTION 5'S HELP MARK
# ===========================================================================

def help_mark():
    icons = read(ICONS)
    check("section 5's `?` exists as authored geometry on the 24 grid",
          "public const string Help =" in icons)
    m = re.search(r'public const string Help =\s*\n\s*"([^"]*)";', icons)
    check("...on ONE line, so no break can fuse a coordinate's x into its y",
          m is not None)
    if m:
        check("...and it is geometry, never an emoji or a font glyph",
              all(ord(c) < 128 for c in m.group(1)))
    bars = read(BARS)
    check("the top bar's right cluster carries it, after the gear (section 5, "
          "and 15.3's fullscreen transcription)",
          bars.index("Icons.Settings") < bars.index("Icons.Help"))
    check("...and it opens the sheet F1 already opens, so Help cannot claim a "
          "key that no longer works",
          "ToggleHelp = ToggleShortcutsPanel" in read(WINDOW))


# ===========================================================================

def main():
    which_state()
    one_bar()
    no_second_delete()
    help_mark()

    width = max(len(n[1]) for n in NOTES)
    for state, label, detail in NOTES:
        line = "%s %s" % (state, label.ljust(width))
        if detail:
            line += "   -- " + detail
        print(line)
    print()
    if FAILURES:
        print("FAILED %d of %d" % (len(FAILURES), len(NOTES)))
        for f in FAILURES:
            print("   " + f)
        return 1
    print("OK - %d checks held." % len(NOTES))
    return 0


if __name__ == "__main__":
    sys.exit(main())
