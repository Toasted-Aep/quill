using Quill.Models;
using Windows.Foundation;

namespace Quill.Services;

public interface IPageAction
{
    string Description { get; }
    void Do(NotePage page);
    void Undo(NotePage page);
    // Whether Do/Undo can change the page's Texts (so the XAML text layer must
    // be rebuilt). Defaults to true for safety; pure ink/shape actions say false
    // so undoing a stroke no longer tears down every text box (#perf-roadmap).
    bool TouchesText => true;
    // World-space bounds of what this action touched, so undo/redo can flash a
    // highlight over the affected element (#roadmap). null = no highlight.
    Rect? AffectedBounds(NotePage page) => null;
}

// Bounds helpers shared by the AffectedBounds implementations (#roadmap).
internal static class ActionBounds
{
    public static Rect? Of(IEnumerable<PenStroke> strokes)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var s in strokes)
        {
            if (s.Points.Count == 0) continue;
            s.GetBounds(out float x0, out float y0, out float x1, out float y1);
            any = true;
            minX = Math.Min(minX, x0); minY = Math.Min(minY, y0);
            maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
        }
        return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : (Rect?)null;
    }
    public static Rect Of(ShapeElement s) =>
        new(Math.Min(s.X, s.X + s.W), Math.Min(s.Y, s.Y + s.H), Math.Abs(s.W), Math.Abs(s.H));
    public static Rect Of(TextElement t) => new(t.X, t.Y, Math.Max(60, t.Width), 40);
    public static Rect? Union(params Rect?[] rects)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var r in rects)
        {
            if (r is not { } rr) continue;
            any = true;
            minX = Math.Min(minX, rr.Left); minY = Math.Min(minY, rr.Top);
            maxX = Math.Max(maxX, rr.Right); maxY = Math.Max(maxY, rr.Bottom);
        }
        return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : (Rect?)null;
    }
}

public class AddStrokeAction : IPageAction
{
    public bool TouchesText => false;

    private readonly PenStroke _stroke;
    public AddStrokeAction(PenStroke stroke) => _stroke = stroke;
    public PenStroke Stroke => _stroke;   // pen-repair needs to identify its stroke
    public string Description => "Draw stroke";
    public void Do(NotePage page) => page.Strokes.Add(_stroke);
    public void Undo(NotePage page) => page.Strokes.Remove(_stroke);
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(new[] { _stroke });
}

public class AddStrokesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<PenStroke> _strokes;
    public AddStrokesAction(List<PenStroke> strokes) => _strokes = strokes;
    public string Description => "Paste";
    public void Do(NotePage page) => page.Strokes.AddRange(_strokes);
    public void Undo(NotePage page) { foreach (var s in _strokes) page.Strokes.Remove(s); }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_strokes);
}

public class RemoveStrokesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<(int Index, PenStroke Stroke)> _items;
    public RemoveStrokesAction(List<(int, PenStroke)> items, string description = "Erase strokes")
    {
        _items = items;
        Description = description;
    }
    public string Description { get; }
    public void Do(NotePage page)
    {
        foreach (var (_, s) in _items) page.Strokes.Remove(s);
    }
    public void Undo(NotePage page)
    {
        foreach (var (idx, s) in _items.OrderBy(i => i.Index))
            page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), s);
    }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_items.Select(i => i.Stroke));
}

public class ReplaceStrokesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<(int Index, PenStroke Stroke)> _removed;
    private readonly List<PenStroke> _added;
    public ReplaceStrokesAction(List<(int, PenStroke)> removed, List<PenStroke> added)
    {
        _removed = removed;
        _added = added;
    }
    public string Description => "Point erase";
    public void Do(NotePage page)
    {
        foreach (var (_, s) in _removed) page.Strokes.Remove(s);
        foreach (var s in _added) if (!page.Strokes.Contains(s)) page.Strokes.Add(s);
    }
    public void Undo(NotePage page)
    {
        foreach (var s in _added) page.Strokes.Remove(s);
        foreach (var (idx, s) in _removed.OrderBy(i => i.Index))
            page.Strokes.Insert(Math.Min(idx, page.Strokes.Count), s);
    }
    public Rect? AffectedBounds(NotePage page) =>
        ActionBounds.Union(ActionBounds.Of(_removed.Select(i => i.Stroke)), ActionBounds.Of(_added));
}

public class MoveStrokesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<PenStroke> _strokes;
    private readonly float _dx, _dy;
    public MoveStrokesAction(List<PenStroke> strokes, float dx, float dy)
    {
        _strokes = strokes; _dx = dx; _dy = dy;
    }
    public string Description => "Move selection";
    public void Do(NotePage page) => Shift(_dx, _dy);
    public void Undo(NotePage page) => Shift(-_dx, -_dy);
    private void Shift(float dx, float dy)
    {
        foreach (var s in _strokes)
            foreach (var p in s.Points) { p.X += dx; p.Y += dy; }
    }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_strokes);
}

public class InsertSpaceAction : IPageAction
{
    private readonly double _y;
    private readonly double _delta;
    private List<PenStroke>? _strokes;
    private List<TextElement>? _texts;
    public InsertSpaceAction(double y, double delta) { _y = y; _delta = delta; }
    public string Description => _delta >= 0 ? "Insert space" : "Remove space";
    public bool TouchesText => _texts == null || _texts.Count > 0;
    public void Do(NotePage page)
    {
        _strokes ??= page.Strokes.Where(s => s.Points.Count > 0 && s.MinY >= _y).ToList();
        _texts ??= page.Texts.Where(t => t.Y >= _y).ToList();
        Shift(_delta);
    }
    public void Undo(NotePage page) => Shift(-_delta);
    private void Shift(double d)
    {
        if (_strokes != null)
            foreach (var s in _strokes)
                foreach (var p in s.Points) p.Y += (float)d;
        if (_texts != null)
            foreach (var t in _texts) t.Y += d;
    }
}

public class AddShapeAction : IPageAction
{
    public bool TouchesText => false;

    private readonly ShapeElement _shape;
    public AddShapeAction(ShapeElement shape) => _shape = shape;
    public string Description => "Insert shape";
    public void Do(NotePage page) => page.Shapes.Add(_shape);
    public void Undo(NotePage page) => page.Shapes.Remove(_shape);
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_shape);
}

