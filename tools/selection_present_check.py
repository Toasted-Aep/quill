#!/usr/bin/env python3
"""CONCEPTS-REF 16.2 / 16.3 / 16.7 / 16.9 acceptance check: THE SELECTION
PRESENTATION.

Written the way tools/click_select_check.py is written - the code is read and
the rules are evaluated, rather than the app being driven - because the two
things section 16 is easiest to get wrong are both invisible on screen until
after they have gone wrong:

  1. THE CAPABILITY RULE (16.3 corrected by 16.9). "Something is selected, so
     grey the dial" and "a subject that LACKS a property greys that property's
     control" behave IDENTICALLY on an attachment. They diverge only when a
     stroke is selected, where the dial must stay live and read that stroke's
     own size, stability, opacity and colour. A screenshot of an attachment
     cannot tell the two rules apart; this can, because it takes the flag
     expressions out of InkSurface.PublishSelection and EVALUATES them for each
     kind of subject rather than trusting a reading of them.

  2. THE FADE NEVER TOUCHING STORED COLOUR (16.7 item 1). The failure mode is
     data loss discovered on the next reload, which is exactly the kind of thing
     that looks perfect at the moment it is done. Proved here by dataflow -
     every Veil result is a local or a draw argument, and no path leads from one
     to a stored field - and then again, for real, by tools/VeilRoundTrip, whose
     Veil.g.cs this script EXTRACTS VERBATIM from InkSurface.cs so the harness
     cannot drift into testing a copy.

Run:  python tools/selection_present_check.py
      dotnet run --project tools/VeilRoundTrip -c Debug     (the round trip)
Exit: 0 all assertions held, 1 otherwise.
"""

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "src", "Quill")
INK = os.path.join(SRC, "Controls", "InkSurface.cs")
WHEEL = os.path.join(SRC, "Controls", "ToolWheel.cs")
PENBAR = os.path.join(SRC, "Controls", "PenBar.cs")
CHROME = os.path.join(SRC, "Controls", "SelectionChrome.cs")
SUBJECT = os.path.join(SRC, "Services", "SelectionSubject.cs")
WINDOW = os.path.join(SRC, "MainWindow.xaml.cs")
STORE = os.path.join(SRC, "Services", "LibraryStore.cs")
GEN = os.path.join(ROOT, "tools", "VeilRoundTrip", "Veil.g.cs")

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


def body_of(src, signature):
    """Text of a method or property body, brace-matched from its signature."""
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
    except ValueError:
        return ""


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return "\n".join(l for l in text.splitlines() if not l.strip().startswith("//"))


# ===========================================================================
# 1. THE CAPABILITY RULE, EVALUATED (16.3 / 16.9)
# ===========================================================================
# The flags are not re-stated here. They are lifted out of PublishSelection as
# C# expression TEXT, translated token-for-token into Python, and evaluated
# against each kind of subject. If somebody later changes `HasPenSize = pureInk`
# to `HasPenSize = false`, this section fails on the stroke case rather than
# quietly agreeing with whatever the file now says.

SUBJECT_FLAGS = ["HasPenSize", "HasStability", "HasOpacity", "CanRecolour", "FadesPage"]


def cs_expr_to_py(e):
    e = e.strip()
    e = re.sub(r"\|\|", " or ", e)
    e = re.sub(r"&&", " and ", e)
    e = re.sub(r"!(?!=)", " not ", e)
    e = e.replace("true", "True").replace("false", "False")
    return e


