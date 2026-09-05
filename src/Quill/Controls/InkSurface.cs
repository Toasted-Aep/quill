using System.Numerics;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
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
    private readonly CanvasVirtualControl _canvas;
    private readonly Canvas _textLayer;
    private readonly CompositeTransform _textTransform = new();
    private readonly DispatcherTimer _zoomSettleTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    // last moment a pen was seen on/over the canvas — used to ignore palm
    // touches while writing (#inkfix)
    private long _lastPenSeenMs;
    private NotePage? _page;

    // ---- view transform ---------------------------------------------------
    public Vector2 ViewOffset { get; private set; } = Vector2.Zero;
    public float ViewZoom { get; private set; } = 1f;
    public TimeSpan? AudioPlayheadPosition { get; set; }
    public long? RecordingStartTicks { get; set; }
    public event Action? ViewChanged;
    /// <summary>Raised when the SUBJECT's bounds move while the view holds still
    /// - a selection being dragged or scaled, and the recompute after the drop.
    ///
    /// <para>Neither existing signal can carry this. <see cref="ViewChanged"/> is
    /// about pan and zoom, and a drag changes nothing about the view;
    /// <c>SelectionState.Changed</c> deliberately drops a publish whose rendered
    /// properties match the last one, which a pure move's always do - same kind,
    /// same count, same flags. So the chrome had nothing to listen to and framed
    /// where the selection STARTED, through the drag and on past the drop.</para></summary>
    public event Action? SubjectMoved;
    /// <summary>Raised the one time a page's CONCEPTS-REF 14.5 reference frame is
    /// captured, so the host can get it persisted. Fires at most once per page.</summary>
    public event Action? RefFrameCaptured;
    public event Action<double>? RulerAngleChanged; // raised when 2-finger tilt changes the ruler

    /// <summary>11.4 item 28 and 10.8: one of the two SAMPLING tools was used.
    /// Carries which tool it was, the colour under the tap, and whether that tap
    /// landed on bare paper rather than on ink.
    ///
    /// <para>The bare-paper flag is reported rather than inferred by comparing
    /// the colour, because 10.8's dilution turns on exactly that question and a
    /// colour comparison would get it wrong in both directions - a stroke drawn
    /// in the page's own colour is not the page, and on a textured paper the
    /// ground the reader sees is not <c>NotePage.Background</c>.</para></summary>
    public event Action<ToolType, Color, bool>? Sampled;

    /// <summary>11.4 item 29's tilt visualiser was clicked with a mouse. Carries
    /// the screen point, so the host can put its angle entry there.</summary>
    public event Action<Vector2>? RulerDialRequested;

    // ---- tool state -------------------------------------------------------
    public ToolType Tool { get; private set; } = ToolType.Pen;
    public PenType Pen { get; set; } = PenType.Standard;
    public Color PenColor { get; set; } = Color.FromArgb(255, 20, 20, 19);
    public float PenSize { get; set; } = 3.5f;
    public float PenSensitivity { get; set; } = 1f;
    public float PenStabiliser { get; set; }
    // Ink opacity of the live tool, 0-1 (1 = opaque). Fed from PenPreset.Opacity
    // and scrubbed by the dial's opacity arc; every renderer path multiplies its
    // own alpha by it, so a translucent pen builds up where it overlaps.
    public float PenOpacity { get; set; } = 1f;
    public List<float>? PenPressureCurve { get; set; }
    // Two-control-point pressure response (#curve v2). When set it is baked down
    // to the 6-float legacy curve the width renderer already interprets, so it
    // drives stroke width without growing per-stroke JSON. null = use the
    // PenPressureCurve above unchanged.
    public PressureCurve2? PenPressureResponse { get; set; }
    // Default font + point size applied to newly created text boxes and to the
    // first characters typed into them, so a size/font chosen with no box selected
    // is honoured instead of falling back to the RichEdit default (#2, #8).
    public float PendingFontSize { get; set; } = 16f;
    public string PendingFontFamily { get; set; } = "Lora";
    /// <summary>CONCEPTS-REF 25.5: THE COLOUR THE NEXT TEXT BOX IS CREATED IN.
    /// Null - the default - means "follow the page's ink convention", which is
    /// what every box did before 25. Its own remembered setting rather than the
    /// active pen's colour, and 25.5 argues why: a pen is a thing you draw
    /// with, and choosing a red pen to annotate a diagram is not a request for
    /// red prose. Persisted as Library.DefaultTextColor through
    /// <see cref="TextColourChosen"/>.</summary>
    public string? PendingTextColor { get; set; }

    /// <summary>Raised when the user picks a colour for TYPED WORDS, so the shell
    /// can remember it across launches. Deliberately an event rather than a reach
    /// into the library from here: this control has never known what a library
    /// is, and 25 is not the section that should teach it.</summary>
    public event Action<string>? TextColourChosen;

    /// <summary>What the colour controls SHOW while the Text tool is in hand -
    /// the pending colour, or the ink a box would actually be drawn in if it were
    /// created right now. Never a bare white or a bare black: on a dark page the
    /// honest answer is the light ink.</summary>
    public Color TextColourNow =>
        PendingTextColor is { Length: > 0 } hex
            ? ColorUtil.Parse(hex)
            : PageTheme.TextInk(_page != null ? ColorUtil.Parse(_page.Background) : Colors.White);
    public EraserMode EraserMode { get; set; } = EraserMode.Object;
    // How the point-eraser treats what it crosses (§7.c). Object mode always
    // removes whole strokes; these styles shape the Point-mode geometry result.
    public EraserStyle EraserStyle { get; set; } = EraserStyle.HardMask;
    // Eraser radius in world units; 0 = derive from the active pen size exactly
    // as before. Library.EraserSize feeds this.
    public double EraserSize { get; set; }
    // ---- selection tool options (UI-SPEC-V2 1.3), surfaced by the dial ----
    /// <summary>Lasso shape: false = freeform, true = square (rubber-band box).</summary>
    public bool LassoSquare { get; set; }
    /// <summary>true = a stroke is caught when it is PARTIALLY inside the lasso;
    /// false = only when every one of its points is inside.</summary>
    public bool SelectPartial { get; set; } = true;

    // ---- 17.10: the rest of the mouse tool's bottom menu ------------------
    // These three are the mouse tool's OWN state and they live here, beside
    // SelectPartial, because that is where the option this menu's second control
    // edits has always lived. 18.1 is explicit that where the layer scope is
    // stored is the mode bar's business and that the layer model must not grow a
    // second home for it, so it is a tool mode on the tool, like the other two.

    /// <summary>17.10's first control. <see cref="MousePick.Lasso"/> selects by
    /// enclosing, <see cref="MousePick.Item"/> by clicking the thing itself. The
    /// Partial/Complete control only appears while this is Lasso, because
    /// "partially inside" is meaningless for a click.</summary>
    public MousePick Pick { get; set; } = MousePick.Lasso;

    /// <summary>17.10's second control, drawn as a padlock: OPEN means INCLUDE
    /// (a locked stroke is still caught), CLOSED means IGNORE (the lasso passes
    /// over it). The capture shows Include, which is what selection did before
    /// it could be asked.</summary>
    public bool IgnoreLocked { get; set; }

    /// <summary>17.10's third control, and the one seam into the layer model.
    /// <c>AllLayers</c> is <see cref="LayerScope"/>'s zero value on purpose
    /// (18.1), so an unset scope means today's behaviour: everything is in
    /// scope. Read by <see cref="InScopeForSelect"/> and nowhere else.</summary>
    public LayerScope SelectScope { get; set; } = LayerScope.AllLayers;

    // ---- 17.9: the mode bar's two on/off modes ---------------------------

    /// <summary>17.9's Scale, on. Off, a corner is a corner and the rest of the
    /// selection box moves - which is what dragging a selection has always done.
    /// On, the WHOLE box is a scale grip: a press picks the corner it is nearest
    /// and drags from the one opposite. Nothing about the corner handles
    /// changes; the mode adds a second way in, it does not take one away.</summary>
    public bool ScaleMode { get; set; }

    /// <summary>17.9's "and stretch". False scales uniformly from the radial
    /// ratio, which is what this path did when it had one factor; true takes
    /// each axis from its own distance to the anchor, so the aspect is
    /// free.</summary>
    public bool ScaleStretch { get; set; }

    public bool RulerMode { get; set; }
    // On-screen ruler angle in degrees (any value, not just 15° steps) (#21).
    public double RulerAngle { get; set; }
    public bool HandDrawMode { get; set; }
    public MouseMode MouseMode { get; set; } = MouseMode.Auto;

    private const int InkCacheThreshold = 2500;   // static ink only for very large pages (the aggressive 600 + per-stroke cache-append dropped just-drawn ink, #hotfix)
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

    // ---- 16.10: click to select, without dragging ----
    // "make just holding selection button on pen and clicking (not dragging to
    // select) select the stroke."
    //
    // A press that never travels this far is a CLICK, however long it is held —
    // the test is movement, not time, because a held press that never moves is
    // exactly what the user described. SCREEN pixels deliberately: the canvas
    // runs 0.1x to 16x (16.1), so the same wobble of the hand is 50 world units
    // at one end and 0.3 at the other, and a world threshold would mean a
    // different gesture at every zoom. The barrel button's own tap-vs-drag test
    // has always used this number; now it reads it from here.
    private const float ClickSlopPx = 8f;
    // How far past a stroke's own painted width a click still counts, again in
    // screen pixels. The stroke's size does the rest of the work — see
    // HitStrokeForClick, which follows the eraser's rule (FindStrokeNear).
    private const float ClickHitPadPx = 10f;
    // Armed at press when selection is the active modality AND the press landed
    // on empty ground, so no other gesture (a grab, a resize, a selection move)
    // has already claimed it. Read on release.
    private bool _clickSelect;
    private bool _clickSelectMoved;
    // True when the whole tool is selection (the Select tool), as opposed to a
    // modality reached through a button. Only then does a click on empty canvas
    // mean "deselect and nothing else"; the barrel keeps its context menu (#44)
    // and the mouse modes keep their title/date/caret click.
    private bool _clickSelectDeselectsEmpty;
    private Vector2 _clickSelectStartScreen;
    private Vector2 _clickSelectStartWorld;

    /// <summary>Arms 16.10's click-to-select for this gesture. Called from every
    /// press where selection is the active modality and the press found empty
    /// ground: the Select tool, the pen's barrel button, and the mouse's Select
    /// mode.</summary>
    private void ArmClickSelect(Vector2 screen, Vector2 world, bool deselectsEmpty)
    {
        _clickSelect = true;
        _clickSelectMoved = false;
        _clickSelectDeselectsEmpty = deselectsEmpty;
        _clickSelectStartScreen = screen;
        _clickSelectStartWorld = world;
    }

    public UndoRedoManager UndoManager { get; } = new();
    public NotePage? Page => _page;
    public RichEditBox? ActiveTextBox { get; private set; }
    // survives focus loss — menus steal focus before their click lands (#12-batch4)
    public RichEditBox? LastTextBox { get; private set; }

    public event Action? ContentChanged;
    public event Action<RichEditBox?>? ActiveTextChanged;
    public event Action? ReplayEnded;
    public event Action? TitleClicked;
    public event Action? DateClicked;
    /// <summary>Right-click / pen barrel-tap / touch long-press: position is in
    /// InkSurface-local coordinates so the menu can be shown there.</summary>
    public event Action<Point>? ContextMenuRequested;
    public event Action<PenStroke>? StrokeTapped;

    // Comment pins (#roadmap): a modal placing tool + a flash-highlight on undo.
    public event Action<PageComment, Point>? CommentActivated;
    public bool CommentMode { get; set; }
    public bool ShowResolvedComments { get; set; } = true;
    // pins stay hidden outside comment mode unless the user opts in (#A3)
    public bool ShowCommentsAlways { get; set; }
    private Rect? _flashRect;
    private long _flashStartMs;

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

    // Pen repair (#2-batch2): compensates for faulty pen hardware. When on, a
    // pen-down right where a stroke just ended resumes that stroke (bridging a
    // momentary contact drop-out), and a tiny stroke at a fresh stroke's end is
    // discarded as lift-bounce.
    public bool PenRepairBridge { get; set; }   // resume a stroke after a contact drop-out
    public bool PenRepairDots { get; set; }     // drop the lift-bounce dot at a stroke's end

    // Motion blur (#A5): while the view pans/zooms, frames draw through a
    // Gaussian blur proportional to recent view velocity, decaying to sharp
    // within ~150ms of the motion stopping. Never active mid pen gesture.
    public bool MotionBlur { get; set; }
    private const float BlurVelocityMin = 350f;   // px/s — slower motion stays sharp
    private readonly DispatcherTimer _blurDecayTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private float _blurVelocity;                  // low-passed view speed, px/s
    private Vector2 _blurPrevOffset;
    private float _blurPrevZoom = 1f;
    private long _blurPrevMs;
    private CanvasRenderTarget? _blurScratch;     // intermediate target, alive only while blurred

    // the app accent, pushed from MainWindow so selection chrome, handles and
    // table adorners match the rest of the app instead of a hardcoded orange
    public Color Accent { get; set; } = Color.FromArgb(255, 217, 119, 87);
    // Real monitor width in DIPs, pushed by MainWindow, for the text-box growth
    // ceiling (half the physical screen); 0 falls back to the window width (#15).
    public double ScreenWidthDip { get; set; }

    // ---- page chrome + export switches (UI-SPEC-V3 B / J) ----------------
    // The page name and date are drawn ON the page today. When the floating
    // top-left bar is up it carries the title instead, and the spec says the
    // page must stop drawing them - so the bar owns this flag rather than the
    // surface guessing (UI-SPEC-V3 B, I).
    public bool ShowPageHeader { get; set; } = true;

    // Set for the duration of an export capture. Chromeless drops the editing
    // furniture (the header and the selection marquee + handles) so an exported
    // image is the drawing, never the editor; the two Omit flags are the export
    // pane's "Include Background" / "Include Grid" toggles. All three are
    // restored by the caller the moment the frame is taken.
    public bool ExportChromeless { get; set; }
    public bool ExportOmitBackground { get; set; }
    public bool ExportOmitGrid { get; set; }

    // Hold-still shape recognition. It has always run unconditionally; the
    // Precision panel needs a real switch for it (UI-SPEC-V3 I), and a user who
    // draws deliberately wobbly shapes needs one too.
    public bool ShapeRecognition { get; set; } = true;
    private PenStroke? _lastCommitted;
    private long _lastCommitMs;
    private Vector2 _lastCommitEnd;
    private Vector2 _wetStart, _wetEnd;

    private Vector2 _eraseLast;
    private EraserMode _gestureEraserMode;
    // Style latched at pen-down so mid-gesture setting changes never split one
    // erase stroke across two behaviours.
    private EraserStyle _gestureEraserStyle;
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
    private ShapeElement? _activeShapeBack;
    /// <summary>The single selected shape or image. A PROPERTY rather than a
    /// field so that all twenty-odd places that set it publish the selection
    /// (CONCEPTS-REF 16.3 / 16.9) without any of them having to remember to.
    /// Adding the twenty-first cannot forget.</summary>
    private ShapeElement? _activeShape
    {
        get => _activeShapeBack;
        set
        {
            if (ReferenceEquals(_activeShapeBack, value)) return;
            _activeShapeBack = value;
            PublishSelection();
        }
    }
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
    // rotation centre frozen at resize-drag start: converting the live pointer
    // against the CURRENT centre made rotated shapes drift while resizing (#17-batch2)
    private Vector2 _resizeCenter;
    private double _resizeAspect;
    private Vector2 _shapeStart, _resizeAnchor;
    private (double X, double Y, double W, double H) _shapeOrig;
    private List<(int Index, ShapeElement Shape)> _eraseRemovedShapes = new();
    private readonly CanvasTextFormat _labelFormat = new() { FontSize = 15 };

    private bool _rectSelect;
    private Vector2 _rectStart, _rectCur;

    private readonly Dictionary<string, CanvasBitmap?> _bitmaps = new();
    private readonly HashSet<string> _bitmapLoading = new();

    // Last view that was numerically valid, kept so OnViewChanged can refuse a
    // non-finite one (16.1). Not a bound - see the note there.
    private Vector2 _lastGoodOffset = Vector2.Zero;
    private float _lastGoodZoom = 1f;
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
        // CanvasVirtualControl (#cvc): the draw callback receives INVALIDATED
        // REGIONS instead of always painting the whole viewport, so wet ink can
        // repaint just the pixels around the fresh segment on dense pages.
        _canvas = new CanvasVirtualControl();
        _canvas.RegionsInvalidated += OnRegionsInvalidated;
        // A LOST DEVICE orphans every baked paper tile. PaperTextures caches its
        // render targets and image brushes PER DEVICE, so without this the dead
        // device's resources are pinned by that dictionary for the rest of the
        // session and the next draw bakes a second set beside them instead of
        // replacing them. Invalidate went a long time with NO caller at all,
        // which is precisely how that leak survived; this is the device-loss
        // caller it was written for, and SetDisplayDpi below is the other.
        //
        // The reason to watch is NewDevice, not a "DeviceLost" constant: Win2D
        // reports a recovered device loss by handing the control a NEW device
        // through this same event (the enum is FirstTime / NewDevice /
        // DpiChanged).
        //
        // DpiChanged matters too, but not for the page tiles - those are baked
        // at a fixed 96 DPI on purpose, so one tile pixel is one world unit and
        // a display change cannot affect them. It matters for the settings
        // swatches, which are CanvasImageSource and are XAML-composited:
        // rasterised at 96 on a 125% or 150% display they come out soft. So the
        // DPI is pushed into PaperTextures, which re-bakes only if it moved.
        _canvas.CreateResources += (_, args) =>
        {
            var reason = args.Reason;
            if (reason == Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)
                PaperTextures.Invalidate();
            if (reason != Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.FirstTime)
                PaperTextures.SetDisplayDpi(_canvas.Dpi);
        };
        // FirstTime fires before the control has its real DPI in some shells, so
        // the initial value is taken from the XamlRoot once we are in the tree.
        Loaded += (_, _) =>
        {
            try { PaperTextures.SetDisplayDpi((float)(XamlRoot?.RasterizationScale ?? 1.0) * 96f); }
            catch { }
        };
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

        // Motion blur (#A5): after the view stops moving, halve the tracked
        // velocity each tick (sharp within ~150ms) and repaint until it lands.
        _blurDecayTimer.Tick += (_, _) =>
        {
            _blurVelocity *= 0.5f;
            if (_blurVelocity <= BlurVelocityMin * 0.5f || !MotionBlur)
            {
                _blurVelocity = 0f;
                _blurDecayTimer.Stop();
                _blurScratch?.Dispose();
                _blurScratch = null;
            }
            _canvas.Invalidate();
        };

        // Receive the first typed character so a pending caret can spawn a box.
        CharacterReceived += OnCharacterReceived;
        KeyDown += OnKeyDown;

        ContentChanged += () =>
        {
            // text-box keystrokes never touch ink, so they leave the static-ink
            // cache valid; every other edit invalidates it (#43). The per-stroke
            // cache-append optimisation was removed — it dropped just-drawn ink.
            if (_inkCacheTextOnly) return;
            _inkCacheDirty = true;
        };
        // After a zoom settles, re-render the static-ink cache at the exact new
        // zoom so big pages stop looking slightly soft between the 0.50-1.05x
        // rebuild thresholds (#roughedge).
        _zoomSettleTimer.Tick += (_, _) =>
        {
            _zoomSettleTimer.Stop();
            if (_page != null && _page.Strokes.Count >= InkCacheThreshold &&
                Math.Abs(ViewZoom - _inkCacheBuiltZoom) > 0.01f)
            {
                _inkCacheDirty = true;
                _canvas.Invalidate();
            }
        };
        Unloaded += (_, _) => _canvas.RemoveFromVisualTree();
    }

    // =======================================================================
    // View transform
    // =======================================================================
    /// <summary>The zoom range, finalised by the user at 0.1x - 16x.
    ///
    /// <para>ONE definition, because there were three, at two different values:
    /// <see cref="ZoomAround"/> clamped 0.1..8, <see cref="SetView"/> clamped
    /// 0.05..8, and page restore clamped 0.1..8. Copies of a range drift the
    /// moment one is retuned - the fullscreen strip's clearance reached 2 DIP
    /// exactly this way - and the odd 0.05 meant a programmatic view could sit
    /// at a zoom the user could neither have chosen nor return to.</para>
    ///
    /// <para>This is an AFFORDANCE, not a canvas bound. 16.1 removed the wall
    /// that made the canvas finite; a zoom range stops you getting lost at
    /// scales where a stroke is subpixel or the whole page is a speck, which is
    /// a different concern from extent. Fit-to-content keeps its own 4x ceiling
    /// on top of this - it is a heuristic about not blowing a small drawing up,
    /// not a limit on where the user may go.</para></summary>
    public const float MinZoom = 0.1f, MaxZoom = 16f;

    private Vector2 ToWorld(Vector2 screen) => (screen - ViewOffset) / ViewZoom;
    private Vector2 ToWorld(Point screen) => ToWorld(new Vector2((float)screen.X, (float)screen.Y));

    private void ZoomAround(Vector2 screenPivot, float newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var world = ToWorld(screenPivot);
        ViewZoom = newZoom;
        ViewOffset = screenPivot - world * newZoom;
        OnViewChanged();
        // crisp cache re-render once the zoom stops moving (#roughedge)
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
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
        ViewZoom = Math.Clamp(zoom, MinZoom, MaxZoom);
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

    /// <summary>Frames an arbitrary WORLD rectangle, the way FitToContent frames
    /// the whole page. The export pane uses it to point the viewport at the
    /// selection before a raster capture.</summary>
    public void FitToRect(Windows.Foundation.Rect r, double marginPx)
    {
        double vw = ActualWidth, vh = ActualHeight;
        if (vw < 10 || vh < 10 || r.Width <= 0 || r.Height <= 0) return;
        double zx = (vw - 2 * marginPx) / r.Width;
        double zy = (vh - 2 * marginPx) / r.Height;
        float zoom = (float)Math.Clamp(Math.Min(zx, zy), 0.05, 4.0);
        SetView(new Vector2((float)(marginPx - r.X * zoom), (float)(marginPx - r.Y * zoom)), zoom);
    }

    private void PanBy(Vector2 screenDelta)
    {
        ViewOffset += screenDelta;
        OnViewChanged();
    }

    private void OnViewChanged()
    {
        // CONCEPTS-REF 16.1: THE CANVAS IS INFINITE. Pan has no stop in any
        // direction, so NOTHING here may bound ViewOffset. Two walls used to
        // stand at this spot and both are gone:
        //
        // * The bottom/right clamp (27e999b, "Invisible-ink root cause (view
        //   teleport)"). It pinned the view inside max(page size, content) + 900
        //   world units, which on a default page is a wall about 2400 x 3100 out
        //   - roughly one page plus a screen, which is exactly the symptom the
        //   user reported. It was layer 1 of a THREE-layer fix for a palm-fling
        //   that hurled the view thousands of px past the content and then SAVED
        //   it there. Layers 2 and 3 are the ones that actually address that bug
        //   and both stay untouched: OnManipStarted rejects a touch pan within
        //   400ms of a pen, and LoadPage self-heals a restored view that shows
        //   none of the page's content by jumping to it. This clamp added no
        //   protection those two do not already give; it only made the canvas
        //   finite.
        //
        // * The top/left OriginMargin clamp, here since 1.0, which kept the world
        //   origin within 48px of the viewport's top-left corner and so made
        //   every negative coordinate unreachable. NormalizeContent existed only
        //   to shovel pre-clamp content back inside that wall "so it stays
        //   reachable"; with no wall nothing is unreachable, so the migration and
        //   the image-drop clamp that dodged its trigger zone have gone with it.
        //
        // What is left is a VALIDITY guard, not a bound. A degenerate
        // manipulation (a zero or NaN scale) can produce a non-finite offset or
        // zoom, and one of those poisons every ToWorld() that follows - the view
        // never recovers, and the page saves the poison. Such a value is refused
        // and the last good one kept. Any FINITE offset, however far out, is
        // accepted: there is no distance at which panning stops.
        if (float.IsFinite(ViewOffset.X) && float.IsFinite(ViewOffset.Y))
            _lastGoodOffset = ViewOffset;
        else
            ViewOffset = _lastGoodOffset;
        if (float.IsFinite(ViewZoom) && ViewZoom > 0f)
            _lastGoodZoom = ViewZoom;
        else
            ViewZoom = _lastGoodZoom;
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
        TrackViewVelocity();
        _canvas.Invalidate();
        ViewChanged?.Invoke();
    }

    private void OnManipStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        _touchPanActive =
            e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch &&
            _gestureTool == null && !_replaying &&
            // a palm resting near an active pen must not pan the page (#inkfix)
            Environment.TickCount64 - _lastPenSeenMs > 400;
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
        _page = page;
        // Evict decoded image bitmaps the new page doesn't reference — the cache
        // previously only ever grew, keeping every pasted image of the whole
        // session resident in GPU memory (#perf-roadmap).
        try
        {
            var live = new HashSet<string>(page.Shapes.Where(s => s.ImagePath != null).Select(s => s.ImagePath!));
            foreach (var stale in _bitmaps.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _bitmaps[stale]?.Dispose();
                _bitmaps.Remove(stale);
                _bitmapLoading.Remove(stale);
            }
        }
        catch { }
        _inkCacheDirty = true;   // fresh page, fresh ink cache (#43)
        _gridDirty = true;       // fresh page, fresh spatial index
        UndoManager.Clear();
        ClearSelection();
        _activeShape = null;
        ViewOffset = new Vector2((float)page.ViewX, (float)page.ViewY);
        ViewZoom = Math.Clamp((float)page.ViewZoom, MinZoom, MaxZoom);
        // Heal cells eaten by the old empty-box cleanup (#cellfix): every grid
        // slot of every table needs a TextElement, EXCEPT slots covered by a
        // merged cell's span (merges legitimately remove hidden cells).
        try
        {
            foreach (var tb in page.Shapes.Where(s => s.Kind == ShapeKind.Table))
            {
                var cw = TableColWidths(tb);
                var rh = TableRowHeights(tb);
                bool added = false;
                for (int r = 0; r < rh.Length; r++)
                    for (int c = 0; c < cw.Length; c++)
                    {
                        bool covered = page.Texts.Any(t => t.TableId == tb.Id &&
                            r >= t.TableRow && r < t.TableRow + Math.Max(1, t.CellRowSpan) &&
                            c >= t.TableCol && c < t.TableCol + Math.Max(1, t.CellColSpan));
                        if (covered) continue;
                        page.Texts.Add(new TextElement
                        {
                            TableId = tb.Id, TableRow = r, TableCol = c,
                            X = tb.X + 4, Y = tb.Y + 2, Width = Math.Max(28, cw[c] - 8)
                        });
                        added = true;
                    }
                if (added) ReflowTableCells(tb);
            }
        }
        catch { }
        RebuildTextLayer();
        OnViewChanged();
        // Self-heal corrupt saved views (#inkfix): the palm-fling bug SAVED
        // runaway offsets with the page, so on open the ink looked gone. If the
        // restored view shows none of the page's content, jump to the content.
        try
        {
            if (ActualWidth > 10 && ContentBoundsWorld() is { } cb && (cb.Width > 1 || cb.Height > 1))
            {
                var tl = ToWorld(new Vector2(0, 0));
                var br = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
                var vis = new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
                vis.Intersect(cb);
                if (vis.IsEmpty || vis.Width < 1) FitToContent(48);
            }
        }
        catch { }
    }

    // NormalizeContent stood here: a load-time migration that shifted every
    // stroke, text and shape down-right whenever anything sat more than 10 world
    // units above or left of the origin. Its whole purpose was the OriginMargin
    // wall - content out there could not be panned to, so it was moved where it
    // could. 16.1 removes the wall, which removes the premise: negative
    // coordinates are now ordinary canvas. Keeping the migration would have been
    // strictly worse than the bug it fixed, because ink the user had just drawn
    // up and to the left would be silently relocated on the next page load -
    // which is the "#27-batch2" failure (an equation insert moving the notes)
    // turned from an edge case into the normal path.

    /// <summary>True while the MOUSE TOOL is the live tool (17.10).
    ///
    /// <para>Every test that used to read <c>Tool == ToolType.Select</c> reads
    /// this instead. <see cref="SetTool"/> folds <c>Select</c> into
    /// <c>Mouse</c>, so the property can never be <c>Select</c> and this is
    /// simply the new spelling - but it is a named property rather than a bare
    /// comparison because there are a dozen sites and the fold has to be
    /// impossible to half-apply.</para></summary>
    public bool MouseTool => Tool == ToolType.Mouse;

    public void SetTool(ToolType tool)
    {
        // 17.10 folds the lasso into the MOUSE TOOL. Select is kept in the enum
        // because a dial sector or a pen-row cell stores its tag by NAME and
        // every library.json in existence spells it "Select" - but it is folded
        // here, on the way in, so exactly one of the two is ever live and no
        // downstream test has to know about both.
        if (tool == ToolType.Select) tool = ToolType.Mouse;
        Tool = tool;
        // 11.4 item 29: the ruler IS a tool now, so the straightedge follows the
        // selection instead of a separate switch on the top bar. Selecting any
        // other tool puts it away - which is what made the old toggle awkward,
        // since it could be left on under a tool that had no use for it.
        RulerMode = tool == ToolType.Ruler;
        CancelPendingText();
        // 17.11's rotate tool acts ON the selection, so unlike every other tool
        // it must not clear it: choosing it in order to turn what you just
        // lassoed would otherwise throw the lasso away first. Pan does not touch
        // the selection either - it moves the view, not the page.
        if (tool is not (ToolType.Mouse or ToolType.Rotate or ToolType.Pan))
        {
            ClearSelection();
            _activeShape = null;
        }
        _textLayer.IsHitTestVisible = tool is ToolType.Text or ToolType.Mouse;
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
        var act = UndoManager.PeekUndo;
        bool touchesText = act?.TouchesText ?? true;
        FlushTexts();
        UndoManager.Undo(_page);
        _gridDirty = true;
        ClearSelection();
        _activeShape = null; // don't leave a removed shape's selection box behind
        // rebuilding every RichEditBox is expensive on text-heavy pages — only
        // do it when the undone action could actually change the text layer
        if (touchesText) RebuildTextLayer();
        if (act != null) FlashAction(act);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void Redo()
    {
        if (_page == null || _replaying) return;
        var act = UndoManager.PeekRedo;
        bool touchesText = act?.TouchesText ?? true;
        FlushTexts();
        UndoManager.Redo(_page);
        _gridDirty = true;
        ClearSelection();
        _activeShape = null;
        if (touchesText) RebuildTextLayer();
        if (act != null) FlashAction(act);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // ---- comment pins + undo flash (#roadmap) ----
    private void FlashAction(IPageAction act)
    {
        try
        {
            if (_page != null && act.AffectedBounds(_page) is { } r && r.Width >= 0 && r.Height >= 0)
            {
                _flashRect = r;
                _flashStartMs = Environment.TickCount64;
            }
        }
        catch { }
    }

    private PageComment? HitComment(Vector2 world)
    {
        if (_page == null) return null;
        float r = 14f / ViewZoom;
        for (int i = _page.Comments.Count - 1; i >= 0; i--)   // topmost first
        {
            var c = _page.Comments[i];
            if (c.Resolved && !ShowResolvedComments) continue;
            if (Vector2.Distance(world, new Vector2((float)c.X, (float)c.Y)) <= r) return c;
        }
        return null;
    }

    public void ResolveComment(PageComment c, bool resolved)
    {
        c.Resolved = resolved;
        ContentChanged?.Invoke();
        _canvas.Invalidate();
    }

    public void DeleteComment(PageComment c)
    {
        if (_page == null) return;
        _page.Comments.Remove(c);
        ContentChanged?.Invoke();
        _canvas.Invalidate();
    }

    public void NotifyCommentEdited()   // called after the flyout edits a comment's text
    {
        ContentChanged?.Invoke();
        _canvas.Invalidate();
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
        PublishSelection();
        _canvas.Invalidate();
    }

    private bool HasMultiSelection => _selected.Count > 0 || _selShapes.Count > 0 || _selTexts.Count > 0;

    public bool HasSelection => HasMultiSelection;

    /// <summary>True when Delete should remove something: a multi-selection or
    /// the active shape/image.</summary>
    public bool HasDeletable => HasMultiSelection || _activeShape != null;

    public void DeleteSelection()
    {
        // 16.2's padlock. A lock that stops a drag but not a delete is not a
        // lock; the waste bin greys instead, so the refusal is visible before it
        // is attempted rather than after.
        if (_page == null || AnyLocked) return;
        if (_activeShape != null)
        {
            int idx = _page.Shapes.IndexOf(_activeShape);
            PushAction(new RemoveShapesAction(new List<(int, ShapeElement)> { (idx, _activeShape) }), _page);
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
        PushAction(new RemoveMixedAction(strokes, shapes, texts), _page);
        ClearSelection();
        RebuildTextLayer();
        ContentChanged?.Invoke();
    }

    public void DuplicateSelection()
    {
        if (_page == null) return;
        FlushTexts();

        const float offset = 40f;

        // One rule for both branches, and it lives on the models
        // (Models/ElementClone.cs) so it can be proved headlessly rather than
        // asserted: tools/CloneRoundTrip runs this exact function. What used to be
        // here was two hand-written initialiser lists that had drifted apart, and
        // between them they dropped the padlock, the layer key, the pen, the
        // opacity and every table cell's styling.
        var (clonedStrokes, clonedShapes, clonedTexts) = _activeShape != null
            ? ElementClone.Duplicate(Array.Empty<PenStroke>(), new[] { _activeShape },
                                     Array.Empty<TextElement>(), _page.Texts, offset)
            : HasMultiSelection
                ? ElementClone.Duplicate(_selected, _selShapes, _selTexts, _page.Texts, offset)
                : (new List<PenStroke>(), new List<ShapeElement>(), new List<TextElement>());

        if (clonedStrokes.Count > 0 || clonedShapes.Count > 0 || clonedTexts.Count > 0)
        {
            PushAction(new AddMixedAction(clonedStrokes, clonedShapes, clonedTexts), _page);
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
    private float EraserRadius => EraserSize > 0 ? (float)EraserSize : Math.Max(8f, PenSize * 2.2f);

    // The pen's eraser tip / inverted end / extra barrel buttons all act as an
    // eraser. Shared by pointer-down routing and the hover-cursor detection.
    private static bool IsEraserButtons(Microsoft.UI.Input.PointerPointProperties p, bool isPen) =>
        p.IsEraser || p.IsInverted ||
        (isPen && (p.IsXButton1Pressed || p.IsXButton2Pressed || p.IsMiddleButtonPressed));

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        CancelPendingText(); // a fresh press dismisses any blinking caret
        // _skipNextRightTap is a one-shot that is only ever cleared by being
        // CONSUMED, so a gesture that arms it and then draws no RightTapped -
        // the barrel button's, which the recogniser raises unreliably over a
        // Win2D canvas - leaves it armed to eat somebody else's menu later.
        // Cleared here too (belt and suspenders: whatever armed it belonged to
        // the gesture that has just ended), but this is no longer the actual
        // bound - see the time check in OnRightTapped (4.3). A RightTapped
        // raised by the Menu key / Shift+F10 reaches OnRightTapped with no
        // PointerPressed in front of it at all, so a reset that only fires on
        // the next press could never catch that case by itself.
        _skipNextRightTap = false;
        var pp = e.GetCurrentPoint(_canvas);
        var props = pp.Properties;
        var device = e.Pointer.PointerDeviceType;
        bool isPen = device == Microsoft.UI.Input.PointerDeviceType.Pen;
        if (isPen) _lastPenSeenMs = Environment.TickCount64;   // (#inkfix)
        bool isMouse = device == Microsoft.UI.Input.PointerDeviceType.Mouse;
        var screen = new Vector2((float)pp.Position.X, (float)pp.Position.Y);
        var pos = ToWorld(screen);

        // Comment mode is modal: a press opens the pin under it, or drops a new
        // one, and never draws (#roadmap). Right-click still reaches the menu.
        if (CommentMode && !props.IsRightButtonPressed)
        {
            var hit = HitComment(pos);
            if (hit == null)
            {
                hit = new PageComment { X = pos.X, Y = pos.Y };
                _page.Comments.Add(hit);
                ContentChanged?.Invoke();
                _canvas.Invalidate();
            }
            CommentActivated?.Invoke(hit, new Point(screen.X, screen.Y));
            e.Handled = true;
            return;
        }

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
            ArmSkipNextRightTap();
            _activePointer = e.Pointer.PointerId;
            _gestureTool = ToolType.Mouse;
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
            _gestureTool = ToolType.Mouse;
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
                // 16.10: the barrel button IS "the selection button on the pen",
                // so a press-and-release here that never moves selects whatever
                // stroke is under it. Only a click that finds no stroke falls
                // through to the context menu this gesture has always opened.
                ArmClickSelect(screen, pos, deselectsEmpty: false);
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

        // 11.4 item 29: "a tilt visualiser clickable by mouse to type an exact
        // angle." The bubble is the visualiser, and it is tested before anything
        // else can claim the press - it sits in the middle of the page, over
        // whatever happens to be there.
        //
        // Mouse only, deliberately. The same bubble under a finger or a pen tip
        // would eat a stroke started in the centre of the page, and touch has
        // the two-finger twist for this already.
        if (RulerMode && isMouse && RulerBubble().Contains(new Point(pos.X, pos.Y)))
        {
            RulerDialRequested?.Invoke(screen);
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

        // 11.4 item 28 and 10.8. Both sampling tools ANSWER ON PRESS: they take
        // no capture, start no gesture and commit nothing to the page, so they
        // are handled here rather than as two more cases in the switch below,
        // every arm of which is about a drag.
        if (tool is ToolType.Eyedropper or ToolType.Mix)
        {
            var hit = SampleColorAt(screen, out bool bare);
            if (hit is Color got) Sampled?.Invoke(tool, got, bare);
            e.Handled = true;
            return;
        }

        // 11.4 item 29: the ruler is the PEN with a straightedge under it.
        // Everything below - the mouse modes, the selection grabs, the stroke
        // itself - is the pen's behaviour, and RulerMode is what bends the
        // committed points onto the edge (see BuildRulerPoints). Folding it in
        // here is what keeps the two from drifting apart; a ninth case in the
        // switch would have to be kept in step with the pen's by hand.
        if (tool == ToolType.Ruler) tool = ToolType.Pen;

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
                    _gestureTool = ToolType.Mouse;
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
                        _gestureTool = ToolType.Mouse;
                        _canvas.CapturePointer(e.Pointer);
                        if (handle != null)
                        {
                            _resizingShape = true;
                            _resizeAnchor = handle.Value;
                            _resizeCenter = ShapeCenter(_activeShape);
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
                        _gestureTool = ToolType.Mouse;
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
                            _gestureTool = ToolType.Mouse; // reuse the move/resize machinery
                            if (handleP != null)
                            {
                                _resizingShape = true;
                                _resizeAnchor = handleP.Value;
                                _resizeCenter = ShapeCenter(_activeShape);
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
                // Pen repair (#2-batch2): a faulty pen that momentarily loses
                // contact ends the stroke and instantly starts a new one. If a
                // pen-down lands right where a stroke just finished, pick that
                // stroke back up and keep drawing as if nothing happened.
                if (PenRepairBridge && !RulerMode && _lastCommitted != null && _page != null &&
                    Environment.TickCount64 - _lastCommitMs < 200 &&
                    Vector2.Distance(pos, _lastCommitEnd) < 14f / ViewZoom &&
                    _page.Strokes.Count > 0 && ReferenceEquals(_page.Strokes[^1], _lastCommitted))
                {
                    var resume = _lastCommitted;
                    UndoManager.TryDiscardTop(a => a is AddStrokeAction asa && ReferenceEquals(asa.Stroke, resume));
                    RemoveStroke(resume);
                    _wet = new List<StrokePoint>(resume.Points) { new(pos.X, pos.Y, props.Pressure) };
                    _lastCommitted = null;
                }
                else
                {
                    _wet = new List<StrokePoint> { new(pos.X, pos.Y, props.Pressure) };
                }
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
                _gestureEraserStyle = EraserStyle;
                _eraseRemoved = new List<(int, PenStroke)>();
                _eraseRemovedShapes = new List<(int, ShapeElement)>();
                _gestureFragments = new HashSet<PenStroke>();
                _eraseLast = pos;
                EraseAt(pos, pos);
                break;

            case ToolType.Mouse:
            {
                float tol = 10f / ViewZoom;
                if (TryBeginShapeOrSelectionDrag(pos, tol)) break;
                _activeShape = null;
                ClearSelection();
                // 17.10's first control. ITEM PICKER starts no lasso at all - it
                // is the click gesture and nothing else, so a drag across the
                // page leaves no rubber band behind it. LASSO is what the tool
                // has always done: square lasso reuses the rubber-band rectangle
                // the mouse path already tracks and commits, so both shapes are
                // one code path.
                if (Pick == MousePick.Lasso)
                {
                    if (LassoSquare) { _rectSelect = true; _rectStart = pos; _rectCur = pos; }
                    else _lasso = new List<Vector2> { pos };
                }
                // 16.10: selection reached as a TOOL. A drag from here still
                // lassoes, freeform or square; a press-and-release that never
                // moves selects the stroke under it, and on empty canvas means
                // nothing more than the deselect ClearSelection just did.
                ArmClickSelect(screen, pos, deselectsEmpty: true);
                break;
            }

            // 17.11. Pan reuses the mouse's own pan state rather than a second
            // one: PanBy is already the single place the view offset moves, and
            // a tool with its own copy is how two pans drift apart.
            case ToolType.Pan:
                _mousePanning = true;
                _mousePanLast = screen;
                break;

            // 17.11a. A press arms the turn and captures the subject; the sweep
            // turns it live at a free angle and the release commits one action.
            // A press that never moves commits nothing - see CommitGesture.
            case ToolType.Rotate:
                BeginRotateGesture(pos);
                break;

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

        _gestureTool = ToolType.Mouse;

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
        // 16.10: MouseMode.Select is the third way selection becomes the active
        // modality, so a click there selects the stroke under it too. Auto is
        // left alone on purpose — its click already means title, date, text box
        // or a fresh caret, and rubber-banding is only half of what it does.
        if (MouseMode == MouseMode.Select) ArmClickSelect(screen, pos, deselectsEmpty: false);
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
                _resizeCenter = ShapeCenter(_activeShape);
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
    // 17.9's Scale is "on/off, AND STRETCH", so the scale carries a factor PER
    // AXIS. Uniform keeps both equal from the radial ratio, which is exactly
    // what this path did when it had one number; stretch takes each axis from
    // its own distance to the anchor. One pair of numbers rather than a second
    // scale path, because a second one is how the live drag and the committed
    // action end up disagreeing about where the selection went.
    private float _scaleFactor = 1f, _scaleFactorY = 1f;
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
        // 16.2's padlock, enforced. A locked selection still SHOWS its corner
        // handles - hiding them would leave no clue why dragging does nothing -
        // but the drag refuses to start.
        if (!HasMultiSelection || _selBounds.IsEmpty || AnyLocked) return false;
        var corners = SelCorners();
        for (int i = 0; i < 4; i++)
        {
            // 17.9's Scale MODE. Off, a corner is a corner and the rest of the
            // box moves - today's behaviour, unchanged. On, the whole box is a
            // scale grip: the press picks the corner it is nearest and drags
            // from the one opposite. That is what makes Scale a mode rather than
            // a label on a gesture that was already there.
            if (!ScaleMode && Vector2.Distance(pos, corners[i]) > Math.Max(tol, 9f / ViewZoom)) continue;
            if (ScaleMode && NearestCorner(pos, corners) != i) continue;
            _scalingSel = true;
            _scaleAnchor = corners[(i + 2) % 4];   // opposite corner stays put
            _scaleStartPos = pos;
            _scaleFactor = _scaleFactorY = 1f;
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

    private static int NearestCorner(Vector2 p, Vector2[] corners)
    {
        int best = 0;
        float bd = float.MaxValue;
        for (int i = 0; i < corners.Length; i++)
        {
            float d = Vector2.DistanceSquared(p, corners[i]);
            if (d < bd) { bd = d; best = i; }
        }
        return best;
    }

    private void ApplyScaleLive()
    {
        if (_scaleStrokes == null || _scaleShapes == null || _scaleTexts == null) return;
        float f = _scaleFactor, g = _scaleFactorY, ax = _scaleAnchor.X, ay = _scaleAnchor.Y;
        foreach (var (s, xs, ys) in _scaleStrokes)
            for (int i = 0; i < s.Points.Count && i < xs.Length; i++)
            {
                s.Points[i].X = ax + (xs[i] - ax) * f;
                s.Points[i].Y = ay + (ys[i] - ay) * g;
            }
        foreach (var (s, x, y, w, h) in _scaleShapes)
        {
            s.X = ax + (x - ax) * f;
            s.Y = ay + (y - ay) * g;
            s.W = w * f;
            s.H = h * g;
        }
        foreach (var (t, x, y, w) in _scaleTexts)
        {
            t.X = ax + (x - ax) * f;
            t.Y = ay + (y - ay) * g;
            // A text box has a width and no height - it reflows - so the y
            // factor moves it and only the x factor resizes it. Stretching a
            // paragraph vertically is not a thing the model can express, and
            // pretending otherwise would put the box somewhere the user did not
            // drag it.
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
        double ny = ay + (_scaleBoundsOrig.Y - ay) * g;
        _selBounds = new Rect(Math.Min(nx, ax), Math.Min(ny, ay),
            _scaleBoundsOrig.Width * f, _scaleBoundsOrig.Height * g);
        _inkCacheDirty = true;
        SubjectMoved?.Invoke();   // the chrome follows the scale
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
        var cand = StrokeCandidates(p.X - radius, p.Y - radius, p.X + radius, p.Y + radius);
        foreach (var s in _page.Strokes)
        {
            if (cand != null && !cand.Contains(s)) continue;
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

    /// <summary>
    /// The stroke under a click, or null when the click found empty canvas
    /// (16.10). Not to be confused with <see cref="HitStroke"/>, whose flat
    /// tolerance serves the tap-to-inspect gesture.
    ///
    /// TOLERANCE follows the eraser's rule (see FindStrokeNear): pad by the
    /// stroke's OWN size, so a hairline is as clickable as a broad nib, plus a
    /// fixed reach expressed in screen pixels and divided by the zoom here — so
    /// the reach under the pen tip is the same at 0.1x as at 16x. There is no
    /// second notion of "near" in this file.
    ///
    /// OVERLAP resolves to the TOPMOST stroke: the page paints in _page.Strokes
    /// order, so walking it backwards answers with the one drawn on top, which
    /// is the one under the user's eye. Nothing here contradicts the lasso — a
    /// lasso takes every stroke it encloses and never has to choose — and it
    /// matches how the lasso's own result is drawn, newest ink over oldest.
    /// </summary>
    private PenStroke? HitStrokeForClick(Vector2 p)
    {
        if (_page == null) return null;
        float reach = ClickHitPadPx / ViewZoom;
        // The spatial index already inflates every stroke by its own size + 8,
        // so a query box of `reach` cannot drop a stroke that the wider per-
        // stroke pad below would have caught (FindStrokeNear reasons the same).
        var cand = StrokeCandidates(p.X - reach, p.Y - reach, p.X + reach, p.Y + reach);
        for (int i = _page.Strokes.Count - 1; i >= 0; i--)
        {
            var s = _page.Strokes[i];
            var pts = s.Points;
            if (pts.Count == 0) continue;
            if (cand != null && !cand.Contains(s)) continue;
            // 17.10's scope and padlock bind here as well as on the lasso. A
            // click that selects and a lasso that selects are the mouse tool's
            // two gestures, not two tools, so they cannot disagree about what is
            // selectable - which is exactly what a second copy of this test in
            // one of the two paths would eventually produce.
            if (!CanCatch(s.LayerKey, s.Locked)) continue;
            float pad = reach + s.Size;
            s.GetBounds(out float bx0, out float by0, out float bx1, out float by1);
            if (p.X < bx0 - pad || p.X > bx1 + pad || p.Y < by0 - pad || p.Y > by1 + pad) continue;
            if (pts.Count == 1)
            {
                if (Vector2.Distance(p, new Vector2(pts[0].X, pts[0].Y)) <= pad) return s;
                continue;
            }
            for (int j = 1; j < pts.Count; j++)
                if (GeometryUtil.DistToSegment(p,
                        new Vector2(pts[j - 1].X, pts[j - 1].Y),
                        new Vector2(pts[j].X, pts[j].Y)) <= pad)
                    return s;
        }
        return null;
    }

    /// <summary>Makes one stroke the entire selection — what a click leaves
    /// behind (16.10). Deliberately the same tail as SelectWithLasso, so a
    /// clicked stroke and a lassoed one are selected in exactly the same
    /// state and every consumer of the selection sees no difference.</summary>
    private void SelectSingleStroke(PenStroke s)
    {
        _selected.Clear();
        _selectedSet.Clear();
        _selShapes.Clear();
        _selShapeSet.Clear();
        _selTexts.Clear();
        _selected.Add(s);
        _selectedSet.Add(s);
        _activeShape = null;
        RecomputeSelectionBounds();
    }

    private void BeginSelectionMove(Vector2 pos)
    {
        if (AnyLocked) return;   // 16.2's padlock
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
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen)
            _lastPenSeenMs = Environment.TickCount64;   // pen hover counts (#inkfix)
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
        if (_barrelGesture && !_barrelMoved && Vector2.Distance(screen, _barrelStartScreen) > ClickSlopPx)
            _barrelMoved = true;

        // 16.10: the same question for the selection modality at large. Screen
        // space, and movement only — nothing here consults a clock, so a press
        // held still for a second is still a click.
        if (_clickSelect && !_clickSelectMoved && Vector2.Distance(screen, _clickSelectStartScreen) > ClickSlopPx)
            _clickSelectMoved = true;

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
                    int wetBefore = _wet?.Count ?? 0;
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
                    // The virtual-control win (#cvc): while inking, repaint ONLY
                    // the pixels around the fresh segment instead of the whole
                    // viewport — dense pages stop re-drawing per pen move.
                    if (_wet != null && _wet.Count > wetBefore)
                    {
                        float mnX = float.MaxValue, mnY = float.MaxValue, mxX = float.MinValue, mxY = float.MinValue;
                        for (int i = Math.Max(0, wetBefore - 1); i < _wet.Count; i++)
                        {
                            mnX = Math.Min(mnX, _wet[i].X); mxX = Math.Max(mxX, _wet[i].X);
                            mnY = Math.Min(mnY, _wet[i].Y); mxY = Math.Max(mxY, _wet[i].Y);
                        }
                        double pad = PenSize * 3f * ViewZoom + 12;
                        InvalidateScreenRect(new Rect(
                            mnX * ViewZoom + ViewOffset.X - pad, mnY * ViewZoom + ViewOffset.Y - pad,
                            (mxX - mnX) * ViewZoom + 2 * pad, (mxY - mnY) * ViewZoom + 2 * pad));
                    }
                    e.Handled = true;
                    return;
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

            case ToolType.Mouse:
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
                    SubjectMoved?.Invoke();   // the chrome follows the turn
                }
                else if (_resizingShape && _activeShape != null)
                {
                    var sR = _activeShape;
                    ResizeShape(sR, _resizeAnchor, ToShapeLocalAt(sR, pos, _resizeCenter), false, _resizeAspect);
                    // Resizing moves the centre, and rotation pivots about the
                    // centre — so a rotated shape used to swim away from the
                    // pointer. Translate so the grabbed anchor stays pinned at
                    // its original world position (#13-batch3).
                    if (Math.Abs(sR.Rotation) > 0.01)
                    {
                        var cNew = ShapeCenter(sR);
                        var wBefore = RotatePoint(_resizeAnchor, _resizeCenter, sR.Rotation);
                        var wAfter = RotatePoint(_resizeAnchor, cNew, sR.Rotation);
                        sR.X += wBefore.X - wAfter.X;
                        sR.Y += wBefore.Y - wAfter.Y;
                    }
                    SubjectMoved?.Invoke();   // the chrome follows the resize
                }
                else if (_movingShape && _activeShape != null)
                {
                    _activeShape.X = _shapeOrig.X + (pos.X - _shapeStart.X);
                    _activeShape.Y = _shapeOrig.Y + (pos.Y - _shapeStart.Y);
                    // 17.8: THE SINGLE-SHAPE DRAG HAS TO SAY SO TOO.
                    //
                    // SubjectBoundsWorld already reports this shape's live
                    // position - its active-shape branch reads ShapeBounds
                    // straight off the model, which the two lines above have just
                    // rewritten, so unlike the multi-selection it needs no move
                    // offset folded in. What was missing was the NOTIFICATION.
                    // SelectionChrome re-places itself on SubjectMoved and on
                    // nothing else that a pure drag raises: ViewChanged is about
                    // pan and zoom, and SelectionState.Changed drops a publish
                    // whose kind, count and flags match the last one - which a
                    // drag's always do. So the circles and the guides framed
                    // where the attachment STARTED, through the drag and on past
                    // the drop, while the Win2D chrome underneath followed. That
                    // disagreement is what a visual pass saw.
                    SubjectMoved?.Invoke();   // the chrome follows the drag
                }
                else if (_scalingSel)
                {
                    if (ScaleStretch)
                    {
                        // 17.9's stretch: each axis takes its own ratio to the
                        // anchor, so the aspect is free.
                        float x0 = _scaleStartPos.X - _scaleAnchor.X, y0 = _scaleStartPos.Y - _scaleAnchor.Y;
                        float x1 = pos.X - _scaleAnchor.X, y1 = pos.Y - _scaleAnchor.Y;
                        _scaleFactor = Math.Clamp(Math.Abs(x0) < 1f ? 1f : x1 / x0, 0.15f, 10f);
                        _scaleFactorY = Math.Clamp(Math.Abs(y0) < 1f ? 1f : y1 / y0, 0.15f, 10f);
                    }
                    else
                    {
                        float d0 = Vector2.Distance(_scaleStartPos, _scaleAnchor);
                        float d1 = Vector2.Distance(pos, _scaleAnchor);
                        _scaleFactor = _scaleFactorY =
                            Math.Clamp(d0 < 1f ? 1f : d1 / d0, 0.15f, 10f);
                    }
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
                    SubjectMoved?.Invoke();   // the chrome follows the drag
                }
                else
                {
                    _lasso?.Add(pos);
                }
                break;

            // 17.11. Pan is handled before this switch by the _mousePanning
            // block, which is the point of reusing it; 17.11a: Rotate turns the
            // subject live at whatever angle the hand has described so far.
            case ToolType.Rotate:
                RotateDragTo(pos);
                break;

            case ToolType.FreeSpace:
                _spaceDelta = pos.Y - _spaceStartY;
                break;
        }

        e.Handled = true;
        _canvas.Invalidate();
    }

    // Clamped partial invalidation with a full-repaint fallback (#cvc).
    private void InvalidateScreenRect(Rect r)
    {
        try
        {
            var clip = new Rect(0, 0, ActualWidth, ActualHeight);
            clip.Intersect(r);
            if (clip.IsEmpty || clip.Width <= 0 || clip.Height <= 0) return;
            _canvas.Invalidate(clip);
        }
        catch { try { _canvas.Invalidate(); } catch { } }
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
    // 4.3: pairs with the flag above. It used to be a pure one-shot armed by a
    // press or a release and consumed by the RightTapped the gesture
    // recognizer raises for that SAME gesture - fine as long as that
    // RightTapped actually arrives, but it "has been narrowed before, not
    // fixed": the recognizer is documented above as unreliable over a Win2D
    // canvas, and when it never fires the flag stayed armed until the next
    // PointerPressed, of ANY pointer, reset it. A RightTapped raised by the
    // Menu key / Shift+F10 goes straight to OnRightTapped with no
    // PointerPressed in front of it, so it could land arbitrarily long after
    // an unrelated gesture armed the flag and be silently eaten.
    //
    // Bound chosen: TIME. The RightTapped that belongs to a given press or
    // release is delivered in the same input dispatch, comfortably inside a
    // few tens of milliseconds; SkipNextRightTapWindowMs gives it a generous
    // margin and nothing else. Rejected:
    //   - pointer-id bound: RightTappedRoutedEventArgs exposes only a
    //     PointerDeviceType, not the PointerId that armed the flag, so there
    //     is nothing on the consuming side to compare against - and by the
    //     time RightTapped fires, capture has already been released and
    //     _activePointer already cleared by CommitGesture, so even tracking
    //     it ourselves would be comparing against state that gesture already
    //     tore down.
    //   - reset on every event that ends the gesture (PointerReleased/Lost):
    //     one of the two arm sites (the click-select case below) ARMS the
    //     flag from inside that very handler, so "reset on gesture end" would
    //     have to special-case the site that just set it - which collapses
    //     into the same press/release ordering this class already used and
    //     already proved insufficient.
    // This is also the same primitive PenRepairDots already uses a few
    // hundred lines up (Environment.TickCount64 - _lastCommitMs < 160) for an
    // identical shape of problem: a one-shot suppression that must not
    // outlive the gesture that armed it.
    private long _skipNextRightTapArmedMs;
    private const long SkipNextRightTapWindowMs = 500;

    private void ArmSkipNextRightTap()
    {
        _skipNextRightTap = true;
        _skipNextRightTapArmedMs = Environment.TickCount64;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_page == null || _replaying) return;
        if (_skipNextRightTap)
        {
            bool fresh = Environment.TickCount64 - _skipNextRightTapArmedMs <= SkipNextRightTapWindowMs;
            // Always clear on consumption, fresh or not: a stale flag must
            // never survive to poison a LATER RightTapped either.
            _skipNextRightTap = false;
            if (fresh)
            {
                // the press-side right-button gesture already handled this click
                e.Handled = true;
                return;
            }
            // Stale: whatever armed this ended without ever producing a
            // RightTapped, so this RightTapped belongs to something else
            // entirely (most notably the Menu key / Shift+F10, which never
            // passes through the per-press reset above) - fall through and
            // show its menu normally rather than eating it.
        }
        ContextMenuRequested?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    // The content-extent cache that used to live here fed the view clamp and
    // had no other reader; 16.1 removed the clamp, so it went with it.

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
                        PushAction(new AddShapeAction(sh), _page);
                        changed = true;
                    }
                    break;
                }
                var pts = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : FinalizeStroke(_wet ?? new List<StrokePoint>());
                if (pts.Count >= 1)
                {
                    // Pen repair (#2-batch2): a tiny stroke that lands where the
                    // previous one just ended is lift-bounce from a faulty pen —
                    // discard it. Deliberate dots (i-dots, full stops) land away
                    // from a stroke end or after a pause, so they still register.
                    bool bounceDot = false;
                    if (PenRepairDots && !RulerMode && _lastCommitted != null &&
                        Environment.TickCount64 - _lastCommitMs < 160 && pts.Count <= 2)
                    {
                        float span = 0;
                        for (int i = 1; i < pts.Count; i++)
                            span += Math.Abs(pts[i].X - pts[i - 1].X) + Math.Abs(pts[i].Y - pts[i - 1].Y);
                        var first = new Vector2(pts[0].X, pts[0].Y);
                        bounceDot = span < 3f / ViewZoom &&
                                    Vector2.Distance(first, _lastCommitEnd) < 12f / ViewZoom;
                    }
                    if (!bounceDot)
                    {
                        var stroke = new PenStroke
                        {
                            Pen = Pen,
                            Color = ColorUtil.ToHex(PenColor),
                            Size = PenSize,
                            Sens = PenSensitivity,
                            Opacity = PenOpacity >= 0.999f ? (float?)null : PenOpacity,
                            Points = pts,
                            PressureCurve = EffectivePressureCurve()
                        };
                        PushAction(new AddStrokeAction(stroke), _page);
                        changed = true;
                        _lastCommitted = stroke;
                        _lastCommitMs = Environment.TickCount64;
                        _lastCommitEnd = new Vector2(pts[^1].X, pts[^1].Y);
                    }
                }
                break;
            }
            case ToolType.Eraser:
            {
                if (_eraseRemoved.Count > 0)
                {
                    if (_gestureEraserMode == EraserMode.Object)
                        PushAction(new RemoveStrokesAction(_eraseRemoved), _page, alreadyDone: true);
                    else
                    {
                        var added = _gestureFragments.Where(f => _page.Strokes.Contains(f)).ToList();
                        PushAction(new ReplaceStrokesAction(_eraseRemoved, added), _page, alreadyDone: true);
                    }
                    changed = true;
                }
                if (_eraseRemovedShapes.Count > 0)
                {
                    PushAction(new RemoveShapesAction(_eraseRemovedShapes), _page, alreadyDone: true);
                    _eraseRemovedShapes = new List<(int, ShapeElement)>();
                    changed = true;
                }
                break;
            }
            case ToolType.Mouse:
            {
                // 16.10: a press and a release with no meaningful movement
                // between them is a CLICK, and a click on a stroke selects that
                // stroke. Tested first, because the gestures below all assume a
                // drag happened. A click that finds no stroke falls straight
                // through to them, so every existing empty-canvas gesture —
                // most of all the barrel button's context menu (#44) — is
                // exactly where it was.
                if (_clickSelect && !_clickSelectMoved)
                {
                    var clicked = HitStrokeForClick(_clickSelectStartWorld);
                    if (clicked != null)
                    {
                        _lasso = null;
                        _rectSelect = false;
                        SelectSingleStroke(clicked);
                        // 17.7: this click SELECTED, so it must not also open the
                        // dropdown. The barrel button reaches here as a right-tap
                        // and the recogniser raises RightTapped behind it, which
                        // is where the second half of "both the quick actions and
                        // the dropdown" came from - the break below already keeps
                        // the release path's own menu (#44) out of it.
                        //
                        // Suppressed HERE, on the outcome, rather than at the
                        // press: a barrel tap on an ALREADY selected stroke never
                        // arms a click-select, so it still opens that selection's
                        // menu, which is #42 and is not what 17.7 is about.
                        ArmSkipNextRightTap();
                        break;
                    }
                    if (_clickSelectDeselectsEmpty)
                    {
                        // The Select tool: a click on empty canvas deselects and
                        // does no more. The press already cleared the selection;
                        // what this stops is the square lasso falling into the
                        // mouse path's click handling below and dropping a text
                        // caret, which is not something a selection tool does.
                        _lasso = null;
                        _rectSelect = false;
                        break;
                    }
                }
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
                    else if (_clickSelectDeselectsEmpty)
                    {
                        // 16.10: the Select tool's square lasso, dragged too
                        // small to enclose anything — at 16x, 8 screen px is
                        // half a world unit, so a real drag can land here.
                        // Deselect and no more, as for a click on empty canvas:
                        // a selection tool never opens a text box.
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
                    PushAction(new TableLayoutAction(tdone,
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
                        PushAction(new RotateShapeAction(_activeShape, _rotateStartShapeDeg, _activeShape.Rotation), _page, alreadyDone: true);
                        if (_activeShape.Kind == ShapeKind.Table) RebuildTextLayer();   // cells follow (#tablerot)
                        changed = true;
                    }
                    _rotatingShape = false;
                    SubjectMoved?.Invoke();   // ...and settles where it was dropped
                    break;
                }
                if ((_resizingShape || _movingShape) && _activeShape != null)
                {
                    var now = Snapshot(_activeShape);
                    if (Math.Abs(now.X - _shapeOrig.X) > 0.5 || Math.Abs(now.Y - _shapeOrig.Y) > 0.5 ||
                        Math.Abs(now.W - _shapeOrig.W) > 0.5 || Math.Abs(now.H - _shapeOrig.H) > 0.5)
                    {
                        PushAction(new MoveResizeShapeAction(_activeShape, _shapeOrig, now), _page, alreadyDone: true);
                        if (_activeShape.Kind == ShapeKind.Table)
                            ReflowTableCells(_activeShape);   // cells follow their table (#40)
                        changed = true;
                    }
                    _movingShape = _resizingShape = false;
                    // The DROP, for the same reason RecomputeSelectionBounds
                    // raises this after a multi-selection's drop: the drop can
                    // still move the subject (a table reflows its cells, a
                    // rotated resize translates to pin its anchor) and nothing
                    // else here will tell the chrome about it.
                    SubjectMoved?.Invoke();
                    break;
                }
                if (_scalingSel)
                {
                    if ((Math.Abs(_scaleFactor - 1f) > 0.01f || Math.Abs(_scaleFactorY - 1f) > 0.01f)
                        && _scaleStrokes != null && _page != null)
                    {
                        PushAction(new ScaleMixedAction(
                            _scaleStrokes, _scaleShapes!, _scaleTexts!,
                            _scaleAnchor.X, _scaleAnchor.Y, _scaleFactor, _scaleFactorY), _page,
                            alreadyDone: true);
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
                            PushAction(new CompositeAction(acts, "Move selection"), _page);
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
            // 17.11a requirement 2: the sweep commits ONE action for the whole
            // gesture, at the angle the hand described.
            //
            // The subject has been turning live since the press, so the model is
            // already at the end angle and the action that has to go on the stack
            // needs the START angle to undo to. Rather than snapshot every stroke
            // point at press - the undo stack would grow like the page - the
            // sweep is UN-APPLIED here, the action is built against the restored
            // state (which is where it captures the shapes' and boxes' exact
            // before-values), and Push re-applies it. The user sees nothing: no
            // frame is drawn between the two.
            //
            // A TAP NO LONGER TURNS ANYTHING. It used to turn one quarter, which
            // made sense while the sweep was quartered too; inside a free
            // rotation a click that jumps 90 degrees is a surprise, and 17.11a is
            // explicit that "0 should not be sticky unless asked for". The
            // quarter turn is still one press away - it is what the mode bar's
            // Rotate button and 16.2's bottom row commit.
            //
            // 17.11a: only the SELECTION half reports a content change. A handle
            // or pivot drag moved a number the page does not contain, so marking
            // the document dirty for it would queue a save of nothing and put an
            // untouched page in the "edited" state.
            case ToolType.Rotate:
            {
                if (_rotateGrab == RotateGrab.None && _rotating &&
                    Math.Abs(_rotateTurnedDeg) > 1e-6 && _page != null)
                {
                    double total = _rotateTurnedDeg;
                    SpinRotateSubject(-total);
                    PushAction(new RotateFreeMixedAction(_rotateInk, _rotateShapes, _rotateTexts,
                                                         _rotateCentre.X, _rotateCentre.Y, total), _page);
                    AfterSelectionTransform(_rotateTexts.Count > 0);
                    changed = true;
                }
                _rotating = false;
                _rotateTurnedDeg = 0;
                _rotateInk = new();
                _rotateShapes = new();
                _rotateTexts = new();
                _rotateGrab = RotateGrab.None;
                break;
            }
            case ToolType.FreeSpace:
            {
                if (Math.Abs(_spaceDelta) > 2)
                {
                    PushAction(new InsertSpaceAction(_spaceY, _spaceDelta), _page);
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
        // An ABANDONED sweep - capture lost, tool switched mid-drag, a second
        // pointer arriving - has already moved the drawing but will never reach
        // the commit in PointerReleased. Put it back rather than leaving the page
        // turned by a gesture that has no undo entry. On the normal path the
        // commit has already zeroed this, so this is a no-op there.
        if (_rotating && Math.Abs(_rotateTurnedDeg) > 1e-6)
        {
            SpinRotateSubject(-_rotateTurnedDeg);
            RecomputeSelectionBoundsFrozen();
        }
        _rotating = false;
        _rotateTurnedDeg = 0;
        _rotateInk = new();
        _rotateShapes = new();
        _rotateTexts = new();
        _rotateGrab = RotateGrab.None;
        _shapeAdjust = false;
        _adjustShape = null;
        _movingShape = _resizingShape = false;
        _rotatingShape = false;
        _rectSelect = false;
        _barrelGesture = false;
        _barrelMoved = false;
        _clickSelect = false;
        _clickSelectMoved = false;
        _clickSelectDeselectsEmpty = false;
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

    /// <summary>The tilt visualiser's box, in WORLD units. One definition, two
    /// consumers - <see cref="DrawRuler"/> paints it and the press handler hit
    /// tests it - because a visualiser you can click is only as good as the
    /// agreement between where it is drawn and where the click is caught.</summary>
    private Rect RulerBubble()
    {
        var c = ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));
        float bw = 70f / ViewZoom, bh = 34f / ViewZoom;
        return new Rect(c.X - bw / 2, c.Y - bh / 2, bw, bh);
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
        var edgeColor = Color.FromArgb(210, Accent.R, Accent.G, Accent.B);
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
        var br = RulerBubble();
        ds.FillRoundedRectangle(br, 9f / ViewZoom, 9f / ViewZoom, Color.FromArgb(235, 28, 28, 32));
        ds.DrawRoundedRectangle(br, 9f / ViewZoom, 9f / ViewZoom, edgeColor, 1.5f / ViewZoom);
        // reuse one format across frames (DrawRuler runs every frame while the
        // ruler is on; allocating a CanvasTextFormat per frame was the one spot
        // that missed the cached-format pattern) — only the zoom-dependent size
        // is updated per call.
        _rulerFormat.FontSize = 16f / ViewZoom;
        ds.DrawText(deg, br, Colors.White, _rulerFormat);
    }

    private readonly CanvasTextFormat _rulerFormat = new()
    {
        HorizontalAlignment = CanvasHorizontalAlignment.Center,
        VerticalAlignment = CanvasVerticalAlignment.Center
    };

    /// <summary>17.11a's rotate interface, transcribed from the user's capture:
    /// a red line through the pivot at the current angle, a crosshair whose
    /// centre is EMPTY, a glowing arc crossing the line, and a donut handle where
    /// the two meet. Colour <c>#BF3D38</c>, given directly by the user.
    ///
    /// <para><b>Answering 17.11a's third question: the line is the ANGLE'S
    /// INDICATOR, not a fixed axis.</b> It is the page's own horizontal drawn
    /// through the pivot, so at 0 degrees it is horizontal - which is exactly
    /// what the capture shows and what 17.11a's own wording says ("at the
    /// current rotation angle - horizontal at 0"). It runs the full width
    /// because it is a HORIZON: it says which way the page is lying, and the
    /// screen's own edges are the reference it is read against. That is also why
    /// the whole assembly turns rigidly - line, ticks, arc and handle - rather
    /// than a fixed axis with an arm swinging off it. There is one angle here
    /// and every mark is drawn from it.</para>
    ///
    /// <para><b>Every size is in SCREEN PIXELS over ViewZoom.</b> The assembly is
    /// chrome, so it keeps its physical size at 0.1x and at 16x; see the
    /// constants block beside <see cref="PageRotationDeg"/>.</para>
    ///
    /// <para><b>The session's transform is COMPOSED, never assigned.</b>
    /// <see cref="DrawRegion"/> has already put <paramref name="ds"/> into world
    /// space on top of the CanvasVirtualControl's per-tile pre-translation, and
    /// this method only reads that. Overwriting it is what painted every
    /// non-origin tile displaced and clipped (#inkfix2).</para></summary>
    private void DrawRotateInterface(CanvasDrawingSession ds, ICanvasResourceCreator rc)
    {
        float k = 1f / ViewZoom;                       // screen pixels -> world units
        var pivot = RotatePivotWorld;
        double rad = PageRotationDeg * Math.PI / 180.0;
        var dir = new Vector2((float)Math.Cos(rad), (float)Math.Sin(rad));
        var perp = new Vector2(-dir.Y, dir.X);
        float r = RotateRadiusPx * k;                  // pivot to arc IS the drag radius
        var handle = pivot + dir * r;

        // ---- the line: full width, and BROKEN AT THE PIVOT ------------------
        // 17.11a is specific that the crosshair's centre is empty - "four ticks
        // around a gap, not a plus sign". A line drawn straight through the pivot
        // with the crosshair laid on top would fill that gap, so the gap is cut
        // out of the LINE and the ticks are drawn around it. Half-length is
        // width+height rather than the diagonal: cheaper, always longer, and it
        // only has to leave the viewport at every angle.
        float reach = (float)((ActualWidth + ActualHeight) * k);
        float gap = RotateCrossGapPx * k;
        ds.DrawLine(pivot + dir * gap, pivot + dir * reach, RotateInk, RotateLineWidthPx * k);
        ds.DrawLine(pivot - dir * gap, pivot - dir * reach, RotateInk, RotateLineWidthPx * k);

        // ---- the crosshair: four short strokes pointing outward -------------
        // Heavier than the line and starting where the line stops, so the two
        // along the line read as the innermost SEGMENT of it rather than as more
        // line - which is what makes the mark read as segmented.
        float tip = (RotateCrossGapPx + RotateCrossTickPx) * k;
        float tw = RotateCrossWidthPx * k;
        void Tick(Vector2 d) => ds.DrawLine(pivot + d * gap, pivot + d * tip, RotateInk, tw);
        Tick(dir);
        Tick(-dir);
        Tick(perp);
        Tick(-perp);

        // ---- the arc: the rotation path, convex away from the pivot ---------
        // An arc of the circle centred ON the pivot at the drag radius, centred
        // ON the current angle - so it crosses the line exactly at the handle and
        // its bulge faces outward, both of which fall out of the geometry rather
        // than being arranged for.
        using var pb = new CanvasPathBuilder(rc);
        double half = RotateArcHalfSpanDeg * Math.PI / 180.0;
        pb.BeginFigure(pivot + new Vector2((float)Math.Cos(rad - half), (float)Math.Sin(rad - half)) * r);
        pb.AddArc(pivot, r, r, (float)(rad - half), (float)(2 * half));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var arc = CanvasGeometry.CreatePath(pb);

        // ---- the glow: a SOFT halo, and why it is built here -----------------
        // 17.11a says "a soft halo outside the stroke, not a hard edge", and
        // neither existing piece of glow machinery can supply one HERE. The glow
        // engine of 27e999b is XAML: it animates the stops of the chrome rims'
        // LinearGradientBrush and has no way to reach a Win2D session. This
        // canvas's own DrawStrokeGlow is a single wider translucent pass, which
        // is precisely the hard edge being ruled out. What this does reuse is the
        // effect composition DrawRegionBlurred already proves works inside a
        // CanvasVirtualControl tile - render into an intermediate, blur, then
        // composite - with a CanvasCommandList as the intermediate, since there
        // is nothing on screen to capture and the marks are drawn from scratch.
        //
        // TWO RADII, not one: a tight bright halo hugging the stroke and a wide
        // faint one beyond it. One pass gives a second blurry stroke; two give a
        // falloff, which is what reads as a glow.
        DrawRotateGlow(ds, rc, arc, handle, k, RotateGlowWidePx, RotateGlowWideAlpha);
        DrawRotateGlow(ds, rc, arc, handle, k, RotateGlowTightPx, RotateGlowTightAlpha);

        ds.DrawGeometry(arc, RotateInk, RotateArcWidthPx * k, _roundStyle);

        // ---- the handle: a DONUT, not a dot ---------------------------------
        // The fill stops at the ring's INNER edge (radius minus half the stroke
        // width) so the dark centre meets the red rather than creeping under it
        // and thinning the ring from the inside.
        ds.FillCircle(handle, (RotateHandleRingPx - RotateHandleRingWidthPx / 2) * k, RotateHandleCore);
        ds.DrawCircle(handle, RotateHandleRingPx * k, RotateInk, RotateHandleRingWidthPx * k);
    }

    /// <summary>One blurred pass of the arc and the handle ring. Kept separate so
    /// the two radii differ in nothing but their two arguments - a copied second
    /// pass is how the wide halo ends up a different shape from the tight one
    /// after the next edit.</summary>
    private void DrawRotateGlow(CanvasDrawingSession ds, ICanvasResourceCreator rc,
                                CanvasGeometry arc, Vector2 handle, float k,
                                float radiusPx, byte alpha)
    {
        var ink = Color.FromArgb(alpha, RotateInk.R, RotateInk.G, RotateInk.B);
        using var list = new CanvasCommandList(rc);
        using (var lds = list.CreateDrawingSession())
        {
            lds.DrawGeometry(arc, ink, RotateArcWidthPx * k, _roundStyle);
            lds.DrawCircle(handle, RotateHandleRingPx * k, ink, RotateHandleRingWidthPx * k);
        }
        using var blur = new GaussianBlurEffect
        {
            Source = list,
            // In the command list's own units, which are world units, so the
            // halo is radiusPx SCREEN pixels once ds.Transform has scaled it.
            BlurAmount = radiusPx * k,
            // SOFT, where DrawRegionBlurred uses Hard. That one is suppressing a
            // transparent fringe at a tile edge; here the spread past the
            // source's own extent IS the halo.
            BorderMode = EffectBorderMode.Soft,
            Optimization = EffectOptimization.Quality,
        };
        ds.DrawImage(blur);
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
    // Spatial index
    // =======================================================================
    // A uniform grid over world space. Every element registers in each cell its
    // padded bounds touch, so a query can only over-report, never miss: a hit a
    // linear scan would find is always in the candidate set. Callers use the set
    // purely to skip the per-point distance maths and still walk the page's own
    // lists in order, so the list indices recorded for undo stay exact.
    //
    // Element bounds carry a Size + 8 pad (the widest thickness tolerance any
    // caller adds), so query rects only need the caller's own radius.
    private const float GridCell = 256f;
    // Under this element count a linear scan beats maintaining the grid.
    private const int GridMinElements = 128;

    private sealed class SpatialGrid
    {
        // An element (or query) spanning more cells than this goes to / returns
        // the loose list instead: bounded work, still exact.
        private const int MaxCells = 1024;

        private readonly Dictionary<long, List<PenStroke>> _sCells = new();
        private readonly Dictionary<long, List<ShapeElement>> _hCells = new();
        private readonly List<PenStroke> _sLoose = new();
        private readonly List<ShapeElement> _hLoose = new();
        // Bounds each element was registered with, so removal always hits the
        // same cells even if the element's geometry has since been edited.
        private readonly Dictionary<PenStroke, (float x0, float y0, float x1, float y1)> _sReg = new();
        private readonly Dictionary<ShapeElement, (float x0, float y0, float x1, float y1)> _hReg = new();

        public void Clear()
        {
            _sCells.Clear(); _hCells.Clear();
            _sLoose.Clear(); _hLoose.Clear();
            _sReg.Clear(); _hReg.Clear();
        }

        private static long Key(int cx, int cy) => ((long)cx << 32) | (uint)cy;

        private static bool CellRange(float x0, float y0, float x1, float y1,
                                      out int cx0, out int cy0, out int cx1, out int cy1)
        {
            cx0 = cy0 = cx1 = cy1 = 0;
            if (!float.IsFinite(x0) || !float.IsFinite(y0) || !float.IsFinite(x1) || !float.IsFinite(y1))
                return false;
            const float Limit = 1e7f;
            if (x0 < -Limit || y0 < -Limit || x1 > Limit || y1 > Limit || x1 < x0 || y1 < y0) return false;
            cx0 = (int)Math.Floor(x0 / GridCell); cy0 = (int)Math.Floor(y0 / GridCell);
            cx1 = (int)Math.Floor(x1 / GridCell); cy1 = (int)Math.Floor(y1 / GridCell);
            return ((long)cx1 - cx0 + 1) * ((long)cy1 - cy0 + 1) <= MaxCells;
        }

        public void Add(PenStroke s, float x0, float y0, float x1, float y1)
        {
            if (_sReg.ContainsKey(s)) Remove(s);
            _sReg[s] = (x0, y0, x1, y1);
            if (!CellRange(x0, y0, x1, y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                _sLoose.Add(s);
                return;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long k = Key(cx, cy);
                    if (!_sCells.TryGetValue(k, out var list)) _sCells[k] = list = new List<PenStroke>();
                    list.Add(s);
                }
        }

        public void Add(ShapeElement s, float x0, float y0, float x1, float y1)
        {
            if (_hReg.ContainsKey(s)) Remove(s);
            _hReg[s] = (x0, y0, x1, y1);
            if (!CellRange(x0, y0, x1, y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                _hLoose.Add(s);
                return;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long k = Key(cx, cy);
                    if (!_hCells.TryGetValue(k, out var list)) _hCells[k] = list = new List<ShapeElement>();
                    list.Add(s);
                }
        }

        public void Remove(PenStroke s)
        {
            if (!_sReg.TryGetValue(s, out var b)) return;
            _sReg.Remove(s);
            if (!CellRange(b.x0, b.y0, b.x1, b.y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                _sLoose.Remove(s);
                return;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long k = Key(cx, cy);
                    if (!_sCells.TryGetValue(k, out var list)) continue;
                    list.Remove(s);
                    if (list.Count == 0) _sCells.Remove(k);
                }
        }

        public void Remove(ShapeElement s)
        {
            if (!_hReg.TryGetValue(s, out var b)) return;
            _hReg.Remove(s);
            if (!CellRange(b.x0, b.y0, b.x1, b.y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                _hLoose.Remove(s);
                return;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                {
                    long k = Key(cx, cy);
                    if (!_hCells.TryGetValue(k, out var list)) continue;
                    list.Remove(s);
                    if (list.Count == 0) _hCells.Remove(k);
                }
        }

        public HashSet<PenStroke> QueryStrokes(float x0, float y0, float x1, float y1)
        {
            var hit = new HashSet<PenStroke>(_sLoose);
            if (!CellRange(x0, y0, x1, y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                foreach (var s in _sReg.Keys) hit.Add(s);   // unbounded query: everything
                return hit;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                    if (_sCells.TryGetValue(Key(cx, cy), out var list))
                        foreach (var s in list) hit.Add(s);
            return hit;
        }

        public HashSet<ShapeElement> QueryShapes(float x0, float y0, float x1, float y1)
        {
            var hit = new HashSet<ShapeElement>(_hLoose);
            if (!CellRange(x0, y0, x1, y1, out int cx0, out int cy0, out int cx1, out int cy1))
            {
                foreach (var s in _hReg.Keys) hit.Add(s);
                return hit;
            }
            for (int cx = cx0; cx <= cx1; cx++)
                for (int cy = cy0; cy <= cy1; cy++)
                    if (_hCells.TryGetValue(Key(cx, cy), out var list))
                        foreach (var s in list) hit.Add(s);
            return hit;
        }
    }

    private readonly SpatialGrid _grid = new();
    private NotePage? _gridPage;
    private bool _gridDirty = true;
    private int _gridStrokeCount, _gridShapeCount;

    /// <summary>Marks the spatial index stale; the rebuild is lazy, on the next
    /// query. Needed after an edit that moves elements without changing the
    /// collection counts (the count check below catches adds and removes).</summary>
    public void InvalidateSpatialIndex() => _gridDirty = true;

    private static void StrokeIndexBounds(PenStroke s, out float x0, out float y0, out float x1, out float y1)
    {
        s.GetBounds(out x0, out y0, out x1, out y1);
        float pad = s.Size + 8f;
        x0 -= pad; y0 -= pad; x1 += pad; y1 += pad;
    }

    private static void ShapeIndexBounds(ShapeElement s, out float x0, out float y0, out float x1, out float y1)
    {
        var r = ShapeBounds(s);
        double hw = r.Width / 2, hh = r.Height / 2;
        double cx = r.X + hw, cy = r.Y + hh;
        // rotation is about the centre, so the bounding circle covers every angle
        if (Math.Abs(s.Rotation) > 0.01) { double d = Math.Sqrt(hw * hw + hh * hh); hw = hh = d; }
        double pad = s.Size + 8;
        x0 = (float)(cx - hw - pad); y0 = (float)(cy - hh - pad);
        x1 = (float)(cx + hw + pad); y1 = (float)(cy + hh + pad);
    }

    private void GridAdd(PenStroke s)
    {
        StrokeIndexBounds(s, out float x0, out float y0, out float x1, out float y1);
        _grid.Add(s, x0, y0, x1, y1);
    }

    private void GridAdd(ShapeElement s)
    {
        ShapeIndexBounds(s, out float x0, out float y0, out float x1, out float y1);
        _grid.Add(s, x0, y0, x1, y1);
    }

    private SpatialGrid? EnsureGrid()
    {
        if (_page == null) return null;
        if (_page.Strokes.Count + _page.Shapes.Count < GridMinElements)
        {
            _gridDirty = true;   // stay cold; build on the first query past the threshold
            return null;
        }
        // The count comparison is the safety net for mutations made outside this
        // control (undo action bodies, sync-log replay): any add or remove shows
        // up as a mismatch and forces a rebuild before the query is answered.
        if (_gridDirty || !ReferenceEquals(_gridPage, _page) ||
            _gridStrokeCount != _page.Strokes.Count || _gridShapeCount != _page.Shapes.Count)
        {
            _grid.Clear();
            foreach (var s in _page.Strokes) GridAdd(s);
            foreach (var sh in _page.Shapes) GridAdd(sh);
            _gridPage = _page;
            _gridStrokeCount = _page.Strokes.Count;
            _gridShapeCount = _page.Shapes.Count;
            _gridDirty = false;
        }
        return _grid;
    }

    /// <summary>Strokes whose padded bounds meet the world rect, or null when the
    /// index is cold — null means "no filter, scan the whole list".</summary>
    private HashSet<PenStroke>? StrokeCandidates(float x0, float y0, float x1, float y1)
        => EnsureGrid()?.QueryStrokes(x0, y0, x1, y1);

    private HashSet<ShapeElement>? ShapeCandidates(float x0, float y0, float x1, float y1)
        => EnsureGrid()?.QueryShapes(x0, y0, x1, y1);

    // ---- centralised element mutation ----
    // Every direct add/remove of the page's Strokes/Shapes goes through these so
    // the grid tracks the page exactly. The tracked counts are stepped, never
    // reassigned, so a mutation made elsewhere still surfaces as a mismatch.
    // Adds performed by undo actions arrive via PushAction instead.

    private void InsertStroke(int index, PenStroke s)
    {
        if (_page == null) return;
        _page.Strokes.Insert(Math.Clamp(index, 0, _page.Strokes.Count), s);
        GridAdd(s);
        _gridStrokeCount++;
    }

    private void RemoveStrokeAt(int index)
    {
        if (_page == null) return;
        var s = _page.Strokes[index];
        _page.Strokes.RemoveAt(index);
        _grid.Remove(s);
        _gridStrokeCount--;
    }

    private void RemoveStroke(PenStroke s)
    {
        if (_page == null || !_page.Strokes.Remove(s)) return;
        _grid.Remove(s);
        _gridStrokeCount--;
    }

    private void RemoveShapeAt(int index)
    {
        if (_page == null) return;
        var s = _page.Shapes[index];
        _page.Shapes.RemoveAt(index);
        _grid.Remove(s);
        _gridShapeCount--;
    }

    /// <summary>Wraps UndoManager.Push: action Do()/Undo() bodies mutate the page
    /// directly and many move elements without changing counts, so every undoable
    /// edit invalidates the index.</summary>
    private void PushAction(IPageAction action, NotePage page, bool alreadyDone = false)
    {
        UndoManager.Push(action, page, alreadyDone);
        _gridDirty = true;
    }

    // =======================================================================
    // Erasing
    // =======================================================================
    private void EraseAt(Vector2 from, Vector2 to)
    {
        if (_page == null) return;
        // One candidate query for the whole gesture step. The pad is the widest
        // query-side tolerance either mode uses; per-element thickness is already
        // baked into the indexed bounds.
        float qp = Math.Max(EraserRadius, 8f);
        float qx0 = Math.Min(from.X, to.X) - qp, qy0 = Math.Min(from.Y, to.Y) - qp;
        float qx1 = Math.Max(from.X, to.X) + qp, qy1 = Math.Max(from.Y, to.Y) + qp;
        var shCand = ShapeCandidates(qx0, qy0, qx1, qy1);
        var stCand = StrokeCandidates(qx0, qy0, qx1, qy1);
        // shapes are erased whole in either mode — but images are never erased
        // (move/delete them with the selection tools instead)
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var sh = _page.Shapes[i];
            if (shCand != null && !shCand.Contains(sh)) continue;
            if (sh.Kind == ShapeKind.Image) continue;
            float tolS = sh.Size + 8f;
            if (DistToShapeOutline(sh, from) <= tolS || DistToShapeOutline(sh, to) <= tolS)
            {
                _eraseRemovedShapes.Add((i, sh));
                RemoveShapeAt(i);
                if (_activeShape == sh) _activeShape = null;
            }
        }
        if (_gestureEraserMode == EraserMode.Object)
        {
            for (int i = _page.Strokes.Count - 1; i >= 0; i--)
            {
                var s = _page.Strokes[i];
                if (stCand != null && !stCand.Contains(s)) continue;
                float tol = s.Size + 6f;
                bool hit = s.Points.Any(p =>
                    GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) <= tol);
                if (hit)
                {
                    _eraseRemoved.Add((i, s));
                    RemoveStrokeAt(i);
                }
            }
        }
        else
        {
            float r = EraserRadius;
            // Walk the live list backwards so the index is the stroke's real
            // position (no IndexOf) and freshly inserted fragments — placed at
            // i and above — are never re-examined this pass. Every mutation still
            // routes through RemoveStrokeAt/InsertStroke so the spatial index and
            // the stroke-count net stay correct (CONSTRAINT 5).
            for (int i = _page.Strokes.Count - 1; i >= 0; i--)
            {
                var s = _page.Strokes[i];
                if (stCand != null && !stCand.Contains(s)) continue;

                bool any = false;
                foreach (var p in s.Points)
                    if (GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to) <= r) { any = true; break; }
                if (!any) continue;

                // The style shapes the surviving geometry; null = leave this stroke
                // untouched this step (e.g. Slice with no true crossing).
                List<List<StrokePoint>>? runs = _gestureEraserStyle switch
                {
                    EraserStyle.Slice => SliceRuns(s, from, to),
                    EraserStyle.Nudge => NudgeRuns(s, from, to, r),
                    EraserStyle.SoftMask => SoftMaskRuns(s, from, to, r),
                    _ => HardMaskRuns(s, from, to, r),
                };
                if (runs == null) continue;

                RemoveStrokeAt(i);
                // A fragment from earlier in this gesture: its original was
                // already recorded, so only record genuinely original strokes.
                if (!_gestureFragments.Remove(s)) _eraseRemoved.Add((i, s));

                int insertAt = i;
                foreach (var run in runs)
                {
                    if (run.Count < 1) continue;
                    var frag = s.CloneWithPoints(run);
                    InsertStroke(insertAt++, frag);
                    _gestureFragments.Add(frag);
                }
            }
        }
    }

    // ---- eraser styles (§7.c) ---------------------------------------------
    // Each returns the surviving point-runs for one stroke, or null to leave the
    // stroke untouched. Styles that alter point data build fresh StrokePoint
    // objects so the recorded original stays intact for undo; HardMask and Slice
    // remove no data and may reuse the point references.

    // Hard mask: crisp removal of every point within the eraser radius, keeping
    // the surviving contiguous runs (today's point eraser).
    private static List<List<StrokePoint>> HardMaskRuns(PenStroke s, Vector2 from, Vector2 to, float r)
    {
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
        return runs;
    }

    // Soft mask: hard-remove the core within r, but thin the surviving ink in the
    // falloff band just outside it by lowering pressure toward the cut, so the
    // pressure→width renderer tapers the end to a soft point rather than a blunt
    // cap. Constant-width pens (monoline/highlighter) degrade to a hard edge since
    // they ignore pressure by design.
    private static List<List<StrokePoint>> SoftMaskRuns(PenStroke s, Vector2 from, Vector2 to, float r)
    {
        float outer = r * 1.7f;
        var runs = new List<List<StrokePoint>>();
        var cur = new List<StrokePoint>();
        foreach (var p in s.Points)
        {
            float d = GeometryUtil.DistToSegment(new Vector2(p.X, p.Y), from, to);
            if (d <= r)
            {
                if (cur.Count > 1) runs.Add(cur);
                cur = new List<StrokePoint>();
                continue;
            }
            if (d < outer)
            {
                float f = (d - r) / (outer - r);      // 0 at the cut, 1 at the band edge
                float taper = 0.12f + 0.88f * f * f;   // ease-in: thins sharply near the cut
                cur.Add(new StrokePoint(p.X, p.Y, p.Pressure * taper));
            }
            else cur.Add(p);
        }
        if (cur.Count > 1) runs.Add(cur);
        return runs;
    }

    // Slice: cut the stroke into two strokes where the eraser path crosses it,
    // removing no area. Returns null when the eraser only grazes without a true
    // crossing, so dragging along a stroke never shatters it.
    private static List<List<StrokePoint>>? SliceRuns(PenStroke s, Vector2 from, Vector2 to)
    {
        var pts = s.Points;
        if (pts.Count < 4) return null;   // too short to split into two drawable halves
        for (int i = 1; i < pts.Count; i++)
        {
            var a = new Vector2(pts[i - 1].X, pts[i - 1].Y);
            var b = new Vector2(pts[i].X, pts[i].Y);
            if (!SegmentsCross(a, b, from, to)) continue;
            var left = pts.GetRange(0, i);               // points [0 .. i-1]
            var right = pts.GetRange(i, pts.Count - i);  // points [i .. end]
            if (left.Count < 2 || right.Count < 2) continue;
            return new List<List<StrokePoint>> { left, right };
        }
        return null;
    }

    // Nudge: push points out of the eraser's way instead of deleting them. Each
    // point inside r is displaced onto the eraser rim, away from the nearest point
    // on the eraser path. One run, no points lost.
    private static List<List<StrokePoint>> NudgeRuns(PenStroke s, Vector2 from, Vector2 to, float r)
    {
        var run = new List<StrokePoint>(s.Points.Count);
        foreach (var p in s.Points)
        {
            var pv = new Vector2(p.X, p.Y);
            var c = ClosestOnSegment(pv, from, to);
            var away = pv - c;
            float d = away.Length();
            if (d >= r) { run.Add(p); continue; }
            // A point sitting exactly on the path has no outward direction; push
            // along the path normal so it still clears the cursor.
            Vector2 dir = d > 1e-3f ? away / d : PerpUnit(to - from);
            var np = c + dir * r;
            run.Add(new StrokePoint(np.X, np.Y, p.Pressure));
        }
        return new List<List<StrokePoint>> { run };
    }

    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        if (len2 < 1e-6f) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f);
        return a + ab * t;
    }

    private static Vector2 PerpUnit(Vector2 v)
    {
        var n = new Vector2(-v.Y, v.X);
        float len = n.Length();
        return len > 1e-3f ? n / len : new Vector2(0, 1);
    }

    // Proper segment-segment crossing test (AB genuinely straddles CD and back).
    private static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        static float Cross(Vector2 o, Vector2 e, Vector2 p) =>
            (e.X - o.X) * (p.Y - o.Y) - (e.Y - o.Y) * (p.X - o.X);
        float d1 = Cross(c, d, a);
        float d2 = Cross(c, d, b);
        float d3 = Cross(a, b, c);
        float d4 = Cross(a, b, d);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    // =======================================================================
    // Lasso selection
    // =======================================================================
    /// <summary>17.10's second and third controls, as one predicate: may this
    /// element be caught by a selection right now?
    ///
    /// <para><b>The layer half goes through <see cref="PageLayers.CanSelect"/>
    /// and nowhere else.</b> 18.1 asks for exactly that - "if something needs to
    /// know about layers, it asks PageLayers" - and that predicate is already
    /// scope AND not hidden AND not locked, so there is nothing to reimplement
    /// here. On a page with one implicit layer it answers true for everything,
    /// which is why the All/Active control is real rather than aspirational the
    /// moment it is drawn.</para>
    ///
    /// <para>The lock half is the ELEMENT's own <c>Locked</c> flag, which is a
    /// different thing from a locked layer and is the one the padlock control
    /// switches: open padlock = INCLUDE, closed = IGNORE.</para></summary>
    private bool CanCatch(int layerKey, bool elementLocked)
    {
        if (_page == null) return true;
        if (IgnoreLocked && elementLocked) return false;
        return PageLayers.CanSelect(_page, layerKey, SelectScope);
    }

    private void SelectWithLasso(List<Vector2> poly)
    {
        if (_page == null) return;
        _selected.Clear();
        _selectedSet.Clear();
        _selShapes.Clear();
        _selShapeSet.Clear();
        _selTexts.Clear();
        // Anything the lasso can select must overlap the lasso's bounding box.
        float lx0 = float.MaxValue, ly0 = float.MaxValue, lx1 = float.MinValue, ly1 = float.MinValue;
        foreach (var v in poly)
        {
            if (v.X < lx0) lx0 = v.X;
            if (v.Y < ly0) ly0 = v.Y;
            if (v.X > lx1) lx1 = v.X;
            if (v.Y > ly1) ly1 = v.Y;
        }
        var lsCand = poly.Count > 0 ? StrokeCandidates(lx0, ly0, lx1, ly1) : null;
        var lhCand = poly.Count > 0 ? ShapeCandidates(lx0, ly0, lx1, ly1) : null;
        foreach (var s in _page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            if (lsCand != null && !lsCand.Contains(s)) continue;
            if (!CanCatch(s.LayerKey, s.Locked)) continue;
            int inside = s.Points.Count(p => GeometryUtil.PointInPolygon(new Vector2(p.X, p.Y), poly));
            // Partial: any part of the stroke inside the lasso catches it.
            // Complete: the whole stroke has to be inside (UI-SPEC-V2 1.3).
            if (SelectPartial ? inside > 0 : inside == s.Points.Count)
            {
                _selected.Add(s);
                _selectedSet.Add(s);
            }
        }
        foreach (var sh in _page.Shapes)
        {
            if (lhCand != null && !lhCand.Contains(sh)) continue;
            if (!CanCatch(sh.LayerKey, sh.Locked)) continue;
            var r = ShapeBounds(sh);
            var c = new Vector2((float)(r.X + r.Width / 2), (float)(r.Y + r.Height / 2));
            if (GeometryUtil.PointInPolygon(c, poly)) { _selShapes.Add(sh); _selShapeSet.Add(sh); }
        }
        foreach (var t in _page.Texts)
        {
            if (!CanCatch(t.LayerKey, t.Locked)) continue;
            // The CENTRE, which a rotation about that same centre does not move -
            // so this test is already rotation-correct and stays that way.
            if (GeometryUtil.PointInPolygon(TextCentreWorld(t), poly)) _selTexts.Add(t);
        }
        _activeShape = null; // a multi-selection supersedes the single active shape
        RecomputeSelectionBounds();
    }

    private void RecomputeSelectionBounds()
    {
        RecomputeSelectionBoundsCore();
        // Every path that changes the multi-selection ends here, so this is the
        // one place the dial, the pen row and the selection chrome have to be
        // told (16.3 / 16.9). SelectionState drops a publish that says the same
        // thing as the last one, so calling it from a hot path is free.
        PublishSelection();
        // ...and that dropping is exactly why the chrome needs telling
        // separately. The recompute after a DROP publishes the same kind, the
        // same count and the same flags as before the drag, so Changed never
        // fires and nothing would re-place the marks at their new home.
        SubjectMoved?.Invoke();
    }

    private void RecomputeSelectionBoundsCore()
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
            // 17.11a: a text box TURNS, so its four corners are what bound it and
            // not its stored X/Y/W/H. Taking the unrotated box here drew a
            // marquee that cut the corners off a tilted box and left a gap on its
            // flat sides - and, worse, gave the rotate sweep a centre that was
            // not the centre of what the user could see.
            foreach (var c in TextCornersWorld(t)) Inc(c.X, c.Y);
        }
        if (minX == double.MaxValue) { _selBounds = Rect.Empty; return; }
        _selBounds = new Rect(minX - 8, minY - 8, (maxX - minX) + 16, (maxY - minY) + 16);
    }

    /// <summary>A text box's RENDERED size in world units.
    ///
    /// <para>The height is not in the model - it comes from the wrapped content,
    /// so only the live XAML container knows it. The fallback is the model's own
    /// width and the 40 every other site in this codebase assumes
    /// (<c>MirrorMixedAction</c>, <c>RotateQuarterMixedAction</c>,
    /// <c>ActionBounds.Of</c>), which matters before the first layout pass and
    /// for a page being measured without a text layer at all.</para></summary>
    private (double W, double H) TextBoxSizeWorld(TextElement t)
    {
        double w = Math.Max(60, t.Width), h = 40;
        if (_textUi.TryGetValue(t.Id, out var ui))
        {
            if (ui.Container.ActualWidth > 0) w = ui.Container.ActualWidth;
            if (ui.Container.ActualHeight > 0) h = ui.Container.ActualHeight;
        }
        return (w, h);
    }

    /// <summary>The centre a text box turns about: the container's own centre,
    /// which is what <c>RenderTransformOrigin 0.5,0.5</c> means on the editing
    /// overlay and what the Win2D path mirrors. Rotation does not move it, which
    /// is why the lasso's centre test needs no rotation of its own.</summary>
    private Vector2 TextCentreWorld(TextElement t)
    {
        var (w, h) = TextBoxSizeWorld(t);
        return new Vector2((float)(t.X + w / 2), (float)(t.Y + h / 2));
    }

    /// <summary>The four corners of a text box as they are actually drawn,
    /// turned by its own <see cref="TextElement.Rotation"/>.</summary>
    private Vector2[] TextCornersWorld(TextElement t)
    {
        var (w, h) = TextBoxSizeWorld(t);
        var c = new Vector2((float)(t.X + w / 2), (float)(t.Y + h / 2));
        var box = new[]
        {
            new Vector2((float)t.X, (float)t.Y),
            new Vector2((float)(t.X + w), (float)t.Y),
            new Vector2((float)(t.X + w), (float)(t.Y + h)),
            new Vector2((float)t.X, (float)(t.Y + h)),
        };
        if (Math.Abs(t.Rotation) < 0.01) return box;
        for (int i = 0; i < box.Length; i++) box[i] = RotatePoint(box[i], c, t.Rotation);
        return box;
    }

    // =======================================================================
    // THE SELECTION, PUBLISHED (CONCEPTS-REF 16.3 / 16.9)
    //
    // One description of what is selected and what it SUPPORTS, so the dial, the
    // pen row and the selection chrome all answer the same question the same
    // way. 16.3 is easy to misread as "something is selected, so grey the dial",
    // and 16.9 corrects that: a selected stroke leaves the dial live and reading
    // that stroke's own values. Nothing below ever asks "is anything selected?"
    // - it asks what the selection HAS.
    // =======================================================================

    /// <summary>Reduce-motion, supplied by the host exactly as the dial and the
    /// fullscreen strip take it. Null reads as "animate".</summary>
    public Func<bool>? ReduceMotion { get; set; }

    /// <summary>World bounds of whatever the selection presentation should frame:
    /// the multi-selection if there is one, otherwise the active shape. Empty
    /// when nothing is selected.
    ///
    /// <para><b>A drag in flight is included.</b> A multi-selection being moved
    /// writes nothing until the drop - its strokes, shapes and texts are
    /// TRANSLATED at draw time by <c>_moveDx</c>/<c>_moveDy</c> while
    /// <c>_selBounds</c> stays where the drag began - so the offset has to be
    /// added here or the presentation frames where the selection used to be.
    /// This predates 17.8, but 17.8 is what exposes it: the tinted rectangle it
    /// removed was the only mark that followed a drag, and the corner circles
    /// and the guides were already standing still behind it. The stale GUIDE is
    /// the worse half - alignment is the only thing a guide is for, and one left
    /// on the old bounds points at nothing that is there any more.</para>
    ///
    /// <para><b>The active-shape branch takes no offset, and must not.</b> A
    /// single shape's drag writes straight through to its own X and Y as the
    /// pointer moves (<c>_activeShape.X = _shapeOrig.X + ...</c>), so
    /// <see cref="ShapeBounds"/> already reports where it is now; adding
    /// <c>_moveDx</c> there would count the same movement twice. A live SCALE
    /// needs nothing either - <c>ApplyScaleLive</c> rewrites <c>_selBounds</c>
    /// itself on every step.</para></summary>
    public Rect SubjectBoundsWorld =>
        HasMultiSelection && !_selBounds.IsEmpty
            ? new Rect(_selBounds.X + _moveDx, _selBounds.Y + _moveDy,
                       _selBounds.Width, _selBounds.Height)
        : _activeShape != null ? ShapeBounds(_activeShape)
        : Rect.Empty;

    /// <summary>World -> screen, the exact inverse of <see cref="ToWorld(Vector2)"/>.
    /// The selection chrome is XAML laid over the canvas, so it needs the same
    /// mapping the draw path uses rather than a second one that can drift.</summary>
    public Vector2 WorldToScreen(Vector2 world) => world * ViewZoom + ViewOffset;

    private bool AnyLocked =>
        _selected.Any(s => s.Locked) || _selShapes.Any(s => s.Locked) ||
        _selTexts.Any(t => t.Locked) || (_activeShape?.Locked ?? false);

    /// <summary>True while any part of the selection is locked - the padlock
    /// shows its closed state and the move / scale paths refuse to start.</summary>
    public bool SelectionLocked => AnyLocked;

    /// <summary>The selected attachment's file, or null when the subject is not
    /// a single attachment. The bar's paperclip is live only for this case, on
    /// 16.3's own rule: a subject that lacks a capability greys its control.</summary>
    public ShapeElement? SelectedAttachment
    {
        get
        {
            if (_activeShape is { Kind: ShapeKind.Image } a) return a;
            if (_selected.Count == 0 && _selTexts.Count == 0 &&
                _selShapes.Count == 1 && _selShapes[0].Kind == ShapeKind.Image)
                return _selShapes[0];
            return null;
        }
    }

    private static SubjectKind KindOf(bool ink, bool text, bool attach, bool otherShape)
    {
        int kinds = (ink ? 1 : 0) + (text ? 1 : 0) + (attach || otherShape ? 1 : 0);
        if (kinds == 0) return SubjectKind.None;
        if (kinds > 1) return SubjectKind.Mixed;
        if (ink) return SubjectKind.Ink;
        if (text) return SubjectKind.Text;
        return attach ? SubjectKind.Attachment : SubjectKind.Mixed;
    }

    /// <summary>A value shared by every stroke in the selection, or null when
    /// they disagree. Disagreement is a THIRD state, distinct from "the subject
    /// has no such property": the control stays live, it simply has no single
    /// number to print.</summary>
    private static T? Common<T>(IReadOnlyList<PenStroke> src, Func<PenStroke, T> pick) where T : struct
    {
        if (src.Count == 0) return null;
        T first = pick(src[0]);
        for (int i = 1; i < src.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(pick(src[i]), first)) return null;
        return first;
    }

    private void PublishSelection()
    {
        var strokes = _selected;
        bool ink = strokes.Count > 0;
        bool text = _selTexts.Count > 0;
        var attachment = SelectedAttachment;
        bool otherShape = _selShapes.Any(s => s.Kind != ShapeKind.Image) ||
                          (_activeShape != null && _activeShape.Kind != ShapeKind.Image);
        bool attach = attachment != null || _selShapes.Any(s => s.Kind == ShapeKind.Image);

        var kind = KindOf(ink, text, attach, otherShape);
        int count = strokes.Count + _selShapes.Count + _selTexts.Count +
                    (HasMultiSelection ? 0 : _activeShape != null ? 1 : 0);

        if (kind == SubjectKind.None)
        {
            SelectionState.Clear();
            SetVeil(false);
            return;
        }

        // 16.9: a STROKE has pen size, stability, opacity and a colour, so all
        // four stay live and report the stroke's own values. Anything else in
        // the selection takes one of them away - a photograph has no pen size,
        // a text box has no stabiliser - and that is the ONLY thing that greys a
        // control. An attachment keeps opacity, which is why 16.3 lists opacity
        // beside undo and redo as the three marks that stay live.
        bool pureInk = ink && !text && !attach && !otherShape;
        bool anyShapeOrText = text || attach || otherShape;

        var page = _page;
        var frozen = strokes.ToList();     // the closures below outlive this call
        var frozenTexts = _selTexts.ToList();

        // 25.1: A TEXT BOX CAN NOW TAKE A COLOUR, so the thing that greys the
        // colour dot is no longer "anything that is not pure ink". 16.3's rule is
        // unchanged and is now simply being applied to a subject whose
        // capabilities changed: an ATTACHMENT still cannot be recoloured and
        // neither can a drawn shape, so those two keep the white, inert dot. Ink
        // and text together take ONE colour - 25.6 argues why that is the honest
        // answer for a mixed lasso rather than refusing it.
        bool canColour = (ink || text) && !attach && !otherShape;

        // The selection's own colour, across BOTH kinds. A box with no colour of
        // its own reports the ink it is ACTUALLY DRAWN IN, so black strokes plus
        // a default text box on a white page read as one colour rather than as a
        // disagreement the dot would have to blank.
        Color? subjectInk = null;
        if (canColour)
        {
            bool first = true;
            foreach (var c in frozen.Select(s => ColorUtil.Parse(s.Color))
                                    .Concat(frozenTexts.Select(TextInkFor)))
            {
                if (first) { subjectInk = c; first = false; }
                else if (!subjectInk!.Value.Equals(c)) { subjectInk = null; break; }
            }
        }

        var subject = new SelectionSubject
        {
            Kind = kind,
            Count = count,
            HasPenSize = pureInk,
            HasStability = pureInk,
            // Opacity is the one property everything on the page has.
            HasOpacity = pureInk || attach || !anyShapeOrText,
            CanRecolour = canColour,
            Size = pureInk ? Common(frozen, s => s.Size) : null,
            Stability = pureInk ? Common(frozen, s => s.Sens) : null,
            Opacity = pureInk ? Common(frozen, s => s.Opacity ?? 1f) : null,
            Ink = subjectInk,
            SetSize = pureInk && page != null
                ? v => Restyle(frozen, RestyleStrokesAction.Field.Size, v)
                : null,
            SetStability = pureInk && page != null
                ? v => Restyle(frozen, RestyleStrokesAction.Field.Stability, v)
                : null,
            SetOpacity = pureInk && page != null
                ? v => Restyle(frozen, RestyleStrokesAction.Field.Opacity, v)
                : null,
            SetInk = canColour && page != null
                ? c => RecolourSelection(frozen, frozenTexts, c)
                : null,
            // 16.7 is about an ATTACHMENT being selected, and its example is the
            // handwriting greyed beneath one. A selected stroke does not fade the
            // page it is part of - 16.9 extends the PRESENTATION to strokes and
            // is explicit that 16.3's greying does not generalise, and the same
            // restraint applies here.
            FadesPage = attach,
        };

        SelectionState.Set(subject);
        SetVeil(subject.FadesPage);
    }

    private void Restyle(List<PenStroke> strokes, RestyleStrokesAction.Field field,
                         float value, string colour = "")
    {
        if (_page == null || strokes.Count == 0) return;
        PushAction(new RestyleStrokesAction(strokes, field, value, colour), _page);
        _inkCacheDirty = true;
        PublishSelection();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // 25: TEXT COLOUR
    // =======================================================================

    /// <summary>The colour ONE box is drawn in: its own if it has one, the page's
    /// ink convention if it does not. The single answer the editor, the veil, the
    /// raster and both exporters take.</summary>
    /// <para>25.4 puts the page's half of the answer in
    /// <see cref="PageTheme.TextInk"/>, where it can be compiled into a console
    /// harness and measured - see tools/TextColourRoundTrip.</para>
    private Color TextInkFor(TextElement t) =>
        t.TextColor is { Length: > 0 } hex
            ? ColorUtil.Parse(hex)
            : PageTheme.TextInk(_page != null ? ColorUtil.Parse(_page.Background) : Colors.White);

    /// <summary>2.2: PINS THE EDITOR'S OWN GROUND AND MARK AGAINST WinUI'S
    /// VISUAL STATES.
    ///
    /// <para><b>The defect, measured on screen.</b> A box being edited on a
    /// <c>#FCFCFC</c> page drew its words on <c>#606060</c> - a dark grey slab
    /// on white paper, <b>2.93:1</b>, under <see cref="PagePlate.MarkFloor"/>,
    /// and the lowest-contrast text anywhere on the page is the text the user is
    /// currently typing.</para>
    ///
    /// <para><b>Where #606060 comes from - arithmetic, not a guess.</b> The box
    /// below sets <c>Background = Transparent</c> and <c>Foreground = boxInk</c>,
    /// and WinUI's RichEditBox template overrides BOTH from its visual states:
    /// a VisualState setter outranks a local value for as long as the state
    /// holds. <c>TextControlBackgroundFocused</c> resolves to the DARK theme's
    /// <c>ControlFillColorInputActive</c>, <c>#B31E1E1E</c>, and composited over
    /// the paper that is
    /// <code>0x1E * (179/255) + 0xFC * (1 - 179/255) = 96.17 -> #606060</code>
    /// on all three channels. The theme is dark because
    /// <c>MainWindow.ApplyTheme</c> sets <c>RootGrid.RequestedTheme</c> from
    /// <c>PageTheme.IsDark</c> - the SHELL's darkness - and on a default install
    /// the shell is a pinned dark <c>#0F0E10</c> under white paper. §0's split
    /// pair once more, arriving through a WinUI resource rather than through one
    /// of ours.</para>
    ///
    /// <para><b>Why the ground and the mark HAD to move together.</b> Fixing the
    /// ground alone would have shipped a regression, and it was measured before
    /// it was avoided. <c>TextControlForegroundFocused</c> overrides
    /// <c>Foreground</c> the same way, and on a RE-OPENED box it wins: screen
    /// reading <c>#FFFFFF</c> on <c>#606060</c>, 6.29:1. Take the grey away
    /// without taking the white away and that becomes <c>#FFFFFF</c> on
    /// <c>#FCFCFC</c> - <b>1.02:1</b>, invisible, far worse than the defect being
    /// fixed. §0's rule is not a formality here: it is the difference between
    /// this change and a much worse bug.</para>
    ///
    /// <para><b>What is pinned, and what deliberately is not.</b> Background:
    /// all four states, to the Transparent this box already declares - the
    /// editor then stands on the PAGE, where <see cref="PageTheme.TextInk"/>
    /// carries a proven floor of 4.183:1 over the WHOLE sRGB gamut, not merely
    /// over the nine shipped papers. Foreground: <b>only</b> PointerOver and
    /// Focused, the two states that were observed overriding it. The NORMAL
    /// state is left alone on purpose - <c>ApplyTextVeil</c> writes
    /// <c>Foreground</c> on every unfocused box on every frame of a veil fade,
    /// and pinning Normal would freeze the veil solid.</para></summary>
    private static void PinEditorBrushes(RichEditBox box, Color ink)
    {
        try
        {
            var clear = new SolidColorBrush(Colors.Transparent);
            box.Resources["TextControlBackground"] = clear;
            box.Resources["TextControlBackgroundPointerOver"] = clear;
            box.Resources["TextControlBackgroundFocused"] = clear;
            box.Resources["TextControlBackgroundDisabled"] = clear;
            var mark = new SolidColorBrush(ink);
            box.Resources["TextControlForegroundPointerOver"] = mark;
            box.Resources["TextControlForegroundFocused"] = mark;
        }
        catch { }
    }

    /// <summary>25.2: writes a colour ACROSS A WHOLE LIVE BOX - the default
    /// character format so the next character typed takes it, and every existing
    /// character so what is already there takes it too. Both are needed: the
    /// default alone leaves the typed words behind, and the range alone leaves
    /// the caret carrying the old colour.</summary>
    private static void StampTextColour(RichEditBox box, Color ink)
    {
        try
        {
            var dcf = box.Document.GetDefaultCharacterFormat();
            dcf.ForegroundColor = ink;
            box.Document.SetDefaultCharacterFormat(dcf);
        }
        catch { }
        try { box.Document.GetRange(0, int.MaxValue).CharacterFormat.ForegroundColor = ink; }
        catch { }
        try { box.Foreground = new SolidColorBrush(ink); } catch { }
    }

    /// <summary>25.1: ONE colour onto everything in the selection that can take
    /// one - strokes and text boxes together, as a single undo step.
    ///
    /// <para>25.6: a lasso holding both is not refused and is not split. The user
    /// picked one colour with one gesture over one selection; handing the strokes
    /// that colour and leaving the words black would be the same "did nothing"
    /// failure 16.3 exists to prevent, one level down.</para></summary>
    private void RecolourSelection(List<PenStroke> strokes, List<TextElement> texts, Color c)
    {
        if (_page == null || (strokes.Count == 0 && texts.Count == 0)) return;
        string hex = ColorUtil.ToHex(c);
        // RecolourTextsAction captures each box's RTF so undo can put the words
        // back exactly, so the words have to BE in the model first.
        if (texts.Count > 0) FlushTexts();

        var parts = new List<IPageAction>();
        if (strokes.Count > 0)
            parts.Add(new RestyleStrokesAction(strokes, RestyleStrokesAction.Field.Colour, 0f, hex));
        if (texts.Count > 0)
            parts.Add(new RecolourTextsAction(texts, hex));
        PushAction(parts.Count == 1 ? parts[0] : new CompositeAction(parts, "Selection colour"), _page);

        if (texts.Count > 0)
        {
            // 25.5: the wheel is the only place a text colour is ever chosen, so
            // the colour just picked is also the one the NEXT box is created in.
            PendingTextColor = hex;
            try { TextColourChosen?.Invoke(hex); } catch { }
            // The boxes are XAML, not Win2D: only a rebuild re-reads the field,
            // and BuildTextUi's stamp is what puts the colour on the screen.
            RebuildTextLayer();
        }
        _inkCacheDirty = true;
        PublishSelection();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    /// <summary>25.3: the format bar's own per-run colour picker hands the box
    /// back to its RTF.
    ///
    /// <para>Without this the two controls fight silently: a box with a whole-box
    /// colour is re-stamped every time it is built, so a word coloured with the
    /// format bar would look right until the next rebuild and then quietly go
    /// back. The rule is "the last control you used wins", and it is the only one
    /// of the two that can be stated in a sentence.</para>
    ///
    /// <para>What the user gives up by reaching for the per-run picker is stated
    /// in 25.3 and is NOT new: per-run colour has never reached the canvas raster
    /// or either exporter, because RtfRunParser skips the colour table.</para></summary>
    public void ClearActiveTextColour()
    {
        if (_page == null || ActiveTextBox == null) return;
        foreach (var (k, ui) in _textUi)
        {
            if (!ReferenceEquals(ui.Box, ActiveTextBox)) continue;
            var t = _page.Texts.FirstOrDefault(x => x.Id == k);
            if (t is { TextColor: { Length: > 0 } }) { t.TextColor = null; ContentChanged?.Invoke(); }
            return;
        }
    }

    /// <summary>25.5: the user chose a colour for TYPED WORDS with nothing
    /// selected. It becomes the pending colour for the next box, and - if a box
    /// is open under the caret - that box's colour too, undoably.
    ///
    /// <para>The open box is recoloured IN PLACE rather than through
    /// RebuildTextLayer, because a rebuild clears ActiveTextBox and would take
    /// the caret out from under someone who is mid-sentence.</para></summary>
    public void SetTextColour(Color c)
    {
        string hex = ColorUtil.ToHex(c);
        PendingTextColor = hex;
        try { TextColourChosen?.Invoke(hex); } catch { }

        var box = ActiveTextBox;
        if (_page != null && box != null)
        {
            Guid id = Guid.Empty;
            foreach (var (k, ui) in _textUi)
                if (ReferenceEquals(ui.Box, box)) { id = k; break; }
            var t = id == Guid.Empty ? null : _page.Texts.FirstOrDefault(x => x.Id == id);
            if (t != null)
            {
                FlushTexts();                                  // capture the words as they stand
                PushAction(new RecolourTextsAction(new List<TextElement> { t }, hex), _page);
                StampTextColour(box, c);
                FlushTexts();                                  // and store the stamped RTF
                ContentChanged?.Invoke();
            }
        }
        _canvas.Invalidate();
    }

    // =======================================================================
    // THE TEXT BEING EDITED (CONCEPTS-REF 11.9)
    //
    // 11.9 asks for "quick-action buttons above the text bubble ... a Cancel
    // Editing affordance with a red X, and a row of attach / duplicate / lock /
    // delete marks". THE STATE THAT BELONGS TO IS EDITING, NOT SELECTION, and
    // this block exists because those are two different states in this file:
    //
    //   SELECTED is published through SelectionState by PublishSelection above,
    //   and is reached only by the lasso (SelectInPolygon), a click on a stroke
    //   (SelectSingleStroke, 16.10), a paste, Select All and the table row and
    //   column selectors. NOTHING puts a text box into _selTexts because it was
    //   tapped into, so a box being typed in publishes no selection and
    //   SelectionChrome never appears for it.
    //
    //   EDITING is ActiveTextBox - a RichEditBox with the caret in it. It raises
    //   ActiveTextChanged, and the surface it currently brings up is the pinned
    //   top FormatBar (MainWindow.UpdateFormatBarVisibility).
    //
    // "CANCEL EDITING" IS WHAT SETTLES WHICH OF THE TWO 11.9 MEANS. You cannot
    // cancel editing a box you are not editing; a lasso-selected text box has no
    // caret and no focus. So 11.9 is the editing state's own subject, and
    // SelectionChrome reads it through the members below exactly as it reads
    // SelectionLocked and SelectedAttachment for the selected state - one class,
    // two triggers, so the two bars can never be on screen together.
    //
    // TABLE CELLS ARE NOT SUBJECTS. A cell's bubble has no independent existence
    // to duplicate, lock or delete - its TextElement IS the cell, and this file
    // already records (see the LostFocus guard, #cellfix) that removing one
    // leaves the cell untypeable forever. Every member below reads through
    // EditingPair, which returns null for anything carrying a TableId.
    // =======================================================================

    /// <summary>Raised when the bubble being edited changes SHAPE or POSITION -
    /// it grew a line, or its grip was dragged. The chrome above it has to move
    /// with it, and neither <see cref="ViewChanged"/> (pan and zoom only) nor
    /// <see cref="ActiveTextChanged"/> (which box, not where) fires for either.
    ///
    /// <para>Raised from the container's own SizeChanged rather than from the
    /// TextChanged that caused it, because AutoSizeBubble sets Width and Height
    /// and the container's ActualWidth does not follow until layout has run. A
    /// bar placed from the pre-layout size lags the bubble by one frame on every
    /// keystroke, which is exactly the jitter this event exists to avoid.</para></summary>
    public event Action? EditingTextGeometryChanged;

    private void RaiseEditingGeometry(RichEditBox box)
    {
        if (ReferenceEquals(ActiveTextBox, box)) EditingTextGeometryChanged?.Invoke();
    }

    /// <summary>The model and the container behind <see cref="ActiveTextBox"/>,
    /// or null when nothing is being edited or the box is a table cell.</summary>
    private (TextElement Text, Grid Container)? EditingPair()
    {
        if (_page == null || ActiveTextBox == null) return null;
        foreach (var (id, ui) in _textUi)
        {
            if (!ReferenceEquals(ui.Box, ActiveTextBox)) continue;
            var t = _page.Texts.FirstOrDefault(x => x.Id == id);
            return t is { TableId: null } ? (t, ui.Container) : null;
        }
        return null;
    }

    /// <summary>The free text bubble currently being edited, or null. This is
    /// 11.9's subject.</summary>
    public TextElement? EditingText => EditingPair()?.Text;

    /// <summary>16.2's padlock, asked of the editing subject. Locked greys the
    /// waste bin, on the same rule the selection bar follows: a control the
    /// subject cannot act through says so rather than looking live.</summary>
    public bool EditingTextLocked => EditingText?.Locked ?? false;

    /// <summary>World bounds of the bubble being edited - what 11.9's bar is
    /// placed "above". Read off the CONTAINER rather than off the model, because
    /// the model carries X, Y and Width but no height: a bubble's height is
    /// whatever its text just wrapped to.
    ///
    /// <para>Canvas.Left/Top and ActualWidth/Height here are all in WORLD units.
    /// The text layer carries the pan/zoom as a RenderTransform, so its children
    /// are laid out in world space and painted through it - which is also why
    /// the grip's drag deltas are applied to Canvas.Left directly.</para>
    ///
    /// <para>A ROTATED bubble reports its unrotated box. That is the same
    /// approximation <see cref="SubjectBoundsWorld"/> makes through ShapeBounds
    /// for a rotated attachment, and it keeps the bar horizontal above a
    /// tilted box rather than tilting the controls with it.</para></summary>
    public Rect EditingTextBoundsWorld
    {
        get
        {
            if (EditingPair() is not { } p) return Rect.Empty;
            double w = p.Container.ActualWidth, h = p.Container.ActualHeight;
            if (w <= 0 || h <= 0) return Rect.Empty;
            double x = Canvas.GetLeft(p.Container), y = Canvas.GetTop(p.Container);
            if (double.IsNaN(x) || double.IsNaN(y)) return Rect.Empty;
            return new Rect(x, y, w, h);
        }
    }

    /// <summary>11.9's red X. IT CANCELS EDITING, NOT THE TEXT - the words stay
    /// on the page and the caret leaves. Anything else would make a red X beside
    /// a waste bin mean the same thing twice, and the destructive one is the bin.
    ///
    /// <para>The blur is done by taking focus onto this control, which is
    /// already how <see cref="SetPendingText"/> takes focus off a RichEditBox
    /// when the Text tool taps empty canvas - the same two lines, including
    /// flipping IsTabStop for the call, because this control is not a tab stop
    /// the rest of the time (see the constructor). The box's own LostFocus
    /// handler is then what clears ActiveTextBox and raises ActiveTextChanged,
    /// so cancelling and clicking away leave the app in one state rather than
    /// two.</para>
    ///
    /// <para>An EMPTY box is removed by that same handler, exactly as it is when
    /// the user clicks away from one. Cancelling out of a box you never typed
    /// into leaves nothing behind, which is the existing promise.</para></summary>
    public void CancelTextEditing()
    {
        if (ActiveTextBox == null) return;
        FlushTexts();                 // commit the live RTF into the model first
        bool tab = IsTabStop;
        IsTabStop = true;
        Focus(FocusState.Programmatic);
        IsTabStop = tab;
    }

    /// <summary>11.9's duplicate, on the editing subject. Deliberately NOT
    /// <see cref="DuplicateSelection"/>: that reads _selected / _selShapes /
    /// _selTexts, all of which are empty while a box is merely being typed in.
    /// The 40-unit offset is the same one it uses, so a duplicated bubble lands
    /// where a duplicated anything else does.</summary>
    public void DuplicateEditingText()
    {
        if (_page == null || EditingText is not { } t) return;
        FlushTexts();                 // the clone must carry what has just been typed
        const double offset = 40;
        // The sixth clone path, and it dropped the same two things the other five
        // did: the padlock and the LAYER (CONCEPTS-REF 18.10). CloneAsFreeBox
        // rather than Clone because EditingPair never hands back a table cell -
        // so today the two are the same copy, and if that filter ever changes,
        // duplicating mid-type still cannot stack a ghost cell on the original.
        var clone = t.CloneAsFreeBox();
        clone.X += offset;
        clone.Y += offset;
        PushAction(new AddTextAction(clone), _page);
        // Only the new box, never RebuildTextLayer: a full rebuild would steal
        // focus from the box the user is still typing in, which is the same
        // reason SpawnTextBox builds one box rather than the layer (A2).
        BuildTextUi(clone);
        ContentChanged?.Invoke();
    }

    /// <summary>11.9's padlock. One text, but through the SAME LockMixedAction
    /// the selection bar pushes, so a bubble locked from the editing bar and one
    /// locked from the selection bar are one undo step of one kind.</summary>
    public void ToggleEditingTextLock()
    {
        if (_page == null || EditingText is not { } t) return;
        PushAction(new LockMixedAction(new List<PenStroke>(), new List<ShapeElement>(),
                                       new List<TextElement> { t }, !t.Locked), _page);
        ContentChanged?.Invoke();
    }

    /// <summary>11.9's waste bin. Refuses while locked, which is 16.2's rule -
    /// "a lock that stops a drag but not a delete is not a lock" - and the bar
    /// greys the mark as well, so the refusal is visible before it is attempted.
    ///
    /// <para>The teardown is RemoveTextAction + RebuildTextLayer +
    /// ActiveTextChanged(null), which is exactly what the box's own close button
    /// used to do. That button is gone: see BuildTextUi.</para></summary>
    public void DeleteEditingText()
    {
        if (_page == null || EditingText is not { } t || t.Locked) return;
        FlushTexts();
        PushAction(new RemoveTextAction(t), _page);
        RebuildTextLayer();           // clears ActiveTextBox
        ActiveTextChanged?.Invoke(null);
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // 16.7: WHILE AN ATTACHMENT IS SELECTED, THE PAGE FADES TO #8E8E8E
    //
    // "make texts the exact shade of grey shown in photo ... make them slowly
    // turn grey not instantly". Everything the user put on the page
    // de-emphasises so the attachment reads as the thing being worked on.
    //
    // THE CONSTRAINT THAT OUTRANKS EVERYTHING ELSE, from 16.7 item 1: this is a
    // RENDER-TIME effect and must never touch stored colour. The whole mechanism
    // is one pure function, Veil(Color) -> Color, applied to a LOCAL variable at
    // the exact point a stored colour string has just been parsed for drawing:
    //
    //     var color = Veil(ColorUtil.Parse(s.Color), exempt);
    //
    // It takes a Color and returns a Color. It has no reference to the stroke,
    // the shape, the text or the page, so there is nothing for it to write to
    // even by accident, and every alpha and grain variant downstream is derived
    // from that one local. A page saved while faded is byte-identical to the
    // same page saved unfaded - see scratchpad/prove_veil.py, which round-trips
    // a library through select -> save -> deselect -> reload and diffs the
    // stored colours against a baseline.
    // =======================================================================

    /// <summary>16.7: "The colour is #8E8E8E, given directly by the user. Not
    /// sampled, not approximated - that exact value."</summary>
    private static readonly Color VeilGrey = Color.FromArgb(0xFF, 0x8E, 0x8E, 0x8E);

    private double _veil;          // 0 = the page's own colours, 1 = fully #8E8E8E
    private bool _veilWant;        // where it is heading
    private bool _veilTicking;
    private long _veilLastTick;

    /// <summary>True while any of the page is de-emphasised.
    ///
    /// <para><b>Never during an export.</b> <see cref="ExportChromeless"/> is
    /// set for the duration of a capture and already means "this frame is the
    /// drawing, never the editor". A page fade is editor state by definition -
    /// it says which object is being worked on - so an export taken while an
    /// attachment happens to be selected must come out in the page's own
    /// colours. This is the same class of promise as 16.7 item 1 (never write
    /// grey into stored colour), one step further out: never write it into a
    /// file the user asked for either.</para></summary>
    private bool Veiling => _veil > 0.0005 && !ExportChromeless;

    /// <summary>THE WHOLE EFFECT. A pure Color -> Color, called on a local that
    /// has just been parsed out of stored data and is about to be handed to
    /// Win2D. RGB is pulled toward <see cref="VeilGrey"/>; ALPHA IS NOT TOUCHED,
    /// because the stroke's own opacity is applied to it further down and
    /// fading a translucent stroke must not also make it more opaque.</summary>
    private Color Veil(Color c, bool exempt = false)
    {
        if (exempt || !Veiling) return c;
        double t = Motion.FadeEase(_veil);
        static byte Mix(byte a, byte b, double k) => (byte)Math.Clamp(a + (b - a) * k, 0, 255);
        return Color.FromArgb(c.A, Mix(c.R, VeilGrey.R, t), Mix(c.G, VeilGrey.G, t), Mix(c.B, VeilGrey.B, t));
    }

    /// <summary>The elements THIS veil exempts, so they hold full contrast -
    /// 16.7 item 3, "the attachment itself does not fade. It is the subject."
    ///
    /// <para>A SNAPSHOT of the selection rather than a live read of it, for the
    /// reason <see cref="CaptureVeilSubject"/> gives at length (17.13).</para>
    ///
    /// <para>WITH TWO ATTACHMENTS, both selected ones hold contrast and every
    /// unselected one fades with the rest of the page. That falls out of filling
    /// this set from the SELECTION rather than asking "is this element an
    /// image?", and it is the reading that keeps the effect meaning what it says:
    /// the page recedes behind WHAT IS BEING WORKED ON, and if the user has two
    /// attachments in hand then both of them are.</para></summary>
    private readonly HashSet<object> _veilSubject = new(ReferenceEqualityComparer.Instance);

    private bool IsSubject(PenStroke s) => _veilSubject.Contains(s);
    private bool IsSubject(ShapeElement s) => _veilSubject.Contains(s);
    private bool IsSubject(TextElement t) => _veilSubject.Contains(t);

    /// <summary>17.13: TAKE THE EXEMPTION FROM THE SAME INSTANT AS THE VEIL.
    ///
    /// <para>The veil has two halves and they used to be read off two different
    /// clocks. HOW MUCH veil is <c>_veil</c>, an animated double that takes
    /// 190 ms up and 130 ms down and therefore lags the selection on purpose.
    /// WHO is exempt was read from <c>_selectedSet</c>, <c>_selShapeSet</c> and
    /// <c>_selTexts</c>, which turn over in the instant the click lands. One
    /// <c>OnDraw</c> reads both, so on any edge where the two disagree it paints
    /// a veil raised for one selection through an exemption belonging to
    /// another.</para>
    ///
    /// <para><b>Going in, the two agreed by luck.</b> <c>_activeShape</c> assigns
    /// its backing field before it publishes, so the subject was already exempt
    /// on the first frame - and <c>_veil</c> starts from 0 there in any case, so
    /// even a frame of disagreement would have shown nothing. <b>Coming out they
    /// could not agree.</b> <see cref="PublishSelection"/> clears the selection
    /// and only then calls <see cref="SetVeil"/>, which leaves <c>_veil</c>
    /// sitting at 1 with NOTHING exempt: for the 130 ms of the fade-out the
    /// attachment the veil had been raised for was painted with that veil -
    /// fully grey on the first frame, decaying to none over the rest. That is
    /// 17.13's "turns grey for a moment and returns", and it is exactly why
    /// clicking INTO an attachment never showed it and clicking OUT always
    /// did.</para>
    ///
    /// <para><b>Why a snapshot rather than a second exemption.</b> Another clause
    /// on the test above would silence this one edge and leave both clocks
    /// running, so the next thing to change the settle timing would bring it back
    /// somewhere else. Capturing the subject where the veil level is set leaves
    /// ONE clock. While the veil is up or rising the snapshot is refreshed from
    /// the live selection on every publish, so swapping to a second attachment
    /// moves the exemption in the same frame; while it is coming down the
    /// snapshot is HELD, so the subject the veil was raised for keeps full
    /// contrast until that veil is gone, and <see cref="VeilTick"/> releases it
    /// at <c>_veil</c> 0 - the frame on which nothing is veiled anyway. The
    /// selected attachment therefore never fades, not even transiently.</para>
    ///
    /// <para>Reference identity, not value equality: two strokes with identical
    /// points are two subjects, and <see cref="ReferenceEqualityComparer"/> keeps
    /// saying so even if these models ever become records.</para></summary>
    private void CaptureVeilSubject()
    {
        _veilSubject.Clear();
        foreach (var s in _selectedSet) _veilSubject.Add(s);
        foreach (var sh in _selShapeSet) _veilSubject.Add(sh);
        foreach (var t in _selTexts) _veilSubject.Add(t);
        if (_activeShapeBack != null) _veilSubject.Add(_activeShapeBack);
    }

    private void SetVeil(bool on)
    {
        // 17.13: refresh WHO is exempt whenever the veil is up or heading up -
        // including when _veilWant is already true and the early return below
        // fires. Selecting a second attachment has to move the exemption in the
        // same frame, and that arrives as a publish, not as a veil edge.
        if (on) CaptureVeilSubject();
        if (_veilWant == on) return;
        _veilWant = on;
        if (ReduceMotion?.Invoke() == true)
        {
            StopVeilTick();
            _veil = on ? 1 : 0;
            // No fade to outlive, so the subject is released here instead.
            if (!on) _veilSubject.Clear();
            ApplyTextVeil();
            _inkCacheDirty = true;
            _canvas.Invalidate();
            return;
        }
        // A hand-pumped tween on the same 190 / 130 and the same curve the menus
        // and the fullscreen strip use (Helpers/Motion). Hand-pumped rather than
        // a Storyboard for the reason FullscreenChrome sets out at length: this
        // reverses mid-flight every time a selection is dropped before the fade
        // has finished, and a Storyboard holds its end value and reverts to the
        // BASE value on Stop, so every reversal would jump.
        //
        // 16.7 item 2: "Fade back on deselect too; a one-directional fade would
        // leave the page grey until something forced a repaint." That is why the
        // tween runs on both edges and why the LAST frame of the fade-out still
        // invalidates - the final Invalidate at _veil == 0 is the repaint that
        // puts the page's own colours back.
        PumpVeil();
    }

    private void PumpVeil()
    {
        if (_veilTicking) return;
        _veilTicking = true;
        _veilLastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        // The ink cache holds RENDERED pixels, so it cannot follow a fade. Mark
        // it stale once at the start and again at the end; in between,
        // cacheEligible refuses it outright rather than rebuilding it per frame.
        _inkCacheDirty = true;
        CompositionTarget.Rendering += VeilTick;
    }

    private void StopVeilTick()
    {
        if (!_veilTicking) return;
        _veilTicking = false;
        CompositionTarget.Rendering -= VeilTick;
    }

    private void VeilTick(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double ms = (now - _veilLastTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _veilLastTick = now;
        double target = _veilWant ? 1 : 0;
        _veil = Motion.Step(_veil, target, ms, _veilWant ? Motion.OpenMs : Motion.CloseMs);
        ApplyTextVeil();
        _canvas.Invalidate();
        if (_veil != target) return;
        StopVeilTick();
        // 17.13: the veil is fully lifted, so the selection it was raised for
        // stops being exempt HERE rather than back on the deselect that started
        // this fade. Holding it across the fade is the fix; releasing it on the
        // frame where nothing is veiled anyway is what keeps that honest.
        if (!_veilWant) _veilSubject.Clear();
        _inkCacheDirty = true;
        _canvas.Invalidate();       // the repaint that restores the page's colours
    }

    /// <summary>The typed-text half of the fade.
    ///
    /// <para>Text is NOT drawn by the Win2D path - every text element is a live
    /// <c>RichEditBox</c> in the XAML overlay - so the one-line intercept the
    /// strokes and shapes get is not available here. This sets the CONTROL's
    /// <c>Foreground</c>, which is a XAML property and is never serialised:
    /// <c>FlushTexts</c> stores <c>Document.GetText(FormatRtf)</c>, and the
    /// document is not touched by this at all.</para>
    ///
    /// <para><b>Its limit, stated rather than hidden.</b> A run that carries its
    /// own colour in the RTF - which Quill's own boxes do, because the default
    /// character format is written with the page's ink colour and comes back as
    /// a <c>\colortbl</c> plus <c>\cf1</c> - overrides <c>Foreground</c> and
    /// will not grey. The only way to move those is to write the document's
    /// character format, and the document is exactly what <c>FlushTexts</c>
    /// serialises. 16.7 item 1 makes that trade for us: a visual nicety must
    /// never become data loss, so the run keeps its colour.</para></summary>
    private void ApplyTextVeil()
    {
        if (_page == null) return;
        double t = Motion.FadeEase(_veil);
        foreach (var (id, ui) in _textUi)
        {
            var model = _page.Texts.FirstOrDefault(x => x.Id == id);
            bool exempt = model != null && IsSubject(model);
            // 25.4: fade FROM the box's own colour, not from the page's ink. A
            // red box that greyed towards black and came back black would be the
            // "visual nicety became data loss" this method's remarks forbid.
            var ink = model != null ? TextInkFor(model) : PageTheme.TextInk(ColorUtil.Parse(_page.Background));
            var shown = exempt || t <= 0.0005
                ? ink
                : Color.FromArgb(255,
                    (byte)Math.Clamp(ink.R + (VeilGrey.R - ink.R) * t, 0, 255),
                    (byte)Math.Clamp(ink.G + (VeilGrey.G - ink.G) * t, 0, 255),
                    (byte)Math.Clamp(ink.B + (VeilGrey.B - ink.B) * t, 0, 255));
            try { ui.Box.Foreground = new SolidColorBrush(shown); } catch { }
        }
    }

    // =======================================================================
    // 16.2's bar and bottom row, as operations on the selection
    // =======================================================================

    private (List<PenStroke> S, List<ShapeElement> H, List<TextElement> T) SelectionParts()
    {
        var s = _selected.ToList();
        var h = _selShapes.ToList();
        var t = _selTexts.ToList();
        if (h.Count == 0 && s.Count == 0 && t.Count == 0 && _activeShape != null) h.Add(_activeShape);
        return (s, h, t);
    }

    /// <summary>The text boxes in a selection that a ROTATION may turn: the free
    /// ones. Table cells are dropped.
    ///
    /// <para><b>A cell already turns - with its table, and only with its
    /// table.</b> LayoutTableCells recomputes a cell's X/Y from the table's
    /// geometry and drives its container's transform from the TABLE's Rotation
    /// on every layout pass, so a cell turned on its own is put straight back.
    /// What was NOT put back is the cell's own <see cref="TextElement.Rotation"/>
    /// - it survives in the model, and <c>DrawTextElement</c> reads it, so a
    /// lassoed table that had been rotated came out of "copy as image" and the
    /// exporters with its cell words at twice the angle of its grid while the
    /// live canvas looked correct. Dropping cells here is what makes the stored
    /// angle and the drawn one the same number again.</para>
    ///
    /// <para>Shared by the quarter turn and the free sweep deliberately: two
    /// rotations that disagreed about what a table cell is would be exactly the
    /// kind of second path this codebase keeps saying not to add.</para></summary>
    private static List<TextElement> RotatableTexts(List<TextElement> texts)
    {
        if (texts.Count == 0) return texts;
        var free = texts.Where(t => t.TableId == null).ToList();
        return free.Count == texts.Count ? texts : free;
    }

    /// <summary>16.2's flip-horizontal / flip-vertical. Mirrors about the
    /// selection's own centre line, so the selection lands exactly where it was
    /// and only its contents turn over.</summary>
    public void FlipSelection(bool horizontal)
    {
        if (_page == null || AnyLocked) return;
        var b = SubjectBoundsWorld;
        if (b.IsEmpty) return;
        var (s, h, t) = SelectionParts();
        if (s.Count + h.Count + t.Count == 0) return;
        FlushTexts();
        PushAction(new MirrorMixedAction(s, h, t, horizontal ? b.Left + b.Width / 2 : b.Top + b.Height / 2,
                                         horizontal), _page);
        AfterSelectionTransform(t.Count > 0);
    }

    /// <summary>16.2's bottom row: Rotate. A quarter turn clockwise about the
    /// selection's centre - four presses return it exactly, which is why the
    /// action swaps and negates coordinates rather than multiplying by a
    /// sine.</summary>
    public void RotateSelectionQuarter(bool clockwise = true)
    {
        if (_page == null || AnyLocked) return;
        var b = SubjectBoundsWorld;
        if (b.IsEmpty) return;
        var (s, h, t) = SelectionParts();
        if (s.Count + h.Count + t.Count == 0) return;
        t = RotatableTexts(t);
        FlushTexts();
        PushAction(new RotateQuarterMixedAction(s, h, t, b.Left + b.Width / 2, b.Top + b.Height / 2,
                                                clockwise), _page);
        AfterSelectionTransform(t.Count > 0);
    }

    // =======================================================================
    // 17.11 / 17.11a: the rotate tool
    // =======================================================================
    //
    // 17.11a SUPERSEDES 17.11's reading and splits this tool into two halves
    // that share one interface. Only one of them turns anything today, and the
    // whole design of this block is about making that impossible to mistake.
    //
    // THE PAGE HALF (17.11a requirement 1) IS THE INTERFACE AND NOTHING ELSE.
    // "The rotate tool rotates the PAGE, freely." No page-rotation state exists
    // anywhere in this codebase: the view transform is a scale and a translate,
    // and turning it is the roadmap's tilt item - 62 inline screen/world
    // conversions and 51 axis-aligned rectangles that stop being valid the
    // moment the canvas is not square to the screen, audited at 3-5 days plus a
    // full input regression. So PageRotationDeg is STORED, REPORTED, AND
    // APPLIED NOWHERE. It never reaches DrawRegion's transform; it never
    // reaches the top bar's tilt readout, which stays at 0 because the CANVAS
    // is still at 0; and it is not written to the page, because a rotation
    // saved into a document that cannot honour it is a lie with a long
    // half-life. The user asked to see and correct the interface first, and
    // this is that interface with nothing behind it pretending otherwise.
    //
    // THE SELECTION HALF NOW TURNS FREELY - 17.11a requirement 2, delivered.
    //
    // WHAT CHANGED, AND WHY IT COULD. The comment that used to stand here said
    // quarter turns were forced because "a TextElement is an axis-aligned box
    // with a width and a height and takes NONE" of a rotation, so a free drag
    // would turn two subject kinds out of three and leave text square.
    //
    // THAT WAS FALSE, AND THIS COMMENT IS HOW IT SPREAD. It was written in
    // 17.11a from an agent's report, copied down here as prose, and after that
    // every reader of this file met the claim restated as fact directly above
    // the code that disproves it - while the field itself, twelve hundred lines
    // away in NoteModels.cs, was never the thing anyone checked. The full
    // correction, and the reason the wrong version is kept visible rather than
    // deleted, is CONCEPTS-REF 17.11a.1.
    //
    // TextElement has carried Rotation since #20; the Win2D path draws through
    // it (DrawTextElement), the editing overlay applies it as a RenderTransform
    // about the container centre, RotateActiveText drives it, the mirror negates
    // it, the quarter turn adds to it, every clone path carries it, and the
    // box's own grip bar has had a FREE-ANGLE drag handle on it the whole time.
    // The audit for this change found the assumptions that really were left -
    // selection bounds, the click probe and the table cell, all three fixed - and
    // no subject kind that cannot take an angle. Export honours no rotation for
    // ANY kind and still does not; that is 17.11a.1's last bullet, not this
    // tool's business.
    //
    // THE QUARTER TURN SURVIVES, AS A BUTTON AND NOT AS THE SWEEP. The mode
    // bar's Rotate control and 16.2's bottom row both call
    // RotateSelectionQuarter, and they still do: "turn this a quarter" is a
    // different thing to want than "turn this to here", not a degraded version
    // of it, and it is the one turn a hand cannot make exactly. What is gone is
    // the quarter DETENT INSIDE THE DRAG - 17.11a says the user asked for free
    // and that "any snap must be opt-in", so the sweep now commits the angle the
    // hand actually described. RotateSnap, already the tool's opt-in for the
    // page half, is that opt-in here too and steps the same 15 degrees.
    //
    // HOW ONE TOOL CARRIES BOTH WITHOUT A MODE SWITCH: BY WHERE THE PRESS LANDS.
    // The page half owns exactly two objects and they are the two the overlay
    // draws - the donut handle and the pivot crosshair. A press on either is a
    // page gesture; a press anywhere else is the selection sweep this tool has
    // always been. Nothing is hidden behind a toggle, so there is no state in
    // which the user cannot tell which rotation they are about to get: the two
    // things that move the page angle are the two things that are drawn.
    private bool _rotating;
    // How far the live sweep has turned the selection, in degrees. The model is
    // moved AS THE HAND MOVES so the user rotates the drawing rather than a
    // preview of it; this is what the single action pushed on release is for,
    // and what that action is handed back to un-apply before it captures the
    // before-state it will need for undo.
    private double _rotateTurnedDeg;
    private Vector2 _rotateCentre;
    private double _rotateFromDeg;      // pointer bearing when the sweep began
    // The subject, captured ONCE at press. Re-reading the selection mid-drag
    // would be re-reading a selection whose bounds this very drag is changing.
    private List<PenStroke> _rotateInk = new();
    private List<ShapeElement> _rotateShapes = new();
    private List<RotateFreeMixedAction.SizedText> _rotateTexts = new();

    // ---- the page half: state, and the only thing that reads it is a draw --

    /// <summary>17.11a's page rotation, in degrees clockwise from the page's own
    /// horizontal, wrapped into (-180, 180].
    ///
    /// <para><b>NOTHING APPLIES THIS.</b> It is the value the tool's handle
    /// drives and the value the tool's menu reports, and it reaches neither the
    /// view transform nor the saved page. Search this file for the property: the
    /// only readers are the overlay draw and the report. That is deliberate and
    /// it is the whole shape of this change - see the block header.</para></summary>
    public double PageRotationDeg { get; private set; }

    /// <summary>Raised on every change, so the tool's menu can report the number
    /// without polling. Carries the new value rather than making the listener
    /// read it back, which is how a listener ends up one frame stale.</summary>
    public event Action<double>? PageRotationChanged;

    /// <summary>17.11a: <i>"whether rotation snaps at all. The user said free, so
    /// any snap must be opt-in, and 0 should not be sticky unless asked for."</i>
    ///
    /// <para>So this is OFF by default and there is no other detent in the
    /// gesture - not a soft one near 0, not a magnet at the quarters. With it
    /// off, <see cref="SetPageRotation"/> stores whatever bearing the hand gave
    /// it. With it on, every step is a multiple of
    /// <see cref="RotateSnapStepDeg"/> INCLUDING 0, because a snap that skipped
    /// its own zero would be a stranger rule than either choice.</para></summary>
    public bool RotateSnap { get; set; }

    /// <summary>The opt-in snap's step. 15 rather than 45 or 90: the tool is a
    /// free rotation and the snap is an aid inside it, so the step has to be
    /// fine enough that turning it on does not become a different tool.</summary>
    public const double RotateSnapStepDeg = 15;

    // Where the pivot is, and whether the user has put it anywhere. UNPLACED is
    // not "at the origin" - it is "the middle of what you are looking at", which
    // is why the getter derives it rather than storing it. The moment the user
    // drags it, it becomes a point on the PAGE and stops following the view:
    // the tool turns the page about a place on the page, so the pivot is world
    // space as soon as it means anything.
    private Vector2 _rotatePivot;
    private bool _rotatePivotPlaced;

    // 0 until a drag sets it; the getter derives the default from the viewport
    // so a small window does not get a handle off the edge of itself.
    private float _rotateRadiusPx;

    private enum RotateGrab { None, Handle, Pivot }
    private RotateGrab _rotateGrab;

    // Every number the interface is drawn and hit-tested with, in SCREEN PIXELS.
    // Multiplied by 1/ViewZoom at the point of use, so the assembly is the same
    // physical size at 0.1x and at 16x. It is chrome, not content: a handle that
    // shrank with the page would be unusable at exactly the zoom levels where
    // turning the page is most wanted.
    private const float RotateLineWidthPx = 1.6f;
    private const float RotateCrossGapPx = 7f;
    private const float RotateCrossTickPx = 7.5f;
    private const float RotateCrossWidthPx = 2.4f;
    private const float RotateArcWidthPx = 3.2f;
    private const float RotateArcHalfSpanDeg = 17f;
    private const float RotateHandleRingPx = 8.5f;
    private const float RotateHandleRingWidthPx = 3.2f;
    private const float RotateGlowTightPx = 4.5f;
    private const float RotateGlowWidePx = 11f;
    private const byte RotateGlowTightAlpha = 165;
    private const byte RotateGlowWideAlpha = 95;
    private const float RotateHandleReachPx = 20f;
    private const float RotatePivotReachPx = 18f;
    private const float RotateRadiusMinPx = 48f;

    /// <summary>17.11a's colour, given directly by the user: <c>#BF3D38</c>. It
    /// is NOT the app accent and must not follow it - every other overlay on
    /// this canvas is drawn in the accent, and this one being fixed is what
    /// keeps it recognisable as the rotate interface on any theme.</summary>
    private static readonly Color RotateInk = Color.FromArgb(255, 0xBF, 0x3D, 0x38);

    /// <summary>The donut's filled centre. 17.11a says "a filled dark centre
    /// inside a red ring", and dark is taken literally rather than as the page's
    /// ground: on the dark page of the capture it reads as a hole through the
    /// arc, and on a light page it reads as a dark plug. Either way it is a
    /// donut and not a dot, which is the distinction the capture makes.</summary>
    private static readonly Color RotateHandleCore = Color.FromArgb(255, 20, 20, 19);

    /// <summary>Where the pivot is, in world units.
    ///
    /// <para><b>Answering 17.11a's first question: the pivot is PLACED, and it
    /// starts under the middle of the view.</b> Not the selection's centre - the
    /// tool turns the PAGE, and a page pivot that jumped every time the
    /// selection changed would be a pivot nobody could aim. Not a fixed viewport
    /// centre either, because the capture shows it left of centre. Unplaced it
    /// tracks the middle of the viewport, so choosing the tool always shows a
    /// usable interface rather than nothing; dragging the crosshair pins it to a
    /// point on the page, and from then on it pans and zooms with the
    /// drawing.</para></summary>
    public Vector2 RotatePivotWorld =>
        _rotatePivotPlaced
            ? _rotatePivot
            : ToWorld(new Vector2((float)ActualWidth / 2, (float)ActualHeight / 2));

    /// <summary>Pivot to arc, in screen pixels - which is to say the drag radius,
    /// and therefore the gesture's angular sensitivity.
    ///
    /// <para><b>Answering 17.11a's second question: THE HAND SETS IT,
    /// CONTINUOUSLY.</b> A handle drag takes its bearing AND its distance from
    /// the pointer, so pulling further out mid-drag makes the rest of that same
    /// drag finer - one degree costs more travel at a longer radius - and the
    /// arc redraws at the radius actually in force, so the sensitivity the user
    /// has is the sensitivity they can see. It persists after the drag, so the
    /// next one starts with the reach they chose. Floored at
    /// <see cref="RotateRadiusMinPx"/>: at a radius near zero a pixel of hand
    /// movement is most of a turn, and no amount of care makes that
    /// usable.</para></summary>
    private float RotateRadiusPx
    {
        get
        {
            if (_rotateRadiusPx > 0) return _rotateRadiusPx;
            double shortSide = Math.Min(ActualWidth, ActualHeight);
            if (shortSide <= 0) shortSide = 640;   // before the first layout pass
            return (float)Math.Max(RotateRadiusMinPx, shortSide * 0.28);
        }
    }

    /// <summary>The donut's centre, in world units: the point on the line, at the
    /// drag radius, in the direction of the current angle. The arc crosses the
    /// line here, which is what makes "where the arc meets the line" a place
    /// rather than a description.</summary>
    private Vector2 RotateHandleWorld()
    {
        double r = PageRotationDeg * Math.PI / 180.0;
        return RotatePivotWorld +
               new Vector2((float)Math.Cos(r), (float)Math.Sin(r)) * (RotateRadiusPx / ViewZoom);
    }

    /// <summary>Store the angle and report it. The ONE place
    /// <see cref="PageRotationDeg"/> is assigned, so the snap cannot be applied
    /// on one route and skipped on another.</summary>
    private void SetPageRotation(double deg)
    {
        if (RotateSnap) deg = Math.Round(deg / RotateSnapStepDeg) * RotateSnapStepDeg;
        deg = WrapDegrees(deg);
        if (Math.Abs(deg - PageRotationDeg) < 1e-6) return;
        PageRotationDeg = deg;
        try { PageRotationChanged?.Invoke(deg); } catch { }
    }

    /// <summary>Into (-180, 180], so a sweep across the -x axis reads as a small
    /// step rather than a 350 degree jump back the other way.</summary>
    private static double WrapDegrees(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg <= -180) deg += 360;
        return deg;
    }

    /// <summary>Un-place the pivot, putting it back under the middle of the view.
    /// Published for the tool's own bottom menu; there is no second way to move
    /// it, because the crosshair drag is the first.</summary>
    public void ResetRotatePivot()
    {
        _rotatePivotPlaced = false;
        _canvas.Invalidate();
    }

    /// <summary>Put the angle back to 0 - which, since nothing is applied, is
    /// only ever putting the HANDLE back on the horizontal.</summary>
    public void ResetPageRotation()
    {
        SetPageRotation(0);
        _canvas.Invalidate();
    }

    private void BeginRotateGesture(Vector2 pos)
    {
        // The two drawn objects first, and in this order: the handle sits on the
        // line at the drag radius and the pivot sits at its foot, so at the
        // minimum radius their reaches nearly touch and the one the user is
        // more likely to be aiming at has to win.
        _rotateGrab = RotateGrab.None;
        if (Vector2.Distance(pos, RotateHandleWorld()) * ViewZoom <= RotateHandleReachPx)
        {
            _rotateGrab = RotateGrab.Handle;
            _rotating = false;
            return;
        }
        if (Vector2.Distance(pos, RotatePivotWorld) * ViewZoom <= RotatePivotReachPx)
        {
            _rotateGrab = RotateGrab.Pivot;
            // Freeze where it is NOW before the drag moves it: an unplaced pivot
            // is derived from the view, and reading it again mid-drag after
            // marking it placed would read the field it has not been given yet.
            _rotatePivot = RotatePivotWorld;
            _rotatePivotPlaced = true;
            _rotating = false;
            return;
        }

        // A press anywhere else is the selection sweep - free-angle since 17.11a
        // requirement 2, and about the selection's own centre.
        var b = SubjectBoundsWorld;
        if (b.IsEmpty) { _rotating = false; return; }
        if (AnyLocked) { _rotating = false; return; }   // 16.2's padlock, as every other transform honours it
        var (s, h, t) = SelectionParts();
        if (s.Count + h.Count + t.Count == 0) { _rotating = false; return; }
        t = RotatableTexts(t);
        // The typed text has to be in the model before it is turned, or a box
        // still holding unflushed keystrokes rotates and then reverts them.
        FlushTexts();
        _rotateInk = s;
        _rotateShapes = h;
        // Measure every box ONCE, here. A text box's height is its wrapped
        // content's, and the drag rebuilds the text layer on every frame - so a
        // size read per frame would be read back off a container that this same
        // gesture had just re-laid-out, and the box would creep.
        _rotateTexts = t.Select(x =>
        {
            var (w, hh) = TextBoxSizeWorld(x);
            return new RotateFreeMixedAction.SizedText(x, w, hh);
        }).ToList();
        _rotating = true;
        _rotateTurnedDeg = 0;
        _rotateCentre = new Vector2((float)(b.Left + b.Width / 2), (float)(b.Top + b.Height / 2));
        _rotateFromDeg = Bearing(pos, _rotateCentre);
    }

    /// <summary>Turns the captured subject by a DELTA, in place and without
    /// touching the undo stack. The sweep's live feedback and the un-apply that
    /// precedes the commit are the same operation in opposite directions, so
    /// they are the same code.</summary>
    private void SpinRotateSubject(double deltaDeg)
    {
        if (_page == null || Math.Abs(deltaDeg) < 1e-9) return;
        new RotateFreeMixedAction(_rotateInk, _rotateShapes, _rotateTexts,
                                  _rotateCentre.X, _rotateCentre.Y, deltaDeg).Do(_page);
        _inkCacheDirty = true;
        _gridDirty = true;
    }

    /// <summary>Degrees clockwise from the +x axis, in the canvas's y-down
    /// frame, so a growing angle is a clockwise sweep on screen.</summary>
    private static double Bearing(Vector2 p, Vector2 centre) =>
        Math.Atan2(p.Y - centre.Y, p.X - centre.X) * 180.0 / Math.PI;

    /// <summary>The handle drag: 17.11a's free page rotation, and the only
    /// gesture in this file that moves <see cref="PageRotationDeg"/>.</summary>
    private void RotateHandleDragTo(Vector2 pos)
    {
        var pivot = RotatePivotWorld;
        float distPx = Vector2.Distance(pos, pivot) * ViewZoom;
        // A pointer sitting on the pivot has no bearing to read. The angle is
        // left where it was rather than taking whatever atan2 returns for a
        // vector that is almost entirely rounding error.
        if (distPx < 1f) return;
        _rotateRadiusPx = Math.Max(RotateRadiusMinPx, distPx);
        SetPageRotation(Bearing(pos, pivot));
    }

    private void RotateDragTo(Vector2 pos)
    {
        switch (_rotateGrab)
        {
            case RotateGrab.Handle: RotateHandleDragTo(pos); return;
            // The pivot follows the pointer exactly. No offset from where it was
            // grabbed: the crosshair is a point, and a point picked up 6 pixels
            // off centre that then trails the finger by 6 pixels is a point that
            // cannot be put anywhere precisely.
            case RotateGrab.Pivot: _rotatePivot = pos; return;
        }
        if (!_rotating) return;
        // Wrapped into (-180, 180] so a sweep across the -x axis reads as a small
        // step rather than a 350 degree jump back the other way. The bearing is
        // re-based every frame, so a sweep can pass any number of full turns and
        // the total accumulates instead of wrapping.
        double d = WrapDegrees(Bearing(pos, _rotateCentre) - _rotateFromDeg);
        if (Math.Abs(d) < 1e-6) return;
        _rotateFromDeg = WrapDegrees(_rotateFromDeg + d);
        // 17.11a: "the user said free, so any snap must be opt-in". Off, the
        // subject sits at whatever angle the hand described. On, it is the same
        // opt-in and the same 15 degree step the page half uses - the snap is a
        // property of the TOOL, so turning it on cannot mean two different
        // things depending on which half of the tool is being dragged.
        double target = _rotateTurnedDeg + d;
        if (RotateSnap) target = Math.Round(target / RotateSnapStepDeg) * RotateSnapStepDeg;
        SpinRotateSubject(target - _rotateTurnedDeg);
        _rotateTurnedDeg = target;
        RecomputeSelectionBoundsFrozen();
    }

    /// <summary>Re-measures the selection marquee mid-sweep WITHOUT moving the
    /// centre the sweep is turning about.
    ///
    /// <para>The bounds of a turning selection change on every frame, so
    /// recomputing the centre from them would walk the pivot away under the
    /// user's hand - the drawing would drift across the page while the hand went
    /// in a circle. <see cref="_rotateCentre"/> is therefore fixed at press and
    /// only the drawn marquee follows.</para></summary>
    private void RecomputeSelectionBoundsFrozen()
    {
        RecomputeSelectionBoundsCore();
        SubjectMoved?.Invoke();
    }

    /// <summary>16.2's padlock. Locking any part of a mixed selection locks all
    /// of it; unlocking restores what each element had, so a stroke that was
    /// already locked before it was lassoed with others stays locked.</summary>
    public void ToggleSelectionLock()
    {
        if (_page == null) return;
        var (s, h, t) = SelectionParts();
        if (s.Count + h.Count + t.Count == 0) return;
        PushAction(new LockMixedAction(s, h, t, !AnyLocked), _page);
        PublishSelection();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    private void AfterSelectionTransform(bool touchedText)
    {
        _inkCacheDirty = true;
        RecomputeSelectionBounds();
        if (touchedText) RebuildTextLayer();
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    // =======================================================================
    // Copy / paste (canvas objects)
    // =======================================================================
    public bool HasCanvasSelection => HasMultiSelection || _activeShape != null;
    public static bool HasCanvasClipboard =>
        _clipStrokes is { Count: > 0 } || _clipShapes is { Count: > 0 } || _clipTexts is { Count: > 0 };

    private static PenStroke CloneStroke(PenStroke s) =>
        s.CloneWithPoints(s.Points.Select(p => new StrokePoint(p.X, p.Y, p.Pressure)).ToList());

    // The whole element but its Id, table geometry and styling included, so a
    // pasted table keeps its grid rather than collapsing to an empty 0x0 one -
    // and, since the copy goes through the model's own clone, so do the pen, the
    // opacity, the padlock, the LAYER, the equation source and the axis labels
    // that this list used to leave behind.
    private static ShapeElement CloneShape(ShapeElement s) => s.Clone();

    /// <summary>The clipboard's text copy, DETACHED from its table.
    ///
    /// <para>Everything the box is made of comes along - the words, the width, the
    /// angle, the padlock, the layer, its own fill and border. The one thing that
    /// does not is its cell membership, and that is a decision rather than an
    /// omission: the shape copy beside it is a NEW table with a NEW id, nothing
    /// re-links cells to it, and a bubble that went on naming the table it was
    /// copied FROM would be dragged back into that table's grid by its next
    /// reflow - out of the paste, on top of the cell it came from. Pasting a cell
    /// as a free box is the honest outcome of what the clipboard can carry.</para>
    ///
    /// <para>A copy that keeps its cell identity needs the table copied WITH it and
    /// re-linked, which is <see cref="ElementClone.Duplicate"/>'s job.</para></summary>
    private static TextElement CloneText(TextElement t) => t.CloneAsFreeBox();

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
                // A single-selected table draws only its grid above; its cell text
                // lives in separate TextElements Win2D never sees, so rasterise those
                // too or "copy as image" silently drops every cell's words.
                if (_activeShape.Kind == ShapeKind.Table && _page != null)
                    foreach (var t in _page.Texts)
                        if (t.TableId == _activeShape.Id) DrawTextElement(ds, t);
            }
            else
            {
                foreach (var sh in _selShapes) DrawShape(ds, sh);
                foreach (var s in _selected) DrawStroke(ds, _canvas, s, System.Numerics.Vector2.Zero, null);
                // Text boxes and table cells are XAML RichEditBox overlays the
                // drawing session can't reach; draw them last (on top of shapes)
                // so a copied selection keeps its text. _selTexts already holds
                // both free boxes and any lassoed table cells.
                foreach (var t in _selTexts) DrawTextElement(ds, t);
            }
        }
        var pixels = rt.GetPixelBytes();
        return (pixels, width, height);
    }

    /// <summary>Rasterises a text element (free text box or table cell) into the
    /// drawing session so "copy as image" captures its words. The live text is a
    /// XAML RichEditBox overlay Win2D can't render, so this draws the plain text
    /// pulled from the RTF — mirroring the vector PDF exporter's RtfToPlainText
    /// path — honouring position, wrap width, font size and rotation.</summary>
    private void DrawTextElement(CanvasDrawingSession ds, TextElement t)
    {
        var plain = RtfToPlainText(t.Rtf, out float size, out string font);
        if (string.IsNullOrWhiteSpace(plain)) return;

        // 25.4: the box's OWN colour when it has one, and the page's ink
        // convention when it does not - one expression, shared with the editor,
        // the veil and both exporters. Per-run colour still does not survive
        // RtfToPlainText; 25.3 states that limit rather than pretending it away.
        var ink = TextInkFor(t);

        using var format = new CanvasTextFormat
        {
            FontFamily = font,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.Wrap
        };
        using var layout = new CanvasTextLayout(ds, plain, format, (float)Math.Max(60, t.Width), 0f);

        Matrix3x2 prevT = ds.Transform;
        bool rot = Math.Abs(t.Rotation) > 0.01;
        if (rot)
        {
            // The overlay rotates about its container centre (RenderTransformOrigin
            // 0.5,0.5); mirror that so rotated text lands where the user sees it.
            // One helper for the centre, shared with the selection bounds, the
            // click probe and the free sweep - four sites that have to agree on
            // where a box's middle is or the drawn text and the marquee around it
            // turn about different points.
            //
            // Composed onto prevT, never assigned over it: CanvasVirtualControl
            // hands this session a transform that has already placed the tile.
            var center = TextCentreWorld(t);
            ds.Transform = Matrix3x2.CreateRotation((float)(t.Rotation * Math.PI / 180.0), center) * prevT;
        }

        // +4 inset and +16 below the fixed 16px grip bar match where the RichEditBox
        // renders its text inside the container (same offsets as the PDF exporter).
        // 16.7's third intercept, for the Win2D text path (table cells, capture).
        ds.DrawTextLayout(layout, (float)t.X + 4, (float)t.Y + 16, Veil(ink, IsSubject(t)));

        if (rot) ds.Transform = prevT;
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

        PushAction(new AddMixedAction(ns, nsh, nt), _page);

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
        PushAction(new AddShapeAction(s), _page);
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
    /// <summary>
    /// CONCEPTS-REF 14.5. Captures this page's REFERENCE FRAME - the viewport as
    /// it stands on the page's FIRST FRAME - if it has not got one yet.
    ///
    /// <para>Called from the paint handler because "the first frame" is exactly
    /// what that is, and because it is the first moment the surface is certain to
    /// have been laid out: <c>LoadPage</c> can run before the control has a size
    /// (app start, or a page switch made while the window is being restored), and
    /// a frame captured at zero size would be worse than none.</para>
    ///
    /// <para>Runs once per page and then costs a null test per paint. It writes
    /// to the page, so it raises <see cref="RefFrameCaptured"/> and the host
    /// schedules a save - a frame that is not persisted would be derived again
    /// from a different view next time the page is opened, which is the drifting
    /// grid 14.5 asks to be designed out.</para>
    /// </summary>
    public bool EnsureRefFrame()
    {
        if (_page == null || _page.RefFrame is { IsUsable: true }) return false;
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return false;
        float z = MathF.Max(0.01f, ViewZoom);
        _page.RefFrame = new ViewFrame(-ViewOffset.X / z, -ViewOffset.Y / z, w / z, h / z);
        RefFrameCaptured?.Invoke();
        return true;
    }

    /// <summary>§27: consecutive <see cref="OnRegionsInvalidated"/> passes in
    /// which at least one region threw. Zeroed by the first clean pass.</summary>
    private int _renderFailStreak;

    /// <summary>§27: how many times a failing canvas may answer itself with a
    /// full invalidate before it stops trying.
    ///
    /// <para>The self-heal is worth having - a dropped region is BLANK content
    /// and a repaint usually fixes a transient - but it is a repaint scheduled
    /// BY the failure, so a persistent fault makes it a feedback loop. It has
    /// already run in the field: <c>crash.log</c> holds 472 of these across five
    /// bursts in 29 seconds, ~120 to a burst, which is one burst per pass with
    /// every region failing and every region enqueuing its own full
    /// invalidate.</para>
    ///
    /// <para>Eight is enough for a transient and short enough that a real fault
    /// stops costing frames. Backing off does NOT stop the logging - a dropped
    /// region that goes quiet is strictly worse than one that shouts - and any
    /// later clean pass rearms it, as does any pan, zoom, resize or edit, since
    /// those invalidate on their own account.</para></summary>
    private const int RenderHealAttempts = 8;

    /// <summary>§27: what actually failed, in the terms the failure carries it.
    ///
    /// <para><c>Message</c> alone produced 472 EMPTY log lines. A Win2D device
    /// loss is the obvious suspect for a whole viewport of regions failing at
    /// once, and it arrives as a <c>COMException</c> whose detail is the
    /// HRESULT - <c>0x887A0005 DXGI_ERROR_DEVICE_REMOVED</c>,
    /// <c>0x887A0006 _HUNG</c>, <c>0x887A0007 _RESET</c> - with the message
    /// empty or a generic localisation. So the type and the HRESULT are logged
    /// whatever the exception is, and Win2D is asked directly whether that
    /// HRESULT is a device loss.</para>
    ///
    /// <para>It only REPORTS the device-loss verdict; it does not act on it.
    /// Recovery is already wired, at the <c>CreateResources</c> handler in the
    /// constructor, which is where Win2D delivers a replacement device.</para></summary>
    private string DescribeRenderFailure(Exception ex)
    {
        string message = string.IsNullOrWhiteSpace(ex.Message) ? "(no message)" : ex.Message.Trim();
        string lost = "";
        try
        {
            if (_canvas.Device is { } dev && dev.IsDeviceLost(ex.HResult)) lost = " DEVICE-LOST";
        }
        catch { }
        return $"{ex.GetType().Name} hresult=0x{ex.HResult:X8}{lost}: {message}";
    }

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        EnsureRefFrame();
        float blur = CurrentBlurRadius();
        // §27: counted per PASS, not per region. Every region used to log its own
        // line and enqueue its own full-canvas invalidate, so one bad frame wrote
        // ~120 identical lines and asked for ~120 identical repaints. One line and
        // one repaint say the same thing.
        int failed = 0, total = 0;
        Exception? first = null;
        foreach (var region in args.InvalidatedRegions)
        {
            total++;
            try
            {
                using var ds = sender.CreateDrawingSession(region);
                if (blur > 0f) DrawRegionBlurred(sender, ds, region, blur);
                else DrawRegion(ds, region);
            }
            catch (Exception ex)
            {
                // A silently dropped region shows BLANK content — the "invisible
                // ink" failure class (#inkfix3). Nothing here swallows it.
                failed++;
                first ??= ex;
            }
        }

        if (failed == 0) { _renderFailStreak = 0; return; }

        _renderFailStreak++;
        bool heal = _renderFailStreak <= RenderHealAttempts;
        // ALWAYS logged, including after the self-heal has given up - the point
        // of this log is that the failure class is invisible on screen.
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(LibraryStore.Dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] render region failed: {failed}/{total} regions, " +
                $"pass {_renderFailStreak}, {(heal ? "repainting" : $"NOT repainting (over {RenderHealAttempts})")}; " +
                DescribeRenderFailure(first!) + Environment.NewLine);
        }
        catch { }
        if (heal)
            try { DispatcherQueue.TryEnqueue(() => _canvas.Invalidate()); } catch { }
    }

    // ---- motion blur (#A5) ------------------------------------------------

    // Feed one view-change sample into the velocity estimate. Large jumps
    // (page switch, fit-to-view, restore) are teleports, not motion — reset
    // rather than flash a blur.
    private void TrackViewVelocity()
    {
        if (!MotionBlur) return;
        long now = Environment.TickCount64;
        float dist = (ViewOffset - _blurPrevOffset).Length() +
                     MathF.Abs(ViewZoom - _blurPrevZoom) * 800f;   // zoom as edge travel
        float dt = Math.Clamp(now - _blurPrevMs, 1, 100) / 1000f;
        _blurPrevOffset = ViewOffset;
        _blurPrevZoom = ViewZoom;
        _blurPrevMs = now;
        if (dist > 400f) { _blurVelocity = 0f; return; }
        _blurVelocity = _blurVelocity * 0.6f + (dist / dt) * 0.4f; // low-pass the spikes
        if (_blurVelocity > BlurVelocityMin) _blurDecayTimer.Start();
    }

    private float CurrentBlurRadius()
    {
        // never while a pen/tool gesture is live — the wet stroke must stay crisp
        if (!MotionBlur || _gestureTool != null || _wet != null || _blurVelocity <= BlurVelocityMin)
            return 0f;
        return MathF.Min((_blurVelocity - BlurVelocityMin) / 900f, 5f);
    }

    // Renders the region into an intermediate target, then composites it through
    // a GaussianBlurEffect. Only runs while blurred, so the steady-state draw
    // path pays nothing.
    private void DrawRegionBlurred(CanvasVirtualControl sender, CanvasDrawingSession ds, Rect region, float radius)
    {
        float w = (float)region.Width, h = (float)region.Height;
        if (w < 1f || h < 1f) { DrawRegion(ds, region); return; }
        if (_blurScratch == null || _blurScratch.Dpi != sender.Dpi ||
            Math.Abs(_blurScratch.Size.Width - w) > 0.5 || Math.Abs(_blurScratch.Size.Height - h) > 0.5)
        {
            _blurScratch?.Dispose();
            _blurScratch = new CanvasRenderTarget(sender, w, h, sender.Dpi);
        }
        using (var rds = _blurScratch.CreateDrawingSession())
        {
            // mimic the CVC's pre-translated tile session so DrawRegion composes
            // its world transform exactly as in the direct path
            rds.Transform = Matrix3x2.CreateTranslation(-(float)region.X, -(float)region.Y);
            DrawRegion(rds, region);
        }
        using var blur = new GaussianBlurEffect
        {
            Source = _blurScratch,
            BlurAmount = radius,
            BorderMode = EffectBorderMode.Hard,   // no transparent halo at the tile edges
            Optimization = EffectOptimization.Speed,
        };
        ds.DrawImage(blur, (float)region.X, (float)region.Y);
    }

    private void DrawRegion(CanvasDrawingSession ds, Rect region)
    {
        var sender = _canvas;   // resource creator for the draw helpers below
        // CVC hands each tile a session PRE-TRANSLATED so control-space
        // coordinates land inside the tile. Overwriting that transform painted
        // every non-origin tile displaced and clipped — strokes drew in the
        // wrong place and looked invisible (#inkfix2). Compose, don't clobber.
        var regionT = ds.Transform;
        if (_page == null)
        {
            ds.Clear(Colors.Transparent);
            return;
        }

        // §7.3: the page's EFFECTIVE ground — a paper texture with a ground of its
        // own (Blueprint / Darkprint / Brown) owns the colour; everything else,
        // including a null Paper (i.e. every existing page), keeps the page's own
        // plain colour. This is the same colour the theme derivation reads.
        var bg = PaperTextures.Ground(_page.Paper, ColorUtil.Parse(_page.Background));
        // "Include Background" off exports onto transparency rather than onto a
        // white lie: the frame is cleared to nothing and the paper is skipped.
        ds.Clear(ExportOmitBackground ? Colors.Transparent : bg);

        ds.Transform = Matrix3x2.CreateScale(ViewZoom) *
                       Matrix3x2.CreateTranslation(ViewOffset.X, ViewOffset.Y) *
                       regionT;

        if (!ExportOmitBackground) DrawPaper(ds, region, bg);
        if (!ExportOmitGrid) DrawGrid(ds, bg);
        DrawPerspective(ds, bg);
        DrawArtboard(ds, bg);
        DrawPageTitle(ds, bg);

        // World rectangle of THIS REGION — anything fully outside it is skipped,
        // so a small invalidation only pays for the strokes it actually touches.
        var vTL = ToWorld(new Vector2((float)region.X, (float)region.Y));
        var vBR = ToWorld(new Vector2((float)region.Right, (float)region.Bottom));
        float visMinX = vTL.X, visMinY = vTL.Y, visMaxX = vBR.X, visMaxY = vBR.Y;

        foreach (var sh in _page.Shapes)
        {
            var sb = ShapeBounds(sh);
            if (sb.Right < visMinX - 8 || sb.Left > visMaxX + 8 ||
                sb.Bottom < visMinY - 8 || sb.Top > visMaxY + 8) continue;
            bool veilShape = !IsSubject(sh);
            if (_movingSel && _selShapeSet.Contains(sh))
            {
                var prev = ds.Transform;
                ds.Transform = Matrix3x2.CreateTranslation(_moveDx, _moveDy) * prev;
                DrawShape(ds, sh, veilShape);
                ds.Transform = prev;
            }
            else
            {
                DrawShape(ds, sh, veilShape);
            }
        }

        // Big pages draw settled ink from the offscreen cache (#43); anything
        // that offsets strokes (replay, selection move, free space) falls back
        // to the classic per-stroke path so offsets stay live.
        // 16.7: the cache holds RENDERED pixels, so it cannot follow a fade that
        // moves every frame. It stands down for the duration rather than being
        // rebuilt 190 ms in a row, which is what marking it dirty per frame would
        // cost on exactly the pages (2500+ strokes) that need it most.
        bool cacheEligible = !_replaying && !_movingSel && !_spacing && !Veiling &&
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
                        DrawStroke(ds, sender, s, off, null, veil: !IsSubject(s));
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
                Opacity = PenOpacity >= 0.999f ? (float?)null : PenOpacity,
                Points = RulerMode ? BuildRulerPoints(_wetStart, _wetEnd) : (_wet ?? new List<StrokePoint>()),
                PressureCurve = EffectivePressureCurve()
            };
            // 16.7, THE MID-STROKE CASE. Ink under the nib is not yet part of
            // the page, so it never fades - a stroke begun while an attachment
            // is selected stays in the pen's own colour under the tip, and joins
            // the faded page only once it is committed and the selection that
            // caused the fade is still standing.
            DrawStroke(ds, sender, temp, Vector2.Zero, null, veil: false);
        }

        var accent = Accent;   // follows the app accent (#6-batch3)
        float uiScale = 1.5f / ViewZoom;

        if (_shapeAdjust && _adjustShape != null)
        {
            DrawShape(ds, _adjustShape);
            var bb = ShapeBounds(_adjustShape);
            ds.DrawRectangle(bb, accent, uiScale, _dashStyle);
        }

        if (_activeShape != null && !_replaying && !ExportChromeless)
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
            ds.FillRectangle(r, Color.FromArgb(18, Accent.R, Accent.G, Accent.B));
            ds.DrawRectangle(r, accent, uiScale, _dashStyle);
        }

        // 17.8: THE SELECTION TINT IS GONE, and nothing replaces it here.
        //
        // A committed selection used to be drawn on this canvas as a 26-alpha
        // accent wash, a dashed rectangle on its bounds, and four white corner
        // squares. All three are removed: "only the edges remain - the corner
        // circles and the full-canvas guides of 16.2. No tinted rectangle, no
        // dashed box." This was the suppression offered when the selection
        // chrome landed and deferred until the user had seen it.
        //
        // The marks that remain are SelectionChrome's, which is XAML over this
        // canvas rather than Win2D in it: four hollow Ellipses on the same four
        // corners and four full-canvas guide lines projected from the same
        // bounds (SubjectBoundsWorld reads _selBounds for a multi-selection, so
        // the two framed exactly the same rectangle - the squares were a second
        // set of corner marks sitting on top of the circles that 16.2 replaced).
        //
        // Only the DRAWING went. #54's scale drag is untouched: TryBeginScale
        // still hit-tests SelCorners(), and those corners are still marked -
        // by a circle now instead of a square. The RUBBER BANDS above are also
        // untouched, and deliberately so: the lasso and the in-flight rectangle
        // are a gesture in progress, not a selection, and 17.8 is about what a
        // settled selection looks like.
        //
        // AND THE SAME NOW GOES FOR THE SINGLE ACTIVE SHAPE, which this comment
        // used to speak for without covering. DrawShapeSelection above kept its
        // dashed box and its white squares long after this paragraph was
        // written, so an attachment wore both chromes at once and a visual pass
        // saw exactly that. The box is gone from there too, and the squares are
        // gone wherever the chrome's circles already mark the same four points;
        // DrawShapeSelection carries the reasoning and the one case that keeps
        // them, and it is the same reasoning as the paragraph above - a mark is
        // removed when something else already makes it, never when it is the
        // only sign that a grip is there.

        if (!_replaying) DrawRuler(ds, bg);

        // 17.11a's rotate interface. Chrome, so it is gated exactly as the shape
        // selection above is - out of a replay, out of a chromeless export - and
        // drawn after the ink so the assembly sits over the page it turns.
        if (Tool == ToolType.Rotate && !_replaying && !ExportChromeless)
            DrawRotateInterface(ds, sender);

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

        // Comment pins: a numbered accent dot per comment, greyed when resolved.
        if (_page != null && _page.Comments.Count > 0 && (CommentMode || ShowCommentsAlways))
        {
            float pr = 11f / ViewZoom;
            for (int i = 0; i < _page.Comments.Count; i++)
            {
                var c = _page.Comments[i];
                if (c.Resolved && !ShowResolvedComments) continue;
                var ctr = new Vector2((float)c.X, (float)c.Y);
                var baseCol = c.Resolved ? Color.FromArgb(255, 150, 150, 150) : Accent;
                byte a = c.Resolved ? (byte)165 : (byte)255;
                ds.FillCircle(ctr, pr, Color.FromArgb(a, baseCol.R, baseCol.G, baseCol.B));
                ds.DrawCircle(ctr, pr, Color.FromArgb(a, 255, 255, 255), Math.Max(1f, 1.4f / ViewZoom));
                using var cf = new CanvasTextFormat
                {
                    FontSize = 12.5f / ViewZoom,
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
                };
                ds.DrawText((i + 1).ToString(), new Rect(ctr.X - pr, ctr.Y - pr, pr * 2, pr * 2), Colors.White, cf);
            }
        }

        // Undo / redo flash highlight over the affected element (#roadmap).
        if (_flashRect is { } fr)
        {
            double ft = (Environment.TickCount64 - _flashStartMs) / 500.0;
            if (ft >= 1) _flashRect = null;
            else
            {
                float pad = 5f / ViewZoom;
                var rr = new Rect(fr.X - pad, fr.Y - pad, fr.Width + 2 * pad, fr.Height + 2 * pad);
                ds.FillRectangle(rr, Color.FromArgb((byte)(70 * (1 - ft)), Accent.R, Accent.G, Accent.B));
                ds.DrawRectangle(rr, Color.FromArgb((byte)(210 * (1 - ft)), Accent.R, Accent.G, Accent.B),
                                 Math.Max(1.4f, 1.6f / ViewZoom));
                _canvas.Invalidate();
            }
        }
    }

    // Procedural paper texture (§7.3). Called with the session ALREADY composed
    // into world space by DrawRegion — this method only READS ds.Transform's
    // effect, it never assigns to it, so the CVC's per-tile pre-translation
    // survives (overwriting it is what made ink vanish in #inkfix2).
    //
    // The fill rectangle is the visible world rect and the brush wraps every
    // PaperTextures.TileSize WORLD units, so the texture is glued to the page:
    // it pans and zooms with the drawing instead of swimming across it.
    private void DrawPaper(CanvasDrawingSession ds, Rect region, Color ground)
    {
        var paper = _page?.Paper;
        if (string.IsNullOrEmpty(paper)) return;   // plain colour: byte-for-byte today's path
        // Zoomed far enough out that the grain is sub-pixel: it would alias into
        // noise, and the flat ground already reads correctly on its own. The tile
        // is 512 world units, so this cuts in when it covers ~90 screen pixels.
        if (ViewZoom < 0.18f) return;
        var brush = PaperTextures.Brush(_canvas, paper, ground);
        if (brush == null) return;
        var tl = ToWorld(new Vector2((float)region.X, (float)region.Y));
        var br = ToWorld(new Vector2((float)region.Right, (float)region.Bottom));
        float w = br.X - tl.X, h = br.Y - tl.Y;
        if (w <= 0 || h <= 0) return;
        ds.FillRectangle(new Rect(tl.X, tl.Y, w, h), brush);
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
        if (!string.IsNullOrEmpty(_page.GridColor))
        {
            // custom colour keeps the subtle automatic alpha so the grid stays unobtrusive
            try { var c = ColorUtil.Parse(_page.GridColor); gridColor = Color.FromArgb(gridColor.A, c.R, c.G, c.B); }
            catch { }
        }
        // Grid opacity (UI-SPEC-V3 C) is a MULTIPLIER over the automatic alpha
        // above, never a replacement for it: 1 is exactly the grid this build
        // has always drawn, and the setting can only ever fade it out.
        double gridOpacity = Math.Clamp(_page.GridOpacity, 0, 1);
        if (gridOpacity < 1)
            gridColor = Color.FromArgb((byte)Math.Round(gridColor.A * gridOpacity),
                                       gridColor.R, gridColor.G, gridColor.B);
        if (gridColor.A == 0) return;
        // CONCEPTS-REF 12.2's "1 pts" box. Divided by the zoom so the weight is a
        // SCREEN weight: a 1 pt guide stays a hairline at 800%, which is what a
        // construction line is for.
        float lw = (float)Math.Clamp(_page.GridWeight > 0 ? _page.GridWeight : 1, 0.25, 8) / ViewZoom;

        // 12.2: "Only show the grid lines inside the artboard." Nothing to
        // confine on an infinite page, so the flag is simply inert there.
        var confine = _page.GridConfine && PageSizes.TryResolve(_page, out double aw, out double ah)
                      && aw > 0 && ah > 0
            ? new Rect(0, 0, aw, ah)
            : (Rect?)null;
        if (confine is Rect box)
        {
            tl = new Vector2(MathF.Max(tl.X, (float)box.X), MathF.Max(tl.Y, (float)box.Y));
            br = new Vector2(MathF.Min(br.X, (float)box.Right), MathF.Min(br.Y, (float)box.Bottom));
            if (br.X <= tl.X || br.Y <= tl.Y) return;
            startX = MathF.Floor(tl.X / spacing) * spacing;
            startY = MathF.Floor(tl.Y / spacing) * spacing;
        }

        // 12.2's Orientation circles. Read ONLY by the kinds 12.3 gives the
        // control to - a square grid and a dot grid are unchanged by a 90 degree
        // turn, so the flag can never reach them.
        bool portrait = _page.GridPortrait &&
                        _page.Grid is GridType.Lines or GridType.Isometric or GridType.Triangle;

        // 12.3's Divisions: minor lines between the main ones, in a fainter ink
        // so the main lines still read as the main lines.
        int div = Math.Clamp(_page.GridDivisions > 0 ? _page.GridDivisions : 1, 1, 64);
        var minorColor = Color.FromArgb((byte)Math.Round(gridColor.A * 0.45),
                                        gridColor.R, gridColor.G, gridColor.B);
        float minorStep = spacing / div;

        switch (_page.Grid)
        {
            case GridType.Dotted:
                for (float y = startY; y < br.Y; y += spacing)
                    for (float x = startX; x < br.X; x += spacing)
                        ds.FillCircle(new Vector2(x, y), MathF.Max(1.0f, lw * 1.4f * ViewZoom) / ViewZoom, gridColor);
                break;
            case GridType.Square:
                if (div > 1 && minorStep * ViewZoom > 3f)
                {
                    for (float x = startX; x < br.X; x += minorStep)
                        ds.DrawLine(new Vector2(x, tl.Y), new Vector2(x, br.Y), minorColor, lw * 0.75f);
                    for (float y = startY; y < br.Y; y += minorStep)
                        ds.DrawLine(new Vector2(tl.X, y), new Vector2(br.X, y), minorColor, lw * 0.75f);
                }
                for (float x = startX; x < br.X; x += spacing)
                    ds.DrawLine(new Vector2(x, tl.Y), new Vector2(x, br.Y), gridColor, lw);
                for (float y = startY; y < br.Y; y += spacing)
                    ds.DrawLine(new Vector2(tl.X, y), new Vector2(br.X, y), gridColor, lw);
                break;
            case GridType.Lines:
                if (portrait)
                    for (float x = startX; x < br.X; x += spacing)
                        ds.DrawLine(new Vector2(x, tl.Y), new Vector2(x, br.Y), gridColor, lw);
                else
                    for (float y = startY; y < br.Y; y += spacing)
                        ds.DrawLine(new Vector2(tl.X, y), new Vector2(br.X, y), gridColor, lw);
                break;
            // CONCEPTS-REF 12.11: the angle is the inclination of the DIAGONAL
            // families and nothing else. Isometric's verticals and the triangle
            // grid's horizontals stay exactly where they are at every value, so
            // moving it restretches each cell rather than turning the whole
            // field - which is what the user reported, and what Orientation
            // (still a rigid 90 degrees, still independent) already does.
            case GridType.Isometric:
            case GridType.Triangle:
                {
                    var kind = _page.Grid == GridType.Triangle ? GridKind.Triangle
                                                               : GridKind.Isometric;
                    // Zero means unset, and unset means the kind's OWN default -
                    // 30 for isometric, 60 for the equilateral triangle case.
                    double angle = _page.GridAngle > 0 ? _page.GridAngle
                                                       : GridSpec.DefaultAngle(kind);
                    // The guard above counted cells at the straight family's
                    // spacing; the diagonals can be several times finer, so the
                    // zoomed-out count is re-taken against the finest of them.
                    float sp = spacing;
                    float fine = (float)GridLattice.FinestPerp(kind, angle, sp);
                    while (fine >= 0.01f &&
                           (br.X - tl.X) / fine * ((br.Y - tl.Y) / fine) > 25000)
                    { sp *= 2; fine *= 2; }

                    foreach (var fam in GridLattice.Families(kind, angle, sp, portrait))
                        foreach (var ln in GridLattice.Lines(fam, tl, br))
                            ds.DrawLine(ln.A, ln.B, gridColor, lw);
                }
                break;
        }
    }

    // One parallel family of construction lines: direction angleDeg, adjacent
    // lines perpDist apart, clipped to the visible world rect. Generated in
    // world space so the view transform keeps them stable under pan and zoom,
    // and generated by GridLattice so the canvas and the editor's preview strip
    // are running the same arithmetic rather than two copies of it.
    private static void DrawLineFamily(CanvasDrawingSession ds, Vector2 tl, Vector2 br,
                                       float angleDeg, float perpDist, Color color, float lw)
    {
        foreach (var ln in GridLattice.Lines(new GridFamily(angleDeg, perpDist), tl, br))
            ds.DrawLine(ln.A, ln.B, color, lw);
    }

    // Perspective construction overlay (CONCEPTS-DIRECTION 7.4). Not a GridType:
    // it coexists with any grid and draws when the page carries a PerspectiveDef.
    // Guides are on-screen construction aids and deliberately do not export.
    private void DrawPerspective(CanvasDrawingSession ds, Color bg)
    {
        var def = _page?.Perspective;
        if (def == null || def.Vps.Count == 0) return;

        var tl = ToWorld(new Vector2(0, 0));
        var br = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
        // CONCEPTS-REF 12.5: the guides must read as a fine, low-contrast wash of
        // BLUE-GREY, never as heavy black lines. The bias is applied to the
        // contrast ink rather than to a named slate, so a brown page's guides
        // stay a cool brown instead of every page getting the same grey.
        bool darkBg = ColorUtil.IsDark(bg);
        var col = darkBg
            ? Color.FromArgb(56, 226, 241, 255)
            : Color.FromArgb(40, 0, 16, 38);
        if (!string.IsNullOrEmpty(_page!.GridColor))
        {
            try { var c = ColorUtil.Parse(_page.GridColor!); col = Color.FromArgb(col.A, c.R, c.G, c.B); }
            catch { }
        }
        double pOpacity = Math.Clamp(_page.GridOpacity, 0, 1);
        if (pOpacity < 1)
            col = Color.FromArgb((byte)Math.Round(col.A * pOpacity), col.R, col.G, col.B);
        if (col.A == 0) return;
        float lw = (float)Math.Clamp(_page.GridWeight > 0 ? _page.GridWeight : 1, 0.25, 8) / ViewZoom;
        float spacing = (float)Math.Max(8, _page!.GridSpacing);
        int vpCount = Math.Min(def.Vps.Count, 3);

        // CONCEPTS-REF 12.8: the horizon TILTS. It is not a fixed horizontal -
        // it is the LINE THROUGH the two on-horizon points, so dragging either
        // one rotates it and the whole grid re-solves under the new geometry.
        // 12.9: the angle is STORED, not derived. The points are constrained to
        // this line and slide along it; only the rotation grip on the cone
        // circle's rim turns it.
        float tiltDeg = (float)def.HorizonAngle;

        // The horizon, drawn through the points rather than at a stored height.
        // A 3-point set has nothing straight left, so it keeps none.
        if (vpCount < 3)
        {
            var a = vpCount >= 2
                ? new Vector2((float)def.Vps[0].X, (float)def.Vps[0].Y)
                : new Vector2(tl.X, (float)def.HorizonY);
            float slope = MathF.Tan(tiltDeg * MathF.PI / 180f);
            var p0 = new Vector2(tl.X, a.Y + (tl.X - a.X) * slope);
            var p1 = new Vector2(br.X, a.Y + (br.X - a.X) * slope);
            ds.DrawLine(p0, p1, col, lw * 1.6f);
        }

        // 14.4: a 1-POINT GRID CARRIES NO LATTICE. It is a fan from its single
        // vanishing point plus the horizon, and nothing else - the square grid
        // that used to be laid over it is not in the reference and was reading as
        // a graph page with a fan on top. 2-point keeps its verticals, which are
        // a true family of that configuration rather than an overlay.
        if (vpCount == 2)
        {
            // The verticals stay perpendicular to the horizon, so they turn with
            // it - that IS the grid re-solving under the tilt.
            DrawLineFamily(ds, tl, br, 90f + tiltDeg, spacing, col, lw);
        }

        int rays = Math.Clamp(def.RayCount, 4, 96);
        // The viewport corners do not change across the VP loop - tl/br are fixed
        // for the frame - so the buffer is filled once here rather than re-made on
        // each pass. Same four points either way; it just keeps the stackalloc off
        // a repeated path (CA2014).
        Span<Vector2> corners = stackalloc Vector2[4] { tl, new(br.X, tl.Y), br, new(tl.X, br.Y) };
        for (int i = 0; i < vpCount; i++)
        {
            var vp = new Vector2((float)def.Vps[i].X, (float)def.Vps[i].Y);
            bool inside = vp.X >= tl.X && vp.X <= br.X && vp.Y >= tl.Y && vp.Y <= br.Y;

            // angular window the viewport subtends from this VP (full circle when
            // the VP is on screen)
            float a0 = 0f, a1 = MathF.PI * 2f;
            if (!inside)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                float baseA = MathF.Atan2(((tl.Y + br.Y) * 0.5f) - vp.Y, ((tl.X + br.X) * 0.5f) - vp.X);
                foreach (var corner in corners)
                {
                    float rel = MathF.Atan2(corner.Y - vp.Y, corner.X - vp.X) - baseA;
                    while (rel > MathF.PI) rel -= MathF.PI * 2f;
                    while (rel < -MathF.PI) rel += MathF.PI * 2f;
                    lo = MathF.Min(lo, rel); hi = MathF.Max(hi, rel);
                }
                a0 = baseA + lo; a1 = baseA + hi;
            }

            for (int r = 0; r < rays; r++)
            {
                float ang = a0 + (a1 - a0) * (rays == 1 ? 0.5f : r / (float)(rays - 1));
                var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                if (ClipRay(vp, d, tl, br, out var q0, out var q1))
                    ds.DrawLine(q0, q1, col, lw);
            }
        }
    }

    // Clips the ray origin + t*dir (t >= 0) to a rect; false when it misses.
    private static bool ClipRay(Vector2 o, Vector2 d, Vector2 tl, Vector2 br,
                                out Vector2 p0, out Vector2 p1)
    {
        float t0 = 0f, t1 = float.MaxValue;
        p0 = p1 = o;
        Span<float> pp = stackalloc float[4] { -d.X, d.X, -d.Y, d.Y };
        Span<float> qq = stackalloc float[4] { o.X - tl.X, br.X - o.X, o.Y - tl.Y, br.Y - o.Y };
        for (int i = 0; i < 4; i++)
        {
            if (MathF.Abs(pp[i]) < 1e-9f) { if (qq[i] < 0) return false; continue; }
            float t = qq[i] / pp[i];
            if (pp[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
        }
        if (t1 == float.MaxValue) return false;
        p0 = o + d * t0; p1 = o + d * t1;
        return true;
    }

    // Eyedropper (U2 wires the tool): returns the colour under a SCREEN point.
    // Sampling is logical rather than a GPU readback - it hit-tests the top-most
    // element and returns its true authored colour, which is what a picker wants
    // (a pixel read would return antialiased blends at stroke edges). Draw order
    // is shapes below ink, so strokes win; within each list, later = on top.
    public Color? SampleColorAt(Vector2 screenPt) => SampleColorAt(screenPt, out _);

    /// <summary>As above, and additionally reports whether the tap fell through
    /// to BARE PAPER - nothing drawn under it - which 10.8's Mix tool needs in
    /// order to tell "thin this ink" from "blend this ink with that one".
    ///
    /// <para>The returned colour is unchanged on that path: it is still
    /// <c>NotePage.Background</c>, exactly as before, so the existing eyedropper
    /// behaves identically. A caller that needs the ground the READER sees - the
    /// texture's own colour on a Brown Paper or Blueprint page - resolves it
    /// through <c>PaperTextures.Ground</c> off the back of this flag.</para></summary>
    public Color? SampleColorAt(Vector2 screenPt, out bool bareGround)
    {
        bareGround = false;
        if (_page == null) return null;
        var w = ToWorld(screenPt);
        float slop = 3f / ViewZoom;   // small screen-constant tolerance

        var stCand = StrokeCandidates(w.X - 24, w.Y - 24, w.X + 24, w.Y + 24);
        for (int i = _page.Strokes.Count - 1; i >= 0; i--)
        {
            var st = _page.Strokes[i];
            if (stCand != null && !stCand.Contains(st)) continue;
            float r = st.Size * 0.5f + slop;
            var pts = st.Points;
            for (int j = 0; j + 1 < pts.Count; j++)
            {
                var a = new Vector2(pts[j].X, pts[j].Y);
                var b = new Vector2(pts[j + 1].X, pts[j + 1].Y);
                var ab = b - a;
                float len2 = ab.LengthSquared();
                float t = len2 < 1e-6f ? 0f : Math.Clamp(Vector2.Dot(w - a, ab) / len2, 0f, 1f);
                if (Vector2.DistanceSquared(w, a + ab * t) <= r * r)
                {
                    try { return ColorUtil.Parse(st.Color); } catch { return null; }
                }
            }
        }

        var shCand = ShapeCandidates(w.X - 24, w.Y - 24, w.X + 24, w.Y + 24);
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var sh = _page.Shapes[i];
            if (shCand != null && !shCand.Contains(sh)) continue;
            var b = ShapeBounds(sh);
            if (w.X < b.Left - slop || w.X > b.Right + slop ||
                w.Y < b.Top - slop || w.Y > b.Bottom + slop) continue;
            try { return ColorUtil.Parse(sh.Color); } catch { }
        }

        bareGround = true;
        try { return ColorUtil.Parse(_page.Background); } catch { return null; }
    }

    // Finite pages draw their artboard: a boundary plus a scrim over everything
    // outside it. The canvas stays fully usable beyond the edge - the scrim only
    // signals what an export will include (CONCEPTS-DIRECTION 7.1).
    private void DrawArtboard(CanvasDrawingSession ds, Color bg)
    {
        var ab = _page == null ? null : PageSizes.ResolveArtboard(_page);
        if (ab == null) return;

        var tl = ToWorld(new Vector2(0, 0));
        var br = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
        float w = (float)ab.W, h = (float)ab.H;

        var scrim = ColorUtil.IsDark(bg)
            ? Color.FromArgb(96, 0, 0, 0)
            : Color.FromArgb(34, 40, 38, 34);
        // four side rects rather than a clipped layer: cheaper, and tile-safe
        if (tl.Y < 0) ds.FillRectangle(tl.X, tl.Y, br.X - tl.X, MathF.Min(0, br.Y) - tl.Y, scrim);
        if (br.Y > h) ds.FillRectangle(tl.X, MathF.Max(h, tl.Y), br.X - tl.X, br.Y - MathF.Max(h, tl.Y), scrim);
        float bandT = MathF.Max(0, tl.Y), bandB = MathF.Min(h, br.Y);
        if (bandB > bandT)
        {
            if (tl.X < 0) ds.FillRectangle(tl.X, bandT, MathF.Min(0, br.X) - tl.X, bandB - bandT, scrim);
            if (br.X > w) ds.FillRectangle(MathF.Max(w, tl.X), bandT, br.X - MathF.Max(w, tl.X), bandB - bandT, scrim);
        }

        var edge = ColorUtil.IsDark(bg)
            ? Color.FromArgb(120, 255, 255, 255)
            : Color.FromArgb(110, 0, 0, 0);
        ds.DrawRectangle(0, 0, w, h, edge, 1f / ViewZoom);
    }

    private void DrawPageTitle(CanvasDrawingSession ds, Color bg)
    {
        if (_page == null) return;
        // The floating bar carries the title now, and an export is the drawing
        // rather than the editor - either one silences the header entirely.
        if (!ShowPageHeader || ExportChromeless) return;
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

        var color = Color.FromArgb(80, Accent.R, Accent.G, Accent.B); // semi-translucent accent highlight
        float hw = s.Size * 3.5f;

        if (n == 1)
        {
            ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, hw / 2, color);
            return;
        }
        DrawPolyline(ds, rc, pts, n, offset, color, hw, _roundStyle);
    }

    /// <param name="veil">16.7's page fade. False for the WET stroke - ink under
    /// the nib is not yet part of the page and must never grey mid-gesture - and
    /// for anything that is itself the selection subject.</param>
    private void DrawStroke(CanvasDrawingSession ds, ICanvasResourceCreator rc, PenStroke s, Vector2 offset,
                            int? pointLimit, bool veil = true)
    {
        var pts = s.Points;
        int n = Math.Min(pointLimit ?? pts.Count, pts.Count);
        if (n == 0) return;

        // 16.7, THE ONE INTERCEPT. This is the only place a stroke's stored
        // colour string becomes a draw colour; every alpha and grain variant
        // below is derived from this local, so fading the page is one call on
        // one local variable and cannot reach `s.Color`.
        var color = Veil(ColorUtil.Parse(s.Color), !veil);
        // Every alpha this method lays down is scaled by the stroke's own
        // opacity. Clamped off zero so a fully transparent pen still leaves a
        // hairline the user can find and erase rather than invisible geometry.
        float op = Math.Clamp(s.Opacity ?? 1f, 0.02f, 1f);
        byte Al(int a) => (byte)Math.Clamp(a * op, 1, 255);
        color.A = Al(color.A);

        if (s.Pen == PenType.Highlighter)
        {
            color.A = Al(110);
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
                var c1 = color; c1.A = Al(150);
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, Math.Max(0.6f, pw / 2), c1);
                return;
            }
            var core = color; core.A = Al(145);
            DrawPolyline(ds, rc, pts, n, offset, core, pw, _roundStyle);
            var grain = color; grain.A = Al(55);
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
                var c1 = color; c1.A = Al(220);
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, Math.Max(1f, cw / 2), c1);
                return;
            }
            var coreC = color; coreC.A = Al(210);
            DrawPolyline(ds, rc, pts, n, offset, coreC, cw, _roundStyle);
            var gr = color; gr.A = Al(70);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(0.8f, 0.7f), gr, cw * 0.55f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(-0.7f, -0.6f), gr, cw * 0.5f, _roundStyle);
            DrawPolyline(ds, rc, pts, n, offset + new Vector2(0.2f, -0.8f), gr, cw * 0.4f, _roundStyle);
            return;
        }

        if (s.Pen == PenType.Watercolor)
        {
            // soft translucent wash that builds up where strokes overlap
            var wc = color; wc.A = Al(70);
            float ww = Math.Max(1.5f, s.Size * 2.2f);
            if (n == 1)
            {
                ds.FillCircle(new Vector2(pts[0].X, pts[0].Y) + offset, ww / 2, wc);
                return;
            }
            var wc2 = color; wc2.A = Al(42);
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

        if (s.Pen == PenType.Marker) color.A = Al(235);
        else if (s.Pen == PenType.Ballpoint) color.A = Al(240);

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


    // =======================================================================
    // Preview rendering (UI-SPEC-V2 1.1) — the radial dial's live scrub preview
    // =======================================================================

    /// <summary>Draws <paramref name="s"/> through the REAL stroke renderer onto
    /// any session. The dial's preview circle goes through here, so what the user
    /// sees while scrubbing is produced by the same code that lays ink on the
    /// page — never a UI ellipse standing in for it.</summary>
    public void RenderStrokeTo(CanvasDrawingSession ds, ICanvasResourceCreator rc,
        PenStroke s, Vector2 offset = default) => DrawStroke(ds, rc, s, offset, null);

    /// <summary>The widest this stroke will actually be drawn, in DIP.
    ///
    /// <para>Reference 11.1 item 2. The dial's preview has to choose its radius
    /// BEFORE it draws, and "a hollow circle stroked with the selected pen" is
    /// only hollow if that radius is chosen against the width the renderer is
    /// going to produce. That width is not the pen's size: every pen type maps
    /// size, pressure and stroke DIRECTION to a width differently - a
    /// calligraphy nib swings between 0.2x and 1.95x of it, a brush reaches
    /// 3.3x - so the only honest answer comes from the same function that lays
    /// the ink down.</para></summary>
    public float MaxStrokeWidth(PenStroke s)
    {
        float max = 0f;
        for (int i = 1; i < s.Points.Count; i++)
            max = Math.Max(max, SegmentWidth(s, s.Points[i - 1], s.Points[i], i));
        return max;
    }

    /// <summary>A perfect circle as a real stroke carrying the LIVE tool state:
    /// pen type, colour, size, opacity and pressure response. Pressure sweeps one
    /// full lobe so the pen's dynamics show, and the points run through the very
    /// same one-pole stabiliser the wet-ink path applies — so dragging Smoothness
    /// changes the preview for the real reason rather than a mimicked one.</summary>
    public PenStroke PreviewCircle(Vector2 centre, float radius, int segments = 200)
    {
        var s = new PenStroke
        {
            Pen = Pen,
            Color = ColorUtil.ToHex(PenColor),
            Size = PenSize,
            Sens = PenSensitivity,
            Opacity = PenOpacity >= 0.999f ? (float?)null : PenOpacity,
            PressureCurve = EffectivePressureCurve()
        };
        var pts = s.Points;
        var smooth = centre + new Vector2(0, -radius);
        float factor = PenStabiliser > 0 ? 1f - PenStabiliser * 0.85f : 1f;
        for (int i = 0; i <= segments; i++)
        {
            double t = i / (double)segments;
            double th = t * Math.PI * 2 - Math.PI / 2;          // start at the top
            var v = centre + new Vector2((float)(radius * Math.Cos(th)), (float)(radius * Math.Sin(th)));
            if (factor < 1f) { smooth += (v - smooth) * factor; v = smooth; }
            float pr = 0.28f + 0.72f * (float)Math.Sin(t * Math.PI);
            pts.Add(new StrokePoint(v.X, v.Y, pr));
        }
        return s;
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

    /// <summary>The pressure curve a freshly committed or wet stroke should carry:
    /// the two-control-point response baked to the 6-float form SegmentWidth already
    /// interprets when one is set, otherwise a copy of the raw PenPressureCurve. The
    /// bake keeps per-stroke JSON the same size (library.json is 53 MB).</summary>
    private List<float>? EffectivePressureCurve()
    {
        if (PenPressureResponse != null) return PenPressureResponse.ToLegacyPoints();
        return PenPressureCurve != null ? new List<float>(PenPressureCurve) : null;
    }

    private static float SegmentWidth(PenStroke s, StrokePoint a, StrokePoint b, int index)
    {
        float pr = (a.Pressure + b.Pressure) * 0.5f;
        if (pr <= 0.01f) pr = 0.5f;
        if (s.PressureCurve is { Count: >= 3 } pc)
        {
            float t = Math.Clamp(pr, 0f, 1f);
            if (pc.Count >= 6)
            {
                // custom curve: piecewise-linear through (0,0), three user
                // points (x,y pairs), and (1,1) (#curve)
                Span<float> xs = stackalloc float[5] { 0f, pc[0], pc[2], pc[4], 1f };
                Span<float> ys = stackalloc float[5] { 0f, pc[1], pc[3], pc[5], 1f };
                pr = ys[4];
                for (int k = 1; k < 5; k++)
                    if (t <= xs[k])
                    {
                        float dx = Math.Max(1e-4f, xs[k] - xs[k - 1]);
                        pr = ys[k - 1] + (ys[k] - ys[k - 1]) * (t - xs[k - 1]) / dx;
                        break;
                    }
            }
            else
            {
                float mid = pc[1];   // legacy single-midpoint quadratic
                pr = 2f * (1f - t) * t * mid + t * t;
            }
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
        if (!ShapeRecognition) return;
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
        // brief settle pulse so the squiggle->shape snap is visibly confirmed
        // instead of an instant silent swap (#anim-roadmap)
        _settleShape = _adjustShape;
        _settleStartMs = Environment.TickCount64;
        _canvas.Invalidate();
    }

    // shape-recognition settle pulse state (drawn on the Win2D canvas, so it's a
    // per-frame ease rather than a XAML Storyboard)
    private ShapeElement? _settleShape;
    private long _settleStartMs;

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

    // Same, but about an explicitly frozen centre (used while resizing, #17-batch2).
    private static Vector2 ToShapeLocalAt(ShapeElement s, Vector2 p, Vector2 center)
        => Math.Abs(s.Rotation) < 0.001 ? p : RotatePoint(p, center, -s.Rotation);

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
        => s.Kind != ShapeKind.Table && Vector2.Distance(RotateHandlePos(s), pos) <= tol + 8f / ViewZoom;

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
        var cand = ShapeCandidates(pos.X - tol, pos.Y - tol, pos.X + tol, pos.Y + tol);
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            if (cand != null && !cand.Contains(_page.Shapes[i])) continue;
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
                // resize anchored at the bbox corner farthest from the grabbed
                // vertex — robust for true polygon vertices, not just bbox corners
                int far = 0; float bd = -1;
                for (int i = 0; i < bbox.Length; i++)
                {
                    float d = Vector2.Distance(bbox[i], hpt);
                    if (d > bd) { bd = d; far = i; }
                }
                return bbox[far];
            }
        }
        return null;
    }

    /// <param name="veil">16.7 again — false for the subject and for the grain
    /// passes, which re-enter carrying the already-veiled colour.</param>
    private void DrawShape(CanvasDrawingSession ds, ShapeElement s, bool veil = true)
    {
        Matrix3x2 prevT = ds.Transform;
        bool rot = Math.Abs(s.Rotation) > 0.01;
        if (rot)
            ds.Transform = Matrix3x2.CreateRotation((float)(s.Rotation * Math.PI / 180.0), ShapeCenter(s)) * prevT;
        // settle pulse: a freshly recognised shape eases in from a 6% overshoot
        bool settling = ReferenceEquals(s, _settleShape);
        if (settling)
        {
            float t = (Environment.TickCount64 - _settleStartMs) / 200f;
            if (t >= 1f) _settleShape = null;
            else
            {
                float ease = 1f - (1f - t) * (1f - t);           // quad ease-out
                float sc = 1.06f - 0.06f * ease;
                ds.Transform = Matrix3x2.CreateScale(sc, sc, ShapeCenter(s)) * ds.Transform;
                _canvas.Invalidate();                            // keep animating
            }
        }
        // 11.20 item 16: the shape takes the STYLE of the pen that placed it, not
        // a flat line at its nominal size. Helpers/PenStyle evaluates the stroke
        // renderer's own per-pen treatment at neutral pressure - the same
        // arithmetic solved at a point, because a shape has no pressure samples
        // and no direction to take a nib angle from.
        //
        // Images and tables are exempt: a photograph has no ink of its own, and a
        // table's rules are structure rather than a mark - a highlighter would
        // render one unreadable.
        bool inked = s.Kind is not (ShapeKind.Image or ShapeKind.Table);
        // 16.7's second intercept — the only place a shape's stored colour is
        // parsed for drawing. Same shape as the stroke's: a local in, a local out.
        var color = Veil(ColorUtil.Parse(s.Color), !veil);
        float w = Math.Max(1f, s.Size);
        // The faint offset passes the grainy pens lay beside their core. Held
        // until the core has been drawn, because that is the order the stroke
        // renderer lays them in; the wash's single WIDE pass goes underneath and
        // is drawn immediately.
        List<ShapeElement>? overGrain = null;
        if (inked)
        {
            float op = Math.Clamp(s.Opacity <= 0f ? 1f : s.Opacity, 0.02f, 1f);
            color.A = (byte)Math.Clamp(PenStyle.Alpha(s.Pen) * op, 1, 255);
            w = PenStyle.Width(s.Pen, Math.Max(1f, s.Size));
            foreach (var g in PenStyle.Grain(s.Pen))
            {
                // Each pass re-enters this method as a MONOLINE clone: monoline's
                // alpha is opaque and PenStyle.Width(Monoline, x) == x, so the
                // pass's own opacity carries the grain's faintness and its size
                // lands exactly on the width factor. Standard-to-monoline means
                // the clone has no grain of its own and cannot recurse twice.
                var pass = new ShapeElement
                {
                    Kind = s.Kind, X = s.X + g.Dx, Y = s.Y + g.Dy, W = s.W, H = s.H,
                    Color = s.Color, Rotation = s.Rotation,
                    Size = Math.Max(0.6f, w * g.WidthK),
                    Pen = PenType.Monoline,
                    Opacity = (g.Alpha / 255f) * op,
                };
                // `veil` has to ride along: the pass is a CLONE and so is not in
                // the selection sets, so it would fade under an exempt subject.
                if (PenStyle.GrainUnderneath(s.Pen)) DrawShape(ds, pass, veil);
                else (overGrain ??= new List<ShapeElement>()).Add(pass);
            }
        }
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
                    // equations stay readable when the page flips light/dark:
                    // if the equation ink matches the page brightness, invert
                    // RGB at draw time — alpha (transparent bg) untouched (#eq)
                    if (s.EquationLatex != null && _page != null &&
                        ColorUtil.IsDark(ColorUtil.Parse(_page.Background)) == EquationInkIsDark(s.ImagePath, bmp))
                    {
                        using var inv = new Microsoft.Graphics.Canvas.Effects.ColorMatrixEffect
                        {
                            Source = bmp,
                            ColorMatrix = new Microsoft.Graphics.Canvas.Effects.Matrix5x4
                            {
                                M11 = -1, M22 = -1, M33 = -1, M44 = 1,
                                M51 = 1, M52 = 1, M53 = 1, M54 = 0
                            }
                        };
                        ds.DrawImage(inv, r, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height));
                    }
                    else
                    {
                        ds.DrawImage(bmp, r);
                    }
                }
                else
                {
                    ds.DrawRectangle(r, Color.FromArgb(130, 128, 128, 128), 1.5f, _dashStyle);
                    if (s.ImagePath != null) RequestBitmap(s.ImagePath);
                }
                // 16.7 for an attachment that is NOT the subject. A photograph
                // has no stored colour to intercept, so the fade is composited
                // over the pixels instead - which is the same lerp toward
                // #8E8E8E the other two intercepts perform, done after the fact
                // because an opaque image fills its own rectangle exactly. The
                // bitmap is untouched; only this frame is.
                if (veil && Veiling)
                    ds.FillRectangle(r, Color.FromArgb(
                        (byte)Math.Clamp(255 * Motion.FadeEase(_veil), 0, 255),
                        VeilGrey.R, VeilGrey.G, VeilGrey.B));
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
                            ds.FillRectangle(cellRect, Color.FromArgb(40, Accent.R, Accent.G, Accent.B));
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
                ds.DrawText(s.AxisLabelX ?? "x", new Vector2((float)(s.X + s.W) - 4, o.Y + 6), color, _labelFormat);
                ds.DrawText(s.AxisLabelY ?? "y", new Vector2(o.X - 18, (float)s.Y - 4), color, _labelFormat);
                break;
            }
            case ShapeKind.AxesXYZ:
            {
                var o = new Vector2((float)(s.X + s.W / 2), (float)(s.Y + s.H / 2));
                DrawArrow(ds, o, new Vector2((float)(s.X + s.W), o.Y), color, w);
                DrawArrow(ds, o, new Vector2(o.X, (float)s.Y), color, w);
                DrawArrow(ds, o, new Vector2((float)s.X, (float)(s.Y + s.H)), color, w);
                ds.DrawText(s.AxisLabelX ?? "x", new Vector2((float)(s.X + s.W) - 2, o.Y + 4), color, _labelFormat);
                ds.DrawText(s.AxisLabelY ?? "y", new Vector2(o.X + 8, (float)s.Y - 4), color, _labelFormat);
                ds.DrawText(s.AxisLabelZ ?? "z", new Vector2((float)s.X - 2, (float)(s.Y + s.H) + 2), color, _labelFormat);
                break;
            }
        }
        // The pencil's and the crayon's grain, laid over the core exactly as the
        // stroke renderer lays it. Inside the rotate/settle transform, so a
        // rotated shape's grain rotates with it.
        if (overGrain != null)
            foreach (var pass in overGrain) DrawShape(ds, pass, veil);
        if (rot || settling) ds.Transform = prevT;   // settle pulse also bends the transform
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

    /// <summary>17.8, asked of ONE shape: has <see cref="SelectionChrome"/>
    /// already put a mark on every point this shape's square handles mark?
    ///
    /// <para>This is the question that decides whether removing the squares is a
    /// de-duplication or a deletion, and it is asked geometrically rather than
    /// assumed. The chrome draws four hollow circles on the four corners of
    /// <see cref="SubjectBoundsWorld"/>, which for a single active shape is
    /// <see cref="ShapeBounds"/>. So the squares are redundant exactly when
    /// <see cref="HandleVertices"/> IS those four corners:</para>
    ///
    /// <list type="bullet">
    /// <item>an image, an ellipse, a rectangle, axes - handles are the bbox
    /// corners, so every square has a circle on it. REDUNDANT.</item>
    /// <item>a line or an arrow - two handles, at the two ENDPOINTS. A circle on
    /// the bounding box's corners does not mark either of them.</item>
    /// <item>a polygon - handles are the true vertices (#11). A triangle's apex
    /// is not a corner of its bounding box.</item>
    /// <item>ANY rotated shape - the handles turn with it and the chrome's
    /// rectangle does not, so the circles land somewhere the grips are not.</item>
    /// </list>
    ///
    /// <para>In the first case the square is a second mark on a marked point and
    /// 17.8 removes it. In the others it is the ONLY mark on a live grip, and
    /// removing it would hide a gesture rather than tidy a duplicate.</para></summary>
    private static bool ChromeAlreadyMarksHandles(ShapeElement s)
    {
        if (Math.Abs(s.Rotation) > 0.001) return false;
        var handles = HandleVertices(s);
        var corners = ShapeCorners(s);
        if (handles.Length != corners.Length) return false;
        for (int i = 0; i < handles.Length; i++)
            if (Vector2.Distance(handles[i], corners[i]) > 0.001f) return false;
        // Line and Arrow report two "corners" that are the endpoints, not a box.
        return s.Kind is not (ShapeKind.Line or ShapeKind.Arrow);
    }

    private void DrawShapeSelection(CanvasDrawingSession ds, ShapeElement s, Color accent, float uiScale)
    {
        var c = ShapeCenter(s);
        var bb = ShapeBounds(s);

        // 17.8 ON THE SINGLE-ACTIVE-SHAPE PATH, which it had reached in comment
        // only. The multi-selection lost its tint, its dashed box and its white
        // corner squares; this path kept all of the second two, and a visual pass
        // on the built app found an attachment wearing BOTH chromes at once -
        // squares sitting on SelectionChrome's circles, a dashed box drawn on
        // ShapeBounds while the chrome framed the very same rectangle. That is
        // the doubling 17.8's own comment claims to have removed.
        //
        // THE DASHED BOX GOES, unconditionally: SubjectBoundsWorld for an active
        // shape IS ShapeBounds(s), so this line was tracing the chrome's own
        // rectangle. 17.8: "only the edges remain - the corner circles and the
        // full-canvas guides. No tinted rectangle, no dashed box."
        //
        // THE SQUARES GO WHERE THE CIRCLES STAND ON THEM, and only there - see
        // ChromeAlreadyMarksHandles. This is the one place care is owed, because
        // the squares are NOT decoration: HitHandle hit-tests HandleVertices(s)
        // to start a resize, so they are the visible affordance for a real
        // gesture.
        //
        // The HIT REGION IS UNTOUCHED EITHER WAY. HitHandle computes from the
        // model and never consults a draw call, so nothing below can widen or
        // narrow it. (The corner-drag the earlier note pointed at,
        // TryBeginSelectionScale over SelCorners(), is a different gesture on a
        // different path - it returns immediately unless HasMultiSelection, so it
        // never sees an active shape at all and is not in question here.)
        if (!ChromeAlreadyMarksHandles(s))
        {
            float hs = 5.5f / ViewZoom;
            foreach (var v in HandleVertices(s))
            {
                var p = RotatePoint(v, c, s.Rotation);
                ds.FillRectangle(new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2), Colors.White);
                ds.DrawRectangle(new Rect(p.X - hs, p.Y - hs, hs * 2, hs * 2), accent, uiScale);
            }
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

    public void InsertImage(string path, double pixelW, double pixelH, string? equationLatex = null)
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
            EquationLatex = equationLatex,
            // An image lands where it was dropped, negative coordinates included.
            // It used to be clamped to (44, 104) to stay out of
            // NormalizeContent's trigger zone; with 16.1's infinite canvas that
            // migration is gone, and the clamp with it - it would otherwise fling
            // an image across the page whenever the user was panned above or left
            // of the origin, which is now somewhere they can be.
            X = atCaret ? c.X : c.X - w / 2,
            Y = atCaret ? c.Y : c.Y - h / 2,
            W = w,
            H = h,
            Size = 0
        };
        PushAction(new AddShapeAction(s), _page);
        _activeShape = s;
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    /// <summary>[[Note Name]] links found in the text box under the world point,
    /// in document order (#31-batch2). Empty when there is no text box there.</summary>
    public IReadOnlyList<string> TextLinksAt(Vector2 pos)
    {
        if (_page == null) return Array.Empty<string>();
        FlushTexts();
        foreach (var (id, ui) in _textUi)
        {
            double l = Canvas.GetLeft(ui.Container), tp = Canvas.GetTop(ui.Container);
            double w = ui.Container.ActualWidth, h = ui.Container.ActualHeight;
            if (pos.X < l || pos.X > l + w || pos.Y < tp || pos.Y > tp + h) continue;
            var model = _page.Texts.FirstOrDefault(t => t.Id == id);
            if (model == null || string.IsNullOrEmpty(model.Rtf)) continue;
            var plain = RtfToPlainText(model.Rtf, out _, out _);
            return System.Text.RegularExpressions.Regex
                .Matches(plain, @"\[\[([^\[\]\r\n]{1,64})\]\]")
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        return Array.Empty<string>();
    }

    /// <summary>Topmost axes shape whose bounds contain the world point (#28-batch2).</summary>
    public ShapeElement? AxesShapeAt(Vector2 pos)
    {
        if (_page == null) return null;
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var s = _page.Shapes[i];
            if (s.Kind is not (ShapeKind.AxesXY or ShapeKind.AxesXYZ)) continue;
            var b = ShapeBounds(s);
            if (pos.X >= b.Left && pos.X <= b.Right && pos.Y >= b.Top && pos.Y <= b.Bottom) return s;
        }
        return null;
    }

    /// <summary>Topmost equation image whose bounds contain the world point (#27-batch2).</summary>
    public ShapeElement? EquationShapeAt(Vector2 pos)
    {
        if (_page == null) return null;
        for (int i = _page.Shapes.Count - 1; i >= 0; i--)
        {
            var s = _page.Shapes[i];
            if (s.Kind != ShapeKind.Image || s.EquationLatex == null) continue;
            var b = ShapeBounds(s);
            if (pos.X >= b.Left && pos.X <= b.Right && pos.Y >= b.Top && pos.Y <= b.Bottom) return s;
        }
        return null;
    }

    /// <summary>Swaps an equation image for a re-rendered one in place, keeping
    /// its position and on-page width (#27-batch2).</summary>
    /// <summary>CONCEPTS-REF 16.2's paperclip: point the selected attachment at
    /// a different file.
    ///
    /// <para>The rectangle the user placed is kept - same X, Y and W - and only
    /// the height follows the new file's aspect, so a replacement lands where the
    /// old picture was rather than being re-inserted centred and re-fitted.</para>
    ///
    /// <para>Refuses on a LOCKED attachment, on the same rule the waste bin and
    /// the flips follow: a lock that stops a drag but not a swap is not a
    /// lock.</para></summary>
    public void ReplaceAttachmentImage(ShapeElement s, string path, double pixelW, double pixelH)
    {
        if (_page == null || !_page.Shapes.Contains(s) || s.Kind != ShapeKind.Image) return;
        if (s.Locked || string.IsNullOrEmpty(path)) return;
        double h = Math.Max(24, Math.Abs(s.W) * (pixelH / Math.Max(1, pixelW)));
        PushAction(new ReplaceImageAction(s, path, s.H < 0 ? -h : h), _page);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
        // The bounds moved, so the guides, the corner circles and both plates have
        // to be told - and that is one call, because every route into the selection
        // presentation goes through the same publish.
        PublishSelection();
    }

    public void UpdateEquationImage(ShapeElement s, string path, double pixW, double pixH, string latex)
    {
        if (_page == null || !_page.Shapes.Contains(s)) return;
        s.ImagePath = path;
        s.EquationLatex = latex;
        s.H = Math.Max(24, Math.Abs(s.W) * (pixH / Math.Max(1, pixW)));
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    private bool FocusTextAt(Vector2 pos)
    {
        foreach (var (id, ui) in _textUi)
        {
            double l = Canvas.GetLeft(ui.Container), tp = Canvas.GetTop(ui.Container);
            double w = ui.Container.ActualWidth, h = ui.Container.ActualHeight;
            // 17.11a: Canvas.Left/Top and ActualWidth/Height describe the box
            // BEFORE its RenderTransform, so a rotated box was tested against a
            // rectangle it is no longer sitting in - a tap inside a tilted box
            // fell through to the canvas and started a lasso. Bring the probe
            // into the box's own frame instead of trying to turn the box.
            var probe = pos;
            double rot = _page?.Texts.FirstOrDefault(x => x.Id == id)?.Rotation ?? 0;
            if (Math.Abs(rot) > 0.01)
                probe = RotatePoint(pos, new Vector2((float)(l + w / 2), (float)(tp + h / 2)), -rot);
            if (probe.X >= l && probe.X <= l + w && probe.Y >= tp && probe.Y <= tp + h)
            {
                ui.Box.Focus(FocusState.Pointer);
                ActiveTextBox = ui.Box;
                ActiveTextChanged?.Invoke(ui.Box);
                // caret lands at the TAP, not at position 0 (B4) — matters most
                // for tall table cells whose empty lower half is now tappable
                var cellT = _page?.Texts.FirstOrDefault(x => x.Id == id);
                // (skip rotated tables: the world→box-local mapping below assumes no rotation)
                if (cellT?.TableId is Guid tid &&
                    Math.Abs(_page?.Shapes.FirstOrDefault(sh => sh.Id == tid)?.Rotation ?? 0) < 0.01)
                {
                    var box = ui.Box;
                    // box-local coordinates: the container sits at (l, tp) in the
                    // text layer (world space) and the cell grip has zero height
                    var local = new Point(pos.X - l, pos.Y - tp);
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            box.Document.Selection.SetPoint(local, PointOptions.ClientCoordinates, false);
                        }
                        catch
                        {
                            try
                            {
                                var rng = box.Document.GetRangeFromPoint(local, PointOptions.ClientCoordinates);
                                box.Document.Selection.SetRange(rng.StartPosition, rng.StartPosition);
                            }
                            catch { }
                        }
                    });
                }
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
        // CONCEPTS-REF 11.20 item 16 - a placed shape is drawn WITH THE ACTIVE
        // PEN: its colour and size, as before, and now its STYLE and OPACITY too,
        // so putting a shape down looks like the user drew it with whatever pen
        // is in their hand.
        var s = new ShapeElement
        {
            Kind = kind,
            Color = ColorUtil.ToHex(PenColor),
            Size = Math.Max(2f, PenSize * 0.9f),
            Pen = Pen,
            Opacity = PenOpacity,
        };
        if (kind is ShapeKind.Line or ShapeKind.Arrow)
        {
            s.X = c.X - w / 2; s.Y = c.Y; s.W = w; s.H = 0;
        }
        else
        {
            s.X = c.X - w / 2; s.Y = c.Y - h / 2; s.W = w; s.H = h;
        }
        PushAction(new AddShapeAction(s), _page);
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

    private RichEditBox? SpawnTextBox(Vector2 worldPos, string? initial)
    {
        if (_page == null) return null;
        FlushTexts(); // save other boxes' live edits before adding a new one
        // 25.5: a new box is created in the remembered text colour. Null leaves
        // it following the page's ink, which is what every box did before 25.
        var t = new TextElement
        {
            X = worldPos.X - 4, Y = worldPos.Y - 10, AutoWidth = true,
            TextColor = PendingTextColor,
        };
        PushAction(new AddTextAction(t), _page);
        BuildTextUi(t); // add ONLY the new box so existing boxes keep their formatting
        if (_textUi.TryGetValue(t.Id, out var ui))
        {
            ui.Box.Focus(FocusState.Programmatic);
            // Honour the chosen default font + size for the first characters typed.
            try { var cf = ui.Box.Document.Selection.CharacterFormat; cf.Name = PendingFontFamily; cf.Size = PendingFontSize; } catch { }
            if (!string.IsNullOrEmpty(initial))
                try { ui.Box.Document.Selection.TypeText(initial); } catch { }
            ContentChanged?.Invoke();
            return ui.Box;
        }
        ContentChanged?.Invoke();
        return null;
    }

    // Per-bubble width-growth scratch state: cached max-run font size (C1),
    // keystroke counter for cache refresh, and the shrink-debounce timer (C3).
    // Lives in the BuildTextUi closure alongside the box it sizes.
    private sealed class BubbleSizeState
    {
        public float MaxRunPx;                 // cached max run font size, PIXELS
        public int Keystrokes;                 // keystrokes since last cache refresh
        public int LastLen = -1;               // doc length at the previous TextChanged
        public Microsoft.UI.Dispatching.DispatcherQueueTimer? ShrinkTimer;
        public double PendingShrinkW = -1;     // width the debounced shrink will apply
    }

    // Sizes a free bubble to its content: width phase (auto-width boxes only,
    // #15 contract) then height phase (ALL free bubbles, including pinned and
    // legacy fixed-width boxes). Table cells never come through here — their
    // rows grow instead (AutoGrowCellRow).
    private void AutoSizeBubble(TextElement t, RichEditBox box, BubbleSizeState st)
    {
        if (t.TableId != null) return;   // cells: rows grow, boxes don't (spec D)
        if (!t.WidthPinned && t.AutoWidth) AutoSizeBubbleWidth(t, box, st);
        AutoSizeBubbleHeight(t, box);
        // persist the derived width so selection bounds / export see the real
        // size, not the stale construction default (no undo push — derived).
        if (!double.IsNaN(box.Width) && box.Width > 0) t.Width = box.Width;
    }

    // Width phase: measures each LINE at the document's max run size and sets
    // the width explicitly — grow immediately, shrink debounced 400 ms (C3),
    // hard-capped at the box's snapshotted ceiling (#15).
    private void AutoSizeBubbleWidth(TextElement t, RichEditBox box, BubbleSizeState st)
    {
        try
        {
            // a degenerate saved cap (pre-floor-change notes) starves MinWidth —
            // re-snapshot it (C4)
            if (t.MaxWidth > 0 && t.MaxWidth < 200)
            {
                t.MaxWidth = ComputeBubbleMaxWidth(t.X);
                box.MaxWidth = t.MaxWidth;
            }
            box.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out string txt);
            txt = txt.TrimEnd('\r', '\n');
            // Max run size, cached (C1): Selection.CharacterFormat.Size is the
            // caret's font — wrong the moment sizes are mixed. Recompute on
            // paste/format change (length delta ≠ ±1) or every 10th keystroke.
            int len = txt.Length;
            if (st.MaxRunPx <= 0 || st.LastLen < 0 || Math.Abs(len - st.LastLen) != 1 || ++st.Keystrokes >= 10)
            {
                st.MaxRunPx = MaxRunFontSizePx(box);
                st.Keystrokes = 0;
            }
            st.LastLen = len;
            float fs = st.MaxRunPx;
            // Per-line natural width (C2): a long first line + short second must
            // let the box shrink back — measure lines separately, take the max.
            double natural = 0;
            if (txt.Length > 0)
            {
                using var fmt = new CanvasTextFormat { FontSize = fs, FontFamily = box.FontFamily?.Source ?? "Segoe UI" };
                var dev = CanvasDevice.GetSharedDevice();
                foreach (var line in txt.Split('\r'))
                {
                    if (line.Length == 0) continue;
                    using var tl = new CanvasTextLayout(dev, line, fmt, float.MaxValue, float.MaxValue);
                    natural = Math.Max(natural, tl.LayoutBounds.Width);
                }
            }
            // one-character lookahead so the box widens BEFORE the next
            // keystroke would wrap, not after; floor 200 aligns with MinWidth (C4)
            double needed = Math.Clamp(natural + fs * 0.75 + box.Padding.Left + box.Padding.Right + 18, 200, Math.Max(200, t.MaxWidth));
            double cur = double.IsNaN(box.Width) ? box.ActualWidth : box.Width;
            if (double.IsNaN(box.Width) || cur <= 0 || needed > cur - 2)
            {
                // grow NOW — wrap must never beat the widen
                st.PendingShrinkW = -1;
                st.ShrinkTimer?.Stop();
                if (double.IsNaN(box.Width) || Math.Abs(cur - needed) > 1.5) box.Width = needed;
            }
            else if (needed < cur - 32)
            {
                // shrink after the typing pause, not per keystroke (C3)
                if (st.ShrinkTimer == null)
                {
                    var timer = DispatcherQueue.CreateTimer();
                    timer.Interval = TimeSpan.FromMilliseconds(400);
                    timer.IsRepeating = false;
                    timer.Tick += (_, _) =>
                    {
                        if (st.PendingShrinkW <= 0) return;
                        box.Width = st.PendingShrinkW;
                        st.PendingShrinkW = -1;
                        AutoSizeBubbleHeight(t, box);   // narrower box may wrap taller
                        if (!double.IsNaN(box.Width) && box.Width > 0) t.Width = box.Width;
                    };
                    st.ShrinkTimer = timer;
                }
                st.PendingShrinkW = needed;
                st.ShrinkTimer.Stop();
                st.ShrinkTimer.Start();
            }
            else
            {
                // inside the hysteresis band — cancel any pending shrink
                st.PendingShrinkW = -1;
                st.ShrinkTimer?.Stop();
            }
        }
        catch { }
    }

    // Height phase (A1): the wrapped content height comes from the RichEdit
    // document itself — mixed fonts/sizes make a single-format Win2D
    // measurement wrong vertically. Explicit height means the disabled inner
    // ScrollViewer can never clip or scroll; content is always fully visible.
    private static void AutoSizeBubbleHeight(TextElement t, RichEditBox box)
    {
        try
        {
            box.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out string plain);
            if (string.IsNullOrEmpty(plain.TrimEnd('\r', '\n')))
            {
                box.ClearValue(FrameworkElement.HeightProperty);   // empty: MinHeight rules
                return;
            }
            double contentH = MeasureContentHeight(box);
            if (contentH <= 0) return;   // measurement failed — never clip by guessing
            float fs;
            try { fs = box.Document.Selection.CharacterFormat.Size * 96f / 72f; } catch { fs = (float)box.FontSize; }
            if (fs <= 1 || float.IsNaN(fs)) fs = (float)box.FontSize;
            // descender/swash headroom — same motive as the 1.25 line-spacing fix (#17-batch3)
            double h = Math.Max(40, contentH + box.Padding.Top + box.Padding.Bottom + fs * 0.35);
            if (double.IsNaN(box.Height) || Math.Abs(box.Height - h) > 1.5) box.Height = h;
        }
        catch { }
    }

    // Wrapped content height straight from the RichEdit engine: bottom of the
    // last char minus top of the first (A1/B1). Falls back to a XAML measure
    // when GetPoint is unavailable (box not realised yet).
    private static double MeasureContentHeight(RichEditBox box)
    {
        try
        {
            var full = box.Document.GetRange(0, int.MaxValue);
            int end = full.EndPosition;
            var topR = box.Document.GetRange(0, 0);
            topR.GetPoint(Microsoft.UI.Text.HorizontalCharacterAlignment.Left,
                          Microsoft.UI.Text.VerticalCharacterAlignment.Top,
                          Microsoft.UI.Text.PointOptions.NoHorizontalScroll | Microsoft.UI.Text.PointOptions.AllowOffClient,
                          out Point pTop);
            var botR = box.Document.GetRange(end, end);
            botR.GetPoint(Microsoft.UI.Text.HorizontalCharacterAlignment.Left,
                          Microsoft.UI.Text.VerticalCharacterAlignment.Bottom,
                          Microsoft.UI.Text.PointOptions.NoHorizontalScroll | Microsoft.UI.Text.PointOptions.AllowOffClient,
                          out Point pBot);
            double h = pBot.Y - pTop.Y;
            if (h > 0.5 && h < 100000) return h;
        }
        catch { }
        try
        {
            double w = !double.IsNaN(box.Width) && box.Width > 0 ? box.Width
                     : box.ActualWidth > 0 ? box.ActualWidth : 280;
            box.Measure(new Size(w, double.PositiveInfinity));
            return Math.Max(0, box.DesiredSize.Height - box.Padding.Top - box.Padding.Bottom);
        }
        catch { return 0; }
    }

    // Largest font size across the document's format runs, in PIXELS (C1).
    // O(#runs), capped at 64 runs; points→pixels because Win2D measures pixels
    // while CharacterFormat.Size is points (#15 fix).
    private static float MaxRunFontSizePx(RichEditBox box)
    {
        float maxPt = 0;
        try
        {
            var doc = box.Document;
            int len = doc.GetRange(0, int.MaxValue).EndPosition;
            int pos = 0, guard = 0;
            while (pos < len && guard++ < 64)
            {
                var run = doc.GetRange(pos, pos);
                run.Expand(Microsoft.UI.Text.TextRangeUnit.CharacterFormat);
                float sz = run.CharacterFormat.Size;
                if (sz > maxPt && sz < 1000 && !float.IsNaN(sz)) maxPt = sz;
                if (run.EndPosition <= pos) break;   // no forward progress — bail
                pos = run.EndPosition;
            }
        }
        catch { }
        if (maxPt <= 1 || float.IsNaN(maxPt))
        {
            try { maxPt = box.Document.Selection.CharacterFormat.Size; } catch { }
        }
        if (maxPt <= 1 || float.IsNaN(maxPt)) maxPt = (float)(box.FontSize * 72.0 / 96.0);
        return maxPt * 96f / 72f;
    }

    // Ceiling for an auto-growing text box, in world units (#15): half the real
    // screen, but clamped so the box never spills past the app-window's right
    // edge. Snapshotted per box at first build; a later window resize won't move
    // it (only new boxes pick up the new measurements).
    private double ComputeBubbleMaxWidth(double worldLeft)
    {
        double zoom = Math.Max(0.1, ViewZoom);
        double windowDip = ActualWidth > 40 ? ActualWidth : 1280;
        double screenDip = ScreenWidthDip > 40 ? ScreenWidthDip : windowDip;
        double halfScreenDip = screenDip * 0.5;
        double boxScreenLeft = worldLeft * zoom + ViewOffset.X;   // text layer maps world->screen
        double capDip = halfScreenDip;
        // clamp to the window edge only for boxes actually visible right now —
        // a box built while scrolled far away must not inherit a nonsense cap
        if (boxScreenLeft >= 0 && boxScreenLeft < windowDip - 60)
            capDip = Math.Min(capDip, windowDip - boxScreenLeft - 24);
        return Math.Max(260, capDip / zoom);
    }

    public bool HasPendingText => _pendingTextPos != null;

    /// <summary>Turns a pending Text-tool caret into a real empty box so
    /// dictation and paste land at the tapped spot without needing a typed
    /// character first (#5-batch4). A bare tap alone still creates nothing.</summary>
    public RichEditBox? MaterializePendingText()
    {
        if (_page == null || _pendingTextPos == null) return null;
        var at = _pendingTextPos.Value;
        CancelPendingText();
        return SpawnTextBox(at, null);
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
        PushAction(new AddTextAction(t), _page);
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

        PushAction(new AddMixedAction(new List<PenStroke>(), new List<ShapeElement> { table }, texts), _page);
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
        PushAction(new RepositionTextsAction(moves), _page);
        RebuildTextLayer();
    }

    /// <summary>Word model: a cell's stored row height is a MINIMUM — typing
    /// past it grows the row (never shrinks; row-fit is the divider
    /// double-tap). Reflows in place so the focused cell keeps focus and
    /// caret (B1). Undo is coalesced per focus session in BuildTextUi.</summary>
    private void AutoGrowCellRow(TextElement t, RichEditBox box)
    {
        if (_page == null || t.TableId == null) return;
        var table = _page.Shapes.FirstOrDefault(s => s.Id == t.TableId && s.Kind == ShapeKind.Table);
        if (table == null) return;
        try
        {
            var rh = TableRowHeights(table);
            int r0 = Math.Clamp(t.TableRow, 0, rh.Length - 1);
            int span = Math.Clamp(Math.Max(1, t.CellRowSpan), 1, rh.Length - r0);
            double sum = 0;
            for (int i = 0; i < span; i++) sum += rh[r0 + i];

            double contentH = MeasureContentHeight(box);
            if (contentH <= 0) return;
            double needed = contentH + box.Padding.Top + box.Padding.Bottom + 4;
            if (needed <= sum + 0.5) return;   // never auto-shrink while typing

            double deficit = needed - sum;
            // materialize TRowH so the growth lands on a real per-row list
            if (table.TRowH == null || table.TRowH.Count != rh.Length) table.TRowH = rh.ToList();
            table.TRowH[r0 + span - 1] += deficit;   // a row-span merge grows its LAST row
            table.H += deficit;
            ReflowTableCellsInPlace(table);
            _canvas.Invalidate();
        }
        catch { }
    }

    /// <summary>Same geometry as ReflowTableCells but mutates the LIVE UI —
    /// no undo push, no rebuild — so typing in a cell never loses focus or
    /// caret when the table reflows (B2). Structural ops (insert/delete row,
    /// merge/split, drag-resize, LoadPage heal) keep the rebuild variant.</summary>
    public void ReflowTableCellsInPlace(ShapeElement table)
    {
        if (_page == null || table.Kind != ShapeKind.Table) return;
        var cw = TableColWidths(table);
        var rh = TableRowHeights(table);
        double[] px = new double[cw.Length + 1];
        for (int i = 0; i < cw.Length; i++) px[i + 1] = px[i] + cw[i];
        double[] py = new double[rh.Length + 1];
        for (int i = 0; i < rh.Length; i++) py[i + 1] = py[i] + rh[i];

        foreach (var t in _page.Texts)
        {
            if (t.TableId != table.Id) continue;
            int c = Math.Clamp(t.TableCol, 0, cw.Length - 1);
            int r = Math.Clamp(t.TableRow, 0, rh.Length - 1);
            int colSpan = Math.Clamp(t.CellColSpan, 1, cw.Length - c);
            int rowSpan = Math.Clamp(Math.Max(1, t.CellRowSpan), 1, rh.Length - r);

            double widthSum = 0;
            for (int i = 0; i < colSpan; i++) widthSum += cw[c + i];
            double heightSum = 0;
            for (int i = 0; i < rowSpan; i++) heightSum += rh[r + i];

            t.X = table.X + px[c] + 6;
            t.Y = table.Y + py[r] + 2;
            t.Width = Math.Max(48, widthSum - 16);

            if (!_textUi.TryGetValue(t.Id, out var ui)) continue;
            Canvas.SetLeft(ui.Container, t.X);
            Canvas.SetTop(ui.Container, t.Y);
            ui.Box.Width = t.Width;
            ui.Box.MaxHeight = Math.Max(22, heightSum - 4);   // span-aware (B3)
            ui.Box.MinHeight = Math.Max(20, heightSum - 4);   // hit target fills the row (B3)
            if (Math.Abs(table.Rotation) > 0.01)
            {
                // recompute the ride-along rotation (#tablerot) at the new origin
                double ang = table.Rotation * Math.PI / 180.0;
                double tcx = table.X + table.W / 2, tcy = table.Y + table.H / 2;
                double dxr = t.X - tcx, dyr = t.Y - tcy;
                double rx = tcx + dxr * Math.Cos(ang) - dyr * Math.Sin(ang);
                double ry = tcy + dxr * Math.Sin(ang) + dyr * Math.Cos(ang);
                ui.Container.RenderTransformOrigin = new Point(0, 0);
                ui.Container.RenderTransform = new CompositeTransform
                { Rotation = table.Rotation, TranslateX = rx - t.X, TranslateY = ry - t.Y };
            }
            else
            {
                ui.Container.RenderTransform = null;
            }
        }
        _canvas.Invalidate();
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

        PushAction(new CellMergeAction(topLeft, topLeft.CellColSpan, topLeft.CellRowSpan, toColSpan, toRowSpan, hidden), _page);
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

        PushAction(new CellMergeAction(cell, cell.CellColSpan, cell.CellRowSpan, 1, 1, restored), _page);
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
        PushAction(new CompositeAction(actions, "Change cell fill"), _page);
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
        PushAction(new CompositeAction(actions, "Change cell border"), _page);
        _canvas.Invalidate();
        ContentChanged?.Invoke();
    }

    public void TableToggleHeaderRow(ShapeElement table)
    {
        if (_page == null) return;
        PushAction(new HeaderRowAction(table, table.HeaderRow, !table.HeaderRow), _page);

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
        PushAction(new CompositeAction(acts, "Add table column"), _page);
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
        PushAction(new CompositeAction(acts, "Add table row"), _page);
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
        PushAction(new CompositeAction(acts, "Delete table row"), _page);
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
        PushAction(new CompositeAction(acts, "Delete table column"), _page);
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

        // ---- images, decoded to pixels for embedding ----
        foreach (var sh in _page.Shapes)
        {
            if (sh.Kind != ShapeKind.Image || sh.ImagePath == null) continue;
            try
            {
                CanvasBitmap? bmp = _bitmaps.TryGetValue(sh.ImagePath, out var cached) ? cached : null;
                bmp ??= await CanvasBitmap.LoadAsync(_canvas, sh.ImagePath);
                // An image is the one shape kind FlattenShape cannot pre-turn — it
                // has no points to turn — so it carries its angle instead. The
                // centre comes from ShapeCenter, the same helper DrawShape rotates
                // about, so the export agrees with the canvas by construction.
                var ic = ShapeCenter(sh);
                images.Add(new PdfVectorImage(sh.X, sh.Y, Math.Max(1, sh.W), Math.Max(1, sh.H),
                    (int)bmp.SizeInPixels.Width, (int)bmp.SizeInPixels.Height, bmp.GetPixelBytes(),
                    sh.Rotation, ic.X, ic.Y));
            }
            catch { /* unreadable image: skip, ink still exports */ }
        }

        // ---- text boxes as selectable PDF text ----
        foreach (var t in _page.Texts)
        {
            // 25: THE EXPORTER TAKES THE SAME ANSWER THE SCREEN TOOK. It used to
            // compute one hardcoded black-or-white for the whole PAGE, outside
            // this loop, so every box on it exported the same colour whatever was
            // on the screen. TextInkFor is the one function the editor, the veil
            // and the Win2D raster also ask, so the file cannot disagree with the
            // canvas without the canvas being wrong too.
            string boxHex = ColorUtil.ToHex(TextInkFor(t));
            var logical = RtfRunParser.Parse(t.Rtf, 16f, "Lora");
            // wrap at the box width less the 4px inset DrawTextElement lays out with
            var visual = WrapRunLines(logical, Math.Max(60, t.Width) - 8);
            float prevSize = 16f;
            double baseline = t.Y + 16;
            // ONCE, outside the line loop. TextCentreWorld is the single answer the
            // renderer, the selection bounds, the click probe and the rotate sweep
            // all take for where a box's middle is; the exporter takes it too
            // rather than becoming a fifth. Taking it per line would give each line
            // its own pivot and fan the box open instead of turning it.
            var tc = TextCentreWorld(t);
            for (int li = 0; li < visual.Count; li++)
            {
                var line = visual[li];
                // a mixed-size line sits on the baseline its tallest run needs
                float size = line.Count > 0 ? line.Max(r => r.Size) : prevSize;
                prevSize = size;
                baseline += li == 0 ? size : size * 1.35;
                if (line.Count == 0) continue;
                // 25: THE COLOUR REACHES THE FILE. inkHex was one hardcoded
                // black-or-white for every box on the page, so a recoloured box
                // was perfect on screen and black in the PDF and the SVG both.
                // PdfVectorText.Color is what BOTH emitters read - PdfExporter
                // sets it as the rg/RG operand and HtmlSvgExporter as the text
                // element's fill - so this one substitution covers them both.
                texts.Add(new PdfVectorText(
                    (float)(t.X + 4), (float)baseline,
                    size, boxHex, string.Concat(line.Select(r => r.Text)), line[0].Font, line,
                    t.Rotation, tc.X, tc.Y));
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

    /// <summary>Greedy word wrap that respects run boundaries: each token measures
    /// with its own run's font, size and weight, so a heading-sized word and a
    /// body-sized word on one line both count for what they actually occupy.
    /// An empty inner list is a blank line and still consumes a baseline.</summary>
    private List<List<PdfVectorTextRun>> WrapRunLines(List<List<PdfVectorTextRun>> logical, double maxWidth)
    {
        var cache = new Dictionary<(string, float, string, bool, bool), float>();
        float Measure(string s, PdfVectorTextRun r)
        {
            if (string.IsNullOrEmpty(s)) return 0f;
            var key = (s, r.Size, r.Font, r.Bold, r.Italic);
            if (cache.TryGetValue(key, out float hit)) return hit;
            float w;
            try
            {
                using var fmt = new CanvasTextFormat
                {
                    FontFamily = string.IsNullOrEmpty(r.Font) ? "Lora" : r.Font,
                    FontSize = r.Size,
                    FontWeight = r.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    FontStyle = r.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
                };
                using var tl = new CanvasTextLayout(_canvas, s, fmt, float.MaxValue, float.MaxValue);
                w = (float)tl.LayoutBounds.Width;
            }
            catch { w = s.Length * r.Size * 0.5f; }   // unresolvable family: rough advance
            cache[key] = w;
            return w;
        }

        var outLines = new List<List<PdfVectorTextRun>>();
        foreach (var line in logical)
        {
            if (line.Count == 0) { outLines.Add(new List<PdfVectorTextRun>()); continue; }
            var visual = new List<PdfVectorTextRun>();
            var chunk = new System.Text.StringBuilder();
            double x = 0;
            foreach (var run in line)
            {
                chunk.Clear();
                foreach (var tok in WrapTokens(run.Text))
                {
                    float tw = Measure(tok, run);
                    // x > 0 guards a single over-long word from looping on empty lines
                    if (x + tw > maxWidth && x > 0)
                    {
                        if (chunk.Length > 0)
                        {
                            visual.Add(run with { Text = chunk.ToString().TrimEnd() });
                            chunk.Clear();
                        }
                        outLines.Add(visual);
                        visual = new List<PdfVectorTextRun>();
                        var head = tok.TrimStart();
                        chunk.Append(head);
                        x = Measure(head, run);
                    }
                    else
                    {
                        chunk.Append(tok);
                        x += tw;
                    }
                }
                if (chunk.Length > 0) visual.Add(run with { Text = chunk.ToString() });
            }
            outLines.Add(visual);
        }
        return outLines;
    }

    // word plus the whitespace that trails it, so breaks land between words
    private static IEnumerable<string> WrapTokens(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            yield return s[start..i];
        }
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
        LastTextBox = null;
        if (_page == null) return;
        foreach (var t in _page.Texts)
            BuildTextUi(t);
    }

    private static readonly System.Text.RegularExpressions.Regex UrlRx = new(
        @"(?i)\b(?:https?://|www\.)[^\s""]+|\b[a-z0-9-]+(?:\.[a-z0-9-]+)*\.(?:com|org|net|edu|gov|io|dev|app|co|me|de|it|tr|uk|fr|ai)(?:/[^\s]*)?\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Turns bare URLs into real, Ctrl+Click-able links (#20-batch3).
    private static void LinkifyBox(RichEditBox box)
    {
        try
        {
            box.Document.GetText(TextGetOptions.None, out string plain);
            if (plain.Length < 4) return;
            foreach (System.Text.RegularExpressions.Match m in UrlRx.Matches(plain))
            {
                var range = box.Document.GetRange(m.Index, m.Index + m.Length);
                if (!string.IsNullOrEmpty(range.Link)) continue;
                string url = m.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? m.Value : "https://" + m.Value;
                try { range.Link = "\"" + url + "\""; } catch { }
            }
        }
        catch { }
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
        // THE GRIP'S ✕ IS GONE (11.9). It was a 22 x 16 close button that
        // DELETED the box, revealed on GotFocus and hidden on LostFocus - which
        // is the exact visibility condition 11.9's quick-action bar now runs
        // under, and that bar carries a waste bin for the same command with a
        // mark that says so. Two affordances, one condition, one job.
        //
        // Keeping it would have been actively worse than redundant. The new bar
        // teaches red-X = "Cancel Editing", which keeps the text; a ✕ four DIP
        // away that throws the box away is a trap built by this change.
        //
        // And on a TABLE CELL it was already wrong: it pushed RemoveTextAction
        // on the cell's own TextElement, and the LostFocus guard a few lines
        // below records what that costs - "an empty TABLE CELL is normal -
        // discarding it deletes the cell's TextElement and leaves the cell
        // untypeable forever (#cellfix)". The cell is where the bar deliberately
        // does not appear, so nothing replaces it there; nothing should.

        // rotate handle: drag left/right to spin the box, like image rotation (#38).
        // A real-sized hit target (the old bare 11px glyph was nearly impossible
        // to grab — misses fell through to the grip and moved the box, #11-batch2).
        var rotate = new TextBlock
        {
            Text = "⟳",
            FontSize = 11,
            Width = 34,
            Height = 16,
            TextAlignment = TextAlignment.Center,
            // Was 24, which was the width of the ✕ this rotate handle used to sit
            // to the left of. That button is gone (see above), so the reservation
            // went with it - a 24 DIP gap held open for a control that no longer
            // exists is exactly the stale artefact a removal leaves behind.
            Margin = new Thickness(0, 0, 4, 0),
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
        Grid.SetRow(grip, 0);

        var box = new RichEditBox
        {
            MinWidth = 160,
            MinHeight = 40,
            FontSize = PendingFontSize,
            FontFamily = new FontFamily(PendingFontFamily),
            TextWrapping = TextWrapping.Wrap,
            IsSpellCheckEnabled = true,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        // Ink colour follows the PAGE background, not the app theme, so flipping
        // light/dark mode no longer recolours notes and new text is readable on
        // any page (#8/#16-batch3).
        // 25.4: the box's OWN colour when it has one; otherwise the page's ink
        // convention, through the one shared helper. Named boxInk rather than
        // pageInk since 25 - it is no longer always the page's answer, and a
        // name that says otherwise is how the next reader gets it wrong.
        var boxInk = TextInkFor(t);
        box.Foreground = new SolidColorBrush(boxInk);
        // 2.2: and make that Foreground, and the Transparent Background above,
        // survive WinUI's own visual states. Without this the editor paints
        // #606060 under #141413 on white paper (2.93:1) and #FFFFFF on re-open.
        PinEditorBrushes(box, boxInk);
        try
        {
            var dcf = box.Document.GetDefaultCharacterFormat();
            dcf.ForegroundColor = boxInk;
            box.Document.SetDefaultCharacterFormat(dcf);
            // headroom for script fonts whose swashes overshoot the em box —
            // Amsterdam-style faces were getting clipped (#17-batch3)
            var dpf = box.Document.GetDefaultParagraphFormat();
            dpf.SetLineSpacing(Microsoft.UI.Text.LineSpacingRule.Multiple, 1.25f);
            box.Document.SetDefaultParagraphFormat(dpf);
        }
        catch { }
        box.Padding = new Thickness(6, 8, 6, 10);

        if (!string.IsNullOrEmpty(t.Rtf))
        {
            try { box.Document.SetText(TextSetOptions.FormatRtf, t.Rtf); } catch { }
        }
        // 25.2: THE FIELD WINS OVER THE RTF, AND THIS IS WHERE THAT IS DECIDED.
        // The RTF carries a colour of its own - Quill writes the page ink into
        // the default character format, so it comes back as a colortbl plus cf1 -
        // and SetText above has just restored it. A box that has been given a
        // colour therefore has to be stamped AFTER the words are in, or the RTF's
        // stale colour would beat the field on screen while the canvas and both
        // exporters used the field. That split is the whole defect 25 closes.
        //
        // Only when the box HAS a colour. A box that has never been given one is
        // left exactly as it was, so the per-run colours the format bar's own
        // picker can set still show, and no existing note changes.
        if (t.TextColor is { Length: > 0 }) StampTextColour(box, boxInk);

        // table cells: no drag grip, and the box must not spill past its row (#24-batch3)
        bool isCell = t.TableId != null;
        if (isCell)
        {
            box.Width = t.Width;
            grip.Height = 0;
            box.MinHeight = 20;
            var cellTable = _page?.Shapes.FirstOrDefault(sh => sh.Id == t.TableId);
            if (cellTable != null)
            {
                var rhs = TableRowHeights(cellTable);
                int rr = Math.Clamp(t.TableRow, 0, rhs.Length - 1);
                int rspan = Math.Clamp(Math.Max(1, t.CellRowSpan), 1, rhs.Length - rr);
                double spanH = 0;
                for (int i = 0; i < rspan; i++) spanH += rhs[rr + i];
                box.MaxHeight = Math.Max(22, spanH - 4);   // span-aware: merged cells cap at ALL spanned rows (B3)
                box.MinHeight = Math.Max(20, spanH - 4);   // empty cell's hit target fills the row (B3)
                if (Math.Abs(cellTable.Rotation) > 0.01)
                {
                    // ride the table's rotation (#tablerot): rotate the cell
                    // origin about the table centre, then spin the box itself
                    double ang = cellTable.Rotation * Math.PI / 180.0;
                    double tcx = cellTable.X + cellTable.W / 2, tcy = cellTable.Y + cellTable.H / 2;
                    double dxr = t.X - tcx, dyr = t.Y - tcy;
                    double rx = tcx + dxr * Math.Cos(ang) - dyr * Math.Sin(ang);
                    double ry = tcy + dxr * Math.Sin(ang) + dyr * Math.Cos(ang);
                    container.RenderTransformOrigin = new Point(0, 0);
                    container.RenderTransform = new CompositeTransform
                    { Rotation = cellTable.Rotation, TranslateX = rx - t.X, TranslateY = ry - t.Y };
                }
            }
            // rows grow with the content, Word-style (B1)
            box.TextChanged += (_, _) => AutoGrowCellRow(t, box);
            // Undo: one coalesced TableLayoutAction per focus session, not one
            // per keystroke (B1). Snapshot on GotFocus, push the delta on
            // LostFocus — this handler is attached BEFORE any FlushTexts-
            // triggered rebuild can run in the same LostFocus chain (spec D).
            List<double>? rowSnap = null;
            double hSnap = 0;
            box.GotFocus += (_, _) =>
            {
                if (cellTable != null) { rowSnap = TableRowHeights(cellTable).ToList(); hSnap = cellTable.H; }
            };
            box.LostFocus += (_, _) =>
            {
                if (cellTable == null || rowSnap == null || _page == null) return;
                var snap = rowSnap;
                rowSnap = null;
                var cur = TableRowHeights(cellTable).ToList();
                bool grew = Math.Abs(cellTable.H - hSnap) > 0.5;
                if (!grew)
                    for (int i = 0; i < cur.Count && i < snap.Count; i++)
                        if (Math.Abs(cur[i] - snap[i]) > 0.5) { grew = true; break; }
                if (!grew) return;
                var colW = TableColWidths(cellTable).ToList();
                PushAction(new TableLayoutAction(cellTable,
                    colW, snap, cellTable.W, hSnap,
                    colW, cur, cellTable.W, cellTable.H), _page, alreadyDone: true);
            };
            // an overflowing cell may still scroll internally while edited —
            // safety net for content that outruns a mid-typing measurement (B3)
            box.GotFocus += (_, _) => ScrollViewer.SetVerticalScrollMode(box, ScrollMode.Enabled);
            box.LostFocus += (_, _) => ScrollViewer.SetVerticalScrollMode(box, ScrollMode.Disabled);
        }
        else
        {
            // Free bubbles start at the familiar width and GROW WITH THE TEXT,
            // up to a ceiling of half the physical screen but never past the
            // app-window edge. The ceiling is snapshotted once per box, so a
            // later window resize only affects NEW boxes (#15). RichEditBox
            // does not reliably auto-size horizontally in wrap mode, so the
            // width is measured explicitly from the text on every change.
            // Height auto-sizes for EVERY free bubble — pinned and legacy
            // fixed-width boxes included (A1); their width phase is skipped
            // inside AutoSizeBubble.
            var sizeState = new BubbleSizeState();
            if (t.WidthPinned)
            {
                box.Width = t.Width;   // the user dragged the width grip — keep it
            }
            else if (t.AutoWidth)
            {
                if (t.MaxWidth <= 0 || t.MaxWidth < 200) t.MaxWidth = ComputeBubbleMaxWidth(t.X);
                box.MaxWidth = t.MaxWidth;
                box.MinWidth = Math.Min(t.MaxWidth, 200);
            }
            else
            {
                box.Width = t.Width;   // pre-feature boxes never re-wrap (#15)
            }
            AutoSizeBubble(t, box, sizeState);                     // size to current content
            box.TextChanged += (_, _) => AutoSizeBubble(t, box, sizeState);
            box.Loaded += (_, _) => AutoSizeBubble(t, box, sizeState);   // re-measure once real layout exists
        }

        // The RichEditBox's inner ScrollViewer grabbed the wheel through direct
        // manipulation, so the routed event never surfaced: bubbles dead-ended
        // scrolling entirely, and text mode paid DManip's ~1 s scroll-chaining
        // delay (#13-batch4). Free bubbles auto-grow and never scroll inside —
        // disable the inner ScrollViewer and hand every wheel to the canvas.
        ScrollViewer.SetVerticalScrollMode(box, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollMode(box, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Hidden);
        ScrollViewer.SetZoomMode(box, ZoomMode.Disabled);
        container.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, we) =>
        {
            OnPointerWheel(_canvas, we);
            we.Handled = true;
        }), true);

        // Ctrl+Click opens links (#20-batch3)
        box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, pe) =>
        {
            if (!pe.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
            try
            {
                var pt = pe.GetCurrentPoint(box).Position;
                var linkRange = box.Document.GetRangeFromPoint(pt, Microsoft.UI.Text.PointOptions.ClientCoordinates);
                linkRange.Expand(Microsoft.UI.Text.TextRangeUnit.Link);
                var link = linkRange.Link;
                if (!string.IsNullOrEmpty(link))
                {
                    _ = Windows.System.Launcher.LaunchUriAsync(new Uri(link.Trim('"')));
                    pe.Handled = true;
                }
            }
            catch { }
        }), true);

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
            LastTextBox = box;
            ActiveTextChanged?.Invoke(box);
        };
        box.TextChanged += (_, _) =>
        {
            // typing never moves ink: raise the change without nuking the
            // ink cache, which stuttered text entry on big pages (#13-batch4)
            _inkCacheTextOnly = true;
            try { ContentChanged?.Invoke(); } finally { _inkCacheTextOnly = false; }
        };
        box.LostFocus += (_, _) =>
        {
            if (_page == null) return;
            // an empty TABLE CELL is normal — discarding it deletes the cell's
            // TextElement and leaves the cell untypeable forever (#cellfix)
            if (t.TableId != null) return;
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
                if (ReferenceEquals(LastTextBox, box)) LastTextBox = null;
                // targeted teardown (A2): a full RebuildTextLayer here steals
                // focus from the box the user just tapped into
                _textLayer.Children.Remove(container);
                _textUi.Remove(t.Id);
                ContentChanged?.Invoke();
            }
        };

        var rGrip = new Border
        {
            Width = 11,
            Background = new SolidColorBrush(Color.FromArgb(110, Accent.R, Accent.G, Accent.B)),
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
            gripBrush.Color = Color.FromArgb(60, Accent.R, Accent.G, Accent.B);
            dots.Visibility = Visibility.Visible;
            rotate.Visibility = Visibility.Visible;
            rGrip.Visibility = t.TableId == null ? Visibility.Visible : Visibility.Collapsed;
        };
        box.LostFocus += (_, _) =>
        {
            gripBrush.Color = Colors.Transparent;
            dots.Visibility = Visibility.Collapsed;
            rotate.Visibility = Visibility.Collapsed;
            rGrip.Visibility = Visibility.Collapsed;
            LinkifyBox(box);   // bare URLs become real links on commit (#20-batch3)
            // release active status once focus has truly left, so the format
            // bar stops lingering (#19-batch3). Format-bar buttons don't steal
            // focus, so this only fires for real focus moves.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(ActiveTextBox, box) && box.FocusState == FocusState.Unfocused)
                {
                    ActiveTextBox = null;
                    ActiveTextChanged?.Invoke(null);
                }
            });
        };
        rGrip.ManipulationMode = ManipulationModes.TranslateX;
        rGrip.ManipulationDelta += (_, e) =>
        {
            double cur = double.IsNaN(box.Width) ? box.ActualWidth : box.Width;
            box.Width = Math.Max(120, cur + e.Delta.Translation.X);
            // wrap changes must be visible DURING the drag (spec D)
            if (t.TableId == null) AutoSizeBubbleHeight(t, box);
        };
        rGrip.ManipulationCompleted += (_, _) =>
        {
            t.Width = box.Width;
            t.WidthPinned = true;   // an explicit drag opts out of auto-sizing (#15-batch4)
            t.AutoWidth = false;
            ContentChanged?.Invoke();
        };
        container.Children.Add(rGrip);

        double startX = 0, startY = 0;
        grip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        grip.ManipulationStarted += (_, e) =>
        {
            if (rotating) { e.Complete(); return; }   // rotate wins; never also move (#11-batch2)
            startX = Canvas.GetLeft(container);
            startY = Canvas.GetTop(container);
        };
        grip.ManipulationDelta += (_, e) =>
        {
            if (rotating) return;
            // deltas are reported in the grip's local space, which is already
            // world units (the text layer's RenderTransform maps screen->world)
            Canvas.SetLeft(container, Canvas.GetLeft(container) + e.Delta.Translation.X);
            Canvas.SetTop(container, Canvas.GetTop(container) + e.Delta.Translation.Y);
            // 11.9: the quick actions ride above this box. A Canvas.Left change
            // fires no SizeChanged, so the drag says so itself. The position is
            // already set above, so the bounds this reads are the new ones.
            RaiseEditingGeometry(box);
        };
        grip.ManipulationCompleted += (_, _) =>
        {
            if (rotating || _page == null) return;
            double nx = Canvas.GetLeft(container), ny = Canvas.GetTop(container);
            if (Math.Abs(nx - startX) > 0.5 || Math.Abs(ny - startY) > 0.5)
            {
                PushAction(new MoveTextAction(t, startX, startY, nx, ny), _page);
                ContentChanged?.Invoke();
            }
        };

        // 11.9: the quick-action bar is placed off this container's world rect,
        // so it has to move when the container does. SizeChanged rather than the
        // box's TextChanged, because AutoSizeBubble sets Width and Height and
        // ActualWidth does not follow until layout has run - a bar placed from
        // the pre-layout size lags the bubble by a frame on every keystroke.
        container.SizeChanged += (_, _) => RaiseEditingGeometry(box);

        _textLayer.Children.Add(container);
        _textUi[t.Id] = (container, box);
    }

    // Whether an equation bitmap's ink is dark — sampled once per image and
    // cached, so the draw-time invert knows when the page and ink clash (#eq).
    private readonly Dictionary<string, bool> _eqInkDark = new();
    private bool EquationInkIsDark(string path, CanvasBitmap bmp)
    {
        if (_eqInkDark.TryGetValue(path, out bool dark)) return dark;
        try
        {
            var px = bmp.GetPixelColors();
            long lum = 0, n = 0;
            for (int i = 0; i < px.Length; i += 7)   // sparse sample is plenty
            {
                if (px[i].A < 128) continue;
                lum += (px[i].R * 3 + px[i].G * 6 + px[i].B) / 10;
                n++;
            }
            dark = n == 0 || lum / Math.Max(1, n) < 128;
        }
        catch { dark = true; }
        _eqInkDark[path] = dark;
        return dark;
    }

    public PenStroke? HitStroke(Vector2 pos, float tol)
    {
        if (_page == null) return null;
        var cand = StrokeCandidates(pos.X - tol, pos.Y - tol, pos.X + tol, pos.Y + tol);
        foreach (var s in _page.Strokes)
        {
            if (cand != null && !cand.Contains(s)) continue;
            // cheap bbox reject before the per-point scan (FindStrokeNear has
            // always done this; HitStroke was the one path that didn't)
            s.GetBounds(out float mnX, out float mnY, out float mxX, out float mxY);
            if (pos.X < mnX - tol || pos.X > mxX + tol || pos.Y < mnY - tol || pos.Y > mxY + tol) continue;
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

                    PushAction(new TableLayoutAction(shape, oldColW, rowH, shape.W, shape.H, colW, rowH, newW, shape.H), _page);
                    ReflowTableCells(shape);
                    _canvas.Invalidate();
                    ContentChanged?.Invoke();
                    e.Handled = true;
                    return;
                }
                if (row > 0)
                {
                    // row-fit, mirroring the column-fit above (B1): shrink/grow
                    // row row-1 to the max content height of its cells
                    int targetRow = row - 1;
                    double maxCellH = 0;
                    foreach (var text in _page.Texts)
                    {
                        if (text.TableId != shape.Id) continue;
                        if (targetRow < text.TableRow || targetRow >= text.TableRow + Math.Max(1, text.CellRowSpan)) continue;
                        double cellH = 0;
                        if (_textUi.TryGetValue(text.Id, out var ui))
                        {
                            double contentH = MeasureContentHeight(ui.Box);
                            if (contentH > 0) cellH = contentH + ui.Box.Padding.Top + ui.Box.Padding.Bottom + 4;
                        }
                        else
                        {
                            string txt = StripRtf(text.Rtf);
                            if (!string.IsNullOrEmpty(txt))
                            {
                                using var layout = new CanvasTextLayout(_canvas, txt,
                                    new CanvasTextFormat { FontSize = 16f },
                                    (float)Math.Max(24, text.Width), 10000);
                                cellH = layout.LayoutBounds.Height + 22;
                            }
                        }
                        // a row-span-merged cell asks this row only for its share
                        int spanR = Math.Max(1, text.CellRowSpan);
                        maxCellH = Math.Max(maxCellH, cellH / spanR);
                    }

                    double newRowH = Math.Clamp(maxCellH, 24, 800);
                    var colW2 = TableColWidths(shape).ToList();
                    var rowH2 = TableRowHeights(shape).ToList();
                    var oldRowH = rowH2.ToList();

                    double diff = newRowH - rowH2[targetRow];
                    if (Math.Abs(diff) < 0.5) { e.Handled = true; return; }
                    rowH2[targetRow] = newRowH;
                    double newH = shape.H + diff;

                    PushAction(new TableLayoutAction(shape, colW2, oldRowH, shape.W, shape.H, colW2, rowH2, shape.W, newH), _page);
                    ReflowTableCells(shape);
                    _canvas.Invalidate();
                    ContentChanged?.Invoke();
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Flattens a page to PNG bytes off the UI thread. <paramref name="cropToContent"/>
    /// frames the drawn content instead of the page origin — what a gallery cover
    /// wants, since a wide cover strip of an empty page corner shows nothing.
    /// Returns null when there is nothing to draw, so callers can fall back.
    /// </summary>
    public static byte[]? RenderPageThumbnail(NotePage page, int targetWidth, int targetHeight, bool cropToContent = false, Color? backgroundOverride = null)
    {
        var device = CanvasDevice.GetSharedDevice();
        if (device == null) return null;
        try
        {
            double srcX = 0, srcY = 0, srcW = 1200, srcH = 900;
            if (cropToContent && !TryContentBounds(page, targetWidth, targetHeight,
                                                   out srcX, out srcY, out srcW, out srcH))
                return null;

            using var rt = new CanvasRenderTarget(device, targetWidth, targetHeight, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                // A cover thumbnail composites the ink OVER the notebook's identity
                // colour, not the page's own (usually dark) background, so the card
                // reads as "notebook colour with a preview of ink on it" (#coverfix).
                var bg = backgroundOverride ?? PaperTextures.Ground(page.Paper, ColorUtil.Parse(page.Background));
                ds.Clear(bg);

                // With an override the page's ink can vanish against the notebook
                // colour, so pick ONE contrast ink from the fill's luminance and
                // draw the whole preview in it; the true page keeps its own colours.
                Color? forceInk = backgroundOverride is Color ob
                    ? ((0.299 * ob.R + 0.587 * ob.G + 0.114 * ob.B) / 255.0 > 0.5
                        ? Color.FromArgb(255, 0x1B, 0x1A, 0x18)
                        : Color.FromArgb(255, 0xF4, 0xF2, 0xEC))
                    : (Color?)null;

                // content framing fits exactly; the origin framing letterboxes
                float scale = cropToContent
                    ? (float)(targetWidth / srcW)
                    : Math.Min((float)(targetWidth / srcW), (float)(targetHeight / srcH));
                ds.Transform = Matrix3x2.CreateTranslation((float)-srcX, (float)-srcY) *
                               Matrix3x2.CreateScale(scale);

                foreach (var sh in page.Shapes)
                {
                    var color = forceInk ?? ColorUtil.Parse(sh.Color);
                    ds.DrawRectangle(new Rect(sh.X, sh.Y, Math.Max(1, sh.W), Math.Max(1, sh.H)), color, Math.Max(1f, sh.Size));
                }

                foreach (var s in page.Strokes)
                {
                    var color = forceInk ?? ColorUtil.Parse(s.Color);
                    for (int i = 1; i < s.Points.Count; i++)
                    {
                        ds.DrawLine(new Vector2(s.Points[i - 1].X, s.Points[i - 1].Y), new Vector2(s.Points[i].X, s.Points[i].Y), color, s.Size);
                    }
                }

                // Text carries no colour of its own here — the RTF run colours are
                // dropped by StripRtf — so contrast against the page background.
                double lum = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
                var textCol = forceInk ?? (lum > 0.5
                    ? Color.FromArgb(255, 0x1B, 0x1A, 0x18)
                    : Color.FromArgb(255, 0xF4, 0xF2, 0xEC));
                foreach (var t in page.Texts)
                {
                    string txt = StripRtf(t.Rtf);
                    if (string.IsNullOrEmpty(txt)) continue;
                    using var layout = new CanvasTextLayout(device, txt,
                        new CanvasTextFormat { FontSize = 16f },
                        (float)Math.Max(24, t.Width), 4000);
                    ds.DrawTextLayout(layout, (float)t.X, (float)t.Y, textCol);
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

    // Union of everything drawn on the page, padded and then grown to the
    // target's aspect ratio so the fit is exact without cropping. Growing in
    // height anchors the top: notes read downward, so the top is what matters.
    private static bool TryContentBounds(NotePage page, int targetWidth, int targetHeight,
                                         out double x, out double y, out double w, out double h)
    {
        x = y = w = h = 0;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        void Add(double ax, double ay, double bx, double by)
        {
            if (ax < x0) x0 = ax;
            if (ay < y0) y0 = ay;
            if (bx > x1) x1 = bx;
            if (by > y1) y1 = by;
        }

        foreach (var s in page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            s.GetBounds(out float a, out float b, out float c, out float d);
            float pad = s.Size * 0.5f;
            Add(a - pad, b - pad, c + pad, d + pad);
        }
        foreach (var sh in page.Shapes)
            Add(Math.Min(sh.X, sh.X + sh.W), Math.Min(sh.Y, sh.Y + sh.H),
                Math.Max(sh.X, sh.X + sh.W), Math.Max(sh.Y, sh.Y + sh.H));
        foreach (var t in page.Texts)
        {
            if (string.IsNullOrEmpty(StripRtf(t.Rtf))) continue;
            Add(t.X, t.Y, t.X + Math.Max(24, t.Width), t.Y + 48);
        }

        if (x1 <= x0 || y1 <= y0) return false;

        double bw = x1 - x0, bh = y1 - y0;
        double pad2 = Math.Max(12, Math.Max(bw, bh) * 0.03);
        x0 -= pad2; y0 -= pad2; bw += pad2 * 2; bh += pad2 * 2;

        double ta = (double)targetWidth / Math.Max(1, targetHeight);
        if (bw / bh < ta) { double nw = bh * ta; x0 -= (nw - bw) / 2; bw = nw; }
        else bh = bw / ta;

        x = x0; y = y0; w = bw; h = bh;
        return true;
    }

    private bool TryDrawInkCache(CanvasDrawingSession ds, ICanvasResourceCreator sender,
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
                // build around the FULL viewport — under the virtual control the
                // vx params describe one invalidated region, which may be tiny
                var ftl = ToWorld(new Vector2(0, 0));
                var fbr = ToWorld(new Vector2((float)ActualWidth, (float)ActualHeight));
                double vw = Math.Max(64, fbr.X - ftl.X), vh = Math.Max(64, fbr.Y - ftl.Y);
                var world = new Rect(ftl.X - vw, ftl.Y - vh, vw * 3, vh * 3);
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

    private bool _inkCacheTextOnly;
}