public class RemoveShapesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<(int Index, ShapeElement Shape)> _items;
    public RemoveShapesAction(List<(int, ShapeElement)> items) => _items = items;
    public string Description => "Delete shape";
    public void Do(NotePage page)
    {
        foreach (var (_, s) in _items) page.Shapes.Remove(s);
    }
    public void Undo(NotePage page)
    {
        foreach (var (idx, s) in _items.OrderBy(i => i.Index))
            page.Shapes.Insert(Math.Min(idx, page.Shapes.Count), s);
    }
    public Rect? AffectedBounds(NotePage page) =>
        ActionBounds.Union(_items.Select(i => (Rect?)ActionBounds.Of(i.Shape)).ToArray());
}

public class MoveResizeShapeAction : IPageAction
{
    public bool TouchesText => false;

    private readonly ShapeElement _shape;
    private readonly (double X, double Y, double W, double H) _from, _to;
    public MoveResizeShapeAction(ShapeElement shape,
        (double, double, double, double) from, (double, double, double, double) to)
    {
        _shape = shape; _from = from; _to = to;
    }
    public string Description => "Adjust shape";
    public void Do(NotePage page) => Apply(_to);
    public void Undo(NotePage page) => Apply(_from);
    private void Apply((double X, double Y, double W, double H) v)
    {
        _shape.X = v.X; _shape.Y = v.Y; _shape.W = v.W; _shape.H = v.H;
    }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_shape);
}

public class RotateShapeAction : IPageAction
{
    // tables carry cell text boxes that must re-place on rotation (#tablerot)
    public bool TouchesText => true;

    private readonly ShapeElement _s;
    private readonly double _from, _to;
    public RotateShapeAction(ShapeElement s, double from, double to) { _s = s; _from = from; _to = to; }
    public string Description => "Rotate";
    public void Do(NotePage page) => _s.Rotation = _to;
    public void Undo(NotePage page) => _s.Rotation = _from;
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_s);
}

public class AddTextAction : IPageAction
{
    private readonly TextElement _text;
    public TextElement Text => _text;
    public AddTextAction(TextElement text) => _text = text;
    public string Description => "Add text box";
    public void Do(NotePage page) => page.Texts.Add(_text);
    public void Undo(NotePage page) => page.Texts.Remove(_text);
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_text);
}

public class RemoveTextAction : IPageAction
{
    private readonly TextElement _text;
    private int _index;
    public RemoveTextAction(TextElement text) => _text = text;
    public string Description => "Delete text box";
    public void Do(NotePage page)
    {
        _index = page.Texts.IndexOf(_text);
        page.Texts.Remove(_text);
    }
    public void Undo(NotePage page) =>
        page.Texts.Insert(Math.Clamp(_index, 0, page.Texts.Count), _text);
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_text);
}

public class MoveTextAction : IPageAction
{
    private readonly TextElement _text;
    private readonly double _fx, _fy, _tx, _ty;
    public MoveTextAction(TextElement text, double fromX, double fromY, double toX, double toY)
    {
        _text = text; _fx = fromX; _fy = fromY; _tx = toX; _ty = toY;
    }
    public string Description => "Move text box";
    public void Do(NotePage page) { _text.X = _tx; _text.Y = _ty; }
    public void Undo(NotePage page) { _text.X = _fx; _text.Y = _fy; }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_text);
}

// Bundles several actions into one undo/redo step (e.g. moving a mixed
// selection of strokes, shapes and text boxes together).
public class CompositeAction : IPageAction
{
    private readonly List<IPageAction> _actions;
    public CompositeAction(IEnumerable<IPageAction> actions, string description)
    {
        _actions = actions.ToList();
        Description = description;
    }
    public string Description { get; }
    public bool TouchesText => _actions.Any(a => a.TouchesText);
    public void Do(NotePage page) { foreach (var a in _actions) a.Do(page); }
    public void Undo(NotePage page) { for (int i = _actions.Count - 1; i >= 0; i--) _actions[i].Undo(page); }
    public Rect? AffectedBounds(NotePage page) =>
        ActionBounds.Union(_actions.Select(a => a.AffectedBounds(page)).ToArray());
}

// Moves a set of shapes by a delta (used in mixed-selection moves).
public class MoveShapesAction : IPageAction
{
    public bool TouchesText => false;

    private readonly List<ShapeElement> _shapes;
    private readonly double _dx, _dy;
    public MoveShapesAction(List<ShapeElement> shapes, double dx, double dy)
    {
        _shapes = shapes; _dx = dx; _dy = dy;
    }
    public string Description => "Move shapes";
    public void Do(NotePage page) => Shift(_dx, _dy);
    public void Undo(NotePage page) => Shift(-_dx, -_dy);
    private void Shift(double dx, double dy)
    {
        foreach (var s in _shapes) { s.X += dx; s.Y += dy; }
    }
    public Rect? AffectedBounds(NotePage page) =>
        ActionBounds.Union(_shapes.Select(s => (Rect?)ActionBounds.Of(s)).ToArray());
}

// Moves a set of text boxes by a delta.
public class MoveTextsAction : IPageAction
{
    private readonly List<TextElement> _texts;
    private readonly double _dx, _dy;
    public MoveTextsAction(List<TextElement> texts, double dx, double dy)
    {
        _texts = texts; _dx = dx; _dy = dy;
    }
    public string Description => "Move text";
    public void Do(NotePage page) => Shift(_dx, _dy);
    public void Undo(NotePage page) => Shift(-_dx, -_dy);
    private void Shift(double dx, double dy)
    {
        foreach (var t in _texts) { t.X += dx; t.Y += dy; }
    }
    public Rect? AffectedBounds(NotePage page) =>
        ActionBounds.Union(_texts.Select(t => (Rect?)ActionBounds.Of(t)).ToArray());
}