def capability_rule():
    src = read(INK)
    pub = strip_comments(safe_body(src, "private void PublishSelection()"))
    check("InkSurface publishes a selection subject at all",
          "SelectionState.Set(subject)" in pub)

    # The three intermediate booleans the flags are written in terms of.
    inter = {}
    for name in ("pureInk", "anyShapeOrText"):
        m = re.search(r"bool\s+%s\s*=\s*([^;]+);" % name, pub)
        if not m:
            check("the '%s' predicate exists" % name, False)
            return
        inter[name] = cs_expr_to_py(m.group(1))
    check("the flags are written over pureInk / anyShapeOrText", True,
          "; ".join("%s = %s" % kv for kv in inter.items()))

    flags = {}
    for f in SUBJECT_FLAGS:
        m = re.search(r"\b%s\s*=\s*([^,\n]+),\s*$" % f, pub, flags=re.M)
        if not m:
            check("the subject reports %s" % f, False)
            return
        flags[f] = cs_expr_to_py(m.group(1))

    # The four writers, which 16.9 needs for "editing them edits the selection".
    setters = {}
    for s in ("SetSize", "SetStability", "SetOpacity", "SetInk"):
        m = re.search(r"\b%s\s*=\s*(.+?)\s*\?" % s, pub, flags=re.S)
        setters[s] = cs_expr_to_py(m.group(1)) if m else None

    # One binding per kind of subject, in the same variables PublishSelection
    # computes them from.
    worlds = {
        "a stroke": dict(ink=True, text=False, attach=False, otherShape=False),
        "an attachment": dict(ink=False, text=False, attach=True, otherShape=False),
        "a text box": dict(ink=False, text=True, attach=False, otherShape=False),
        "a stroke + an attachment": dict(ink=True, text=False, attach=True, otherShape=False),
    }

    def evaluate(world):
        env = dict(world)
        env["page"] = True      # a page is open; the setters' `page != null` guard
        env["null"] = None
        for k, v in inter.items():
            env[k] = eval(v, {"__builtins__": {}}, env)
        out = {f: bool(eval(x, {"__builtins__": {}}, env)) for f, x in flags.items()}
        for s, x in setters.items():
            out[s] = bool(eval(x, {"__builtins__": {}}, env)) if x else False
        return out

    got = {k: evaluate(v) for k, v in worlds.items()}

    # ---- 16.9: A STROKE GREYS NOTHING ------------------------------------
    st = got["a stroke"]
    check("16.9 - a selected STROKE greys nothing in the dial",
          st["HasPenSize"] and st["HasStability"] and st["HasOpacity"] and st["CanRecolour"],
          "size=%s stability=%s opacity=%s colour=%s"
          % (st["HasPenSize"], st["HasStability"], st["HasOpacity"], st["CanRecolour"]))
    check("16.9 - editing the dial edits the selected stroke",
          st["SetSize"] and st["SetStability"] and st["SetOpacity"] and st["SetInk"])
    check("16.9 - a selected stroke does NOT fade the page it is part of",
          not st["FadesPage"])

    # ---- 16.3: AN ATTACHMENT -------------------------------------------
    at = got["an attachment"]
    check("16.3 - an attachment greys pen size",       not at["HasPenSize"])
    check("16.3 - an attachment greys stability",      not at["HasStability"])
    check("16.3 - an attachment KEEPS opacity live",   at["HasOpacity"])
    check("16.3 - an attachment cannot be recoloured", not at["CanRecolour"])
    check("16.7 - an attachment fades the page",       at["FadesPage"])

    # ---- the divergence the whole design turns on ------------------------
    check("the rule is capability, not 'something is selected'",
          at["HasPenSize"] != st["HasPenSize"] and at["CanRecolour"] != st["CanRecolour"],
          "attachment and stroke disagree on size and colour, which 'is anything "
          "selected?' could never produce")

    # ---- mixed: supports only what everything in it supports -------------
    mx = got["a stroke + an attachment"]
    check("a mixed selection supports only what all of it supports",
          not mx["HasPenSize"] and not mx["CanRecolour"] and not mx["HasStability"])

    # ---- undo, redo and the per-pen arcs are ABSENT from the type --------
    sub = read(SUBJECT)
    check("16.3 - the subject type carries no undo/redo flag "
          "(they are page commands, live for every subject)",
          not re.search(r"\bbool\s+(Can|Has)?(Undo|Redo)\b", sub))
    check("16.3 - the subject type carries no per-pen-arc flag "
          "(those arcs REPORT, so they have nothing to ask)",
          "Arc" not in sub and "Rim" not in sub)
    check("the subject type has no flag called 'Selected' at all",
          not re.search(r"\bbool\s+Selected\b", sub))

    # ---- both surfaces ask the SUBJECT, and ask it the same question -----
    for path, name in ((WHEEL, "the dial"), (PENBAR, "the pen row")):
        s = read(path)
        en = strip_comments(safe_body(s, "private bool Enabled(Prop p)"))
        check("%s greys by capability" % name,
              "HasPenSize" in en and "HasOpacity" in en and "HasStability" in en,
              " ".join(en.split())[:110])
        check("%s never greys on 'something is selected'" % name,
              not re.search(r"return\s+(!\s*)?SelectionState\.Current\.Any\s*;", en) and
              not re.search(r"Any\s*\?\s*false", en))
        inert = re.search(r"ColourInert\s*=>\s*([^;]+);", s)
        check("%s sends the colour circle white only when the SUBJECT cannot "
              "be recoloured" % name,
              inert is not None and "CanRecolour: false" in inert.group(1),
              (inert.group(1).strip() if inert else "MISSING"))

    # ---- the per-pen colour arcs, which must NOT grey --------------------
    wheel = read(WHEEL)
    arc = None
    for sig in ("private void PaintRim(", "private void Rim(", "_rimArc"):
        if sig in wheel:
            arc = sig
            break
    check("the dial still paints per-pen colour arcs", arc is not None, str(arc))
    # What an arc is PAINTED FROM, rather than which method happens to contain it:
    # the whole dial is re-rendered by one Refresh, so "does Refresh mention the
    # selection?" would always be yes and would prove nothing. The two inputs are
    # SlotColour (which pen sits in this slot, and what colour it carries) and
    # Available (does the slot hold anything at all). Neither may be a function of
    # what is selected, and the assignment itself may not be conditioned on it.
    wsrc = strip_comments(wheel)
    arc_lines = [l.strip() for l in wsrc.splitlines() if "_rimArc[i]." in l]
    check("the arcs are painted from the PEN, not from the selection",
          bool(arc_lines) and not any(
              t in l for l in arc_lines
              for t in ("SelectionState", "ColourInert", "sel.", "subject")),
          "; ".join(arc_lines))
    for fn in ("private Color? SlotColour(string id)", "private bool Available(string id)"):
        b = strip_comments(safe_body(wheel, fn))
        check("16.3 - %s is not a function of the selection" % fn.split("(")[0].split()[-1],
              b != "" and "SelectionState" not in b)
    # And the pen row's half of the same sentence: each cell draws its pen's own
    # stroke silhouette in that pen's own colour, and that fact is as true under a
    # selected attachment as it is under nothing at all.
    art = strip_comments(safe_body(read(PENBAR), "private FrameworkElement? Art("))
    check("16.3 - the pen row's per-pen marks are painted from the PEN, not from "
          "the selection",
          art != "" and "SelectionState" not in art and "ColourInert" not in art,
          "ColorUtil.Parse(pen.Color)" in art and "pen's own colour" or "")

    # ---- undo and redo stay live under every subject ---------------------
    for path, name in ((WHEEL, "the dial"), (PENBAR, "the pen row")):
        s = strip_comments(read(path))
        near = [l.strip() for l in s.splitlines()
                if ("Undo" in l or "Redo" in l) and "SelectionState" in l]
        check("16.3 - %s never greys undo or redo on the selection" % name,
              not near, "; ".join(near) or "no undo/redo line mentions SelectionState")


