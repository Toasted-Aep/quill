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
    public bool TouchesText => false;

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
    public ScaleMixedAction(List<(PenStroke, float[], float[])> strokes,
                            List<(ShapeElement, double, double, double, double)> shapes,
                            List<(TextElement, double, double, double)> texts,
                            float ax, float ay, float factor)
    {
        _strokes = strokes; _shapes = shapes; _texts = texts;
        _ax = ax; _ay = ay; _factor = factor;
    }
    public string Description => "Scale selection";
    public bool TouchesText => _texts.Count > 0;
    public void Do(NotePage page) => Apply(_factor);
    public void Undo(NotePage page) => Apply(1f);
    private void Apply(float f)
    {
        foreach (var (s, xs, ys) in _strokes)
            for (int i = 0; i < s.Points.Count && i < xs.Length; i++)
            {
                s.Points[i].X = _ax + (xs[i] - _ax) * f;
                s.Points[i].Y = _ay + (ys[i] - _ay) * f;
            }
        foreach (var (s, x, y, w, h) in _shapes)
        {
            s.X = _ax + (x - _ax) * f;
            s.Y = _ay + (y - _ay) * f;
            s.W = w * f;
            s.H = h * f;
        }
        foreach (var (t, x, y, w) in _texts)
        {
            t.X = _ax + (x - _ax) * f;
            t.Y = _ay + (y - _ay) * f;
            t.Width = Math.Max(60, w * f);
        }
    }
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