// Adds a batch of strokes, shapes and texts (used by mixed paste).
public class AddMixedAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    public AddMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes, List<TextElement> texts)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
    }
    public string Description => "Paste";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page)
    {
        page.Strokes.AddRange(_strokes);
        page.Shapes.AddRange(_shapes);
        page.Texts.AddRange(_texts);
    }
    public void Undo(NotePage page)
    {
        foreach (var s in _strokes) page.Strokes.Remove(s);
        foreach (var s in _shapes) page.Shapes.Remove(s);
        foreach (var t in _texts) page.Texts.Remove(t);
    }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Union(
        ActionBounds.Of(_strokes),
        ActionBounds.Union(_shapes.Select(s => (Rect?)ActionBounds.Of(s)).ToArray()),
        ActionBounds.Union(_texts.Select(t => (Rect?)ActionBounds.Of(t)).ToArray()));
}

// Removes a batch of strokes, shapes and texts (used by mixed delete).
public class RemoveMixedAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    public RemoveMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes, List<TextElement> texts)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
    }
    public string Description => "Delete selection";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page)
    {
        foreach (var s in _strokes) page.Strokes.Remove(s);
        foreach (var s in _shapes) page.Shapes.Remove(s);
        foreach (var t in _texts) page.Texts.Remove(t);
    }
    public void Undo(NotePage page)
    {
        page.Strokes.AddRange(_strokes);
        page.Shapes.AddRange(_shapes);
        page.Texts.AddRange(_texts);
    }
    public Rect? AffectedBounds(NotePage page) => ActionBounds.Union(
        ActionBounds.Of(_strokes),
        ActionBounds.Union(_shapes.Select(s => (Rect?)ActionBounds.Of(s)).ToArray()),
        ActionBounds.Union(_texts.Select(t => (Rect?)ActionBounds.Of(t)).ToArray()));
}

// Repositions table cell text boxes after their table moves or resizes (#40).
public class RepositionTextsAction : IPageAction
{
    private readonly List<(TextElement T, double FromX, double FromY, double ToX, double ToY)> _items;
    public RepositionTextsAction(List<(TextElement, double, double, double, double)> items) => _items = items;
    public string Description => "Reflow table";
    public void Do(NotePage page) { foreach (var (t, _, _, tx, ty) in _items) { t.X = tx; t.Y = ty; } }
    public void Undo(NotePage page) { foreach (var (t, fx, fy, _, _) in _items) { t.X = fx; t.Y = fy; } }
}

// Changes a table's grid dimensions undoably (#40).
public class TableGridAction : IPageAction
{
    private readonly ShapeElement _s;
    private readonly int _fr, _fc, _tr, _tc;
    public TableGridAction(ShapeElement s, int fromRows, int fromCols, int toRows, int toCols)
    {
        _s = s; _fr = fromRows; _fc = fromCols; _tr = toRows; _tc = toCols;
    }
    public string Description => "Resize table grid";
    public void Do(NotePage page) { _s.TRows = _tr; _s.TCols = _tc; }
    public void Undo(NotePage page) { _s.TRows = _fr; _s.TCols = _fc; }
}

// Changes a table's column widths / row heights (and outer size) undoably (#49).
public class TableLayoutAction : IPageAction
{
    private readonly ShapeElement _s;
    private readonly List<double> _fcw, _frh, _tcw, _trh;
    private readonly double _fw, _fh, _tw, _th;
    public TableLayoutAction(ShapeElement s,
        List<double> fromColW, List<double> fromRowH, double fromW, double fromH,
        List<double> toColW, List<double> toRowH, double toW, double toH)
    {
        _s = s;
        _fcw = fromColW.ToList(); _frh = fromRowH.ToList(); _fw = fromW; _fh = fromH;
        _tcw = toColW.ToList(); _trh = toRowH.ToList(); _tw = toW; _th = toH;
    }
    public string Description => "Resize table cells";
    public void Do(NotePage page) { _s.TColW = _tcw.ToList(); _s.TRowH = _trh.ToList(); _s.W = _tw; _s.H = _th; }
    public void Undo(NotePage page) { _s.TColW = _fcw.ToList(); _s.TRowH = _frh.ToList(); _s.W = _fw; _s.H = _fh; }
}

// Shifts table cells' row/column indices (used when inserting/deleting) (#49).
public class ShiftTableCellsAction : IPageAction
{
    private readonly List<TextElement> _cells;
    private readonly int _dRow, _dCol;
    public ShiftTableCellsAction(List<TextElement> cells, int dRow, int dCol)
    {
        _cells = cells; _dRow = dRow; _dCol = dCol;
    }
    public string Description => "Reindex table cells";
    public void Do(NotePage page) { foreach (var c in _cells) { c.TableRow += _dRow; c.TableCol += _dCol; } }
    public void Undo(NotePage page) { foreach (var c in _cells) { c.TableRow -= _dRow; c.TableCol -= _dCol; } }
}

// Uniformly scales a mixed selection about an anchor point (#54).
// Snapshots the original geometry, so Undo restores it exactly.
public class ScaleMixedAction : IPageAction
{
    private readonly List<(PenStroke S, float[] Xs, float[] Ys)> _strokes;
    private readonly List<(ShapeElement S, double X, double Y, double W, double H)> _shapes;
    private readonly List<(TextElement T, double X, double Y, double W)> _texts;
    private readonly float _ax, _ay, _factor;
    // 17.9's Scale is "on/off, and STRETCH", so the factor is per axis. The
    // parameter is optional and defaults to the uniform case, which is why every
    // existing caller is untouched and an old undo entry cannot change meaning.
    private readonly float _factorY;
    public ScaleMixedAction(List<(PenStroke, float[], float[])> strokes,
                            List<(ShapeElement, double, double, double, double)> shapes,
                            List<(TextElement, double, double, double)> texts,
                            float ax, float ay, float factor, float factorY = float.NaN)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
        _ax = ax; _ay = ay; _factor = factor;
        _factorY = float.IsNaN(factorY) ? factor : factorY;
    }
    public string Description => "Scale selection";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page) => Apply(_factor, _factorY);
    public void Undo(NotePage page) => Apply(1f, 1f);
    private void Apply(float f, float g)
    {
        foreach (var (s, xs, ys) in _strokes)
            for (int i = 0; i < s.Points.Count && i < xs.Length; i++)
            {
                s.Points[i].X = _ax + (xs[i] - _ax) * f;
                s.Points[i].Y = _ay + (ys[i] - _ay) * g;
            }
        foreach (var (s, x, y, w, h) in _shapes)
        {
            s.X = _ax + (x - _ax) * f;
            s.Y = _ay + (y - _ay) * g;
            s.W = w * f;
            s.H = h * g;
        }
        foreach (var (t, x, y, w) in _texts)
        {
            // A text box reflows: it has a width and no height, so the y factor
            // MOVES it and only the x factor resizes it. Same rule as the live
            // drag in InkSurface.ApplyScaleLive, and it has to be, or the commit
            // would put the box somewhere other than where the user watched it
            // go - which is the failure the two-copies-of-the-arithmetic shape
            // of this file makes easy and this comment exists to prevent.
            t.X = _ax + (x - _ax) * f;
            t.Y = _ay + (y - _ay) * g;
            t.Width = Math.Max(60, w * f);
        }
    }
}