# ===========================================================================
# 2. THE FADE CANNOT REACH STORED COLOUR (16.7 item 1) - BY DATAFLOW
# ===========================================================================
# The mechanism is one pure function Color -> Color. This proves the three
# things that makes true, in the order they could fail:
#
#   a. every Veil result is a LOCAL or a draw ARGUMENT - never the left of a
#      member assignment;
#   b. no local that came from Veil is ever handed to ColorUtil.ToHex, which is
#      the ONLY Color -> string bridge in the app and therefore the only door
#      from a draw colour to a stored one;
#   c. nothing on the save path can even see the fade.

STORED_COLOUR_MEMBERS = ["Color", "Colour", "Fill", "Stroke", "Background", "Rtf", "Ink"]
VEIL_TOKENS = ["Veil(", "VeilGrey", "_veil", "8E8E8E", "0x8E"]


def statement_around(src, idx):
    """The statement containing position idx: back to the previous ; { or }."""
    start = max(src.rfind(";", 0, idx), src.rfind("{", 0, idx), src.rfind("}", 0, idx))
    end = src.find(";", idx)
    return src[start + 1:end + 1].strip()


def veil_dataflow():
    src = read(INK)
    stripped = strip_comments(src)

    # (a) every call site
    sites = []
    for m in re.finditer(r"(?<![A-Za-z_])Veil\s*\(", stripped):
        # skip the declaration itself and the pump/tick/text helpers
        pre = stripped[max(0, m.start() - 40):m.start()]
        if re.search(r"(private|public)\s+Color\s*$", pre):
            continue
        sites.append(statement_around(stripped, m.start()))
    check("16.7 has exactly the intercepts its own commentary claims",
          len(sites) == 3, "%d call sites: %s" % (len(sites), " | ".join(
              " ".join(s.split())[:64] for s in sites)))

    locals_from_veil = set()
    bad = []
    for st in sites:
        one = " ".join(st.split())
        as_local = re.match(r"^(?:var|Color)\s+([A-Za-z_]\w*)\s*=\s*Veil\s*\(", one)
        as_arg = re.search(r"\bds\.\w+\(.*Veil\s*\(", one)
        if as_local:
            locals_from_veil.add(as_local.group(1))
        elif not as_arg:
            bad.append(one[:100])
        # and in NO case may the target be a member of anything
        if re.match(r"^[A-Za-z_]\w*(?:\.\w+)+\s*=\s*[^=]", one):
            bad.append("MEMBER TARGET: " + one[:100])
    check("16.7 item 1 - every Veil result is a local or a draw argument, "
          "never a member",
          not bad, ", ".join(bad) or
          "locals: %s" % (", ".join(sorted(locals_from_veil)) or "none"))

    # (b) the Color -> string door
    hexed = []
    for m in re.finditer(r"ToHex\s*\(\s*([A-Za-z_]\w*)", stripped):
        if m.group(1) in locals_from_veil:
            hexed.append(m.group(1))
    check("16.7 item 1 - no veiled colour ever reaches ColorUtil.ToHex, the only "
          "Color -> string bridge there is",
          not hexed, ", ".join(hexed) or
          "%d ToHex call(s) in InkSurface, none of them on a veiled local"
          % len(re.findall(r"ToHex\s*\(", stripped)))

    # and the same question asked of the whole tree, by assignment target
    offenders = []
    for dirpath, _dirs, files in os.walk(SRC):
        if os.sep + "obj" in dirpath or os.sep + "bin" in dirpath:
            continue
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            text = strip_comments(read(os.path.join(dirpath, fn)))
            for m in re.finditer(
                    r"\.(%s)\s*=\s*([^;]{0,200});" % "|".join(STORED_COLOUR_MEMBERS), text):
                rhs = m.group(2)
                if any(t in rhs for t in VEIL_TOKENS):
                    offenders.append("%s: .%s = %s" % (fn, m.group(1), rhs[:60]))
    check("16.7 item 1 - nothing anywhere assigns a stored colour member from "
          "anything the fade produces",
          not offenders, "; ".join(offenders) or
          "checked .%s across every .cs under src/Quill"
          % ", .".join(STORED_COLOUR_MEMBERS))

    # (c) the save path cannot see the fade
    for path, name in ((STORE, "LibraryStore"),):
        text = strip_comments(read(path))
        check("the save path (%s) contains no reference to the fade" % name,
              not any(t in text for t in VEIL_TOKENS))
    flush = strip_comments(safe_body(src, "public void FlushTexts()"))
    check("FlushTexts serialises the DOCUMENT, not the control's Foreground",
          "GetText" in flush and "Foreground" not in flush and
          not any(t in flush for t in VEIL_TOKENS),
          " ".join(flush.split())[:130])

    # the text half writes a XAML property and nothing else
    atv = strip_comments(safe_body(src, "private void ApplyTextVeil()"))
    writes = re.findall(r"([A-Za-z_][\w.]*)\s*=\s*[^=]", atv)
    stored_writes = [w for w in writes if w.startswith("model.") or w.startswith("_page.")
                     or w.endswith(".Rtf")]
    check("16.7 - the text half of the fade writes ui.Box.Foreground and nothing "
          "that is serialised",
          "ui.Box.Foreground" in atv and not stored_writes,
          ", ".join(stored_writes) or "no write to a model field")

    # an export is never faded, and a thumbnail cannot be
    veiling = re.search(r"private\s+bool\s+Veiling\s*=>\s*([^;]+);", stripped)
    check("16.7 - a capture taken while an attachment is selected comes out in "
          "the page's own colours",
          veiling is not None and "!ExportChromeless" in veiling.group(1),
          veiling.group(1).strip() if veiling else "MISSING")
    thumb = safe_body(src, "public static byte[]? RenderPageThumbnail(")
    check("a page thumbnail cannot be faded: its renderer is STATIC and has no "
          "instance fade to read",
          thumb != "" and "_veil" not in thumb and "Veil(" not in thumb)

    # the fade animates on Quill's own motion rather than a new duration
    setv = strip_comments(safe_body(src, "private void SetVeil(bool on)"))
    tick = strip_comments(safe_body(src, "private void VeilTick(object? sender, object e)"))
    check("16.7 item 2 - the fade uses Quill's own 190 / 130 and its own curve",
          "Motion.OpenMs" in tick and "Motion.CloseMs" in tick and "Motion.Step" in tick,
          " ".join(tick.split())[:120])
    tail = re.sub(r"//[^\n]*", "", tick).strip()
    check("16.7 item 2 - it fades BACK, and the last frame still invalidates "
          "(that repaint is what puts the page's colours back)",
          "_veilWant ? 1 : 0" in tick and "StopVeilTick();" in tick and
          tail.endswith("_canvas.Invalidate();"),
          " ".join(tail.split())[-90:])
    check("16.7 - the fade honours reduce-motion",
          "ReduceMotion" in setv)

    # 16.7 item 3 / the two-attachment question, as 17.13 leaves it.
    #
    # The exemption is now ONE read of a snapshot, and the snapshot is filled
    # from the SELECTION - so "both of two selected attachments hold contrast"
    # is still the property being pinned, and "is this an image?" is still the
    # answer being refused. What 17.13 adds is the clock: the snapshot is taken
    # where the veil LEVEL is set, held while the veil comes down, and released
    # only at _veil 0, so the subject cannot be un-exempted out from under a
    # veil that is still being applied. All three are asserted together, because
    # any one of them alone lets the transient fade back in: a live read races,
    # a snapshot never refreshed strands the exemption on the wrong element, and
    # a snapshot cleared on the deselect is just the live read again.
    subj = re.search(r"private bool IsSubject\(ShapeElement s\)\s*=>([^;]+);", stripped)
    subj = subj.group(1).strip() if subj else ""
    cap = strip_comments(safe_body(src, "private void CaptureVeilSubject()"))
    setv_all = strip_comments(safe_body(src, "private void SetVeil(bool on)"))
    tick_all = strip_comments(safe_body(src, "private void VeilTick("))
    check("16.7 item 3 / 17.13 - the exemption is one snapshot READ, filled from "
          "the SELECTION and not from 'is this an image?', refreshed where the "
          "veil level is set and released only once the veil has fully lifted - "
          "so the subject never fades, not even transiently",
          "_veilSubject.Contains(s)" in subj and "ShapeKind.Image" not in subj and
          "_selShapeSet" in cap and "_activeShapeBack" in cap and
          "ShapeKind.Image" not in cap and
          "CaptureVeilSubject();" in setv_all and
          "_veilSubject.Clear();" in tick_all,
          " ".join(subj.split()) or "MISSING")

    return locals_from_veil


