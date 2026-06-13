using System.Numerics;
using LectureInk.Helpers;
using LectureInk.Models;
using LectureInk.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace LectureInk.Controls;

/// <summary>
/// Infinite pen-first drawing surface. The canvas fills the viewport and all
/// content lives in "world" coordinates mapped through a pan/zoom transform
/// (screen = world * zoom + offset), so pages extend forever in every
/// direction. Pen input always draws; touch pans/pinches (unless touch-draw
/// is on); mouse pans with the wheel or middle-drag.
/// </summary>
public sealed class InkSurface : UserControl
{
    private readonly CanvasControl _canvas;
    private readonly Canvas _textLayer;
    private readonly CompositeTransform _textTransform = new();
    private NotePage? _page;

    // ---- view transform ---------------------------------------------------
    public Vector2 ViewOffset { get; private set; } = Vector2.Zero;
    public float ViewZoom { get; private set; } = 1f;
    public event Action? ViewChanged;

    // ---- tool state -------------------------------------------------------
    public ToolType Tool { get; private set; } = ToolType.Pen;
    public PenType Pen { get; set; } = PenType.Standard;
    public Color PenColor { get; set; } = Color.FromArgb(255, 20, 20, 19);
    public float PenSize { get; set; } = 3.5f;
    public float PenSensitivity { get; set; } = 1f;
    public EraserMode EraserMode { get; set; } = EraserMode.Point;
    public bool RulerMode { get; set; }
    public bool HandDrawMode { get; set; }

    public UndoRedoManager UndoManager { get; } = new();
    public NotePage? Page => _page;
    public RichEditBox? ActiveTextBox { get; private set; }

    public event Action? ContentChanged;
    public event Action<RichEditBox?>? ActiveTextChanged;
    public event Action? ReplayEnded;
    public event Action? TitleClicked;
    public event Action? DateClicked;

    // ---- gesture state ----------------------------------------------------
    private uint? _activePointer;
    private ToolType? _gestureTool;
    private Vector2? _hover;

    private bool _mousePanning;
    private Vector2 _mousePanLast;
    private bool _touchPanActive;

    private List<StrokePoint>? _wet;
    private Vector2 _wetStart, _wetEnd;

    private Vector2 _eraseLast;
    private EraserMode _gestureEraserMode;
    private List<(int Index, PenStroke Stroke)> _eraseRemoved = new();
    private HashSet<PenStroke> _gestureFragments = new();

    private List<Vector2>? _lasso;
    private readonly List<PenStroke> _selected = new();
    private readonly HashSet<PenStroke> _selectedSet = new();
    private Rect _selBounds = Rect.Empty;
    private bool _movingSel;
    private Vector2 _moveStart;
    private float _moveDx, _moveDy;

    private bool _spacing;
    private double _spaceY, _spaceDelta, _spaceStartY;

    private bool _replaying;
    private int _replayStroke, _replayPoint;
    private readonly DispatcherTimer _replayTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    public bool IsReplaying => _replaying;

    // ---- shapes ----
    private ShapeElement? _activeShape;
    private bool _shapeAdjust;
    private ShapeElement? _adjustShape;
    private Vector2 _adjustAnchor;
    private bool _adjustConstrain;
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private Vector2 _stablePos;
    private long _lastMoveMs;
    private bool _movingShape, _resizingShape;
    private double _resizeAspect;
    private Vector2 _shapeStart, _resizeAnchor;
    private (double X, double Y, double W, double H) _shapeOrig;
    private List<(int Index, ShapeElement Shape)> _eraseRemovedShapes = new();
    private readonly CanvasTextFormat _labelFormat = new() { FontSize = 15 };

    private bool _rectSelect;
    private Vector2 _rectStart, _rectCur;

    private readonly Dictionary<string, CanvasBitmap?> _bitmaps = new();
    private readonly HashSet<string> _bitmapLoading = new();

    private const float OriginMargin = 48f;
    private readonly CanvasTextFormat _titleFormat = new()
    {
        FontFamily = "Poppins",
        FontSize = 30,
        FontWeight = new Windows.UI.Text.FontWeight { Weight = 600 }
    };
    private readonly CanvasTextFormat _subtitleFormat = new()
    {
        FontFamily = "Poppins",
        FontSize = 12.5f
    };