/// <summary>CONCEPTS-REF 16.2: the selection bar's flip-horizontal and
/// flip-vertical, for a selection of any mixture of kinds.
///
/// <para><b>Its own inverse.</b> Mirroring about a fixed axis twice is the
/// identity, so Do and Undo are the same call and there is no snapshot to hold
/// or to get out of step with the page. That is worth more than it looks: the
/// alternative - storing every original coordinate, as
/// <see cref="ScaleMixedAction"/> must - keeps a second copy of every point in
/// the selection alive in the undo stack for as long as the page is open.</para>
///
/// <para>The axis is captured at construction from the selection's bounds and
/// never recomputed, because undoing must mirror about the SAME line the flip
/// used, not about wherever the selection's bounds happen to be later.</para></summary>
public class MirrorMixedAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    private readonly double _axis;
    private readonly bool _horizontal;

    public MirrorMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes,
                             List<TextElement> texts, double axis, bool horizontal)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
        _axis = axis; _horizontal = horizontal;
    }

    public string Description => _horizontal ? "Flip horizontal" : "Flip vertical";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page) => Apply();
    public void Undo(NotePage page) => Apply();

    private void Apply()
    {
        double a2 = _axis * 2;
        foreach (var s in _strokes)
            foreach (var p in s.Points)
            {
                if (_horizontal) p.X = (float)(a2 - p.X);
                else p.Y = (float)(a2 - p.Y);
            }
        foreach (var s in _shapes)
        {
            // X/Y is the top-left of the bounds for every kind except Line, where
            // it is the START point and W/H are a SIGNED delta. Mirroring the
            // start and negating the delta is right for both readings at once:
            // for a box, X + W is the far edge, and reflecting the far edge to
            // become the near one is exactly what a flip does.
            if (_horizontal) { s.X = a2 - s.X - s.W; s.W = s.W; }
            else { s.Y = a2 - s.Y - s.H; s.H = s.H; }
            // A rotation reflects too, or a tilted shape would come back tilted
            // the same way and the flip would look like a translation.
            if (Math.Abs(s.Rotation) > 0.001) s.Rotation = -s.Rotation;
        }
        foreach (var t in _texts)
        {
            // Text is NOT mirrored glyph-for-glyph - reversed writing is not what
            // "flip" means for a text box. Its BOX is reflected so it lands where
            // the mirrored layout puts it, and it stays readable.
            if (_horizontal) t.X = a2 - t.X - Math.Max(60, t.Width);
            else t.Y = a2 - t.Y - 40;
            if (Math.Abs(t.Rotation) > 0.001) t.Rotation = -t.Rotation;
        }
    }

    public Rect? AffectedBounds(NotePage page) => ActionBounds.Union(
        ActionBounds.Of(_strokes),
        _shapes.Count > 0 ? ActionBounds.Union(_shapes.Select(s => (Rect?)ActionBounds.Of(s)).ToArray()) : null,
        _texts.Count > 0 ? ActionBounds.Union(_texts.Select(t => (Rect?)ActionBounds.Of(t)).ToArray()) : null);
}

/// <summary>CONCEPTS-REF 16.2's bottom row: Rotate. A quarter turn clockwise
/// about the selection's centre, for a selection of any mixture of kinds.
///
/// <para>Undo turns the other way rather than restoring a snapshot, for the same
/// reason <see cref="MirrorMixedAction"/> holds none: four presses return the
/// selection to where it started EXACTLY, because a quarter turn about a fixed
/// centre is exact in floating point for the axis-swap form used here (the
/// coordinates are exchanged and negated, never multiplied by a sine).</para></summary>
public class RotateQuarterMixedAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    private readonly double _cx, _cy;
    // 17.11's rotate tool turns BOTH ways. One flag rather than three clockwise
    // turns for one anticlockwise one: three turns is three undo entries for a
    // gesture the user made once, and the arithmetic below is already symmetric
    // - Undo has always been the opposite turn.
    private readonly bool _cw;

    public RotateQuarterMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes,
                                    List<TextElement> texts, double cx, double cy,
                                    bool clockwise = true)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
        _cx = cx; _cy = cy; _cw = clockwise;
    }

    public string Description => "Rotate selection";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page) => Apply(_cw);
    public void Undo(NotePage page) => Apply(!_cw);

    private (double X, double Y) Turn(double x, double y, bool cw)
    {
        double dx = x - _cx, dy = y - _cy;
        return cw ? (_cx - dy, _cy + dx) : (_cx + dy, _cy - dx);
    }

    private void Apply(bool cw)
    {
        foreach (var s in _strokes)
            foreach (var p in s.Points)
            {
                var (nx, ny) = Turn(p.X, p.Y, cw);
                p.X = (float)nx; p.Y = (float)ny;
            }
        foreach (var s in _shapes)
        {
            // Turn the corner that BECOMES the new top-left, then swap the
            // extents. Taking the stored X/Y through the turn instead would put
            // a box a width away from where the user watched it go.
            double x0 = Math.Min(s.X, s.X + s.W), y0 = Math.Min(s.Y, s.Y + s.H);
            double w = Math.Abs(s.W), h = Math.Abs(s.H);
            var (nx, ny) = cw ? Turn(x0, y0 + h, true) : Turn(x0 + w, y0, false);
            s.X = nx; s.Y = ny; s.W = h; s.H = w;
            s.Rotation += cw ? 90 : -90;
            if (s.Rotation >= 360) s.Rotation -= 360;
            if (s.Rotation <= -360) s.Rotation += 360;
        }
        foreach (var t in _texts)
        {
            double w = Math.Max(60, t.Width), h = 40;
            var (nx, ny) = cw ? Turn(t.X, t.Y + h, true) : Turn(t.X + w, t.Y, false);
            t.X = nx; t.Y = ny;
            // The box does NOT swap width for height: a text box's height comes
            // from its wrapped content, so trading them would reflow the text and
            // a second press would not put it back.
            t.Rotation += cw ? 90 : -90;
            if (t.Rotation >= 360) t.Rotation -= 360;
            if (t.Rotation <= -360) t.Rotation += 360;
        }
    }
}