# ===========================================================================
# 3. THE PRESENTATION IS ACTUALLY REACHABLE (16.2 / 16.9)
# ===========================================================================

def wiring():
    win = strip_comments(read(WINDOW))
    check("16.2 - MainWindow attaches the selection presentation",
          "SelectionChrome.Attach(" in win)
    m = re.search(r"SelectionChrome\.Attach\(\s*(\w+)\s*,\s*(\w+)", win)
    check("it is hosted on the CANVAS AREA, whose origin is the one "
          "InkSurface.WorldToScreen maps into",
          m is not None and m.group(1) == "CanvasArea" and m.group(2) == "Surface",
          "%s, %s" % (m.group(1), m.group(2)) if m else "MISSING")
    for d in ("Duplicate", "Delete", "ToggleLock", "Flip", "Rotate",
              "ReplaceAttachment", "IsBlocked"):
        check("the bar's %s is wired to something real" % d,
              re.search(r"\b%s\s*=\s*\S" % d, win) is not None)
    check("16.7 - the fade is given the window's reduce-motion setting",
          "Surface.ReduceMotion" in win)
    check("blocked-ness is re-asked when the window's answer changes",
          "_selChrome?.Refresh()" in win)

    chrome = read(CHROME)
    check("16.2 - the presentation draws all four marks",
          all(k in chrome for k in ("_guides", "_handles", "_bar", "_row")))
    check("16.2 - the guides are FULL-CANVAS, not a box on the bounds",
          re.search(r"Line\(_guides\[0\], x0, 0, [^,]+, vh\)", chrome) is not None and
          re.search(r"Line\(_guides\[2\], 0, y0, vw,", chrome) is not None)
    check("the layer is transparent to hit-testing and its CONTROLS are not",
          "_layer.Background = null" in chrome and
          "IsHitTestVisible = false" not in body_of(chrome, "private static Border Plate("))
    # Counted over CODE, not commentary: this file explains the trap at length and
    # the phrase appears in the prose too.
    code = strip_comments(chrome)
    n_hit = code.count("IsHitTestVisible = false")
    check("IsHitTestVisible = false is set on decoration only - the guides, the "
          "corner circles and the divider, and nowhere that a pointer must land",
          n_hit == 3, "%d occurrence(s) in code" % n_hit)
    check("the plates are never given a null Background",
          "Background = new SolidColorBrush(Colors.Transparent)"
          in body_of(chrome, "private static Border Plate("))

    # the trap that has silently killed input in this file before
    ink = read(INK)
    pressed = strip_comments(body_of(ink, "private void OnPointerPressed("))
    n = len(re.findall(r"e\.Handled\s*=\s*true", pressed))
    check("OnPointerPressed still marks exactly 11 events handled "
          "(pinned by tools/click_select_check.py on main)",
          n == 11, "%d" % n)


