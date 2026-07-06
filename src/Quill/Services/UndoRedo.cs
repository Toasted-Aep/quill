using Quill.Models;

namespace Quill.Services;

public interface IPageAction
{
    string Description { get; }
    void Do(NotePage page);
    void Undo(NotePage page);
}

public class AddStrokeAction : IPageAction
{
    private readonly PenStroke _stroke;
    public AddStrokeAction(PenStroke stroke) => _stroke = stroke;
    public string Description => "Draw stroke";
    public void Do(NotePage page) => page.Strokes.Add(_stroke);
    public void Undo(NotePage page) => page.Strokes.Remove(_stroke);
}

public class AddStrokesAction : IPageAction
{
    private readonly List<PenStroke> _strokes;
    public AddStrokesAction(List<PenStroke> strokes) => _strokes = strokes;
    public string Description => "Paste";
    public void Do(NotePage page) => page.Strokes.AddRange(_strokes);
    public void Undo(NotePage page) { foreach (var s in _strokes) page.Strokes.Remove(s); }
}

public class RemoveStrokesAction : IPageAction
{
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
}

public class ReplaceStrokesAction : IPageAction
{
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
}

public class MoveStrokesAction : IPageAction
{
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
}

public class InsertSpaceAction : IPageAction
{
    private readonly double _y;
    private readonly double _delta;
    private List<PenStroke>? _strokes;
    private List<TextElement>? _texts;
    public InsertSpaceAction(double y, double delta) { _y = y; _delta = delta; }
    public string Description => _delta >= 0 ? "Insert space" : "Remove space";
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
    private readonly ShapeElement _shape;
    public AddShapeAction(ShapeElement shape) => _shape = shape;
    public string Description => "Insert shape";
    public void Do(NotePage page) => page.Shapes.Add(_shape);
    public void Undo(NotePage page) => page.Shapes.Remove(_shape);
}

public class RemoveShapesAction : IPageAction
{
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
}

public class MoveResizeShapeAction : IPageAction
{
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
}

public class RotateShapeAction : IPageAction
{
    private readonly ShapeElement _s;
    private readonly double _from, _to;
    public RotateShapeAction(ShapeElement s, double from, double to) { _s = s; _from = from; _to = to; }
    public string Description => "Rotate";
    public void Do(NotePage page) => _s.Rotation = _to;
    public void Undo(NotePage page) => _s.Rotation = _from;
}

public class AddTextAction : IPageAction
{
    private readonly TextElement _text;
    public TextElement Text => _text;
    public AddTextAction(TextElement text) => _text = text;
    public string Description => "Add text box";
    public void Do(NotePage page) => page.Texts.Add(_text);
    public void Undo(NotePage page) => page.Texts.Remove(_text);
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
    public void Do(NotePage page) { foreach (var a in _actions) a.Do(page); }
    public void Undo(NotePage page) { for (int i = _actions.Count - 1; i >= 0; i--) _actions[i].Undo(page); }
}

// Moves a set of shapes by a delta (used in mixed-selection moves).
public class MoveShapesAction : IPageAction
{
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

public class UndoRedoManager
{
    private readonly Stack<IPageAction> _undo = new();
    private readonly Stack<IPageAction> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

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