/// <summary>CONCEPTS-REF 17.11a requirement 2: <i>"I want every rotatable object
/// to freely rotate."</i> An ARBITRARY angle about an arbitrary pivot, for a
/// selection of any mixture of kinds.
///
/// <para><b>This is what the rotate tool's sweep commits now.</b> 17.11a's
/// reason for quarter steps was that <c>TextElement</c> "is an axis-aligned box
/// and takes no rotation at all" - which has not been true since #20: it carries
/// <c>Rotation</c>, the Win2D path draws it, the editing overlay carries it as a
/// RenderTransform, and its grip bar has had a free-drag rotate handle all
/// along. So the third subject kind honours an arbitrary angle and the reason
/// for rounding to 90 is gone. <see cref="RotateQuarterMixedAction"/> STAYS: it
/// is what the mode bar's Rotate BUTTON commits, and a discrete quarter turn is
/// a different thing to want than a free drag, not a degraded one.</para>
///
/// <para><b>Why the before-state is held for shapes and text but not for ink.</b>
/// A free angle is a sine and a cosine, so unlike the quarter turn it is not
/// exactly invertible in floating point. A shape or a text box is three numbers
/// and a selection holds a handful of them, so the exact before-state is simply
/// kept - an angle that crept a fraction of a degree per undo would eventually
/// be a visible tilt on a box nobody touched. A stroke is thousands of points,
/// and a copy of all of them per action would put the undo stack in the same
/// size class as the page; ink is turned back by the opposite rotation instead,
/// whose error is a rounding step of a float per cycle and is orders below the
/// width of the thinnest nib at any zoom this app offers.</para></summary>
public class RotateFreeMixedAction : IPageAction
{
    /// <summary>A text box and the RENDERED size it was turned about.
    ///
    /// <para>A text box's height is not in the model - it comes from the wrapped
    /// content, and only the live XAML container knows it. So the caller
    /// measures once and the action HOLDS that measurement: reading it again on
    /// undo could give a different number (the text layer may have been rebuilt,
    /// or the box re-wrapped) and the box would come back a few units from where
    /// it left.</para></summary>
    public readonly record struct SizedText(TextElement T, double W, double H);

    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<SizedText> _texts;
    private readonly double _cx, _cy;
    private readonly double _deg;
    private readonly List<(double X, double Y, double Rot)> _shapeFrom = new();
    private readonly List<(double X, double Y, double Rot)> _textFrom = new();

    public RotateFreeMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes,
                                 List<SizedText> texts, double cx, double cy, double degrees)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
        _cx = cx; _cy = cy; _deg = degrees;
        foreach (var s in _shapes) _shapeFrom.Add((s.X, s.Y, s.Rotation));
        foreach (var e in _texts) _textFrom.Add((e.T.X, e.T.Y, e.T.Rotation));
    }

    public string Description => "Rotate selection";
    public bool TouchesText => _texts.Count > 0;

    /// <summary>The angle committed, in degrees. The drag reads it back so a
    /// gesture that ended where it started can be dropped rather than pushed -
    /// an undo entry for a rotation of nothing is noise in the stack.</summary>
    public double Degrees => _deg;

    private (double X, double Y) Turn(double x, double y, double cos, double sin)
    {
        double dx = x - _cx, dy = y - _cy;
        return (_cx + dx * cos - dy * sin, _cy + dx * sin + dy * cos);
    }

    public void Do(NotePage page)
    {
        double r = _deg * Math.PI / 180.0;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        SpinInk(cos, sin);
        for (int i = 0; i < _shapes.Count; i++)
        {
            var s = _shapes[i];
            var f = _shapeFrom[i];
            // A shape renders as its box turned about the box's OWN centre, so a
            // rotation about an outside pivot is exactly two things: carry the
            // centre round the pivot, and add the angle. W and H are untouched -
            // the quarter turn's extent swap has no meaning at 37 degrees, and
            // the renderer would double-count it.
            double hw = s.W / 2, hh = s.H / 2;
            var (nx, ny) = Turn(f.X + hw, f.Y + hh, cos, sin);
            s.X = nx - hw; s.Y = ny - hh;
            s.Rotation = Wrap(f.Rot + _deg);
        }
        for (int i = 0; i < _texts.Count; i++)
        {
            var e = _texts[i];
            var f = _textFrom[i];
            // The same arithmetic, and it is only right because a text box turns
            // about its own centre too - RenderTransformOrigin 0.5,0.5 on the
            // container, mirrored by the Win2D path. A box that rotated about its
            // top-left would need the CORNER carried instead of the centre.
            double hw = e.W / 2, hh = e.H / 2;
            var (nx, ny) = Turn(f.X + hw, f.Y + hh, cos, sin);
            e.T.X = nx - hw; e.T.Y = ny - hh;
            e.T.Rotation = Wrap(f.Rot + _deg);
        }
    }

    public void Undo(NotePage page)
    {
        double r = -_deg * Math.PI / 180.0;
        SpinInk(Math.Cos(r), Math.Sin(r));
        for (int i = 0; i < _shapes.Count; i++)
        {
            var f = _shapeFrom[i];
            _shapes[i].X = f.X; _shapes[i].Y = f.Y; _shapes[i].Rotation = f.Rot;
        }
        for (int i = 0; i < _texts.Count; i++)
        {
            var f = _textFrom[i];
            _texts[i].T.X = f.X; _texts[i].T.Y = f.Y; _texts[i].T.Rotation = f.Rot;
        }
    }

    private void SpinInk(double cos, double sin)
    {
        foreach (var s in _strokes)
            foreach (var p in s.Points)
            {
                var (nx, ny) = Turn(p.X, p.Y, cos, sin);
                p.X = (float)nx; p.Y = (float)ny;
            }
    }

    /// <summary>Into (-180, 180], the same window
    /// <c>InkSurface.WrapDegrees</c> keeps the page angle in, so a stored object
    /// angle and a stored page angle read the same way.</summary>
    private static double Wrap(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }

    public Rect? AffectedBounds(NotePage page) => ActionBounds.Union(
        ActionBounds.Of(_strokes),
        _shapes.Count > 0 ? ActionBounds.Union(_shapes.Select(s => (Rect?)ActionBounds.Of(s)).ToArray()) : null,
        _texts.Count > 0 ? ActionBounds.Union(_texts.Select(e => (Rect?)ActionBounds.Of(e.T)).ToArray()) : null);
}