# ===========================================================================
# 4. THE TWO INTERACTIONS 16.7 ASKS TO BE DECIDED RATHER THAN DISCOVERED
# ===========================================================================
# "Interaction worth deciding rather than assuming: what happens with TWO
#  attachments, or an attachment selected WHILE A STROKE IS MID-FLIGHT. Report
#  what the implementation does rather than leaving it to be discovered."
#
# Both answers are pinned here so they stay answers.

def interactions():
    src = read(INK)
    stripped = strip_comments(src)

    # ---- TWO ATTACHMENTS -------------------------------------------------
    # Falls out of the exemption question. IsSubject asks "is this element part
    # of the selection?", so two selected attachments are both subjects and both
    # hold contrast, and a third, unselected one fades with the page. Asserted in
    # section 2 and measured for real by tools/VeilRoundTrip.
    pub = strip_comments(safe_body(src, "private void PublishSelection()"))
    check("two attachments still read as ONE attachment subject, so the dial "
          "greys exactly as it does for one",
          "_selShapes.Any(s => s.Kind == ShapeKind.Image)" in pub)
    paperclip = strip_comments(safe_body(src, "public ShapeElement? SelectedAttachment"))
    check("...but the PAPERCLIP greys with two, because two attachments are not "
          "one file to replace",
          "_selShapes.Count == 1" in paperclip,
          " ".join(paperclip.split())[:120])

    # ---- A STROKE MID-FLIGHT ---------------------------------------------
    # Two rules, at two different places, and they have to agree.
    wet = [l.strip() for l in stripped.splitlines() if "DrawStroke(" in l and "veil:" in l]
    check("16.7 - the WET stroke is drawn with the fade off: ink under the nib is "
          "not yet part of the page and never greys mid-gesture",
          any("veil: false" in l for l in wet),
          "; ".join(l[:80] for l in wet))
    check("...and a committed stroke fades unless it IS the subject",
          any("veil: !IsSubject(s)" in l for l in wet))

    # In practice the question rarely arises, and that is worth pinning too: the
    # Pen tool's own press handler drops the selection before a stroke can start.
    pressed = strip_comments(body_of(src, "private void OnPointerPressed("))
    pen_case = pressed[pressed.index("case ToolType.Pen:"):]
    nxt = pen_case.find("case ToolType.", 20)
    if nxt > 0:
        pen_case = pen_case[:nxt]
    check("a pen-down with the Pen tool DROPS an attachment selection before the "
          "stroke starts, so the page is already fading back beneath the nib",
          "ClearSelection();" in pen_case and "_activeShape = null;" in pen_case)


