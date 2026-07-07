using System.Numerics;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
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

namespace Quill.Controls;

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
    public TimeSpan? AudioPlayheadPosition { get; set; }
    public long? RecordingStartTicks { get; set; }
    public event Action? ViewChanged;
    public event Action<double>? RulerAngleChanged; // raised when 2-finger tilt changes the ruler

    // ---- tool state -------------------------------------------------------
    public ToolType Tool { get; private set; } = ToolType.Pen;
    public PenType Pen { get; set; } = PenType.Standard;
    public Color PenColor { get; set; } = Color.FromArgb(255, 20, 20, 19);
    public float PenSize { get; set; } = 3.5f;
    public float PenSensitivity { get; set; } = 1f;
    public float PenStabiliser { get; set; }
    public List<float>? PenPressureCurve { get; set; }
    // Default font + point size applied to newly created text boxes and to the
    // first characters typed into them, so a size/font chosen with no box selected
    // is honoured instead of falling back to the RichEdit default (#2, #8).
    public float PendingFontSize { get; set; } = 16f;
    public string PendingFontFamily { get; set; } = "Lora";
    public EraserMode EraserMode { get; set; } = EraserMode.Object;
    public bool RulerMode { get; set; }
    // On-screen ruler angle in degrees (any value, not just 15° steps) (#21).
    public double RulerAngle { get; set; }
    public bool HandDrawMode { get; set; }
    public MouseMode MouseMode { get; set; } = MouseMode.Auto;

    private const int InkCacheThreshold = 2500;
    private CanvasRenderTarget? _inkCache;
    private Rect _inkCacheWorld;
    private float _inkCacheScale = 1f;
    private float _inkCacheBuiltZoom = 1f;   // ViewZoom when the cache was rendered (#55)
    private bool _inkCacheDirty = true;

    // true while a pen is hovering with its eraser engaged (button/inverted) so
    // we can show the eraser ring before it touches down.
    private bool _penEraserHover;
    // pen barrel ("select") button gesture: a tap (no drag) opens the context
    // menu on release; a drag becomes a lasso selection.
    private bool _barrelGesture;
    private bool _barrelMoved;
    private Vector2 _barrelStartScreen;

    public UndoRedoManager UndoManager { get; } = new();
    public NotePage? Page => _page;
    public RichEditBox? ActiveTextBox { get; private set; }

    public event Action? ContentChanged;
    public event Action<RichEditBox?>? ActiveTextChanged;
    public event Action? ReplayEnded;
    public event Action? TitleClicked;
    public event Action? DateClicked;
    /// <summary>Right-click / pen barrel-tap / touch long-press: position is in
    /// InkSurface-local coordinates so the menu can be shown there.</summary>
    public event Action<Point>? ContextMenuRequested;
    public event Action<PenStroke>? StrokeTapped;

    // Internal copy/paste clipboard for canvas objects (shared across pages).
    private static List<PenStroke>? _clipStrokes;
    private static List<ShapeElement>? _clipShapes;
    private static List<TextElement>? _clipTexts;

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
    // Lasso/rubber-band can also select shapes and text boxes (#19).
    private readonly List<ShapeElement> _selShapes = new();
    private readonly HashSet<ShapeElement> _selShapeSet = new();
    private readonly List<TextElement> _selTexts = new();
    private readonly Dictionary<TextElement, (double L, double T)> _textMoveOrig = new();
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

    // Pending text caret: a Text-tool tap blinks a caret here; the text box is
    // only created once the user starts typing. An image pasted while a caret
    // is pending lands here instead of the screen centre.
    private Vector2? _pendingTextPos;
    private bool _caretOn;
    private readonly DispatcherTimer _caretTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };

    // ---- shapes ----
    private ShapeElement? _activeShape;
    private bool _shapeAdjust;
    private ShapeElement? _adjustShape;
    private Vector2 _adjustAnchor;
    private bool _adjustConstrain;
    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private Vector2 _stablePos;
    private Vector2 _lastSmoothedPos;
    private long _lastMoveMs;
    private bool _movingShape, _resizingShape;
    private bool _rotatingShape;
    private double _rotateStartPointerDeg, _rotateStartShapeDeg;
    private Vector2 _rotateCenter;
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
    // Flat caps for the highlighter so it doesn't leave rounded blobs at the ends.
    private readonly CanvasStrokeStyle _flatStyle = new()
    {
        StartCap = CanvasCapStyle.Flat,
        EndCap = CanvasCapStyle.Flat,
        LineJoin = CanvasLineJoin.Round
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
        _canvas.PointerExited += (_, _) => { _hover = null; _penEraserHover = false; _canvas.Invalidate(); };

        // Right mouse button, pen barrel-button tap, and touch press-and-hold all
        // raise RightTapped — our single context-menu trigger.
        _canvas.RightTapped += OnRightTapped;
        _canvas.Tapped += OnCanvasTapped;
        _canvas.DoubleTapped += OnCanvasDoubleTapped;

        // Touch panning / pinch zoom handled by us (no ScrollViewer anywhere,
        // so nothing can steal the pen mid-stroke).
        _canvas.ManipulationMode =
            ManipulationModes.TranslateX | ManipulationModes.TranslateY |
            ManipulationModes.Scale | ManipulationModes.Rotate |
            ManipulationModes.TranslateInertia | ManipulationModes.ScaleInertia;
        _canvas.ManipulationStarted += OnManipStarted;
        _canvas.ManipulationDelta += OnManipDelta;

        _replayTimer.Tick += ReplayTick;
        _holdTimer.Tick += HoldTick;
        _caretTimer.Tick += (_, _) => { _caretOn = !_caretOn; _canvas.Invalidate(); };

        // Receive the first typed character so a pending caret can spawn a box.
        CharacterReceived += OnCharacterReceived;
        KeyDown += OnKeyDown;

        ContentChanged += () => _inkCacheDirty = true;   // any edit invalidates the ink cache (#43)
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

    public (Vector2 Offset, float Zoom) GetView() => (ViewOffset, ViewZoom);

    public void SetView(Vector2 offset, float zoom)
    {
        ViewZoom = Math.Clamp(zoom, 0.05f, 8f);
        ViewOffset = offset;
        OnViewChanged();
    }

    /// <summary>World-space bounding rectangle of everything on the page (strokes,
    /// shapes, text boxes and the title header). Used by whole-page export.</summary>
    public Rect? ContentBoundsWorld()
    {
        if (_page == null) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Inc(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        foreach (var s in _page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            s.GetBounds(out float a, out float b, out float c, out float d);
            Inc(a, b); Inc(c, d);
        }
        foreach (var sh in _page.Shapes)
        {
            var r = ShapeBounds(sh);
            Inc(r.Left, r.Top); Inc(r.Right, r.Bottom);
        }
        foreach (var (_, ui) in _textUi)
        {
            double l = Canvas.GetLeft(ui.Container), t = Canvas.GetTop(ui.Container);
            double w = ui.Container.ActualWidth > 0 ? ui.Container.ActualWidth : ui.Box.Width;
            double h = ui.Container.ActualHeight > 0 ? ui.Container.ActualHeight : 48;
            Inc(l, t); Inc(l + w, t + h);
        }
        // always include the title/date header at the top-left
        Inc(40, 14); Inc(470, 96);
        if (minX == double.MaxValue) { Inc(0, 0); Inc(_page.Width, _page.Height); }
        return new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY));
    }

    /// <summary>Pans/zooms so the whole page content fits the viewport (for export).</summary>
    public void FitToContent(double marginPx)
    {
        var b = ContentBoundsWorld();
        if (b == null) return;
        var r = b.Value;
        double vw = ActualWidth, vh = ActualHeight;
        if (vw < 10 || vh < 10) return;
        double zx = (vw - 2 * marginPx) / r.Width;
        double zy = (vh - 2 * marginPx) / r.Height;
        float zoom = (float)Math.Clamp(Math.Min(zx, zy), 0.05, 4.0);
        float offX = (float)(marginPx - r.X * zoom);
        float offY = (float)(marginPx - r.Y * zoom);
        SetView(new Vector2(offX, offY), zoom);
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
        // While the ruler is shown, a two-finger twist tilts it to any angle (#21).
        if (RulerMode && Math.Abs(e.Delta.Rotation) > 0.01)
        {
            RulerAngle += e.Delta.Rotation;
            RulerAngleChanged?.Invoke(RulerAngle);
            _canvas.Invalidate();
        }
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
        CancelPendingText();
        NormalizeContent(page);
        _page = page;
        _inkCacheDirty = true;   // fresh page, fresh ink cache (#43)
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
        CancelPendingText();
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
        _activeShape = null; // don't leave a removed shape's selection box behind
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
        _activeShape = null;
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
        _selShapes.Clear();
        _selShapeSet.Clear();
        _selTexts.Clear();
        _textMoveOrig.Clear();
        _selBounds = Rect.Empty;
        _movingSel = false;
        _moveDx = _moveDy = 0;
        _canvas.Invalidate();
    }

    private bool HasMultiSelection => _selected.Count > 0 || _selShapes.Count > 0 || _selTexts.Count > 0;

    public bool HasSelection => HasMultiSelection;

    /// <summary>True when Delete should remove something: a multi-selection or
    /// the active shape/image.</summary>
    public bool HasDeletable => HasMultiSelection || _activeShape != null;

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
        if (!HasMultiSelection) return;
        FlushTexts();
        var strokes = _selected.ToList();
        var shapes = _selShapes.ToList();
        var texts = _selTexts.ToList();
        UndoManager.Push(new RemoveMixedAction(strokes, shapes, texts), _page);
        ClearSelection();
        RebuildTextLayer();
        ContentChanged?.Invoke();
    }

    // Duplicating a table shape must also duplicate its cell text bubbles,
    // re-linked to the clone's id (#55).
    private void CloneTableCells(ShapeElement source, ShapeElement clone, float offset, List<TextElement> into)
    {
        if (_page == null || source.Kind != ShapeKind.Table) return;
        foreach (var cell in _page.Texts)
        {
            if (cell.TableId != source.Id) continue;
            into.Add(new TextElement
            {
                X = cell.X + offset,
                Y = cell.Y + offset,
                Width = cell.Width,
                Rtf = cell.Rtf,
                Rotation = cell.Rotation,
                TableId = clone.Id,
                TableRow = cell.TableRow,
                TableCol = cell.TableCol
            });
        }
    }

    public void DuplicateSelection()
    {
        if (_page == null) return;
        FlushTexts();

        var clonedStrokes = new List<PenStroke>();
        var clonedShapes = new List<ShapeElement>();
        var clonedTexts = new List<TextElement>();

        float offset = 40f;

        if (_activeShape != null)
        {
            var shape = _activeShape;
            var clone = new ShapeElement
            {
                Kind = shape.Kind,
                X = shape.X + offset,
                Y = shape.Y + offset,
                W = shape.W,
                H = shape.H,
                Color = shape.Color,
                Size = shape.Size,
                ImagePath = shape.ImagePath,
                Rotation = shape.Rotation,
                TRows = shape.TRows,
                TCols = shape.TCols,
                TColW = shape.TColW != null ? new List<double>(shape.TColW) : null,
                TRowH = shape.TRowH != null ? new List<double>(shape.TRowH) : null
            };
            clonedShapes.Add(clone);
            CloneTableCells(shape, clone, offset, clonedTexts);   // tables bring their cells (#55)
        }
        else if (HasMultiSelection)
        {
            foreach (var stroke in _selected)
            {
                var pts = stroke.Points.Select(p => new StrokePoint(p.X + offset, p.Y + offset, p.Pressure)).ToList();
                clonedStrokes.Add(stroke.CloneWithPoints(pts));
            }
            foreach (var shape in _selShapes)
            {
                var clone = new ShapeElement
                {
                    Kind = shape.Kind,
                    X = shape.X + offset,
                    Y = shape.Y + offset,
                    W = shape.W,
                    H = shape.H,
                    Color = shape.Color,
                    Size = shape.Size,
                    ImagePath = shape.ImagePath,
                    Rotation = shape.Rotation,
                    TRows = shape.TRows,
                    TCols = shape.TCols,
                    TColW = shape.TColW != null ? new List<double>(shape.TColW) : null,
                    TRowH = shape.TRowH != null ? new List<double>(shape.TRowH) : null
                };
                clonedShapes.Add(clone);
                CloneTableCells(shape, clone, offset, clonedTexts);   // tables bring their cells (#55)
            }
            foreach (var text in _selTexts)
            {
                var clone = new TextElement
                {
                    X = text.X + offset,
                    Y = text.Y + offset,
                    Width = text.Width,
                    Rtf = text.Rtf,
                    Rotation = text.Rotation,
                    TableId = text.TableId,
                    TableRow = text.TableRow,
                    TableCol = text.TableCol
                };
                clonedTexts.Add(clone);
            }
        }
        else
        {
            return;
        }

        if (clonedStrokes.Count > 0 || clonedShapes.Count > 0 || clonedTexts.Count > 0)
        {
            UndoManager.Push(new AddMixedAction(clonedStrokes, clonedShapes, clonedTexts), _page);
            ClearSelection();
            _activeShape = null;

            if (clonedShapes.Count == 1 && clonedStrokes.Count == 0 && clonedTexts.Count == 0)
            {
                _activeShape = clonedShapes[0];
            }
            else
            {
                foreach (var s in clonedStrokes) _selected.Add(s);
                foreach (var sh in clonedShapes) _selShapes.Add(sh);
                foreach (var t in clonedTexts) _selTexts.Add(t);
                RecomputeSelectionBounds();
            }

            RebuildTextLayer();
            _canvas.Invalidate();
            ContentChanged?.Invoke();
        }
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

    // The pen's eraser tip / inverted end / extra barrel buttons all act as an
    // eraser. Shared by pointer-down routing and the hover-cursor detection.
    private static bool IsEraserButtons(Microsoft.UI.Input.PointerPointProperties p, bool isPen) =>
        p.IsEraser || p.IsInverted ||
        (isPen && (p.IsXButton1Pressed || p.IsXButton2Pressed || p.IsMiddleButtonPressed));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        CancelPendingText(); // a fresh press dismisses any blinking caret
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
        // Right-button DRAG lassos; right-button TAP opens the context menu (#44).
        // Over an existing selection the tap keeps it (menu applies to it) and a
        // drag moves it — mirroring the pen's barrel button.
        if (isMouse && props.IsRightButtonPressed)
        {
            _barrelGesture = true;   // reuse the barrel tap-vs-drag machinery
            _barrelMoved = false;
            _barrelStartScreen = screen;
            _skipNextRightTap = true;
            _activePointer = e.Pointer.PointerId;
            _gestureTool = ToolType.Select;
            _canvas.CapturePointer(e.Pointer);
            bool overSel =
                (HasMultiSelection && !_selBounds.IsEmpty && _selBounds.Contains(new Point(pos.X, pos.Y))) ||
                (_activeShape != null && OnShapeBody(_activeShape, pos, 10f / ViewZoom));
            if (overSel)
            {
                TryBeginShapeOrSelectionDrag(pos, 10f / ViewZoom);
            }
            else
            {
                ClearSelection();
                _activeShape = null;
                _lasso = new List<Vector2> { pos };
            }
            e.Handled = true;
            _canvas.Invalidate();
            return;
        }

        if (isMouse && !props.IsLeftButtonPressed) return;

        // Pen barrel ("select") button opens the context menu. RightTapped is
        // unreliable for the pen barrel over a Win2D canvas, so raise it here
        // directly rather than depending on the gesture recogniser.
        if (isPen && props.IsBarrelButtonPressed && !IsEraserButtons(props, isPen))
        {
            // Begin a barrel gesture: tapped (no drag) -> context menu on
            // release; dragged -> lasso selection.
            _barrelGesture = true;
            _barrelMoved = false;
            _barrelStartScreen = screen;
            _activePointer = e.Pointer.PointerId;
            _gestureTool = ToolType.Select;
            _canvas.CapturePointer(e.Pointer);
            // Barrel press ON the current selection keeps it: a tap opens the
            // selection's context menu, a drag moves it (#42). Elsewhere it
            // starts a fresh lasso as before.
            bool overSelection =
                (HasMultiSelection && !_selBounds.IsEmpty && _selBounds.Contains(new Point(pos.X, pos.Y))) ||
                (_activeShape != null && OnShapeBody(_activeShape, pos, 10f / ViewZoom));
            if (overSelection)
            {
                TryBeginShapeOrSelectionDrag(pos, 10f / ViewZoom);
            }
            else
            {
                ClearSelection();
                _activeShape = null;
                _lasso = new List<Vector2> { pos };
            }
            e.Handled = true;
            _canvas.Invalidate();
            return;
        }

        // "+" adder buttons around a selected table work with any input (#49)
        if (_activeShape is { Kind: ShapeKind.Table } tPlus && HitTablePlus(tPlus, pos, out bool plusColumn))
        {
            ShowTablePlusMenu(tPlus, plusColumn, screen);
            e.Handled = true;
            return;
        }

        var tool = Tool;
        // Pen first button (eraser tip / inverted pen / extra side buttons)
        // acts as the eraser.
        if (IsEraserButtons(props, isPen))
        {
            tool = ToolType.Eraser;
        }

        if (tool == ToolType.Pen && !isPen && !HandDrawMode)
        {
            if (!isMouse)
            {
                // touch can grab, move or scale an existing lasso selection (#42/#54)
                if (HasMultiSelection && !_selBounds.IsEmpty &&
                    (TryBeginSelectionScale(pos, 14f / ViewZoom) ||
                     _selBounds.Contains(new Point(pos.X, pos.Y))))
                {
                    _activePointer = e.Pointer.PointerId;
                    _gestureTool = ToolType.Select;
                    _canvas.CapturePointer(e.Pointer);
                    if (!_scalingSel) BeginSelectionMove(pos);
                    e.Handled = true;
                    return;
                }
                if (_activeShape != null)
                {
                    float tol = 10f / ViewZoom;
                    var handle = HitHandle(_activeShape, pos, tol);
                    bool onBody = OnShapeBody(_activeShape, pos, tol);
                    if (handle != null || onBody)
                    {
                        _activePointer = e.Pointer.PointerId;
                        _gestureTool = ToolType.Select;
                        _canvas.CapturePointer(e.Pointer);
                        if (handle != null)
                        {
                            _resizingShape = true;
                            _resizeAnchor = handle.Value;
                            _shapeOrig = Snapshot(_activeShape);
                            _adjustConstrain = false;
                            _resizeAspect = _activeShape.Kind == ShapeKind.Image && Math.Abs(_shapeOrig.H) > 1
                                ? Math.Abs(_shapeOrig.W) / Math.Abs(_shapeOrig.H)
                                : 0;
                        }
                        else
                        {
                            _movingShape = true;
                            _shapeStart = pos;
                            _shapeOrig = Snapshot(_activeShape);
                        }
                        e.Handled = true;
                        return;
                    }
                }
                return;            // touch pans
            }
            HandleMousePress(e, pos, screen); // routed by the selected mouse mode
            return;
        }

        if (tool == ToolType.Text)
        {
            // Don't create a box yet — just blink a caret where text will go.
            SetPendingText(pos);
            e.Handled = true;
            return;
        }

        _activePointer = e.Pointer.PointerId;
        _gestureTool = tool;
        _canvas.CapturePointer(e.Pointer);

        switch (tool)
        {
            case ToolType.Pen:
                // The pen can grab, drag or SCALE a lasso selection directly (#42/#54):
                // corner handles scale, anywhere inside moves; elsewhere draws.
                if (HasMultiSelection && !_selBounds.IsEmpty)
                {
                    if (TryBeginSelectionScale(pos, 10f / ViewZoom) ||
                        _selBounds.Contains(new Point(pos.X, pos.Y)))
                    {
                        _gestureTool = ToolType.Select;
                        if (!_scalingSel) BeginSelectionMove(pos);
                        break;
                    }
                    else
                    {
                        ClearSelection();
                    }
                }
                // A selected shape/image can be moved or resized with the pen:
                // pressing on the active shape's body or a handle grabs it,
                // pressing anywhere else draws — so you can still draw over
                // images that aren't selected.
                if (_activeShape != null)
                {
                    bool hitHandled = false;
                    if (_activeShape.Kind != ShapeKind.Image)
                    {
                        float tolP = 10f / ViewZoom;
                        var handleP = HitHandle(_activeShape, pos, tolP);
                        bool onBody = OnShapeBody(_activeShape, pos, tolP);
                        if (handleP != null || onBody)
                        {
                            _gestureTool = ToolType.Select; // reuse the move/resize machinery
                            if (handleP != null)
                            {
                                _resizingShape = true;
                                _resizeAnchor = handleP.Value;
                                _shapeOrig = Snapshot(_activeShape);
                                _adjustConstrain = false;
                                _resizeAspect = 0;
                            }
                            else
                            {
                                _movingShape = true;
                                _shapeStart = pos;
                                _shapeOrig = Snapshot(_activeShape);
                            }
                            hitHandled = true;
                        }
                    }
                    if (hitHandled) break;
                    _activeShape = null;
                }
                _wet = new List<StrokePoint> { new(pos.X, pos.Y, props.Pressure) };
                _wetStart = pos;
                _wetEnd = pos;
                _stablePos = pos;
                _lastSmoothedPos = pos;
                _lastMoveMs = Environment.TickCount64;
                _shapeAdjust = false;
                _adjustShape = null;
                _holdTimer.Start();
                break;

            case ToolType.Eraser:
                ClearSelection();
                _activeShape = null;
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
                if (TryBeginShapeOrSelectionDrag(pos, tol)) break;
                _activeShape = null;
                ClearSelection();
                _lasso = new List<Vector2> { pos };
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

    // Mouse press while the Pen tool is active: behaviour depends on the chosen
    // mouse mode. Auto = normal mouse, Grab = pan, Select = rubber-band select,
    // Move = drag images/shapes only.
    private void HandleMousePress(PointerRoutedEventArgs e, Vector2 pos, Vector2 screen)
    {
        _activePointer = e.Pointer.PointerId;
        _canvas.CapturePointer(e.Pointer);
        float tol = 10f / ViewZoom;

        if (MouseMode == MouseMode.Grab)
        {
            _mousePanning = true;
            _mousePanLast = screen;
            e.Handled = true;
            return;
        }

        _gestureTool = ToolType.Select;

        // Grab a shape, image, or the existing selection first — Auto, Select
        // and Move all let you drag objects.
        if (TryBeginShapeOrSelectionDrag(pos, tol))
        {
            e.Handled = true;
            _canvas.Invalidate();
            return;
        }

        // Auto / Move: clicking a text box focuses it for editing (no drag).
        if (MouseMode != MouseMode.Select && FocusTextAt(pos))
        {
            ResetGesture();
            _canvas.ReleasePointerCaptures();
            e.Handled = true;
            return;
        }

        ClearSelection();
        _activeShape = null;

        if (MouseMode == MouseMode.Move)
        {
            // Move never rubber-bands: an empty press just deselects.
            ResetGesture();
            _canvas.ReleasePointerCaptures();
            e.Handled = true;
            _canvas.Invalidate();
            return;
        }

        // Auto / Select: rubber-band rectangle (also drives title/date/text-box
        // click handling in CommitGesture).
        _rectSelect = true;
        _rectStart = pos;
        _rectCur = pos;
        e.Handled = true;
        _canvas.Invalidate();
    }

    /// <summary>
    /// Starts a move/resize on the active shape (handle or body), on a freshly
    /// hit shape, or on the current stroke selection. Returns false if the press
    /// landed on empty space. Shared by the Select tool, the mouse modes, and
    /// the pen's grab-the-selected-shape path.
    /// </summary>
    private bool TryBeginShapeOrSelectionDrag(Vector2 pos, float tol)
    {
        if (_page == null) return false;
        // table dividers first: grab an inner line to resize its column/row (#49)
        if (_activeShape is { Kind: ShapeKind.Table } tab &&
            HitTableDivider(tab, pos, Math.Max(tol, 6f / ViewZoom), out int dc, out int dr))
        {
            _tableDividerDrag = true;
            _tableDivCol = dc;
            _tableDivRow = dr;
            _tableOrigColW = TableColWidths(tab).ToList();
            _tableOrigRowH = TableRowHeights(tab).ToList();
            _tableOrigW = tab.W;
            _tableOrigH = tab.H;
            tab.TColW = _tableOrigColW.ToList();
            tab.TRowH = _tableOrigRowH.ToList();
            return true;
        }
        // selection scale handles win over a plain grab (#54)
        if (TryBeginSelectionScale(pos, tol)) return true;
        if (_activeShape != null)
        {
            if (HitRotateHandle(_activeShape, pos, tol))
            {
                BeginRotate(_activeShape, pos);
                return true;
            }
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
                return true;
            }
            if (OnShapeBody(_activeShape, pos, tol))
            {
                _movingShape = true;
                _shapeStart = pos;
                _shapeOrig = Snapshot(_activeShape);
                return true;
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
            return true;
        }
        if (HasMultiSelection && !_selBounds.IsEmpty && _selBounds.Contains(new Point(pos.X, pos.Y)))
        {
            BeginSelectionMove(pos);
            return true;
        }
        return false;
    }

    // ---- selection scaling (#54): corner handles resize the whole selection ----
    private bool _scalingSel;
    private Vector2 _scaleAnchor, _scaleStartPos;
    private float _scaleFactor = 1f;
    private List<(PenStroke S, float[] Xs, float[] Ys)>? _scaleStrokes;
    private List<(ShapeElement S, double X, double Y, double W, double H)>? _scaleShapes;
    private List<(TextElement T, double X, double Y, double W)>? _scaleTexts;
    private Rect _scaleBoundsOrig;

    private Vector2[] SelCorners() => new[]
    {
        new Vector2((float)_selBounds.Left, (float)_selBounds.Top),
        new Vector2((float)_selBounds.Right, (float)_selBounds.Top),
        new Vector2((float)_selBounds.Right, (float)_selBounds.Bottom),
        new Vector2((float)_selBounds.Left, (float)_selBounds.Bottom)
    };

    private bool TryBeginSelectionScale(Vector2 pos, float tol)
    {
        if (!HasMultiSelection || _selBounds.IsEmpty) return false;
        var corners = SelCorners();
        for (int i = 0; i < 4; i++)
        {
            if (Vector2.Distance(pos, corners[i]) > Math.Max(tol, 9f / ViewZoom)) continue;
            _scalingSel = true;
            _scaleAnchor = corners[(i + 2) % 4];   // opposite corner stays put
            _scaleStartPos = pos;
            _scaleFactor = 1f;
            _scaleBoundsOrig = _selBounds;
            _scaleStrokes = _selected
                .Select(s => (s, s.Points.Select(p => p.X).ToArray(), s.Points.Select(p => p.Y).ToArray()))
                .ToList();
            _scaleShapes = _selShapes.Select(s => (s, s.X, s.Y, s.W, s.H)).ToList();
            _scaleTexts = _selTexts.Select(t => (t, t.X, t.Y, t.Width)).ToList();
            return true;
        }
        return false;
    }

    private void ApplyScaleLive()
    {
        if (_scaleStrokes == null || _scaleShapes == null || _scaleTexts == null) return;
        float f = _scaleFactor, ax = _scaleAnchor.X, ay = _scaleAnchor.Y;
        foreach (var (s, xs, ys) in _scaleStrokes)
            for (int i = 0; i < s.Points.Count && i < xs.Length; i++)
            {
                s.Points[i].X = ax + (xs[i] - ax) * f;
                s.Points[i].Y = ay + (ys[i] - ay) * f;
            }
        foreach (var (s, x, y, w, h) in _scaleShapes)
        {
            s.X = ax + (x - ax) * f;
            s.Y = ay + (y - ay) * f;
            s.W = w * f;
            s.H = h * f;
        }
        foreach (var (t, x, y, w) in _scaleTexts)
        {
            t.X = ax + (x - ax) * f;
            t.Y = ay + (y - ay) * f;
            t.Width = Math.Max(60, w * f);
            if (_textUi.TryGetValue(t.Id, out var ui))
            {
                Canvas.SetLeft(ui.Container, t.X);
                Canvas.SetTop(ui.Container, t.Y);
                ui.Box.Width = t.Width;
            }
        }
        // scale the visible selection box too
        double nx = ax + (_scaleBoundsOrig.X - ax) * f;
        double ny = ay + (_scaleBoundsOrig.Y - ay) * f;
        _selBounds = new Rect(Math.Min(nx, ax), Math.Min(ny, ay),
            _scaleBoundsOrig.Width * f, _scaleBoundsOrig.Height * f);
        _inkCacheDirty = true;
    }

    // Preview which stroke the object eraser would remove (#53).
    private PenStroke? FindStrokeNear(Vector2 p, float radius)
    {
        if (_page == null) return null;
        static float DistSeg(Vector2 pt, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float len2 = ab.LengthSquared();
            if (len2 < 1e-6f) return Vector2.Distance(pt, a);
            float t = Math.Clamp(Vector2.Dot(pt - a, ab) / len2, 0f, 1f);
            return Vector2.Distance(pt, a + ab * t);
        }
        foreach (var s in _page.Strokes)
        {
            s.GetBounds(out float bx0, out float by0, out float bx1, out float by1);
            float pad = radius + s.Size;
            if (p.X < bx0 - pad || p.X > bx1 + pad || p.Y < by0 - pad || p.Y > by1 + pad) continue;
            var pts = s.Points;
            if (pts.Count == 1)
            {
                if (Vector2.Distance(p, new Vector2(pts[0].X, pts[0].Y)) <= pad) return s;
                continue;
            }
            for (int i = 1; i < pts.Count; i++)
                if (DistSeg(p, new Vector2(pts[i - 1].X, pts[i - 1].Y), new Vector2(pts[i].X, pts[i].Y)) <= pad)
                    return s;
        }
        return null;
    }

    private void BeginSelectionMove(Vector2 pos)
    {
        _movingSel = true;
        _moveStart = pos;
        _moveDx = _moveDy = 0;
        _textMoveOrig.Clear();
        foreach (var t in _selTexts)
            if (_textUi.TryGetValue(t.Id, out var ui))
                _textMoveOrig[t] = (Canvas.GetLeft(ui.Container), Canvas.GetTop(ui.Container));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_page == null) return;
        var pp = e.GetCurrentPoint(_canvas);
        var screen = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
        _hover = ToWorld(screen);

        if (_activePointer == null || e.Pointer.PointerId != _activePointer)
        {
            // hovering, not dragging: light up the eraser ring when a pen
            // hovers with its eraser engaged.
            bool penEraser = e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen &&
                             IsEraserButtons(pp.Properties, true);
            bool changed = penEraser != _penEraserHover;
            _penEraserHover = penEraser;
            // redraw to move/clear the ring (changed covers the falling edge).
            if (changed || Tool == ToolType.Eraser || _penEraserHover) _canvas.Invalidate();
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

        // a barrel gesture that moves past a small threshold is a drag (lasso),
        // not a tap (which would open the context menu on release).
        if (_barrelGesture && !_barrelMoved && Vector2.Distance(screen, _barrelStartScreen) > 8f)
            _barrelMoved = true;

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
                        if (PenStabiliser > 0)
                        {
                            float factor = 1f - PenStabiliser * 0.85f;
                            v.X = _lastSmoothedPos.X + (v.X - _lastSmoothedPos.X) * factor;
                            v.Y = _lastSmoothedPos.Y + (v.Y - _lastSmoothedPos.Y) * factor;
                            _lastSmoothedPos = v;
                        }
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
                if (_tableDividerDrag && _activeShape is { Kind: ShapeKind.Table } tdrag)
                {
                    // live column/row resize (#49); cells snap on release
                    if (_tableDivCol > 0 && tdrag.TColW != null)
                    {
                        double before = 0;
                        for (int i = 0; i < _tableDivCol - 1; i++) before += tdrag.TColW[i];
                        tdrag.TColW[_tableDivCol - 1] = Math.Clamp(pos.X - tdrag.X - before, 28, 4000);
                        tdrag.W = tdrag.TColW.Sum();
                    }
                    else if (_tableDivRow > 0 && tdrag.TRowH != null)
                    {
                        double before = 0;
                        for (int i = 0; i < _tableDivRow - 1; i++) before += tdrag.TRowH[i];
                        tdrag.TRowH[_tableDivRow - 1] = Math.Clamp(pos.Y - tdrag.Y - before, 24, 4000);
                        tdrag.H = tdrag.TRowH.Sum();
                    }
                    break;
                }
                if (_rotatingShape && _activeShape != null)
                {
                    double cur = Math.Atan2(pos.Y - _rotateCenter.Y, pos.X - _rotateCenter.X) * 180.0 / Math.PI;
                    double ang = _rotateStartShapeDeg + (cur - _rotateStartPointerDeg);
                    double snap = Math.Round(ang / 15.0) * 15.0;     // gentle 15° snap
                    if (Math.Abs(snap - ang) < 4) ang = snap;
                    _activeShape.Rotation = ang;
                }
                else if (_resizingShape && _activeShape != null)
                {
                    ResizeShape(_activeShape, _resizeAnchor, ToShapeLocal(_activeShape, pos), false, _resizeAspect);
                }
                else if (_movingShape && _activeShape != null)
                {
                    _activeShape.X = _shapeOrig.X + (pos.X - _shapeStart.X);
                    _activeShape.Y = _shapeOrig.Y + (pos.Y - _shapeStart.Y);
                }
                else if (_scalingSel)
                {
                    float d0 = Vector2.Distance(_scaleStartPos, _scaleAnchor);
                    float d1 = Vector2.Distance(pos, _scaleAnchor);
                    _scaleFactor = Math.Clamp(d0 < 1f ? 1f : d1 / d0, 0.15f, 10f);
                    ApplyScaleLive();
                }
                else if (_movingSel)
                {
                    _moveDx = pos.X - _moveStart.X;
                    _moveDy = pos.Y - _moveStart.Y;
                    foreach (var kv in _textMoveOrig)
                        if (_textUi.TryGetValue(kv.Key.Id, out var ui))
                        {
                            Canvas.SetLeft(ui.Container, kv.Value.L + _moveDx);
                            Canvas.SetTop(ui.Container, kv.Value.T + _moveDy);
                        }
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

    private bool _skipNextRightTap;

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        if (_skipNextRightTap)
        {
            // the press-side right-button gesture already handled this click
            _skipNextRightTap = false;
            e.Handled = true;
            return;
        }
        ContextMenuRequested?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    public Vector2 ScreenToWorld(Point screen) => ToWorld(new Vector2((float)screen.X, (float)screen.Y));

    /// <summary>Pans (keeping the current zoom) so the given world point sits
    /// in the middle of the view — used by search result navigation (#46).</summary>
    public void CenterOnWorld(double x, double y) =>
        SetView(new Vector2(
            (float)(ActualWidth / 2 - x * ViewZoom),
            (float)(ActualHeight / 2 - y * ViewZoom)), ViewZoom);

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
                var pts = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : FinalizeStroke(_wet ?? new List<StrokePoint>());
                if (pts.Count >= 1)
                {
                    var stroke = new PenStroke
                    {
                        Pen = Pen,
                        Color = ColorUtil.ToHex(PenColor),
                        Size = PenSize,
                        Sens = PenSensitivity,
                        Points = pts,
                        PressureCurve = PenPressureCurve != null ? new List<float>(PenPressureCurve) : null
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
                if (_barrelGesture && !_barrelMoved)
                {
                    // tapped, not dragged -> open the context menu, no selection
                    _lasso = null;
                    ContextMenuRequested?.Invoke(new Point(_barrelStartScreen.X, _barrelStartScreen.Y));
                    break;
                }
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
                        // a plain mouse click (no drag): title / date, then text
                        // boxes; otherwise empty space drops a blinking caret so
                        // you can start typing (or paste an image) right there.
                        var cp = _rectStart;
                        if (cp.X >= 38 && cp.X <= 470 && cp.Y >= 12 && cp.Y <= 58)
                            TitleClicked?.Invoke();
                        else if (cp.X >= 38 && cp.X <= 470 && cp.Y > 58 && cp.Y <= 92)
                            DateClicked?.Invoke();
                        else if (!FocusTextAt(cp))
                        {
                            ClearSelection();
                            _activeShape = null;
                            SetPendingText(cp);
                        }
                    }
                    break;
                }
                if (_tableDividerDrag && _activeShape is { Kind: ShapeKind.Table } tdone)
                {
                    UndoManager.Push(new TableLayoutAction(tdone,
                        _tableOrigColW ?? TableColWidths(tdone).ToList(),
                        _tableOrigRowH ?? TableRowHeights(tdone).ToList(),
                        _tableOrigW, _tableOrigH,
                        TableColWidths(tdone).ToList(), TableRowHeights(tdone).ToList(),
                        tdone.W, tdone.H), _page, alreadyDone: true);
                    ReflowTableCells(tdone);
                    _tableDividerDrag = false;
                    _tableDivCol = _tableDivRow = -1;
                    changed = true;
                    break;
                }
                if (_rotatingShape && _activeShape != null)
                {
                    if (Math.Abs(_activeShape.Rotation - _rotateStartShapeDeg) > 0.1)
                    {
                        UndoManager.Push(new RotateShapeAction(_activeShape, _rotateStartShapeDeg, _activeShape.Rotation), _page, alreadyDone: true);
                        changed = true;
                    }
                    _rotatingShape = false;
                    break;
                }
                if ((_resizingShape || _movingShape) && _activeShape != null)
                {
                    var now = Snapshot(_activeShape);
                    if (Math.Abs(now.X - _shapeOrig.X) > 0.5 || Math.Abs(now.Y - _shapeOrig.Y) > 0.5 ||
                        Math.Abs(now.W - _shapeOrig.W) > 0.5 || Math.Abs(now.H - _shapeOrig.H) > 0.5)
                    {
                        UndoManager.Push(new MoveResizeShapeAction(_activeShape, _shapeOrig, now), _page, alreadyDone: true);
                        if (_activeShape.Kind == ShapeKind.Table)
                            ReflowTableCells(_activeShape);   // cells follow their table (#40)
                        changed = true;
                    }
                    _movingShape = _resizingShape = false;
                    break;
                }
                if (_scalingSel)
                {
                    if (Math.Abs(_scaleFactor - 1f) > 0.01f && _scaleStrokes != null && _page != null)
                    {
                        UndoManager.Push(new ScaleMixedAction(
                            _scaleStrokes, _scaleShapes!, _scaleTexts!,
                            _scaleAnchor.X, _scaleAnchor.Y, _scaleFactor), _page, alreadyDone: true);
                        changed = true;
                    }
                    _scalingSel = false;
                    _scaleStrokes = null;
                    _scaleShapes = null;
                    _scaleTexts = null;
                    RebuildTextLayer();
                    RecomputeSelectionBounds();
                    break;
                }
                if (_movingSel)
                {
                    if (Math.Abs(_moveDx) > 0.5f || Math.Abs(_moveDy) > 0.5f)
                    {
                        var acts = new List<IPageAction>();
                        if (_selected.Count > 0) acts.Add(new MoveStrokesAction(_selected.ToList(), _moveDx, _moveDy));
                        if (_selShapes.Count > 0) acts.Add(new MoveShapesAction(_selShapes.ToList(), _moveDx, _moveDy));
                        if (_selTexts.Count > 0) acts.Add(new MoveTextsAction(_selTexts.ToList(), _moveDx, _moveDy));
                        if (acts.Count > 0)
                        {
                            UndoManager.Push(new CompositeAction(acts, "Move selection"), _page);
                            changed = true;
                        }
                    }
                    _movingSel = false;
                    _moveDx = _moveDy = 0;
                    _textMoveOrig.Clear();
                    if (_selTexts.Count > 0) RebuildTextLayer(); // resync moved text to model
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
        _rotatingShape = false;
        _rectSelect = false;
        _barrelGesture = false;
        _barrelMoved = false;
        _holdTimer.Stop();
    }

    private List<StrokePoint> BuildRulerPoints(Vector2 start, Vector2 end)
    {
        // Line locked to the ruler's exact angle (any degree), through the
        // point where the pen went down.
        double r = RulerAngle * Math.PI / 180.0;
        var dir = new Vector2((float)Math.Cos(r), (float)Math.Sin(r));
        var d = end - start;
        if (d.Length() < 2) return new List<StrokePoint> { new(start.X, start.Y, 0.5f) };
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

    private void DrawRuler(CanvasDrawingSession ds, Color bg)
    {
        if (!RulerMode) return;
        var center = ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        double r = RulerAngle * Math.PI / 180.0;
        var dir = new Vector2((float)Math.Cos(r), (float)Math.Sin(r));
        var perp = new Vector2(-dir.Y, dir.X);
        float half = (float)(Math.Max(ActualWidth, ActualHeight) / ViewZoom);
        float width = 60f / ViewZoom;

        var bodyColor = ColorUtil.IsDark(bg) ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(46, 60, 80, 120);
        var edgeColor = Color.FromArgb(210, 217, 119, 87);
        var a1 = center - dir * half;
        var a2 = center + dir * half;
        var e1 = a1 + perp * width;
        var e2 = a2 + perp * width;
        using (var pb = new CanvasPathBuilder(_canvas))
        {
            pb.BeginFigure(a1);
            pb.AddLine(a2);
            pb.AddLine(e2);
            pb.AddLine(e1);
            pb.EndFigure(CanvasFigureLoop.Closed);
            using var geo = CanvasGeometry.CreatePath(pb);
            ds.FillGeometry(geo, bodyColor);
        }
        ds.DrawLine(a1, a2, edgeColor, 2.5f / ViewZoom); // the straightedge

        var tickColor = ColorUtil.IsDark(bg) ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(150, 40, 40, 40);
        const float spacing = 40f;
        int count = (int)(half / spacing);
        for (int i = -count; i <= count; i++)
        {
            var p = center + dir * (i * spacing);
            float tl = (i % 5 == 0) ? 14f : 8f;
            ds.DrawLine(p, p + perp * (tl / ViewZoom), tickColor, 1f / ViewZoom);
        }
        // degree readout in a bubble at the centre of the ruler
        string deg = $"{(((RulerAngle % 180) + 180) % 180):0}°";
        float bw = 70f / ViewZoom, bh = 34f / ViewZoom;
        var br = new Rect(center.X - bw / 2, center.Y - bh / 2, bw, bh);
        ds.FillRoundedRectangle(br, 9f / ViewZoom, 9f / ViewZoom, Color.FromArgb(235, 28, 28, 32));
        ds.DrawRoundedRectangle(br, 9f / ViewZoom, 9f / ViewZoom, edgeColor, 1.5f / ViewZoom);
        using var bf = new CanvasTextFormat
        {
            FontSize = 16f / ViewZoom,
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };
        ds.DrawText(deg, br, Colors.White, bf);
    }

    /// <summary>
    /// Cleans a freshly drawn stroke so it doesn't leave a stray dot at either
    /// end: trims points that sit on top of their neighbour (a near-zero-length
    /// end segment paints a full round cap), and replaces the very first/last
    /// pressure with its neighbour's so a hard tap-down or lift doesn't blob.
    /// </summary>
    private static List<StrokePoint> FinalizeStroke(List<StrokePoint> pts)
    {
        while (pts.Count >= 2 && Coincident(pts[^1], pts[^2])) pts.RemoveAt(pts.Count - 1);
        while (pts.Count >= 2 && Coincident(pts[0], pts[1])) pts.RemoveAt(0);
        if (pts.Count >= 3)
        {
            pts[0].Pressure = pts[1].Pressure;
            pts[^1].Pressure = pts[^2].Pressure;
        }
        return pts;
    }

    private static bool Coincident(StrokePoint a, StrokePoint b)
        => Math.Abs(a.X - b.X) < 0.6f && Math.Abs(a.Y - b.Y) < 0.6f;

    // =======================================================================
    // Erasing
    // =======================================================================
    private void EraseAt(Vector2 from, Vector2 to)
    {
        if (_page == null) return;
        // shapes are erased whole in either mode — but images are never erased
        // (move/delete them with the selection tools instead)
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var sh = _page.Shapes[i];
            if (sh.Kind == ShapeKind.Image) continue;
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
            // Walk the live list backwards so the index is the stroke's real
            // position (no IndexOf) and freshly inserted fragments — placed at
            // i and above — are never re-examined this pass.
            for (int i = _page.Strokes.Count - 1; i >= 0; i--)
            {
                var s = _page.Strokes[i];

                bool any = false;
                foreach (var p in s.Points)
                    if (GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) <= r) { any = true; break; }
                if (!any) continue;

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

                _page.Strokes.RemoveAt(i);
                // A fragment from earlier in this gesture: its original was
                // already recorded, so only record genuinely original strokes.
                if (!_gestureFragments.Remove(s)) _eraseRemoved.Add((i, s));

                int insertAt = i;
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
        _selShapes.Clear();
        _selShapeSet.Clear();
        _selTexts.Clear();
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
        foreach (var sh in _page.Shapes)
        {
            var r = ShapeBounds(sh);
            var c = new Vector2((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2));
            if (GeometryUtil.PointInPolygon(c, poly)) { _selShapes.Add(sh); _selShapeSet.Add(sh); }
        }
        foreach (var t in _page.Texts)
        {
            double w = 180, h = 40;
            if (_textUi.TryGetValue(t.Id, out var ui))
            {
                if (ui.Container.ActualWidth > 0) w = ui.Container.ActualWidth;
                if (ui.Container.ActualHeight > 0) h = ui.Container.ActualHeight;
            }
            var c = new Vector2((float)(t.X + w / 2), (float)(t.Y + h / 2));
            if (GeometryUtil.PointInPolygon(c, poly)) _selTexts.Add(t);
        }
        _activeShape = null; // a multi-selection supersedes the single active shape
        RecomputeSelectionBounds();
    }

    private void RecomputeSelectionBounds()
    {
        if (!HasMultiSelection) { _selBounds = Rect.Empty; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Inc(double x, double y)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        foreach (var s in _selected)
            foreach (var p in s.Points) Inc(p.X, p.Y);
        foreach (var sh in _selShapes)
        {
            var r = ShapeBounds(sh);
            Inc(r.Left, r.Top); Inc(r.Right, r.Bottom);
        }
        foreach (var t in _selTexts)
        {
            double w = 180, h = 40;
            if (_textUi.TryGetValue(t.Id, out var ui))
            {
                if (ui.Container.ActualWidth > 0) w = ui.Container.ActualWidth;
                if (ui.Container.ActualHeight > 0) h = ui.Container.ActualHeight;
            }
            Inc(t.X, t.Y); Inc(t.X + w, t.Y + h);
        }
        if (minX == double.MaxValue) { _selBounds = Rect.Empty; return; }
        _selBounds = new Rect(minX - 8, minY - 8, (maxX - minX) + 16, (maxY - minY) + 16);
    }

    // =======================================================================
    // Copy / paste (canvas objects)
    // =======================================================================
    public bool HasCanvasSelection => HasMultiSelection || _activeShape != null;
    public static bool HasCanvasClipboard =>
        _clipStrokes is { Count: > 0 } || _clipShapes is { Count: > 0 } || _clipTexts is { Count: > 0 };

    private static PenStroke CloneStroke(PenStroke s) =>
        s.CloneWithPoints(s.Points.Select(p => new StrokePoint(p.X, p.Y, p.Pressure)).ToList());

    private static ShapeElement CloneShape(ShapeElement s) => new()
    {
        Kind = s.Kind, X = s.X, Y = s.Y, W = s.W, H = s.H,
        Color = s.Color, Size = s.Size, ImagePath = s.ImagePath, Rotation = s.Rotation
    };

    private static TextElement CloneText(TextElement t) => new()
    {
        X = t.X, Y = t.Y, Width = t.Width, Rtf = t.Rtf, Rotation = t.Rotation
    };

    /// <summary>Copies the current multi-selection (strokes, shapes, text) or active shape.</summary>
    public void CopySelection()
    {
        CommitActiveSelection();
        FlushTexts();
        if (HasMultiSelection)
        {
            _clipStrokes = _selected.Select(CloneStroke).ToList();
            _clipShapes = _selShapes.Select(CloneShape).ToList();
            _clipTexts = _selTexts.Select(CloneText).ToList();
        }
        else if (_activeShape != null)
        {
            _clipShapes = new List<ShapeElement> { CloneShape(_activeShape) };
            _clipStrokes = null;
            _clipTexts = null;
        }
    }

    /// <summary>Renders the current selection into an image buffer so it can be pasted elsewhere as an image.</summary>
    public async Task<(byte[] Pixels, int Width, int Height)?> CaptureSelectionAsync()
    {
        if (!HasMultiSelection && _activeShape == null) return null;

        Windows.Foundation.Rect bounds = _selBounds;
        if (!HasMultiSelection && _activeShape != null)
        {
            bounds = ShapeBounds(_activeShape);
        }

        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return null;

        double pad = 10;
        bounds.X -= pad; bounds.Y -= pad; bounds.Width += pad * 2; bounds.Height += pad * 2;

        int width = (int)Math.Ceiling(bounds.Width);
        int height = (int)Math.Ceiling(bounds.Height);

        if (width <= 0 || height <= 0 || width > 4000 || height > 4000) return null;

        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        using var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device, width, height, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ds.Transform = System.Numerics.Matrix3x2.CreateTranslation((float)-bounds.X, (float)-bounds.Y);

            if (_activeShape != null)
            {
                DrawShape(ds, _activeShape);
            }
            else
            {
                foreach (var sh in _selShapes) DrawShape(ds, sh);
                foreach (var s in _selected) DrawStroke(ds, _canvas, s, System.Numerics.Vector2.Zero, null);
            }
        }
        var pixels = rt.GetPixelBytes();
        return (pixels, width, height);
    }

    public void CommitActiveSelection()
    {
        if (_lasso is { Count: > 2 })
        {
            SelectWithLasso(_lasso);
            _lasso = null;
            _canvas.Invalidate();
        }
    }

    /// <summary>Pastes the canvas clipboard so its top-left lands at <paramref name="world"/>.</summary>
    public void PasteCanvasAt(Vector2 world)
    {
        if (_page == null) return;
        bool any = _clipStrokes is { Count: > 0 } || _clipShapes is { Count: > 0 } || _clipTexts is { Count: > 0 };
        if (!any) return;

        double minX = double.MaxValue, minY = double.MaxValue;
        if (_clipStrokes != null)
            foreach (var s in _clipStrokes)
                foreach (var p in s.Points) { if (p.X < minX) minX = p.X; if (p.Y < minY) minY = p.Y; }
        if (_clipShapes != null)
            foreach (var s in _clipShapes) { minX = Math.Min(minX, Math.Min(s.X, s.X + s.W)); minY = Math.Min(minY, Math.Min(s.Y, s.Y + s.H)); }
        if (_clipTexts != null)
            foreach (var t in _clipTexts) { minX = Math.Min(minX, t.X); minY = Math.Min(minY, t.Y); }
        if (minX == double.MaxValue) { minX = world.X; minY = world.Y; }
        double dx = world.X - minX, dy = world.Y - minY;

        var ns = (_clipStrokes ?? new()).Select(s =>
        {
            var c = CloneStroke(s);
            foreach (var p in c.Points) { p.X += (float)dx; p.Y += (float)dy; }
            return c;
        }).ToList();
        var nsh = (_clipShapes ?? new()).Select(s => { var c = CloneShape(s); c.X += dx; c.Y += dy; return c; }).ToList();
        var nt = (_clipTexts ?? new()).Select(t => { var c = CloneText(t); c.X += dx; c.Y += dy; return c; }).ToList();

        UndoManager.Push(new AddMixedAction(ns, nsh, nt), _page);

        _selected.Clear(); _selectedSet.Clear();
        _selShapes.Clear(); _selShapeSet.Clear();
        _selTexts.Clear();
        foreach (var s in ns) { _selected.Add(s); _selectedSet.Add(s); }
        foreach (var s in nsh) { _selShapes.Add(s); _selShapeSet.Add(s); }
        foreach (var t in nt) _selTexts.Add(t);
        _activeShape = null;
        RebuildTextLayer();
        RecomputeSelectionBounds();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    /// <summary>Pastes the canvas clipboard near the centre of the visible page
    /// (used by the Ctrl+V keyboard shortcut, which has no click point).</summary>
    public void PasteCanvasAtViewCenter()
    {
        var c = ToWorld(new Vector2((float)ActualWidth / 2 - 40, (float)ActualHeight / 2 - 40));
        PasteCanvasAt(c);
    }

    /// <summary>Inserts an image with its top-left at the given world point.</summary>
    public void InsertImageAt(string path, double pixelW, double pixelH, Vector2 topLeftWorld)
    {
        if (_page == null) return;
        CancelPendingText();
        double scale = Math.Min(1.0, 520.0 / Math.Max(1, Math.Max(pixelW, pixelH)));
        double w = Math.Max(48, pixelW * scale), h = Math.Max(48, pixelH * scale);
        var s = new ShapeElement
        {
            Kind = ShapeKind.Image, ImagePath = path,
            X = topLeftWorld.X, Y = topLeftWorld.Y, W = w, H = h, Size = 0
        };
        UndoManager.Push(new AddShapeAction(s), _page);
        _activeShape = s;
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // Static ink cache (phase 3 #43): on pages with thousands of strokes the
    // settled ink is rendered once into an offscreen bitmap covering ~3x the
    // viewport, then blitted each frame. Rebuilt when content changes, zoom
    // drifts, or the view pans outside the cached area.
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

        // Visible world rectangle — anything fully outside it is skipped so pages
        // with thousands of strokes stay smooth (only on-screen content is drawn).
        var vTL = ToWorld(new Vector2(0, 0));
        var vBR = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
        float visMinX = vTL.X, visMinY = vTL.Y, visMaxX = vBR.X, visMaxY = vBR.Y;

        foreach (var sh in _page.Shapes)
        {
            var sb = ShapeBounds(sh);
            if (sb.Right < visMinX - 8 || sb.Left > visMaxX + 8 ||
                sb.Bottom < visMinY - 8 || sb.Top > visMaxY + 8) continue;
            if (_movingSel && _selShapeSet.Contains(sh))
            {
                var prev = ds.Transform;
                ds.Transform = Matrix3x2.CreateTranslation(_moveDx, _moveDy) * prev;
                DrawShape(ds, sh);
                ds.Transform = prev;
            }
            else
            {
                DrawShape(ds, sh);
            }
        }

        // Big pages draw settled ink from the offscreen cache (#43); anything
        // that offsets strokes (replay, selection move, free space) falls back
        // to the classic per-stroke path so offsets stay live.
        bool cacheEligible = !_replaying && !_movingSel && !_spacing &&
                             _page.Strokes.Count >= InkCacheThreshold && AudioPlayheadPosition == null;
        if (!(cacheEligible && TryDrawInkCache(ds, sender, visMinX, visMinY, visMaxX, visMaxY)))
        {
            PenStroke? activeStroke = null;
            if (AudioPlayheadPosition != null && RecordingStartTicks != null)
            {
                long elapsedTicks = AudioPlayheadPosition.Value.Ticks;
                long bestDiff = long.MaxValue;
                foreach (var s in _page.Strokes)
                {
                    long offsetTicks = s.CreatedTicks - RecordingStartTicks.Value;
                    if (offsetTicks >= 0 && offsetTicks <= elapsedTicks)
                    {
                        long diff = elapsedTicks - offsetTicks;
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            activeStroke = s;
                        }
                    }
                }
            }

            int idx = 0;
            foreach (var s in _page.Strokes)
            {
                var off = Vector2.Zero;
                if (_movingSel && _selectedSet.Contains(s)) off = new Vector2(_moveDx, _moveDy);
                else if (_spacing && s.Points.Count > 0 && s.MinY >= _spaceY) off = new Vector2(0, (float)_spaceDelta);

                if (AudioPlayheadPosition != null && RecordingStartTicks != null)
                {
                    long strokeOffsetTicks = s.CreatedTicks - RecordingStartTicks.Value;
                    if (strokeOffsetTicks > AudioPlayheadPosition.Value.Ticks) continue;
                }

                if (_replaying)
                {
                    if (idx > _replayStroke) break;
                    int? limit = idx == _replayStroke ? _replayPoint : null;
                    DrawStroke(ds, sender, s, off, limit);
                }
                else
                {
                    s.GetBounds(out float bx0, out float by0, out float bx1, out float by1);
                    float pad = s.Size * 2.5f + 6f;
                    if (bx1 + off.X >= visMinX - pad && bx0 + off.X <= visMaxX + pad &&
                        by1 + off.Y >= visMinY - pad && by0 + off.Y <= visMaxY + pad)
                    {
                        if (s == activeStroke)
                        {
                            DrawStrokeGlow(ds, sender, s, off);
                        }
                        DrawStroke(ds, sender, s, off, null);
                    }
                }
                idx++;
            }
        }

        if (_gestureTool == ToolType.Pen)
        {
            var temp = new PenStroke
            {
                Pen = Pen,
                Color = ColorUtil.ToHex(PenColor),
                Size = PenSize,
                Sens = PenSensitivity,
                Points = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : (_wet ?? new List<StrokePoint>()),
                PressureCurve = PenPressureCurve
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

        if (_activeShape != null && !_replaying)
        {
            DrawShapeSelection(ds, _activeShape, accent, uiScale);

            // Word-like "+" adders for tables: top = column, left = row (#49)
            if (_activeShape.Kind == ShapeKind.Table)
            {
                var (cb, rb) = TablePlusCentres(_activeShape);
                float r = 11f / ViewZoom;
                foreach (var centre in new[] { cb, rb })
                {
                    ds.FillCircle(centre, r, Colors.White);
                    ds.DrawCircle(centre, r, accent, uiScale);
                    float a = r * 0.5f;
                    ds.DrawLine(centre.X - a, centre.Y, centre.X + a, centre.Y, accent, uiScale * 1.4f);
                    ds.DrawLine(centre.X, centre.Y - a, centre.X, centre.Y + a, accent, uiScale * 1.4f);
                }
            }
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

        if (HasMultiSelection && !_selBounds.IsEmpty)
        {
            var r = new Rect(_selBounds.X + _moveDx, _selBounds.Y + _moveDy, _selBounds.Width, _selBounds.Height);
            ds.FillRectangle(r, Color.FromArgb(26, 217, 119, 87));
            ds.DrawRectangle(r, accent, uiScale, _dashStyle);

            // corner handles: drag to scale the whole selection (#54)
            if (!_movingSel)
            {
                float hs = 5.5f / ViewZoom;
                foreach (var cpt in SelCorners())
                {
                    ds.FillRectangle(new Rect(cpt.X - hs, cpt.Y - hs, hs * 2, hs * 2), Colors.White);
                    ds.DrawRectangle(new Rect(cpt.X - hs, cpt.Y - hs, hs * 2, hs * 2), accent, uiScale);
                }
            }
        }

        if (!_replaying) DrawRuler(ds, bg);

        // Eraser cursor: shown for the Eraser tool, while a pen hovers with its
        // eraser button held, and during an active erase gesture.
        bool eraserCursor = Tool == ToolType.Eraser || _penEraserHover || _gestureTool == ToolType.Eraser;
        if (eraserCursor && _hover.HasValue && !_replaying)
        {
            var ring = ColorUtil.IsDark(bg) ? Colors.White : Color.FromArgb(255, 70, 70, 70);
            ds.DrawCircle(_hover.Value, EraserRadius, ring, uiScale, _dashStyle);
            // a small centre dot so the cursor clearly reads as "erase"
            ds.FillCircle(_hover.Value, Math.Max(1.5f, 2f / ViewZoom), ring);

            // object-eraser preview: tint the stroke that would be removed (#53)
            if (EraserMode == EraserMode.Object && _gestureTool != ToolType.Eraser)
            {
                var victim = FindStrokeNear(_hover.Value, EraserRadius);
                if (victim != null)
                {
                    victim.GetBounds(out float vx0, out float vy0, out float vx1, out float vy1);
                    var vr = new Rect(vx0 - 4, vy0 - 4, vx1 - vx0 + 8, vy1 - vy0 + 8);
                    ds.FillRectangle(vr, Color.FromArgb(34, 220, 70, 60));
                    ds.DrawRectangle(vr, Color.FromArgb(150, 220, 70, 60), uiScale, _dashStyle);
                }
            }
        }

        // Blinking text caret: where a Text-tool tap will start a box once typed.
        if (_pendingTextPos is { } caret && _caretOn && !_replaying)
        {
            var caretColor = ColorUtil.IsDark(bg) ? Colors.White : Color.FromArgb(255, 20, 20, 19);
            ds.DrawLine(new Vector2(caret.X, caret.Y - 13), new Vector2(caret.X, caret.Y + 13),
                        caretColor, Math.Max(1.4f, 1.6f / ViewZoom));
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

    private void DrawStrokeGlow(CanvasDrawingSession ds, ICanvasResourceCreator rc, PenStroke s, Vector2 offset)
    {
        var pts = s.Points;
        int n = pts.Count;
        if (n == 0) return;

        var color = Color.FromArgb(80, 217, 119, 87); // beautiful semi-translucent orange/red highlight
        float hw = s.Size * 3.5f;

        if (n == 1)
        {
            ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, hw / 2, color);
            return;
        }
        DrawPolyline(ds, rc, pts, n, offset, color, hw, _roundStyle);
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
            // single continuous geometry with FLAT caps -> no rounded end blobs
            DrawPolyline(ds, rc, pts, n, offset, color, hw, _flatStyle);
            return;
        }

        if (s.Pen == PenType.Pencil)
        {
            // Graphite: one continuous soft core (no beaded round-cap dots) plus
            // two faint offset passes for grain. Width follows average pressure.
            float prAvg = 0;
            for (int i = 0; i < n; i++) prAvg += pts[i].Pressure;
            prAvg = n > 0 ? prAvg / n : 0.5f;
            if (prAvg <= 0.01f) prAvg = 0.5f;
            float sens = s.Sens <= 0.01f ? 1f : s.Sens;
            float pw = Math.Max(0.6f, s.Size * (0.45f + 0.7f * sens * prAvg));
            if (n == 1)
            {
                var c1 = color; c1.A = 150;
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, Math.Max(0.6f, pw / 2), c1);
                return;
            }
            var core = color; core.A = 145;
            DrawPolyline(ds, rc, pts, n, offset, core, pw, _roundStyle);
            var grain = color; grain.A = 55;
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(0.5f, 0.45f), grain, pw * 0.5f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(-0.45f, -0.4f), grain, pw * 0.45f, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Crayon)
        {
            // waxy, thick and grainy — heavier and more textured than the pencil
            float prAvg = 0;
            for (int i = 0; i < n; i++) prAvg += pts[i].Pressure;
            prAvg = n > 0 ? prAvg / n : 0.5f;
            if (prAvg <= 0.01f) prAvg = 0.5f;
            float sens = s.Sens <= 0.01f ? 1f : s.Sens;
            float cw = Math.Max(1.2f, s.Size * (0.9f + 0.7f * sens * prAvg));
            if (n == 1)
            {
                var c1 = color; c1.A = 220;
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, Math.Max(1f, cw / 2), c1);
                return;
            }
            var coreC = color; coreC.A = 210;
            DrawPolyline(ds, rc, pts, n, offset, coreC, cw, _roundStyle);
            var gr = color; gr.A = 70;
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(0.8f, 0.7f), gr, cw * 0.55f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(-0.7f, -0.6f), gr, cw * 0.5f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(0.2f, -0.8f), gr, cw * 0.4f, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Watercolor)
        {
            // soft translucent wash that builds up where strokes overlap
            var wc = color; wc.A = 70;
            float ww = Math.Max(1.5f, s.Size * 2.2f);
            if (n == 1)
            {
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, ww / 2, wc);
                return;
            }
            var wc2 = color; wc2.A = 42;
            DrawPolyline(ds, rc, pts, n, offset, wc2, ww * 1.5f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset, wc, ww, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Monoline)
        {
            // perfectly even technical line, independent of pressure
            float mw = Math.Max(0.6f, s.Size);
            if (n == 1)
            {
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, mw / 2, color);
                return;
            }
            DrawPolyline(ds, rc, pts, n, offset, color, mw, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Marker) color.A = 235;
        else if (s.Pen == PenType.Ballpoint) color.A = 240;

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


    // Strokes a single continuous polyline through the points (used for pens that
    // should read as one smooth line rather than a chain of round-capped dots).
    private static void DrawPolyline(CanvasDrawingSession ds, ICanvasResourceCreator rc,
        List<StrokePoint> pts, int n, Vector2 offset, Color color, float width, CanvasStrokeStyle style)
    {
        if (n < 2) return;
        using var pb = new CanvasPathBuilder(rc);
        pb.BeginFigure(new Vector2(pts[0].X, pts[0].Y) + offset);
        for (int i = 1; i < n; i++)
            pb.AddLine(new Vector2(pts[i].X, pts[i].Y) + offset);
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, color, width, style);
    }

    private static float SegmentWidth(PenStroke s, StrokePoint a, StrokePoint b, int index)
    {
        float pr = (a.Pressure + b.Pressure) * 0.5f;
        if (pr <= 0.01f) pr = 0.5f;
        if (s.PressureCurve != null && s.PressureCurve.Count >= 3)
        {
            float t = Math.Clamp(pr, 0f, 1f);
            float mid = s.PressureCurve[1];
            pr = 2f * (1f - t) * t * mid + t * t;
        }
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
                // Broad-edge nib held ~40°: strong thick (down/perpendicular) vs
                // thin (across the nib) contrast, with a gentle, wet pressure
                // response — reads like real fountain-pen calligraphy.
                double ang = Math.Atan2(b.Y - a.Y, b.X - a.X);
                float nib = (float)Math.Abs(Math.Sin(ang - 0.7));   // 0 thin .. 1 thick
                float contrast = 0.22f + 1.15f * nib;
                float press = 0.78f + 0.5f * sens * pr;             // gentle flow, not flex
                return Math.Max(0.5f, s.Size * 0.62f * contrast * press);
            }
            case PenType.Rollerball:
                return Math.Max(0.4f, s.Size * (0.7f + 0.5f * sens * pr));
            case PenType.Gel:
                return Math.Max(0.6f, s.Size * (0.85f + 0.5f * sens * pr));
            case PenType.Ballpoint:
                return Math.Max(0.35f, s.Size * (0.55f + 0.6f * sens * pr));
            case PenType.FeltTip:
                return Math.Max(1f, s.Size * (0.95f + 0.35f * sens * pr));
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

        // average radial error from a perfect ellipse (used as the smooth fallback)
        double ellErr = 0;
        foreach (var p in pts)
        {
            float dx = (p.X - cx) / rx, dy = (p.Y - cy) / ry;
            ellErr += Math.Abs(Math.Sqrt(dx * dx + dy * dy) - 1);
        }
        ellErr /= pts.Count;

        // Corner-based classification: distil the stroke to its dominant vertices,
        // then decide by corner count + geometry. This recognises triangles,
        // right-triangles and diamonds, and stops squares becoming circles.
        var poly = new List<Vector2>(pts.Count);
        foreach (var p in pts) poly.Add(new Vector2(p.X, p.Y));
        var corners = DominantCorners(poly, Math.Max(w, h));
        int n = corners.Count;
        bool nearEqual = Math.Abs(w - h) <= 0.18f * Math.Max(w, h);

        void Eq() { if (nearEqual) { float m = Math.Max(w, h); cx = (minX + maxX) / 2; cy = (minY + maxY) / 2; w = h = m; } }

        if (n == 3)
        {
            var kind = HasRightAngle(corners) ? ShapeKind.RightTriangle : ShapeKind.Triangle;
            return (MakeShape(kind, cx - w / 2, cy - h / 2, w, h), false);
        }
        if (n == 4)
        {
            if (IsDiamond(corners, minX, minY, maxX, maxY))
            {
                Eq();
                return (MakeShape(ShapeKind.Diamond, cx - w / 2, cy - h / 2, w, h), nearEqual);
            }
            if (IsAxisRect(corners, minX, minY, maxX, maxY))
            {
                Eq();
                return (MakeShape(ShapeKind.Rect, cx - w / 2, cy - h / 2, w, h), nearEqual);
            }
            // a quad that's neither axis-aligned nor a diamond: treat as ellipse if
            // the outline is smooth, otherwise a rectangle.
            if (ellErr < 0.16) { Eq(); return (MakeShape(ShapeKind.Ellipse, cx - w / 2, cy - h / 2, w, h), nearEqual); }
            Eq();
            return (MakeShape(ShapeKind.Rect, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        if (n == 5)
        {
            Eq();
            return (MakeShape(ShapeKind.Pentagon, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        if (n == 6)
        {
            Eq();
            return (MakeShape(ShapeKind.Hexagon, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        // smooth, many small corners -> ellipse
        if (ellErr < 0.22)
        {
            Eq();
            return (MakeShape(ShapeKind.Ellipse, cx - w / 2, cy - h / 2, w, h), nearEqual);
        }
        return null;
    }

    // ---- corner detection helpers (shape recognition) ----
    private static List<Vector2> DominantCorners(List<Vector2> poly, float size)
    {
        if (poly.Count < 3) return new List<Vector2>(poly);
        Vector2 c = Vector2.Zero;
        foreach (var p in poly) c += p;
        c /= poly.Count;
        // rotate so the path starts at the point farthest from the centroid (a
        // likely true corner) — RDP keeps its endpoints, so this avoids cutting a
        // real corner at the seam.
        int start = 0; float bd = -1;
        for (int i = 0; i < poly.Count; i++)
        {
            float d = Vector2.Distance(poly[i], c);
            if (d > bd) { bd = d; start = i; }
        }
        var rot = new List<Vector2>(poly.Count + 1);
        for (int i = 0; i < poly.Count; i++) rot.Add(poly[(start + i) % poly.Count]);
        rot.Add(rot[0]);

        float eps = Math.Max(7f, size * 0.075f);
        var simp = Rdp(rot, eps);
        if (simp.Count > 1 && Vector2.Distance(simp[0], simp[^1]) < eps) simp.RemoveAt(simp.Count - 1);

        float mergeDist = size * 0.16f;
        var merged = new List<Vector2>();
        foreach (var p in simp)
            if (merged.Count == 0 || Vector2.Distance(merged[^1], p) > mergeDist) merged.Add(p);
        if (merged.Count >= 2 && Vector2.Distance(merged[0], merged[^1]) < mergeDist)
            merged.RemoveAt(merged.Count - 1);
        return merged;
    }

    private static List<Vector2> Rdp(List<Vector2> pts, float eps)
    {
        if (pts.Count < 3) return new List<Vector2>(pts);
        int idx = 0; float dmax = 0;
        for (int i = 1; i < pts.Count - 1; i++)
        {
            float d = GeometryUtil.DistToSegment(pts[i], pts[0], pts[^1]);
            if (d > dmax) { dmax = d; idx = i; }
        }
        if (dmax > eps)
        {
            var left = Rdp(pts.GetRange(0, idx + 1), eps);
            var right = Rdp(pts.GetRange(idx, pts.Count - idx), eps);
            left.RemoveAt(left.Count - 1);
            left.AddRange(right);
            return left;
        }
        return new List<Vector2> { pts[0], pts[^1] };
    }

    private static float AngleDeg(Vector2 a, Vector2 b, Vector2 c)
    {
        var u = a - b; var w = c - b;
        if (u.LengthSquared() < 1e-3f || w.LengthSquared() < 1e-3f) return 180f;
        float dot = Math.Clamp(Vector2.Dot(Vector2.Normalize(u), Vector2.Normalize(w)), -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    private static bool HasRightAngle(List<Vector2> v)
    {
        int n = v.Count;
        for (int i = 0; i < n; i++)
        {
            float ang = AngleDeg(v[(i - 1 + n) % n], v[i], v[(i + 1) % n]);
            if (ang >= 74 && ang <= 106) return true;
        }
        return false;
    }

    private static bool IsDiamond(List<Vector2> v, float minX, float minY, float maxX, float maxY)
    {
        float w = maxX - minX, h = maxY - minY;
        float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
        float tol = 0.2f * Math.Max(w, h);
        var mids = new[]
        {
            new Vector2(cx, minY), new Vector2(maxX, cy),
            new Vector2(cx, maxY), new Vector2(minX, cy)
        };
        foreach (var m in mids)
            if (!v.Any(p => Vector2.Distance(p, m) <= tol)) return false;
        return true;
    }

    private static bool IsAxisRect(List<Vector2> v, float minX, float minY, float maxX, float maxY)
    {
        float w = maxX - minX, h = maxY - minY;
        float tol = 0.22f * Math.Max(w, h);
        var cor = new[]
        {
            new Vector2(minX, minY), new Vector2(maxX, minY),
            new Vector2(maxX, maxY), new Vector2(minX, maxY)
        };
        foreach (var k in cor)
            if (!v.Any(p => Vector2.Distance(p, k) <= tol)) return false;
        return true;
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
        if (s.Kind is ShapeKind.Line or ShapeKind.Arrow)
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
        if (s.Kind is ShapeKind.Line or ShapeKind.Arrow)
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
        if (s.Kind is ShapeKind.Line or ShapeKind.Arrow)
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

    // ---- rotation helpers (#20) ----
    private static Vector2 ShapeCenter(ShapeElement s)
    {
        double x = Math.Min(s.X, s.X + s.W), y = Math.Min(s.Y, s.Y + s.H);
        return new Vector2((float)(x + Math.Abs(s.W) / 2), (float)(y + Math.Abs(s.H) / 2));
    }

    private static Vector2 RotatePoint(Vector2 p, Vector2 c, double deg)
    {
        if (Math.Abs(deg) < 0.001) return p;
        double r = deg * Math.PI / 180.0;
        float cos = (float)Math.Cos(r), sin = (float)Math.Sin(r);
        var d = p - c;
        return new Vector2(c.X + d.X * cos - d.Y * sin, c.Y + d.X * sin + d.Y * cos);
    }

    // World point -> the shape's un-rotated local frame (for hit-testing).
    private static Vector2 ToShapeLocal(ShapeElement s, Vector2 p)
        => Math.Abs(s.Rotation) < 0.001 ? p : RotatePoint(p, ShapeCenter(s), -s.Rotation);

    // World position of the rotation handle (above the shape, in rotated space).
    private Vector2 RotateHandlePos(ShapeElement s)
    {
        var bb = ShapeBounds(s);
        var top = new Vector2((float)(bb.Left + bb.Width / 2), (float)bb.Top - 26f / ViewZoom);
        return RotatePoint(top, ShapeCenter(s), s.Rotation);
    }

    private bool HitRotateHandle(ShapeElement s, Vector2 pos, float tol)
        => s.Kind != ShapeKind.Table && Vector2.Distance(RotateHandlePos(s), pos) <= tol + 4f / ViewZoom;

    private static bool OnShapeBody(ShapeElement s, Vector2 pos, float tol)
    {
        var lp = ToShapeLocal(s, pos);
        return ShapeBounds(s).Contains(new Point(lp.X, lp.Y)) || DistToShapeOutline(s, pos) < tol;
    }

    private void BeginRotate(ShapeElement s, Vector2 pos)
    {
        _rotatingShape = true;
        _rotateCenter = ShapeCenter(s);
        _rotateStartShapeDeg = s.Rotation;
        _rotateStartPointerDeg = Math.Atan2(pos.Y - _rotateCenter.Y, pos.X - _rotateCenter.X) * 180.0 / Math.PI;
    }

    // True outline vertices of a polygon shape (used for drawing, hit-testing and
    // vertex resize handles). Bounds are normalised so negative W/H still work.
    private static Vector2[] PolygonVertices(ShapeElement s)
    {
        float x = (float)Math.Min(s.X, s.X + s.W), y = (float)Math.Min(s.Y, s.Y + s.H);
        float w = (float)Math.Abs(s.W), h = (float)Math.Abs(s.H);
        float cx = x + w / 2, cy = y + h / 2;
        switch (s.Kind)
        {
            case ShapeKind.Triangle:
                return new[] { new Vector2(cx, y), new Vector2(x, y + h), new Vector2(x + w, y + h) };
            case ShapeKind.RightTriangle:
                return new[] { new Vector2(x, y), new Vector2(x, y + h), new Vector2(x + w, y + h) };
            case ShapeKind.Diamond:
                return new[] { new Vector2(cx, y), new Vector2(x + w, cy), new Vector2(cx, y + h), new Vector2(x, cy) };
            case ShapeKind.Parallelogram:
            {
                float sx = w * 0.25f;
                return new[] { new Vector2(x + sx, y), new Vector2(x + w, y), new Vector2(x + w - sx, y + h), new Vector2(x, y + h) };
            }
            case ShapeKind.Trapezoid:
            {
                float sx = w * 0.22f;
                return new[] { new Vector2(x + sx, y), new Vector2(x + w - sx, y), new Vector2(x + w, y + h), new Vector2(x, y + h) };
            }
            case ShapeKind.Pentagon: return RegularPoly(cx, cy, w / 2, h / 2, 5, -MathF.PI / 2);
            case ShapeKind.Hexagon: return RegularPoly(cx, cy, w / 2, h / 2, 6, -MathF.PI / 2);
            case ShapeKind.Star: return StarPoly(cx, cy, w / 2, h / 2, 5, -MathF.PI / 2);
            default: // Rect and any bbox-based kind
                return new[] { new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h) };
        }
    }

    private static bool IsPolygonKind(ShapeKind k) =>
        k is ShapeKind.Rect or ShapeKind.Triangle or ShapeKind.RightTriangle or ShapeKind.Diamond
          or ShapeKind.Pentagon or ShapeKind.Hexagon or ShapeKind.Star
          or ShapeKind.Parallelogram or ShapeKind.Trapezoid;

    private static Vector2[] RegularPoly(float cx, float cy, float rx, float ry, int n, float start)
    {
        var v = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float ang = start + i * MathF.PI * 2 / n;
            v[i] = new Vector2(cx + rx * MathF.Cos(ang), cy + ry * MathF.Sin(ang));
        }
        return v;
    }

    private static Vector2[] StarPoly(float cx, float cy, float rx, float ry, int points, float start)
    {
        int n = points * 2;
        var v = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            float ang = start + i * MathF.PI / points;
            float f = (i % 2 == 0) ? 1f : 0.42f;
            v[i] = new Vector2(cx + rx * f * MathF.Cos(ang), cy + ry * f * MathF.Sin(ang));
        }
        return v;
    }

    // Vertices where resize handles are shown (#11): true polygon corners for
    // polygons, the bbox corners for ellipse/image/axes, endpoints for lines.
    private static Vector2[] HandleVertices(ShapeElement s)
    {
        if (s.Kind is ShapeKind.Line or ShapeKind.Arrow) return ShapeCorners(s);
        if (IsPolygonKind(s.Kind)) return PolygonVertices(s);
        return ShapeCorners(s); // ellipse / image / axes -> bbox corners
    }

    private static float PolyOutlineDist(Vector2[] v, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < v.Length; i++)
            best = Math.Min(best, GeometryUtil.DistToSegment(p, v[i], v[(i + 1) % v.Length]));
        return best;
    }

    private static float DistToShapeOutline(ShapeElement s, Vector2 p)
    {
        p = ToShapeLocal(s, p); // hit-test in the shape's un-rotated frame (#20)
        if (IsPolygonKind(s.Kind)) return PolyOutlineDist(PolygonVertices(s), p);
        switch (s.Kind)
        {
            case ShapeKind.Line:
            case ShapeKind.Arrow:
                return GeometryUtil.DistToSegment(p,
                    new Vector2((float)s.X, (float)s.Y),
                    new Vector2((float)(s.X + s.W), (float)(s.Y + s.H)));
            case ShapeKind.Image:
            {
                float rx = (float)Math.Min(s.X, s.X + s.W);
                float ry = (float)Math.Min(s.Y, s.Y + s.H);
                float rw = (float)Math.Abs(s.W);
                float rh = (float)Math.Abs(s.H);
                if (p.X >= rx && p.X <= rx + rw && p.Y >= ry && p.Y <= ry + rh)
                    return 0f;
                var i1 = new Vector2(rx, ry);
                var i2 = new Vector2(rx + rw, ry);
                var i3 = new Vector2(rx + rw, ry + rh);
                var i4 = new Vector2(rx, ry + rh);
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
        var lp = ToShapeLocal(s, pos); // test handles in the un-rotated frame (#20)
        if (s.Kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            var ep = ShapeCorners(s);
            if (Vector2.Distance(ep[0], lp) <= tol) return ep[1];
            if (Vector2.Distance(ep[1], lp) <= tol) return ep[0];
            return null;
        }
        var bbox = ShapeCorners(s);            // [TL, TR, BL, BR]
        foreach (var hpt in HandleVertices(s)) // true vertices (#11)
        {
            if (Vector2.Distance(hpt, lp) <= tol)
            {
                // resize the bbox from the corner diagonally opposite the grabbed vertex
                int nearest = 0; float bd = float.MaxValue;
                for (int i = 0; i < bbox.Length; i++)
                {
                    float d = Vector2.Distance(bbox[i], hpt);
                    if (d < bd) { bd = d; nearest = i; }
                }
                return bbox[bbox.Length - 1 - nearest];
            }
        }
        return null;
    }

    private void DrawShape(CanvasDrawingSession ds, ShapeElement s)
    {
        Matrix3x2 prevT = ds.Transform;
        bool rot = Math.Abs(s.Rotation) > 0.01;
        if (rot)
            ds.Transform = Matrix3x2.CreateRotation((float)(s.Rotation * Math.PI / 180.0), ShapeCenter(s)) * prevT;
        var color = ColorUtil.Parse(s.Color);
        float w = Math.Max(1f, s.Size);
        switch (s.Kind)
        {
            case ShapeKind.Line:
                ds.DrawLine((float)s.X, (float)s.Y, (float)(s.X + s.W), (float)(s.Y + s.H), color, w, _roundStyle);
                break;
            case ShapeKind.Arrow:
                DrawArrow(ds, new Vector2((float)s.X, (float)s.Y),
                          new Vector2((float)(s.X + s.W), (float)(s.Y + s.H)), color, w);
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
            case ShapeKind.RightTriangle:
            case ShapeKind.Diamond:
            case ShapeKind.Pentagon:
            case ShapeKind.Hexagon:
            case ShapeKind.Star:
            case ShapeKind.Parallelogram:
            case ShapeKind.Trapezoid:
                DrawPolygon(ds, PolygonVertices(s), color, w);
                break;
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
            case ShapeKind.Table:
            {
                var r = new Rect(s.X, s.Y, Math.Max(1, s.W), Math.Max(1, s.H));
                ds.DrawRectangle(r, color, w);
                float inner = Math.Max(1f, w * 0.75f);
                var cw = TableColWidths(s);
                var rh = TableRowHeights(s);
                
                double[] px = new double[cw.Length + 1];
                for (int i = 0; i < cw.Length; i++) px[i + 1] = px[i] + cw[i];
                double[] py = new double[rh.Length + 1];
                for (int i = 0; i < rh.Length; i++) py[i + 1] = py[i] + rh[i];

                var cellMap = new TextElement[rh.Length, cw.Length];
                if (_page == null) break;   // draw is only reachable with a page, but be safe
                foreach (var t in _page.Texts)
                {
                    if (t.TableId != s.Id) continue;
                    int col = Math.Clamp(t.TableCol, 0, cw.Length - 1);
                    int row = Math.Clamp(t.TableRow, 0, rh.Length - 1);
                    int colSpan = Math.Clamp(t.CellColSpan, 1, cw.Length - col);
                    int rowSpan = Math.Clamp(t.CellRowSpan, 1, rh.Length - row);
                    
                    for (int dr = 0; dr < rowSpan; dr++)
                        for (int dc = 0; dc < colSpan; dc++)
                            cellMap[row + dr, col + dc] = t;
                }

                for (int row = 0; row < rh.Length; row++)
                {
                    for (int col = 0; col < cw.Length; col++)
                    {
                        var t = cellMap[row, col];
                        if (t != null && (t.TableRow != row || t.TableCol != col)) continue;

                        int colSpan = t != null ? Math.Clamp(t.CellColSpan, 1, cw.Length - col) : 1;
                        int rowSpan = t != null ? Math.Clamp(t.CellRowSpan, 1, rh.Length - row) : 1;

                        double cellX = s.X + px[col];
                        double cellY = s.Y + py[row];
                        double cellW = px[col + colSpan] - px[col];
                        double cellH = py[row + rowSpan] - py[row];
                        var cellRect = new Rect(cellX, cellY, cellW, cellH);

                        if (t != null && !string.IsNullOrEmpty(t.FillColor))
                        {
                            ds.FillRectangle(cellRect, ColorUtil.Parse(t.FillColor));
                        }
                        else if (s.HeaderRow && row == 0)
                        {
                            ds.FillRectangle(cellRect, Color.FromArgb(40, 217, 119, 87));
                        }

                        var bColor = (t != null && !string.IsNullOrEmpty(t.BorderColor)) ? ColorUtil.Parse(t.BorderColor) : color;
                        var bWidth = (t != null && t.BorderWidth.HasValue) ? t.BorderWidth.Value : inner;

                        if (col + colSpan < cw.Length)
                        {
                            ds.DrawLine((float)(cellX + cellW), (float)cellY, (float)(cellX + cellW), (float)(cellY + cellH), bColor, bWidth);
                        }
                        if (row + rowSpan < rh.Length)
                        {
                            ds.DrawLine((float)cellX, (float)(cellY + cellH), (float)(cellX + cellW), (float)(cellY + cellH), bColor, bWidth);
                        }
                    }
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
        if (rot) ds.Transform = prevT;
    }

    private void DrawPolygon(CanvasDrawingSession ds, Vector2[] v, Color color, float w)
    {
        if (v.Length < 2) return;
        for (int i = 0; i < v.Length; i++)
            ds.DrawLine(v[i], v[(i + 1) % v.Length], color, w, _roundStyle);
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
        var c = ShapeCenter(s);
        var bb = ShapeBounds(s);
        var corners = new[]
        {
            new Vector2((float)bb.Left, (float)bb.Top),
            new Vector2((float)bb.Right, (float)bb.Top),
            new Vector2((float)bb.Right, (float)bb.Bottom),
            new Vector2((float)bb.Left, (float)bb.Bottom)
        };
        for (int i = 0; i < 4; i++)
            ds.DrawLine(RotatePoint(corners[i], c, s.Rotation),
                        RotatePoint(corners[(i + 1) % 4], c, s.Rotation), accent, uiScale, _dashStyle);

        float hs = 5.5f / ViewZoom;
        foreach (var v in HandleVertices(s))
        {
            var p = RotatePoint(v, c, s.Rotation);
            ds.FillRectangle(new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2), Colors.White);
            ds.DrawRectangle(new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2), accent, uiScale);
        }

        if (s.Kind != ShapeKind.Table)
        {
            // rotation handle (small circle on a stem above the shape)
            var topMid = RotatePoint(new Vector2((float)(bb.Left + bb.Width / 2), (float)bb.Top), c, s.Rotation);
            var rh = RotateHandlePos(s);
            ds.DrawLine(topMid, rh, accent, uiScale);
            ds.FillCircle(rh, 6f / ViewZoom, Colors.White);
            ds.DrawCircle(rh, 6f / ViewZoom, accent, uiScale);
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
        // With a blinking caret: the image's TOP-LEFT sits on the caret.
        // Otherwise it lands centred on the screen.
        bool atCaret = _pendingTextPos.HasValue;
        var c = _pendingTextPos ?? ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        CancelPendingText();
        var s = new ShapeElement
        {
            Kind = ShapeKind.Image,
            ImagePath = path,
            X = atCaret ? c.X : c.X - w / 2,
            Y = atCaret ? c.Y : c.Y - h / 2,
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
            case ShapeKind.Arrow: w = 240; h = 0; break;
            case ShapeKind.Rect: if (equalDims) { w = h = 170; } break;
            case ShapeKind.Ellipse: if (equalDims) { w = h = 170; } break;
            case ShapeKind.Triangle: w = 210; h = 180; break;
            case ShapeKind.RightTriangle: w = 200; h = 190; break;
            case ShapeKind.Diamond: w = h = 190; break;
            case ShapeKind.Pentagon: w = h = 200; break;
            case ShapeKind.Hexagon: w = h = 200; break;
            case ShapeKind.Star: w = h = 210; break;
            case ShapeKind.Parallelogram: w = 250; h = 150; break;
            case ShapeKind.Trapezoid: w = 250; h = 150; break;
            case ShapeKind.AxesXY: w = 280; h = 210; break;
            case ShapeKind.AxesXYZ: w = 240; h = 240; break;
        }
        var s = new ShapeElement
        {
            Kind = kind,
            Color = ColorUtil.ToHex(PenColor),
            Size = Math.Max(2f, PenSize * 0.9f)
        };
        if (kind is ShapeKind.Line or ShapeKind.Arrow)
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

    /// <summary>Tap with the Text tool: blink a caret here; the box is created
    /// lazily once the user types (or an image is pasted onto it).</summary>
    private void SetPendingText(Vector2 worldPos)
    {
        if (_page == null) return;
        _pendingTextPos = worldPos;
        _caretOn = true;
        _caretTimer.Start();
        IsTabStop = true;                  // so we receive the first character
        Focus(FocusState.Programmatic);
        _canvas.Invalidate();
    }

    private void CancelPendingText()
    {
        if (_pendingTextPos == null) return;
        _pendingTextPos = null;
        _caretOn = false;
        _caretTimer.Stop();
        IsTabStop = false;
        _canvas.Invalidate();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_pendingTextPos != null && e.Key == Windows.System.VirtualKey.Escape)
        {
            CancelPendingText();
            e.Handled = true;
        }
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        if (_pendingTextPos == null || _page == null) return;
        char c = args.Character;
        if (char.IsControl(c)) return;     // ignore Enter / Tab / Backspace etc.
        var at = _pendingTextPos.Value;
        CancelPendingText();
        SpawnTextBox(at, c.ToString());
        args.Handled = true;
    }

    private void SpawnTextBox(Vector2 worldPos, string? initial)
    {
        if (_page == null) return;
        FlushTexts(); // save other boxes' live edits before adding a new one
        var t = new TextElement { X = worldPos.X - 4, Y = worldPos.Y - 10 };
        UndoManager.Push(new AddTextAction(t), _page);
        BuildTextUi(t); // add ONLY the new box so existing boxes keep their formatting
        if (_textUi.TryGetValue(t.Id, out var ui))
        {
            ui.Box.Focus(FocusState.Programmatic);
            // Honour the chosen default font + size for the first characters typed.
            try { var cf = ui.Box.Document.Selection.CharacterFormat; cf.Name = PendingFontFamily; cf.Size = PendingFontSize; } catch { }
            if (!string.IsNullOrEmpty(initial))
                try { ui.Box.Document.Selection.TypeText(initial); } catch { }
        }
        ContentChanged?.Invoke();
    }

    /// <summary>Rotates the focused text box by the given delta in degrees (#20).</summary>
    public void RotateActiveText(double deltaDeg)
    {
        if (_page == null || ActiveTextBox == null) return;
        foreach (var (id, ui) in _textUi)
        {
            if (!ReferenceEquals(ui.Box, ActiveTextBox)) continue;
            var t = _page.Texts.FirstOrDefault(x => x.Id == id);
            if (t == null) return;
            t.Rotation = (t.Rotation + deltaDeg) % 360;
            ui.Container.RenderTransformOrigin = new Point(0.5, 0.5);
            ui.Container.RenderTransform = new RotateTransform { Angle = t.Rotation };
            ContentChanged?.Invoke();
            return;
        }
    }

    // =======================================================================
    // Programmatic content (phase 2): handwriting→text, tables, vector export
    // =======================================================================
    public IReadOnlyList<PenStroke> SelectedStrokes => _selected;
    public Rect SelectionBoundsWorld => _selBounds;

    /// <summary>Adds a text box with pre-built RTF (undoable).</summary>
    public void AddTextElement(double x, double y, double width, string rtf)
    {
        if (_page == null) return;
        FlushTexts();
        var t = new TextElement { X = x, Y = y, Width = Math.Max(60, width), Rtf = rtf };
        UndoManager.Push(new AddTextAction(t), _page);
        BuildTextUi(t);
        ContentChanged?.Invoke();
    }

    /// <summary>Inserts an empty rows×cols table centred in the view: ONE table
    /// shape plus a linked text bubble per cell, as a single undo step (#40).</summary>
    public void InsertTable(int rows, int cols, double cellW, double cellH)
    {
        if (_page == null) return;
        rows = Math.Clamp(rows, 1, 20);
        cols = Math.Clamp(cols, 1, 12);
        FlushTexts();
        var centre = ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        double x0 = centre.X - cols * cellW / 2, y0 = centre.Y - rows * cellH / 2;

        var table = new ShapeElement
        {
            Kind = ShapeKind.Table, X = x0, Y = y0,
            W = cols * cellW, H = rows * cellH,
            Color = "#8A8884", Size = 1.6f, TRows = rows, TCols = cols,
            TColW = Enumerable.Repeat(cellW, cols).ToList(),
            TRowH = Enumerable.Repeat(cellH, rows).ToList()
        };
        var texts = new List<TextElement>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                texts.Add(new TextElement
                {
                    X = x0 + c * cellW + 6, Y = y0 + r * cellH + 2, Width = cellW - 28,
                    TableId = table.Id, TableRow = r, TableCol = c
                });

        UndoManager.Push(new AddMixedAction(new List<PenStroke>(), new List<ShapeElement> { table }, texts), _page);
        _activeShape = table;
        RebuildTextLayer();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public ShapeElement? ActiveShape => _activeShape;

    // ---- Word-like table geometry (#49): per-column widths / per-row heights ----
    private static double[] TableColWidths(ShapeElement t)
    {
        int cols = Math.Max(1, t.TCols);
        var w = new double[cols];
        double sum = 0;
        for (int i = 0; i < cols; i++)
        {
            w[i] = t.TColW != null && i < t.TColW.Count && t.TColW[i] > 4 ? t.TColW[i] : t.W / cols;
            sum += w[i];
        }
        if (sum > 1 && Math.Abs(sum - t.W) > 0.5)
        {
            double f = t.W / sum;                    // whole-table resize scales columns
            for (int i = 0; i < cols; i++) w[i] *= f;
        }
        return w;
    }

    private static double[] TableRowHeights(ShapeElement t)
    {
        int rows = Math.Max(1, t.TRows);
        var h = new double[rows];
        double sum = 0;
        for (int i = 0; i < rows; i++)
        {
            h[i] = t.TRowH != null && i < t.TRowH.Count && t.TRowH[i] > 4 ? t.TRowH[i] : t.H / rows;
            sum += h[i];
        }
        if (sum > 1 && Math.Abs(sum - t.H) > 0.5)
        {
            double f = t.H / sum;
            for (int i = 0; i < rows; i++) h[i] *= f;
        }
        return h;
    }

    /// <summary>Which cell of the table the world point is in, if any.</summary>
    public (int Row, int Col)? TableCellAt(ShapeElement table, Vector2 world)
    {
        if (table.Kind != ShapeKind.Table) return null;
        double lx = world.X - table.X, ly = world.Y - table.Y;
        if (lx < 0 || ly < 0 || lx > table.W || ly > table.H) return null;
        var cw = TableColWidths(table);
        var rh = TableRowHeights(table);
        int col = 0, row = 0;
        double acc = 0;
        for (int i = 0; i < cw.Length; i++) { acc += cw[i]; if (lx <= acc) { col = i; break; } col = i; }
        acc = 0;
        for (int i = 0; i < rh.Length; i++) { acc += rh[i]; if (ly <= acc) { row = i; break; } row = i; }
        return (row, col);
    }

    /// <summary>Snaps a table's cell bubbles back onto its grid after the
    /// table shape has been moved, resized or restructured (#40/#49).</summary>
    public void ReflowTableCells(ShapeElement table)
    {
        if (_page == null || table.Kind != ShapeKind.Table) return;
        var cw = TableColWidths(table);
        var rh = TableRowHeights(table);
        double[] px = new double[cw.Length + 1];
        for (int i = 0; i < cw.Length; i++) px[i + 1] = px[i] + cw[i];
        double[] py = new double[rh.Length + 1];
        for (int i = 0; i < rh.Length; i++) py[i + 1] = py[i] + rh[i];

        var moves = new List<(TextElement, double, double, double, double)>();
        foreach (var t in _page.Texts)
        {
            if (t.TableId != table.Id) continue;
            int c = Math.Clamp(t.TableCol, 0, cw.Length - 1);
            int r = Math.Clamp(t.TableRow, 0, rh.Length - 1);
            int colSpan = Math.Clamp(t.CellColSpan, 1, cw.Length - c);
            
            double widthSum = 0;
            for (int i = 0; i < colSpan; i++) widthSum += cw[c + i];

            double nx = table.X + px[c] + 6;
            double ny = table.Y + py[r] + 2;
            t.Width = Math.Max(48, widthSum - 16);
            if (Math.Abs(t.X - nx) > 0.01 || Math.Abs(t.Y - ny) > 0.01)
                moves.Add((t, t.X, t.Y, nx, ny));
        }
        if (moves.Count == 0) { RebuildTextLayer(); return; }
        UndoManager.Push(new RepositionTextsAction(moves), _page);
        RebuildTextLayer();
    }

    public List<TextElement> GetSelectedTableCells(ShapeElement table)
    {
        var list = new List<TextElement>();
        if (ActiveTextBox != null && _textUi.FirstOrDefault(x => x.Value.Box == ActiveTextBox) is var pair && pair.Value.Box != null)
        {
            var t = _page?.Texts.FirstOrDefault(x => x.Id == pair.Key);
            if (t != null && t.TableId == table.Id) list.Add(t);
        }
        if (list.Count == 0)
        {
            foreach (var t in _selTexts)
            {
                if (t.TableId == table.Id) list.Add(t);
            }
        }
        return list;
    }

    public void TableMergeSelectedCells(ShapeElement table)
    {
        if (_page == null) return;
        var selected = GetSelectedTableCells(table);
        if (selected.Count <= 1) return;

        int minRow = selected.Min(x => x.TableRow);
        int maxRow = selected.Max(x => x.TableRow);
        int minCol = selected.Min(x => x.TableCol);
        int maxCol = selected.Max(x => x.TableCol);

        var topLeft = selected.FirstOrDefault(x => x.TableRow == minRow && x.TableCol == minCol);
        if (topLeft == null) return;

        var hidden = selected.Where(x => x != topLeft).ToList();
        
        var sb = new System.Text.StringBuilder(StripRtf(topLeft.Rtf));
        foreach (var h in hidden)
        {
            var text = StripRtf(h.Rtf);
            if (!string.IsNullOrEmpty(text))
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(text);
            }
        }
        
        topLeft.Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Lora;}}\viewkind4\uc1\pars " + sb.ToString() + "}";

        int toColSpan = maxCol - minCol + 1;
        int toRowSpan = maxRow - minRow + 1;

        UndoManager.Push(new CellMergeAction(topLeft, topLeft.CellColSpan, topLeft.CellRowSpan, toColSpan, toRowSpan, hidden), _page);
        ReflowTableCells(table);
        ClearSelection();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableSplitSelectedCell(ShapeElement table)
    {
        if (_page == null) return;
        var selected = GetSelectedTableCells(table);
        if (selected.Count != 1) return;

        var cell = selected[0];
        if (cell.CellColSpan <= 1 && cell.CellRowSpan <= 1) return;

        var restored = new List<TextElement>();
        for (int r = 0; r < cell.CellRowSpan; r++)
        {
            for (int c = 0; c < cell.CellColSpan; c++)
            {
                if (r == 0 && c == 0) continue;
                restored.Add(new TextElement
                {
                    TableId = table.Id,
                    TableRow = cell.TableRow + r,
                    TableCol = cell.TableCol + c,
                    Rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Lora;}}\viewkind4\uc1\pars }"
                });
            }
        }

        UndoManager.Push(new CellMergeAction(cell, cell.CellColSpan, cell.CellRowSpan, 1, 1, restored), _page);
        foreach (var r in restored) _page.Texts.Add(r);

        ReflowTableCells(table);
        ClearSelection();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableSetSelectedCellsFill(ShapeElement table, string? fillHex)
    {
        if (_page == null) return;
        var selected = GetSelectedTableCells(table);
        if (selected.Count == 0) return;

        var actions = new List<IPageAction>();
        foreach (var cell in selected)
        {
            actions.Add(new CellStyleAction(cell, cell.FillColor, fillHex, cell.BorderColor, cell.BorderColor, cell.BorderWidth, cell.BorderWidth));
        }
        UndoManager.Push(new CompositeAction(actions, "Change cell fill"), _page);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableSetSelectedCellsBorder(ShapeElement table, string? borderHex, float? borderWidth)
    {
        if (_page == null) return;
        var selected = GetSelectedTableCells(table);
        if (selected.Count == 0) return;

        var actions = new List<IPageAction>();
        foreach (var cell in selected)
        {
            actions.Add(new CellStyleAction(cell, cell.FillColor, cell.FillColor, cell.BorderColor, borderHex, cell.BorderWidth, borderWidth));
        }
        UndoManager.Push(new CompositeAction(actions, "Change cell border"), _page);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableToggleHeaderRow(ShapeElement table)
    {
        if (_page == null) return;
        UndoManager.Push(new HeaderRowAction(table, table.HeaderRow, !table.HeaderRow), _page);
        
        // Bold/unbold text of the header row cells
        foreach (var t in _page.Texts)
        {
            if (t.TableId == table.Id && t.TableRow == 0)
            {
                // Simple best effort to bold header cells:
                // If it is bold (contains \b ), unbold it. Or visa-versa.
                if (!table.HeaderRow)
                {
                    // Enabling header row (which is !table.HeaderRow before the undo action commits) -> make it bold!
                    if (!t.Rtf.Contains(@"\b "))
                    {
                        t.Rtf = t.Rtf.Replace(@"\pars ", @"\pars \b ").Replace(@"}", @"\b0}");
                    }
                }
                else
                {
                    // Disabling
                    t.Rtf = t.Rtf.Replace(@"\b ", "").Replace(@"\b0", "");
                }
            }
        }

        _canvas.Invalidate();
        RebuildTextLayer();
        ContentChanged?.Invoke();
    }

    /// <summary>Inserts a column at the given index (0 = far left).</summary>
    public void TableInsertColumn(ShapeElement table, int at)
    {
        if (_page == null || table.Kind != ShapeKind.Table) return;
        FlushTexts();
        int rows = Math.Max(1, table.TRows), cols = Math.Max(1, table.TCols);
        at = Math.Clamp(at, 0, cols);
        var colW = TableColWidths(table).ToList();
        var rowH = TableRowHeights(table).ToList();
        double newW = colW.Average();

        var shifted = _page.Texts.Where(t => t.TableId == table.Id && t.TableCol >= at).ToList();
        var newCells = new List<TextElement>();
        for (int r = 0; r < rows; r++)
            newCells.Add(new TextElement { TableId = table.Id, TableRow = r, TableCol = at, Width = Math.Max(48, newW - 16) });

        var newColW = colW.ToList();
        newColW.Insert(at, newW);
        var acts = new IPageAction[]
        {
            new ShiftTableCellsAction(shifted, 0, +1),
            new TableGridAction(table, rows, cols, rows, cols + 1),
            new TableLayoutAction(table, colW, rowH, table.W, table.H, newColW, rowH, table.W + newW, table.H),
            new AddMixedAction(new List<PenStroke>(), new List<ShapeElement>(), newCells)
        };
        UndoManager.Push(new CompositeAction(acts, "Add table column"), _page);
        ReflowTableCells(table);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    /// <summary>Inserts a row at the given index (0 = top).</summary>
    public void TableInsertRow(ShapeElement table, int at)
    {
        if (_page == null || table.Kind != ShapeKind.Table) return;
        FlushTexts();
        int rows = Math.Max(1, table.TRows), cols = Math.Max(1, table.TCols);
        at = Math.Clamp(at, 0, rows);
        var colW = TableColWidths(table).ToList();
        var rowH = TableRowHeights(table).ToList();
        double newH = rowH.Average();

        var shifted = _page.Texts.Where(t => t.TableId == table.Id && t.TableRow >= at).ToList();
        var newCells = new List<TextElement>();
        for (int c = 0; c < cols; c++)
            newCells.Add(new TextElement { TableId = table.Id, TableRow = at, TableCol = c, Width = Math.Max(48, colW[c] - 16) });

        var newRowH = rowH.ToList();
        newRowH.Insert(at, newH);
        var acts = new IPageAction[]
        {
            new ShiftTableCellsAction(shifted, +1, 0),
            new TableGridAction(table, rows, cols, rows + 1, cols),
            new TableLayoutAction(table, colW, rowH, table.W, table.H, colW, newRowH, table.W, table.H + newH),
            new AddMixedAction(new List<PenStroke>(), new List<ShapeElement>(), newCells)
        };
        UndoManager.Push(new CompositeAction(acts, "Add table row"), _page);
        ReflowTableCells(table);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableDeleteRow(ShapeElement table, int row)
    {
        if (_page == null || table.Kind != ShapeKind.Table || table.TRows <= 1) return;
        FlushTexts();
        int rows = Math.Max(1, table.TRows), cols = Math.Max(1, table.TCols);
        row = Math.Clamp(row, 0, rows - 1);
        var colW = TableColWidths(table).ToList();
        var rowH = TableRowHeights(table).ToList();
        var doomed = _page.Texts.Where(t => t.TableId == table.Id && t.TableRow == row).ToList();
        var shifted = _page.Texts.Where(t => t.TableId == table.Id && t.TableRow > row).ToList();
        var newRowH = rowH.ToList();
        double gone = newRowH[row];
        newRowH.RemoveAt(row);
        var acts = new IPageAction[]
        {
            new RemoveMixedAction(new List<PenStroke>(), new List<ShapeElement>(), doomed),
            new ShiftTableCellsAction(shifted, -1, 0),
            new TableGridAction(table, rows, cols, rows - 1, cols),
            new TableLayoutAction(table, colW, rowH, table.W, table.H, colW, newRowH, table.W, table.H - gone)
        };
        UndoManager.Push(new CompositeAction(acts, "Delete table row"), _page);
        ReflowTableCells(table);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableDeleteColumn(ShapeElement table, int col)
    {
        if (_page == null || table.Kind != ShapeKind.Table || table.TCols <= 1) return;
        FlushTexts();
        int rows = Math.Max(1, table.TRows), cols = Math.Max(1, table.TCols);
        col = Math.Clamp(col, 0, cols - 1);
        var colW = TableColWidths(table).ToList();
        var rowH = TableRowHeights(table).ToList();
        var doomed = _page.Texts.Where(t => t.TableId == table.Id && t.TableCol == col).ToList();
        var shifted = _page.Texts.Where(t => t.TableId == table.Id && t.TableCol > col).ToList();
        var newColW = colW.ToList();
        double gone = newColW[col];
        newColW.RemoveAt(col);
        var acts = new IPageAction[]
        {
            new RemoveMixedAction(new List<PenStroke>(), new List<ShapeElement>(), doomed),
            new ShiftTableCellsAction(shifted, 0, -1),
            new TableGridAction(table, rows, cols, rows, cols - 1),
            new TableLayoutAction(table, colW, rowH, table.W, table.H, newColW, rowH, table.W - gone, table.H)
        };
        UndoManager.Push(new CompositeAction(acts, "Delete table column"), _page);
        ReflowTableCells(table);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void SelectTableRow(ShapeElement table, int row)
    {
        if (_page == null) return;
        ClearSelection();
        foreach (var t in _page.Texts.Where(t => t.TableId == table.Id && t.TableRow == row))
            _selTexts.Add(t);
        RecomputeSelectionBounds();
        _canvas.Invalidate();
    }

    public void SelectTableColumn(ShapeElement table, int col)
    {
        if (_page == null) return;
        ClearSelection();
        foreach (var t in _page.Texts.Where(t => t.TableId == table.Id && t.TableCol == col))
            _selTexts.Add(t);
        RecomputeSelectionBounds();
        _canvas.Invalidate();
    }

    // ---- divider dragging (#49): grab an inner grid line to resize its column/row ----
    private bool _tableDividerDrag;
    private int _tableDivCol = -1, _tableDivRow = -1;
    private List<double>? _tableOrigColW, _tableOrigRowH;
    private double _tableOrigW, _tableOrigH;

    private bool HitTableDivider(ShapeElement t, Vector2 pos, float tol, out int col, out int row)
    {
        col = row = -1;
        double lx = pos.X - t.X, ly = pos.Y - t.Y;
        if (lx < -tol || ly < -tol || lx > t.W + tol || ly > t.H + tol) return false;
        var cw = TableColWidths(t);
        double acc = 0;
        for (int i = 0; i < cw.Length - 1; i++)
        {
            acc += cw[i];
            if (Math.Abs(lx - acc) <= tol && ly >= 0 && ly <= t.H) { col = i + 1; return true; }
        }
        var rh = TableRowHeights(t);
        acc = 0;
        for (int i = 0; i < rh.Length - 1; i++)
        {
            acc += rh[i];
            if (Math.Abs(ly - acc) <= tol && lx >= 0 && lx <= t.W) { row = i + 1; return true; }
        }
        return false;
    }

    // ---- floating "+" buttons (#49): top = add column, left = add row ----
    private (Vector2 ColBtn, Vector2 RowBtn) TablePlusCentres(ShapeElement t)
    {
        float d = 26f / ViewZoom;
        return (new Vector2((float)(t.X + t.W / 2), (float)t.Y - d),
                new Vector2((float)t.X - d, (float)(t.Y + t.H / 2)));
    }

    private bool HitTablePlus(ShapeElement t, Vector2 pos, out bool column)
    {
        var (cb, rb) = TablePlusCentres(t);
        float r = 15f / ViewZoom;
        column = Vector2.Distance(pos, cb) <= r;
        if (column) return true;
        return Vector2.Distance(pos, rb) <= r;
    }

    private void ShowTablePlusMenu(ShapeElement table, bool column, Vector2 screen)
    {
        var fly = new MenuFlyout();
        void Add(string txt, Action act)
        {
            var it = new MenuFlyoutItem { Text = txt };
            it.Click += (_, _) => act();
            fly.Items.Add(it);
        }
        if (column)
        {
            Add("Add column left", () => TableInsertColumn(table, 0));
            Add("Add column right", () => TableInsertColumn(table, Math.Max(1, table.TCols)));
        }
        else
        {
            Add("Add row above", () => TableInsertRow(table, 0));
            Add("Add row below", () => TableInsertRow(table, Math.Max(1, table.TRows)));
        }
        fly.ShowAt(_canvas, new Point(screen.X, screen.Y));
    }

    /// <summary>Flattens the current page's ink, shapes, grid, images and text
    /// into vector primitives for the vector PDF exporter (#41).</summary>
    public async Task<PdfVectorPage?> BuildVectorPageAsync(double marginPx)
    {
        if (_page == null) return null;
        FlushTexts();
        var content = ContentBoundsWorld() ?? new Rect(0, 0, 800, 600);
        // make sure text boxes are inside the exported area too
        foreach (var t in _page.Texts)
        {
            var est = new Rect(t.X, t.Y, Math.Max(60, t.Width), 60);
            content = content.IsEmpty ? est : RectUnion(content, est);
        }
        double minX = content.X - marginPx, minY = content.Y - marginPx;
        double w = Math.Max(64, content.Width + marginPx * 2);
        double h = Math.Max(64, content.Height + marginPx * 2);

        var paths = new List<PdfVectorPath>();
        var dots = new List<PdfVectorDot>();
        var images = new List<PdfVectorImage>();
        var texts = new List<PdfVectorText>();
        var bgCol = ColorUtil.Parse(_page.Background);
        string inkHex = ColorUtil.IsDark(bgCol) ? "#FAF9F5" : "#141413";

        // ---- images, decoded to pixels for embedding ----
        foreach (var sh in _page.Shapes)
        {
            if (sh.Kind != ShapeKind.Image || sh.ImagePath == null) continue;
            try
            {
                CanvasBitmap? bmp = _bitmaps.TryGetValue(sh.ImagePath, out var cached) ? cached : null;
                bmp ??= await CanvasBitmap.LoadAsync(_canvas, sh.ImagePath);
                images.Add(new PdfVectorImage(sh.X, sh.Y, Math.Max(1, sh.W), Math.Max(1, sh.H),
                    (int)bmp.SizeInPixels.Width, (int)bmp.SizeInPixels.Height, bmp.GetPixelBytes()));
            }
            catch { /* unreadable image: skip, ink still exports */ }
        }

        // ---- text boxes as selectable PDF text ----
        foreach (var t in _page.Texts)
        {
            var plain = RtfToPlainText(t.Rtf, out float size, out string font);
            if (string.IsNullOrWhiteSpace(plain)) continue;
            var lines = plain.Split('\n');
            for (int li = 0; li < lines.Length; li++)
            {
                if (lines[li].Length == 0) continue;
                texts.Add(new PdfVectorText(
                    (float)(t.X + 4),
                    (float)(t.Y + 16 + size + li * size * 1.35),
                    size, inkHex, lines[li], font));
            }
        }

        // ---- grid, pre-blended against the background ----
        if (_page.Grid != GridType.None)
        {
            float spacing = (float)Math.Max(8, _page.GridSpacing);
            while (w / spacing * (h / spacing) > 25000) spacing *= 2;
            var over = ColorUtil.IsDark(bgCol) ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(46, 0, 0, 0);
            var blended = Color.FromArgb(255,
                (byte)((over.R * over.A + bgCol.R * (255 - over.A)) / 255),
                (byte)((over.G * over.A + bgCol.G * (255 - over.A)) / 255),
                (byte)((over.B * over.A + bgCol.B * (255 - over.A)) / 255));
            string gHex = ColorUtil.ToHex(blended);
            double sx = Math.Floor(minX / spacing) * spacing;
            double sy = Math.Floor(minY / spacing) * spacing;
            switch (_page.Grid)
            {
                case GridType.Dotted:
                    for (double y = sy; y < minY + h; y += spacing)
                        for (double x = sx; x < minX + w; x += spacing)
                            dots.Add(new PdfVectorDot((float)x, (float)y, 1.4f, gHex));
                    break;
                case GridType.Square:
                    for (double x = sx; x < minX + w; x += spacing)
                        paths.Add(LinePath(x, minY, x, minY + h, gHex, 1f));
                    for (double y = sy; y < minY + h; y += spacing)
                        paths.Add(LinePath(minX, y, minX + w, y, gHex, 1f));
                    break;
                case GridType.Lines:
                    for (double y = sy; y < minY + h; y += spacing)
                        paths.Add(LinePath(minX, y, minX + w, y, gHex, 1f));
                    break;
            }
        }

        // ---- shapes ----
        foreach (var sh in _page.Shapes)
            FlattenShape(sh, paths);

        // ---- ink strokes ----
        foreach (var s in _page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            var pts = new List<(float X, float Y)>(s.Points.Count);
            foreach (var p in s.Points) pts.Add((p.X, p.Y));
            if (pts.Count == 1) pts.Add((pts[0].X + 0.2f, pts[0].Y + 0.2f)); // dot taps
            bool hl = s.Pen == PenType.Highlighter;
            paths.Add(new PdfVectorPath(pts, s.Color, s.Size * (hl ? 1.6f : 1f), false, hl ? 0.35f : 1f));
        }

        return new PdfVectorPage(w, h, minX, minY, _page.Background, paths, dots, images, texts);
    }

    private static Rect RectUnion(Rect a, Rect b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    // Best-effort RTF → plain text with \par as newlines; also pulls the first
    // \fsN font size. Skips the fonttbl/colortbl header groups.
    private static string RtfToPlainText(string rtf, out float fontSize, out string fontFamily)
    {
        fontSize = 16f;
        fontFamily = "Lora";
        if (string.IsNullOrEmpty(rtf)) return "";

        int tblIdx = rtf.IndexOf("{\\fonttbl");
        if (tblIdx >= 0)
        {
            int endTbl = rtf.IndexOf('}', tblIdx);
            if (endTbl > tblIdx)
            {
                string tbl = rtf[tblIdx..endTbl];
                int sem = tbl.IndexOf(';');
                if (sem > 0)
                {
                    int start = sem;
                    while (start > 0 && tbl[start - 1] != ' ' && tbl[start - 1] != '}') start--;
                    var foundFont = tbl[start..sem].Trim();
                    if (!string.IsNullOrEmpty(foundFont)) fontFamily = foundFont;
                }
            }
        }
        var sb = new System.Text.StringBuilder();
        bool sizeFound = false;
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{')
            {
                // skip header groups entirely: {\fonttbl...} {\colortbl...} {\*\...}
                foreach (var head in new[] { "{\\fonttbl", "{\\colortbl", "{\\stylesheet", "{\\*" })
                {
                    if (i + head.Length <= rtf.Length && rtf.AsSpan(i, head.Length).SequenceEqual(head))
                    {
                        int depth = 0;
                        for (; i < rtf.Length; i++)
                        {
                            if (rtf[i] == '{') depth++;
                            else if (rtf[i] == '}' && --depth == 0) break;
                        }
                        break;
                    }
                }
                continue;
            }
            if (c == '}') continue;
            if (c == '\\')
            {
                if (i + 1 < rtf.Length && (rtf[i + 1] is '{' or '}' or '\\'))
                {
                    sb.Append(rtf[i + 1]);
                    i++;
                    continue;
                }
                if (i + 3 < rtf.Length && rtf[i + 1] == '\'' &&
                    byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte bv))
                {
                    sb.Append((char)bv);
                    i += 3;
                    continue;
                }
                i++;
                int ws = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                string word = rtf[ws..i];
                int ns = i;
                while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                string num = rtf[ns..i];
                if (i >= rtf.Length || rtf[i] != ' ') i--;
                if (word is "par" or "line") sb.Append('\n');
                else if (word == "fs" && !sizeFound && int.TryParse(num, out int hp) && hp > 4)
                {
                    fontSize = hp / 2f;
                    sizeFound = true;
                }
                else if (word == "u" && int.TryParse(num, out int uc))
                {
                    sb.Append((char)Math.Abs(uc));
                    if (i + 1 < rtf.Length) i++;   // skip the '?' substitute
                }
                continue;
            }
            if (c is '\r' or '\n') continue;
            sb.Append(c);
        }
        var cleaned = string.Join('\n',
            sb.ToString().Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, " {2,}", " ").Trim()));
        return cleaned.Trim('\n');
    }

    private static PdfVectorPath LinePath(double x0, double y0, double x1, double y1, string color, float width)
        => new(new List<(float X, float Y)> { ((float)x0, (float)y0), ((float)x1, (float)y1) }, color, width, false, 1f);

    private void FlattenShape(ShapeElement s, List<PdfVectorPath> paths)
    {
        void Add(List<(float X, float Y)> pts, bool closed)
        {
            if (Math.Abs(s.Rotation) > 0.01)
            {
                var c = ShapeCenter(s);
                for (int i = 0; i < pts.Count; i++)
                {
                    var rp = RotatePoint(new Vector2(pts[i].X, pts[i].Y), c, s.Rotation);
                    pts[i] = (rp.X, rp.Y);
                }
            }
            paths.Add(new PdfVectorPath(pts, s.Color, Math.Max(1f, s.Size), closed, 1f));
        }
        void AddLine(double x0, double y0, double x1, double y1)
            => Add(new List<(float X, float Y)> { ((float)x0, (float)y0), ((float)x1, (float)y1) }, false);
        void AddArrow(Vector2 a, Vector2 b)
        {
            AddLine(a.X, a.Y, b.X, b.Y);
            var dir = b - a;
            float len = dir.Length();
            if (len < 1) return;
            dir /= len;
            float hs = Math.Max(9f, Math.Max(1f, s.Size) * 3.2f);
            var perp = new Vector2(-dir.Y, dir.X);
            var h1 = b - dir * hs + perp * hs * 0.5f;
            var h2 = b - dir * hs - perp * hs * 0.5f;
            AddLine(b.X, b.Y, h1.X, h1.Y);
            AddLine(b.X, b.Y, h2.X, h2.Y);
        }

        switch (s.Kind)
        {
            case ShapeKind.Line:
                AddLine(s.X, s.Y, s.X + s.W, s.Y + s.H);
                break;
            case ShapeKind.Arrow:
                AddArrow(new Vector2((float)s.X, (float)s.Y), new Vector2((float)(s.X + s.W), (float)(s.Y + s.H)));
                break;
            case ShapeKind.Rect:
                Add(new List<(float X, float Y)>
                {
                    ((float)s.X, (float)s.Y), ((float)(s.X + s.W), (float)s.Y),
                    ((float)(s.X + s.W), (float)(s.Y + s.H)), ((float)s.X, (float)(s.Y + s.H))
                }, true);
                break;
            case ShapeKind.Ellipse:
            {
                var pts = new List<(float X, float Y)>();
                double cx = s.X + s.W / 2, cy = s.Y + s.H / 2, rx = Math.Max(1, s.W) / 2, ry = Math.Max(1, s.H) / 2;
                for (int i = 0; i < 48; i++)
                {
                    double a = i * Math.PI * 2 / 48;
                    pts.Add(((float)(cx + Math.Cos(a) * rx), (float)(cy + Math.Sin(a) * ry)));
                }
                Add(pts, true);
                break;
            }
            case ShapeKind.Triangle:
            case ShapeKind.RightTriangle:
            case ShapeKind.Diamond:
            case ShapeKind.Pentagon:
            case ShapeKind.Hexagon:
            case ShapeKind.Star:
            case ShapeKind.Parallelogram:
            case ShapeKind.Trapezoid:
            {
                var v = PolygonVertices(s);
                var pts = new List<(float X, float Y)>(v.Length);
                foreach (var p in v) pts.Add((p.X, p.Y));
                Add(pts, true);
                break;
            }
            case ShapeKind.Table:
            {
                Add(new List<(float X, float Y)>
                {
                    ((float)s.X, (float)s.Y), ((float)(s.X + s.W), (float)s.Y),
                    ((float)(s.X + s.W), (float)(s.Y + s.H)), ((float)s.X, (float)(s.Y + s.H))
                }, true);
                var cw = TableColWidths(s);
                var rh = TableRowHeights(s);
                double acc = 0;
                for (int i = 0; i < rh.Length - 1; i++) { acc += rh[i]; AddLine(s.X, s.Y + acc, s.X + s.W, s.Y + acc); }
                acc = 0;
                for (int i = 0; i < cw.Length - 1; i++) { acc += cw[i]; AddLine(s.X + acc, s.Y, s.X + acc, s.Y + s.H); }
                break;
            }
            case ShapeKind.AxesXY:
            {
                var o = new Vector2((float)s.X, (float)(s.Y + s.H));
                AddArrow(o, new Vector2((float)(s.X + s.W), o.Y));
                AddArrow(o, new Vector2(o.X, (float)s.Y));
                break;
            }
            case ShapeKind.AxesXYZ:
            {
                var o = new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2));
                AddArrow(o, new Vector2((float)(s.X + s.W), o.Y));
                AddArrow(o, new Vector2(o.X, (float)s.Y));
                AddArrow(o, new Vector2((float)s.X, (float)(s.Y + s.H)));
                break;
            }
            // Image shapes are skipped — vector export covers ink, shapes, grid.
        }
    }

    public void RebuildTextLayer()
    {
        FlushTexts(); // persist any live edits before tearing boxes down (prevents resets)
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

        // The grip bar stays transparent (but still draggable) until the box is
        // focused, so an unselected text box shows only its text — no chrome (#3).
        var gripBrush = new SolidColorBrush(Colors.Transparent);
        var grip = new Grid
        {
            Height = 16,
            Background = gripBrush,
            CornerRadius = new CornerRadius(5, 5, 0, 0)
        };
        var dots = new TextBlock
        {
            Text = "⋮⋮",
            FontSize = 9,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Visibility = Visibility.Collapsed
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
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Collapsed
        };
        // rotate handle: drag left/right to spin the box, like image rotation (#38)
        var rotate = new TextBlock
        {
            Text = "⟳",
            FontSize = 11,
            Margin = new Thickness(0, 0, 28, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.75,
            Visibility = Visibility.Collapsed
        };
        ToolTipService.SetToolTip(rotate, "Drag to rotate around the centre (double-tap to reset)");
        // Rotation tracks the pointer's absolute angle around the box centre in
        // the (non-rotating) layer space — no feedback loop, no barrel rolls.
        bool rotating = false;
        double rotStartAngle = 0, rotStartDeg = 0;
        Point RotCentre() => new(
            Canvas.GetLeft(container) + container.ActualWidth / 2,
            Canvas.GetTop(container) + container.ActualHeight / 2);
        rotate.PointerPressed += (s2, e) =>
        {
            rotating = true;
            var c = RotCentre();
            var p = e.GetCurrentPoint(_textLayer).Position;
            rotStartAngle = Math.Atan2(p.Y - c.Y, p.X - c.X);
            rotStartDeg = t.Rotation;
            ((UIElement)s2).CapturePointer(e.Pointer);
            e.Handled = true;
        };
        rotate.PointerMoved += (_, e) =>
        {
            if (!rotating) return;
            var c = RotCentre();
            var p = e.GetCurrentPoint(_textLayer).Position;
            double angle = Math.Atan2(p.Y - c.Y, p.X - c.X);
            t.Rotation = (rotStartDeg + (angle - rotStartAngle) * 180.0 / Math.PI) % 360;
            container.RenderTransformOrigin = new Point(0.5, 0.5);
            container.RenderTransform = new RotateTransform { Angle = t.Rotation };
            e.Handled = true;
        };
        rotate.PointerReleased += (s2, e) =>
        {
            if (!rotating) return;
            rotating = false;
            ((UIElement)s2).ReleasePointerCaptures();
            ContentChanged?.Invoke();
            e.Handled = true;
        };
        rotate.DoubleTapped += (_, _) =>
        {
            t.Rotation = 0;
            container.RenderTransform = null;
            ContentChanged?.Invoke();
        };

        grip.Children.Add(dots);
        grip.Children.Add(rotate);
        grip.Children.Add(close);
        Grid.SetRow(grip, 0);

        var box = new RichEditBox
        {
            MinWidth = 160,
            Width = t.Width,
            MinHeight = 40,
            FontSize = PendingFontSize,
            FontFamily = new FontFamily(PendingFontFamily),
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
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
        if (Math.Abs(t.Rotation) > 0.01)
        {
            container.RenderTransformOrigin = new Point(0.5, 0.5);
            container.RenderTransform = new RotateTransform { Angle = t.Rotation };
        }

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
                // If creating this box is still the latest action, retire it so
                // the discarded empty box leaves no dead undo step.
                UndoManager.TryDiscardTop(a => a is AddTextAction ata && ReferenceEquals(ata.Text, t));
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
            Width = 11,
            Background = new SolidColorBrush(Color.FromArgb(110, 217, 119, 87)),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 4, -13, 4),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(rGrip, 1);
        ToolTipService.SetToolTip(rGrip, "Drag to change the text box width");

        // Reveal the grip / close / resize handle only while this box is focused.
        box.GotFocus += (_, _) =>
        {
            gripBrush.Color = Color.FromArgb(60, 217, 119, 87);
            dots.Visibility = Visibility.Visible;
            rotate.Visibility = Visibility.Visible;
            close.Visibility = Visibility.Visible;
            rGrip.Visibility = Visibility.Visible;
        };
        box.LostFocus += (_, _) =>
        {
            gripBrush.Color = Colors.Transparent;
            dots.Visibility = Visibility.Collapsed;
            rotate.Visibility = Visibility.Collapsed;
            close.Visibility = Visibility.Collapsed;
            rGrip.Visibility = Visibility.Collapsed;
        };
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

    public PenStroke? HitStroke(Vector2 pos, float tol)
    {
        if (_page == null) return null;
        foreach (var s in _page.Strokes)
        {
            for (int i = 0; i < s.Points.Count - 1; i++)
            {
                var from = new Vector2(s.Points[i].X, s.Points[i].Y);
                var to = new Vector2(s.Points[i + 1].X, s.Points[i + 1].Y);
                if (GeometryUtil.DistToSegment(pos, from, to) <= tol)
                    return s;
            }
        }
        return null;
    }

    private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        var pos = ToWorld(e.GetPosition(_canvas));
        float tol = 10f / ViewZoom;

        var hitStroke = HitStroke(pos, tol);
        if (hitStroke != null)
        {
            StrokeTapped?.Invoke(hitStroke);
            e.Handled = true;
            return;
        }

        if (e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch && !HandDrawMode)
        {
            var hitShape = HitShape(pos, tol);
            if (hitShape != null)
            {
                if (_activeShape != hitShape)
                {
                    _activeShape = hitShape;
                    ClearSelection();
                    _canvas.Invalidate();
                }
                e.Handled = true;
            }
            else if (hitShape == null)
            {
                if (_activeShape != null)
                {
                    _activeShape = null;
                    _canvas.Invalidate();
                }
            }
        }
    }

    private static string StripRtf(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{' || c == '}') continue;
            if (c == '\\')
            {
                i++;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                if (i < rtf.Length && rtf[i] != ' ') i--;
                continue;
            }
            if (c is '\r' or '\n') { sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        var screen = e.GetPosition(_canvas);
        var pos = ToWorld(new Vector2((float)screen.X, (float)screen.Y));

        foreach (var shape in _page.Shapes)
        {
            if (shape.Kind == ShapeKind.Table && HitTableDivider(shape, pos, Math.Max(12f, 8f / ViewZoom), out int col, out int row))
            {
                if (col > 0)
                {
                    int targetCol = col - 1; // Double tap divider col -> resizes column col-1
                    double maxTextWidth = 0;
                    foreach (var text in _page.Texts)
                    {
                        if (text.TableId == shape.Id && text.TableCol == targetCol)
                        {
                            string txt = StripRtf(text.Rtf);
                            if (string.IsNullOrEmpty(txt)) continue;
                            
                            using var layout = new CanvasTextLayout(_canvas, txt, new CanvasTextFormat { FontSize = 16f }, 1000, 1000);
                            maxTextWidth = Math.Max(maxTextWidth, layout.LayoutBounds.Width);
                        }
                    }

                    double newColW = Math.Clamp(maxTextWidth + 32, 60, 800);
                    var colW = TableColWidths(shape).ToList();
                    var rowH = TableRowHeights(shape).ToList();
                    var oldColW = colW.ToList();

                    double diff = newColW - colW[targetCol];
                    colW[targetCol] = newColW;
                    double newW = shape.W + diff;

                    UndoManager.Push(new TableLayoutAction(shape, oldColW, rowH, shape.W, shape.H, colW, rowH, newW, shape.H), _page);
                    ReflowTableCells(shape);
                    _canvas.Invalidate();
                    ContentChanged?.Invoke();
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    public static byte[]? RenderPageThumbnail(NotePage page, int targetWidth, int targetHeight)
    {
        var device = CanvasDevice.GetSharedDevice();
        if (device == null) return null;
        try
        {
            using var rt = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                var bg = ColorUtil.Parse(page.Background);
                ds.Clear(bg);
                
                double w = 1200;
                double h = 900;
                float scale = Math.Min((float)(targetWidth / w), (float)(targetHeight / h));
                ds.Transform = Matrix3x2.CreateScale(scale);

                foreach (var sh in page.Shapes)
                {
                    var color = ColorUtil.Parse(sh.Color);
                    ds.DrawRectangle(new Rect(sh.X, sh.Y, Math.Max(1, sh.W), Math.Max(1, sh.H)), color, Math.Max(1f, sh.Size));
                }

                foreach (var s in page.Strokes)
                {
                    var color = ColorUtil.Parse(s.Color);
                    for (int i = 1; i < s.Points.Count; i++)
                    {
                        ds.DrawLine(new Vector2(s.Points[i - 1].X, s.Points[i - 1].Y), new Vector2(s.Points[i].X, s.Points[i].Y), color, s.Size);
                    }
                }
            }

            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var task = rt.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask();
            task.Wait();
            var bytes = new byte[stream.Size];
            using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
            {
                var loadTask = reader.LoadAsync((uint)stream.Size).AsTask();
                loadTask.Wait();
                reader.ReadBytes(bytes);
            }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private bool TryDrawInkCache(CanvasDrawingSession ds, CanvasControl sender,
                                 float vx0, float vy0, float vx1, float vy1)
    {
        try
        {
            bool coversView = _inkCache != null &&
                vx0 >= _inkCacheWorld.Left && vy0 >= _inkCacheWorld.Top &&
                vx1 <= _inkCacheWorld.Right && vy1 <= _inkCacheWorld.Bottom;

            // Compare against the zoom the cache was BUILT at, not the render
            // scale: on large windows the 4096px clamp lowers the render scale,
            // and comparing against it made this false every frame — a
            // permanent rebuild loop (#55).
            bool zoomOk = _inkCache != null && ViewZoom / _inkCacheBuiltZoom is > 0.50f and < 1.05f;

            if (_inkCacheDirty || !zoomOk || !coversView)
            {
                double vw = Math.Max(64, vx1 - vx0), vh = Math.Max(64, vy1 - vy0);
                var world = new Rect(vx0 - vw, vy0 - vh, vw * 3, vh * 3);
                float scale = ViewZoom * 1.5f;
                double pxW = world.Width * scale, pxH = world.Height * scale;
                const double maxPx = 4096;
                if (pxW > maxPx || pxH > maxPx)
                {
                    double f = Math.Min(maxPx / pxW, maxPx / pxH);
                    scale *= (float)f;
                    pxW *= f;
                    pxH *= f;
                }

                _inkCache?.Dispose();
                _inkCache = new CanvasRenderTarget(sender, (float)pxW, (float)pxH, 96);
                using (var cds = _inkCache.CreateDrawingSession())
                {
                    cds.Clear(Colors.Transparent);
                    cds.Transform = Matrix3x2.CreateTranslation((float)-world.X, (float)-world.Y) *
                                    Matrix3x2.CreateScale(scale);
                    foreach (var s in _page!.Strokes)
                    {
                        s.GetBounds(out float bx0, out float by0, out float bx1, out float by1);
                        float pad = s.Size * 2.5f + 6f;
                        if (bx1 < world.Left - pad || bx0 > world.Right + pad ||
                            by1 < world.Top - pad || by0 > world.Bottom + pad) continue;
                        DrawStroke(cds, sender, s, Vector2.Zero, null);
                    }
                }
                _inkCacheWorld = world;
                _inkCacheScale = scale;
                _inkCacheBuiltZoom = ViewZoom;
                _inkCacheDirty = false;
            }

            if (_inkCache == null) return false;
            ds.DrawImage(_inkCache, _inkCacheWorld);
            return true;
        }
        catch
        {
            _inkCache?.Dispose();
            _inkCache = null;
            return false;   // fall back to the per-stroke path
        }
    }
}