/// <summary>CONCEPTS-REF 16.9: "the controls stay usable and EDITING THEM EDITS
/// THE SELECTION." The dial's size / stability / opacity / colour, written to a
/// selection of strokes instead of to the active pen.
///
/// <para>One action per property rather than one per stroke, so a scrub across
/// the dial does not bury the undo stack.</para></summary>
public class RestyleStrokesAction : IPageAction
{
    public enum Field { Size, Stability, Opacity, Colour }

    private readonly List<PenStroke> _strokes;
    private readonly Field _field;
    private readonly List<(float Size, float Sens, float? Opacity, string Colour)> _before = new();
    private readonly float _value;
    private readonly string _colour;

    public RestyleStrokesAction(List<PenStroke> strokes, Field field, float value, string colour = "")
    {
        _strokes = strokes; _field = field; _value = value; _colour = colour;
        foreach (var s in strokes) _before.Add((s.Size, s.Sens, s.Opacity, s.Color));
    }

    public string Description => _field switch
    {
        Field.Size => "Stroke size",
        Field.Stability => "Stroke stability",
        Field.Opacity => "Stroke opacity",
        _ => "Stroke colour",
    };
    public bool TouchesText => false;

    public void Do(NotePage page)
    {
        foreach (var s in _strokes)
            switch (_field)
            {
                case Field.Size: s.Size = _value; break;
                case Field.Stability: s.Sens = _value; break;
                // null IS the opaque value on the wire (see PenStroke.Opacity):
                // writing 1f instead would add a field to every restyled stroke
                // in a 53 MB library for no change in appearance.
                case Field.Opacity: s.Opacity = _value >= 0.999f ? null : _value; break;
                default: s.Color = _colour; break;
            }
    }

    public void Undo(NotePage page)
    {
        for (int i = 0; i < _strokes.Count && i < _before.Count; i++)
        {
            var b = _before[i];
            _strokes[i].Size = b.Size;
            _strokes[i].Sens = b.Sens;
            _strokes[i].Opacity = b.Opacity;
            _strokes[i].Color = b.Colour;
        }
    }

    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_strokes);
}

/// <summary>CONCEPTS-REF 16.2's padlock. Its own inverse in the same sense the
/// mirror is: the flag's previous value is captured per element, so unlocking a
/// mixed selection restores exactly what each element had.</summary>
public class LockMixedAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    private readonly List<bool> _sBefore = new(), _hBefore = new(), _tBefore = new();
    private readonly bool _to;

    public LockMixedAction(List<PenStroke> strokes, List<ShapeElement> shapes,
                           List<TextElement> texts, bool to)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts; _to = to;
        foreach (var s in strokes) _sBefore.Add(s.Locked);
        foreach (var s in shapes) _hBefore.Add(s.Locked);
        foreach (var t in texts) _tBefore.Add(t.Locked);
    }

    public string Description => _to ? "Lock selection" : "Unlock selection";
    public bool TouchesText => false;

    public void Do(NotePage page)
    {
        foreach (var s in _strokes) s.Locked = _to;
        foreach (var s in _shapes) s.Locked = _to;
        foreach (var t in _texts) t.Locked = _to;
    }

    public void Undo(NotePage page)
    {
        for (int i = 0; i < _strokes.Count && i < _sBefore.Count; i++) _strokes[i].Locked = _sBefore[i];
        for (int i = 0; i < _shapes.Count && i < _hBefore.Count; i++) _shapes[i].Locked = _hBefore[i];
        for (int i = 0; i < _texts.Count && i < _tBefore.Count; i++) _texts[i].Locked = _tBefore[i];
    }
}

/// <summary>CONCEPTS-REF 18: move a selection to another layer.
///
/// <para>Modelled on <see cref="LockMixedAction"/>, and for the same reason: the
/// per-element state it changes is a single field, so the undo record is the
/// list of elements plus their previous values and nothing is cloned.</para>
///
/// <para>Recording the BEFORE keys individually rather than one shared value is
/// the whole point — a selection can span layers, and an undo that put
/// everything back on one of them would quietly merge two layers' worth of
/// drawing.</para></summary>
public class AssignLayerAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    private readonly List<ShapeElement> _shapes;
    private readonly List<TextElement> _texts;
    private readonly List<int> _sBefore = new(), _hBefore = new(), _tBefore = new();
    private readonly int _to;

    public AssignLayerAction(List<PenStroke> strokes, List<ShapeElement> shapes,
                             List<TextElement> texts, int toLayerKey)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts; _to = toLayerKey;
        foreach (var s in strokes) _sBefore.Add(s.LayerKey);
        foreach (var s in shapes) _hBefore.Add(s.LayerKey);
        foreach (var t in texts) _tBefore.Add(t.LayerKey);
    }

    public string Description => "Move to layer";
    // The Texts' LAYER changes, not their content or geometry, so the XAML text
    // layer does not need tearing down and rebuilding.
    public bool TouchesText => false;

    public void Do(NotePage page)
    {
        foreach (var s in _strokes) s.LayerKey = _to;
        foreach (var s in _shapes) s.LayerKey = _to;
        foreach (var t in _texts) t.LayerKey = _to;
    }

    public void Undo(NotePage page)
    {
        for (int i = 0; i < _strokes.Count && i < _sBefore.Count; i++) _strokes[i].LayerKey = _sBefore[i];
        for (int i = 0; i < _shapes.Count && i < _hBefore.Count; i++) _shapes[i].LayerKey = _hBefore[i];
        for (int i = 0; i < _texts.Count && i < _tBefore.Count; i++) _texts[i].LayerKey = _tBefore[i];
    }

    public Rect? AffectedBounds(NotePage page)
    {
        Rect? r = ActionBounds.Of(_strokes);
        foreach (var s in _shapes) r = ActionBounds.Union(r, ActionBounds.Of(s));
        foreach (var t in _texts) r = ActionBounds.Union(r, ActionBounds.Of(t));
        return r;
    }
}