# ===========================================================================
# 5. EXTRACT THE REAL Veil FOR THE ROUND-TRIP HARNESS
# ===========================================================================

GEN_HEADER = """// <auto-generated>
//   GENERATED BY tools/selection_present_check.py - DO NOT EDIT.
//
//   Everything below the marker is lifted VERBATIM out of
//   src/Quill/Controls/InkSurface.cs.  The point of the harness this belongs to
//   is that it runs the REAL fade against the REAL serialiser; a hand-copied
//   Veil would only prove that a copy is pure.  Regenerate with:
//
//       python tools/selection_present_check.py
// </auto-generated>
using Quill.Helpers;
using Windows.UI;

namespace Quill.Tools;

/// <summary>InkSurface's fade, out of its control and into a console.</summary>
public sealed class RealVeil
{
    // ---- harness surface (the only hand-written code in this file) --------
    // InkSurface drives _veil from a CompositionTarget.Rendering tick and reads
    // ExportChromeless off a property the export window sets.  Neither exists
    // here, so the two inputs are exposed and everything between them is the
    // real thing.
    public void Drive(double veil, bool exportChromeless)
    {
        _veil = veil;
        ExportChromeless = exportChromeless;
    }

    public double Level => _veil;
    public bool IsVeiling => Veiling;
    public Color Apply(Color c, bool exempt) => Veil(c, exempt);
    public bool ExportChromeless { get; set; }

    // ======================= VERBATIM FROM InkSurface.cs ===================
"""


