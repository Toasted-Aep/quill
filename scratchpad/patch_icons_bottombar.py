import io, os, sys
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
P = os.path.join(ROOT, "src", "Quill", "Helpers", "Icons.cs")
raw = open(P, "rb").read()
crlf = b"\r\n" in raw
text = raw.decode("utf-8").replace("\r\n", "\n")

marks = dict(l.rstrip("\n").split("\t", 1)
             for l in open(os.path.join(ROOT, "scratchpad", "bottombar_icons.txt"), encoding="utf-8"))

def one(name, doc):
    return doc + "\n    public const string %s =\n        \"%s\";\n" % (name, marks[name])

# --- ChevronLeft, beside its twin -----------------------------------------
anchor = '    /// Chevron, for the collapsible rows. Stroked.\n    public const string ChevronDown = "M5 8.5 L12 15.5 L19 8.5";\n'
assert text.count(anchor) == 1, "ChevronDown anchor"
add = anchor + '''
    /// <summary>Back, for a bottom menu that was DESCENDED into (17.9). The same
    /// chevron as <see cref="ChevronDown"/>, turned to point at what it returns
    /// to, so the two read as one family rather than as two arrows. Stroked.
    ///
    /// <para>It is drawn only when a menu sits over another one - see
    /// <c>BottomMenu.ShowsBack</c>. The colour picker reached from the mode
    /// bar's Filter has it; the colour picker reached as a tool in its own right
    /// does not, and that difference is the whole of 17.9's last clause.</para></summary>
    public const string ChevronLeft = "M15.5 4.5 L8 12 L15.5 19.5";
'''
text = text.replace(anchor, add, 1)

# --- the rest, after Filter ------------------------------------------------
tail = '''    public const string Filter =
        "M 2.5 2.7 L 21.5 2.7 L 13.92 11.8 L 13.92 19.48 L 10.08 21.3 L 10.08 11.8 Z";
'''
assert text.count(tail) == 1, "Filter anchor"
block = tail + "\n" + "\n".join([
    one("Alpha", '''    /// <summary>Alpha, the colour picker's bottom menu (17.9): the checkerboard
    /// that stands for transparency in every image editor there is. A ring, then
    /// two quadrants inside its hole - even-odd fills them, because a point
    /// inside a quadrant inside the hole has crossed three edges.</summary>'''),
    one("Stretch", '''    /// <summary>Stretch, the second face of the mode bar's Scale (17.9): two jambs
    /// and a double arrow between them - the width changes and the height does
    /// not, which is the whole difference from <see cref="Scale"/>'s corner drag.
    ///
    /// <para><c>F1</c> (nonzero) because each head OVERLAPS the shaft. All five
    /// subpaths are wound clockwise on the screen's y-down axes, so nonzero
    /// unions them; even-odd would punch a notch at both joins, which is exactly
    /// the defect 16.11 measured on the old arrowhead.</para></summary>'''),
    one("Pan", '''    /// <summary>The pan tool (17.11): a cross with four heads, authored as ONE
    /// closed 24-point outline. Being a single subpath, no fill rule can break
    /// it - the same reason <see cref="Filter"/> is one polygon.</summary>'''),
    one("ItemPicker", '''    /// <summary>Item picker, the mouse tool's first choice (17.10): one object and
    /// the cursor that takes it. The square is a RING - outer outline plus a
    /// reversed inner one, so even-odd punches the hole - and the arrow is a
    /// third subpath placed clear of it, so the two cannot interfere. The arrow
    /// is <see cref="Mouse"/> at 0.55, which is what keeps the mouse tool's own
    /// mark and its first option visibly the same cursor.</summary>'''),
    one("Lasso", '''    /// <summary>Lasso, the mouse tool's second choice (17.10), which 17.10 folds
    /// into the mouse tool rather than leaving as a selection mode of its own:
    /// the loop and its dangling tail.
    ///
    /// <para>STROKED, and that is not a style choice. A lasso IS a line; drawn as
    /// a filled outline at the 18 DIP this menu uses, the ring's two edges are
    /// under a pixel apart and close up into a blob.</para></summary>'''),
    one("Partial", '''    /// <summary>Partial catch (17.10): a disc straddling the square's edge - any
    /// part inside counts. Stroked, so the overlap the mark IS cannot punch a
    /// hole in itself. Its twin below shares the square exactly, so the pair
    /// reads as one control in two states.</summary>'''),
    one("Complete", '''    /// <summary>Complete catch (17.10): the same square, with the disc wholly
    /// inside it - every point of the stroke must be enclosed.</summary>'''),
    one("LayerOne", '''    /// <summary>The ACTIVE layer scope (17.10), against <see cref="Layers"/>'s
    /// stack of three for ALL: one plate, alone, at the stack's own width.
    ///
    /// <para>Layers do not exist yet - another branch owns that model - so this
    /// mark is drawn from <see cref="Quill.Services.LayerScope"/>, which today
    /// answers with one implicit layer. The mark is authored now so the seam has
    /// nothing left to add when the model lands.</para></summary>'''),
])
text = text.replace(tail, block, 1)

# --- the tool-tag table ----------------------------------------------------
tt = '''        "Mix" => Mix,
        _ => Pen,'''
assert text.count(tt) == 1, "Tool table anchor"
text = text.replace(tt, '''        "Mix" => Mix,
        // 17.10 / 17.11. Mouse is the arrow cursor the tool IS; Pan and Rotate
        // are the two view/subject tools 17.11 adds beside the others.
        "Mouse" => Mouse,
        "Pan" => Pan,
        "Rotate" => Rotate,
        _ => Pen,''', 1)

out = text.encode("utf-8")
if crlf:
    assert b"\r\n" not in out
    out = out.replace(b"\n", b"\r\n")
open(P, "wb").write(out)
print("patched", P, "crlf" if crlf else "lf")