/// <summary>CONCEPTS-REF 18.12 item 3: DELETE A LAYER, AND ITS DRAWING WITH IT.
///
/// <para><b>This action exists because of the ruling, not beside it.</b> The
/// model's original default reassigned a deleted layer's content to the base
/// layer, which could not destroy work; the user ruled for Photoshop's
/// behaviour instead, and the moment deleting a layer can destroy a drawing the
/// deletion has to be undoable and <b>the undo has to bring the CONTENT
/// back</b>. A <c>Layer</c> row returning empty would be worse than the old
/// default: the user would believe their work was recoverable and find an empty
/// shell.</para>
///
/// <para>So this captures the elements themselves — not copies, the very
/// objects — together with the INDEX each held in its list, and puts them back
/// where they were. Their <c>LayerKey</c> is never touched on the way out
/// (<c>List.RemoveAll</c> does not modify what it removes), so every element
/// returns still naming the layer it belonged to, and the layer it names is
/// reinserted at its own old position in the stack. Undo therefore restores
/// z-order and membership together rather than dropping everything on top of
/// the base layer.</para>
///
/// <para>Nothing is cloned and nothing is serialised: an undo stack a hundred
/// layer-deletes deep costs the references it already held.</para></summary>
public class RemoveLayerAction : IPageAction
{
    private readonly int _key;
    private readonly LayerRemoval _mode;

    // DeleteContent: what was taken, and the index it occupied.
    private readonly List<(int Index, PenStroke Item)> _strokes = new();
    private readonly List<(int Index, ShapeElement Item)> _shapes = new();
    private readonly List<(int Index, TextElement Item)> _texts = new();
    // ReassignToBase: what was merely repointed. Their old key is _key.
    private readonly List<PenStroke> _movedStrokes = new();
    private readonly List<ShapeElement> _movedShapes = new();
    private readonly List<TextElement> _movedTexts = new();

    private Layer? _layer;
    private int _layerIndex = -1;
    private int _activeBefore;
    private bool _applied;

    public RemoveLayerAction(int layerKey, LayerRemoval mode = LayerRemoval.DeleteContent)
    {
        _key = layerKey;
        _mode = mode;
    }

    public string Description => _mode == LayerRemoval.DeleteContent
        ? "Delete layer" : "Remove layer, keep drawing";

    public void Do(NotePage page)
    {
        _strokes.Clear(); _shapes.Clear(); _texts.Clear();
        _movedStrokes.Clear(); _movedShapes.Clear(); _movedTexts.Clear();
        _layer = null; _layerIndex = -1;
        _activeBefore = page.ActiveLayer;

        var ls = page.Layers;
        if (ls != null)
            for (int i = 0; i < ls.Count; i++)
                if (ls[i].Key == _key) { _layer = ls[i]; _layerIndex = i; break; }

        // Ascending, so Undo can insert at the same indices in the same order.
        if (_mode == LayerRemoval.DeleteContent)
        {
            for (int i = 0; i < page.Strokes.Count; i++)
                if (page.Strokes[i].LayerKey == _key) _strokes.Add((i, page.Strokes[i]));
            for (int i = 0; i < page.Shapes.Count; i++)
                if (page.Shapes[i].LayerKey == _key) _shapes.Add((i, page.Shapes[i]));
            for (int i = 0; i < page.Texts.Count; i++)
                if (page.Texts[i].LayerKey == _key) _texts.Add((i, page.Texts[i]));
        }
        else
        {
            foreach (var s in page.Strokes) if (s.LayerKey == _key) _movedStrokes.Add(s);
            foreach (var s in page.Shapes) if (s.LayerKey == _key) _movedShapes.Add(s);
            foreach (var t in page.Texts) if (t.LayerKey == _key) _movedTexts.Add(t);
        }

        // PageLayers refuses the base layer and refuses to empty the list. When
        // it does, nothing was taken and Undo must not put anything back.
        _applied = PageLayers.Remove(page, _key, _mode);
    }

    public void Undo(NotePage page)
    {
        if (!_applied) return;
        var ls = PageLayers.Materialise(page);
        if (_layer != null && ls.All(l => l.Key != _key))
            ls.Insert(Math.Clamp(_layerIndex, 0, ls.Count), _layer);

        if (_mode == LayerRemoval.DeleteContent)
        {
            foreach (var (i, s) in _strokes) page.Strokes.Insert(Math.Clamp(i, 0, page.Strokes.Count), s);
            foreach (var (i, s) in _shapes) page.Shapes.Insert(Math.Clamp(i, 0, page.Shapes.Count), s);
            foreach (var (i, t) in _texts) page.Texts.Insert(Math.Clamp(i, 0, page.Texts.Count), t);
        }
        else
        {
            foreach (var s in _movedStrokes) s.LayerKey = _key;
            foreach (var s in _movedShapes) s.LayerKey = _key;
            foreach (var t in _movedTexts) t.LayerKey = _key;
        }
        page.ActiveLayer = _activeBefore;
    }

    public Rect? AffectedBounds(NotePage page)
    {
        Rect? r = ActionBounds.Of(_strokes.Select(p => p.Item).Concat(_movedStrokes));
        foreach (var s in _shapes.Select(p => p.Item).Concat(_movedShapes))
            r = ActionBounds.Union(r, ActionBounds.Of(s));
        foreach (var t in _texts.Select(p => p.Item).Concat(_movedTexts))
            r = ActionBounds.Union(r, ActionBounds.Of(t));
        return r;
    }
}

