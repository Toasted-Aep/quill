using LectureInk.Models;

namespace LectureInk.Services;

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