def extract_veil():
    src = read(INK)
    parts = []

    m = re.search(r"^\s*private static readonly Color VeilGrey = [^;]+;", src, flags=re.M)
    ok = check("the veil grey is extractable, and is #8E8E8E exactly",
               m is not None and "0x8E, 0x8E, 0x8E" in m.group(0),
               m.group(0).strip() if m else "MISSING")
    if not ok:
        return
    parts.append(m.group(0).rstrip())

    m2 = re.search(r"^\s*private double _veil;.*$", src, flags=re.M)
    parts.append(m2.group(0).rstrip() if m2 else "    private double _veil;")

    m3 = re.search(r"^\s*private bool Veiling =>[^;]+;", src, flags=re.M)
    ok = check("Veiling is extractable", m3 is not None)
    if not ok:
        return
    parts.append(m3.group(0).rstrip())

    i = src.index("private Color Veil(Color c, bool exempt = false)")
    # take the whole declaration including its doc comment start line
    line_start = src.rfind("\n", 0, i) + 1
    j = src.index("{", i)
    depth, k = 0, j
    while k < len(src):
        if src[k] == "{":
            depth += 1
        elif src[k] == "}":
            depth -= 1
            if depth == 0:
                break
        k += 1
    parts.append(src[line_start:k + 1].rstrip())

    text = GEN_HEADER + "\n\n".join(parts) + "\n}\n"
    text = text.replace("\r\n", "\n")
    old = read(GEN).replace("\r\n", "\n") if os.path.exists(GEN) else None
    if old != text:
        os.makedirs(os.path.dirname(GEN), exist_ok=True)
        with open(GEN, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)
        NOTES.append(("INFO", "tools/VeilRoundTrip/Veil.g.cs regenerated from source", ""))
    else:
        NOTES.append(("INFO", "tools/VeilRoundTrip/Veil.g.cs already matches source", ""))
    check("the harness's Veil is the source's Veil, character for character",
          "return Color.FromArgb(c.A," in text and "if (exempt || !Veiling) return c;" in text)


# ===========================================================================

def main():
    capability_rule()
    veil_dataflow()
    wiring()
    interactions()
    extract_veil()

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
    print("Now run the round trip:  dotnet run --project tools/VeilRoundTrip -c Debug")
    return 0


if __name__ == "__main__":
    sys.exit(main())