/// <summary>CONCEPTS-REF 16.2's PAPERCLIP: swap the file behind an attachment
/// and keep it exactly where it is.
///
/// <para>Position and WIDTH are preserved and only the HEIGHT moves, to the
/// new file's aspect. That is the one rule that makes this a replacement
/// rather than a delete-and-insert: the user placed and sized that rectangle,
/// and a swap that re-centred it or re-fitted it to 520 DIP would throw the
/// placement away. InkSurface.UpdateEquationImage makes the same choice for
/// the same reason.</para>
///
/// <para>Holds paths, not pixels: two strings and a double, whatever the file
/// weighs. The bitmap cache is keyed by path and rebuilt from disk on demand,
/// so an undo stack a hundred swaps deep costs nothing.</para></summary>
public class ReplaceImageAction : IPageAction
{
    public bool TouchesText => false;

    private readonly ShapeElement _shape;
    private readonly string? _fromPath, _toPath;
    private readonly string? _fromLatex;
    private readonly double _fromH, _toH;

    public ReplaceImageAction(ShapeElement shape, string toPath, double toH)
    {
        _shape = shape;
        _fromPath = shape.ImagePath;
        // An equation's image IS its rendering, so replacing the picture ends
        // the equation: leaving the LaTeX behind would make the next right-click
        // re-render over the file the user has just chosen.
        _fromLatex = shape.EquationLatex;
        _fromH = shape.H;
        _toPath = toPath;
        _toH = toH;
    }

    public string Description => "Replace attachment";

    public void Do(NotePage page)
    {
        _shape.ImagePath = _toPath;
        _shape.EquationLatex = null;
        _shape.H = _toH;
    }

    public void Undo(NotePage page)
    {
        _shape.ImagePath = _fromPath;
        _shape.EquationLatex = _fromLatex;
        _shape.H = _fromH;
    }

    public Rect? AffectedBounds(NotePage page) => ActionBounds.Of(_shape);
}

// Changes per-cell fill colour and border styling (#roadmap: table enhancements).
public class CellStyleAction : IPageAction
{
    private readonly TextElement _cell;
    private readonly string? _fromFill, _toFill;
    private readonly string? _fromBorder, _toBorder;
    private readonly float? _fromBorderW, _toBorderW;
    public CellStyleAction(TextElement cell,
        string? fromFill, string? toFill,
        string? fromBorder, string? toBorder,
        float? fromBorderW, float? toBorderW)
    {
        _cell = cell;
        _fromFill = fromFill; _toFill = toFill;
        _fromBorder = fromBorder; _toBorder = toBorder;
        _fromBorderW = fromBorderW; _toBorderW = toBorderW;
    }
    public string Description => "Cell style";
    public void Do(NotePage page) { _cell.FillColor = _toFill; _cell.BorderColor = _toBorder; _cell.BorderWidth = _toBorderW; }
    public void Undo(NotePage page) { _cell.FillColor = _fromFill; _cell.BorderColor = _fromBorder; _cell.BorderWidth = _fromBorderW; }
}

// Merges or splits table cells by adjusting span properties.
public class CellMergeAction : IPageAction
{
    private readonly TextElement _cell;
    private readonly int _fromColSpan, _toColSpan, _fromRowSpan, _toRowSpan;
    private readonly List<TextElement> _hiddenTexts;    // texts removed on merge, restored on split
    public CellMergeAction(TextElement cell, int fromColSpan, int fromRowSpan,
        int toColSpan, int toRowSpan, List<TextElement> hiddenTexts)
    {
        _cell = cell;
        _fromColSpan = fromColSpan; _toColSpan = toColSpan;
        _fromRowSpan = fromRowSpan; _toRowSpan = toRowSpan;
        _hiddenTexts = hiddenTexts;
    }
    public string Description => _toColSpan > 1 || _toRowSpan > 1 ? "Merge cells" : "Split cell";
    public void Do(NotePage page)
    {
        _cell.CellColSpan = _toColSpan; _cell.CellRowSpan = _toRowSpan;
        foreach (var t in _hiddenTexts) page.Texts.Remove(t);
    }
    public void Undo(NotePage page)
    {
        _cell.CellColSpan = _fromColSpan; _cell.CellRowSpan = _fromRowSpan;
        page.Texts.AddRange(_hiddenTexts);
    }
}

// Toggles bold header row on a table.
public class HeaderRowAction : IPageAction
{
    public bool TouchesText => false;

    private readonly ShapeElement _table;
    private readonly bool _from, _to;
    public HeaderRowAction(ShapeElement table, bool from, bool to)
    { _table = table; _from = from; _to = to; }
    public string Description => "Toggle header row";
    public void Do(NotePage page) => _table.HeaderRow = _to;
    public void Undo(NotePage page) => _table.HeaderRow = _from;
}

public class UndoRedoManager
{
    private readonly Stack<IPageAction> _undo = new();
    private readonly Stack<IPageAction> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public IPageAction? PeekUndo => _undo.Count > 0 ? _undo.Peek() : null;
    public IPageAction? PeekRedo => _redo.Count > 0 ? _redo.Peek() : null;

    /// <summary>Push an action. If alreadyDone, the page was mutated live and Do() is skipped.</summary>
    public void Push(IPageAction action, NotePage page, bool alreadyDone = false)
    {
        if (!alreadyDone) action.Do(page);
        _undo.Push(action);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo(NotePage page)
    {
        if (_undo.Count == 0) return;
        var a = _undo.Pop();
        a.Undo(page);
        _redo.Push(a);
        Changed?.Invoke();
    }

    public void Redo(NotePage page)
    {
        if (_redo.Count == 0) return;
        var a = _redo.Pop();
        a.Do(page);
        _undo.Push(a);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>
    /// If the most recent action matches, drop it from the history without
    /// undoing it. Used to retire the Add action of a text box that was
    /// created then immediately discarded empty, so it leaves no dead step.
    /// </summary>
    public bool TryDiscardTop(Func<IPageAction, bool> match)
    {
        if (_undo.Count > 0 && match(_undo.Peek()))
        {
            _undo.Pop();
            Changed?.Invoke();
            return true;
        }
        return false;
    }

    public IReadOnlyList<string> History => _undo.Select(a => a.Description).ToList();
}