    private readonly CanvasStrokeStyle _roundStyle = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round
    };
    private readonly CanvasStrokeStyle _dashStyle = new()
    {
        DashStyle = CanvasDashStyle.Dash,
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round
    };

    private readonly Dictionary<Guid, (Grid Container, RichEditBox Box)> _textUi = new();

    public InkSurface()
    {
        _canvas = new CanvasControl();
        _canvas.Draw += OnDraw;
        _textLayer = new Canvas { Background = null, RenderTransform = _textTransform };

        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_textLayer);
        Content = root;

        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerCanceled += OnPointerLost;
        _canvas.PointerCaptureLost += OnPointerLost;
        _canvas.PointerWheelChanged += OnPointerWheel;
        _canvas.PointerExited += (_, _) => { _hover = null; _canvas.Invalidate(); };

        // Touch panning / pinch zoom handled by us (no ScrollViewer anywhere,
        // so nothing can steal the pen mid-stroke).
        _canvas.ManipulationMode =
            ManipulationModes.TranslateX | ManipulationModes.TranslateY |
            ManipulationModes.Scale |
            ManipulationModes.TranslateInertia | ManipulationModes.ScaleInertia;
        _canvas.ManipulationStarted += OnManipStarted;
        _canvas.ManipulationDelta += OnManipDelta;

        _replayTimer.Tick += ReplayTick;
        _holdTimer.Tick += HoldTick;

        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    // =======================================================================
    // View transform
    // =======================================================================
    private Vector2 ToWorld(Vector2 screen) => (screen - ViewOffset) / ViewZoom;
    private Vector2 ToWorld(Point screen) => ToWorld(new Vector2((float)screen.X, (float)screen.Y));

    private void ZoomAround(Vector2 screenPivot, float newZoom)
    {
        newZoom = Math.Clamp(newZoom, 0.1f, 8f);
        var world = ToWorld(screenPivot);
        ViewZoom = newZoom;
        ViewOffset = screenPivot - world * newZoom;
        OnViewChanged();
    }

    public void SetViewZoom(float zoom) =>
        ZoomAround(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2), zoom);

    public void ZoomBy(float factor) => SetViewZoom(ViewZoom * factor);

    public void ResetView()
    {
        ViewZoom = 1f;
        ViewOffset = Vector2.Zero;
        OnViewChanged();
    }

    private void PanBy(Vector2 screenDelta)
    {
        ViewOffset += screenDelta;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        ViewOffset = new Vector2(
            MathF.Min(ViewOffset.X, OriginMargin),
            MathF.Min(ViewOffset.Y, OriginMargin));
        if (_page != null)
        {
            _page.ViewX = ViewOffset.X;
            _page.ViewY = ViewOffset.Y;
            _page.ViewZoom = ViewZoom;
        }
        _textTransform.ScaleX = ViewZoom;
        _textTransform.ScaleY = ViewZoom;
        _textTransform.TranslateX = ViewOffset.X;
        _textTransform.TranslateY = ViewOffset.Y;
        _canvas.Invalidate();
        ViewChanged?.Invoke();
    }

    private void OnManipStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _touchPanActive =
            e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            _gestureTool == null && !_replaying;
    }

    private void OnManipDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_touchPanActive || _gestureTool != null) return;
        if (Math.Abs(e.Delta.Scale - 1f) > 0.001f)
        {
            var pivot = new Vector2((float)e.Position.X, (float)e.Position.Y);
            ZoomAround(pivot, ViewZoom * e.Delta.Scale);
        }
        PanBy(new Vector2((float)e.Delta.Translation.X, (float)e.Delta.Translation.Y));
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var pp = e.GetCurrentPoint(_canvas);
        int wheel = pp.Properties.MouseWheelDelta;
        var mods = e.KeyModifiers;
        if (pp.Properties.IsHorizontalMouseWheel)
        {
            // trackpad two-finger horizontal swipe
            PanBy(new Vector2(-wheel * 0.5f, 0));
            e.Handled = true;
            return;
        }
        if (mods.HasFlag(VirtualKeyModifiers.Control))
        {
            float factor = wheel > 0 ? 1.1f : 1f / 1.1f;
            ZoomAround(new Vector2((float)pp.Position.X, (float)pp.Position.Y), ViewZoom * factor);
        }
        else if (mods.HasFlag(VirtualKeyModifiers.Shift))
        {
            PanBy(new Vector2(wheel * 0.5f, 0));
        }
        else
        {
            PanBy(new Vector2(0, wheel * 0.5f));
        }
        e.Handled = true;
    }

    // =======================================================================
    // Page lifecycle
    // =======================================================================
    public void LoadPage(NotePage page)
    {
        StopReplay();
        NormalizeContent(page);
        _page = page;
        UndoManager.Clear();
        ClearSelection();
        _activeShape = null;
        ViewOffset = new Vector2((float)page.ViewX, (float)page.ViewY);
        ViewZoom = Math.Clamp((float)page.ViewZoom, 0.1f, 8f);
        RebuildTextLayer();
        OnViewChanged();
    }

    /// <summary>
    /// One-time migration: content drawn above/left of the origin (before the
    /// title-bar clamp existed) is shifted into the valid region so it stays
    /// reachable.
    /// </summary>
    private static void NormalizeContent(NotePage page)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var st in page.Strokes)
            foreach (var p in st.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
            }
        foreach (var t in page.Texts)
        {
            if (t.X < minX) minX = t.X;
            if (t.Y < minY) minY = t.Y;
        }
        foreach (var sh in page.Shapes)
        {
            double sx = Math.Min(sh.X, sh.X + sh.W), sy = Math.Min(sh.Y, sh.Y + sh.H);
            if (sx < minX) minX = sx;
            if (sy < minY) minY = sy;
        }
        if (minX == double.MaxValue) return;

        double dx = minX < -10 ? 44 - minX : 0;
        double dy = minY < -10 ? 104 - minY : 0;
        if (dx == 0 && dy == 0) return;

        foreach (var st in page.Strokes)
            foreach (var p in st.Points)
            {
                p.X += (float)dx;
                p.Y += (float)dy;
            }
        foreach (var t in page.Texts)
        {
            t.X += dx;
            t.Y += dy;
        }
        foreach (var sh in page.Shapes)
        {
            sh.X += dx;
            sh.Y += dy;
        }
        page.ViewX -= dx * page.ViewZoom;
        page.ViewY -= dy * page.ViewZoom;
    }

    public void SetTool(ToolType tool)
    {
        Tool = tool;
        if (tool != ToolType.Select)
        {
            ClearSelection();
            _activeShape = null;
        }
        _textLayer.IsHitTestVisible = tool is ToolType.Text or ToolType.Select;
        _canvas.Invalidate();
    }

    public void Refresh() => _canvas.Invalidate();

    public void FlushTexts()
    {
        if (_page == null) return;
        foreach (var (id, ui) in _textUi)
        {
            var model = _page.Texts.FirstOrDefault(t => t.Id == id);
            if (model == null) continue;
            ui.Box.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            model.Rtf = rtf;
        }
    }

    // =======================================================================
    // Undo / redo
    // =======================================================================
    public void Undo()
    {
        if (_page == null || _replaying) return;
        FlushTexts();
        UndoManager.Undo(_page);
        ClearSelection();
        RebuildTextLayer();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void Redo()
    {
        if (_page == null || _replaying) return;
        FlushTexts();
        UndoManager.Redo(_page);
        ClearSelection();
        RebuildTextLayer();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // Selection
    // =======================================================================
    public void ClearSelection()
    {
        _selected.Clear();
        _selectedSet.Clear();
        _selBounds = Rect.Empty;
        _movingSel = false;
        _moveDx = _moveDy = 0;
        _canvas.Invalidate();
    }

    public bool HasSelection => _selected.Count > 0;

    public void DeleteSelection()
    {
        if (_page == null) return;
        if (_activeShape != null)
        {
            int idx = _page.Shapes.IndexOf(_activeShape);
            UndoManager.Push(new RemoveShapesAction(new List<(int, ShapeElement)> { (idx, _activeShape) }), _page);
            _activeShape = null;
            _canvas.Invalidate();
            ContentChanged?.Invoke();
            return;
        }
        if (_selected.Count == 0) return;
        var pairs = _selected.Select(s => (_page.Strokes.IndexOf(s), s)).ToList();
        UndoManager.Push(new RemoveStrokesAction(pairs, "Delete selection"), _page);
        ClearSelection();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // Replay
    // =======================================================================
    public void StartReplay()
    {
        if (_page == null || _page.Strokes.Count == 0) return;
        ClearSelection();
        _replaying = true;
        _replayStroke = 0;
        _replayPoint = 0;
        _textLayer.Visibility = Visibility.Collapsed;
        _replayTimer.Start();
        _canvas.Invalidate();
    }

    public void StopReplay()
    {
        if (!_replaying) return;
        _replayTimer.Stop();
        _replaying = false;
        _textLayer.Visibility = Visibility.Visible;
        _canvas.Invalidate();
        ReplayEnded?.Invoke();
    }

    private void ReplayTick(object? sender, object e)
    {
        if (_page == null || _replayStroke >= _page.Strokes.Count)
        {
            StopReplay();
            return;
        }
        var cur = _page.Strokes[_replayStroke];
        _replayPoint += Math.Max(2, cur.Points.Count / 30);
        if (_replayPoint >= cur.Points.Count)
        {
            _replayStroke++;
            _replayPoint = 0;
        }
        _canvas.Invalidate();
    }

    // =======================================================================
    // Pointer input
    // =======================================================================
    private float EraserRadius => Math.Max(8f, PenSize * 2.2f);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        var pp = e.GetCurrentPoint(_canvas);
        var props = pp.Properties;
        var device = e.Pointer.PointerDeviceType;
        bool isPen = device == Microsoft.UI.Input.PointerDeviceType.Pen;
        bool isMouse = device == Microsoft.UI.Input.PointerDeviceType.Mouse;
        var screen = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
        var pos = ToWorld(screen);

        // middle-mouse drag pans
        if (isMouse && props.IsMiddleButtonPressed)
        {
            _mousePanning = true;
            _mousePanLast = screen;
            _activePointer = e.Pointer.PointerId;
            _canvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }
        if (isMouse && !props.IsLeftButtonPressed) return;

        var tool = Tool;
        // Pen hardware buttons: first button (eraser tip / inverted pen /
        // extra barrel buttons) -> eraser. Second (barrel) button -> lasso.
        if (props.IsEraser || props.IsInverted ||
            (isPen && (props.IsXButton1Pressed || props.IsXButton2Pressed || props.IsMiddleButtonPressed)))
        {
            tool = ToolType.Eraser;
        }
        else if (isPen && props.IsBarrelButtonPressed)
        {
            tool = ToolType.Select;
        }

        if (tool == ToolType.Pen && !isPen && !HandDrawMode)
        {
            if (!isMouse) return; // touch pans
            // mouse without the lasso button: rectangle selection
            _activePointer = e.Pointer.PointerId;
            _gestureTool = ToolType.Select;
            _canvas.CapturePointer(e.Pointer);
            if (_selected.Count > 0 && _selBounds.Contains(new Point(pos.X, pos.Y)))
            {
                _movingSel = true;
                _moveStart = pos;
                _moveDx = _moveDy = 0;
            }
            else
            {
                ClearSelection();
                _activeShape = null;
                _rectSelect = true;
                _rectStart = pos;
                _rectCur = pos;
            }
            e.Handled = true;
            _canvas.Invalidate();
            return;
        }

        if (tool == ToolType.Text)
        {
            CreateTextElementAt(new Point(pos.X, pos.Y));
            e.Handled = true;
            return;
        }

        _activePointer = e.Pointer.PointerId;
        _gestureTool = tool;
        _canvas.CapturePointer(e.Pointer);

        switch (tool)
        {
            case ToolType.Pen:
                _wet = new List<StrokePoint> { new(pos.X, pos.Y, props.Pressure) };
                _wetStart = pos;
                _wetEnd = pos;
                _stablePos = pos;
                _lastMoveMs = Environment.TickCount64;
                _shapeAdjust = false;
                _adjustShape = null;
                _holdTimer.Start();
                break;

            case ToolType.Eraser:
                _gestureEraserMode = EraserMode;
                _eraseRemoved = new List<(int, PenStroke)>();
                _eraseRemovedShapes = new List<(int, ShapeElement)>();
                _gestureFragments = new HashSet<PenStroke>();
                _eraseLast = pos;
                EraseAt(pos, pos);
                break;

            case ToolType.Select:
            {
                float tol = 10f / ViewZoom;
                if (_activeShape != null)
                {
                    var handle = HitHandle(_activeShape, pos, tol);
                    if (handle != null)
                    {
                        _resizingShape = true;
                        _resizeAnchor = handle.Value;
                        _shapeOrig = Snapshot(_activeShape);
                        _adjustConstrain = false;
                        _resizeAspect = _activeShape.Kind == ShapeKind.Image && Math.Abs(_shapeOrig.H) > 1
                            ? Math.Abs(_shapeOrig.W) / Math.Abs(_shapeOrig.H)
                            : 0;
                        break;
                    }
                    if (ShapeBounds(_activeShape).Contains(new Point(pos.X, pos.Y)) ||
                        DistToShapeOutline(_activeShape, pos) < tol)
                    {
                        _movingShape = true;
                        _shapeStart = pos;
                        _shapeOrig = Snapshot(_activeShape);
                        break;
                    }
                }
                var hitShape = HitShape(pos, tol);
                if (hitShape != null)
                {
                    _activeShape = hitShape;
                    ClearSelection();
                    _movingShape = true;
                    _shapeStart = pos;
                    _shapeOrig = Snapshot(hitShape);
                    break;
                }
                _activeShape = null;
                if (_selected.Count > 0 && _selBounds.Contains(new Point(pos.X, pos.Y)))
                {
                    _movingSel = true;
                    _moveStart = pos;
                    _moveDx = _moveDy = 0;
                }
                else
                {
                    ClearSelection();
                    _lasso = new List<Vector2> { pos };
                }
                break;
            }

            case ToolType.FreeSpace:
                _spacing = true;
                _spaceY = pos.Y;
                _spaceStartY = pos.Y;
                _spaceDelta = 0;
                break;
        }

        e.Handled = true;
        _canvas.Invalidate();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_page == null) return;
        var pp = e.GetCurrentPoint(_canvas);
        var screen = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
        _hover = ToWorld(screen);

        if (_activePointer == null || e.Pointer.PointerId != _activePointer)
        {
            if (Tool == ToolType.Eraser) _canvas.Invalidate();
            return;
        }

        if (_mousePanning)
        {
            PanBy(screen - _mousePanLast);
            _mousePanLast = screen;
            e.Handled = true;
            return;
        }

        var pos = ToWorld(screen);
        switch (_gestureTool)
        {
            case ToolType.Pen:
                if (_shapeAdjust && _adjustShape != null)
                {
                    ResizeShape(_adjustShape, _adjustAnchor, pos, _adjustConstrain);
                    break;
                }
                _wetEnd = pos;
                if (Vector2.Distance(pos, _stablePos) > 3.5f / ViewZoom)
                {
                    _stablePos = pos;
                    _lastMoveMs = Environment.TickCount64;
                }
                if (!RulerMode)
                {
                    var pts = e.GetIntermediatePoints(_canvas);
                    for (int i = pts.Count - 1; i >= 0; i--)
                    {
                        var ip = pts[i];
                        var v = ToWorld(ip.Position);
                        var last = _wet![^1];
                        float minGap = 0.7f / ViewZoom;
                        if (Math.Abs(v.X - last.X) + Math.Abs(v.Y - last.Y) < minGap) continue;
                        _wet.Add(new StrokePoint(v.X, v.Y, ip.Properties.Pressure));
                    }
                }
                break;

            case ToolType.Eraser:
                var ptsE = e.GetIntermediatePoints(_canvas);
                for (int i = ptsE.Count - 1; i >= 0; i--)
                {
                    var v = ToWorld(ptsE[i].Position);
                    EraseAt(_eraseLast, v);
                    _eraseLast = v;
                }
                break;

            case ToolType.Select:
                if (_rectSelect)
                {
                    _rectCur = pos;
                    break;
                }
                if (_resizingShape && _activeShape != null)
                {
                    ResizeShape(_activeShape, _resizeAnchor, pos, false, _resizeAspect);
                }
                else if (_movingShape && _activeShape != null)
                {
                    _activeShape.X = _shapeOrig.X + (pos.X - _shapeStart.X);
                    _activeShape.Y = _shapeOrig.Y + (pos.Y - _shapeStart.Y);
                }
                else if (_movingSel)
                {
                    _moveDx = pos.X - _moveStart.X;
                    _moveDy = pos.Y - _moveStart.Y;
                }
                else
                {
                    _lasso?.Add(pos);
                }
                break;

            case ToolType.FreeSpace:
                _spaceDelta = pos.Y - _spaceStartY;
                break;
        }

        e.Handled = true;
        _canvas.Invalidate();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointer == null || e.Pointer.PointerId != _activePointer) return;
        CommitGesture();
        _canvas.ReleasePointerCaptures();
        e.Handled = true;
    }

    private void OnPointerLost(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointer == null || e.Pointer.PointerId != _activePointer) return;
        CommitGesture();
    }

    private void CommitGesture()
    {
        if (_mousePanning)
        {
            _mousePanning = false;
            _activePointer = null;
            return;
        }
        if (_page == null)
        {
            ResetGesture();
            return;
        }
        bool changed = false;

        switch (_gestureTool)
        {
            case ToolType.Pen:
            {
                if (_shapeAdjust && _adjustShape != null)
                {
                    var sh = _adjustShape;
                    bool big = sh.Kind == ShapeKind.Line
                        ? Math.Abs(sh.W) + Math.Abs(sh.H) > 10
                        : sh.W > 8 && sh.H > 8;
                    if (big)
                    {
                        UndoManager.Push(new AddShapeAction(sh), _page);
                        changed = true;
                    }
                    break;
                }
                var pts = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : (_wet ?? new List<StrokePoint>());
                if (pts.Count >= 1)
                {
                    var stroke = new PenStroke
                    {
                        Pen = Pen,
                        Color = ColorUtil.ToHex(PenColor),
                        Size = PenSize,
                        Sens = PenSensitivity,
                        Points = pts
                    };
                    UndoManager.Push(new AddStrokeAction(stroke), _page);
                    changed = true;
                }
                break;
            }
            case ToolType.Eraser:
            {
                if (_eraseRemoved.Count > 0)
                {
                    if (_gestureEraserMode == EraserMode.Object)
                        UndoManager.Push(new RemoveStrokesAction(_eraseRemoved), _page, alreadyDone: true);
                    else
                    {
                        var added = _gestureFragments.Where(f => _page.Strokes.Contains(f)).ToList();
                        UndoManager.Push(new ReplaceStrokesAction(_eraseRemoved, added), _page, alreadyDone: true);
                    }
                    changed = true;
                }
                if (_eraseRemovedShapes.Count > 0)
                {
                    UndoManager.Push(new RemoveShapesAction(_eraseRemovedShapes), _page, alreadyDone: true);
                    _eraseRemovedShapes = new List<(int, ShapeElement)>();
                    changed = true;
                }
                break;
            }
            case ToolType.Select:
            {
                if (_rectSelect)
                {
                    _rectSelect = false;
                    float x1 = Math.Min(_rectStart.X, _rectCur.X), x2 = Math.Max(_rectStart.X, _rectCur.X);
                    float y1 = Math.Min(_rectStart.Y, _rectCur.Y), y2 = Math.Max(_rectStart.Y, _rectCur.Y);
                    if (x2 - x1 > 6 && y2 - y1 > 6)
                    {
                        SelectWithLasso(new List<Vector2>
                        {
                            new(x1, y1), new(x2, y1), new(x2, y2), new(x1, y2)
                        });
                    }
                    else
                    {
                        // a plain mouse click: title/date first, then text boxes
                        var cp = _rectStart;
                        if (cp.X >= 38 && cp.X <= 470 && cp.Y >= 12 && cp.Y <= 58)
                        {
                            TitleClicked?.Invoke();
                        }
                        else if (cp.X >= 38 && cp.X <= 470 && cp.Y > 58 && cp.Y <= 92)
                        {
                            DateClicked?.Invoke();
                        }
                        else if (!FocusTextAt(cp))
                        {
                            CreateTextElementAt(new Point(cp.X, cp.Y));
                        }
                    }
                    break;
                }
                if ((_resizingShape || _movingShape) && _activeShape != null)
                {
                    var now = Snapshot(_activeShape);
                    if (Math.Abs(now.X - _shapeOrig.X) > 0.5 || Math.Abs(now.Y - _shapeOrig.Y) > 0.5 ||
                        Math.Abs(now.W - _shapeOrig.W) > 0.5 || Math.Abs(now.H - _shapeOrig.H) > 0.5)
                    {
                        UndoManager.Push(new MoveResizeShapeAction(_activeShape, _shapeOrig, now), _page, alreadyDone: true);
                        changed = true;
                    }
                    _movingShape = _resizingShape = false;
                    break;
                }
                if (_movingSel)
                {
                    if (Math.Abs(_moveDx) > 0.5f || Math.Abs(_moveDy) > 0.5f)
                    {
                        UndoManager.Push(new MoveStrokesAction(_selected.ToList(), _moveDx, _moveDy), _page);
                        changed = true;
                    }
                    _movingSel = false;
                    _moveDx = _moveDy = 0;
                    RecomputeSelectionBounds();
                }
                else if (_lasso is { Count: > 2 })
                {
                    SelectWithLasso(_lasso);
                }
                _lasso = null;
                break;
            }
            case ToolType.FreeSpace:
            {
                if (Math.Abs(_spaceDelta) > 2)
                {
                    UndoManager.Push(new InsertSpaceAction(_spaceY, _spaceDelta), _page);
                    RebuildTextLayer();
                    changed = true;
                }
                _spacing = false;
                _spaceDelta = 0;
                break;
            }
        }

        ResetGesture();
        _canvas.Invalidate();
        if (changed) ContentChanged?.Invoke();
    }

    private void ResetGesture()
    {
        _activePointer = null;
        _gestureTool = null;
        _wet = null;
        _spacing = false;
        _mousePanning = false;
        _shapeAdjust = false;
        _adjustShape = null;
        _movingShape = _resizingShape = false;
        _rectSelect = false;
        _holdTimer.Stop();
    }

    private static List<StrokePoint> BuildRulerPoints(Vector2 start, Vector2 end)
    {
        var d = end - start;
        if (d.Length() < 2) return new List<StrokePoint> { new(start.X, start.Y, 0.5f) };
        double ang = Math.Atan2(d.Y, d.X);
        double step = Math.PI / 12;
        double snapped = Math.Round(ang / step) * step;
        var dir = new Vector2((float)Math.Cos(snapped), (float)Math.Sin(snapped));
        float len = Vector2.Dot(d, dir);
        var pts = new List<StrokePoint>();
        int n = Math.Max(2, (int)(Math.Abs(len) / 4));
        for (int i = 0; i <= n; i++)
        {
            var p = start + dir * (len * i / n);
            pts.Add(new StrokePoint(p.X, p.Y, 0.5f));
        }
        return pts;
    }

    // =======================================================================
    // Erasing
    // =======================================================================
    private void EraseAt(Vector2 from, Vector2 to)
    {
        if (_page == null) return;
        // shapes are erased whole in either mode
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var sh = _page.Shapes[i];
            float tolS = sh.Size + 8f;
            if (DistToShapeOutline(sh, from) <= tolS || DistToShapeOutline(sh, to) <= tolS)
            {
                _eraseRemovedShapes.Add((i, sh));
                _page.Shapes.RemoveAt(i);
                if (_activeShape == sh) _activeShape = null;
            }
        }
        if (_gestureEraserMode == EraserMode.Object)
        {
            for (int i = _page.Strokes.Count - 1; i >= 0; i--)
            {
                var s = _page.Strokes[i];
                float tol = s.Size + 6f;
                bool hit = s.Points.Any(p =>
                    GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) <= tol);
                if (hit)
                {
                    _eraseRemoved.Add((i, s));
                    _page.Strokes.RemoveAt(i);
                }
            }
        }
        else
        {
            float r = EraserRadius;
            var snapshot = _page.Strokes.ToList();
            foreach (var s in snapshot)
            {
                bool any = s.Points.Any(p =>
                    GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) <= r);
                if (!any) continue;

                int index = _page.Strokes.IndexOf(s);
                if (index < 0) continue;

                var runs = new List<List<StrokePoint>>();
                var cur = new List<StrokePoint>();
                foreach (var p in s.Points)
                {
                    if (GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) > r)
                        cur.Add(p);
                    else
                    {
                        if (cur.Count > 1) runs.Add(cur);
                        cur = new List<StrokePoint>();
                    }
                }
                if (cur.Count > 1) runs.Add(cur);

                _page.Strokes.RemoveAt(index);
                if (_gestureFragments.Contains(s)) _gestureFragments.Remove(s);
                else _eraseRemoved.Add((index, s));

                int insertAt = index;
                foreach (var run in runs)
                {
                    var frag = s.CloneWithPoints(run);
                    _page.Strokes.Insert(Math.Min(insertAt++, _page.Strokes.Count), frag);
                    _gestureFragments.Add(frag);
                }
            }
        }
    }

    // =======================================================================
    // Lasso selection
    // =======================================================================
    private void SelectWithLasso(List<Vector2> poly)
    {
        if (_page == null) return;
        _selected.Clear();
        _selectedSet.Clear();
        foreach (var s in _page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            int inside = s.Points.Count(p => GeometryUtil.PointInPolygon(new Vector2(p.X, p.Y), poly));
            if (inside > 0 && inside >= s.Points.Count * 0.4)
            {
                _selected.Add(s);
                _selectedSet.Add(s);
            }
        }
        RecomputeSelectionBounds();
    }

    private void RecomputeSelectionBounds()
    {
        if (_selected.Count == 0)
        {
            _selBounds = Rect.Empty;
            return;
        }
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var s in _selected)
            foreach (var p in s.Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
        _selBounds = new Rect(minX - 8, minY - 8, maxX - minX + 16, maxY - minY + 16);
    }

    // =======================================================================
    // Drawing
    // =======================================================================
    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_page == null)
        {
            ds.Clear(Colors.Transparent);
            return;
        }

        var bg = ColorUtil.Parse(_page.Background);
        ds.Clear(bg);

        ds.Transform = Matrix3x2.CreateScale(ViewZoom) *
                       Matrix3x2.CreateTranslation(ViewOffset.X, ViewOffset.Y);

        DrawGrid(ds, bg);
        DrawPageTitle(ds, bg);

        foreach (var sh in _page.Shapes)
            DrawShape(ds, sh);

        int idx = 0;
        foreach (var s in _page.Strokes)
        {
            var off = Vector2.Zero;
            if (_movingSel && _selectedSet.Contains(s)) off = new Vector2(_moveDx, _moveDy);
            else if (_spacing && s.Points.Count > 0 && s.MinY >= _spaceY) off = new Vector2(0, (float)_spaceDelta);

            if (_replaying)
            {
                if (idx > _replayStroke) break;
                int? limit = idx == _replayStroke ? _replayPoint : null;
                DrawStroke(ds, sender, s, off, limit);
            }
            else
            {
                DrawStroke(ds, sender, s, off, null);
            }
            idx++;
        }

        if (_gestureTool == ToolType.Pen)
        {
            var temp = new PenStroke
            {
                Pen = Pen,
                Color = ColorUtil.ToHex(PenColor),
                Size = PenSize,
                Sens = PenSensitivity,
                Points = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : (_wet ?? new List<StrokePoint>())
            };
            DrawStroke(ds, sender, temp, Vector2.Zero, null);
        }

        var accent = Color.FromArgb(255, 217, 119, 87); // brand orange
        float uiScale = 1.5f / ViewZoom;

        if (_shapeAdjust && _adjustShape != null)
        {
            DrawShape(ds, _adjustShape);
            var bb = ShapeBounds(_adjustShape);
            ds.DrawRectangle(bb, accent, uiScale, _dashStyle);
        }

        if (Tool == ToolType.Select && _activeShape != null && !_replaying)
        {
            DrawShapeSelection(ds, _activeShape, accent, uiScale);
        }

        if (_lasso is { Count: > 1 })
        {
            for (int i = 1; i < _lasso.Count; i++)
                ds.DrawLine(_lasso[i - 1], _lasso[i], accent, uiScale, _dashStyle);
        }

        if (_rectSelect)
        {
            var r = new Rect(
                Math.Min(_rectStart.X, _rectCur.X), Math.Min(_rectStart.Y, _rectCur.Y),
                Math.Abs(_rectCur.X - _rectStart.X), Math.Abs(_rectCur.Y - _rectStart.Y));
            ds.FillRectangle(r, Color.FromArgb(18, 217, 119, 87));
            ds.DrawRectangle(r, accent, uiScale, _dashStyle);
        }

        if (_selected.Count > 0 && !_selBounds.IsEmpty)
        {
            var r = new Rect(_selBounds.X + _moveDx, _selBounds.Y + _moveDy, _selBounds.Width, _selBounds.Height);
            ds.FillRectangle(r, Color.FromArgb(26, 217, 119, 87));
            ds.DrawRectangle(r, accent, uiScale, _dashStyle);
        }

        if (Tool == ToolType.Eraser && _hover.HasValue && !_replaying)
        {
            var ring = ColorUtil.IsDark(bg) ? Colors.White : Color.FromArgb(255, 70, 70, 70);
            ds.DrawCircle(_hover.Value, EraserRadius, ring, uiScale, _dashStyle);
        }

        if (_spacing)
        {
            var tl = ToWorld(new Vector2(0, 0));
            var br = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
            ds.DrawLine(new Vector2(tl.X, (float)_spaceY), new Vector2(br.X, (float)_spaceY), accent, uiScale, _dashStyle);
            float y2 = (float)(_spaceY + _spaceDelta);
            ds.DrawLine(new Vector2(tl.X, y2), new Vector2(br.X, y2), accent, uiScale, _dashStyle);
        }
    }

    private void DrawGrid(CanvasDrawingSession ds, Color bg)
    {
        if (_page == null || _page.Grid == GridType.None) return;
        float spacing = (float)Math.Max(8, _page.GridSpacing);

        var tl = ToWorld(new Vector2(0, 0));
        var br = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));

        // keep the cell count sane when zoomed far out
        while ((br.X - tl.X) / spacing * ((br.Y - tl.Y) / spacing) > 25000) spacing *= 2;

        float startX = MathF.Floor(tl.X / spacing) * spacing;
        float startY = MathF.Floor(tl.Y / spacing) * spacing;

        var gridColor = ColorUtil.IsDark(bg)
            ? Color.FromArgb(70, 255, 255, 255)
            : Color.FromArgb(46, 0, 0, 0);
        float lw = 1f / ViewZoom;

        switch (_page.Grid)
        {
            case GridType.Dotted:
                for (float y = startY; y < br.Y; y += spacing)
                    for (float x = startX; x < br.X; x += spacing)
                        ds.FillCircle(new Vector2(x, y), 1.4f, gridColor);
                break;
            case GridType.Square:
                for (float x = startX; x < br.X; x += spacing)
                    ds.DrawLine(new Vector2(x, tl.Y), new Vector2(x, br.Y), gridColor, lw);
                for (float y = startY; y < br.Y; y += spacing)
                    ds.DrawLine(new Vector2(tl.X, y), new Vector2(br.X, y), gridColor, lw);
                break;
            case GridType.Lines:
                for (float y = startY; y < br.Y; y += spacing)
                    ds.DrawLine(new Vector2(tl.X, y), new Vector2(br.X, y), gridColor, lw);
                break;
        }
    }

    private void DrawPageTitle(CanvasDrawingSession ds, Color bg)
    {
        if (_page == null) return;
        bool dark = ColorUtil.IsDark(bg);
        var ink = dark ? Color.FromArgb(255, 250, 249, 245) : Color.FromArgb(255, 20, 20, 19);
        var sub = dark ? Color.FromArgb(170, 250, 249, 245) : Color.FromArgb(170, 20, 20, 19);
        var hairline = dark ? Color.FromArgb(90, 250, 249, 245) : Color.FromArgb(70, 20, 20, 19);

        ds.DrawText(_page.Name, new Vector2(44, 22), ink, _titleFormat);
        var created = new DateTime(_page.CreatedTicks, DateTimeKind.Utc).ToLocalTime();
        ds.DrawText(created.ToString("dd MMMM yyyy") + "      " + created.ToString("HH:mm"),
            new Vector2(46, 64), sub, _subtitleFormat);
        ds.DrawLine(new Vector2(44, 88), new Vector2(460, 88), hairline, 1.2f);
    }

    private void DrawStroke(CanvasDrawingSession ds, ICanvasResourceCreator rc, PenStroke s, Vector2 offset, int? pointLimit)
    {
        var pts = s.Points;
        int n = Math.Min(pointLimit ?? pts.Count, pts.Count);
        if (n == 0) return;

        var color = ColorUtil.Parse(s.Color);

        if (s.Pen == PenType.Highlighter)
        {
            color.A = 110;
            float hw = s.Size * 2.4f;
            if (n == 1)
            {
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, hw / 2, color);
                return;
            }
            using var pb = new CanvasPathBuilder(rc);
            pb.BeginFigure(new Vector2(pts[0].X, pts[0].Y) + offset);
            for (int i = 1; i < n; i++)
                pb.AddLine(new Vector2(pts[i].X, pts[i].Y) + offset);
            pb.EndFigure(CanvasFigureLoop.Open);
            using var geo = CanvasGeometry.CreatePath(pb);
            ds.DrawGeometry(geo, color, hw, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Pencil) color.A = 150;
        else if (s.Pen == PenType.Marker) color.A = 235;

        if (n == 1)
        {
            float w0 = SegmentWidth(s, pts[0], pts[0], 0);
            ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, Math.Max(0.6f, w0 / 2), color);
            return;
        }

        for (int i = 1; i < n; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            float w = SegmentWidth(s, a, b, i);
            ds.DrawLine(
                new Vector2(a.X, a.Y) + offset,
                new Vector2(b.X, b.Y) + offset,
                color, w, _roundStyle);
        }
    }

    private static float SegmentWidth(PenStroke s, StrokePoint a, StrokePoint b, int index)
    {
        float pr = (a.Pressure + b.Pressure) * 0.5f;
        if (pr <= 0.01f) pr = 0.5f;
        float sens = s.Sens <= 0.01f ? 1f : s.Sens;
        switch (s.Pen)
        {
            case PenType.Pencil:
            {
                // graphite: light pressure response + grainy width jitter
                float jitter = 0.82f + 0.36f * (((uint)(index * 2654435761) % 1000) / 1000f);
                return Math.Max(0.4f, s.Size * (0.4f + 0.7f * sens * pr) * jitter);
            }
            case PenType.Marker:
            {
                // chisel tip: width from stroke direction, indifferent to pressure
                double angM = Math.Atan2(b.Y - a.Y, b.X - a.X);
                float nibM = (float)Math.Abs(Math.Sin(angM - 0.78));
                return Math.Max(1f, s.Size * (0.5f + 1.25f * nibM));
            }
            case PenType.Calligraphy:
            {
                // extreme nib contrast + pressure
                double angC = Math.Atan2(b.Y - a.Y, b.X - a.X);
                float nibC = (float)Math.Abs(Math.Sin(angC - 0.7));
                return Math.Max(0.35f, s.Size * (0.2f + 1.75f * nibC) * (0.4f + 0.95f * sens * pr));
            }
            case PenType.Standard:
                return Math.Max(0.5f, s.Size * (0.5f + 1.0f * sens * pr));
            case PenType.Brush:
                // very pressure-hungry: thin whisper -> fat daub
                return Math.Max(0.4f, s.Size * (0.12f + 3.2f * sens * pr * pr));
            case PenType.Fountain:
            {
                // nib angle shapes the line; pressure opens the tines wide
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                float nib = (float)Math.Abs(Math.Sin(ang - 0.7));
                float open = 0.18f + 2.6f * sens * MathF.Pow(pr, 1.4f);
                return Math.Max(0.4f, s.Size * (0.35f + 0.85f * nib) * open);
            }
            default:
                return s.Size;
        }
    }


    // =======================================================================
    // Shapes: recognition, hit-testing, drawing
    // =======================================================================
    private void HoldTick(object? sender, object e)
    {
        if (_gestureTool != ToolType.Pen || _shapeAdjust || RulerMode || _wet == null || _page == null) return;
        if (Environment.TickCount64 - _lastMoveMs < 620) return;

        var rec = RecognizeShape(_wet);
        if (rec == null)
        {
            _lastMoveMs = Environment.TickCount64; // don't retry every tick
            return;
        }
        _adjustShape = rec.Value.Shape;
        _adjustConstrain = rec.Value.Constrain;
        _adjustAnchor = FarthestAnchor(_adjustShape, _stablePos);
        _shapeAdjust = true;
        _wet = null;
        _canvas.Invalidate();
    }

    private (ShapeElement Shape, bool Constrain)? RecognizeShape(List<StrokePoint> pts)
    {
        if (pts.Count < 8) return null;
        float plen = 0;
        for (int i = 1; i < pts.Count; i++)
            plen += MathF.Sqrt((pts[i].X - pts[i - 1].X) * (pts[i].X - pts[i - 1].X) +
                               (pts[i].Y - pts[i - 1].Y) * (pts[i].Y - pts[i - 1].Y));
        if (plen < 40) return null;

        var a = new Vector2(pts[0].X, pts[0].Y);
        var b = new Vector2(pts[^1].X, pts[^1].Y);

        // ---- straight line ----
        float lineLen = Vector2.Distance(a, b);
        if (lineLen > 30)
        {
            float maxDev = 0;
            foreach (var p in pts)
                maxDev = Math.Max(maxDev, GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), a, b));
            if (maxDev < Math.Max(5f, lineLen * 0.05f))
            {
                var end = b;
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X) * 180 / Math.PI;
                double mod = Math.Abs(ang) % 90;
                if (mod < 6 || mod > 84) // snap near-horizontal/vertical
                {
                    if (Math.Abs(b.X - a.X) > Math.Abs(b.Y - a.Y)) end = new Vector2(b.X, a.Y);
                    else end = new Vector2(a.X, b.Y);
                }
                var line = MakeShape(ShapeKind.Line, a.X, a.Y, end.X - a.X, end.Y - a.Y);
                return (line, false);
            }
        }

        // ---- closed shapes ----
        if (Vector2.Distance(a, b) > Math.Max(25f, plen * 0.22f)) return null;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in pts)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        float w = maxX - minX, h = maxY - minY;
        if (w < 22 || h < 22) return null;
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        float rx = w / 2, ry = h / 2;

        double ellErr = 0, rectErr = 0;
        foreach (var p in pts)
        {
            float dx = (p.X - cx) / rx, dy = (p.Y - cy) / ry;
            ellErr += Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1);
            float d = Math.Min(
                Math.Min(Math.Abs(p.X - minX), Math.Abs(maxX - p.X)),
                Math.Min(Math.Abs(p.Y - minY), Math.Abs(maxY - p.Y)));
            rectErr += d;
        }
        ellErr /= pts.Count;
        rectErr = rectErr / pts.Count / (0.5 * Math.Min(w, h));

        bool nearEqual = Math.Abs(w - h) <= 0.16f * Math.Max(w, h);
        if (rectErr < 0.16 && rectErr * 1.15 < ellErr)
        {
            if (nearEqual) { float m = Math.Max(w, h); w = h = m; }
            return (MakeShape(ShapeKind.Rect, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        if (ellErr < 0.20)
        {
            if (nearEqual) { float m = Math.Max(w, h); w = h = m; }
            return (MakeShape(ShapeKind.Ellipse, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        return null;
    }

    private ShapeElement MakeShape(ShapeKind kind, double x, double y, double w, double h) => new()
    {
        Kind = kind,
        X = x, Y = y, W = w, H = h,
        Color = ColorUtil.ToHex(PenColor),
        Size = Math.Max(1.5f, PenSize)
    };

    private static void ResizeShape(ShapeElement s, Vector2 anchor, Vector2 pos, bool constrain, double aspect = 0)
    {
        if (s.Kind == ShapeKind.Line)
        {
            s.X = anchor.X; s.Y = anchor.Y;
            s.W = pos.X - anchor.X; s.H = pos.Y - anchor.Y;
            return;
        }
        double dx = pos.X - anchor.X, dy = pos.Y - anchor.Y;
        if (aspect > 0)
        {
            // images keep their aspect ratio
            double w = Math.Abs(dx);
            double h = w / aspect;
            dx = Math.Sign(dx == 0 ? 1 : dx) * w;
            dy = Math.Sign(dy == 0 ? 1 : dy) * h;
        }
        else if (constrain)
        {
            double m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = Math.Sign(dx == 0 ? 1 : dx) * m;
            dy = Math.Sign(dy == 0 ? 1 : dy) * m;
        }
        s.X = Math.Min(anchor.X, anchor.X + dx);
        s.Y = Math.Min(anchor.Y, anchor.Y + dy);
        s.W = Math.Max(4, Math.Abs(dx));
        s.H = Math.Max(4, Math.Abs(dy));
    }

    private static Vector2 FarthestAnchor(ShapeElement s, Vector2 pos)
    {
        if (s.Kind == ShapeKind.Line)
        {
            var p1 = new Vector2((float)s.X, (float)s.Y);
            var p2 = new Vector2((float)(s.X + s.W), (float)(s.Y + s.H));
            return Vector2.Distance(p1, pos) >= Vector2.Distance(p2, pos) ? p1 : p2;
        }
        var corners = ShapeCorners(s);
        Vector2 best = corners[0];
        float bd = -1;
        foreach (var c in corners)
        {
            float d = Vector2.Distance(c, pos);
            if (d > bd) { bd = d; best = c; }
        }
        return best;
    }

    private static (double X, double Y, double W, double H) Snapshot(ShapeElement s) => (s.X, s.Y, s.W, s.H);

    private static Vector2[] ShapeCorners(ShapeElement s)
    {
        if (s.Kind == ShapeKind.Line)
        {
            return new[]
            {
                new Vector2((float)s.X, (float)s.Y),
                new Vector2((float)(s.X + s.W), (float)(s.Y + s.H))
            };
        }
        return new[]
        {
            new Vector2((float)s.X, (float)s.Y),
            new Vector2((float)(s.X + s.W), (float)s.Y),
            new Vector2((float)s.X, (float)(s.Y + s.H)),
            new Vector2((float)(s.X + s.W), (float)(s.Y + s.H))
        };
    }

    private static Rect ShapeBounds(ShapeElement s)
    {
        double x = Math.Min(s.X, s.X + s.W), y = Math.Min(s.Y, s.Y + s.H);
        double w = Math.Abs(s.W), h = Math.Abs(s.H);
        return new Rect(x - 4, y - 4, w + 8, h + 8);
    }

    private static float DistToShapeOutline(ShapeElement s, Vector2 p)
    {
        switch (s.Kind)
        {
            case ShapeKind.Line:
                return GeometryUtil.DistToSegment(p,
                    new Vector2((float)s.X, (float)s.Y),
                    new Vector2((float)(s.X + s.W), (float)(s.Y + s.H)));
            case ShapeKind.Image:
            {
                var r = new Rect(Math.Min(s.X, s.X + s.W), Math.Min(s.Y, s.Y + s.H), Math.Abs(s.W), Math.Abs(s.H));
                if (r.Contains(new Point(p.X, p.Y))) return 0;
                var i1 = new Vector2((float)r.X, (float)r.Y);
                var i2 = new Vector2((float)(r.X + r.Width), (float)r.Y);
                var i3 = new Vector2((float)(r.X + r.Width), (float)(r.Y + r.Height));
                var i4 = new Vector2((float)r.X, (float)(r.Y + r.Height));
                return Math.Min(Math.Min(GeometryUtil.DistToSegment(p, i1, i2), GeometryUtil.DistToSegment(p, i2, i3)),
                                Math.Min(GeometryUtil.DistToSegment(p, i3, i4), GeometryUtil.DistToSegment(p, i4, i1)));
            }
            case ShapeKind.Ellipse:
            {
                float rx = (float)Math.Abs(s.W) / 2, ry = (float)Math.Abs(s.H) / 2;
                if (rx < 1 || ry < 1) return float.MaxValue;
                var c = new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2));
                var v = p - c;
                float val = MathF.Sqrt((v.X / rx) * (v.X / rx) + (v.Y / ry) * (v.Y / ry));
                return Math.Abs(val - 1) * Math.Min(rx, ry);
            }
            case ShapeKind.Triangle:
            {
                var t1 = new Vector2((float)(s.X + s.W / 2), (float)s.Y);
                var t2 = new Vector2((float)s.X, (float)(s.Y + s.H));
                var t3 = new Vector2((float)(s.X + s.W), (float)(s.Y + s.H));
                return Math.Min(GeometryUtil.DistToSegment(p, t1, t2),
                       Math.Min(GeometryUtil.DistToSegment(p, t2, t3),
                                GeometryUtil.DistToSegment(p, t3, t1)));
            }
            case ShapeKind.AxesXY:
            {
                var o = new Vector2((float)s.X, (float)(s.Y + s.H));
                return Math.Min(
                    GeometryUtil.DistToSegment(p, o, new Vector2((float)(s.X + s.W), o.Y)),
                    GeometryUtil.DistToSegment(p, o, new Vector2(o.X, (float)s.Y)));
            }
            case ShapeKind.AxesXYZ:
            {
                var o = new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2));
                float d1 = GeometryUtil.DistToSegment(p, o, new Vector2((float)(s.X + s.W), o.Y));
                float d2 = GeometryUtil.DistToSegment(p, o, new Vector2(o.X, (float)s.Y));
                float d3 = GeometryUtil.DistToSegment(p, o, new Vector2((float)s.X, (float)(s.Y + s.H)));
                return Math.Min(d1, Math.Min(d2, d3));
            }
            default: // Rect
            {
                var c1 = new Vector2((float)s.X, (float)s.Y);
                var c2 = new Vector2((float)(s.X + s.W), (float)s.Y);
                var c3 = new Vector2((float)(s.X + s.W), (float)(s.Y + s.H));
                var c4 = new Vector2((float)s.X, (float)(s.Y + s.H));
                return Math.Min(Math.Min(GeometryUtil.DistToSegment(p, c1, c2), GeometryUtil.DistToSegment(p, c2, c3)),
                                Math.Min(GeometryUtil.DistToSegment(p, c3, c4), GeometryUtil.DistToSegment(p, c4, c1)));
            }
        }
    }

    private ShapeElement? HitShape(Vector2 pos, float tol)
    {
        if (_page == null) return null;
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            if (DistToShapeOutline(_page.Shapes[i], pos) <= tol + _page.Shapes[i].Size)
                return _page.Shapes[i];
        }
        return null;
    }

    /// <summary>Returns the resize ANCHOR (opposite corner / other endpoint) if a handle was hit.</summary>
    private static Vector2? HitHandle(ShapeElement s, Vector2 pos, float tol)
    {
        var corners = ShapeCorners(s);
        if (s.Kind == ShapeKind.Line)
        {
            if (Vector2.Distance(corners[0], pos) <= tol) return corners[1];
            if (Vector2.Distance(corners[1], pos) <= tol) return corners[0];
            return null;
        }
        for (int i = 0; i < corners.Length; i++)
        {
            if (Vector2.Distance(corners[i], pos) <= tol)
                return corners[corners.Length - 1 - i]; // opposite corner
        }
        return null;
    }

    private void DrawShape(CanvasDrawingSession ds, ShapeElement s)
    {
        var color = ColorUtil.Parse(s.Color);
        float w = Math.Max(1f, s.Size);
        switch (s.Kind)
        {
            case ShapeKind.Line:
                ds.DrawLine((float)s.X, (float)s.Y, (float)(s.X + s.W), (float)(s.Y + s.H), color, w, _roundStyle);
                break;
            case ShapeKind.Rect:
                ds.DrawRectangle(new Rect(s.X, s.Y, Math.Max(1, s.W), Math.Max(1, s.H)), color, w);
                break;
            case ShapeKind.Ellipse:
                ds.DrawEllipse(
                    new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2)),
                    (float)Math.Max(1, s.W) / 2, (float)Math.Max(1, s.H) / 2, color, w);
                break;
            case ShapeKind.Triangle:
            {
                var t1 = new Vector2((float)(s.X + s.W / 2), (float)s.Y);
                var t2 = new Vector2((float)s.X, (float)(s.Y + s.H));
                var t3 = new Vector2((float)(s.X + s.W), (float)(s.Y + s.H));
                ds.DrawLine(t1, t2, color, w, _roundStyle);
                ds.DrawLine(t2, t3, color, w, _roundStyle);
                ds.DrawLine(t3, t1, color, w, _roundStyle);
                break;
            }
            case ShapeKind.Image:
            {
                var r = new Rect(s.X, s.Y, Math.Max(1, s.W), Math.Max(1, s.H));
                if (s.ImagePath != null && _bitmaps.TryGetValue(s.ImagePath, out var bmp) && bmp != null)
                {
                    ds.DrawImage(bmp, r);
                }
                else
                {
                    ds.DrawRectangle(r, Color.FromArgb(130, 128, 128, 128), 1.5f, _dashStyle);
                    if (s.ImagePath != null) RequestBitmap(s.ImagePath);
                }
                break;
            }
            case ShapeKind.AxesXY:
            {
                var o = new Vector2((float)s.X, (float)(s.Y + s.H));
                DrawArrow(ds, o, new Vector2((float)(s.X + s.W), o.Y), color, w);
                DrawArrow(ds, o, new Vector2(o.X, (float)s.Y), color, w);
                ds.DrawText("x", new Vector2((float)(s.X + s.W) - 4, o.Y + 6), color, _labelFormat);
                ds.DrawText("y", new Vector2(o.X - 18, (float)s.Y - 4), color, _labelFormat);
                break;
            }
            case ShapeKind.AxesXYZ:
            {
                var o = new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2));
                DrawArrow(ds, o, new Vector2((float)(s.X + s.W), o.Y), color, w);
                DrawArrow(ds, o, new Vector2(o.X, (float)s.Y), color, w);
                DrawArrow(ds, o, new Vector2((float)s.X, (float)(s.Y + s.H)), color, w);
                ds.DrawText("x", new Vector2((float)(s.X + s.W) - 2, o.Y + 4), color, _labelFormat);
                ds.DrawText("y", new Vector2(o.X + 8, (float)s.Y - 4), color, _labelFormat);
                ds.DrawText("z", new Vector2((float)s.X - 2, (float)(s.Y + s.H) + 2), color, _labelFormat);
                break;
            }
        }
    }

    private void DrawArrow(CanvasDrawingSession ds, Vector2 a, Vector2 b, Color color, float w)
    {
        ds.DrawLine(a, b, color, w, _roundStyle);
        var dir = b - a;
        float len = dir.Length();
        if (len < 1) return;
        dir /= len;
        float hs = Math.Max(9f, w * 3.2f);
        var perp = new Vector2(-dir.Y, dir.X);
        ds.DrawLine(b, b - dir * hs + perp * hs * 0.5f, color, w, _roundStyle);
        ds.DrawLine(b, b - dir * hs - perp * hs * 0.5f, color, w, _roundStyle);
    }

    private void DrawShapeSelection(CanvasDrawingSession ds, ShapeElement s, Color accent, float uiScale)
    {
        var bb = ShapeBounds(s);
        ds.DrawRectangle(bb, accent, uiScale, _dashStyle);
        float hs = 5.5f / ViewZoom;
        foreach (var c in ShapeCorners(s))
        {
            ds.FillRectangle(new Rect(c.X - hs, c.Y - hs, hs * 2, hs * 2), Colors.White);
            ds.DrawRectangle(new Rect(c.X - hs, c.Y - hs, hs * 2, hs * 2), accent, uiScale);
        }
    }

    private async void RequestBitmap(string path)
    {
        if (_bitmapLoading.Contains(path) || _bitmaps.ContainsKey(path)) return;
        _bitmapLoading.Add(path);
        try
        {
            var b = await CanvasBitmap.LoadAsync(_canvas, path);
            _bitmaps[path] = b;
        }
        catch
        {
            _bitmaps[path] = null;
        }
        _bitmapLoading.Remove(path);
        _canvas.Invalidate();
    }

    public void InsertImage(string path, double pixelW, double pixelH)
    {
        if (_page == null) return;
        double scale = Math.Min(1.0, 520.0 / Math.Max(1, Math.Max(pixelW, pixelH)));
        double w = Math.Max(48, pixelW * scale), h = Math.Max(48, pixelH * scale);
        var c = ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        var s = new ShapeElement
        {
            Kind = ShapeKind.Image,
            ImagePath = path,
            X = c.X - w / 2,
            Y = c.Y - h / 2,
            W = w,
            H = h,
            Size = 0
        };
        UndoManager.Push(new AddShapeAction(s), _page);
        _activeShape = s;
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    private bool FocusTextAt(Vector2 pos)
    {
        foreach (var (_, ui) in _textUi)
        {
            double l = Canvas.GetLeft(ui.Container), tp = Canvas.GetTop(ui.Container);
            double w = ui.Container.ActualWidth, h = ui.Container.ActualHeight;
            if (pos.X >= l && pos.X <= l + w && pos.Y >= tp && pos.Y <= tp + h)
            {
                ui.Box.Focus(FocusState.Programmatic);
                ActiveTextBox = ui.Box;
                ActiveTextChanged?.Invoke(ui.Box);
                return true;
            }
        }
        return false;
    }

    public void InsertShape(ShapeKind kind, bool equalDims)
    {
        if (_page == null) return;
        var c = ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        double w = 240, h = 160;
        switch (kind)
        {
            case ShapeKind.Line: w = 240; h = 0; break;
            case ShapeKind.Rect: if (equalDims) { w = h = 170; } break;
            case ShapeKind.Ellipse: if (equalDims) { w = h = 170; } break;
            case ShapeKind.Triangle: w = 210; h = 180; break;
            case ShapeKind.AxesXY: w = 280; h = 210; break;
            case ShapeKind.AxesXYZ: w = 240; h = 240; break;
        }
        var s = new ShapeElement
        {
            Kind = kind,
            Color = ColorUtil.ToHex(PenColor),
            Size = Math.Max(2f, PenSize * 0.9f)
        };
        if (kind == ShapeKind.Line)
        {
            s.X = c.X - w / 2; s.Y = c.Y; s.W = w; s.H = 0;
        }
        else
        {
            s.X = c.X - w / 2; s.Y = c.Y - h / 2; s.W = w; s.H = h;
        }
        UndoManager.Push(new AddShapeAction(s), _page);
        _activeShape = s;
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // Text elements
    // =======================================================================
    private void CreateTextElementAt(Point pos)
    {
        if (_page == null) return;
        var t = new TextElement { X = pos.X - 4, Y = pos.Y - 10 };
        UndoManager.Push(new AddTextAction(t), _page);
        RebuildTextLayer();
        if (_textUi.TryGetValue(t.Id, out var ui))
            ui.Box.Focus(FocusState.Programmatic);
        ContentChanged?.Invoke();
    }

    public void RebuildTextLayer()
    {
        _textLayer.Children.Clear();
        _textUi.Clear();
        ActiveTextBox = null;
        if (_page == null) return;
        foreach (var t in _page.Texts)
            BuildTextUi(t);
    }

    private void BuildTextUi(TextElement t)
    {
        var container = new Grid();
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var grip = new Grid
        {
            Height = 16,
            Background = new SolidColorBrush(Color.FromArgb(50, 217, 119, 87)),
            CornerRadius = new CornerRadius(5, 5, 0, 0)
        };
        var dots = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 9,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7
        };
        var close = new Button
        {
            Content = "✕",
            FontSize = 9,
            Padding = new Thickness(0),
            Width = 22,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        grip.Children.Add(dots);
        grip.Children.Add(close);
        Grid.SetRow(grip, 0);

        var box = new RichEditBox
        {
            MinWidth = 160,
            Width = t.Width,
            MinHeight = 40,
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        if (!string.IsNullOrEmpty(t.Rtf))
        {
            try { box.Document.SetText(TextSetOptions.FormatRtf, t.Rtf); } catch { }
        }
        Grid.SetRow(box, 1);

        container.Children.Add(grip);
        container.Children.Add(box);
        Canvas.SetLeft(container, t.X);
        Canvas.SetTop(container, t.Y);

        box.GotFocus += (_, _) =>
        {
            ActiveTextBox = box;
            ActiveTextChanged?.Invoke(box);
        };
        box.TextChanged += (_, _) => ContentChanged?.Invoke();
        box.LostFocus += (_, _) =>
        {
            if (_page == null) return;
            box.Document.GetText(TextGetOptions.None, out string plain);
            if (string.IsNullOrWhiteSpace(plain))
            {
                _page.Texts.Remove(t);
                if (ActiveTextBox == box)
                {
                    ActiveTextBox = null;
                    ActiveTextChanged?.Invoke(null);
                }
                RebuildTextLayer();
                ContentChanged?.Invoke();
            }
        };

        var rGrip = new Border
        {
            Width = 7,
            Background = new SolidColorBrush(Color.FromArgb(80, 217, 119, 87)),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 4, -9, 4)
        };
        Grid.SetRow(rGrip, 1);
        ToolTipService.SetToolTip(rGrip, "Drag to change the text box width");
        rGrip.ManipulationMode = ManipulationModes.TranslateX;
        rGrip.ManipulationDelta += (_, e) =>
        {
            double cur = double.IsNaN(box.Width) ? t.Width : box.Width;
            box.Width = Math.Max(120, cur + e.Delta.Translation.X);
        };
        rGrip.ManipulationCompleted += (_, _) =>
        {
            t.Width = box.Width;
            ContentChanged?.Invoke();
        };
        container.Children.Add(rGrip);

        close.Click += (_, _) =>
        {
            if (_page == null) return;
            FlushTexts();
            UndoManager.Push(new RemoveTextAction(t), _page);
            RebuildTextLayer();
            ActiveTextChanged?.Invoke(null);
            ContentChanged?.Invoke();
        };

        double startX = 0, startY = 0;
        grip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        grip.ManipulationStarted += (_, _) =>
        {
            startX = Canvas.GetLeft(container);
            startY = Canvas.GetTop(container);
        };
        grip.ManipulationDelta += (_, e) =>
        {
            // deltas are reported in the grip's local space, which is already
            // world units (the text layer's RenderTransform maps screen->world)
            Canvas.SetLeft(container, Canvas.GetLeft(container) + e.Delta.Translation.X);
            Canvas.SetTop(container, Canvas.GetTop(container) + e.Delta.Translation.Y);
        };
        grip.ManipulationCompleted += (_, _) =>
        {
            if (_page == null) return;
            double nx = Canvas.GetLeft(container), ny = Canvas.GetTop(container);
            if (Math.Abs(nx - startX) > 0.5 || Math.Abs(ny - startY) > 0.5)
            {
                UndoManager.Push(new MoveTextAction(t, startX, startY, nx, ny), _page);
                ContentChanged?.Invoke();
            }
        };

        _textLayer.Children.Add(container);
        _textUi[t.Id] = (container, box);
    }
}
