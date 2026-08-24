namespace Quill.Models;

/// <summary>Copying a SET of elements at once, which is not the same problem as
/// copying one.
///
/// <para>Per-element the rule is settled: <see cref="PenStroke.CloneWithPoints"/>,
/// <see cref="ShapeElement.Clone"/> and <see cref="TextElement.Clone"/> each carry
/// every field but the Id. What a SET adds is the question of what the copies mean
/// to EACH OTHER - specifically that a table and its cells are one object split
/// across two lists, and a copy of the pair has to be wired to itself rather than
/// back to the original.</para>
///
/// <para>This lives on the models, beside the clones it composes, so it can be
/// exercised headlessly: tools/CloneRoundTrip runs the real thing rather than a
/// re-typed copy of it. That is also why there is exactly one of it - the shape
/// branch and the text branch of a duplicate used to be two initialiser lists that
/// had drifted apart, and one of them had quietly stopped carrying the layer.</para>
/// </summary>
public static class ElementClone
{
    /// <summary>11.9's duplicate: every element copied and moved by
    /// <paramref name="offset"/> on both axes.
    ///
    /// <para><paramref name="pageTexts"/> is the whole page's text list, not the
    /// selection's - a table's cells have to be found whether or not the lasso
    /// happened to catch them.</para></summary>
    public static (List<PenStroke> Strokes, List<ShapeElement> Shapes, List<TextElement> Texts) Duplicate(
        IEnumerable<PenStroke> strokes,
        IEnumerable<ShapeElement> shapes,
        IEnumerable<TextElement> texts,
        IReadOnlyList<TextElement> pageTexts,
        float offset)
    {
        var outStrokes = new List<PenStroke>();
        var outShapes = new List<ShapeElement>();
        var outTexts = new List<TextElement>();

        foreach (var s in strokes)
            outStrokes.Add(s.CloneWithPoints(
                s.Points.Select(p => new StrokePoint(p.X + offset, p.Y + offset, p.Pressure)).ToList()));

        // Which tables are being copied, so the text pass below can tell a cell
        // that has already been handled from one that has not.
        var duplicatedTables = new HashSet<Guid>();

        foreach (var sh in shapes)
        {
            var clone = sh.Clone();
            clone.X += offset;
            clone.Y += offset;
            outShapes.Add(clone);

            if (sh.Kind != ShapeKind.Table) continue;
            duplicatedTables.Add(sh.Id);
            // A table brings its cell bubbles with it (#55). This is the ONE place
            // a copy is allowed to keep a TableId, and it is re-pointed at the
            // clone - a cell still naming the original would be pulled back into
            // the original's grid by the next reflow.
            foreach (var cell in pageTexts)
            {
                if (cell.TableId != sh.Id) continue;
                var c = cell.Clone();
                c.X += offset;
                c.Y += offset;
                c.TableId = clone.Id;
                outTexts.Add(c);
            }
        }

        foreach (var t in texts)
        {
            // A cell of a table that is being copied in this same operation is
            // ALREADY above, re-linked to the copy. Cloning it again here would
            // produce a second bubble still naming the ORIGINAL table - which the
            // original's next reflow would stack straight on top of the cell it
            // came from, where nothing but a stray caret would ever reveal it.
            if (t.TableId is Guid tid && duplicatedTables.Contains(tid)) continue;
            // Any other cell is copied as a free box: its table is not part of
            // this operation, so there is nothing for it to be a cell OF.
            var c = t.CloneAsFreeBox();
            c.X += offset;
            c.Y += offset;
            outTexts.Add(c);
        }

        return (outStrokes, outShapes, outTexts);
    }
}
