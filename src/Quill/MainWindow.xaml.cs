using System.Runtime.InteropServices.WindowsRuntime;
using Quill.Controls;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using VKey = Windows.System.VirtualKey;
using VMod = Windows.System.VirtualKeyModifiers;

namespace Quill;

public sealed partial class MainWindow : Window
{
    // Async library load, phase 2 (#roadmap): the window is built around this
    // instance BEFORE library.json has been parsed, and the loaded state is
    // adopted INTO it (LibraryStore.AdoptInPlace) so no handler has to re-bind.
    // Until then it only carries the cheap startup hints from settings.json.
    private readonly Library _library = new();
    // false until a real, successfully loaded library has been adopted. Every
    // path that can write checks it, so a failed load can never reach the file.
    private bool _libraryReady;
    private Notebook? _curNb;
    private Section? _curSec;
    private NotePage? _curPage;
    private TreeViewNode? _selNode;
    private Guid? _activePresetId;
    // Notebooks unlocked for this session (#23).
    private readonly HashSet<Guid> _unlockedNotebooks = new();

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private Button? _eraserChip;
    private string _lastZoomPct = "100%";
    private bool _calcReady;
    private long _progValue;
    private bool _progHasValue;
    private double _gxMin = -10, _gxMax = 10;
    private readonly Microsoft.Graphics.Canvas.Text.CanvasTextFormat _graphLabelFormat = new() { FontSize = 10 };

    private bool _backdropActive;
    private bool _syncingUi;
    private bool _syncingSize;
    private bool _uiHidden;
    private bool _floatPen;
    // true right after a drag on the minimal-UI cluster/tab, so the Button.Click
    // that a WinUI manipulation gesture leaves behind on release doesn't also fire.
    private bool _minimalButtonsDragged;
    // true while the minimal-UI cluster is tucked away as a small edge tab.
    private bool _minimalDocked;
    private bool _minimalDockedLeft;
    private bool _minimalDockedTop = true;
    // true only when the hide-all button is what entered full screen, so restore
    // won't pull the user out of a full screen they set up themselves.
    private bool _hideEnteredFullscreen;
    // Gallery / start-screen state: which notebook is being browsed (null = the
    // notebook grid) and whether the gallery is acting as the startup picker (#31).
    private Notebook? _galleryNb;
    private bool _galleryLauncher;
    private readonly Quill.Services.AudioRecorder _audioRecorder = new Quill.Services.AudioRecorder();
    private readonly Quill.Services.DictationService _dictation = new();
    private readonly Quill.Services.AudioPlayer _audioPlayer = new Quill.Services.AudioPlayer();
    private bool _updatingAudioSlider = false;

    private static readonly string[] QuickColors =
        { "#141413", "#FAF9F5", "#D97757", "#D32F2F", "#FBC02D", "#788C5D", "#6A9BCC", "#7B1FA2" };

    private static readonly string[] Fonts =
    {
        "Lora", "Poppins", "Segoe UI", "Segoe Print", "Segoe Script", "Ink Free",
        "Calibri", "Cambria", "Cambria Math", "Georgia", "Times New Roman", "Garamond",
        "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Comic Sans MS",
        "Consolas", "Cascadia Code", "Cascadia Mono", "Courier New", "JetBrains Mono",
        "Space Mono", "Google Sans Mono", "Maple Mono", "Maple Mono NF CN", "Amsterdam Handwriting"
    };

    private static readonly string[] FontSizes =
        { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "40", "48", "64", "72" };

    private static readonly string[] MathSymbols =
    {
        "×","÷","±","∓","≤","≥","≠","≈","≡","∝","∞","√","∛","∑","∏","∫","∬","∮","∂","∇",
        "Δ","π","θ","α","β","γ","δ","ε","ζ","η","λ","μ","ν","ξ","ρ","σ","τ","φ","χ","ψ",
        "ω","Γ","Θ","Λ","Ξ","Π","Σ","Φ","Ψ","Ω","∈","∉","⊂","⊆","⊄","∪","∩","∅","∧","∨",
        "¬","⊕","⊗","⇒","⇐","⇔","→","←","↔","↦","∀","∃","∄","∴","∵","ℝ","ℕ","ℤ","ℚ","ℂ",
        "°","′","″","‰","⋅","∘","ℏ","ℓ","Å","µ"
    };

    public MainWindow()
    {
        InitializeComponent();
        Title = "Quill";
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch { /* icon is best-effort */ }

        // real window-level glass on Windows 11 (Mica preferred, acrylic next);
        // Windows 10 silently keeps the opaque surface (#glass-roadmap)
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            { SystemBackdrop = new MicaBackdrop(); _backdropActive = true; }
            else if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported())
            { SystemBackdrop = new DesktopAcrylicBackdrop(); _backdropActive = true; }
        }
        catch { }

        // The library is still parsing on a worker thread. settings.json carries
        // a mirror of its theme, accent and window placement, so the window can
        // open in the right skin at the right size instead of snapping when the
        // real library lands; FinishStartup does everything else (#roadmap).
        ApplyStartupHints();

        BuildFontItems();
        SizeCombo.ItemsSource = FontSizes;
        SymbolGrid.ItemsSource = MathSymbols;
        BuildStyleItems();
        CalcModeBox.ItemsSource = new[] { "Standard", "Scientific", "Graphing", "Programmer", "Converter" };
        ProgBase.ItemsSource = new[] { "HEX", "DEC", "OCT", "BIN" };
        ConvCat.ItemsSource = ConvCategories;
        CalcModeBox.SelectedIndex = 1;
        ProgBase.SelectedIndex = 1;
        ConvCat.SelectedIndex = 0;
        _calcReady = true;
        ApplyCalcMode();
        BuildCalcButtons();
        FillConvUnits();
        // calc history and variables live in the library — restored in FinishStartup

        Surface.TitleClicked += async () => await RenamePageFromTitleAsync();
        Surface.DateClicked += async () => await EditPageDateAsync();

        // notebooks panel: resize grip (the saved size is restored in FinishStartup)
        NbGrip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        NbGrip.ManipulationDelta += (_, e) =>
        {
            double w = double.IsNaN(NotebookPanel.Width) ? NotebookPanel.ActualWidth : NotebookPanel.Width;
            double h = double.IsNaN(NotebookPanel.Height) ? NotebookPanel.ActualHeight : NotebookPanel.Height;
            NotebookPanel.Width = Math.Clamp(w + e.Delta.Translation.X, 220, 560);
            NotebookPanel.Height = Math.Clamp(h + e.Delta.Translation.Y, 240, 940);
        };
        NbGrip.ManipulationCompleted += (_, _) =>
        {
            _library.NotebookPanelW = NotebookPanel.Width;
            _library.NotebookPanelH = NotebookPanel.Height;
            ScheduleSave();
        };

        HookAccessibilitySettings();
        // Equip panels with the comet rim runner (PenRow is the user's focus;
        // TopBar + NotebookPanel are cheap to include). ApplyGlowMode below
        // switches them on only when GlowMode == "Comet".
        AttachCometRim(PenRow);
        AttachCometRim(NotebookPanel);   // floating panels only — never the docked top bar
        // the rims stay idle until FinishStartup reads the saved GlowMode

        // nothing that escapes the canvas row (unclipped text-box overlays) may
        // paint over or steal clicks from the top bar (#3-batch2)
        CanvasArea.SizeChanged += (_, _) =>
        {
            CanvasArea.Clip = new RectangleGeometry { Rect = new Rect(0, 0, CanvasArea.ActualWidth, CanvasArea.ActualHeight) };
        };

        Surface.ContentChanged += ScheduleSave;
        // one-shot blank space (#7-batch2): after the free-space drag commits,
        // hand the pen straight back.
        Surface.ContentChanged += () =>
        {
            if (_blankSpaceOnce)
            {
                _blankSpaceOnce = false;
                DispatcherQueue.TryEnqueue(() => SelectTool("Pen"));
            }
        };
        Surface.ActiveTextChanged += box => { UpdateFormatBarVisibility(); SyncSizeComboFromSelection(box); };
        Surface.ContextMenuRequested += ShowCanvasContextMenu;
        Surface.CommentActivated += ShowCommentFlyout;
        Surface.ReplayEnded += () => BtnReplay.IsChecked = false;
        Surface.UndoManager.Changed += UpdateUndoButtons;
        Surface.ViewChanged += OnViewChanged;
        Surface.RulerAngleChanged += OnRulerAngleChanged;
        Surface.StrokeTapped += stroke => SeekAudioToStroke(stroke);
        _audioRecorder.ElapsedChanged += elapsed => { DispatcherQueue.TryEnqueue(() => AudioTimeText.Text = elapsed.ToString(@"m\:ss")); };
        // when playback finishes, restore the play icon and un-hide the ink (#55)
        _audioPlayer.PlaybackEnded += () => DispatcherQueue.TryEnqueue(() =>
        {
            AudioPlayIcon.Glyph = "";   // back to the play icon
            Surface.AudioPlayheadPosition = null;
            Surface.Refresh();
            AudioSlider.Value = 0;
            AudioTimeText.Text = "0:00";
        });
        _audioPlayer.PositionChanged += pos => { DispatcherQueue.TryEnqueue(() => UpdateAudioPlayerPosition(pos)); };

        // dictation (#26-batch2): finalised speech segments type into the focused
        // text box, or a fresh text box is created at the view centre
        _dictation.TextRecognized += text => DispatcherQueue.TryEnqueue(() =>
        {
            // pressing the mic button steals focus, so also accept the last
            // focused box; a tapped-but-empty Text caret becomes the target
            // instead of a bubble in the middle of the screen (#5-batch4)
            var box = Surface.ActiveTextBox ?? Surface.LastTextBox;
            if (box == null && Surface.HasPendingText) box = Surface.MaterializePendingText();
            if (box != null)
            {
                try { box.Document.Selection.TypeText(text + " "); } catch { }
            }
            else
            {
                var centre = Surface.ScreenToWorld(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2));
                var (df, ds2) = EffectiveTextDefaults();
                Surface.AddTextElement(centre.X - 160, centre.Y - 20, 360,
                    PlainToRtf(text, df, ds2, ContrastHexForPage()));
            }
        });
        _dictation.Stopped += () => DispatcherQueue.TryEnqueue(() =>
        {
            BtnDictate.IsChecked = false;
            ShowStatus("Dictation stopped.");
        });

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); FadeOut(StatusText, 220); };
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); FadeOut(ZoomBorder, 180); };
        Closed += (_, _) => { CaptureWindowPlacement(); SaveNow(); LibraryStore.Flush(); };

        // pen panel dragging -> dock to an edge
        PenGrip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        PenGrip.ManipulationStarted += (_, _) => BeginCheapDrag(PenRow);
        PenGrip.ManipulationDelta += (_, e) => JellyDrag(PenRow, e);
        PenGrip.ManipulationCompleted += (_, _) => { EndCheapDrag(PenRow); JellyRelease(PenRow); DockPenRowFromPosition(); };

        // minimal-UI floating buttons: drag the cluster itself; snap to nearest corner,
        // or dock to a side edge as a small pull-out tab (#30, #dock). A manipulation
        // and the inner Button's own Click are separate WinUI gesture pipelines, so
        // dragging over a button still let its Click fire on release; guard it with a
        // flag that's only cleared at the START of the next press (handledEventsToo:
        // true so it still resets even though Button marks PointerPressed handled).
        MinimalButtons.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => _minimalButtonsDragged = false), true);
        MinimalButtons.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        MinimalButtons.ManipulationStarted += (_, _) => { _minimalButtonsDragged = true; BeginCheapDrag(MinimalButtons); };
        MinimalButtons.ManipulationDelta += (_, e) => JellyDrag(MinimalButtons, e);
        MinimalButtons.ManipulationCompleted += (_, _) => { EndCheapDrag(MinimalButtons); SnapMinimalButtons(); };

        MinimalButtonsTab.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        MinimalButtonsTab.ManipulationStarted += (_, _) => BeginCheapDrag(MinimalButtonsTab);
        MinimalButtonsTab.ManipulationDelta += (_, e) => JellyDrag(MinimalButtonsTab, e);
        MinimalButtonsTab.ManipulationCompleted += (_, _) => { EndCheapDrag(MinimalButtonsTab); TryUndockFromDrag(); };

        // calculator window dragging (with liquid jelly physics, #48)
        CalcHeader.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        CalcHeader.ManipulationStarted += (_, _) => BeginCheapDrag(CalcPanel);
        CalcHeader.ManipulationDelta += (_, e) => JellyDrag(CalcPanel, e);
        CalcHeader.ManipulationCompleted += (_, _) => { EndCheapDrag(CalcPanel); JellyRelease(CalcPanel); };

        // AI chat panel dragging
        AiHeader.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        AiHeader.ManipulationStarted += (_, _) => BeginCheapDrag(AiPanel);
        AiHeader.ManipulationDelta += (_, e) => JellyDrag(AiPanel, e);
        AiHeader.ManipulationCompleted += (_, _) => { EndCheapDrag(AiPanel); JellyRelease(AiPanel); };

        // audio recording panel dragging (with liquid jelly physics)
        AudioFloatingPanel.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        AudioFloatingPanel.ManipulationStarted += (_, _) => BeginCheapDrag(AudioFloatingPanel);
        AudioFloatingPanel.ManipulationDelta += (_, e) => JellyDrag(AudioFloatingPanel, e);
        AudioFloatingPanel.ManipulationCompleted += (_, _) => { EndCheapDrag(AudioFloatingPanel); JellyRelease(AudioFloatingPanel); };

        AudioGrip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        AudioGrip.ManipulationStarted += (_, _) => BeginCheapDrag(AudioFloatingPanel);
        AudioGrip.ManipulationDelta += (_, e) => JellyDrag(AudioFloatingPanel, e);
        AudioGrip.ManipulationCompleted += (_, _) => { EndCheapDrag(AudioFloatingPanel); JellyRelease(AudioFloatingPanel); };

        // hint-driven, so the very first frame is already the right theme and
        // accent; FinishStartup re-applies both from the library it loads.
        ApplyTheme();
        try { ApplyAccent(ColorUtil.Parse(_library.AccentColor), refreshTheme: true); } catch { }

        ConfigureCustomTitleBar();

        // Restore the last window placement BEFORE any fullscreen presenter, so
        // leaving fullscreen/maximise lands on the size the user actually had
        // last session instead of the small first-run default (#14-batch2).
        try
        {
            if (_library.WinW >= 400 && _library.WinH >= 300)
                AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    (int)_library.WinX, (int)_library.WinY, (int)_library.WinW, (int)_library.WinH));
            if (_library.WinMaximized && AppWindow.Presenter is OverlappedPresenter op)
                op.Maximize();
        }
        catch { }

        // Startup experience: full screen + the notebook/section/page picker,
        // with the last-used page already loaded behind it (#31).
        if (_library.StartFullscreen)
            try { if (AppWindow.Presenter is OverlappedPresenter sop) sop.Maximize(); } catch { }
        UpdateFullscreenIcon();
        // the startup picker needs notebooks, and touch mode needs the saved
        // flag — both happen in FinishStartup (#31, #36)
        RootGrid.Loaded += (_, _) => UpdateScreenMetrics();

        // Stage 1 collaboration (#collab): every 20s, apply ops other devices
        // appended to their oplog files in the (synced) library folder.
        _syncTimer.Interval = TimeSpan.FromSeconds(20);
        _syncTimer.Tick += (_, _) => RunForeignMerge();
        // keep the real monitor width current so a NEW text box caps at half the
        // physical screen; existing boxes snapshot their own cap at creation and
        // are left untouched by a later resize (#15)
        this.SizeChanged += (_, _) => UpdateScreenMetrics();

        // liquid pop for every flyout pane — history, search, page settings… (#50)
        foreach (var fb in new FlyoutBase?[]
                 { HistoryFlyout, SearchBtn.Flyout, PageSettingsBtn.Flyout, ZoomBtn.Flyout, RulerBtn.Flyout, MouseModeBtn.Flyout })
        {
            if (fb is Flyout fl)
                fl.Opened += (_, _) => { if (fl.Content is FrameworkElement root) PopIn(root, 0.9, 280); };
        }

        // The veil is opaque, so pointer input cannot reach the half-built UI,
        // and no accelerators exist yet (ApplyKeyPreset runs in FinishStartup) —
        // but Tab can still walk into the toolbars underneath. Refuse focus
        // outright until the library has landed.
        // The veil's own buttons are the one exception: the error state has to
        // stay reachable from the keyboard.
        RootGrid.GettingFocus += (_, e) =>
        {
            if (_libraryReady || e.NewFocusedElement == null) return;
            var f = e.NewFocusedElement;
            if (ReferenceEquals(f, LoadRetryBtn) || ReferenceEquals(f, LoadFolderBtn) ||
                ReferenceEquals(f, LoadQuitBtn)) return;
            e.TryCancel();
        };

        // The window is ready to show. Everything that needs the library runs as
        // a continuation of the load App.OnLaunched started (#roadmap).
        _ = BeginLibraryLoadAsync();
    }

    // =======================================================================
    // Async library load, phase 2 (#roadmap)
    // =======================================================================

    // Cheap synchronous skin: settings.json mirrors the handful of library
    // fields that decide what the first frame looks like, so the window never
    // opens light-then-dark or small-then-restored.
    private void ApplyStartupHints()
    {
        var h = LibraryStore.Settings.Ui ?? new LibraryStore.UiHints();
        _library.Theme = h.Theme;
        _library.OledBlack = h.OledBlack;
        _library.AccentColor = h.Accent;
        _library.WinX = h.WinX;
        _library.WinY = h.WinY;
        _library.WinW = h.WinW;
        _library.WinH = h.WinH;
        _library.WinMaximized = h.WinMaximized;
        _library.StartFullscreen = h.StartFullscreen;
    }

    private async Task BeginLibraryLoadAsync()
    {
        Library loaded;
        try { loaded = await LibraryStore.LoadAsync(); }
        catch (Exception ex) { ShowLibraryError(ex.Message); return; }

        // A library that exists but will not parse must NOT become an empty one:
        // the save gate stays shut and the veil turns into an error state, so no
        // autosave, page switch or close handler can write over the real file.
        if (LibraryStore.LoadFailed)
        {
            ShowLibraryError(LibraryStore.LoadError ?? "The library file could not be read.");
            return;
        }

        LibraryStore.AdoptInPlace(_library, loaded);
        SyncLog.Initialize(_library);   // op-log shadow: only post-launch edits log (#collab)
        LibraryStore.EnableSaving();    // the single point where writing becomes legal
        FinishStartup();
    }

    // The half of the constructor that needs a real library.
    private void FinishStartup()
    {
        _libraryReady = true;

        Loc.SetLanguage(_library.Language);   // before anything captures a string (#C7)

        SeedPens();
        Surface.PendingFontFamily = _library.DefaultFont;
        Surface.PendingFontSize = (float)_library.DefaultFontSize;
        RefreshCalcHistory();   // history persists with the library (#47)
        RestoreCalcVars();      // user variables persist too (#A7)

        NotebookPanel.Width = Math.Clamp(_library.NotebookPanelW, 220, 560);
        if (_library.NotebookPanelH > 0)
            NotebookPanel.Height = Math.Clamp(_library.NotebookPanelH, 240, 940);

        ApplyGlowMode();
        ApplyKeyPreset();   // installs the saved shortcut layout and its F1 sheet (C5)

        // restore the last-selected eraser mode (#13-batch2)
        Surface.EraserMode = _library.LastEraserMode == "Point" ? EraserMode.Point : EraserMode.Object;
        // the legacy combined pen-repair switch migrates to the split toggles (#6-batch4)
        if (_library.PenRepair) { _library.PenRepairDots = true; _library.PenRepairBridge = true; _library.PenRepair = false; }
        Surface.PenRepairDots = _library.PenRepairDots;
        Surface.PenRepairBridge = _library.PenRepairBridge;
        Surface.MotionBlur = _library.MotionBlur;
        Surface.ShowCommentsAlways = _library.ShowCommentPins;
        // the font list used to say "Amsterdam"; the installed family is
        // "Amsterdam Handwriting" — migrate saved settings (#9-batch2)
        if (_library.DefaultFont == "Amsterdam") _library.DefaultFont = "Amsterdam Handwriting";
        // configurable autosave debounce
        _saveTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(_library.AutosaveSeconds, 0.5, 10));

        // the hints were only a mirror; re-apply from the library itself so a
        // stale settings.json cannot leave the window in the wrong skin
        ApplyTheme();
        try { ApplyAccent(ColorUtil.Parse(_library.AccentColor), refreshTheme: true); } catch { }
        ApplyLiquidness(_library.Liquidness);
        ApplyPenDock();
        BuildTree();
        BuildPenStrip();
        OpenStartupPage();
        SelectTool("Pen");
        ApplyToolbarVisibility();
        if (_library.Pens.Count > 0) ApplyPreset(_library.Pens[0]);
        UpdateUndoButtons();

        // touch mode needs the visual tree; the load can land either side of it (#36)
        if (_library.TouchMode)
        {
            if (RootGrid.IsLoaded) ApplyTouchMode(true);
            else RootGrid.Loaded += (_, _) => ApplyTouchMode(true);
        }
        UpdateScreenMetrics();

        _syncTimer.Start();
        _uiReady = true;

        // the legacy import ran while the save gate was shut; persist it now
        if (LibraryStore.PendingImportSave) ScheduleSave();
        if (_library.StartOnGallery) ShowGallery(launcher: true);

        HideVeil();
        ShowStatus("Right-click a pen to edit its type, colour, size and pressure response. F11 toggles full screen.");
    }

    private void HideVeil()
    {
        if (LoadingVeil.Visibility != Visibility.Visible) return;
        FadeTo(LoadingVeil, 0, 180, collapseAtEnd: true);
    }

    // The veil becomes the error state rather than dismissing: _libraryReady
    // stays false and LibraryStore.EnableSaving is never called, so nothing in
    // the app can overwrite a library we merely failed to read.
    private void ShowLibraryError(string message)
    {
        LoadingBusy.Visibility = Visibility.Collapsed;
        LoadingErrorText.Text = message;
        LoadingError.Visibility = Visibility.Visible;
        LoadingVeil.Visibility = Visibility.Visible;
        LoadingVeil.Opacity = 1;
    }

    private async void LoadRetry_Click(object sender, RoutedEventArgs e)
    {
        LoadingError.Visibility = Visibility.Collapsed;
        LoadingBusy.Visibility = Visibility.Visible;
        LibraryStore.ResetPendingLoad();   // really re-read the disk
        await BeginLibraryLoadAsync();
    }

    private void LoadOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(LibraryStore.Dir) { UseShellExecute = true });
        }
        catch { }
    }

    private void LoadQuit_Click(object sender, RoutedEventArgs e) => Close();

    // ---- unified glow engine (#1/#4-batch3): ONE timer drives every glow
    // brush (shared rims + per-card hover glows), so Breathe is a pure sine
    // (no stacked easing curves) and Circulate rotates the gradient axis at a
    // constant rate for ALL glows, not just the shared ones. ----
    private DispatcherTimer? _glowTimer;
    private double _glowT;
    private long _rippleStartMs = -1;
    private static readonly List<WeakReference<LinearGradientBrush>> _glowClients = new();

    // Comet mode: a true rim runner. Each equipped panel gets 3 dashed
    // Rectangles overlaid on its rim; a single dash segment travels the
    // rounded-rect path at constant PATH speed via StrokeDashOffset (#comet).
    private readonly List<CometRim> _cometRims = new();

    // per-instance glow brushes (gallery hover glows) opt in here
    public static void RegisterGlowBrush(LinearGradientBrush b)
    {
        lock (_glowClients) _glowClients.Add(new WeakReference<LinearGradientBrush>(b));
    }

    // Hand a brush back so the engine stops driving its opacity — without this
    // a hover fade-OUT is overwritten by the next GlowTick (#anim).
    public static void UnregisterGlowBrush(LinearGradientBrush b)
    {
        lock (_glowClients)
            for (int i = _glowClients.Count - 1; i >= 0; i--)
                if (!_glowClients[i].TryGetTarget(out var t) || ReferenceEquals(t, b))
                    _glowClients.RemoveAt(i);
    }

    private static IEnumerable<LinearGradientBrush> GlowBrushes()
    {
        var res = Application.Current.Resources;
        if (res["GlassEdgeBrush"] is LinearGradientBrush e) yield return e;
        if (res["GlowBrush"] is LinearGradientBrush g) yield return g;
        lock (_glowClients)
            for (int i = _glowClients.Count - 1; i >= 0; i--)
            {
                if (_glowClients[i].TryGetTarget(out var b)) { }
                else { _glowClients.RemoveAt(i); continue; }
            }
        List<LinearGradientBrush> live;
        lock (_glowClients)
            live = _glowClients.Select(w => w.TryGetTarget(out var b) ? b : null)
                               .Where(b => b != null).Cast<LinearGradientBrush>().ToList();
        foreach (var b in live) yield return b;
    }

    private void ApplyGlowMode()
    {
        if (_glowTimer == null)
        {
            _glowTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _glowTimer.Tick += (_, _) => GlowTick();
        }
        foreach (var b in GlowBrushes()) b.Opacity = 0.9;
        SetGradientAxes(0, 0, 1, 1);
        // leaving Circulate restores every brush's own artistic stops (#4)
        // a mode change always restores the original stops; the animated modes
        // rebuild their own pattern on the next tick
        RestoreCirculateStops();
        // Comet is now a dashed rim runner, not a brush animation: flip the
        // overlay rectangles on only for Comet (and only when motion is allowed);
        // every other mode collapses them so they cost nothing.
        // Comet and Chase share the rim runner; they differ only in the dash
        // layout — Chase tiles 4 evenly spaced heads with short tails where
        // Comet runs one head with a long one.
        bool chase = _library.GlowMode == "Chase";
        bool rimOn = !_reduceMotion && (chase || _library.GlowMode == "Comet");
        // the static rim is the accent mixed 45% toward white (see ApplyAccent);
        // the comet must be that exact colour, since it replaces it (#anim)
        var rimAccent = Mix(_accentCurrent ?? ColorUtil.Parse(_library.AccentColor), Colors.White, 0.45);
        foreach (var rim in _cometRims)
        {
            rim.SetColor(rimAccent);
            if (rimOn) rim.Configure(chase ? 4 : 1, chase ? 0.30 : 0.34, chase ? 14 : CometRim.Segments);
            rim.SetActive(rimOn);
        }
        if (_reduceMotion || _library.GlowMode == "Off") _glowTimer.Stop();
        else _glowTimer.Start();
    }

    private void GlowTick()
    {
        _glowT += 0.04;
        string mode = _reduceMotion ? "Off" : _library.GlowMode;
        double opacity = 0.9;
        if (mode == "Breathe")
        {
            // pure sinusoid: perfectly even rise and fall
            opacity = 0.675 + 0.325 * Math.Sin(_glowT * (2 * Math.PI / 5.0));
        }
        else if (mode == "Comet")
        {
            opacity = 0.95;
            // The glow brushes keep their designed static look (RestoreCirculateStops
            // already ran on the mode switch); the motion lives entirely in the
            // overlay rim rectangles, whose dash segment travels the rounded-rect
            // path at CONSTANT path speed — follows corners, never mirrors (#comet).
            double ct = (_glowT / 3.5) % 1.0;   // one lap ~3.5s
            foreach (var rim in _cometRims) rim.SetProgress(ct);
        }
        else if (mode == "Circulate")
        {
            opacity = 0.95;
            // A single bright band travelling at a constant rate, done by
            // animating the gradient STOPS on a fixed 0..1 axis. Moving the
            // axis itself washed out any brush that registered after startup
            // (Pad spread painted the whole element with one end colour — the
            // white fog over the gallery, and the washed "disabled-looking"
            // chrome). Stops stay in [0,1], so nothing can wash out (#4).
            double band = (_glowT / 4.5) % 1.0;   // brisk enough that the motion clearly reads
            var seen = new HashSet<LinearGradientBrush>();
            foreach (var b in GlowBrushes()) { seen.Add(b); CirculateStops(b, band); }
            PruneSnapshots(seen);
        }
        else if (mode == "Aurora")
        {
            // Two speeds from two brush-level properties: the axis TRANSLATES at
            // one rate while its SPAN breathes at another, so the three analogous
            // bands slide across each other instead of moving as one rigid
            // pattern — the colour mix changes even where the drift alone would
            // repeat. Stops are baked once (see EnsureAuroraStops).
            double drift = (_glowT / 11.0) % 1.0;
            double span = 1.0 + 0.30 * Math.Sin(_glowT * (2 * Math.PI / 17.0));
            var seen = new HashSet<LinearGradientBrush>();
            foreach (var b in GlowBrushes())
            {
                seen.Add(b);
                EnsureAuroraStops(b);
                b.StartPoint = new Point(drift, drift * 0.6);
                b.EndPoint = new Point(drift + span, drift * 0.6 + span);
            }
            PruneSnapshots(seen);
            opacity = 0.80 + 0.14 * Math.Sin(_glowT * (2 * Math.PI / 7.3));   // never dark
        }
        else if (mode == "Shimmer")
        {
            // One fast sweep, then a long rest. The narrow specular band is baked
            // into the stops; the sweep is the axis sliding it across the panel.
            const double SweepSec = 0.85, RestSec = 4.6;
            double ph = _glowT % (SweepSec + RestSec);
            double p = ph < SweepSec ? ph / SweepSec : 1.0;
            double e = p * p * p * (p * (p * 6 - 15) + 10);   // smootherstep: eases in AND out
            double pos = -0.55 + 1.85 * e;                    // starts and ends off the panel
            var seen = new HashSet<LinearGradientBrush>();
            foreach (var b in GlowBrushes())
            {
                seen.Add(b);
                EnsureShimmerStops(b);
                b.StartPoint = new Point(-pos, -pos * 0.35);
                b.EndPoint = new Point(1 - pos, 1 - pos * 0.35);
            }
            PruneSnapshots(seen);
        }
        else if (mode == "Ember")
        {
            // Smoothed value noise, not white noise: a fixed lattice sampled
            // through a smootherstep interpolant, two octaves. A third, much
            // slower channel is thresholded to gate the occasional surge.
            double n = 0.65 * ValueNoise(_glowT * 0.55) + 0.35 * ValueNoise(_glowT * 1.6 + 31.7);
            double surge = Math.Max(0, ValueNoise(_glowT * 0.17 + 77.3) - 0.62) / 0.38;
            var seen = new HashSet<LinearGradientBrush>();
            foreach (var b in GlowBrushes()) { seen.Add(b); EnsureEmberStops(b); }
            PruneSnapshots(seen);
            opacity = Math.Min(1.0, 0.66 + 0.20 * n + 0.26 * surge * surge);
        }
        else if (mode == "Chase")
        {
            opacity = 0.95;
            // Same rim runner as Comet, reconfigured in ApplyGlowMode into four
            // evenly spaced heads; the lap is slower because four heads now pass
            // any point per lap.
            double ht = (_glowT / 6.0) % 1.0;
            foreach (var rim in _cometRims) rim.SetProgress(ht);
        }
        else if (mode == "Heartbeat")
        {
            // 60bpm double thump: systole, a weaker diastole, then rest.
            // Brightness only — nothing travels.
            double ph = _glowT % 1.0;
            double beat = Thump(ph, 0.00, 1.00) + Thump(ph, 0.26, 0.52);
            opacity = 0.62 + 0.36 * Math.Min(1.0, beat);
        }
        if (_rippleStartMs >= 0)
        {
            double rt = (Environment.TickCount64 - _rippleStartMs) / 340.0;
            if (rt >= 1) _rippleStartMs = -1;
            else opacity *= 0.25 + 0.75 * rt;
        }
        foreach (var b in GlowBrushes()) b.Opacity = opacity;
        if (mode == "Off" && _rippleStartMs < 0) _glowTimer!.Stop();
    }

    // ---- Circulate via gradient stops (#4): each managed brush gets 12
    // fixed-offset stops whose alphas form one travelling peak; the brush's
    // original stops are snapshotted and restored when the mode changes. ----
    private static readonly Dictionary<LinearGradientBrush, List<(double O, Color C)>> _circSnapshot = new();

    private static void CirculateStops(LinearGradientBrush b, double band)
    {
        // WinUI 3 re-renders BRUSH-level property changes (Start/EndPoint,
        // SpreadMethod) but caches gradient stop mutations — every
        // stops-animated variant looked frozen. So: build a STATIC comet
        // gradient once (blazing head at offset 1, tail decaying behind it,
        // faint far side), then animate by SLIDING THE AXIS each tick with
        // Repeat wrap. Repeat is (re)assigned per brush, which also prevents
        // the Pad end-colour wash that fogged late-registered brushes (#4).
        const int N = 24;
        if (!_circSnapshot.ContainsKey(b))
        {
            var snap = b.GradientStops.Select(gs => (gs.Offset, gs.Color)).ToList();
            _circSnapshot[b] = snap;
            if (snap.Count == 0) return;
            b.GradientStops.Clear();
            for (int i = 0; i < N; i++)
            {
                double off = i / (double)(N - 1);
                var orig = SampleStops(snap, off);
                double behindHead = 1 - off;                     // the head lives at offset 1
                double intensity = behindHead < 0.42
                    ? Math.Pow(1 - behindHead / 0.42, 2.2)       // long accelerating tail
                    : 0;                                          // faint far side
                byte a = (byte)Math.Clamp(orig.A * (0.10 + 1.25 * intensity), 0, 255);
                b.GradientStops.Add(new GradientStop { Offset = off, Color = Color.FromArgb(a, orig.R, orig.G, orig.B) });
            }
            b.SpreadMethod = GradientSpreadMethod.Repeat;
        }
        // Repeat tiles the comet; the 1 -> 0 wrap forms the crisp leading edge
        b.StartPoint = new Point(band, band);
        b.EndPoint = new Point(band + 1, band + 1);
    }

    // Aurora/Shimmer/Ember also rebuild their stops ONCE and then animate only
    // brush-level properties (or nothing but Opacity), for the same caching
    // reason spelled out in CirculateStops. They share Circulate's snapshot
    // dictionary so RestoreCirculateStops undoes every one of them.
    private static bool TakeSnapshot(LinearGradientBrush b, out List<(double O, Color C)> snap)
    {
        if (_circSnapshot.TryGetValue(b, out var have)) { snap = have; return false; }
        snap = b.GradientStops.Select(gs => (gs.Offset, gs.Color)).ToList();
        _circSnapshot[b] = snap;
        return true;
    }

    // hover glows come and go — drop snapshots of brushes that left the engine
    // so the dictionary can't pin them alive forever
    private static void PruneSnapshots(HashSet<LinearGradientBrush> seen)
    {
        foreach (var dead in _circSnapshot.Keys.Where(k => !seen.Contains(k)).ToList())
            _circSnapshot.Remove(dead);
    }

    // Three broad, overlapping analogous bands either side of the brush's own
    // hue. Offset 1 reproduces offset 0 exactly so Repeat tiles seamlessly, and
    // the alpha floor keeps every band lit — Aurora is never fully dark.
    private static void EnsureAuroraStops(LinearGradientBrush b)
    {
        if (!TakeSnapshot(b, out var snap) || snap.Count == 0) return;
        const int N = 24;
        b.GradientStops.Clear();
        for (int i = 0; i < N; i++)
        {
            double off = i / (double)(N - 1);
            var orig = SampleStops(snap, off);
            double h = off * 2 * Math.PI;
            var c = RotateHue(orig, 26 * Math.Sin(h) + 14 * Math.Sin(2 * h + 1.1));
            double lift = 0.72 + 0.28 * (0.5 + 0.5 * Math.Sin(h - 0.7));
            byte a = (byte)Math.Clamp(orig.A * lift, 0, 255);
            b.GradientStops.Add(new GradientStop { Offset = off, Color = Color.FromArgb(a, c.R, c.G, c.B) });
        }
        b.SpreadMethod = GradientSpreadMethod.Repeat;
    }

    // A narrow white-hot band on an otherwise resting gradient. Pad is safe
    // here (unlike the wash described in CirculateStops) because BOTH ends
    // carry the brush's own resting colour, not the highlight.
    private static void EnsureShimmerStops(LinearGradientBrush b)
    {
        if (!TakeSnapshot(b, out var snap) || snap.Count == 0) return;
        const int N = 41;
        b.GradientStops.Clear();
        for (int i = 0; i < N; i++)
        {
            double off = i / (double)(N - 1);
            var orig = SampleStops(snap, off);
            double d = Math.Abs(off - 0.5) / 0.085;
            double spec = d >= 1 ? 0 : Math.Pow(1 - d, 2.0);
            var c = Mix(orig, Colors.White, 0.85 * spec);
            byte a = (byte)Math.Clamp(orig.A * (0.55 + 1.9 * spec), 0, 255);
            b.GradientStops.Add(new GradientStop { Offset = off, Color = Color.FromArgb(a, c.R, c.G, c.B) });
        }
        b.SpreadMethod = GradientSpreadMethod.Pad;
    }

    // Ember only warms the palette toward coal-amber; the flicker is Opacity.
    private static void EnsureEmberStops(LinearGradientBrush b)
    {
        if (!TakeSnapshot(b, out var snap) || snap.Count == 0) return;
        const int N = 12;
        b.GradientStops.Clear();
        for (int i = 0; i < N; i++)
        {
            double off = i / (double)(N - 1);
            var orig = SampleStops(snap, off);
            var c = Mix(orig, Color.FromArgb(255, 255, 148, 62), 0.42);
            b.GradientStops.Add(new GradientStop { Offset = off, Color = Color.FromArgb(orig.A, c.R, c.G, c.B) });
        }
        b.SpreadMethod = GradientSpreadMethod.Pad;
    }

    // Fixed lattice, wrapped at both ends so the noise is seamless. Sampling it
    // through smootherstep is what turns it from static into a slow wander.
    private static readonly double[] _noiseLattice = BuildNoiseLattice();

    private static double[] BuildNoiseLattice()
    {
        var r = new Random(4242);
        var a = new double[257];
        for (int i = 0; i < 256; i++) a[i] = r.NextDouble();
        a[256] = a[0];
        return a;
    }

    private static double ValueNoise(double t)
    {
        t %= 256.0;
        if (t < 0) t += 256.0;
        int i = (int)t;
        double f = t - i;
        f = f * f * f * (f * (f * 6 - 15) + 10);
        return _noiseLattice[i] + (_noiseLattice[i + 1] - _noiseLattice[i]) * f;
    }

    // One heartbeat lobe: fast attack, slower decay, zero outside its window.
    private static double Thump(double ph, double at, double amp)
    {
        double d = ph - at;
        if (d < 0 || d > 0.22) return 0;
        double u = d / 0.22;
        return amp * Math.Pow(1 - u, 2.6) * Math.Min(1, u / 0.12);
    }

    // Hue rotation via HSV. Alpha is the caller's business.
    private static Color RotateHue(Color c, double degrees)
    {
        double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, bl)), min = Math.Min(r, Math.Min(g, bl));
        double d = max - min, v = max, sat = max <= 0 ? 0 : d / max;
        double h;
        if (d <= 1e-6) h = 0;
        else if (max == r) h = 60 * (((g - bl) / d) % 6);
        else if (max == g) h = 60 * ((bl - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        h = (h + degrees) % 360;
        if (h < 0) h += 360;
        double cc = v * sat, x = cc * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - cc;
        double rr, gg, bb;
        if (h < 60) { rr = cc; gg = x; bb = 0; }
        else if (h < 120) { rr = x; gg = cc; bb = 0; }
        else if (h < 180) { rr = 0; gg = cc; bb = x; }
        else if (h < 240) { rr = 0; gg = x; bb = cc; }
        else if (h < 300) { rr = x; gg = 0; bb = cc; }
        else { rr = cc; gg = 0; bb = x; }
        return Color.FromArgb(255, (byte)Math.Round((rr + m) * 255),
                                   (byte)Math.Round((gg + m) * 255),
                                   (byte)Math.Round((bb + m) * 255));
    }

    // Comet no longer touches the glow brushes at all — it is a dashed rim
    // runner built from overlay Rectangles (see CometRim below). The brushes
    // simply keep their designed static gradient, restored here on mode switch.
    private static void RestoreCirculateStops()
    {
        foreach (var kv in _circSnapshot)
        {
            try
            {
                kv.Key.GradientStops.Clear();
                foreach (var (o, c) in kv.Value)
                    kv.Key.GradientStops.Add(new GradientStop { Offset = o, Color = c });
                kv.Key.SpreadMethod = GradientSpreadMethod.Pad;
                kv.Key.StartPoint = new Point(0, 0);
                kv.Key.EndPoint = new Point(1, 1);
            }
            catch { }
        }
        _circSnapshot.Clear();
    }

    // Piecewise-linear sample of a brush's snapshotted stops at a given offset.
    private static Color SampleStops(List<(double O, Color C)> snap, double off)
    {
        if (off <= snap[0].O) return snap[0].C;
        for (int i = 1; i < snap.Count; i++)
        {
            if (off > snap[i].O) continue;
            double span = Math.Max(1e-6, snap[i].O - snap[i - 1].O);
            double f = (off - snap[i - 1].O) / span;
            Color a = snap[i - 1].C, c = snap[i].C;
            return Color.FromArgb(
                (byte)(a.A + (c.A - a.A) * f), (byte)(a.R + (c.R - a.R) * f),
                (byte)(a.G + (c.G - a.G) * f), (byte)(a.B + (c.B - a.B) * f));
        }
        return snap[^1].C;
    }


    private static void SetGradientAxes(double sx, double sy, double ex, double ey)
    {
        foreach (var b in GlowBrushes())
        {
            b.StartPoint = new Point(sx, sy);
            b.EndPoint = new Point(ex, ey);
        }
    }

    // The accent that the GlowBrush derives from (App.xaml SystemAccentColor
    // #D97757); the comet rim is stroked in the same hue.
    private static Color AccentGlowColor()
    {
        try
        {
            if (Application.Current.Resources["SystemAccentColor"] is Color c) return c;
        }
        catch { }
        return Color.FromArgb(0xFF, 0xD9, 0x77, 0x57);
    }

    // Wrap a panel's single child in a Grid and overlay 3 dashed Rectangles that
    // run the comet head + fading wake around the rim. Built at runtime so the
    // XAML children are never hand-edited.
    private CometRim AttachCometRim(Border host)
    {
        var rim = new CometRim(host, AccentGlowColor());
        _cometRims.Add(rim);
        return rim;
    }

    // A true rim runner. StrokeDashArray/StrokeDashOffset are expressed in
    // multiples of StrokeThickness (WinUI dash units); we convert the rounded-
    // rect path length into those units so ONE dash = the comet head and the
    // offset advances linearly in path-length units => constant physical speed
    // that follows the corners and never mirrors onto a second edge.
    private sealed class CometRim
    {
        private const double Thick = 2.5;
        public const int Segments = 36;         // short segments so no banding is visible
        private const double TrailFrac = 0.34;  // comet occupies ~a third of the rim
        private int _heads = 1;                 // Chase tiles the dash pattern N times
        private double _trailFrac = TrailFrac;
        private int _live = Segments;           // segments actually drawn (shorter tail = fewer)
        private double _headPeriod;             // dash-pattern period, in thickness units
        private readonly Rectangle[] _segs = new Rectangle[Segments];
        private readonly Border _host;
        private readonly Brush? _restBorder;
        private readonly double[] _fade = new double[Segments];
        private double _perimUnits;             // full lap length, in thickness units
        private double _segUnits;               // one segment's dash length
        private double _radius;
        private bool _active;

        public CometRim(Border host, Color color)
        {
            _host = host;
            _restBorder = host.BorderBrush;   // restored when Comet is switched off
            double rx = host.CornerRadius.TopLeft;
            // Three phases along the trail, as one smooth curve rather than
            // discrete blocks: [0] the main light at full strength, then a
            // steady smooth decay, reaching exactly zero at the tail's end so
            // the far side of the rim carries no glow at all.
            for (int i = 0; i < Segments; i++)
            {
                double u = i / (double)(Segments - 1);          // 0 = head, 1 = tail end
                // raised cosine: flat at both ends, so the head doesn't start
                // with a hard step and the tail reaches zero imperceptibly
                _fade[i] = Math.Pow(0.5 * (1 + Math.Cos(Math.PI * u)), 1.3);
                _segs[i] = MakeRect(color, _fade[i], rx);
            }

            var original = host.Child;
            host.Child = null;
            var grid = new Grid();
            if (original != null) grid.Children.Add(original);
            for (int i = Segments - 1; i >= 0; i--) grid.Children.Add(_segs[i]);   // head paints last
            host.Child = grid;

            // the child grid lives INSIDE the border + padding — pull the rim
            // rectangles back out so they trace the panel's OUTER edge
            var pad = host.Padding; var bt = host.BorderThickness;
            var outset = new Thickness(-(pad.Left + bt.Left), -(pad.Top + bt.Top),
                                       -(pad.Right + bt.Right), -(pad.Bottom + bt.Bottom));
            foreach (var s in _segs) s.Margin = outset;

            _radius = rx;
            // Recompute dash metrics from a rectangle's own rendered size so
            // insets/padding never desync the maths.
            _segs[0].SizeChanged += (_, e) => Recompute(e.NewSize.Width, e.NewSize.Height);
            SetActive(false);
        }

        // Every segment MUST share one StrokeThickness: dash units are
        // thickness-relative, so differing thickness would desync the offsets.
        private static Rectangle MakeRect(Color c, double opacity, double rx) => new Rectangle
        {
            IsHitTestVisible = false,
            Fill = null,
            Stroke = new SolidColorBrush(c),
            StrokeThickness = Thick,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashCap = PenLineCap.Round,
            RadiusX = rx,
            RadiusY = rx,
            Opacity = opacity,
            Visibility = Visibility.Collapsed,
        };

        public void SetColor(Color c)
        {
            foreach (var s in _segs) ((SolidColorBrush)s.Stroke).Color = c;
        }

        private void Recompute(double w, double h)
        {
            if (w <= 0 || h <= 0) return;
            double r = Math.Min(_radius, Math.Min(w, h) / 2.0);
            // rounded-rect perimeter: straight runs (corners removed) + one full
            // circle from the four quarter arcs.
            double perimPx = 2 * (w - 2 * r) + 2 * (h - 2 * r) + 2 * Math.PI * r;
            _perimUnits = perimPx / Thick;
            // one dash period per head: the pattern tiles _heads times around
            // the rim, which is what puts the heads at even spacing for free.
            _headPeriod = _perimUnits / _heads;
            _segUnits = _trailFrac * _headPeriod / _live;
            double gap = Math.Max(0.01, _headPeriod - _segUnits);
            foreach (var s in _segs)
            {
                s.StrokeDashArray = new DoubleCollection { _segUnits, gap };
                s.RadiusX = r;
                s.RadiusY = r;
            }
        }

        // t01 in [0,1): fraction of one lap. Each segment sits one segment-length
        // further back along the path than the one ahead of it, so the whole
        // trail travels as a single continuous comet.
        public void SetProgress(double t01)
        {
            if (!_active || _perimUnits <= 0) return;
            double off = -t01 * _perimUnits;
            for (int i = 0; i < _live; i++)
                _segs[i].StrokeDashOffset = off + i * _segUnits;
        }

        // Reshape the runner: how many heads travel the rim, how much of one
        // head's spacing the tail spans, and how many segments render it.
        public void Configure(int heads, double trailFrac, int live)
        {
            _heads = Math.Max(1, heads);
            _trailFrac = Math.Clamp(trailFrac, 0.02, 0.9);
            _live = Math.Clamp(live, 2, Segments);
            // steeper decay for a multi-head chase, so each tail dies before the
            // next head arrives; the single comet keeps its long gentle falloff.
            double exp = _heads > 1 ? 1.9 : 1.3;
            for (int i = 0; i < Segments; i++)
            {
                double u = Math.Min(1.0, i / (double)(_live - 1));
                _fade[i] = i < _live ? Math.Pow(0.5 * (1 + Math.Cos(Math.PI * u)), exp) : 0;
                _segs[i].Opacity = _fade[i];
            }
            Recompute(_segs[0].ActualWidth, _segs[0].ActualHeight);
            ApplyVisibility();
        }

        private void ApplyVisibility()
        {
            for (int i = 0; i < Segments; i++)
                _segs[i].Visibility = _active && i < _live ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetActive(bool on)
        {
            _active = on;
            ApplyVisibility();
            // only ONE glow on the edge: the panel's static rim steps aside so
            // the comet is the sole light travelling it (#anim)
            _host.BorderBrush = on ? new SolidColorBrush(Colors.Transparent) : _restBorder;
        }
    }

    // ---- drag-time render cost control (#12-batch2) ----
    // The glow pulse is a dependent animation: every tick invalidates every
    // glass panel. And dragging an acrylic panel re-blurs it per frame. Both
    // together are what tanked drag framerate — pause the pulse and swap the
    // dragged panel's acrylic for its cheap fallback colour while dragging.
    private readonly Dictionary<FrameworkElement, Brush> _dragAcrylicSwap = new();

    private void BeginCheapDrag(FrameworkElement el)
    {
        _glowTimer?.Stop();
        if (el is Border b && b.Background is AcrylicBrush a && !_dragAcrylicSwap.ContainsKey(el))
        {
            _dragAcrylicSwap[el] = b.Background;
            b.Background = new SolidColorBrush(a.FallbackColor);
        }
    }

    private void EndCheapDrag(FrameworkElement el)
    {
        if (el is Border b && _dragAcrylicSwap.TryGetValue(el, out var orig))
        {
            b.Background = orig;
            _dragAcrylicSwap.Remove(el);
        }
        if (!_reduceMotion && _library.GlowMode != "Off") _glowTimer?.Start();
    }

    // Remembers the window's real (non-maximised) placement on close so the
    // next launch can restore it (#14-batch2).
    private void CaptureWindowPlacement()
    {
        if (!_libraryReady) return;   // no real library to record the placement into
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                _library.WinMaximized = op.State == OverlappedPresenterState.Maximized;
                if (op.State == OverlappedPresenterState.Restored)
                {
                    _library.WinX = AppWindow.Position.X;
                    _library.WinY = AppWindow.Position.Y;
                    _library.WinW = AppWindow.Size.Width;
                    _library.WinH = AppWindow.Size.Height;
                }
            }
            // full-screen at close: keep the previously stored placement as-is
        }
        catch { }
    }

    // =======================================================================
    // UI transitions (#32): panels fade in and out instead of snapping.
    // A version counter per element keeps rapid toggles from fighting each
    // other (an interrupted fade-out must not collapse a re-shown panel).
    // =======================================================================
    private readonly Dictionary<FrameworkElement, int> _fadeVer = new();

    private void FadeTo(FrameworkElement el, double to, int ms, bool collapseAtEnd)
    {
        // reduced motion: jump to the end state, exactly as the failure path does
        if (_reduceMotion)
        {
            el.Opacity = collapseAtEnd ? 1 : to;
            if (collapseAtEnd) el.Visibility = Visibility.Collapsed;
            return;
        }
        int ver = _fadeVer.TryGetValue(el, out var v) ? v + 1 : 1;
        _fadeVer[el] = ver;
        try
        {
            var anim = new DoubleAnimation
            {
                To = to,   // no From: animates from the current opacity
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, el);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) =>
            {
                if (_fadeVer[el] != ver) return;
                if (collapseAtEnd) { el.Visibility = Visibility.Collapsed; el.Opacity = 1; }
            };
            sb.Begin();
        }
        catch
        {
            el.Opacity = collapseAtEnd ? 1 : to;
            if (collapseAtEnd) el.Visibility = Visibility.Collapsed;
        }
    }

    // ---- liquid glass motion (#48) ----
    private static CompositeTransform EnsureCT(FrameworkElement el)
    {
        if (el.RenderTransform is CompositeTransform ct) return ct;
        var t = new CompositeTransform();
        if (el.RenderTransform is TranslateTransform tt) { t.TranslateX = tt.X; t.TranslateY = tt.Y; }
        el.RenderTransform = t;
        return t;
    }

    // Dragging a floating window squashes and skews it with the momentum of
    // the movement, like pulling something through water.
    private static void JellyDrag(FrameworkElement el, ManipulationDeltaRoutedEventArgs e)
    {
        var ct = EnsureCT(el);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        ct.TranslateX += e.Delta.Translation.X;
        ct.TranslateY += e.Delta.Translation.Y;
        double vx = e.Velocities.Linear.X, vy = e.Velocities.Linear.Y;   // px/ms
        ct.SkewX = Math.Clamp(vx * 4.5, -7, 7);
        ct.SkewY = Math.Clamp(vy * 2.2, -4, 4);
        ct.ScaleX = Math.Clamp(1 + Math.Abs(vx) * 0.02, 1.0, 1.045);
        ct.ScaleY = Math.Clamp(1 + Math.Abs(vy) * 0.02, 1.0, 1.045);
    }

    // On release the window wobbles back into shape (elastic spring).
    private void JellyRelease(FrameworkElement el)
    {
        if (el.RenderTransform is not CompositeTransform) return;
        foreach (var (prop, to) in new[] { ("SkewX", 0.0), ("SkewY", 0.0), ("ScaleX", 1.0), ("ScaleY", 1.0) })
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(560)),
                EasingFunction = new ElasticEase { Oscillations = 2, Springiness = 5, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(a, el);
            Storyboard.SetTargetProperty(a, $"(UIElement.RenderTransform).(CompositeTransform.{prop})");
            var sb = new Storyboard();
            sb.Children.Add(a);
            sb.Begin();
        }
    }

    private void FadeIn(FrameworkElement el, int ms = 170, bool pop = true, double slideX = 0, double slideY = 0)
    {
        bool wasHidden = el.Visibility != Visibility.Visible;
        if (wasHidden) { el.Opacity = 0; el.Visibility = Visibility.Visible; }
        FadeTo(el, 1, ms, collapseAtEnd: false);

        // liquid pop: the panel swells into place with a soft overshoot
        if (pop && wasHidden) PopIn(el, 0.92, Math.Max(240, ms));
        // slide-out: bars and panels glide in from their edge (#51)
        if (wasHidden && (slideX != 0 || slideY != 0)) SlideNudge(el, slideX, slideY);
    }

    // Animates the element's translation from the given offset back to rest.
    // Only used on elements that don't drag via their transform.
    private void SlideNudge(FrameworkElement el, double fromX, double fromY)
    {
        if (_reduceMotion) return;
        try
        {
            EnsureCT(el);
            foreach (var (prop, from) in new[] { ("TranslateX", fromX), ("TranslateY", fromY) })
            {
                if (from == 0) continue;
                var a = new DoubleAnimation
                {
                    From = from, To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                    EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(a, el);
                Storyboard.SetTargetProperty(a, $"(UIElement.RenderTransform).(CompositeTransform.{prop})");
                var sb = new Storyboard();
                sb.Children.Add(a);
                sb.Begin();
            }
        }
        catch { }
    }

    /// <summary>Liquid pop-in: scales the element from slightly small to full
    /// size with a watery overshoot. Safe on elements that also drag.</summary>
    private void PopIn(FrameworkElement el, double from = 0.9, int ms = 300)
    {
        if (_reduceMotion) return;
        try
        {
            EnsureCT(el);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            foreach (var prop in new[] { "ScaleX", "ScaleY" })
            {
                var a = new DoubleAnimation
                {
                    From = from, To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                    EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(a, el);
                Storyboard.SetTargetProperty(a, $"(UIElement.RenderTransform).(CompositeTransform.{prop})");
                var sb = new Storyboard();
                sb.Children.Add(a);
                sb.Begin();
            }
        }
        catch { }
    }

    /// <summary>Uniform scale about the centre. `from` null animates from the
    /// element's current scale. Open and close pass mirrored values so the two
    /// read as one motion reversed.</summary>
    private void SwellScale(FrameworkElement el, double? from, double to, int ms)
    {
        // reduced motion: the resting state is unscaled, whichever leg this was
        if (_reduceMotion)
        {
            if (el.RenderTransform is CompositeTransform rest) { rest.ScaleX = 1; rest.ScaleY = 1; }
            return;
        }
        try
        {
            EnsureCT(el);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            foreach (var prop in new[] { "ScaleX", "ScaleY" })
            {
                var a = new DoubleAnimation
                {
                    From = from, To = to,
                    Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(a, el);
                Storyboard.SetTargetProperty(a, $"(UIElement.RenderTransform).(CompositeTransform.{prop})");
                var sb = new Storyboard();
                sb.Children.Add(a);
                sb.Begin();
            }
        }
        catch { }
    }

    private void FadeOut(FrameworkElement el, int ms = 140)
    {
        if (el.Visibility != Visibility.Visible) return;
        FadeTo(el, 0, ms, collapseAtEnd: true);
    }

    // =======================================================================
    // Connected card → page transition (C3). The page is a Win2D canvas and
    // cannot be animated as a XAML element, so MorphCard stands in for it: it
    // starts on the activated card's bounds, travels to the page bounds, and
    // fades out onto the live surface — and runs in reverse when the gallery
    // comes back. Only the frame and fill morph; the card's contents are
    // deliberately absent so the aspect stretch between a 188x134 card and a
    // full page never shows.
    // =======================================================================
    private Rect _morphSource;              // last activated card, in CanvasArea space
    private Color _morphAccent = Colors.Gray;
    private int _morphVer;

    /// <summary>Bounds of an element in CanvasArea coordinates, or null when it
    /// is not laid out. Must be read while the element is still visible.</summary>
    private Rect? BoundsInCanvas(FrameworkElement? el)
    {
        if (el == null || el.ActualWidth <= 0 || el.ActualHeight <= 0) return null;
        try
        {
            var p = el.TransformToVisual(CanvasArea).TransformPoint(new Point(0, 0));
            return new Rect(p.X, p.Y, el.ActualWidth, el.ActualHeight);
        }
        catch { return null; }
    }

    private Rect PageBounds() => new(0, 0, CanvasArea.ActualWidth, CanvasArea.ActualHeight);

    /// <summary>Grows the activated card into the page and remembers it as the
    /// anchor for the return leg.</summary>
    private void MorphCardToPage(Rect? source, Color accent)
    {
        if (source is not Rect r) return;
        _morphSource = r;
        _morphAccent = accent;
        if (_reduceMotion) return;
        RunMorph(r, PageBounds(), 340);
    }

    /// <summary>Return leg: the page contracts onto the card it was opened from.</summary>
    private void MorphPageToCard()
    {
        if (_reduceMotion || _morphSource.Width <= 0) return;
        RunMorph(PageBounds(), _morphSource, 260);
    }

    // The placeholder RESTS at `to`, and the transform starts it on `from`, so
    // scale 1 / translate 0 is always the finished frame in both directions.
    private void RunMorph(Rect from, Rect to, int ms)
    {
        if (from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0) return;
        int ver = ++_morphVer;
        try
        {
            Canvas.SetLeft(MorphCard, to.X);
            Canvas.SetTop(MorphCard, to.Y);
            MorphCard.Width = to.Width;
            MorphCard.Height = to.Height;
            MorphCard.BorderBrush = new SolidColorBrush(_morphAccent);
            MorphCard.RenderTransformOrigin = new Point(0, 0);
            EnsureCT(MorphCard);
            MorphHost.Opacity = 1;
            MorphHost.Visibility = Visibility.Visible;

            foreach (var (prop, start) in new[]
                     {
                         ("ScaleX", from.Width / to.Width), ("ScaleY", from.Height / to.Height),
                         ("TranslateX", from.X - to.X),     ("TranslateY", from.Y - to.Y)
                     })
                MorphTrack(prop, start, prop[0] == 'S' ? 1 : 0, ms);

            // opaque through most of the travel, then hand over to the canvas
            var fade = new DoubleAnimation
            {
                From = 1, To = 0,
                BeginTime = TimeSpan.FromMilliseconds(ms * 0.55),
                Duration = new Duration(TimeSpan.FromMilliseconds(ms * 0.45)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn }
            };
            Storyboard.SetTarget(fade, MorphHost);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(fade);
            sb.Completed += (_, _) => { if (_morphVer == ver) MorphHost.Visibility = Visibility.Collapsed; };
            sb.Begin();
        }
        catch { MorphHost.Visibility = Visibility.Collapsed; }
    }

    private void MorphTrack(string prop, double from, double to, int ms)
    {
        var a = new DoubleAnimation
        {
            From = from, To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(a, MorphCard);
        Storyboard.SetTargetProperty(a, $"(UIElement.RenderTransform).(CompositeTransform.{prop})");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }

    private void BarScroll_Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        int delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;
        sv.ChangeView(sv.HorizontalOffset - delta, null, null);
        e.Handled = true;
    }

    // =======================================================================
    // Persistence / status
    // =======================================================================
    private void ScheduleSave()
    {
        // Before the library is adopted — and forever, if the load failed — the
        // model in memory is a placeholder; never let it queue a write.
        if (!_libraryReady) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        if (!_libraryReady) return;
        Surface.FlushTexts();
        LibraryStore.Save(_library);
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        // slide-fade entrance instead of an instant text swap (#anim-roadmap)
        if (StatusText.Visibility != Visibility.Visible || StatusText.Opacity < 0.99)
            FadeIn(StatusText, 150, pop: false, slideY: 10);
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // =======================================================================
    // Theme
    // =======================================================================
    private bool _uiReady;

    // "System" theme follows Windows' light/dark preference; there is no direct
    // boolean API at this OS floor, so use the well-known background-luminance check.
    private bool SystemPrefersDark()
    {
        try
        {
            var bg = (_uiSettings ?? new Windows.UI.ViewManagement.UISettings())
                .GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return bg.R + (int)bg.G + bg.B < 384;
        }
        catch { return true; }
    }

    private bool ResolvedDark() =>
        _library.Theme == "System" ? SystemPrefersDark() : _library.Theme == "Dark";

    // Code-built UI captures its strings at build time exactly the way it
    // captures theme colours, so a language change has to repaint the same set of
    // surfaces ApplyTheme does. Anything still declared in XAML stays English
    // until it is extracted.
    private void ApplyLanguage()
    {
        Loc.SetLanguage(_library.Language);
        if (!_uiReady) return;
        try
        {
            BuildTree();
            BuildPenStrip();
            ApplyKeyPreset();   // the F1 sheet is built from localized labels
            if (GalleryPanel.Visibility == Visibility.Visible) BuildGallery();
            Surface.Refresh();
        }
        catch { }
        var name = Loc.Languages.FirstOrDefault(l => l.Tag == Loc.Current).Name ?? Loc.Current;
        ShowStatus(Loc.T("Status.LanguageChanged", name));
    }

    private void ApplyTheme()
    {
        bool dark = ResolvedDark();
        ApplyOledBlack(dark && _library.OledBlack);
        // with a live Mica/acrylic backdrop the root goes translucent so the
        // desktop reads through; OLED black and reduced transparency stay opaque
        bool translucentRoot = _backdropActive && !_reduceTransparency && !(dark && _library.OledBlack);
        RootGrid.Background = new SolidColorBrush(dark
            ? (translucentRoot ? Color.FromArgb(0xD0, 0x0F, 0x0E, 0x10)
               : _library.OledBlack ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 0x0F, 0x0E, 0x10))
            : (translucentRoot ? Color.FromArgb(0xD0, 0xF7, 0xF6, 0xF1) : Color.FromArgb(255, 0xF7, 0xF6, 0xF1)));
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        UpdateThemeToggle(dark);
        ApplyTitleBarColors(dark);
        // Code-built UI (gallery cards, tree, pen strip, calc history) captures
        // theme colours at build time and doesn't react to RequestedTheme — the
        // "half the UI stays in the old theme" bug. Rebuild those surfaces.
        if (_uiReady)
        {
            try
            {
                BuildTree();
                BuildPenStrip();
                RefreshCalcHistory();
                if (GalleryPanel.Visibility == Visibility.Visible) BuildGallery();
                Surface.Refresh();
            }
            catch { }
        }
    }

    // Pure-black dark theme for OLED displays (#32-batch2): mutates the Default
    // (dark) theme dictionary's brushes in place, the same trick ApplyLiquidness uses.
    private void ApplyOledBlack(bool on)
    {
        try
        {
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue("Default", out var d) &&
                d is ResourceDictionary rd)
            {
                if (rd["SurfaceBrush"] is SolidColorBrush s)
                    s.Color = on ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 0x0F, 0x0E, 0x10);
                if (rd["CardBrush"] is AcrylicBrush a)
                {
                    a.TintColor = on ? Color.FromArgb(255, 0x08, 0x08, 0x0A) : Color.FromArgb(255, 0x1A, 0x19, 0x1D);
                    a.FallbackColor = on ? Color.FromArgb(0xE6, 0x02, 0x02, 0x03) : Color.FromArgb(0xCC, 0x15, 0x14, 0x1A);
                }
                if (rd["CardBrushFloat"] is AcrylicBrush f)
                {
                    f.TintColor = on ? Color.FromArgb(255, 0x06, 0x06, 0x08) : Color.FromArgb(255, 0x1A, 0x19, 0x1D);
                    f.FallbackColor = on ? Color.FromArgb(0xE0, 0x02, 0x02, 0x03) : Color.FromArgb(0xC4, 0x15, 0x14, 0x1A);
                }
            }
        }
        catch { }
    }

    // ---- Apple-style sun/moon theme pill (#48) ----
    private void ThemeToggle_Tapped(object sender, TappedRoutedEventArgs e)
        => Theme_Click(sender, new RoutedEventArgs());

    private void UpdateThemeToggle(bool dark)
    {
        try
        {
            // the knob covers the INACTIVE side: light mode shows the sun
            // (knob sits right, over the moon), dark mode shows the moon.
            var a = new DoubleAnimation
            {
                To = dark ? 0.0 : 30.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(280)),
                EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(a, ThemeKnobTT);
            Storyboard.SetTargetProperty(a, "X");
            var sb = new Storyboard();
            sb.Children.Add(a);
            sb.Begin();
        }
        catch { }
    }

    // Windows accessibility preferences (#glass-roadmap): when the user turns
    // off "Transparency effects" or "Animation effects" system-wide, the app
    // respects that instead of overriding it.
    private bool _reduceTransparency;
    private bool _reduceMotion;
    private Windows.UI.ViewManagement.UISettings? _uiSettings;

    private void HookAccessibilitySettings()
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            _reduceTransparency = !ui.AdvancedEffectsEnabled;
            _reduceMotion = !ui.AnimationsEnabled;
            // these callbacks arrive off the UI thread — marshal before touching brushes
            ui.AdvancedEffectsEnabledChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
            {
                _reduceTransparency = !s.AdvancedEffectsEnabled;
                ApplyLiquidness(_library.Liquidness);
            });
            // the changed-event only exists on Windows 10 2004+; older builds
            // still get the startup snapshot above
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                ui.AnimationsEnabledChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    _reduceMotion = !s.AnimationsEnabled;
                    ApplyGlowMode();
                });
            ui.ColorValuesChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (_library.Theme == "System") ApplyTheme();
            });
            _uiSettings = ui;   // keep the subscription alive
        }
        catch { }
    }

    // ---- liquid glass transparency (#48): retints the acrylic panels live.
    //      Two depth tiers: chrome (CardBrush) and floating docks (CardBrushFloat),
    //      the latter noticeably clearer, both driven by the one Liquidness dial. ----
    private void ApplyLiquidness(double v)
    {
        v = Math.Clamp(v, 0, 1);
        try
        {
            foreach (var key in new object[] { "Light", "Default" })
            {
                if (!Application.Current.Resources.ThemeDictionaries.TryGetValue(key, out var dict) ||
                    dict is not ResourceDictionary rd) continue;
                if (rd["CardBrush"] is AcrylicBrush a)
                {
                    if (_reduceTransparency) { a.TintOpacity = 0.96; a.TintLuminosityOpacity = 0.92; }
                    else
                    {
                        // aggressive curve so the glass genuinely reads as glass:
                        // at full liquidness the pane is practically clear.
                        a.TintOpacity = 0.50 - v * 0.48;             // solid 0.50 … liquid 0.02
                        a.TintLuminosityOpacity = 0.60 - v * 0.58;   // 0.60 … 0.02
                    }
                }
                if (rd["CardBrushFloat"] is AcrylicBrush f)
                {
                    if (_reduceTransparency) { f.TintOpacity = 0.96; f.TintLuminosityOpacity = 0.92; }
                    else
                    {
                        f.TintOpacity = 0.40 - v * 0.385;            // one tier clearer than chrome
                        f.TintLuminosityOpacity = 0.48 - v * 0.465;
                    }
                }
            }
        }
        catch { }
    }

    // =======================================================================
    // Accent colour (#33): retints the glow, brand brush and system accent so
    // every glow, icon highlight and control picks up the user's colour.
    // =======================================================================
    private readonly DispatcherTimer _accentTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private bool _accentTimerHooked;

    private void SetAccent(Color c)
    {
        _library.AccentColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        ApplyAccentLive(c);
        ScheduleSave();
    }

    // Immediate retint + debounced theme refresh — shared by manual picking AND
    // accent-follow, so following the pen retints EVERYTHING too (#3-batch3).
    private Color? _accentCurrent;
    private Color _accentFrom, _accentTarget;
    private double _accentLerpT;
    private DispatcherTimer? _accentLerp;

    private static Color LerpColor(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    private void ApplyAccentLive(Color c)
    {
        // strictly linear ramp from the on-screen colour, ~220 ms (#7-batch4)
        _accentFrom = _accentCurrent ?? c;
        _accentTarget = c;
        _accentLerpT = 0;
        if (_accentLerp == null)
        {
            _accentLerp = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _accentLerp.Tick += (_, _) =>
            {
                _accentLerpT = Math.Min(1, _accentLerpT + 16.0 / 220.0);
                ApplyAccent(LerpColor(_accentFrom, _accentTarget, _accentLerpT), refreshTheme: false);
                if (_accentLerpT >= 1)
                {
                    _accentLerp!.Stop();
                    // chrome that bakes the accent into brushes follows suit (#8-batch4)
                    BuildPenStrip();
                    RefreshEraserChip();
                    if (!_accentTimerHooked)
                    {
                        _accentTimerHooked = true;
                        _accentTimer.Tick += (_, _) => { _accentTimer.Stop(); RefreshThemeForAccent(); };
                    }
                    _accentTimer.Stop();
                    _accentTimer.Start();
                }
            };
        }
        _accentLerp.Stop();
        _accentLerp.Start();
    }

    private void ApplyAccent(Color c, bool refreshTheme)
    {
        _accentCurrent = c;
        try { Surface.Accent = c; Surface.Refresh(); } catch { }
        var res = Application.Current.Resources;
        if (res["BrandOrangeBrush"] is SolidColorBrush brand) brand.Color = c;
        if (res["GlowBrush"] is LinearGradientBrush glow)
            foreach (var stop in glow.GradientStops)
                stop.Color = Color.FromArgb(stop.Color.A, c.R, c.G, c.B);
        // the glass rim keeps the accent's colour — a light, luminous tint (#51)
        if (res["GlassEdgeBrush"] is LinearGradientBrush glassEdge)
        {
            var tint = Mix(c, Colors.White, 0.45);
            foreach (var stop in glassEdge.GradientStops)
                stop.Color = Color.FromArgb(stop.Color.A, tint.R, tint.G, tint.B);
        }
        res["SystemAccentColor"] = c;
        res["SystemAccentColorLight1"] = Mix(c, Colors.White, 0.15);
        res["SystemAccentColorLight2"] = Mix(c, Colors.White, 0.30);
        res["SystemAccentColorLight3"] = Mix(c, Colors.White, 0.45);
        res["SystemAccentColorDark1"] = Mix(c, Colors.Black, 0.12);
        res["SystemAccentColorDark2"] = Mix(c, Colors.Black, 0.25);
        res["SystemAccentColorDark3"] = Mix(c, Colors.Black, 0.38);
        if (refreshTheme) RefreshThemeForAccent();
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    // Theme resources cache the accent-derived brushes, so flip the theme once
    // and back to force WinUI to re-resolve them with the new accent.
    private void RefreshThemeForAccent()
    {
        try
        {
            var cur = RootGrid.RequestedTheme;
            RootGrid.RequestedTheme = cur == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
            RootGrid.RequestedTheme = cur;
        }
        catch { }
        // commit ripple: the rim dips and re-brightens once when a new accent
        // lands — driven by the glow engine so it composes with any mode.
        if (_reduceMotion) return;
        _rippleStartMs = Environment.TickCount64;
        _glowTimer?.Start();
    }

    // ---- custom window controls (#caption) -------------------------------
    // The system title bar is removed entirely so the app draws its own
    // minimise / maximise / close in the top bar. Dragging and snap are
    // restored by handing the press to the OS as a caption hit.
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);
    private const int SW_MINIMIZE = 6;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private void ConfigureCustomTitleBar()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                op.SetBorderAndTitleBar(true, false);   // keep the resize border, drop the caption
        }
        catch { }

        // empty space in the top bar drags the window, and double-click toggles
        // maximise — exactly what a real title bar does
        TopBar.PointerPressed += (s, e) =>
        {
            if (e.GetCurrentPoint(TopBar).Properties.IsLeftButtonPressed && !OverInteractive(e.OriginalSource))
            {
                try
                {
                    ReleaseCapture();
                    SendMessage(WinRT.Interop.WindowNative.GetWindowHandle(this),
                                WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                }
                catch { }
            }
        };
        TopBar.DoubleTapped += (s, e) => { if (!OverInteractive(e.OriginalSource)) ToggleMaximise(); };

        // close goes red on hover, the one caption affordance users expect
        BtnWinClose.PointerEntered += (_, _) =>
        {
            BtnWinClose.Background = new SolidColorBrush(Color.FromArgb(255, 0xC4, 0x2B, 0x1C));
            BtnWinCloseIcon.Foreground = new SolidColorBrush(Colors.White);
        };
        BtnWinClose.PointerExited += (_, _) =>
        {
            BtnWinClose.Background = new SolidColorBrush(Colors.Transparent);
            BtnWinCloseIcon.ClearValue(FontIcon.ForegroundProperty);
        };

        UpdateFullscreenIcon();
        this.SizeChanged += (_, _) => UpdateFullscreenIcon();
    }

    // A press on any button/menu must not start a window drag.
    private static bool OverInteractive(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or ComboBox or Slider or TextBox or ScrollViewer)
                return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // also reachable by double-clicking the bar, like a real title bar
    private void ToggleMaximise()
    {
        if (AppWindow.Presenter is not OverlappedPresenter op) return;
        if (op.State == OverlappedPresenterState.Maximized) op.Restore(); else op.Maximize();
        UpdateFullscreenIcon();
    }

    private void WinMax_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);   // leave full screen
            ReapplyBorderlessCaption();
            UpdateFullscreenIcon();
            return;
        }
        ToggleMaximise();
    }

    // Switching presenters hands back a fresh OverlappedPresenter WITH the
    // system caption, which would undo the custom window controls.
    private void ReapplyBorderlessCaption()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op) op.SetBorderAndTitleBar(true, false);
        }
        catch { }
    }

    private void WinMin_Click(object sender, RoutedEventArgs e)
    {
        // OverlappedPresenter.Minimize() does nothing under the full-screen
        // presenter, so minimise the window itself in that case (#caption)
        if (AppWindow.Presenter is OverlappedPresenter op) { op.Minimize(); return; }
        try { ShowWindowNative(WinRT.Interop.WindowNative.GetWindowHandle(this), SW_MINIMIZE); } catch { }
    }

    private void WinClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ApplyTitleBarColors(bool dark)
    {
        try
        {
            if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;
            var tb = AppWindow.TitleBar;
            var bg = dark
                ? (_library.OledBlack ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 0x0F, 0x0E, 0x10))
                : Color.FromArgb(255, 0xF7, 0xF6, 0xF1);
            var fg = dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18);
            var hover = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
            var press = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0);
            tb.BackgroundColor = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.InactiveForegroundColor = fg;
            tb.ButtonBackgroundColor = bg;
            tb.ButtonInactiveBackgroundColor = bg;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonPressedBackgroundColor = press;
            tb.ButtonPressedForegroundColor = fg;
        }
        catch { /* title-bar theming is best-effort */ }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _library.Theme = ResolvedDark() ? "Light" : "Dark";
        ApplyTheme();
        ScheduleSave();
        ShowStatus(_library.Theme == "Dark" ? "Dark mode on." : "Light mode on.");
    }

    // =======================================================================
    // Pen panel docking
    // =======================================================================
    private void ApplyPenDock()
    {
        string dock = _library.PenDock;
        bool vertical = dock is "Left" or "Right";

        PenStack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        PresetPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        PenScroll.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Enabled;
        PenScroll.VerticalScrollMode = vertical ? ScrollMode.Enabled : ScrollMode.Disabled;
        PenScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        PenScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        PenScroll.MaxWidth = vertical ? 200 : 1280;
        PenScroll.MaxHeight = vertical ? 760 : 96;

        switch (dock)
        {
            case "Top":
                PenRow.HorizontalAlignment = HorizontalAlignment.Center;
                PenRow.VerticalAlignment = VerticalAlignment.Top;
                PenRow.Margin = new Thickness(24, 12, 24, 0);
                SetShowBtn(HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, 12, 0, 0));
                break;
            case "Left":
                PenRow.HorizontalAlignment = HorizontalAlignment.Left;
                PenRow.VerticalAlignment = VerticalAlignment.Center;
                PenRow.Margin = new Thickness(12, 24, 0, 24);
                SetShowBtn(HorizontalAlignment.Left, VerticalAlignment.Center, new Thickness(12, 0, 0, 0));
                break;
            case "Right":
                PenRow.HorizontalAlignment = HorizontalAlignment.Right;
                PenRow.VerticalAlignment = VerticalAlignment.Center;
                PenRow.Margin = new Thickness(0, 24, 12, 24);
                SetShowBtn(HorizontalAlignment.Right, VerticalAlignment.Center, new Thickness(0, 0, 12, 0));
                break;
            default:
                PenRow.HorizontalAlignment = HorizontalAlignment.Center;
                PenRow.VerticalAlignment = VerticalAlignment.Bottom;
                PenRow.Margin = new Thickness(24, 0, 24, 16);
                SetShowBtn(HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, 16));
                break;
        }
        PenRow.RenderTransform = null;
    }

    private void SetShowBtn(HorizontalAlignment h, VerticalAlignment v, Thickness m)
    {
        PenRowShowBtn.HorizontalAlignment = h;
        PenRowShowBtn.VerticalAlignment = v;
        PenRowShowBtn.Margin = m;
    }

    private void DockPenRowFromPosition()
    {
        try
        {
            var t = PenRow.TransformToVisual(CanvasArea);
            var tl = t.TransformPoint(new Point(0, 0));
            double cx = tl.X + PenRow.ActualWidth / 2.0;
            double cy = tl.Y + PenRow.ActualHeight / 2.0;
            double w = CanvasArea.ActualWidth, h = CanvasArea.ActualHeight;
            double dl = cx, dr = w - cx, dt = cy, db = h - cy;
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            _library.PenDock = min == dl ? "Left" : min == dr ? "Right" : min == dt ? "Top" : "Bottom";
        }
        catch { }
        ApplyPenDock();
        ScheduleSave();
    }

    // =======================================================================
    // Pen presets
    // =======================================================================
    private void SeedPens()
    {
        if (_library.Pens.Count > 0) return;
        _library.Pens.AddRange(new[]
        {
            new PenPreset { Name = "Ink",          Pen = PenType.Standard,    Color = "#141413", Size = 3.5f, Sens = 1f },
            new PenPreset { Name = "Sky note",     Pen = PenType.Standard,    Color = "#6A9BCC", Size = 3.5f, Sens = 1f },
            new PenPreset { Name = "Red fountain", Pen = PenType.Fountain,    Color = "#D32F2F", Size = 5f,   Sens = 1.4f },
            new PenPreset { Name = "Marker",       Pen = PenType.Highlighter, Color = "#FBC02D", Size = 6f,   Sens = 1f }
        });
    }

    // Original vector silhouettes for every pen type (#15-batch2) — no more
    // generic emoji. 16x16 path data, filled with a contrast colour over the
    // preset's own colour disc.
    private static string PenIconData(PenType t) => t switch
    {
        // approved v2 set (24-grid)
        PenType.Ballpoint => "M14.8 6.4 a1.8 1.8 0 0 1 2.6 2.6 L10.8 15.6 8.2 13 Z M8.2 13 10.8 15.6 4.8 19 Z M16.6 4.2 18.6 6.2 19.8 5 A1.7 1.7 0 0 0 17.8 3 Z",
        PenType.Rollerball => "M15 5.8 a2.1 2.1 0 0 1 3.1 3.1 L11 16 7.9 12.9 Z M7.9 12.9 11 16 5.6 18.4 Z M4.5 18.3 a1.35 1.35 0 1 0 0.01 0 Z",
        PenType.Gel => "M15.4 6.2 a1.9 1.9 0 0 1 2.7 2.7 L11 16 8.3 13.3 Z M8.3 13.3 11 16 4.6 19.7 Z M18.4 14.6 19.1 16.5 21 17.2 19.1 17.9 18.4 19.8 17.7 17.9 15.8 17.2 17.7 16.5 Z",
        PenType.Monoline => "M15.5 5.5 a1.9 1.9 0 0 1 2.9 2.9 L12 15.3 8.6 11.9 Z M9.8 13.1 10.9 14.2 4 20 Z",
        PenType.Brush => "M15.2 5.2 a2.4 2.4 0 0 1 3.6 3.6 L13.6 14 10 10.4 Z M10 10.4 13.6 14 C11.6 16 9.6 17 7.6 17.6 C6 18.1 4.6 19.4 4 20 C4.4 18.8 5.6 17.4 6.2 15.8 C7 13.8 8.2 12.2 10 10.4 Z",
        PenType.Pencil => "M15.3 4.7 a2 2 0 0 1 4 4 l-0.9 0.9 -4 -4 Z M13.6 6.4 l4 4 L10.6 17.4 6.6 13.4 Z M6.6 13.4 l4 4 L4 20 Z",
        PenType.Crayon => "M14 6 L18 10 L9.4 18.6 5.4 14.6 Z M5.4 14.6 9.4 18.6 7.6 19.2 4.8 19.2 4.8 16.4 Z M12.2 7.8 16.2 11.8 14.8 13.2 10.8 9.2 Z",
        PenType.Highlighter => "M14 4.6 L19.4 10 L12.6 16.8 L7.2 11.4 Z M7.2 11.4 L12.6 16.8 L10.2 17.6 5.6 17.6 6.4 13.2 Z M3 20.4 H14.6 V22.6 H3 Z",
        // kept v1 by request (16-grid)
        PenType.Watercolor => "M3 13 C5.5 12.5 6.5 11 7.5 9.5 L11.5 4.5 L12.5 5.5 L8.5 10.5 C7.5 12 5.5 13 3 13 Z M13.4 9 C14.4 10.2 14.4 11.4 13.4 12 C12.4 11.4 12.4 10.2 13.4 9 Z",
        // detailed redesigns (24-grid); fountain matches the user's reference:
        // a classic vertical nib — grip, band, flared nib, breather hole, slit
        PenType.Fountain => "M17.5 2.3 A2.55 2.55 0 0 1 21.7 6.5 L20.1 8.1 L15.9 3.9 Z M14.9 4.9 L19.1 9.1 L11.2 17 L7 12.8 Z M6.4 13.4 L10.6 17.6 C9.2 19.6 6.4 20.7 2.9 21.1 C3.3 17.6 4.4 14.8 6.4 13.4 Z M7.0 15.45 a0.85 0.85 0 1 0 0.001 0 Z M4.2 19.5 L6.0 17.5 L6.6 18.1 L4.8 20.1 Z M18.9 10.1 L14.0 15.0 L14.9 15.9 L19.8 11.0 Z",
        PenType.Calligraphy => "M4 20 C5.6 14.6 8.8 9.6 13.6 5.4 C15.6 3.7 17.6 2.7 19.6 2.3 C19.3 4 18.6 5.8 17.6 7.6 L15.8 7.5 L16.8 9 C16 10.3 15 11.6 13.9 12.8 L12.1 12.7 L13.1 14.2 C11.9 15.4 10.5 16.5 9 17.5 L7.3 17.3 L8.2 18.6 C6.9 19.3 5.5 19.8 4 20 Z M5.4 18.6 C7.6 13.9 10.8 9.5 16.9 4.5 L17.4 5 C11.6 10 8.4 14.4 6 19 Z",
        PenType.Marker => "M12.4 3.2 20.8 11.6 15.6 16.8 7.2 8.4 Z M13.9 4.7 19.3 10.1 18.5 10.9 13.1 5.5 Z M12.8 8.6 15.4 11.2 13.6 13 11 10.4 Z M7.2 8.4 15.6 16.8 11 18.2 5.8 18.2 5.8 13 Z M7.9 12.6 11.4 16.1 10.6 16.9 7.1 13.4 Z",
        PenType.FeltTip => "M14.6 3 21 9.4 17.2 13.2 10.8 6.8 Z M15.9 4.3 19.7 8.1 19 8.8 15.2 5 Z M10.8 6.8 17.2 13.2 15.6 14.8 9.2 8.4 Z M9.2 8.4 15.6 14.8 7.3 18.1 5.9 16.7 Z M7.3 18.1 5.9 16.7 4 20 Z M12.2 10 13.6 11.4 6.9 16.5 6.3 15.9 Z",
        // Standard: kept v1 by request (16-grid)
        _ => "M3.5 12.5 L5 9 L11.5 2.5 L13.5 4.5 L7 11 Z M12.3 1.7 L14.3 3.7 L15 3 A1.4 1.4 0 0 0 13 1 Z"
    };

    // OneNote-style two-tone pen chips (#3-batch4): a neutral-grey body plus a
    // second path that carries the pen's own colour on the tip and top band, on
    // a shared 30x46 grid so both paths scale together. Replaces the colour disc.
    private static (string Body, string Colour) PenChipData(PenType t) => t switch
    {
        PenType.Fountain => ("M10 7.5 H20 V28 L15 33 L10 28 Z",
            "M10 3 H20 V6.5 H10 Z M15 43 C12.2 40.4 11.2 37.8 12.6 33.4 L15 31 L17.4 33.4 C18.8 37.8 17.8 40.4 15 43 Z"),
        PenType.Brush or PenType.Watercolor => ("M12 4 H18 V22 H12 Z M11.3 22 H18.7 V27 H11.3 Z",
            "M12 4 H18 V7.5 H12 Z M15 44 C11.5 39 11 33 12.6 27.5 H17.4 C19 33 18.5 39 15 44 Z"),
        PenType.Pencil or PenType.Crayon => ("M10 7 H20 V32 H10 Z",
            "M10 3 H20 V6.5 H10 Z M10 32 H20 L15 43 Z"),
        PenType.Marker => ("M9.5 4.5 H20.5 C21.3 4.5 22 5.2 22 6 V31 H8 V6 C8 5.2 8.7 4.5 9.5 4.5 Z",
            "M9.5 0.8 H20.5 V3.6 H9.5 Z M11 31.5 H19 L17.2 42 H12.8 Z"),
        PenType.FeltTip => ("M11 6 H19 V33 L15 36.5 L11 33 Z",
            "M11 2.6 H19 V5.6 H11 Z M15 43 C13.4 41 12.6 38.6 13.2 35.4 L15 33.8 L16.8 35.4 C17.4 38.6 16.6 41 15 43 Z"),
        PenType.Highlighter => ("M8.5 5 H21.5 V28 H8.5 Z",
            "M8.5 2 H21.5 V5 H8.5 Z M8.5 28 H21.5 L18 42 H12 Z"),
        PenType.Calligraphy => ("M10 7.5 H20 V28 L15 33 L10 28 Z",
            "M10 3 H20 V6.5 H10 Z M12 33 L20 37 L17.5 43 L13.5 41 Z"),
        _ => ("M10 7.5 H20 V30 L15 36 L10 30 Z",
            "M10 3 H20 V6.5 H10 Z M15 43.5 L11.6 36 H18.4 Z")
    };

    private static Microsoft.UI.Xaml.Media.Geometry ParseGeometry(string data)
    {
        var p = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        var geo = p.Data;
        p.Data = null;   // detach: a Geometry can't be parented to two Paths at once (#3-batch4 fix)
        return geo;
    }

    private Color ChipBodyGrey() =>
        ((FrameworkElement)Content).ActualTheme == ElementTheme.Dark
            ? Color.FromArgb(255, 0xC3, 0xBE, 0xB3)
            : Color.FromArgb(255, 0x7E, 0x7A, 0x71);

    // Builds the two-tone chip visual and hands back the two live paths and the
    // lift transform so selection and flyout edits can update it in place.
    private FrameworkElement BuildTwoToneChip(string bodyData, string colData, Color color,
        out Microsoft.UI.Xaml.Shapes.Path bodyPath, out Microsoft.UI.Xaml.Shapes.Path colPath,
        out TranslateTransform lift)
    {
        bodyPath = new Microsoft.UI.Xaml.Shapes.Path { Fill = new SolidColorBrush(ChipBodyGrey()) };
        colPath = new Microsoft.UI.Xaml.Shapes.Path { Fill = new SolidColorBrush(color) };
        // a malformed path must never take the whole pen row (and app) down
        try { bodyPath.Data = ParseGeometry(bodyData); } catch { }
        try { colPath.Data = ParseGeometry(colData); } catch { }
        var art = new Grid { Width = 30, Height = 46 };
        art.Children.Add(bodyPath);
        art.Children.Add(colPath);
        var chip = new Viewbox { Child = art, Width = 20, Height = 31, Stretch = Stretch.Uniform,
                                 VerticalAlignment = VerticalAlignment.Bottom };
        lift = new TranslateTransform();
        chip.RenderTransform = lift;
        var content = new Grid { Width = 26, Height = 40 };
        content.Children.Add(chip);
        return content;
    }

    // The selected pen (in pen mode) or the eraser (in eraser mode) pops up out
    // of the row; everything else sits tucked at the baseline. The lift replaces
    // the old accent ring as the selection cue.
    private readonly Dictionary<Guid, TranslateTransform> _penLifts = new();
    private TranslateTransform? _eraserLift;
    private void RefreshPenSelection()
    {
        bool penMode = Surface.Tool == ToolType.Pen;
        foreach (var kv in _penLifts)
            kv.Value.Y = (penMode && kv.Key == _activePresetId) ? -8 : 0;
        if (_eraserLift != null)
            _eraserLift.Y = Surface.Tool == ToolType.Eraser ? -8 : 0;
    }

    private static Microsoft.UI.Xaml.Shapes.Path MakeIconPath(string data, Color fill, double size = 14)
    {
        var p = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        p.Fill = new SolidColorBrush(fill);
        p.Width = size;
        p.Height = size;
        p.Stretch = Stretch.Uniform;
        p.HorizontalAlignment = HorizontalAlignment.Center;
        p.VerticalAlignment = VerticalAlignment.Center;
        return p;
    }

    private static void SetPenChipIcon(Grid host, PenType t, Color penColor)
    {
        host.Children.Clear();
        var fg = ColorUtil.IsDark(penColor) ? Colors.White : Color.FromArgb(255, 0x14, 0x14, 0x13);
        try { host.Children.Add(MakeIconPath(PenIconData(t), fg)); } catch { }
    }


    private PenPreset? ActivePreset() => _library?.Pens.FirstOrDefault(x => x.Id == _activePresetId);

    private void BuildPenStrip()
    {
        PresetPanel.Children.Clear();
        BuildEraserChip();
        // freshly built chips must honour touch mode (#36)
        if (_library.TouchMode)
            DispatcherQueue.TryEnqueue(() => ApplyTouchMode(true));
        _penLifts.Clear();
        foreach (var preset in _library.Pens)
        {
            var p = preset;
            var color = ColorUtil.Parse(p.Color);
            var (bodyData, colData) = PenChipData(p.Pen);
            var content = BuildTwoToneChip(bodyData, colData, color,
                out var bodyPath, out var colPath, out var lift);
            _penLifts[p.Id] = lift;

            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(Colors.Transparent)
            };
            ToolTipService.SetToolTip(btn, $"{p.Name} (right-click to edit)");
            btn.Click += (_, _) => ApplyPreset(p);
            btn.ContextFlyout = CreatePresetFlyout(p, bodyPath, colPath);

            PresetPanel.Children.Add(btn);
        }
        RefreshPenSelection();
    }

    private void BuildEraserChip()
    {
        // OneNote-style block eraser: grey body, soft pink-purple base (#3-batch4)
        var content = BuildTwoToneChip(
            "M9 8 C9 6.3 10.3 5 12 5 H18 C19.7 5 21 6.3 21 8 V26 H9 Z",
            "M9 26 H21 V37 C21 38.7 19.7 40 18 40 H12 C10.3 40 9 38.7 9 37 Z",
            Color.FromArgb(255, 0xC0, 0x7C, 0xB4),
            out _, out _, out var elift);
        _eraserLift = elift;

        var chip = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Bottom,
            Background = new SolidColorBrush(Colors.Transparent)
        };
        ToolTipService.SetToolTip(chip, "Eraser (right-click to pick point or stroke mode)");
        chip.Click += (_, _) => SelectTool("Eraser");

        var fly = new Flyout();
        var panel = new StackPanel { Spacing = 6, Width = 220 };
        var rbPoint = new RadioButton { Content = "Point eraser — erases only what you touch", FontSize = 12, IsChecked = Surface.EraserMode == EraserMode.Point };
        var rbObject = new RadioButton { Content = "Stroke eraser — removes whole strokes", FontSize = 12, IsChecked = Surface.EraserMode == EraserMode.Object };
        rbPoint.Checked += (_, _) => { Surface.EraserMode = EraserMode.Point; _library.LastEraserMode = "Point"; ScheduleSave(); SelectTool("Eraser"); };
        rbObject.Checked += (_, _) => { Surface.EraserMode = EraserMode.Object; _library.LastEraserMode = "Object"; ScheduleSave(); SelectTool("Eraser"); };
        panel.Children.Add(rbPoint);
        panel.Children.Add(rbObject);
        fly.Content = panel;
        chip.ContextFlyout = fly;

        _eraserChip = chip;
        PresetPanel.Children.Add(chip);
    }

    private void RefreshEraserChip()
    {
        RefreshPenSelection();   // the lift now shows which tool is active (#3-batch4)
    }

    private Flyout CreatePresetFlyout(PenPreset p, Microsoft.UI.Xaml.Shapes.Path bodyPath, Microsoft.UI.Xaml.Shapes.Path colPath)
    {
        var fly = new Flyout();
        bool built = false;
        fly.Opening += (_, _) =>
        {
            if (built) return;
            built = true;

            var panel = new StackPanel { Spacing = 8, Width = 272 };

            var nameBox = new TextBox { Header = "Name", Text = p.Name };
            nameBox.TextChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text)) p.Name = nameBox.Text.Trim();
                ScheduleSave();
            };
            panel.Children.Add(nameBox);

            var typeCombo = new ComboBox
            {
                Header = "Pen type",
                SelectedIndex = -1,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            string[] penNames = { "Standard pen", "Brush pen", "Fountain pen", "Highlighter", "Pencil", "Marker (chisel)", "Calligraphy",
                                  "Crayon", "Watercolour", "Monoline", "Rollerball", "Gel pen", "Ballpoint", "Felt-tip" };
            for (int ti = 0; ti < penNames.Length; ti++)
            {
                var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                try { rowPanel.Children.Add(MakeIconPath(PenIconData((PenType)ti), Color.FromArgb(255, 0x8a, 0x88, 0x82), 13)); } catch { }
                rowPanel.Children.Add(new TextBlock { Text = penNames[ti], VerticalAlignment = VerticalAlignment.Center });
                typeCombo.Items.Add(new ComboBoxItem { Content = rowPanel });
            }
            typeCombo.SelectedIndex = (int)p.Pen;
            typeCombo.SelectionChanged += (_, _) =>
            {
                if (typeCombo.SelectedIndex < 0) return;
                p.Pen = (PenType)typeCombo.SelectedIndex;
                var (bd, cd) = PenChipData(p.Pen);
                bodyPath.Data = ParseGeometry(bd);
                colPath.Data = ParseGeometry(cd);
                if (_activePresetId == p.Id) Surface.Pen = p.Pen;
                ScheduleSave();
            };
            panel.Children.Add(typeCombo);

            ColorPicker? pickerRef = null;
            void SetColor(Color c)
            {
                var hex = ColorUtil.ToHex(c);
                p.Color = hex;
                colPath.Fill = new SolidColorBrush(c);
                if (_activePresetId == p.Id) Surface.PenColor = c;

                _library.RecentColors.Remove(hex);
                _library.RecentColors.Insert(0, hex);
                if (_library.RecentColors.Count > 16) _library.RecentColors.RemoveAt(16);

                ScheduleSave();
            }

            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var hex in QuickColors)
            {
                var c = ColorUtil.Parse(hex);
                var sb = new Button
                {
                    Padding = new Thickness(3),
                    Content = new Ellipse
                    {
                        Width = 16, Height = 16,
                        Fill = new SolidColorBrush(c),
                        Stroke = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
                        StrokeThickness = 1
                    }
                };
                sb.Click += (_, _) =>
                {
                    SetColor(c);
                    if (pickerRef != null)
                    {
                        _syncingUi = true;
                        pickerRef.Color = c;
                        _syncingUi = false;
                    }
                };
                swatches.Children.Add(sb);
            }
            panel.Children.Add(swatches);

            panel.Children.Add(new TextBlock { Text = "Recent colours", FontSize = 11, Opacity = 0.7 });
            var recents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var hex in _library.RecentColors.Take(8))
            {
                var c = ColorUtil.Parse(hex);
                var rb = new Button
                {
                    Padding = new Thickness(3),
                    Content = new Ellipse
                    {
                        Width = 16, Height = 16,
                        Fill = new SolidColorBrush(c),
                        Stroke = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
                        StrokeThickness = 1
                    }
                };
                rb.Click += (_, _) =>
                {
                    SetColor(c);
                    if (pickerRef != null)
                    {
                        _syncingUi = true;
                        pickerRef.Color = c;
                        _syncingUi = false;
                    }
                };
                recents.Children.Add(rb);
            }
            if (_library.RecentColors.Count == 0)
            {
                recents.Children.Add(new TextBlock { Text = "None yet", FontSize = 11, Opacity = 0.5, Margin = new Thickness(4, 2, 0, 2) });
            }
            panel.Children.Add(recents);

            var picker = new ColorPicker
            {
                IsAlphaEnabled = false,
                IsMoreButtonVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Box,
                Color = ColorUtil.Parse(p.Color)
            };
            pickerRef = picker;
            picker.ColorChanged += (_, args) =>
            {
                if (_syncingUi) return;
                SetColor(args.NewColor);
            };
            panel.Children.Add(new Expander
            {
                Header = "Custom colour",
                Content = picker,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            var sizeLabel = new TextBlock { Text = $"Size: {p.Size:0.#}", FontSize = 12 };
            var size = new Slider { Minimum = 1, Maximum = 24, StepFrequency = 0.5, Value = p.Size };
            size.ValueChanged += (_, args) =>
            {
                p.Size = (float)args.NewValue;
                sizeLabel.Text = $"Size: {p.Size:0.#}";
                if (_activePresetId == p.Id) Surface.PenSize = p.Size;
                ScheduleSave();
            };
            panel.Children.Add(sizeLabel);
            panel.Children.Add(size);

            var sensLabel = new TextBlock { Text = $"Pressure response: {p.Sens:0.0}×", FontSize = 12 };
            var sens = new Slider { Minimum = 0.2, Maximum = 2.5, StepFrequency = 0.1, Value = p.Sens };
            sens.ValueChanged += (_, args) =>
            {
                p.Sens = (float)args.NewValue;
                sensLabel.Text = $"Pressure response: {p.Sens:0.0}×";
                if (_activePresetId == p.Id) Surface.PenSensitivity = p.Sens;
                ScheduleSave();
            };
            panel.Children.Add(sensLabel);
            panel.Children.Add(sens);

            var stabLabel = new TextBlock { Text = $"Stabiliser: {p.Stabiliser * 100:0}%", FontSize = 12 };
            var stab = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = p.Stabiliser * 100 };
            stab.ValueChanged += (_, args) =>
            {
                p.Stabiliser = (float)args.NewValue / 100f;
                stabLabel.Text = $"Stabiliser: {p.Stabiliser * 100:0}%";
                if (_activePresetId == p.Id) Surface.PenStabiliser = p.Stabiliser;
                ScheduleSave();
            };
            panel.Children.Add(stabLabel);
            panel.Children.Add(stab);

            // Pressure response (#curve): Soft / Hard presets, or a Custom
            // curve with THREE draggable points — all move vertically, the
            // outer two move sideways as well. The engine interpolates
            // piecewise-linearly through (0,0), the three points, and (1,1).
            const double cvW = 240, cvH = 116, cvPad = 12;
            double plotW = cvW - 2 * cvPad, plotH = cvH - 2 * cvPad;

            double[] cpts = p.PressureCurve is { Count: >= 6 } pc6
                ? new[] { (double)pc6[0], pc6[1], pc6[2], pc6[3], pc6[4], pc6[5] }
                : p.PressureCurve is { Count: >= 3 } pc3
                    ? new[] { 0.25, Math.Clamp(pc3[1] - 0.15, 0.02, 0.98), 0.5, (double)pc3[1], 0.75, Math.Clamp(pc3[1] + 0.35, 0.02, 0.98) }
                    : new[] { 0.25, 0.25, 0.5, 0.5, 0.75, 0.75 };   // identity diagonal

            var modeCombo = new ComboBox
            {
                Header = "Pressure response",
                ItemsSource = new[] { "Soft (thickens easily)", "Hard (needs pressure)", "Custom curve" },
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            var curveHost = new Grid { Width = cvW, Height = cvH, HorizontalAlignment = HorizontalAlignment.Left };
            curveHost.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(24, 128, 128, 128)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6)
            });
            var curveCanvas = new Canvas();
            curveHost.Children.Add(curveCanvas);
            curveCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = cvPad, Y1 = cvPad + plotH, X2 = cvPad + plotW, Y2 = cvPad,
                Stroke = new SolidColorBrush(Color.FromArgb(50, 128, 128, 128)),
                StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 }
            });
            var poly = new Microsoft.UI.Xaml.Shapes.Polyline
            {
                Stroke = new SolidColorBrush(Surface.Accent), StrokeThickness = 2.5
            };
            curveCanvas.Children.Add(poly);
            var knobs = new Microsoft.UI.Xaml.Shapes.Ellipse[3];
            for (int ki = 0; ki < 3; ki++)
            {
                knobs[ki] = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 15, Height = 15,
                    Fill = new SolidColorBrush(Surface.Accent),
                    Stroke = new SolidColorBrush(Colors.White), StrokeThickness = 2
                };
                curveCanvas.Children.Add(knobs[ki]);
            }
            var curveCaption = new TextBlock
            {
                Text = "Drag the dots — the outer two slide sideways too.",
                FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
            };

            double PxX(double x) => cvPad + x * plotW;
            double PxY(double y) => cvPad + (1 - y) * plotH;
            void RedrawCurve()
            {
                poly.Points = new PointCollection
                {
                    new Point(PxX(0), PxY(0)),
                    new Point(PxX(cpts[0]), PxY(cpts[1])),
                    new Point(PxX(cpts[2]), PxY(cpts[3])),
                    new Point(PxX(cpts[4]), PxY(cpts[5])),
                    new Point(PxX(1), PxY(1))
                };
                for (int ki = 0; ki < 3; ki++)
                {
                    Canvas.SetLeft(knobs[ki], PxX(cpts[ki * 2]) - 7.5);
                    Canvas.SetTop(knobs[ki], PxY(cpts[ki * 2 + 1]) - 7.5);
                }
            }
            void CommitCurve()
            {
                p.PressureCurve = new List<float>
                {
                    (float)cpts[0], (float)cpts[1], (float)cpts[2],
                    (float)cpts[3], (float)cpts[4], (float)cpts[5]
                };
                if (_activePresetId == p.Id) Surface.PenPressureCurve = p.PressureCurve;
                ScheduleSave();
            }
            int dragKnob = -1;
            curveHost.PointerPressed += (s2, e2) =>
            {
                var pt = e2.GetCurrentPoint(curveHost).Position;
                double best = 24 * 24; dragKnob = -1;
                for (int ki = 0; ki < 3; ki++)
                {
                    double dx = pt.X - PxX(cpts[ki * 2]), dy = pt.Y - PxY(cpts[ki * 2 + 1]);
                    double d2 = dx * dx + dy * dy;
                    if (d2 < best) { best = d2; dragKnob = ki; }
                }
                if (dragKnob >= 0) curveHost.CapturePointer(e2.Pointer);
            };
            curveHost.PointerMoved += (s2, e2) =>
            {
                if (dragKnob < 0) return;
                var pt = e2.GetCurrentPoint(curveHost).Position;
                double ny = Math.Clamp(1 - (pt.Y - cvPad) / plotH, 0, 1);
                cpts[dragKnob * 2 + 1] = ny;
                if (dragKnob != 1)   // the middle point keeps its x; the outer two slide
                {
                    double nx = Math.Clamp((pt.X - cvPad) / plotW, 0.02, 0.98);
                    if (dragKnob == 0) nx = Math.Min(nx, cpts[2] - 0.05);
                    else nx = Math.Max(nx, cpts[2] + 0.05);
                    cpts[dragKnob * 2] = Math.Clamp(nx, 0.02, 0.98);
                }
                RedrawCurve();
                CommitCurve();
            };
            curveHost.PointerReleased += (s2, e2) => { dragKnob = -1; curveHost.ReleasePointerCaptures(); };

            bool syncingCurve = true;
            modeCombo.SelectedIndex = p.PressureCurve is { Count: >= 6 } ? 2
                : p.PressureCurve is { Count: >= 3 } lm ? (lm[1] > 0.5f ? 0 : 1)
                : 2;   // null (linear) shows Custom with the identity diagonal, untouched until dragged
            curveHost.Visibility = modeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            curveCaption.Visibility = curveHost.Visibility;
            modeCombo.SelectionChanged += (_, _) =>
            {
                if (syncingCurve) return;
                switch (modeCombo.SelectedIndex)
                {
                    case 0: p.PressureCurve = new List<float> { 0f, 0.6f, 1f }; break;
                    case 1: p.PressureCurve = new List<float> { 0f, 0.2f, 1f }; break;
                    case 2: CommitCurve(); break;   // adopt the drawn points
                }
                if (_activePresetId == p.Id) Surface.PenPressureCurve = p.PressureCurve;
                curveHost.Visibility = modeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                curveCaption.Visibility = curveHost.Visibility;
                ScheduleSave();
            };
            syncingCurve = false;

            RedrawCurve();
            panel.Children.Add(modeCombo);
            panel.Children.Add(curveHost);
            panel.Children.Add(curveCaption);

            panel.Children.Add(new TextBlock
            {
                Text = "Brush and fountain pens react most: press lightly for a hairline, hard for a broad stroke. Stabiliser smooths out pen jitter.",
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });

            var del = new Button { Content = "Delete this pen", HorizontalAlignment = HorizontalAlignment.Stretch };
            del.Click += (_, _) =>
            {
                if (_library.Pens.Count <= 1)
                {
                    ShowStatus("Keep at least one pen — add another first.");
                    return;
                }
                fly.Hide();
                _library.Pens.Remove(p);
                if (_activePresetId == p.Id) ApplyPreset(_library.Pens[0]);
                else BuildPenStrip();
                ScheduleSave();
            };
            panel.Children.Add(del);

            fly.Content = panel;
        };
        fly.Closed += (_, _) => BuildPenStrip(); // refresh tooltips/icons after edits
        return fly;
    }

    private void ApplyPreset(PenPreset p)
    {
        Surface.Pen = p.Pen;
        Surface.PenColor = ColorUtil.Parse(p.Color);
        Surface.PenSize = p.Size;
        Surface.PenSensitivity = p.Sens;
        Surface.PenStabiliser = p.Stabiliser;
        Surface.PenPressureCurve = p.PressureCurve;
        _activePresetId = p.Id;
        SelectTool("Pen");
        BuildPenStrip();
        // accent-follow (#6-batch2): the app tints itself to the active pen.
        // Applied visually only — the stored manual AccentColor is untouched,
        // so switching back to Manual restores the user's own pick.
        if (_library.AccentFollow == "Pen")
            try { ApplyAccentLive(ColorUtil.Parse(p.Color)); } catch { }
    }

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("Name this pen", $"My {Surface.Pen}");
        if (name == null) return;
        var p = new PenPreset
        {
            Name = name,
            Pen = Surface.Pen,
            Color = ColorUtil.ToHex(Surface.PenColor),
            Size = Surface.PenSize,
            Sens = Surface.PenSensitivity,
            Stabiliser = Surface.PenStabiliser,
            PressureCurve = Surface.PenPressureCurve
        };
        _library.Pens.Add(p);
        _activePresetId = p.Id;
        BuildPenStrip();
        ScheduleSave();
        ShowStatus($"Saved pen “{name}”. Right-click it to change its type, colour or feel.");
    }

    // =======================================================================
    // Notebook tree
    // =======================================================================
    private NotePage NewPage(string name, Notebook? parentNotebook = null)
    {
        var nb = parentNotebook ?? _curNb ?? ResolveSelection().nb;
        return new NotePage
        {
            Name = name,
            Background = nb?.DefaultBackground ?? _library.DefaultBackground,
            Grid = nb?.DefaultGrid ?? _library.DefaultGrid,
            GridSpacing = nb?.DefaultGridSpacing ?? _library.DefaultGridSpacing
        };
    }

    private void BuildTree()
    {
        // Everything starts collapsed; remember what the user expanded so a
        // rebuild (after add / rename / reorder / move) keeps their state (#29).
        var expanded = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Scan(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (n.IsExpanded && n.Content != null) expanded.Add(n.Content);
                Scan(n.Children);
            }
        }
        Scan(NotebookTree.RootNodes);

        NotebookTree.RootNodes.Clear();
        foreach (var nb in _library.Notebooks)
        {
            var nbNode = new TreeViewNode { Content = nb, IsExpanded = expanded.Contains(nb) };
            foreach (var sec in nb.Sections)
            {
                var secNode = new TreeViewNode { Content = sec, IsExpanded = expanded.Contains(sec) };
                foreach (var pg in sec.Pages)
                    secNode.Children.Add(new TreeViewNode { Content = pg });
                nbNode.Children.Add(secNode);
            }
            NotebookTree.RootNodes.Add(nbNode);
        }
    }

    private TreeViewNode? FindNode(object content)
    {
        TreeViewNode? Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Content, content)) return n;
                var hit = Walk(n.Children);
                if (hit != null) return hit;
            }
            return null;
        }
        return Walk(NotebookTree.RootNodes);
    }

    // Right-click on a tree item: rename / delete / move / lock (#39)
    private void NotebookTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // walk up to the TreeViewItem that was clicked
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not TreeViewItem)
            el = VisualTreeHelper.GetParent(el);
        if (el is not TreeViewItem item) return;
        var node = NotebookTree.NodeFromContainer(item);
        if (node?.Content == null) return;
        _selNode = node;
        NotebookTree.SelectedNode = node;

        var fly = new MenuFlyout();
        void Add(string txt, RoutedEventHandler h)
        {
            var it = new MenuFlyoutItem { Text = txt };
            it.Click += h;
            fly.Items.Add(it);
        }
        Add("Rename…", Rename_Click);
        Add("Delete…", DeleteItem_Click);
        fly.Items.Add(new MenuFlyoutSeparator());
        Add("Move up", MoveUp_Click);
        Add("Move down", MoveDown_Click);
        if (node.Content is Section or NotePage)
            Add("Move to…", MoveTo_Click);
        if (node.Content is Notebook nb)
        {
            fly.Items.Add(new MenuFlyoutSeparator());
            Add(nb.PasswordHash == null ? "Lock…" : "Remove lock…", LockToggle_Click);
        }
        fly.ShowAt(NotebookTree, e.GetPosition(NotebookTree));
        e.Handled = true;
    }

    // Keep the tree tidy: only the notebook/section being worked on expands.
    private void ExpandCurrentInTree()
    {
        foreach (var nbNode in NotebookTree.RootNodes)
        {
            if (!ReferenceEquals(nbNode.Content, _curNb)) continue;
            nbNode.IsExpanded = true;
            foreach (var secNode in nbNode.Children)
                if (ReferenceEquals(secNode.Content, _curSec))
                    secNode.IsExpanded = true;
        }
    }

    private async void NotebookTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        object? item = args.InvokedItem;
        if (item is TreeViewNode node) item = node.Content;
        if (item == null) return;
        _selNode = FindNode(item);

        if (item is NotePage page)
        {
            var (nb, sec) = FindContext(page);
            if (nb != null && sec != null)
            {
                if (!await EnsureUnlockedAsync(nb)) return; // password gate (#23)
                SwitchToPage(nb, sec, page);
            }
        }
    }

    // ---- password locking (#23) ----
    private static string HashPw(string pw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pw)));
    }

    private async Task<string?> PromptPasswordAsync(string title)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    private async Task<bool> EnsureUnlockedAsync(Notebook nb)
    {
        if (nb.PasswordHash == null || _unlockedNotebooks.Contains(nb.Id)) return true;
        var pw = await PromptPasswordAsync($"“{nb.Name}” is locked");
        if (pw == null) return false;
        if (HashPw(pw) == nb.PasswordHash) { _unlockedNotebooks.Add(nb.Id); return true; }
        ShowStatus("Incorrect password.");
        return false;
    }

    private async void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        var (nb, _, _) = ResolveSelection();
        if (nb == null) { ShowStatus("Select a notebook to lock or unlock."); return; }
        if (nb.PasswordHash == null)
        {
            var pw = await PromptPasswordAsync($"Set a password to lock “{nb.Name}”");
            if (pw == null) return;
            nb.PasswordHash = HashPw(pw);
            _unlockedNotebooks.Add(nb.Id);
            ScheduleSave();
            ShowStatus($"“{nb.Name}” is locked — it'll ask for the password when you open it.");
        }
        else
        {
            var pw = await PromptPasswordAsync($"Enter the password to remove the lock on “{nb.Name}”");
            if (pw == null) return;
            if (HashPw(pw) == nb.PasswordHash)
            {
                nb.PasswordHash = null;
                ScheduleSave();
                ShowStatus($"“{nb.Name}” is unlocked.");
            }
            else ShowStatus("Incorrect password.");
        }
    }

    private async Task<Color?> PickColorAsync(string title, Color initial)
    {
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Color = initial
        };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = picker,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? picker.Color : null;
    }

    private (Notebook?, Section?) FindContext(NotePage page)
    {
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                if (sec.Pages.Contains(page))
                    return (nb, sec);
        return (null, null);
    }

    private (Notebook? nb, Section? sec, NotePage? pg) FindPageById(Guid? id)
    {
        if (id == null) return (null, null, null);
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                foreach (var pg in sec.Pages)
                    if (pg.Id == id) return (nb, sec, pg);
        return (null, null, null);
    }

    // Loads the page the user worked on last (so the startup picker sits over it),
    // skipping locked notebooks; falls back to the first available page.
    private void OpenStartupPage()
    {
        var (nb, sec, pg) = FindPageById(_library.LastPageId);
        if (pg != null && nb!.PasswordHash == null)
        {
            SwitchToPage(nb, sec!, pg);
            return;
        }
        foreach (var n in _library.Notebooks)
        {
            if (n.PasswordHash != null) continue;
            if (n.Sections.Count == 0) n.Sections.Add(new Section { Name = "Section 1" });
            var s = n.Sections[0];
            if (s.Pages.Count == 0) s.Pages.Add(NewPage("Page 1"));
            BuildTree();
            SwitchToPage(n, s, s.Pages[0]);
            return;
        }
        OpenFirstPage();
    }

    private void OpenFirstPage()
    {
        if (_library.Notebooks.Count == 0)
            _library.Notebooks.Add(new Notebook { Name = "My Notebook" });
        var nb = _library.Notebooks[0];
        if (nb.Sections.Count == 0)
            nb.Sections.Add(new Section { Name = "Section 1" });
        var sec = nb.Sections[0];
        if (sec.Pages.Count == 0)
            sec.Pages.Add(NewPage("Page 1"));
        SwitchToPage(nb, sec, sec.Pages[0]);
    }

    private void SwitchToPage(Notebook nb, Section sec, NotePage page)
    {
        bool pageChanged = !ReferenceEquals(_curPage, page);
        if (_curPage != null) SaveNow();
        _curNb = nb;
        _curSec = sec;
        _curPage = page;
        if (_library.LastPageId != page.Id)
        {
            _library.LastPageId = page.Id;   // remembered for "Continue" at startup
            ScheduleSave();
        }

        Surface.LoadPage(page);
        if (pageChanged && !_suppressPageFade)
        {
            // gentle cross-fade so the new page eases in rather than snapping
            Surface.Opacity = 0.25;
            FadeTo(Surface, 1, 200, collapseAtEnd: false);
        }
        CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {page.Name}";

        // accent-follow (#6-batch2): tint the app to the notebook's colour
        if (_library.AccentFollow == "Notebook")
            try { ApplyAccentLive(ColorUtil.Parse(nb.Color)); } catch { }

        // per-notebook text defaults (#10-roadmap)
        var (efFont, efSize) = EffectiveTextDefaults();
        Surface.PendingFontFamily = efFont;
        Surface.PendingFontSize = efSize;

        _syncingUi = true;
        GridRadios.SelectedIndex = (int)page.Grid;
        SpacingSlider.Value = page.GridSpacing;
        BgPicker.Color = ColorUtil.Parse(page.Background);
        _syncingUi = false;

        ApplyPenRowVisibility();
        UpdateUndoButtons();
        UpdateFormatBarVisibility();
        ExpandCurrentInTree();
        SyncAudioPlaybackStateForCurrentPage();
    }

    private async Task<string?> PromptAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = initial.Length };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "Are you sure?",
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void AddNotebook_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New notebook", $"Notebook {_library.Notebooks.Count + 1}");
        if (name == null) return;
        var nb = new Notebook { Name = name };
        var sec = new Section { Name = "Section 1" };
        var pg = NewPage("Page 1");
        sec.Pages.Add(pg);
        nb.Sections.Add(sec);
        _library.Notebooks.Add(nb);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private async void AddSection_Click(object sender, RoutedEventArgs e)
    {
        var (nb, _, _) = ResolveSelection();
        if (nb == null) { ShowStatus("Select a notebook first."); return; }
        var name = await PromptAsync("New section", $"Section {nb.Sections.Count + 1}");
        if (name == null) return;
        var sec = new Section { Name = name };
        var pg = NewPage("Page 1");
        sec.Pages.Add(pg);
        nb.Sections.Add(sec);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private async void AddPage_Click(object sender, RoutedEventArgs e)
    {
        var (nb, sec, _) = ResolveSelection();
        if (nb == null || sec == null) { ShowStatus("Select a section (or a page in it) first."); return; }
        var name = await PromptAsync("New page", $"Page {sec.Pages.Count + 1}");
        if (name == null) return;
        var pg = NewPage(name);
        sec.Pages.Add(pg);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private (Notebook? nb, Section? sec, NotePage? pg) ResolveSelection()
    {
        var content = _selNode?.Content;
        switch (content)
        {
            case NotePage pg:
            {
                var (nb, sec) = FindContext(pg);
                return (nb, sec, pg);
            }
            case Section sec:
            {
                var nb = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                return (nb, sec, null);
            }
            case Notebook nb:
                return (nb, null, null);
            default:
                return (_curNb, _curSec, _curPage);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }
        string current = content switch
        {
            Notebook nb => nb.Name,
            Section sec => sec.Name,
            NotePage pg => pg.Name,
            _ => ""
        };
        var name = await PromptAsync("Rename", current);
        if (name == null) return;
        switch (content)
        {
            case Notebook nb: nb.Name = name; break;
            case Section sec: sec.Name = name; break;
            case NotePage pg: pg.Name = name; break;
        }
        BuildTree();
        if (_curNb != null && _curSec != null && _curPage != null)
            CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
        Surface.Refresh();
        ScheduleSave();
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }

        switch (content)
        {
            case Notebook nb:
                if (!await ConfirmAsync($"Delete notebook “{nb.Name}” and everything inside it?")) return;
                _library.Notebooks.Remove(nb);
                break;
            case Section sec:
            {
                if (!await ConfirmAsync($"Delete section “{sec.Name}” and all its pages?")) return;
                var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                owner?.Sections.Remove(sec);
                break;
            }
            case NotePage pg:
            {
                if (!await ConfirmAsync($"Delete page “{pg.Name}”?")) return;
                var (_, sec2) = FindContext(pg);
                sec2?.Pages.Remove(pg);
                break;
            }
        }

        _selNode = null;
        BuildTree();
        if (_curPage == null || FindContext(_curPage).Item1 == null)
            OpenFirstPage();
        ScheduleSave();
    }

    // Order notebooks / sections / pages by creation date. Tag = "scope:dir",
    // e.g. "pg:desc", "sec:asc", "nb:desc". Manual order is via Move up/down.
    private void Order_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;
        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        string scope = parts[0];
        bool asc = parts[1] == "asc";
        var (selNb, selSec, _) = ResolveSelection();

        switch (scope)
        {
            case "nb":
                ReorderBy(_library.Notebooks, asc, n => n.CreatedTicks);
                ShowStatus(asc ? "Notebooks: oldest first." : "Notebooks: newest first.");
                break;
            case "sec":
            {
                var nbs = selNb != null ? new List<Notebook> { selNb } : _library.Notebooks;
                foreach (var nb in nbs) ReorderBy(nb.Sections, asc, s => s.CreatedTicks);
                ShowStatus(asc ? "Sections: oldest first." : "Sections: newest first.");
                break;
            }
            default: // pages
            {
                List<Section> targets = selSec != null ? new() { selSec }
                    : selNb != null ? selNb.Sections.ToList()
                    : _library.Notebooks.SelectMany(n => n.Sections).ToList();
                foreach (var sec in targets) ReorderBy(sec.Pages, asc, p => p.CreatedTicks);
                ShowStatus(asc ? "Pages: oldest first." : "Pages: newest first.");
                break;
            }
        }
        BuildTree();
        ScheduleSave();
    }

    private static void ReorderBy<T>(List<T> list, bool asc, Func<T, long> key)
    {
        var sorted = asc ? list.OrderBy(key).ToList() : list.OrderByDescending(key).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // ---- manual reordering (replaces the old drag-reorder that lost notes) ----
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }
        bool moved = false;
        switch (content)
        {
            case Notebook nb:
                moved = MoveInList(_library.Notebooks, nb, delta);
                break;
            case Section sec:
            {
                var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                if (owner != null) moved = MoveInList(owner.Sections, sec, delta);
                break;
            }
            case NotePage pg:
            {
                var (_, sec) = FindContext(pg);
                if (sec != null) moved = MoveInList(sec.Pages, pg, delta);
                break;
            }
        }
        if (!moved) return;
        BuildTree();
        var node = FindNode(content);
        if (node != null)
        {
            _selNode = node;
            try { NotebookTree.SelectedNode = node; } catch { }
        }
        ScheduleSave();
    }

    private static bool MoveInList<T>(IList<T> list, T item, int delta)
    {
        int i = list.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return false;
        list.RemoveAt(i);
        list.Insert(j, item);
        return true;
    }

    // Move a section to another notebook, or a page to another section, with no
    // data loss (this is the safe replacement for cross-notebook drag).
    private async void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content is Section sec)
        {
            var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
            var targets = _library.Notebooks.Where(n => !ReferenceEquals(n, owner)).ToList();
            if (targets.Count == 0) { ShowStatus("There's no other notebook to move this section into."); return; }
            int pick = await PickFromAsync("Move section to notebook", targets.Select(n => n.Name).ToList());
            if (pick < 0) return;
            owner?.Sections.Remove(sec);
            targets[pick].Sections.Add(sec);
            BuildTree();
            RefreshCrumb();
            ScheduleSave();
            ShowStatus($"Moved section “{sec.Name}” to “{targets[pick].Name}”.");
        }
        else if (content is NotePage pg)
        {
            var (_, curSec) = FindContext(pg);
            var dests = _library.Notebooks
                .SelectMany(n => n.Sections.Select(s => (Nb: n, Sec: s)))
                .Where(t => !ReferenceEquals(t.Sec, curSec))
                .ToList();
            if (dests.Count == 0) { ShowStatus("There's no other section to move this page into."); return; }
            int pick = await PickFromAsync("Move page to section",
                dests.Select(t => $"{t.Nb.Name} ▸ {t.Sec.Name}").ToList());
            if (pick < 0) return;
            curSec?.Pages.Remove(pg);
            dests[pick].Sec.Pages.Add(pg);
            BuildTree();
            RefreshCrumb();
            ScheduleSave();
            ShowStatus($"Moved page “{pg.Name}” to “{dests[pick].Sec.Name}”.");
        }
        else
        {
            ShowStatus("Select a section or a page to move it elsewhere.");
        }
    }

    private async Task<int> PickFromAsync(string title, List<string> options)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = combo,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? combo.SelectedIndex : -1;
    }

    private void RefreshCrumb()
    {
        if (_curPage == null) return;
        var (nb, sec) = FindContext(_curPage);
        if (nb != null && sec != null)
        {
            _curNb = nb;
            _curSec = sec;
            CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {_curPage.Name}";
        }
    }

    // =======================================================================
    // Search across all notes, incl. handwriting OCR (#18)
    // =======================================================================
    private sealed class SearchHit
    {
        public Notebook Nb = null!;
        public Section Sec = null!;
        public NotePage Page = null!;
        public string Display = "";
        public TextElement? TextHit;   // the text box that matched, if any
        public bool OcrHit;            // matched recognised handwriting
        public string Query = "";
        public override string ToString() => Display;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = RunSearchAsync(SearchBox.Text); }
    }

    // Set once ink analysis fails, so a broken analyzer can't take the whole
    // search down with it — typed text and names remain searchable.
    private static bool _inkAnalysisBroken;

    private async Task RunSearchAsync(string q)
    {
        q = (q ?? "").Trim();
        SearchResults.ItemsSource = null;
        if (q.Length == 0) { SearchStatus.Text = ""; return; }
        SearchStatus.Text = "Searching…";
        string ql = q.ToLowerInvariant();
        var hits = new List<SearchHit>();
        bool indexedSomething = false;

        try
        {
            foreach (var nb in _library.Notebooks)
                foreach (var sec in nb.Sections)
                    foreach (var pg in sec.Pages)
                    {
                        try
                        {
                            // lazily OCR handwriting that hasn't been indexed yet
                            if (!_inkAnalysisBroken && string.IsNullOrEmpty(pg.OcrText) && pg.Strokes.Count > 0)
                            {
                                await IndexHandwritingAsync(pg);
                                indexedSomething = true;
                            }
                            // remember WHERE the match is so clicking a result can jump there (#46)
                            var textHit = pg.Texts.FirstOrDefault(t => RtfToText(t.Rtf).ToLowerInvariant().Contains(ql));
                            bool ocrHit = (pg.OcrText ?? "").ToLowerInvariant().Contains(ql);
                            string hay = ($"{nb.Name} {sec.Name} {pg.Name} {pg.OcrText}").ToLowerInvariant();
                            if (textHit != null || ocrHit || hay.Contains(ql))
                                hits.Add(new SearchHit
                                {
                                    Nb = nb, Sec = sec, Page = pg,
                                    Display = $"{nb.Name} ▸ {sec.Name} ▸ {pg.Name}",
                                    TextHit = textHit, OcrHit = ocrHit, Query = ql
                                });
                        }
                        catch
                        {
                            // one bad page must never abort the whole search
                        }
                    }
        }
        catch { }

        SearchResults.ItemsSource = hits;
        SearchStatus.Text = hits.Count == 0 ? "No matches." : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}.";
        if (indexedSomething) ScheduleSave(); // persist the new OCR index
    }

    private async void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchHit hit) return;
        if (!await EnsureUnlockedAsync(hit.Nb)) return;
        BuildTree();
        SwitchToPage(hit.Nb, hit.Sec, hit.Page);

        // jump to the exact spot the text was found (#46)
        try
        {
            if (hit.TextHit != null)
            {
                Surface.CenterOnWorld(hit.TextHit.X + hit.TextHit.Width / 2, hit.TextHit.Y + 30);
            }
            else if (hit.OcrHit)
            {
                var rect = await FindInkWordRectAsync(hit.Page, hit.Query);
                if (rect is Rect r)
                    Surface.CenterOnWorld(r.X + r.Width / 2, r.Y + r.Height / 2);
            }
        }
        catch { /* navigation is best-effort */ }
    }

    // Re-analyses the page's ink to locate the matched word's bounding box.
    private static async Task<Rect?> FindInkWordRectAsync(NotePage page, string query)
    {
        try
        {
            if (_inkAnalysisBroken || page.Strokes.Count == 0) return null;
            var analyzer = new Windows.UI.Input.Inking.Analysis.InkAnalyzer();
            var builder = new Windows.UI.Input.Inking.InkStrokeBuilder();
            foreach (var s in page.Strokes)
            {
                if (s.Points.Count < 2) continue;
                var pts = s.Points.Select(p =>
                    new Windows.UI.Input.Inking.InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.Pressure));
                analyzer.AddDataForStroke(builder.CreateStrokeFromInkPoints(pts, System.Numerics.Matrix3x2.Identity));
            }
            await analyzer.AnalyzeAsync();
            var words = analyzer.AnalysisRoot.FindNodes(Windows.UI.Input.Inking.Analysis.InkAnalysisNodeKind.InkWord);
            foreach (var w in words)
            {
                var word = (Windows.UI.Input.Inking.Analysis.InkAnalysisInkWord)w;
                if (word.RecognizedText.ToLowerInvariant().Contains(query))
                    return word.BoundingRect;
            }
        }
        catch { }
        return null;
    }

    // Recognise handwriting on a page with the Windows ink analyzer and cache it.
    private static async Task IndexHandwritingAsync(NotePage page)
    {
        try
        {
            if (page.Strokes.Count == 0) { page.OcrText = " "; return; }
            var analyzer = new Windows.UI.Input.Inking.Analysis.InkAnalyzer();
            var builder = new Windows.UI.Input.Inking.InkStrokeBuilder();
            foreach (var s in page.Strokes)
            {
                if (s.Points.Count < 2) continue;
                var pts = s.Points.Select(p =>
                    new Windows.UI.Input.Inking.InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.Pressure));
                var stroke = builder.CreateStrokeFromInkPoints(pts, System.Numerics.Matrix3x2.Identity);
                analyzer.AddDataForStroke(stroke);
            }
            await analyzer.AnalyzeAsync();
            var words = analyzer.AnalysisRoot.FindNodes(Windows.UI.Input.Inking.Analysis.InkAnalysisNodeKind.InkWord);
            var sb = new System.Text.StringBuilder();
            foreach (var w in words)
                sb.Append(((Windows.UI.Input.Inking.Analysis.InkAnalysisInkWord)w).RecognizedText).Append(' ');
            page.OcrText = sb.Length > 0 ? sb.ToString() : " ";
        }
        catch
        {
            page.OcrText = " "; // mark as attempted so we don't retry endlessly
            _inkAnalysisBroken = true;
        }
    }

    // =======================================================================
    // Handwriting → text / maths (phase 2 #35): recognise the lasso-selected
    // strokes and replace them with an editable text box.
    // =======================================================================
    private static async Task<string?> RecognizeStrokesAsync(IReadOnlyList<PenStroke> strokes)
    {
        try
        {
            var analyzer = new Windows.UI.Input.Inking.Analysis.InkAnalyzer();
            var builder = new Windows.UI.Input.Inking.InkStrokeBuilder();
            int added = 0;
            foreach (var s in strokes)
            {
                if (s.Points.Count < 2) continue;
                var pts = s.Points.Select(p =>
                    new Windows.UI.Input.Inking.InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.Pressure));
                analyzer.AddDataForStroke(builder.CreateStrokeFromInkPoints(pts, System.Numerics.Matrix3x2.Identity));
                added++;
            }
            if (added == 0) return null;
            await analyzer.AnalyzeAsync();
            var lines = analyzer.AnalysisRoot.FindNodes(Windows.UI.Input.Inking.Analysis.InkAnalysisNodeKind.Line);
            var sb = new System.Text.StringBuilder();
            foreach (var l in lines)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(((Windows.UI.Input.Inking.Analysis.InkAnalysisLine)l).RecognizedText);
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task ConvertSelectionAsync(bool math)
    {
        try
        {
            var strokes = Surface.SelectedStrokes.ToList();
            if (strokes.Count == 0) { ShowStatus("Lasso-select some handwriting first."); return; }
            var bounds = Surface.SelectionBoundsWorld;
            ShowStatus("Reading your handwriting…");
            var text = await RecognizeStrokesAsync(strokes);
            if (string.IsNullOrWhiteSpace(text)) { ShowStatus("Couldn't read that handwriting — try selecting a full word or line."); return; }

            if (math)
            {
                var expr = NormalizeMathText(text);
                text = CalcEngine.TryEvaluate(expr, true, out double val, out _)
                    ? $"{expr} = {val:G10}"
                    : expr;   // couldn't evaluate: still insert the recognised expression
            }

            Surface.DeleteSelection();   // undoable
            double x = bounds.IsEmpty ? 100 : bounds.X;
            double y = bounds.IsEmpty ? 100 : bounds.Y;
            double w = bounds.IsEmpty ? 320 : Math.Max(220, bounds.Width + 60);
            Surface.AddTextElement(x, y, w,
                PlainToRtf(text, _library.DefaultFont, (float)_library.DefaultFontSize, ContrastHexForPage()));
            ShowStatus(math ? "Converted to maths." : "Converted to text — tap it with the Text tool to edit.");
        }
        catch (Exception ex)
        {
            ShowStatus("Convert failed: " + ex.Message);
        }
    }

    // Text inserted programmatically should be readable on the page: use the
    // opposite of the background (ivory on dark pages, near-black on light).
    private string ContrastHexForPage() =>
        ColorUtil.IsDark(ColorUtil.Parse(_curPage?.Background ?? "#FFFFFF")) ? "#FAF9F5" : "#141413";

    // Common handwriting-recognition slips for maths input.
    private static string NormalizeMathText(string t) => t
        .Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("–", "-")
        .Replace("^", "^").Replace(",", ".").Replace(" ", "");

    private static string PlainToRtf(string text, string font, float size, string hexColor)
    {
        var body = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (ch is '\\' or '{' or '}') body.Append('\\').Append(ch);
            else if (ch == '\n') body.Append("\\par ");
            else if (ch == '\r') { }
            else if (ch > 127) body.Append("\\u").Append((int)ch).Append('?');
            else body.Append(ch);
        }
        int halfPts = Math.Max(8, (int)Math.Round(size * 2));
        var c = ColorUtil.Parse(hexColor);
        return "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 " + font + ";}}" +
               "{\\colortbl ;\\red" + c.R + "\\green" + c.G + "\\blue" + c.B + ";}" +
               "\\f0\\cf1\\fs" + halfPts + " " + body + "}";
    }

    private static string RtfToText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{' || c == '}') continue;
            if (c == '\\')
            {
                // skip a control word and an optional trailing space
                i++;
                while (i < rtf.Length && (char.IsLetter(rtf[i]))) i++;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                if (i < rtf.Length && rtf[i] != ' ') i--; // step back if not the trailing space
                sb.Append(' ');
                continue;
            }
            if (c is '\r' or '\n') { sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // =======================================================================
    // Notebook gallery (#16)
    // =======================================================================
    private static readonly (string Hex, string Name)[] NotebookColours =
    {
        ("#D97757", "Clay"), ("#6A9BCC", "Sky"), ("#788C5D", "Sage"), ("#D32F2F", "Red"),
        ("#FBC02D", "Amber"), ("#7B1FA2", "Violet"), ("#3A3A38", "Graphite"), ("#2E7D6B", "Teal")
    };

    private void OpenGallery_Click(object sender, RoutedEventArgs e) => ShowGallery(launcher: false);

    // Shows the gallery as an overlay. launcher = the startup picker variant
    // (welcome title + "Continue where I left off").
    private void ShowGallery(bool launcher, Notebook? nb = null)
    {
        // return leg of the connected transition: the page contracts onto the
        // card it was opened from, under the gallery fading in (C3)
        if (!launcher && GalleryPanel.Visibility != Visibility.Visible) MorphPageToCard();
        _galleryLauncher = launcher;
        _galleryNb = nb;
        BuildGallery();
        // mirror of CloseGallery's dissolve: settle inward from 1.035 with the
        // same Sine curve, so open and close read as one motion reversed
        FadeIn(GalleryPanel, 220, pop: false);
        SwellScale(GalleryPanel, 1.035, 1.0, 180);
        // start-screen top bar: settings takes the hamburger's spot, crumb hides
        BtnSidebar.Visibility = Visibility.Collapsed;
        BtnSettings.Visibility = Visibility.Visible;
        CrumbText.Visibility = Visibility.Collapsed;
    }

    private void CloseGallery()
    {
        // liquid dissolve: the glass swells slightly as it fades away (#51)
        SwellScale(GalleryPanel, null, 1.035, 180);
        FadeOut(GalleryPanel, 160);
        _galleryLauncher = false;
        _galleryNb = null;
        BtnSidebar.Visibility = Visibility.Visible;
        BtnSettings.Visibility = Visibility.Collapsed;
        CrumbText.Visibility = Visibility.Visible;
    }

    private void CloseGallery_Click(object sender, RoutedEventArgs e) => CloseGallery();

    // Rename the labels on an x-y / x-y-z axes shape (#28-batch2).
    private async Task EditAxisLabelsAsync(ShapeElement ax)
    {
        bool threeD = ax.Kind == ShapeKind.AxesXYZ;
        var bx = new TextBox { Header = "Horizontal axis", Text = ax.AxisLabelX ?? "x" };
        var by = new TextBox { Header = "Vertical axis", Text = ax.AxisLabelY ?? "y" };
        var bz = new TextBox { Header = "Depth axis", Text = ax.AxisLabelZ ?? "z", Visibility = threeD ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 8, Width = 320 };
        panel.Children.Add(bx);
        panel.Children.Add(by);
        if (threeD) panel.Children.Add(bz);
        panel.Children.Add(new TextBlock { Text = "Leave a field as x/y/z for the default label.", FontSize = 12, Opacity = 0.7 });
        var dlg = new ContentDialog
        {
            Title = "Axis labels",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        ax.AxisLabelX = string.IsNullOrWhiteSpace(bx.Text) || bx.Text == "x" ? null : bx.Text.Trim();
        ax.AxisLabelY = string.IsNullOrWhiteSpace(by.Text) || by.Text == "y" ? null : by.Text.Trim();
        ax.AxisLabelZ = string.IsNullOrWhiteSpace(bz.Text) || bz.Text == "z" ? null : bz.Text.Trim();
        Surface.Refresh();
        ScheduleSave();
    }

    // Visual emoji picker for notebook covers (#cust-roadmap): a tappable grid of
    // common study/subject emoji plus a free-text field for anything else.
    // Returns null on cancel, "" to clear, or the chosen emoji.
    private async Task<string?> PickEmojiAsync(string? current)
    {
        string[] choices =
        {
            "📓", "📔", "📕", "📗", "📘", "📙", "📚", "📝",
            "🧮", "📐", "📏", "🧪", "🧬", "🔬", "🔭", "⚗️",
            "💻", "⌨️", "🌍", "🗺️", "🏛️", "⚖️", "🎨", "🎵",
            "🧠", "❤️", "⭐", "🔥", "🌱", "☕", "🏀", "✈️"
        };
        var box = new TextBox { Text = current ?? "", PlaceholderText = "…or type any emoji", Margin = new Thickness(0, 10, 0, 0) };
        var grid = new GridView { MaxWidth = 380, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        foreach (var em in choices)
            grid.Items.Add(new Border
            {
                Width = 40, Height = 40, Tag = em,
                Child = new TextBlock { Text = em, FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        var panel = new StackPanel { Width = 400 };
        panel.Children.Add(grid);
        panel.Children.Add(box);
        var dlg = new ContentDialog
        {
            Title = "Notebook cover",
            Content = panel,
            PrimaryButtonText = "OK",
            SecondaryButtonText = "Remove emoji",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        grid.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is Border b && b.Tag is string em) box.Text = em;
        };
        var res = await dlg.ShowAsync();
        if (res == ContentDialogResult.Primary) return box.Text;
        if (res == ContentDialogResult.Secondary) return "";
        return null;
    }

    private void GalleryBack_Click(object sender, RoutedEventArgs e)
    {
        _galleryNb = null;
        BuildGallery();
    }

    private void BuildGallery()
    {
        GalleryHost.Children.Clear();
        // liquid transition when moving between the grid and a notebook (#51):
        // the content fades in while gliding up into place
        GalleryHost.Opacity = 0;
        FadeTo(GalleryHost, 1, 180, collapseAtEnd: false);
        SlideNudge(GalleryHost, 0, 22);

        bool detail = _galleryNb != null;
        GalleryBackBtn.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        GalleryNbBtn.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        GalleryFolderBtn.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        GallerySecBtn.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        GallerySaveBtn.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;

        if (detail)
        {
            GalleryTitle.Text = _galleryNb!.Name;
            GallerySubtitle.Text = "Pick a page to work on — or create a new section or page.";
            GalleryContinueBtn.Visibility = Visibility.Collapsed;
            BuildNotebookDetail(_galleryNb!);
            return;
        }

        GalleryTitle.Text = _galleryLauncher ? "Welcome back" : "Notebooks";
        GallerySubtitle.Text = _galleryLauncher
            ? "Choose a notebook to browse its sections and pages — or continue where you left off."
            : "Click a notebook to browse its sections and pages · right-click for colour, folder, rename, lock.";

        var (lnb, lsec, lpg) = FindPageById(_library.LastPageId);
        if (lpg != null)   // launcher AND gallery: always offer the way back
        {
            GalleryContinueBtn.Content = new TextBlock
            {
                Text = $"Continue: {lnb!.Name}, {lsec!.Name}, {lpg.Name}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            ToolTipService.SetToolTip(GalleryContinueBtn, $"{lnb.Name} ▸ {lsec!.Name} ▸ {lpg.Name}");
            GalleryContinueBtn.Visibility = Visibility.Visible;
        }
        else GalleryContinueBtn.Visibility = Visibility.Collapsed;

        // ungrouped notebooks first
        var ungrouped = _library.Notebooks.Where(n => string.IsNullOrEmpty(n.Folder)).ToList();
        if (ungrouped.Count > 0) GalleryHost.Children.Add(BuildCardRow(ungrouped));

        // then each folder (union of declared folders and folders in use)
        var folders = _library.Folders
            .Concat(_library.Notebooks.Select(n => n.Folder ?? "").Where(f => f != ""))
            .Distinct().OrderBy(f => f).ToList();
        foreach (var folder in folders)
        {
            GalleryHost.Children.Add(new TextBlock
            {
                Text = "\U0001F4C1  " + folder,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Poppins"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 15,
                Opacity = 0.85,
                Margin = new Thickness(2, 6, 0, 0)
            });
            var inFolder = _library.Notebooks.Where(n => n.Folder == folder).ToList();
            GalleryHost.Children.Add(inFolder.Count > 0
                ? BuildCardRow(inFolder)
                : new TextBlock { Text = "(empty — move a notebook here)", Opacity = 0.5, FontSize = 12, Margin = new Thickness(4, 0, 0, 0) });
        }
    }

    private GridView BuildCardRow(List<Notebook> notebooks)
    {
        var gv = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
        foreach (var nb in notebooks) gv.Items.Add(MakeNotebookCard(nb));
        return gv;
    }

    // Animated per-instance hover glow shared by every gallery level — notebook
    // cards, section cards and page chips — breathing while hovered (#8-batch2).
    private static void AttachHoverGlow(FrameworkElement el, Color col, Brush? restBrush, Thickness restThickness, Thickness hoverThickness)
    {
        void SetBorder(Brush? b, Thickness th)
        {
            if (el is Border bd) { bd.BorderBrush = b; bd.BorderThickness = th; }
            else if (el is Control c) { c.BorderBrush = b; c.BorderThickness = th; }
        }
        // One brush + one timer for the element's whole lifetime: the glow eases
        // IN on enter and OUT on exit, and a re-entry mid-fade simply reverses
        // direction instead of stacking brushes/timers (#anim).
        LinearGradientBrush? glow = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? ramp = null;
        double target = 0;
        bool joined = false;

        void EnsureRamp()
        {
            if (ramp != null) return;
            ramp = el.DispatcherQueue.CreateTimer();
            ramp.Interval = TimeSpan.FromMilliseconds(33);
            ramp.Tick += (_, _) =>
            {
                if (glow == null) { ramp!.Stop(); return; }
                // out is a touch slower than in — the classic ease asymmetry
                double step = target > glow.Opacity ? 0.11 : -0.07;
                double next = glow.Opacity + step;
                if ((step > 0 && next >= target) || (step < 0 && next <= target)) next = target;
                glow.Opacity = next;
                if (next >= 1)
                {
                    ramp!.Stop();
                    if (!joined) { RegisterGlowBrush(glow); joined = true; }   // breathe only once fully in
                }
                else if (next <= 0)
                {
                    ramp!.Stop();
                    SetBorder(restBrush, restThickness);
                    glow = null;
                }
            };
        }

        el.PointerEntered += (_, _) =>
        {
            if (glow == null)
            {
                glow = MakeColorGlowBrush(col);
                glow.Opacity = 0;
                joined = false;
                SetBorder(glow, restThickness);   // paint-only hover: geometry never changes
            }
            target = 1;
            EnsureRamp();
            ramp!.Start();
        };
        el.PointerExited += (_, _) =>
        {
            if (glow == null) { SetBorder(restBrush, restThickness); return; }
            if (joined) { UnregisterGlowBrush(glow); joined = false; }   // let the fade own the opacity
            target = 0;
            EnsureRamp();
            ramp!.Start();
        };
    }

    // A glow gradient in an arbitrary colour (used per-notebook in the gallery).
    private static LinearGradientBrush MakeColorGlowBrush(Color c)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (alpha, offset) in new (byte, double)[]
                 { (0xE6, 0), (0x59, 0.35), (0x26, 0.5), (0x59, 0.65), (0xE6, 1) })
            b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(alpha, c.R, c.G, c.B), Offset = offset });
        return b;
    }

    // Prefer the shared acrylic so cards read as liquid glass (#50).
    private static Brush GlassBrush(Brush fallback)
    {
        try
        {
            if (Application.Current.Resources["CardBrush"] is Brush b) return b;
        }
        catch { }
        return fallback;
    }

    private Border MakeNotebookCard(Notebook nb)
    {
        bool dark = ResolvedDark();
        var cardBg = GlassBrush(new SolidColorBrush(dark ? Color.FromArgb(255, 0x22, 0x21, 0x1F) : Color.FromArgb(255, 0xFF, 0xFF, 0xFF)));
        var inkBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18));
        var col = ColorUtil.Parse(nb.Color);
        var card = new Border
        {
            Width = 188,
            Height = 134,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(12),
            Background = cardBg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var strip = new Border { Background = new SolidColorBrush(col), CornerRadius = new CornerRadius(12, 12, 0, 0) };
        var stripGrid = new Grid();
        strip.Child = stripGrid;

        // Cover thumbnail of the notebook's first page. It fills in behind the
        // emoji/lock badges once the worker has it; until then — and for a
        // locked, empty or unrenderable notebook — the colour block stands alone.
        var coverPage = nb.PasswordHash == null
            ? nb.Sections.SelectMany(s => s.Pages).FirstOrDefault()
            : null;
        if (coverPage != null)
        {
            var cover = new Image { Stretch = Stretch.UniformToFill, Opacity = 0 };
            stripGrid.Children.Add(cover);
            LoadThumbAsync(cover, coverPage, 376, 120, true, 0.92);
        }

        if (nb.CoverEmoji != null)
        {
            stripGrid.Children.Add(new TextBlock
            {
                Text = nb.CoverEmoji,
                FontSize = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        if (nb.PasswordHash != null)
        {
            stripGrid.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8, 8, 0)
            });
        }
        Grid.SetRow(strip, 0);

        var name = new TextBlock { Text = nb.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(12, 8, 12, 0), Foreground = inkBrush };
        Grid.SetRow(name, 1);

        int pages = nb.Sections.Sum(s => s.Pages.Count);
        var counts = new TextBlock { Text = $"{nb.Sections.Count} sections · {pages} pages", FontSize = 11, Opacity = 0.65, Margin = new Thickness(12, 2, 12, 10) };
        Grid.SetRow(counts, 2);

        grid.Children.Add(strip);
        grid.Children.Add(name);
        grid.Children.Add(counts);
        card.Child = grid;

        card.Tapped += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            _galleryNb = nb;             // drill into sections & pages
            BuildGallery();
        };

        // hover polish: the card glows in ITS OWN colour and keeps breathing (#51, #8-batch2)
        AttachHoverGlow(card, col, card.BorderBrush, new Thickness(1), new Thickness(1.6));

        card.ContextFlyout = BuildCardFlyout(nb, card);
        ToolTipService.SetToolTip(card, "Click to browse sections & pages · right-click for more");
        return card;
    }

    private MenuFlyout BuildCardFlyout(Notebook nb, FrameworkElement card)
    {
        var fly = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open first page" };
        openItem.Click += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            var from = BoundsInCanvas(card);
            OpenNotebook(nb);
            CloseGallery();
            MorphCardToPage(from, ColorUtil.Parse(nb.Color));
        };
        fly.Items.Add(openItem);
        fly.Items.Add(new MenuFlyoutSeparator());

        var colourSub = new MenuFlyoutSubItem { Text = "Colour" };
        foreach (var (hex, cname) in NotebookColours)
        {
            var h = hex;
            var it = new MenuFlyoutItem { Text = cname };
            it.Click += (_, _) => { nb.Color = h; ScheduleSave(); BuildGallery(); };
            colourSub.Items.Add(it);
        }
        colourSub.Items.Add(new MenuFlyoutSeparator());
        var customColour = new MenuFlyoutItem { Text = "Custom…" };
        customColour.Click += async (_, _) =>
        {
            var picked = await PickColorAsync($"Colour for “{nb.Name}”", ColorUtil.Parse(nb.Color));
            if (picked is not Color c) return;
            nb.Color = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            ScheduleSave();
            BuildGallery();
        };
        colourSub.Items.Add(customColour);
        fly.Items.Add(colourSub);

        var folderSub = new MenuFlyoutSubItem { Text = "Move to folder" };
        var none = new MenuFlyoutItem { Text = "(none)" };
        none.Click += (_, _) => { nb.Folder = null; ScheduleSave(); BuildGallery(); };
        folderSub.Items.Add(none);
        foreach (var f in _library.Folders.OrderBy(x => x))
        {
            var ff = f;
            var it = new MenuFlyoutItem { Text = f };
            it.Click += (_, _) => { nb.Folder = ff; ScheduleSave(); BuildGallery(); };
            folderSub.Items.Add(it);
        }
        var newF = new MenuFlyoutItem { Text = "New folder…" };
        newF.Click += async (_, _) =>
        {
            var f = await PromptAsync("New folder", "Folder");
            if (f == null) return;
            if (!_library.Folders.Contains(f)) _library.Folders.Add(f);
            nb.Folder = f;
            ScheduleSave();
            BuildGallery();
        };
        folderSub.Items.Add(newF);
        fly.Items.Add(folderSub);

        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) =>
        {
            var n = await PromptAsync("Rename notebook", nb.Name);
            if (n == null) return;
            nb.Name = n; ScheduleSave(); BuildGallery(); BuildTree();
        };
        fly.Items.Add(rename);

        var emojiItem = new MenuFlyoutItem { Text = "Cover emoji…" };
        emojiItem.Click += async (_, _) =>
        {
            var e = await PickEmojiAsync(nb.CoverEmoji);
            if (e != null)
            {
                nb.CoverEmoji = string.IsNullOrWhiteSpace(e) ? null : e.Trim();
                ScheduleSave();
                BuildGallery();
            }
        };
        fly.Items.Add(emojiItem);

        var lockItem = new MenuFlyoutItem { Text = nb.PasswordHash == null ? "Lock…" : "Remove lock…" };
        lockItem.Click += (_, _) => { _selNode = FindNode(nb); LockToggle_Click(this, new RoutedEventArgs()); };
        fly.Items.Add(lockItem);

        fly.Items.Add(new MenuFlyoutSeparator());
        var delItem = new MenuFlyoutItem { Text = "Delete notebook…" };
        delItem.Click += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            if (!await ConfirmAsync($"Delete notebook “{nb.Name}” and everything inside it?")) return;
            _library.Notebooks.Remove(nb);
            _selNode = null;
            BuildTree();
            if (_curPage == null || FindContext(_curPage).Item1 == null) OpenFirstPage();
            ScheduleSave();
            BuildGallery();
        };
        fly.Items.Add(delItem);

        return fly;
    }

    // ---- notebook detail: sections with page chips (start-screen drill-down) ----
    private void BuildNotebookDetail(Notebook nb)
    {
        bool dark = ResolvedDark();
        var inkBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18));
        var cardBg = GlassBrush(new SolidColorBrush(dark ? Color.FromArgb(255, 0x1C, 0x1B, 0x20) : Color.FromArgb(255, 0xFF, 0xFF, 0xFF)));
        var chipBg = GlassBrush(new SolidColorBrush(dark ? Color.FromArgb(255, 0x27, 0x26, 0x2C) : Color.FromArgb(255, 0xF3, 0xF1, 0xEA)));
        var hairline = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        var accent = new SolidColorBrush(ColorUtil.Parse(nb.Color));

        if (nb.Sections.Count == 0)
            GalleryHost.Children.Add(new TextBlock
            {
                Text = "This notebook is empty — click “New section” above to get started.",
                Opacity = 0.6, FontSize = 13, Margin = new Thickness(4, 8, 0, 0)
            });

        foreach (var sec in nb.Sections)
        {
            var s0 = sec;
            var secCard = new Border
            {
                Background = cardBg,
                BorderBrush = hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 12, 16, 12)
            };
            // section cards share the same breathing hover glow as the other levels (#8-batch2)
            AttachHoverGlow(secCard, ColorUtil.Parse(nb.Color), hairline, new Thickness(1), new Thickness(1.6));
            var stack = new StackPanel { Spacing = 8 };

            var header = new Grid();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = accent, VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = sec.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16,
                Foreground = inkBrush, VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = $"{sec.Pages.Count} page{(sec.Pages.Count == 1 ? "" : "s")}",
                FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(titleRow);

            var addPg = new Button
            {
                Content = "+ New page", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(10, 4, 10, 4)
            };
            addPg.Click += async (_, _) => await GalleryNewPageAsync(nb, s0, addPg);
            header.Children.Add(addPg);
            stack.Children.Add(header);

            var secFly = new MenuFlyout();
            var renSec = new MenuFlyoutItem { Text = "Rename section" };
            renSec.Click += async (_, _) =>
            {
                var n2 = await PromptAsync("Rename section", s0.Name);
                if (n2 == null) return;
                s0.Name = n2;
                ScheduleSave(); BuildTree(); BuildGallery();
                if (_curSec == s0 && _curNb != null && _curPage != null)
                    CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
            };
            secFly.Items.Add(renSec);
            var delSec = new MenuFlyoutItem { Text = "Delete section…" };
            delSec.Click += async (_, _) =>
            {
                if (!await ConfirmAsync($"Delete section “{s0.Name}” and all its pages?")) return;
                nb.Sections.Remove(s0);
                ScheduleSave(); BuildTree();
                if (_curPage != null && FindContext(_curPage).Item1 == null) OpenFirstPage();
                BuildGallery();
            };
            secFly.Items.Add(delSec);
            secCard.ContextFlyout = secFly;
            ToolTipService.SetToolTip(secCard, "Right-click to rename or delete this section");

            if (sec.Pages.Count == 0)
                stack.Children.Add(new TextBlock { Text = "No pages yet — add one above.", FontSize = 12, Opacity = 0.55 });
            else
            {
                var wrap = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
                foreach (var pg in sec.Pages) wrap.Items.Add(MakePageChip(nb, sec, pg, chipBg, inkBrush));
                stack.Children.Add(wrap);
            }

            secCard.Child = stack;
            GalleryHost.Children.Add(secCard);
        }
    }

    // Fills a gallery Image from the disk thumbnail cache. Fire-and-forget by
    // design: the card is already on screen and simply gains its picture, and
    // every failure path leaves the caller's own fallback showing.
    private async void LoadThumbAsync(Image target, NotePage page, int w, int h, bool crop, double opacity)
    {
        byte[]? bytes;
        try { bytes = await ThumbnailCache.GetAsync(page, w, h, crop); }
        catch { return; }
        if (bytes == null || bytes.Length == 0) return;

        try
        {
            var bmi = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            using (var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream())
            {
                using (var writer = new Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                ms.Seek(0);
                await bmi.SetSourceAsync(ms);
            }
            target.Source = bmi;
            target.Opacity = opacity;
        }
        catch { }
    }

    private FrameworkElement MakePageChip(Notebook nb, Section sec, NotePage pg, Brush bg, Brush ink)
    {
        bool current = ReferenceEquals(pg, _curPage);
        var inner = new StackPanel { Spacing = 2 };

        var img = new Image { Width = 160, Height = 100, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 0, 6) };
        inner.Children.Add(img);

        LoadThumbAsync(img, pg, 320, 200, false, 1.0);

        inner.Children.Add(new TextBlock
        {
            Text = pg.Name, Foreground = ink, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160
        });
        inner.Children.Add(new TextBlock
        {
            Text = new DateTime(pg.CreatedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy"),
            FontSize = 10, Opacity = 0.55
        });

        var chip = new Button
        {
            Content = inner,
            Background = bg,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(current ? 1.5 : 1),
            BorderBrush = current
                ? (Brush)Application.Current.Resources["BrandOrangeBrush"]
                : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        };
        ToolTipService.SetToolTip(chip, current
            ? "This page is open right now"
            : "Open this page · right-click to rename or delete");

        chip.Click += (_, _) =>
        {
            // measured while the gallery is still up, then grown into the page (C3)
            var from = BoundsInCanvas(chip);
            BuildTree();
            SwitchToPage(nb, sec, pg);
            CloseGallery();
            MorphCardToPage(from, ColorUtil.Parse(nb.Color));
        };

        var fly = new MenuFlyout();
        var ren = new MenuFlyoutItem { Text = "Rename page" };
        ren.Click += async (_, _) =>
        {
            var n2 = await PromptAsync("Rename page", pg.Name);
            if (n2 == null) return;
            pg.Name = n2;
            ScheduleSave(); BuildTree(); BuildGallery();
            if (ReferenceEquals(pg, _curPage))
            {
                CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {pg.Name}";
                Surface.Refresh();
            }
        };
        fly.Items.Add(ren);
        var del = new MenuFlyoutItem { Text = "Delete page…" };
        del.Click += async (_, _) =>
        {
            if (!await ConfirmAsync($"Delete page “{pg.Name}”?")) return;
            sec.Pages.Remove(pg);
            ScheduleSave(); BuildTree();
            if (ReferenceEquals(pg, _curPage)) OpenFirstPage();
            BuildGallery();
        };
        fly.Items.Add(del);
        chip.ContextFlyout = fly;

        // glow lives on a wrapper Border: Button visual states overwrite the
        // chip's own BorderBrush on hover, which is why unselected pages never
        // glowed (#25-batch3)
        var glowWrap = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.6),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Child = chip
        };
        AttachHoverGlow(glowWrap, ColorUtil.Parse(nb.Color), glowWrap.BorderBrush, new Thickness(1.6), new Thickness(2.6));
        return glowWrap;
    }

    private async Task GalleryNewPageAsync(Notebook nb, Section sec, FrameworkElement? origin = null)
    {
        var name = await PromptAsync("New page", $"Page {sec.Pages.Count + 1}");
        if (name == null) return;
        var pg = NewPage(name);
        sec.Pages.Add(pg);
        BuildTree();
        ScheduleSave();
        var from = BoundsInCanvas(origin);
        SwitchToPage(nb, sec, pg);   // creating a page means "work on it now"
        CloseGallery();
        MorphCardToPage(from, ColorUtil.Parse(nb.Color));
    }

    private async void GalleryNewSection_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryNb == null) return;
        var name = await PromptAsync("New section", $"Section {_galleryNb.Sections.Count + 1}");
        if (name == null) return;
        _galleryNb.Sections.Add(new Section { Name = name });
        BuildTree();
        ScheduleSave();
        BuildGallery();   // stay in the picker so a page can be added next
    }

    private async void GalleryContinue_Click(object sender, RoutedEventArgs e)
    {
        var (nb, sec, pg) = FindPageById(_library.LastPageId);
        if (pg == null) { CloseGallery(); return; }
        if (!await EnsureUnlockedAsync(nb!)) return;
        var from = BoundsInCanvas(GalleryContinueBtn);
        BuildTree();
        SwitchToPage(nb!, sec!, pg);
        CloseGallery();
        MorphCardToPage(from, ColorUtil.Parse(nb!.Color));
    }

    private void OpenNotebook(Notebook nb)
    {
        if (nb.Sections.Count == 0) nb.Sections.Add(new Section { Name = "Section 1" });
        var sec = nb.Sections[0];
        if (sec.Pages.Count == 0) sec.Pages.Add(NewPage("Page 1"));
        BuildTree();
        SwitchToPage(nb, sec, sec.Pages[0]);
        ScheduleSave();
    }

    private async void GalleryNewNotebook_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New notebook", $"Notebook {_library.Notebooks.Count + 1}");
        if (name == null) return;
        var nb = new Notebook { Name = name };
        var sec = new Section { Name = "Section 1" };
        sec.Pages.Add(NewPage("Page 1"));
        nb.Sections.Add(sec);
        _library.Notebooks.Add(nb);
        BuildTree();
        _galleryNb = nb;      // jump straight into the new notebook's pages
        BuildGallery();
        ScheduleSave();
    }

    private async void GalleryNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = await PromptAsync("New folder", "Folder");
        if (f == null) return;
        if (!_library.Folders.Contains(f)) _library.Folders.Add(f);
        ScheduleSave();
        BuildGallery();
    }

    // =======================================================================
    // Settings page — central storage folder + recover/import notebooks
    // =======================================================================
    // The panel is code-built, so it captures its strings at build time exactly
    // the way the gallery captures theme colours. Switching language therefore
    // closes and re-presents the dialog rather than trying to patch it in place.
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        while (await ShowSettingsDialogAsync()) { }
    }

    // Returns true when the language changed and the dialog should be reopened.
    private async Task<bool> ShowSettingsDialogAsync()
    {
        bool languageChanged = false;
        var panel = new StackPanel { Spacing = 10, Width = 480 };

        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Storage.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Storage.Desc"), FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        var folderText = new TextBlock { Text = LibraryStore.Dir, FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        panel.Children.Add(folderText);
        var changeBtn = new Button { Content = Loc.T("Settings.Storage.Change"), HorizontalAlignment = HorizontalAlignment.Left };
        changeBtn.Click += async (_, _) =>
        {
            var f = await PickFolderAsync();
            if (f == null) return;
            Surface.FlushTexts();
            LibraryStore.SetDataFolder(f, _library);
            folderText.Text = LibraryStore.Dir;
            ShowStatus(Loc.T("Status.StorageUpdated"));
        };
        panel.Children.Add(changeBtn);

        // ---- language (#C7) ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Language.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var langBox = new ComboBox { Width = 220 };
        langBox.Items.Add(new ComboBoxItem { Content = Loc.T("Settings.Language.System"), Tag = "" });
        foreach (var (tag, name) in Loc.Languages)
            langBox.Items.Add(new ComboBoxItem { Content = name, Tag = tag });
        foreach (ComboBoxItem it in langBox.Items)
            if ((string)it.Tag == _library.Language) { langBox.SelectedItem = it; break; }
        if (langBox.SelectedItem == null) langBox.SelectedIndex = 0;
        langBox.SelectionChanged += (_, _) =>
        {
            if (langBox.SelectedItem is not ComboBoxItem ci || ci.Tag is not string tag) return;
            if (tag == _library.Language) return;
            _library.Language = tag;
            ApplyLanguage();
            ScheduleSave();
            languageChanged = true;
            _settingsDialog?.Hide();   // the while-loop in Settings_Click reopens it
        };
        panel.Children.Add(langBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Language.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- default text font & size ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Font.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var defFont = new ComboBox { Width = 200 };
        foreach (var f in Fonts)
            defFont.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = f, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(f) }, Tag = f });
        foreach (ComboBoxItem it in defFont.Items)
            if ((string)it.Tag == _library.DefaultFont) { defFont.SelectedItem = it; break; }
        defFont.SelectionChanged += (_, _) =>
        {
            if (defFont.SelectedItem is ComboBoxItem ci && ci.Tag is string fn)
            { _library.DefaultFont = fn; Surface.PendingFontFamily = fn; ScheduleSave(); }
        };
        var defSize = new ComboBox { Width = 92, IsEditable = true };
        foreach (var sz in FontSizes) defSize.Items.Add(sz);
        string curSize = ((int)_library.DefaultFontSize).ToString();
        foreach (var it in defSize.Items)
            if (it?.ToString() == curSize) { defSize.SelectedItem = it; break; }
        // an editable ComboBox discards Text set before it is templated, which
        // left the box blank even though the size was stored — restore it on
        // load, which also covers custom sizes that aren't in the list
        defSize.Loaded += (_, _) => { if (defSize.SelectedItem == null) defSize.Text = curSize; };
        void ApplyDefaultSize(string? txt)
        {
            if (ParseSize(txt) is float v) { _library.DefaultFontSize = v; Surface.PendingFontSize = v; ScheduleSave(); }
        }
        defSize.SelectionChanged += (_, _) => { if (defSize.SelectedItem is string s2) ApplyDefaultSize(s2); };
        defSize.TextSubmitted += (cb, args) => { ApplyDefaultSize(args.Text); };
        row.Children.Add(new TextBlock { Text = Loc.T("Settings.Font.Font"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        row.Children.Add(defFont);
        row.Children.Add(new TextBlock { Text = Loc.T("Settings.Font.Size"), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        row.Children.Add(defSize);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Font.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var nbFontRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var nbFontBtn = new Button { Content = Loc.T("Settings.Font.UseForNotebook"), FontSize = 12 };
        nbFontBtn.Click += (_, _) =>
        {
            if (_curNb == null) { ShowStatus(Loc.T("Status.NoNotebookOpen")); return; }
            _curNb.DefaultFont = _library.DefaultFont;
            _curNb.DefaultFontSize = _library.DefaultFontSize;
            ScheduleSave();
            ShowStatus(Loc.T("Status.NotebookFontSet", _curNb.Name, _curNb.DefaultFont, (int)_curNb.DefaultFontSize.GetValueOrDefault()));
        };
        var nbFontClear = new Button { Content = Loc.T("Settings.Font.ClearOverride"), FontSize = 12 };
        nbFontClear.Click += (_, _) =>
        {
            if (_curNb == null) { ShowStatus(Loc.T("Status.NoNotebookOpen")); return; }
            _curNb.DefaultFont = null;
            _curNb.DefaultFontSize = null;
            ScheduleSave();
            ShowStatus(Loc.T("Status.NotebookFontCleared", _curNb.Name));
        };
        nbFontRow.Children.Add(nbFontBtn);
        nbFontRow.Children.Add(nbFontClear);
        panel.Children.Add(nbFontRow);

        // ---- startup behaviour ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Startup.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var fsToggle = new ToggleSwitch { Header = Loc.T("Settings.Startup.Maximised"), IsOn = _library.StartFullscreen };
        fsToggle.Toggled += (_, _) => { _library.StartFullscreen = fsToggle.IsOn; ScheduleSave(); };
        panel.Children.Add(fsToggle);
        var pickerToggle = new ToggleSwitch { Header = Loc.T("Settings.Startup.ShowPicker"), IsOn = _library.StartOnGallery };
        pickerToggle.Toggled += (_, _) => { _library.StartOnGallery = pickerToggle.IsOn; ScheduleSave(); };
        panel.Children.Add(pickerToggle);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Startup.PickerDesc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- touch-screen mode (#36) ----
        var touchToggle = new ToggleSwitch { Header = Loc.T("Settings.TouchMode"), IsOn = _library.TouchMode };
        touchToggle.Toggled += (_, _) =>
        {
            _library.TouchMode = touchToggle.IsOn;
            ApplyTouchMode(touchToggle.IsOn);
            ScheduleSave();
        };
        panel.Children.Add(touchToggle);

        // ---- liquid glass (#48) ----
        var liquidSlider = new Slider
        {
            Minimum = 0, Maximum = 100, StepFrequency = 5,
            Value = _library.Liquidness * 100,
            Header = Loc.T("Settings.Liquid")
        };
        liquidSlider.ValueChanged += (_, args) =>
        {
            _library.Liquidness = args.NewValue / 100.0;
            ApplyLiquidness(_library.Liquidness);
            ScheduleSave();
        };
        panel.Children.Add(liquidSlider);

        // ---- glow animation (#4-batch2) ----
        // the mode names are persisted ids, so the label is localized and the id
        // rides along in Tag — same shape as the theme and key-preset boxes below
        var glowBox = new ComboBox { Header = Loc.T("Settings.Glow.Header"), Width = 220 };
        foreach (var mode in new[] { "Off", "Breathe", "Circulate", "Comet",
                                     "Aurora", "Shimmer", "Ember", "Chase", "Heartbeat" })
            glowBox.Items.Add(new ComboBoxItem { Content = Loc.T("Glow." + mode), Tag = mode });
        foreach (ComboBoxItem it in glowBox.Items)
            if ((string)it.Tag == _library.GlowMode) { glowBox.SelectedItem = it; break; }
        if (glowBox.SelectedItem == null) glowBox.SelectedIndex = 1;   // Breathe
        glowBox.SelectionChanged += (_, _) =>
        {
            if (glowBox.SelectedItem is ComboBoxItem ci && ci.Tag is string gm)
            { _library.GlowMode = gm; ApplyGlowMode(); ScheduleSave(); }
        };
        panel.Children.Add(glowBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Glow.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- keyboard layout (C5) ----
        var keyBox = new ComboBox { Header = Loc.T("Settings.Keys.Header"), Width = 220 };
        foreach (var (tag, key) in KeyPresetOptions)
            keyBox.Items.Add(new ComboBoxItem { Content = Loc.T(key), Tag = tag });
        foreach (ComboBoxItem it in keyBox.Items)
            if ((string)it.Tag == KeyPresetId(_library.KeyPreset)) { keyBox.SelectedItem = it; break; }
        keyBox.SelectionChanged += (_, _) =>
        {
            if (keyBox.SelectedItem is not ComboBoxItem ci || ci.Tag is not string kp) return;
            _library.KeyPreset = kp;
            ApplyKeyPreset();   // rebinds live; no restart
            ScheduleSave();
            ShowStatus(Loc.T("Status.KeyPresetActive", KeyPresetLabel(kp)));
        };
        panel.Children.Add(keyBox);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Keys.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- theme mode (#10-roadmap) ----
        var themeBox = new ComboBox { Header = Loc.T("Settings.Theme.Header"), Width = 220 };
        foreach (var tag in new[] { "Light", "Dark", "System" })
            themeBox.Items.Add(new ComboBoxItem { Content = Loc.T("Theme." + tag), Tag = tag });
        foreach (ComboBoxItem it in themeBox.Items)
            if ((string)it.Tag == _library.Theme) { themeBox.SelectedItem = it; break; }
        if (themeBox.SelectedItem == null) themeBox.SelectedIndex = 1;
        themeBox.SelectionChanged += (_, _) =>
        {
            if (themeBox.SelectedItem is ComboBoxItem ci && ci.Tag is string t)
            { _library.Theme = t; ApplyTheme(); ScheduleSave(); }
        };
        panel.Children.Add(themeBox);

        // ---- true black for OLED (#32-batch2) ----
        var oledToggle = new ToggleSwitch { Header = Loc.T("Settings.Oled"), IsOn = _library.OledBlack };
        oledToggle.Toggled += (_, _) =>
        {
            _library.OledBlack = oledToggle.IsOn;
            ApplyTheme();
            ScheduleSave();
        };
        panel.Children.Add(oledToggle);

        // ---- autosave interval ----
        var autosaveSlider = new Slider
        {
            Minimum = 0.5, Maximum = 10, StepFrequency = 0.5,
            Value = Math.Clamp(_library.AutosaveSeconds, 0.5, 10),
            Header = Loc.T("Settings.Autosave")
        };
        autosaveSlider.ValueChanged += (_, args) =>
        {
            _library.AutosaveSeconds = args.NewValue;
            _saveTimer.Interval = TimeSpan.FromSeconds(args.NewValue);
            ScheduleSave();
        };
        panel.Children.Add(autosaveSlider);

        // ---- pen repair (#2-batch2) ----
        var penFixDots = new ToggleSwitch { Header = Loc.T("Settings.PenRepair.Dots"), IsOn = _library.PenRepairDots };
        penFixDots.Toggled += (_, _) =>
        {
            _library.PenRepairDots = penFixDots.IsOn;
            Surface.PenRepairDots = penFixDots.IsOn;
            ScheduleSave();
        };
        panel.Children.Add(penFixDots);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.PenRepair.DotsDesc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        var penFixBridge = new ToggleSwitch { Header = Loc.T("Settings.PenRepair.Bridge"), IsOn = _library.PenRepairBridge };
        penFixBridge.Toggled += (_, _) =>
        {
            _library.PenRepairBridge = penFixBridge.IsOn;
            Surface.PenRepairBridge = penFixBridge.IsOn;
            ScheduleSave();
        };
        panel.Children.Add(penFixBridge);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.PenRepair.BridgeDesc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- motion blur (#A5) ----
        var motionBlur = new ToggleSwitch { Header = Loc.T("Settings.MotionBlur.Header"), IsOn = _library.MotionBlur };
        motionBlur.Toggled += (_, _) =>
        {
            _library.MotionBlur = motionBlur.IsOn;
            Surface.MotionBlur = motionBlur.IsOn;
            ScheduleSave();
        };
        panel.Children.Add(motionBlur);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.MotionBlur.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- comment pins outside comment mode (#A3) ----
        var pinsToggle = new ToggleSwitch { Header = Loc.T("Settings.CommentPins.Header"), IsOn = _library.ShowCommentPins };
        pinsToggle.Toggled += (_, _) =>
        {
            _library.ShowCommentPins = pinsToggle.IsOn;
            Surface.ShowCommentsAlways = pinsToggle.IsOn;
            Surface.Refresh();
            ScheduleSave();
        };
        panel.Children.Add(pinsToggle);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.CommentPins.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- toolbar buttons (#topbar) ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Toolbar.Header"), FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["HeadingFont"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Toolbar.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        foreach (var key in OptionalTools)
        {
            var k = key;
            var cb = new CheckBox { Content = Loc.T("Tool." + k), IsChecked = !_library.HiddenTools.Contains(k), MinHeight = 0, Margin = new Thickness(0, 2, 0, 2) };
            void Toggle()
            {
                if (cb.IsChecked == true) _library.HiddenTools.Remove(k);
                else if (!_library.HiddenTools.Contains(k)) _library.HiddenTools.Add(k);
                ApplyToolbarVisibility();
                ScheduleSave();
            }
            cb.Checked += (_, _) => Toggle();
            cb.Unchecked += (_, _) => Toggle();
            panel.Children.Add(cb);
        }

        // ---- pen dock position (#cust-roadmap): the drag gesture already works;
        //      this makes the four dock sides discoverable without dragging ----
        var dockBox = new ComboBox { Header = Loc.T("Settings.PenDock.Header"), Width = 220 };
        foreach (var d in new[] { "Bottom", "Top", "Left", "Right" })
            dockBox.Items.Add(new ComboBoxItem { Content = Loc.T("PenDock." + d), Tag = d });
        string curDock = _library.PenDock is "Top" or "Left" or "Right" ? _library.PenDock : "Bottom";
        foreach (ComboBoxItem it in dockBox.Items)
            if ((string)it.Tag == curDock) { dockBox.SelectedItem = it; break; }
        dockBox.SelectionChanged += (_, _) =>
        {
            if (dockBox.SelectedItem is ComboBoxItem ci && ci.Tag is string d)
            { _library.PenDock = d; ApplyPenDock(); ScheduleSave(); }
        };
        panel.Children.Add(dockBox);

        // ---- AI assistant (#25-batch2) ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Ai.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Ai.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        // provider ids are also the vendors' names, so only "None" is translated
        var aiProviderBox = new ComboBox { Header = Loc.T("Settings.Ai.Provider"), Width = 220 };
        foreach (var prov in new[] { "None", "Claude", "OpenAI", "Gemini", "Local" })
            aiProviderBox.Items.Add(new ComboBoxItem { Content = prov == "None" ? Loc.T("Settings.Ai.ProviderNone") : prov, Tag = prov });
        string curProv = _library.AiProvider is "Claude" or "OpenAI" or "Gemini" or "Local" ? _library.AiProvider : "None";
        foreach (ComboBoxItem it in aiProviderBox.Items)
            if ((string)it.Tag == curProv) { aiProviderBox.SelectedItem = it; break; }
        var aiModelBox = new TextBox { Header = Loc.T("Settings.Ai.Model"), Text = _library.AiModel, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var aiEndpointBox = new TextBox
        {
            Header = Loc.T("Settings.Ai.Endpoint"),
            PlaceholderText = "http://localhost:11434/v1",
            Text = _library.AiEndpoint,
            Visibility = _library.AiProvider == "Local" ? Visibility.Visible : Visibility.Collapsed
        };
        var aiKeyBox = new PasswordBox { Header = Loc.T("Settings.Ai.Key"), Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var aiKeyState = new TextBlock { FontSize = 12, Opacity = 0.7 };
        string SelectedProvider() =>
            (aiProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "None";
        void RefreshKeyState()
        {
            var prov = SelectedProvider();
            aiKeyState.Text = prov is "None" ? "" :
                prov == "Local" ? Loc.T("Settings.Ai.KeyLocal") :
                AiService.GetKey(prov) != null ? Loc.T("Settings.Ai.KeySaved", prov) : Loc.T("Settings.Ai.KeyMissing", prov);
        }
        RefreshKeyState();
        aiProviderBox.SelectionChanged += (_, _) =>
        {
            var prov = SelectedProvider();
            _library.AiProvider = prov;
            aiEndpointBox.Visibility = prov == "Local" ? Visibility.Visible : Visibility.Collapsed;
            aiModelBox.PlaceholderText = AiService.DefaultModel(prov);
            RefreshKeyState();
            ScheduleSave();
        };
        aiModelBox.TextChanged += (_, _) => { _library.AiModel = aiModelBox.Text.Trim(); ScheduleSave(); };
        aiEndpointBox.TextChanged += (_, _) => { _library.AiEndpoint = aiEndpointBox.Text.Trim(); ScheduleSave(); };
        var aiKeySave = new Button { Content = Loc.T("Settings.Ai.SaveKey"), FontSize = 12 };
        aiKeySave.Click += (_, _) =>
        {
            var prov = SelectedProvider();
            if (prov is "None") { ShowStatus(Loc.T("Status.AiPickProvider")); return; }
            AiService.SetKey(prov, aiKeyBox.Password);
            aiKeyBox.Password = "";
            RefreshKeyState();
            ShowStatus(Loc.T("Status.AiKeySaved"));
        };
        var aiKeyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        aiKeyRow.Children.Add(aiKeyBox);
        aiKeyRow.Children.Add(aiKeySave);
        panel.Children.Add(aiProviderBox);
        panel.Children.Add(aiModelBox);
        panel.Children.Add(aiEndpointBox);
        panel.Children.Add(aiKeyRow);
        panel.Children.Add(aiKeyState);

        // ---- accent colour (#33) ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Accent.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Accent.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        var accentPicker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Color = ColorUtil.Parse(_library.AccentColor)
        };
        var swatchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string[] accentPresets = { "#D97757", "#6A9BCC", "#788C5D", "#7B1FA2", "#2E7D6B", "#FBC02D", "#D32F2F" };
        foreach (var hex in accentPresets)
        {
            var h = hex;
            var sw = new Button
            {
                Width = 36, Height = 26,
                Background = new SolidColorBrush(ColorUtil.Parse(h)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128))
            };
            ToolTipService.SetToolTip(sw, h == "#D97757" ? Loc.T("Settings.Accent.ClayDefault") : h);
            sw.Click += (_, _) => accentPicker.Color = ColorUtil.Parse(h);   // fires ColorChanged
            swatchRow.Children.Add(sw);
        }
        panel.Children.Add(swatchRow);
        var accentExp = new Expander { Header = Loc.T("Settings.Accent.Custom"), Content = accentPicker, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(accentExp);
        accentPicker.ColorChanged += (_, args) => SetAccent(args.NewColor);

        // ---- user-saved custom colours (#5-batch2): a second, user-curated row ----
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Accent.Mine"), FontSize = 12, Opacity = 0.8, Margin = new Thickness(0, 6, 0, 0) });
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void RebuildCustomRow()
        {
            customRow.Children.Clear();
            foreach (var hex in _library.CustomColors)
            {
                var h = hex;
                var sw = new Button
                {
                    Width = 36, Height = 26,
                    Background = new SolidColorBrush(ColorUtil.Parse(h)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128))
                };
                ToolTipService.SetToolTip(sw, Loc.T("Settings.Accent.SwatchTip", h));
                sw.Click += (_, _) => accentPicker.Color = ColorUtil.Parse(h);
                sw.RightTapped += (_, _) => { _library.CustomColors.Remove(h); ScheduleSave(); RebuildCustomRow(); };
                customRow.Children.Add(sw);
            }
            var add = new Button { Width = 36, Height = 26, Content = new TextBlock { Text = "+", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center }, Padding = new Thickness(0) };
            ToolTipService.SetToolTip(add, Loc.T("Settings.Accent.AddTip"));
            add.Click += (_, _) =>
            {
                var hex = $"#{accentPicker.Color.R:X2}{accentPicker.Color.G:X2}{accentPicker.Color.B:X2}";
                if (!_library.CustomColors.Contains(hex))
                {
                    _library.CustomColors.Add(hex);
                    if (_library.CustomColors.Count > 12) _library.CustomColors.RemoveAt(0);
                    ScheduleSave();
                    RebuildCustomRow();
                }
            };
            customRow.Children.Add(add);
        }
        RebuildCustomRow();
        panel.Children.Add(customRow);

        // ---- accent follows pen / notebook (#6-batch2) ----
        var followBox = new ComboBox { Header = Loc.T("Settings.AccentFollow.Header"), Width = 220 };
        foreach (var tag in new[] { "Manual", "Pen", "Notebook" })
            followBox.Items.Add(new ComboBoxItem { Content = Loc.T("AccentFollow." + tag), Tag = tag });
        foreach (ComboBoxItem it in followBox.Items)
            if ((string)it.Tag == _library.AccentFollow) { followBox.SelectedItem = it; break; }
        if (followBox.SelectedItem == null) followBox.SelectedIndex = 0;
        followBox.SelectionChanged += (_, _) =>
        {
            if (followBox.SelectedItem is not ComboBoxItem ci || ci.Tag is not string mode) return;
            _library.AccentFollow = mode;
            ScheduleSave();
            try
            {
                // apply the new source immediately
                if (mode == "Pen" && _library.Pens.FirstOrDefault(p => p.Id == _activePresetId) is PenPreset ap)
                    ApplyAccent(ColorUtil.Parse(ap.Color), refreshTheme: true);
                else if (mode == "Notebook" && _curNb != null)
                    ApplyAccent(ColorUtil.Parse(_curNb.Color), refreshTheme: true);
                else
                    ApplyAccent(ColorUtil.Parse(_library.AccentColor), refreshTheme: true);
            }
            catch { }
        };
        panel.Children.Add(followBox);

        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Import.Header"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var recoverBtn = new Button { Content = Loc.T("Settings.Import.Recover"), HorizontalAlignment = HorizontalAlignment.Left };
        recoverBtn.Click += (_, _) => ImportFromLegacy();
        panel.Children.Add(recoverBtn);
        var importBtn = new Button { Content = Loc.T("Settings.Import.FromFile"), HorizontalAlignment = HorizontalAlignment.Left };
        importBtn.Click += async (_, _) => { var p = await PickJsonFileAsync(); if (p != null) ImportFromFile(p); };
        panel.Children.Add(importBtn);
        panel.Children.Add(new TextBlock { Text = Loc.T("Settings.Import.Desc"), FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // scrollable so every section (incl. the colour picker) stays reachable
        var scroller = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 540,
            Padding = new Thickness(0, 0, 12, 0)
        };
        var dlg = new ContentDialog
        {
            Title = Loc.T("Settings.Title"),
            Content = scroller,
            CloseButtonText = Loc.T("Common.Done"),
            XamlRoot = RootGrid.XamlRoot
        };
        _settingsDialog = dlg;
        try { await dlg.ShowAsync(); } finally { _settingsDialog = null; }
        return languageChanged;
    }

    // Held only while the Settings dialog is up, so the language picker can
    // dismiss it and let ShowSettingsDialogAsync's caller present a fresh one.
    private ContentDialog? _settingsDialog;

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PickJsonFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void ImportFromLegacy()
    {
        var legacyPath = System.IO.Path.Combine(LibraryStore.LegacyDir, "library.json");
        var src = LibraryStore.LoadFrom(legacyPath) ?? LibraryStore.LoadFrom(legacyPath + ".bak");
        if (src == null) { ShowStatus("No notebooks found in the previous location."); return; }
        int n = LibraryStore.Merge(_library, src);
        BuildTree();
        SaveNow();
        ShowStatus(n > 0 ? $"Recovered {n} notebook(s)." : "Those notebooks are already here.");
    }

    private void ImportFromFile(string path)
    {
        var src = LibraryStore.LoadFrom(path);
        if (src == null) { ShowStatus("That file didn't contain any notebooks."); return; }
        int n = LibraryStore.Merge(_library, src);
        BuildTree();
        SaveNow();
        ShowStatus(n > 0 ? $"Imported {n} notebook(s)." : "Those notebooks are already here.");
    }

    private void Sidebar_Toggle(object sender, RoutedEventArgs e)
    {
        if (BtnSidebar.IsChecked == true) FadeIn(NotebookPanel, slideX: -18);
        else FadeOut(NotebookPanel);
    }

    private void Sidebar_Close(object sender, RoutedEventArgs e)
    {
        BtnSidebar.IsChecked = false;
        FadeOut(NotebookPanel);
    }

    // =======================================================================
    // Tools
    // =======================================================================
    // Lasso button click-count mapping (#7-batch2): single click = lasso tool;
    // double click = leave blank space ONCE then return to the pen; pressing the
    // lasso button while in that one-shot mode backs out to the pen untouched.
    private long _lassoClickMs;
    private bool _blankSpaceOnce;

    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string tag) return;
        if (tag == "Select")
        {
            long now = Environment.TickCount64;
            if (_blankSpaceOnce)
            {
                // already in one-shot blank-space mode -> bail out to the pen
                _blankSpaceOnce = false;
                SelectTool("Pen");
                return;
            }
            if (now - _lassoClickMs < 400)
            {
                _lassoClickMs = 0;
                _blankSpaceOnce = true;
                SelectTool("FreeSpace");
                ShowStatus("Leave blank space once — drag on the page, then the pen comes back. Tap the lasso again to cancel.");
                return;
            }
            _lassoClickMs = now;
        }
        SelectTool(tag);
    }

    // Text defaults resolve per-notebook first, then library-wide (#10-roadmap).
    private (string Font, float Size) EffectiveTextDefaults() => (
        _curNb?.DefaultFont ?? _library.DefaultFont,
        (float)(_curNb?.DefaultFontSize ?? _library.DefaultFontSize));

    private string _toolTag = "Pen";

    private void SelectTool(string tag)
    {
        if (tag != "FreeSpace") _blankSpaceOnce = false;   // any explicit tool pick cancels the one-shot
        _toolTag = tag;
        // picking a drawing tool leaves comment mode, so the user is never stuck
        // unable to draw with the pin tool still armed (#roadmap)
        if (ToolComment.IsChecked == true) { ToolComment.IsChecked = false; Surface.CommentMode = false; }
        ApplyToolbarVisibility();   // pen-only buttons follow the active tool (#topbar)
        ToolPen.IsChecked = tag == "Pen";
        ToolText.IsChecked = tag == "Text";
        ToolSelect.IsChecked = tag == "Select";
        ToolSpace.IsChecked = tag == "FreeSpace";

        var tool = Enum.Parse<ToolType>(tag);
        // Leaving writing mode reverts the chosen size/font to the saved defaults (#8).
        if (tool != ToolType.Text)
        {
            var (df, ds) = EffectiveTextDefaults();
            Surface.PendingFontSize = ds;
            Surface.PendingFontFamily = df;
        }
        Surface.SetTool(tool);
        UpdateFormatBarVisibility();
        RefreshEraserChip();
        ApplyPenRowVisibility();   // the pen row only belongs to pen mode (#14-batch4)

        switch (tool)
        {
            case ToolType.Text:
                ShowStatus("Tap anywhere on the page to add a text box.");
                break;
            case ToolType.Select:
                ShowStatus("Draw a lasso around strokes, then drag inside the box to move them. Del removes them.");
                break;
            case ToolType.FreeSpace:
                ShowStatus("Drag downwards to push everything below apart; drag up to pull together.");
                break;
        }
    }

    private void TouchDraw_Click(object sender, RoutedEventArgs e)
    {
        Surface.HandDrawMode = TouchDrawToggle.IsChecked == true;
        ShowStatus(Surface.HandDrawMode
            ? "Finger and mouse drawing enabled."
            : "Finger and mouse drawing disabled; touch pans the page.");
    }

    private void ToggleComment_Click(object sender, RoutedEventArgs e)
    {
        Surface.CommentMode = ToolComment.IsChecked == true;
        ShowStatus(Surface.CommentMode
            ? "Comment mode: tap the page to drop a note pin, or tap a pin to read, resolve or delete it. Tap the button again to leave."
            : "Comment mode off.");
    }

    // Read / edit / resolve / delete a comment pin (#roadmap). An empty comment
    // (a mis-tap) is discarded when the flyout closes.
    private void ShowCommentFlyout(PageComment c, Point screenPos)
    {
        var fly = new Flyout();
        var panel = new StackPanel { Spacing = 8, Width = 260 };
        panel.Children.Add(new TextBlock
        {
            Text = c.Resolved ? "Resolved comment" : "Comment",
            FontSize = 12, Opacity = 0.7
        });
        var box = new TextBox
        {
            PlaceholderText = "Write a note…",
            Text = c.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 160
        };
        box.TextChanged += (_, _) => { c.Text = box.Text; Surface.NotifyCommentEdited(); ScheduleSave(); };
        panel.Children.Add(box);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var resolveBtn = new Button { Content = c.Resolved ? "Reopen" : "Resolve" };
        resolveBtn.Click += (_, _) => { Surface.ResolveComment(c, !c.Resolved); ScheduleSave(); fly.Hide(); };
        var delBtn = new Button { Content = "Delete" };
        delBtn.Click += (_, _) => { Surface.DeleteComment(c); ScheduleSave(); fly.Hide(); };
        row.Children.Add(resolveBtn);
        row.Children.Add(delBtn);
        panel.Children.Add(row);

        fly.Content = panel;
        fly.Closed += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(c.Text)) { Surface.DeleteComment(c); ScheduleSave(); }
        };
        fly.ShowAt(Surface, new FlyoutShowOptions { Position = screenPos });
        box.Focus(FocusState.Programmatic);
    }

    private void MouseMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            SetMouseMode(Enum.Parse<MouseMode>(tag));
    }

    private void SetMouseMode(MouseMode mode)
    {
        Surface.MouseMode = mode;
        MouseAuto.IsChecked = mode == MouseMode.Auto;
        MouseGrab.IsChecked = mode == MouseMode.Grab;
        MouseSelect.IsChecked = mode == MouseMode.Select;
        MouseMove.IsChecked = mode == MouseMode.Move;
        MouseModeGlyph.Text = mode switch
        {
            MouseMode.Grab => "✋",
            MouseMode.Select => "⬚",
            MouseMode.Move => "✥",
            _ => "↖"
        };
        ToolTipService.SetToolTip(MouseModeBtn, "Mouse mode: " + mode);
        ShowStatus(mode switch
        {
            MouseMode.Grab => "Mouse: grab — drag to pan the page.",
            MouseMode.Select => "Mouse: select — drag a box to lasso strokes.",
            MouseMode.Move => "Mouse: move — drag images and shapes; drag a handle to resize.",
            _ => "Mouse: auto — click to select or edit, drag empty space to select."
        });
    }

    private void RulerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Surface.RulerMode = RulerToggle.IsOn;
        if (RulerToggle.IsOn) SelectTool("Pen");
        Surface.Refresh();
    }

    private void RulerSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RulerDegLabel == null) return;
        Surface.RulerAngle = e.NewValue;
        RulerDegLabel.Text = $"Angle: {(int)e.NewValue}°";
        Surface.Refresh();
    }

    // Keep the Settings flyout slider in sync when the ruler is tilted by gesture.
    private void OnRulerAngleChanged(double deg)
    {
        if (RulerSlider == null) return;
        _syncingUi = true;
        try { RulerSlider.Value = ((deg % 180) + 180) % 180; } catch { }
        _syncingUi = false;
        if (RulerDegLabel != null) RulerDegLabel.Text = $"Angle: {(int)deg}°";
    }

    private void InsertShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;
        SelectTool("Select");
        switch (tag)
        {
            case "Line": Surface.InsertShape(ShapeKind.Line, false); break;
            case "Arrow": Surface.InsertShape(ShapeKind.Arrow, false); break;
            case "Rect": Surface.InsertShape(ShapeKind.Rect, false); break;
            case "Square": Surface.InsertShape(ShapeKind.Rect, true); break;
            case "Ellipse": Surface.InsertShape(ShapeKind.Ellipse, false); break;
            case "Circle": Surface.InsertShape(ShapeKind.Ellipse, true); break;
            case "Triangle": Surface.InsertShape(ShapeKind.Triangle, false); break;
            case "RightTriangle": Surface.InsertShape(ShapeKind.RightTriangle, false); break;
            case "Diamond": Surface.InsertShape(ShapeKind.Diamond, false); break;
            case "Parallelogram": Surface.InsertShape(ShapeKind.Parallelogram, false); break;
            case "Trapezoid": Surface.InsertShape(ShapeKind.Trapezoid, false); break;
            case "Pentagon": Surface.InsertShape(ShapeKind.Pentagon, false); break;
            case "Hexagon": Surface.InsertShape(ShapeKind.Hexagon, false); break;
            case "Star": Surface.InsertShape(ShapeKind.Star, false); break;
            case "AxesXY": Surface.InsertShape(ShapeKind.AxesXY, false); break;
            case "AxesXYZ": Surface.InsertShape(ShapeKind.AxesXYZ, false); break;
        }
        ShowStatus("Drag the shape to move it; drag a corner handle to resize. Del removes it.");
    }

    // =======================================================================
    // Undo / redo / history / replay
    // =======================================================================
    private void Undo_Click(object sender, RoutedEventArgs e) => Surface.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Surface.Redo();

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = Surface.UndoManager.CanUndo;
        BtnRedo.IsEnabled = Surface.UndoManager.CanRedo;
    }

    private void HistoryFlyout_Opening(object? sender, object e)
    {
        var items = Surface.UndoManager.History;
        HistoryList.ItemsSource = items.Count > 0 ? items : new[] { "(no edits on this page yet)" };
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (BtnReplay.IsChecked == true)
        {
            if (_curPage == null || _curPage.Strokes.Count == 0)
            {
                BtnReplay.IsChecked = false;
                ShowStatus("Nothing to replay on this page yet.");
                return;
            }
            Surface.StartReplay();
        }
        else
        {
            Surface.StopReplay();
        }
    }

    private void UndoAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        Surface.Undo();
        args.Handled = true;
    }

    private void RedoAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        Surface.Redo();
        args.Handled = true;
    }

    private void DeleteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        if (Surface.HasDeletable)
        {
            Surface.DeleteSelection();
            args.Handled = true;
        }
        else
        {
            args.Handled = false;
        }
    }

    private void DuplicateAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        Surface.CommitActiveSelection();
        Surface.DuplicateSelection();
        args.Handled = true;
    }

    private void PastePlainTextAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = true;
            PastePlainText();
        }
        else
        {
            args.Handled = false;
        }
    }

    private async void PastePlainText()
    {
        var activeBox = Surface.ActiveTextBox;
        if (activeBox == null) return;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                activeBox.Document.Selection.TypeText(text);
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Failed to paste plain text: " + ex.Message);
        }
    }

    // =======================================================================
    // Zoom / view
    // =======================================================================
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(1f / 1.25f);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Surface.SetViewZoom(1f);
    private void HomeView_Click(object sender, RoutedEventArgs e) => Surface.ResetView();
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Surface.FitToContent(28);

    private void OnViewChanged()
    {
        var pct = $"{Math.Round(Surface.ViewZoom * 100)}%";
        ZoomText.Text = pct;
        ZoomBtnText.Text = pct;
        if (pct != _lastZoomPct)
        {
            _lastZoomPct = pct;
            FadeIn(ZoomBorder, 120, pop: false);
            _zoomTimer.Stop();
            _zoomTimer.Start();
        }
        ScheduleSave();
    }

    // =======================================================================
    // Page settings
    // =======================================================================
    private void BgPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || sender is not Button b || b.Tag is not string hex) return;
        SetPageBackground(hex);
    }

    private void BgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingUi || _curPage == null) return;
        SetPageBackground(ColorUtil.ToHex(args.NewColor));
    }

    // Applies a page background and, when the page flips between light and dark,
    // flips near-black/near-white text with it so notes stay readable (#10-batch2).
    private void SetPageBackground(string hex)
    {
        if (_curPage == null) return;
        bool wasDark = ColorUtil.IsDark(ColorUtil.Parse(_curPage.Background));
        bool nowDark = ColorUtil.IsDark(ColorUtil.Parse(hex));
        _curPage.Background = hex;
        if (wasDark != nowDark && _curPage.Texts.Count > 0)
        {
            FlipTextContrast(_curPage, nowDark);
            Surface.FlushTexts();
            Surface.RebuildTextLayer();
        }
        Surface.Refresh();
        ScheduleSave();
    }

    // Rewrites each text box's RTF colour table: near-black ink turns ivory on a
    // dark page and near-white ink turns near-black on a light page. Colours with
    // real hue (reds, blues, highlights) are left alone.
    private static void FlipTextContrast(NotePage page, bool nowDark)
    {
        foreach (var t in page.Texts)
        {
            if (string.IsNullOrEmpty(t.Rtf)) continue;
            t.Rtf = System.Text.RegularExpressions.Regex.Replace(t.Rtf,
                @"\\red(\d{1,3})\\green(\d{1,3})\\blue(\d{1,3})",
                m =>
                {
                    int r = int.Parse(m.Groups[1].Value), g = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
                    int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    bool greyish = max - min < 40;   // only flip near-neutral colours
                    int lum = (r * 299 + g * 587 + b * 114) / 1000;
                    if (greyish && nowDark && lum < 100) return @"\red250\green249\blue245";   // ivory
                    if (greyish && !nowDark && lum > 180) return @"\red20\green20\blue19";     // near-black
                    return m.Value;
                });
        }
    }

    private void SetDefaultBg_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;
        _library.DefaultBackground = _curPage.Background;
        ScheduleSave();
        ShowStatus($"New pages will now start with {_library.DefaultBackground}.");
    }

    private void SetNotebookDefaultBg_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || _curNb == null) { ShowStatus("No active notebook."); return; }
        _curNb.DefaultBackground = _curPage.Background;
        ScheduleSave();
        ShowStatus($"New pages in this notebook will start with {_curNb.DefaultBackground}.");
    }

    private void SetDefaultGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;
        _library.DefaultGrid = _curPage.Grid;
        _library.DefaultGridSpacing = _curPage.GridSpacing;
        ScheduleSave();
        ShowStatus("New pages will now start with this grid.");
    }

    private void SetNotebookDefaultGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || _curNb == null) { ShowStatus("No active notebook."); return; }
        _curNb.DefaultGrid = _curPage.Grid;
        _curNb.DefaultGridSpacing = _curPage.GridSpacing;
        ScheduleSave();
        ShowStatus("New pages in this notebook will start with this grid.");
    }

    private void GridRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || _curPage == null || GridRadios.SelectedIndex < 0) return;
        _curPage.Grid = (GridType)GridRadios.SelectedIndex;
        Surface.Refresh();
        ScheduleSave();
    }

    private void GridColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;
        // Tag holds "#RRGGBB"; the untagged "Auto" swatch clears back to automatic
        _curPage.GridColor = (sender as FrameworkElement)?.Tag as string;
        Surface.Refresh();
        ScheduleSave();
    }

    private void SpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingUi || _curPage == null) return;
        _curPage.GridSpacing = e.NewValue;
        Surface.Refresh();
        ScheduleSave();
    }

    // =======================================================================
    // Pen panel visibility
    // =======================================================================
    private void ApplyPenRowVisibility()
    {
        bool rowOn = _curPage?.PenRowVisible ?? true;
        bool showRow = _uiHidden ? _floatPen : rowOn;
        bool showChip = !_uiHidden && !rowOn;
        // outside pen/eraser mode neither the row nor its reopen chip belongs
        // on screen (#14-batch4); the floating pen in minimal UI stays manual
        bool penMode = _toolTag is "Pen" or "Eraser";
        if (!penMode && !_uiHidden) { showRow = false; showChip = false; }
        // slide in from the bottom edge the dock lives on, like the other bars
        if (showRow) FadeIn(PenRow, slideY: 24); else FadeOut(PenRow);
        if (showChip) FadeIn(PenRowShowBtn); else FadeOut(PenRowShowBtn);
    }

    private void PenRowCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (_uiHidden)
        {
            _floatPen = false;
        }
        else if (_curPage != null)
        {
            _curPage.PenRowVisible = false;
            ScheduleSave();
        }
        ApplyPenRowVisibility();
    }

    private void PenRowShow_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage != null)
        {
            _curPage.PenRowVisible = true;
            ScheduleSave();
        }
        ApplyPenRowVisibility();
    }

    // =======================================================================
    // Minimal UI / fullscreen
    // =======================================================================
    private void HideUi_Click(object sender, RoutedEventArgs e)
    {
        _uiHidden = true;
        _floatPen = false;
        FadeOut(TopBar);
        FadeOut(FormatBar);
        FadeOut(NotebookPanel);
        FadeOut(CalcPanel);
        BtnCalc.IsChecked = false;
        // if the cluster was left tucked away as an edge tab, keep it tucked
        // (rather than popping the full 3-button cluster back over it).
        if (_minimalDocked) { PositionDockedTab(); FadeIn(MinimalButtonsTab, pop: false); }
        else FadeIn(MinimalButtons);
        ApplyPenRowVisibility();
        // hiding everything also goes full screen — but only take credit for it
        // if we weren't already full screen, so restore won't yank the user out.
        try
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
            {
                _hideEnteredFullscreen = true;
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                _hideEnteredFullscreen = false;
            }
        }
        catch { }
        UpdateFullscreenIcon();
    }

    private void RestoreUi_Click(object sender, RoutedEventArgs e)
    {
        if (_minimalButtonsDragged) return;
        _uiHidden = false;
        FadeIn(TopBar, pop: false, slideY: -14);
        FadeOut(MinimalButtons);
        FadeOut(MinimalButtonsTab);
        if (BtnSidebar.IsChecked == true) FadeIn(NotebookPanel, slideX: -18);
        else FadeOut(NotebookPanel);
        UpdateFormatBarVisibility();
        ApplyPenRowVisibility();
        // only leave full screen if hiding the UI is what entered it; if the user
        // was already full screen beforehand, stay full screen.
        try
        {
            if (_hideEnteredFullscreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
                ReapplyBorderlessCaption();
        }
        catch { }
        _hideEnteredFullscreen = false;
        UpdateFullscreenIcon();
    }

    private void SnapMinimalButtons()
    {
        try
        {
            var t = MinimalButtons.TransformToVisual(CanvasArea);
            var tl = t.TransformPoint(new Point(0, 0));
            double bw = MinimalButtons.ActualWidth, bh = MinimalButtons.ActualHeight;
            double cx = tl.X + bw / 2.0, cy = tl.Y + bh / 2.0;
            double w = CanvasArea.ActualWidth, h = CanvasArea.ActualHeight;

            // dragged flush against a side edge -> tuck away as a pull-out tab
            const double dockZone = 34;
            if (cx < dockZone || cx > w - dockZone)
            {
                CollapseMinimalButtons(left: cx < dockZone, top: cy < h / 2);
                return;
            }

            const double pad = 14;
            // snap to the NEAREST edge; near an edge's middle, magnetise to centre (#16-batch2)
            double dL = cx, dR = w - cx, dT = cy, dB = h - cy;
            double m = Math.Min(Math.Min(dL, dR), Math.Min(dT, dB));
            if (m == dT || m == dB)
            {
                bool top = m == dT;
                MinimalButtons.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                if (Math.Abs(cx - w / 2) < w / 6)
                {
                    MinimalButtons.HorizontalAlignment = HorizontalAlignment.Center;
                    MinimalButtons.Margin = new Thickness(0, top ? pad : 0, 0, top ? 0 : pad);
                }
                else
                {
                    bool left = cx < w / 2;
                    MinimalButtons.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                    double off = Math.Clamp(left ? cx - bw / 2 : w - cx - bw / 2, pad, Math.Max(pad, w - bw - pad));
                    MinimalButtons.Margin = new Thickness(left ? off : 0, top ? pad : 0, left ? 0 : off, top ? 0 : pad);
                }
            }
            else
            {
                bool left = m == dL;
                MinimalButtons.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                if (Math.Abs(cy - h / 2) < h / 6)
                {
                    MinimalButtons.VerticalAlignment = VerticalAlignment.Center;
                    MinimalButtons.Margin = new Thickness(left ? pad : 0, 0, left ? 0 : pad, 0);
                }
                else
                {
                    bool top = cy < h / 2;
                    MinimalButtons.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                    double off = Math.Clamp(top ? cy - bh / 2 : h - cy - bh / 2, pad, Math.Max(pad, h - bh - pad));
                    MinimalButtons.Margin = new Thickness(left ? pad : 0, top ? off : 0, left ? 0 : pad, top ? 0 : off);
                }
            }
            MinimalButtons.RenderTransform = null;
            PopIn(MinimalButtons, 0.96, 240);   // settle wobble on snap
        }
        catch { }
    }

    // Tucks the 3-button cluster away as a small tab flush in the nearest corner
    // of that edge -- exactly in the corner, never a pixel off-screen (#16-batch2).
    private void CollapseMinimalButtons(bool left, bool top)
    {
        _minimalDocked = true;
        _minimalDockedLeft = left;
        _minimalDockedTop = top;
        MinimalButtons.RenderTransform = null;
        FadeOut(MinimalButtons, 130);

        MinimalButtonsTabGlyph.Glyph = left ? "" : "";  // chevron points inward, toward the canvas
        MinimalButtonsTab.RenderTransform = null;
        PositionDockedTab();
        FadeIn(MinimalButtonsTab, pop: false);
    }

    private void PositionDockedTab()
    {
        MinimalButtonsTab.HorizontalAlignment = _minimalDockedLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        MinimalButtonsTab.VerticalAlignment = _minimalDockedTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        MinimalButtonsTab.Margin = new Thickness(0);
    }

    // Restores the full 3-button cluster near the corner the tab was docked in.
    private void ExpandMinimalButtons()
    {
        _minimalDocked = false;
        FadeOut(MinimalButtonsTab, 110);
        MinimalButtons.HorizontalAlignment = _minimalDockedLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        MinimalButtons.VerticalAlignment = _minimalDockedTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        MinimalButtons.Margin = new Thickness(
            _minimalDockedLeft ? 14 : 0, _minimalDockedTop ? 14 : 0,
            _minimalDockedLeft ? 0 : 14, _minimalDockedTop ? 0 : 14);
        MinimalButtons.RenderTransform = null;
        FadeIn(MinimalButtons);
    }

    private void MinimalButtonsTab_Tapped(object sender, TappedRoutedEventArgs e) => ExpandMinimalButtons();

    // Dragging the tab far enough away from its edge undocks it back to the full
    // cluster; a small nudge that doesn't clear the edge just snaps back into place.
    private void TryUndockFromDrag()
    {
        try
        {
            var t = MinimalButtonsTab.TransformToVisual(CanvasArea);
            var tl = t.TransformPoint(new Point(0, 0));
            double cx = tl.X + MinimalButtonsTab.ActualWidth / 2.0;
            double w = CanvasArea.ActualWidth;
            double distFromEdge = _minimalDockedLeft ? cx : w - cx;
            if (distFromEdge > 46) ExpandMinimalButtons();
            else MinimalButtonsTab.RenderTransform = null;
        }
        catch { }
    }

    private void FloatPen_Click(object sender, RoutedEventArgs e)
    {
        if (_minimalButtonsDragged) return;
        _floatPen = !_floatPen;
        ApplyPenRowVisibility();
    }

    private void FloatNotebook_Click(object sender, RoutedEventArgs e)
    {
        if (_minimalButtonsDragged) return;
        NotebookPanel.Visibility = NotebookPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var presenter = AppWindow.Presenter;
        if (presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            ReapplyBorderlessCaption();
        }
        else
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        UpdateFullscreenIcon();
    }

    private void UpdateFullscreenIcon()
    {
        try
        {
            bool covers = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
                       || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            BtnFullscreenIcon.Glyph = covers ? "" : "";   // inward once it fills the screen
            ToolTipService.SetToolTip(BtnWinFull, covers ? "Restore down" : "Maximise");
        }
        catch { }
    }

    private void FullscreenAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Fullscreen_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // Esc: close the start screen / gallery if it's open; otherwise leave full
    // screen (unless a text box has focus — then let the control handle it).
    private void EscAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ShortcutsPanel.Visibility == Visibility.Visible)
        {
            FadeOut(ShortcutsPanel, 140);
            args.Handled = true;
            return;
        }
        if (GalleryPanel.Visibility == Visibility.Visible)
        {
            if (_galleryNb != null) { _galleryNb = null; BuildGallery(); }   // step back first
            else CloseGallery();
            args.Handled = true;
            return;
        }
        if (Surface.ActiveTextBox == null && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            UpdateFullscreenIcon();
            args.Handled = true;
            return;
        }
        args.Handled = false;
    }

    private void ShortcutsAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleShortcutsPanel();
        args.Handled = true;
    }

    private void ToggleShortcutsPanel()
    {
        if (ShortcutsPanel.Visibility == Visibility.Visible) FadeOut(ShortcutsPanel, 140);
        else FadeIn(ShortcutsPanel, 200);
    }

    private void Shortcuts_Close(object sender, TappedRoutedEventArgs e) => FadeOut(ShortcutsPanel, 140);

    // =======================================================================
    // KEYBOARD PRESETS (C5)
    // The accelerators live here rather than in XAML so the preset is the only
    // source of truth for both the live bindings and the F1 sheet — the sheet
    // is generated from the accelerators that actually got installed, so it
    // cannot claim a key that no longer works. Per-command remapping is out of
    // scope; Ctrl+K reaches every command by name.
    // =======================================================================
    private sealed record KeyBind(string Id, VKey Key, VMod Mods);

    private const VMod Ctrl = VMod.Control;
    private const VMod CtrlShift = VMod.Control | VMod.Shift;

    // Esc, Delete and F1 are identical in every preset on purpose: they are the
    // escape hatches, and a preset that moved them could strand the user.
    private static readonly Dictionary<string, KeyBind[]> KeyPresets = new()
    {
        ["Quill"] = new[]
        {
            new KeyBind("Undo", VKey.Z, Ctrl),
            new KeyBind("Redo", VKey.Y, Ctrl),
            new KeyBind("Cut", VKey.X, Ctrl),
            new KeyBind("Copy", VKey.C, Ctrl),
            new KeyBind("Paste", VKey.V, Ctrl),
            new KeyBind("PastePlain", VKey.V, CtrlShift),
            new KeyBind("Duplicate", VKey.D, Ctrl),
            new KeyBind("Delete", VKey.Delete, VMod.None),
            new KeyBind("Search", VKey.F, Ctrl),
            new KeyBind("Palette", VKey.K, Ctrl),
            new KeyBind("Fullscreen", VKey.F11, VMod.None),
            new KeyBind("Shortcuts", VKey.F1, VMod.None),
            new KeyBind("Escape", VKey.Escape, VMod.None),
            new KeyBind("ToolPen", VKey.P, VMod.None),
            new KeyBind("ToolText", VKey.T, VMod.None),
            new KeyBind("ToolSelect", VKey.L, VMod.None),
            new KeyBind("ToolSpace", VKey.S, VMod.None),
        },
        ["OneNote"] = new[]
        {
            new KeyBind("Undo", VKey.Z, Ctrl),
            new KeyBind("Redo", VKey.Y, Ctrl),
            new KeyBind("Cut", VKey.X, Ctrl),
            new KeyBind("Copy", VKey.C, Ctrl),
            new KeyBind("Paste", VKey.V, Ctrl),
            new KeyBind("PastePlain", VKey.V, CtrlShift),
            new KeyBind("Duplicate", VKey.D, Ctrl),
            new KeyBind("Delete", VKey.Delete, VMod.None),
            new KeyBind("Search", VKey.E, Ctrl),          // OneNote searches with Ctrl+E
            new KeyBind("Palette", VKey.K, Ctrl),
            new KeyBind("Fullscreen", VKey.F11, VMod.None),
            new KeyBind("Shortcuts", VKey.F1, VMod.None),
            new KeyBind("Escape", VKey.Escape, VMod.None),
            // OneNote never steals bare letters — they belong to the text box.
            new KeyBind("ToolPen", VKey.P, CtrlShift),
            new KeyBind("ToolText", VKey.T, CtrlShift),
            new KeyBind("ToolSelect", VKey.L, CtrlShift),
            new KeyBind("ToolSpace", VKey.S, CtrlShift),
        },
        ["Photoshop"] = new[]
        {
            new KeyBind("Undo", VKey.Z, Ctrl),
            new KeyBind("Redo", VKey.Z, CtrlShift),       // Photoshop steps forward with Ctrl+Shift+Z
            new KeyBind("Cut", VKey.X, Ctrl),
            new KeyBind("Copy", VKey.C, Ctrl),
            new KeyBind("Paste", VKey.V, Ctrl),
            new KeyBind("PastePlain", VKey.V, CtrlShift),
            new KeyBind("Duplicate", VKey.J, Ctrl),       // "duplicate layer"
            new KeyBind("Delete", VKey.Delete, VMod.None),
            new KeyBind("Search", VKey.F, Ctrl),
            new KeyBind("Palette", VKey.K, Ctrl),
            new KeyBind("Fullscreen", VKey.F, VMod.None),
            new KeyBind("Shortcuts", VKey.F1, VMod.None),
            new KeyBind("Escape", VKey.Escape, VMod.None),
            new KeyBind("ToolPen", VKey.B, VMod.None),    // brush
            new KeyBind("ToolText", VKey.T, VMod.None),
            new KeyBind("ToolSelect", VKey.L, VMod.None), // lasso
            new KeyBind("ToolSpace", VKey.H, VMod.None),  // hand
        },
    };

    // id -> resource key; the visible label comes from Loc so the picker and the
    // F1 sheet both follow the chosen language.
    private static readonly (string Id, string LocKey)[] KeyPresetOptions =
    {
        ("Quill", "KeyPreset.Quill"),
        ("OneNote", "KeyPreset.OneNote"),
        ("Photoshop", "KeyPreset.Photoshop"),
    };

    // Label + handler per command id, in the order the F1 sheet lists them.
    private (string Id, string Label, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> Run)[] KeyCommands => new (string, string, TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs>)[]
    {
        ("Fullscreen", "Full screen", FullscreenAccel_Invoked),
        ("Escape", "Close picker / leave full screen", EscAccel_Invoked),
        ("Shortcuts", "This sheet", ShortcutsAccel_Invoked),
        ("Search", "Search all notes", SearchAccel_Invoked),
        ("Palette", "Command palette", PaletteAccel_Invoked),
        ("Undo", "Undo", UndoAccel_Invoked),
        ("Redo", "Redo", RedoAccel_Invoked),
        ("Copy", "Copy (ink, images and text)", CopyAccel_Invoked),
        ("Cut", "Cut", CutAccel_Invoked),
        ("Paste", "Paste", PasteAccel_Invoked),
        ("PastePlain", "Paste as plain text", PastePlainTextAccel_Invoked),
        ("Duplicate", "Duplicate the selection", DuplicateAccel_Invoked),
        ("Delete", "Remove the selection", DeleteAccel_Invoked),
        ("ToolPen", "Pen tool", ToolPenAccel_Invoked),
        ("ToolText", "Text tool", ToolTextAccel_Invoked),
        ("ToolSelect", "Lasso tool", ToolSelectAccel_Invoked),
        ("ToolSpace", "Insert-space tool", ToolSpaceAccel_Invoked),
    };

    private void ToolPenAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => ToolAccel("Pen", a);
    private void ToolTextAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => ToolAccel("Text", a);
    private void ToolSelectAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => ToolAccel("Select", a);
    private void ToolSpaceAccel_Invoked(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) => ToolAccel("FreeSpace", a);

    private void ToolAccel(string tag, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Presets that bind bare letters must still let those letters be typed.
        if (TextBoxFocused) { args.Handled = false; return; }
        SelectTool(tag);
        args.Handled = true;
    }

    // Combos Windows owns outright: binding one produces an accelerator that
    // never fires, which is exactly the silent breakage this step must avoid.
    private static bool IsReservedCombo(VKey k, VMod m)
    {
        if (m.HasFlag(VMod.Windows)) return true;
        if (m.HasFlag(VMod.Menu) && (k == VKey.F4 || k == VKey.Tab || k == VKey.Escape)) return true;
        if (m.HasFlag(Ctrl) && k == VKey.Escape) return true;
        return false;
    }

    private static string ComboText(VKey k, VMod m)
    {
        var sb = new System.Text.StringBuilder();
        if (m.HasFlag(Ctrl)) sb.Append("Ctrl+");
        if (m.HasFlag(VMod.Menu)) sb.Append("Alt+");
        if (m.HasFlag(VMod.Shift)) sb.Append("Shift+");
        if (m.HasFlag(VMod.Windows)) sb.Append("Win+");
        sb.Append(k switch
        {
            VKey.Escape => "Esc",
            VKey.Delete => "Del",
            _ => k.ToString()
        });
        return sb.ToString();
    }

    private static string KeyPresetId(string? s) =>
        s != null && KeyPresets.ContainsKey(s) ? s : "Quill";

    private static string KeyPresetLabel(string id) =>
        Loc.T(KeyPresetOptions.FirstOrDefault(o => o.Id == id).LocKey ?? "KeyPreset.Quill");

    // Rebuilds the live accelerators and the F1 sheet from the saved preset.
    // Safe to call at any time — switching presets is just another call.
    private void ApplyKeyPreset()
    {
        string id = KeyPresetId(_library.KeyPreset);
        _library.KeyPreset = id;

        RootGrid.KeyboardAccelerators.Clear();
        var rows = new List<(string Combo, string Label)>();
        var dropped = new List<string>();
        var taken = new Dictionary<(VKey, VMod), string>();

        var commands = KeyCommands;
        foreach (var b in KeyPresets[id])
        {
            var cmd = commands.FirstOrDefault(c => c.Id == b.Id);
            if (cmd.Run == null) continue;
            string combo = ComboText(b.Key, b.Mods);

            if (IsReservedCombo(b.Key, b.Mods))
            {
                dropped.Add($"{cmd.Label} — {combo} belongs to Windows, so it is unbound");
                continue;
            }
            // First entry wins; the later one loses and says so on the F1 sheet
            // rather than installing a second accelerator that never runs.
            if (taken.TryGetValue((b.Key, b.Mods), out string? owner))
            {
                dropped.Add($"{cmd.Label} — {combo} already runs {owner}, so it is unbound");
                continue;
            }

            taken[(b.Key, b.Mods)] = cmd.Label;
            var acc = new KeyboardAccelerator { Key = b.Key, Modifiers = b.Mods };
            acc.Invoked += cmd.Run;
            RootGrid.KeyboardAccelerators.Add(acc);
            rows.Add((combo, cmd.Label));
        }

        BuildShortcutsSheet(id, rows, dropped);
    }

    private void BuildShortcutsSheet(string id, List<(string Combo, string Label)> rows, List<string> dropped)
    {
        ShortcutsPresetLine.Text = $"{KeyPresetLabel(id)} layout — change it in Settings";
        ShortcutsKeyRows.Children.Clear();

        // Foreground is deliberately left unset: these rows are rebuilt only on a
        // preset change, so a captured brush would survive a theme flip stale.
        foreach (var (combo, label) in rows)
        {
            var tb = new TextBlock { FontSize = 13 };
            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = combo, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "   " + label });
            ShortcutsKeyRows.Children.Add(tb);
        }
        foreach (var d in dropped)
            ShortcutsKeyRows.Children.Add(new TextBlock
            {
                Text = "! " + d,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
    }

    // Search entry point on the gallery (#19-batch2) — reuses the one search flyout.
    private void GallerySearch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SearchBtn.Flyout?.ShowAt(GallerySearchBtn);
            SearchBox.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    // Save button on the notebook gallery (#21-batch2): export the whole notebook,
    // one section, or one page — built as a menu from the browsed notebook.
    private void GallerySave_Click(object sender, RoutedEventArgs e)
    {
        var nb = _galleryNb;
        if (nb == null) return;
        var menu = new MenuFlyout();

        var whole = new MenuFlyoutItem { Text = $"Whole notebook “{nb.Name}” (PDF)" };
        whole.Click += (_, _) => ExportFromGallery(nb, null, null);
        menu.Items.Add(whole);
        menu.Items.Add(new MenuFlyoutSeparator());

        foreach (var sec in nb.Sections)
        {
            var s = sec;
            var sub = new MenuFlyoutSubItem { Text = $"Section “{s.Name}”" };
            var secPdf = new MenuFlyoutItem { Text = "Whole section (PDF)" };
            secPdf.Click += (_, _) => ExportFromGallery(nb, s, null);
            sub.Items.Add(secPdf);
            if (s.Pages.Count > 0) sub.Items.Add(new MenuFlyoutSeparator());
            foreach (var pg in s.Pages)
            {
                var p = pg;
                var pageItem = new MenuFlyoutItem { Text = $"Page “{p.Name}” (PDF)" };
                pageItem.Click += (_, _) => ExportFromGallery(nb, s, p);
                sub.Items.Add(pageItem);
            }
            menu.Items.Add(sub);
        }
        menu.ShowAt(GallerySaveBtn);
    }

    // Navigates to the chosen scope, then drives the existing export pipeline.
    private void ExportFromGallery(Notebook nb, Section? sec, NotePage? page)
    {
        var targetSec = sec ?? nb.Sections.FirstOrDefault();
        var targetPage = page ?? targetSec?.Pages.FirstOrDefault();
        if (targetSec == null || targetPage == null) { ShowStatus("Nothing to export — this notebook has no pages."); return; }
        SwitchToPage(nb, targetSec, targetPage);
        if (page != null) ExportVectorPdf_Click(GallerySaveBtn, new RoutedEventArgs());
        else if (sec != null) ExportSectionVectorPdf_Click(GallerySaveBtn, new RoutedEventArgs());
        else ExportNotebookVectorPdf_Click(GallerySaveBtn, new RoutedEventArgs());
    }

    private void SearchAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            if (_uiHidden || SearchBtn.Flyout == null) { args.Handled = false; return; }
            // the search button lives in the notebook panel now (#19-batch2);
            // anchor the flyout to whichever search entry point is on screen.
            FrameworkElement anchor =
                GalleryPanel.Visibility == Visibility.Visible ? GallerySearchBtn :
                NotebookPanel.Visibility == Visibility.Visible ? SearchBtn : TopBar;
            SearchBtn.Flyout.ShowAt(anchor);
            SearchBox.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
        catch { args.Handled = false; }
    }

    // =======================================================================
    // Text formatting
    // =======================================================================
    private void UpdateFormatBarVisibility()
    {
        bool show = !_uiHidden && (Surface.Tool == ToolType.Text || Surface.ActiveTextBox != null);
        if (show) FadeIn(FormatBar, 150, pop: false, slideY: -14); else FadeOut(FormatBar, 120);
    }

    private ITextSelection? Sel()
    {
        var box = Surface.ActiveTextBox;
        if (box == null)
        {
            ShowStatus("Pick the Text tool and tap a text box first.");
            return null;
        }
        return box.Document.Selection;
    }

    private void FormatBold_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Bold = FormatEffect.Toggle;
    }

    private void FormatItalic_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Italic = FormatEffect.Toggle;
    }

    private void FormatUnderline_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        s.CharacterFormat.Underline = s.CharacterFormat.Underline == UnderlineType.Single
            ? UnderlineType.None
            : UnderlineType.Single;
    }

    private void FormatStrike_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Strikethrough = FormatEffect.Toggle;
    }

    private void FormatSuper_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Superscript = FormatEffect.Toggle;
    }

    private void FormatSub_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Subscript = FormatEffect.Toggle;
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) s.CharacterFormat.ForegroundColor = args.NewColor;
    }

    private void HighlightPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) s.CharacterFormat.BackgroundColor = args.NewColor;
    }

    private void ClearHighlight_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.BackgroundColor = Colors.Transparent;
    }

    private void FormatBullets_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        var pf = s.ParagraphFormat;
        pf.ListType = pf.ListType == MarkerType.Bullet ? MarkerType.None : MarkerType.Bullet;
    }

    private void FormatIndent_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        try
        {
            var pf = s.ParagraphFormat;
            pf.SetIndents(pf.FirstLineIndent, pf.LeftIndent + 24, pf.RightIndent);
        }
        catch { }
    }

    private void FormatOutdent_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        try
        {
            var pf = s.ParagraphFormat;
            pf.SetIndents(pf.FirstLineIndent, Math.Max(0, pf.LeftIndent - 24), pf.RightIndent);
        }
        catch { }
    }

    // Checklist: cycle the current paragraph through none -> ☐ -> ☑ -> none (#22).
    private void FormatChecklist_Click(object sender, RoutedEventArgs e)
    {
        var box = Surface.ActiveTextBox;
        if (box == null) { ShowStatus("Tap a text box first, then add a checklist."); return; }
        try
        {
            var sel = box.Document.Selection;
            var para = box.Document.GetRange(sel.StartPosition, sel.EndPosition);
            para.Expand(TextRangeUnit.Paragraph);
            para.GetText(TextGetOptions.None, out string txt);
            string body = txt.TrimEnd('\r');
            string tail = txt.Substring(body.Length);
            string next;
            if (body.StartsWith("☐ ")) next = "☑ " + body.Substring(2);
            else if (body.StartsWith("☑ ")) next = body.Substring(2);
            else next = "☐ " + body;
            para.SetText(TextSetOptions.None, next + tail);
        }
        catch { ShowStatus("Couldn't update the checklist here."); }
        box.Focus(FocusState.Programmatic);
    }

    // Add a hyperlink to the selected text (#24).
    private async void FormatLink_Click(object sender, RoutedEventArgs e)
    {
        var box = Surface.ActiveTextBox;
        if (box == null) { ShowStatus("Select some text in a text box first."); return; }
        var sel = box.Document.Selection;
        if (sel.StartPosition == sel.EndPosition)
        {
            ShowStatus("Select the text you want to turn into a link first.");
            return;
        }
        var url = await PromptAsync("Add link", "https://");
        if (url == null) return;
        if (!url.Contains("://")) url = "https://" + url;
        try { sel.Link = "\"" + url + "\""; }
        catch { ShowStatus("Couldn't add that link."); return; }
        box.Focus(FocusState.Programmatic);
        ShowStatus("Link added. Ctrl+click it to open.");
    }

    private void RotateText_Click(object sender, RoutedEventArgs e) => Surface.RotateActiveText(15);

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Left);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Center);
    private void AlignRight_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Right);
    private void AlignJustify_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Justify);

    private void SetAlignment(ParagraphAlignment alignment)
    {
        var s = Sel();
        if (s != null) s.ParagraphFormat.Alignment = alignment;
    }

    private void FontCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string font) return;
        Surface.PendingFontFamily = font; // also becomes the default for new boxes this session
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null)
        {
            s.CharacterFormat.Name = font;
            Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
        }
    }

    private static float? ParseSize(string? s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= 400
            ? v : (float?)null;

    private void SizeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSize) return; // ignore programmatic sync (prevents cross-box bleed)
        if (SizeCombo.SelectedItem is string s && ParseSize(s) is float v) ApplyFontSize(v);
    }

    // Typed-in size (e.g. "37" + Enter) — makes the size fully typeable (#1).
    private void SizeCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (ParseSize(args.Text) is float v)
        {
            ApplyFontSize(v);
            args.Handled = true;
        }
    }

    // Applies a size to the active text box's selection if there is one, and
    // always records it as the session default for new boxes / new typing (#2, #8).
    private void ApplyFontSize(float size)
    {
        Surface.PendingFontSize = size;
        var box = Surface.ActiveTextBox;
        if (box != null)
        {
            try { box.Document.Selection.CharacterFormat.Size = size; } catch { }
            box.Focus(FocusState.Programmatic);
        }
    }

    // Reflect the focused box's actual size in the combo so a later change is
    // intentional and never carries a stale value onto another box (#14).
    private void SyncSizeComboFromSelection(RichEditBox? box)
    {
        if (box == null) return;
        try
        {
            float sz = box.Document.Selection.CharacterFormat.Size;
            if (sz > 0)
            {
                _syncingSize = true;
                SizeCombo.Text = sz % 1 == 0 ? ((int)sz).ToString() : sz.ToString("0.#");
            }
        }
        catch { }
        finally { _syncingSize = false; }
    }

    private void Symbol_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string symbol) return;
        var box = Surface.ActiveTextBox;
        if (box == null)
        {
            ShowStatus("Pick the Text tool, tap a text box, then insert symbols.");
            return;
        }
        box.Document.Selection.TypeText(symbol);
        box.Focus(FocusState.Programmatic);
    }

    // =======================================================================
    // Text styles (brand typography)
    // =======================================================================
    private record StyleDef(string Font, float Size, int Weight, bool Italic, Color? Color);

    // Shared definitions used both to render the dropdown (each name shown in its
    // own style, #26) and to apply the style to selected text.
    private static readonly (string Name, StyleDef Def)[] StyleDefs =
    {
        ("Normal",     new StyleDef("Lora", 12, 400, false, null)),
        ("Heading 1",  new StyleDef("Poppins", 22, 700, false, null)),
        ("Heading 2",  new StyleDef("Poppins", 18, 700, false, null)),
        ("Heading 3",  new StyleDef("Poppins", 16, 600, false, null)),
        ("Heading 4",  new StyleDef("Poppins", 14, 600, true, null)),
        ("Heading 5",  new StyleDef("Poppins", 13, 600, false, null)),
        ("Heading 6",  new StyleDef("Poppins", 12, 600, false, Color.FromArgb(255, 138, 136, 127))),
        ("Page Title", new StyleDef("Poppins", 28, 700, false, null)),
        ("Citation",   new StyleDef("Lora", 10.5f, 400, true, Color.FromArgb(255, 138, 136, 127))),
        ("Quote",      new StyleDef("Lora", 13, 400, true, Color.FromArgb(255, 106, 104, 98))),
        ("Code",       new StyleDef("Consolas", 11.5f, 400, false, null)),
    };

    private void BuildStyleItems()
    {
        StyleCombo.Items.Clear();
        foreach (var (name, d) in StyleDefs)
        {
            float display = Math.Clamp(d.Size, 11f, 19f); // keep the list compact
            var tb = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily(d.Font),
                FontSize = display,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)d.Weight },
                FontStyle = d.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
            };
            if (d.Color is Color c) tb.Foreground = new SolidColorBrush(c);
            StyleCombo.Items.Add(new ComboBoxItem { Content = tb, Tag = name, MinHeight = 0 });
        }
    }

    private void BuildFontItems()
    {
        FontCombo.Items.Clear();
        foreach (var f in Fonts)
        {
            FontCombo.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = f, FontFamily = new FontFamily(f) },
                Tag = f,
                MinHeight = 0
            });
        }
    }

    private void StyleCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StyleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;
        var def = StyleDefs.FirstOrDefault(x => x.Name == style).Def;
        if (def == null) return;
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s == null) return;
        var cf = s.CharacterFormat;
        cf.Name = def.Font;
        cf.Size = def.Size;
        cf.Weight = def.Weight;
        cf.Italic = def.Italic ? FormatEffect.On : FormatEffect.Off;
        if (def.Color is Color c) cf.ForegroundColor = c;
        Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
    }

    private void StyleChip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string style)
        {
            var def = StyleDefs.FirstOrDefault(x => x.Name == style).Def;
            if (def == null) return;
            var s = Surface.ActiveTextBox?.Document.Selection;
            if (s == null) return;
            var cf = s.CharacterFormat;
            cf.Name = def.Font;
            cf.Size = def.Size;
            cf.Weight = def.Weight;
            cf.Italic = def.Italic ? FormatEffect.On : FormatEffect.Off;
            if (def.Color is Color c) cf.ForegroundColor = c;
            Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
        }
    }

    // =======================================================================
    // Canvas context menu (right-click / pen barrel-tap / touch long-press)
    // =======================================================================
    // =======================================================================
    // Phase 2: tables, typed equations, multi-page & vector PDF, touch mode
    // =======================================================================
    private async void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        var rows = new NumberBox { Header = "Rows", Value = 3, Minimum = 1, Maximum = 20, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var cols = new NumberBox { Header = "Columns", Value = 3, Minimum = 1, Maximum = 12, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var panel = new StackPanel { Spacing = 10, Width = 260 };
        panel.Children.Add(rows);
        panel.Children.Add(cols);
        panel.Children.Add(new TextBlock
        {
            Text = "Cells are text boxes — click one with the Text tool to type. Lines can be moved with the Move mouse mode.",
            FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap
        });
        var dlg = new ContentDialog
        {
            Title = "Insert table",
            Content = panel,
            PrimaryButtonText = "Insert",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        int r = double.IsNaN(rows.Value) ? 3 : (int)rows.Value;
        int c = double.IsNaN(cols.Value) ? 3 : (int)cols.Value;
        // cells sized from the default text size, like a fresh Word table (#49)
        double fs = _library.DefaultFontSize;
        Surface.InsertTable(r, c, Math.Max(120, fs * 11), Math.Max(44, fs * 2.4 + 14));
        SelectTool("Text");   // writing-first: tap any cell and type straight away
        ShowStatus("Table inserted — tap a cell to type. Use the + buttons or right-click for rows and columns; drag a grid line to resize.");
    }

    private const string EquationHtml = """
<!DOCTYPE html><html><head><meta charset="utf-8">
<script defer src="https://cdn.jsdelivr.net/npm/mathlive"></script>
<style>
 body{background:#ffffff;margin:12px;font-family:sans-serif}
 math-field{font-size:26px;display:block;border:1px solid #bbb;border-radius:6px;padding:8px;min-height:44px}
 #out{margin-top:18px;display:inline-block;padding:10px;background:#ffffff}
 #hint{color:#777;font-size:12px;margin-top:6px}
</style></head><body>
<math-field id="mf">x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}</math-field>
<div id="hint">Type maths above (LaTeX shortcuts work: \frac, \sqrt, ^, _ ...). The preview below is what gets inserted.</div>
<div id="out"></div>
<script>
const mf=document.getElementById('mf'),out=document.getElementById('out');
function render(){out.innerHTML='';const m=document.createElement('math-field');m.setAttribute('read-only','');m.style.border='none';m.style.fontSize='34px';m.value=mf.value;out.appendChild(m);}
window.addEventListener('load',()=>{mf.addEventListener('input',render);setTimeout(render,400);});
function getFormulaRect(){const r=out.getBoundingClientRect();return JSON.stringify({x:r.x,y:r.y,w:r.width,h:r.height,s:window.devicePixelRatio||1});}
</script></body></html>
""";

    private async void InsertEquation_Click(object sender, RoutedEventArgs e)
        => await InsertOrEditEquationAsync(null);

    // Insert a new equation, or reopen an existing one with its LaTeX already
    // in the editor so the user tweaks instead of retyping (#27-batch2).
    // Offline LaTeX prompt with a live preview rendered by the built-in
    // equation renderer — no internet, no WebView2 required (#roughedge).
    private async Task<string?> PromptLatexWithPreviewAsync(string initial)
    {
        var box = new TextBox { Text = initial, AcceptsReturn = false };
        var img = new Image { MaxHeight = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        var err = new TextBlock { FontSize = 11, Opacity = 0.7, Visibility = Visibility.Collapsed, Text = "Could not preview — the equation may still insert fine." };
        var panel = new StackPanel { Spacing = 4, Width = 420 };
        panel.Children.Add(box);
        panel.Children.Add(img);
        panel.Children.Add(err);

        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        async void Render()
        {
            try
            {
                var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                var color = ColorUtil.Parse(ContrastHexForPage());
                var bytes = await Quill.Services.EquationRenderer.RenderToPngBytesAsync(device, box.Text, 28f, "Cambria Math", color);
                if (bytes == null) { err.Visibility = Visibility.Visible; return; }
                using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var wtr = new Windows.Storage.Streams.DataWriter(ms.GetOutputStreamAt(0)))
                { wtr.WriteBytes(bytes); await wtr.StoreAsync(); }
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bmp.SetSourceAsync(ms);
                img.Source = bmp;
                err.Visibility = Visibility.Collapsed;
            }
            catch { err.Visibility = Visibility.Visible; }
        }
        debounce.Tick += (_, _) => { debounce.Stop(); Render(); };
        box.TextChanged += (_, _) => { debounce.Stop(); debounce.Start(); };
        Render();

        var dlg = new ContentDialog
        {
            Title = "Equation (LaTeX)",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        var res = await dlg.ShowAsync();
        return res == ContentDialogResult.Primary ? box.Text : null;
    }

    private async Task InsertOrEditEquationAsync(ShapeElement? existing)
    {
        string? latex = null;
        string? initial = existing?.EquationLatex;
        byte[]? webShot = null;
        double[]? webRect = null;
        WebView2? web = null;
        try
        {
            web = new WebView2 { Width = 640, Height = 360 };
            var dlg = new ContentDialog
            {
                Title = existing == null ? "Insert equation" : "Edit equation",
                Content = web,
                PrimaryButtonText = existing == null ? "Insert" : "Update",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot
            };
            bool webReady = false;
            dlg.Opened += async (_, _) =>
            {
                try
                {
                    await web.EnsureCoreWebView2Async();
                    web.NavigateToString(EquationHtml);
                    webReady = true;
                    // tint the preview to match the page, so the captured image
                    // blends seamlessly instead of arriving as a white card
                    try
                    {
                        string bgHex = _curPage?.Background ?? "#FFFFFF";
                        string inkHex = ContrastHexForPage();
                        await web.CoreWebView2.ExecuteScriptAsync(
                            "document.body.style.background='" + bgHex + "';" +
                            "var o=document.getElementById('out');" +
                            "o.style.background='" + bgHex + "';o.style.color='" + inkHex + "';");
                    }
                    catch { }
                    if (!string.IsNullOrEmpty(initial))
                    {
                        // MathLive registers <math-field> asynchronously — poll
                        // until setValue exists, then prefill the existing LaTeX
                        string js = "window.__init=" + System.Text.Json.JsonSerializer.Serialize(initial) +
                                    ";(function f(){var m=document.getElementById('mf');" +
                                    "if(m&&m.setValue){m.setValue(window.__init);}else{setTimeout(f,120);}})();";
                        await web.CoreWebView2.ExecuteScriptAsync(js);
                    }
                }
                catch { /* fall through to the plain LaTeX prompt below */ }
            };
            dlg.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    if (webReady && web.CoreWebView2 != null)
                    {
                        string raw = await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('mf').value");
                        latex = System.Text.Json.JsonSerializer.Deserialize<string>(raw);
                        // capture MathLive's own preview: pixel-perfect layout,
                        // no re-rendering bugs (#14-batch3). Must happen while
                        // the WebView is still on screen.
                        try
                        {
                            string rectRaw = await web.CoreWebView2.ExecuteScriptAsync("getFormulaRect()");
                            var rectJson = System.Text.Json.JsonSerializer.Deserialize<string>(rectRaw);
                            if (!string.IsNullOrEmpty(rectJson))
                            {
                                using var doc = System.Text.Json.JsonDocument.Parse(rectJson);
                                var r = doc.RootElement;
                                webRect = new[]
                                {
                                    r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(),
                                    r.GetProperty("w").GetDouble(), r.GetProperty("h").GetDouble(),
                                    r.GetProperty("s").GetDouble()
                                };
                                using var shotStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                                await web.CoreWebView2.CapturePreviewAsync(
                                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, shotStream);
                                webShot = new byte[shotStream.Size];
                                using var reader = new Windows.Storage.Streams.DataReader(shotStream.GetInputStreamAt(0));
                                await reader.LoadAsync((uint)shotStream.Size);
                                reader.ReadBytes(webShot);
                            }
                        }
                        catch { webShot = null; }
                    }
                }
                catch { }
                finally { deferral.Complete(); }
            };
            var res = await dlg.ShowAsync();
            if (res != ContentDialogResult.Primary) return;
        }
        catch { /* WebView2 runtime missing */ }
        finally { try { web?.Close(); } catch { } }

        // offline / no-WebView2 fallback: type LaTeX with a LIVE preview from
        // the built-in renderer (#roughedge — offline used to be blind)
        latex ??= await PromptLatexWithPreviewAsync(initial ?? @"x=\frac{-b\pm\sqrt{b^2-4ac}}{2a}");
        if (string.IsNullOrWhiteSpace(latex)) return;

        // Preferred path: crop the MathLive screenshot to the formula (#14-batch3)
        try
        {
            if (webShot != null && webRect is { Length: 5 } && webRect[2] > 4 && webRect[3] > 4)
            {
                var device0 = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                using var msIn = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(msIn.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(webShot);
                    await writer.StoreAsync();
                }
                using var full = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device0, msIn);
                double s = Math.Max(0.5, webRect[4]);
                double pad = 6 * s;
                double px = Math.Max(0, webRect[0] * s - pad);
                double py = Math.Max(0, webRect[1] * s - pad);
                double pw = Math.Min(full.SizeInPixels.Width - px, webRect[2] * s + 2 * pad);
                double ph = Math.Min(full.SizeInPixels.Height - py, webRect[3] * s + 2 * pad);
                if (pw > 8 && ph > 8)
                {
                    using var rt = new Microsoft.Graphics.Canvas.CanvasRenderTarget(device0, (float)pw, (float)ph, 96);
                    using (var ds0 = rt.CreateDrawingSession())
                        ds0.DrawImage(full, new Rect(0, 0, pw, ph), new Rect(px, py, pw, ph));
                    // page-coloured pixels become transparent, so the equation
                    // floats over grids, ruling and images instead of sitting
                    // on an opaque card (#17-batch4)
                    try
                    {
                        var bgc = ColorUtil.Parse(_curPage?.Background ?? "#FFFFFF");
                        var pix = rt.GetPixelColors();
                        for (int i = 0; i < pix.Length; i++)
                        {
                            int d = Math.Abs(pix[i].R - bgc.R) + Math.Abs(pix[i].G - bgc.G) + Math.Abs(pix[i].B - bgc.B);
                            if (d < 24) pix[i] = Microsoft.UI.Colors.Transparent;
                        }
                        rt.SetPixelColors(pix);
                    }
                    catch { }
                    var dir0 = System.IO.Path.Combine(LibraryStore.Dir, "images");
                    Directory.CreateDirectory(dir0);
                    var shotPath = System.IO.Path.Combine(dir0, $"{Guid.NewGuid()}.png");
                    using (var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
                    {
                        await rt.SaveAsync(outStream, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
                        var outBytes = new byte[outStream.Size];
                        using var r2 = new Windows.Storage.Streams.DataReader(outStream.GetInputStreamAt(0));
                        await r2.LoadAsync((uint)outStream.Size);
                        r2.ReadBytes(outBytes);
                        await System.IO.File.WriteAllBytesAsync(shotPath, outBytes);
                    }
                    if (existing != null)
                        Surface.UpdateEquationImage(existing, shotPath, pw / s, ph / s, latex);
                    else
                        Surface.InsertImage(shotPath, pw / s, ph / s, latex);
                    ShowStatus(existing == null
                        ? "Equation inserted — right-click it any time to edit."
                        : "Equation updated.");
                    return;
                }
            }
        }
        catch { /* fall back to the built-in renderer below */ }

        try
        {
            var dir = System.IO.Path.Combine(LibraryStore.Dir, "images");
            Directory.CreateDirectory(dir);
            var filename = $"{Guid.NewGuid()}.png";
            var filePath = System.IO.Path.Combine(dir, filename);

            var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
            var color = ColorUtil.Parse(ContrastHexForPage());
            var bytes = await Quill.Services.EquationRenderer.RenderToPngBytesAsync(device, latex, 36f, "Cambria Math", color);
            if (bytes != null)
            {
                await System.IO.File.WriteAllBytesAsync(filePath, bytes);
                using (var bmp = await Microsoft.Graphics.Canvas.CanvasBitmap.LoadAsync(device, filePath))
                {
                    if (existing != null)
                        Surface.UpdateEquationImage(existing, filePath, bmp.Size.Width, bmp.Size.Height, latex);
                    else
                        Surface.InsertImage(filePath, bmp.Size.Width, bmp.Size.Height, latex);
                }
                ShowStatus(existing == null
                    ? "Equation inserted — right-click it any time to edit."
                    : "Equation updated.");
                return;
            }
        }
        catch { }

        if (existing != null) { ShowStatus("Could not re-render the equation."); return; }
        var unicode = LatexToUnicode(latex);
        var centre = Surface.ScreenToWorld(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2));
        Surface.AddTextElement(centre.X - 160, centre.Y - 20, 360,
            PlainToRtf(unicode, "Cambria Math", 20f, ContrastHexForPage()));
        ShowStatus("Equation inserted as text.");
    }

    // ---- LaTeX → Unicode maths text (#37): keeps equations editable ----
    private static readonly Dictionary<string, string> LatexSymbols = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε",
        ["zeta"] = "ζ", ["eta"] = "η", ["theta"] = "θ", ["iota"] = "ι", ["kappa"] = "κ",
        ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν", ["xi"] = "ξ", ["pi"] = "π",
        ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["upsilon"] = "υ", ["phi"] = "φ",
        ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ",
        ["Pi"] = "Π", ["Sigma"] = "Σ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·",
        ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥", ["ne"] = "≠", ["neq"] = "≠",
        ["approx"] = "≈", ["equiv"] = "≡", ["propto"] = "∝", ["infty"] = "∞",
        ["sum"] = "∑", ["prod"] = "∏", ["int"] = "∫", ["iint"] = "∬", ["oint"] = "∮",
        ["partial"] = "∂", ["nabla"] = "∇", ["to"] = "→", ["rightarrow"] = "→",
        ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["Leftrightarrow"] = "⇔",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆",
        ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅", ["forall"] = "∀", ["exists"] = "∃",
        ["land"] = "∧", ["lor"] = "∨", ["neg"] = "¬", ["angle"] = "∠", ["degree"] = "°",
        ["therefore"] = "∴", ["because"] = "∵", ["ldots"] = "…", ["cdots"] = "⋯",
        ["prime"] = "′", ["hbar"] = "ℏ", ["ell"] = "ℓ",
        ["left"] = "", ["right"] = "", ["!"] = "", [","] = " ", [";"] = " ", ["quad"] = "  ", ["qquad"] = "    "
    };

    private static readonly Dictionary<char, char> SuperMap = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵',
        ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹', ['+'] = '⁺', ['-'] = '⁻',
        ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ'
    };

    private static readonly Dictionary<char, char> SubMap = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅',
        ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉', ['+'] = '₊', ['-'] = '₋',
        ['='] = '₌', ['('] = '₍', [')'] = '₎', ['a'] = 'ₐ', ['e'] = 'ₑ', ['i'] = 'ᵢ',
        ['n'] = 'ₙ', ['m'] = 'ₘ', ['x'] = 'ₓ', ['k'] = 'ₖ', ['t'] = 'ₜ'
    };

    /// <summary>Best-effort LaTeX → Unicode plain-text maths. Handles \frac,
    /// \sqrt, super/subscripts, Greek letters and common operators; unknown
    /// commands are kept as-is so nothing is silently lost.</summary>
    private static string LatexToUnicode(string latex)
    {
        // grab the {...} group starting at index i (which must point at '{')
        static string Group(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '{') { var ch = i < s.Length ? s[i++].ToString() : ""; return ch; }
            int depth = 0, start = ++i;
            for (; i < s.Length; i++)
            {
                if (s[i] == '{') depth++;
                else if (s[i] == '}')
                {
                    if (depth == 0) { var inner = s[start..i]; i++; return inner; }
                    depth--;
                }
            }
            return s[start..];
        }

        static string MapScript(string s, Dictionary<char, char> map, string fallbackPrefix)
        {
            if (s.Length > 0 && s.All(map.ContainsKey))
                return new string(s.Select(c => map[c]).ToArray());
            return fallbackPrefix + "(" + s + ")";
        }

        var output = new System.Text.StringBuilder();
        int p = 0;
        while (p < latex.Length)
        {
            char c = latex[p];
            if (c == '\\')
            {
                p++;
                int cmdStart = p;
                while (p < latex.Length && char.IsLetter(latex[p])) p++;
                string cmd = latex[cmdStart..p];
                if (cmd.Length == 0 && p < latex.Length) { cmd = latex[p].ToString(); p++; }

                switch (cmd)
                {
                    case "frac":
                    {
                        string a = LatexToUnicode(Group(latex, ref p));
                        string b = LatexToUnicode(Group(latex, ref p));
                        bool simpleA = a.Length <= 2, simpleB = b.Length <= 2;
                        output.Append(simpleA ? a : "(" + a + ")").Append('/')
                              .Append(simpleB ? b : "(" + b + ")");
                        break;
                    }
                    case "sqrt":
                    {
                        if (p < latex.Length && latex[p] == '[')
                        {
                            int close = latex.IndexOf(']', p);
                            if (close > 0) { output.Append(LatexToUnicode(latex[(p + 1)..close])).Append('√'); p = close + 1; }
                        }
                        else output.Append('√');
                        string inner = LatexToUnicode(Group(latex, ref p));
                        output.Append('(').Append(inner).Append(')');
                        break;
                    }
                    case "text":
                    case "mathrm":
                    case "operatorname":
                        output.Append(Group(latex, ref p));
                        break;
                    default:
                        output.Append(LatexSymbols.TryGetValue(cmd, out var sym) ? sym : cmd);
                        break;
                }
            }
            else if (c == '^')
            {
                p++;
                output.Append(MapScript(LatexToUnicode(Group(latex, ref p)), SuperMap, "^"));
            }
            else if (c == '_')
            {
                p++;
                output.Append(MapScript(LatexToUnicode(Group(latex, ref p)), SubMap, "_"));
            }
            else if (c is '{' or '}')
            {
                p++;
            }
            else
            {
                output.Append(c);
                p++;
            }
        }
        return output.ToString();
    }

    // ---- PDF import: render each page to an image and ink over it (#53) ----
    private async void ImportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (_curNb == null) { ShowStatus("Open a notebook first."); return; }
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            ShowStatus("Importing PDF…");
            var doc = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
            // effectively uncapped, with a guardrail against pathological files
            // (pages rasterise to page-backdrop images; text stays non-selectable)
            int count = (int)Math.Min(doc.PageCount, 2000);
            var dir = System.IO.Path.Combine(LibraryStore.Dir, "assets");
            Directory.CreateDirectory(dir);

            var textBlock = new TextBlock { Text = $"Processing page 1 of {count}...", HorizontalAlignment = HorizontalAlignment.Center };
            var progressBar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = count, Value = 0, Width = 300, Margin = new Thickness(0, 10, 0, 0) };
            var panel = new StackPanel { Spacing = 10, Padding = new Thickness(10) };
            panel.Children.Add(textBlock);
            panel.Children.Add(progressBar);

            var progressDlg = new ContentDialog
            {
                Title = "Importing PDF",
                Content = panel,
                XamlRoot = RootGrid.XamlRoot
            };

            var dlgTask = progressDlg.ShowAsync();

            var sec = new Section { Name = file.DisplayName };

            await Task.Run(async () =>
            {
                for (int i = 0; i < count; i++)
                {
                    using var pdfPage = doc.GetPage((uint)i);
                    double w = pdfPage.Size.Width, h = pdfPage.Size.Height;
                    var path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.png");
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
                    {
                        await pdfPage.RenderToStreamAsync(fs.AsRandomAccessStream(),
                            new Windows.Data.Pdf.PdfPageRenderOptions { DestinationWidth = (uint)Math.Clamp(w * 2, 600, 3200) });
                    }

                    var idx = i;
                    var pagePath = path;
                    var pageW = w;
                    var pageH = h;

                    var tcs = new TaskCompletionSource<bool>();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            var page = NewPage($"Page {idx + 1}");
                            page.Background = "#FFFFFF";
                            const double scale = 1.4;
                            page.Shapes.Add(new ShapeElement
                            {
                                Kind = ShapeKind.Image, ImagePath = pagePath,
                                X = 44, Y = 104, W = pageW * scale, H = pageH * scale
                            });
                            sec.Pages.Add(page);

                            progressBar.Value = idx + 1;
                            textBlock.Text = $"Processing page {idx + 1} of {count}...";
                            tcs.SetResult(true);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });
                    await tcs.Task;
                }
            });

            progressDlg.Hide();

            _curNb.Sections.Add(sec);
            BuildTree();
            ScheduleSave();
            if (sec.Pages.Count > 0) SwitchToPage(_curNb, sec, sec.Pages[0]);
            ShowStatus($"Imported {sec.Pages.Count} PDF pages into “{sec.Name}” — ink over them freely.");
        }
        catch
        {
            ShowStatus("Couldn't import that PDF.");
        }
    }

    // ---- image exports (#53) ----
    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static async Task SavePngAsync(string path, byte[] bgra, int w, int h)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)w, (uint)h, 96, 96, bgra);
        await enc.FlushAsync();
    }

    private async void CopyPageImage_Click(object sender, RoutedEventArgs e)
    {
        Surface.FlushTexts();
        var cap = await CapturePageAsync();
        if (cap == null) return;
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)cap.Value.Width, (uint)cap.Value.Height, 96, 96, cap.Value.Pixels);
            await enc.FlushAsync();
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            ShowStatus("Page copied to the clipboard as an image.");
        }
        catch
        {
            ShowStatus("Couldn't copy the page.");
        }
    }

    private async void ExportSectionPngs_Click(object sender, RoutedEventArgs e)
    {
        if (_curSec == null || _curNb == null || _curPage == null) return;
        var folder = await PickFolderAsync();
        if (folder == null) return;
        Surface.FlushTexts();
        var keep = (_curNb, _curSec, _curPage);
        int n = 0;
        _suppressPageFade = true;
        try
        {
            var pages = _curSec.Pages.ToList();
            foreach (var pg in pages)
            {
                var (nb, sec) = FindContext(pg);
                if (nb == null || sec == null) continue;
                SwitchToPage(nb, sec, pg);
                ShowStatus($"Exporting image {n + 1} of {pages.Count}…");
                var cap = await CapturePageAsync();
                if (cap == null) continue;
                await SavePngAsync(System.IO.Path.Combine(folder, $"{++n:00} - {Sanitize(pg.Name)}.png"),
                    cap.Value.Pixels, cap.Value.Width, cap.Value.Height);
            }
        }
        finally
        {
            SwitchToPage(keep.Item1, keep.Item2, keep.Item3);
            _suppressPageFade = false;
        }
        ShowStatus($"Exported {n} PNG{(n == 1 ? "" : "s")}.");
    }

    private async void ExportNotebookMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_curNb == null) return;
        await ExportMarkdownAsync(_curNb.Name, _curNb.Sections);
    }

    private async void ExportSectionMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (_curNb == null || _curSec == null) return;
        await ExportMarkdownAsync($"{_curNb.Name} — {_curSec.Name}", new List<Section> { _curSec });
    }

    // Markdown + page-image export for any scope: the whole notebook or one
    // section (#24-batch2 extended the notebook-only export to sections).
    private async Task ExportMarkdownAsync(string title, IReadOnlyList<Section> sections)
    {
        if (_curNb == null || _curSec == null || _curPage == null) return;
        var folder = await PickFolderAsync();
        if (folder == null) return;
        Surface.FlushTexts();
        var keep = (_curNb, _curSec, _curPage);
        var nbDir = System.IO.Path.Combine(folder, Sanitize(title));
        var imgDir = System.IO.Path.Combine(nbDir, "images");
        Directory.CreateDirectory(imgDir);
        var md = new System.Text.StringBuilder();
        md.AppendLine($"# {title}");
        int n = 0;
        _suppressPageFade = true;
        try
        {
            foreach (var sec in sections)
            {
                md.AppendLine().AppendLine($"## {sec.Name}");
                foreach (var pg in sec.Pages)
                {
                    SwitchToPage(_curNb, sec, pg);
                    ShowStatus($"Exporting page {++n}…");
                    var cap = await CapturePageAsync();
                    string img = $"images/{n:00}-{Sanitize(pg.Name)}.png";
                    if (cap != null)
                        await SavePngAsync(System.IO.Path.Combine(imgDir, $"{n:00}-{Sanitize(pg.Name)}.png"),
                            cap.Value.Pixels, cap.Value.Width, cap.Value.Height);
                    md.AppendLine().AppendLine($"### {pg.Name}");
                    md.AppendLine($"![{pg.Name}]({img})");
                    var texts = string.Join("\n\n",
                        pg.Texts.Select(t => RtfToText(t.Rtf)).Where(t => !string.IsNullOrWhiteSpace(t)));
                    if (!string.IsNullOrWhiteSpace(texts)) md.AppendLine().AppendLine(texts.Trim());
                    if (!string.IsNullOrWhiteSpace(pg.OcrText)) md.AppendLine().AppendLine("> " + pg.OcrText.Trim());
                }
            }
            File.WriteAllText(System.IO.Path.Combine(nbDir, $"{Sanitize(title)}.md"), md.ToString());
        }
        finally
        {
            SwitchToPage(keep.Item1, keep.Item2, keep.Item3);
            _suppressPageFade = false;
        }
        ShowStatus($"Exported {n} page{(n == 1 ? "" : "s")} as Markdown with images.");
    }

    // ---- vector PDF export (the raster PDF path was retired in #45) ----
    private bool _suppressPageFade;

    private async void ExportVectorPdf_Click(object sender, RoutedEventArgs e)
        => await ExportVectorAsync(_curPage != null ? new List<NotePage> { _curPage } : new List<NotePage>());

    private async void ExportSectionVectorPdf_Click(object sender, RoutedEventArgs e)
        => await ExportVectorAsync(_curSec?.Pages.ToList() ?? new List<NotePage>());

    private async void ExportNotebookVectorPdf_Click(object sender, RoutedEventArgs e)
        => await ExportVectorAsync(_curNb?.Sections.SelectMany(s => s.Pages).ToList() ?? new List<NotePage>());

    private async Task ExportVectorAsync(List<NotePage> pages)
    {
        if (pages.Count == 0 || _curNb == null || _curSec == null || _curPage == null)
        {
            ShowStatus("Nothing to export.");
            return;
        }
        var file = await PickSaveFileAsync(".pdf", "PDF document");
        if (file == null) return;

        var vpages = await CollectVectorPagesAsync(pages);
        if (vpages.Count == 0) { ShowStatus("Nothing to export."); return; }
        try
        {
            await FileIO.WriteBytesAsync(file, PdfExporter.CreateVector(vpages));
            ShowStatus($"Exported {vpages.Count} vector page{(vpages.Count == 1 ? "" : "s")} to {file.Name} — ink stays crisp, text stays selectable.");
        }
        catch
        {
            ShowStatus("Could not save the PDF. Check the location and try again.");
        }
    }

    // Walks the given pages through the live surface and flattens each into the
    // vector page model shared by the PDF, SVG and HTML exporters (#24-batch2).
    private async Task<List<PdfVectorPage>> CollectVectorPagesAsync(List<NotePage> pages)
    {
        Surface.FlushTexts();
        var (keepNb, keepSec, keepPage) = (_curNb!, _curSec!, _curPage!);
        var vpages = new List<PdfVectorPage>();
        _suppressPageFade = true;
        try
        {
            foreach (var pg in pages)
            {
                var (nb, sec) = FindContext(pg);
                if (nb == null || sec == null) continue;
                SwitchToPage(nb, sec, pg);
                ShowStatus($"Exporting page {vpages.Count + 1} of {pages.Count}…");
                var vp = await Surface.BuildVectorPageAsync(28);
                if (vp != null) vpages.Add(vp);
            }
        }
        finally
        {
            SwitchToPage(keepNb, keepSec, keepPage);
            _suppressPageFade = false;
        }
        return vpages;
    }

    // ---- SVG: the honest "vector PNG" (#18-batch2). PNG is raster by
    //      definition; SVG is the vector image format, so that's what we offer. ----
    private async void ExportSvg_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || _curNb == null || _curSec == null) { ShowStatus("Nothing to export."); return; }
        var file = await PickSaveFileAsync(".svg", "SVG vector image");
        if (file == null) return;
        var vpages = await CollectVectorPagesAsync(new List<NotePage> { _curPage });
        if (vpages.Count == 0) { ShowStatus("Nothing to export."); return; }
        try
        {
            await FileIO.WriteTextAsync(file, HtmlSvgExporter.PageToSvg(vpages[0]));
            ShowStatus($"Exported {file.Name} — a true vector image, crisp at any zoom.");
        }
        catch { ShowStatus("Could not save the SVG. Check the location and try again."); }
    }

    // ---- HTML with vector pages, for every scope (#24-batch2) ----
    private async void ExportHtml_Click(object sender, RoutedEventArgs e)
        => await ExportHtmlAsync(_curPage != null ? new List<NotePage> { _curPage } : new List<NotePage>(), _curPage?.Name ?? "Page");

    private async void ExportSectionHtml_Click(object sender, RoutedEventArgs e)
        => await ExportHtmlAsync(_curSec?.Pages.ToList() ?? new List<NotePage>(), _curSec?.Name ?? "Section");

    private async void ExportNotebookHtml_Click(object sender, RoutedEventArgs e)
        => await ExportHtmlAsync(_curNb?.Sections.SelectMany(s => s.Pages).ToList() ?? new List<NotePage>(), _curNb?.Name ?? "Notebook");

    private async Task ExportHtmlAsync(List<NotePage> pages, string title)
    {
        if (pages.Count == 0 || _curNb == null || _curSec == null || _curPage == null)
        {
            ShowStatus("Nothing to export.");
            return;
        }
        var file = await PickSaveFileAsync(".html", "HTML document");
        if (file == null) return;
        var vpages = await CollectVectorPagesAsync(pages);
        if (vpages.Count == 0) { ShowStatus("Nothing to export."); return; }
        try
        {
            await FileIO.WriteTextAsync(file, HtmlSvgExporter.BuildHtml(title, vpages));
            ShowStatus($"Exported {vpages.Count} vector page{(vpages.Count == 1 ? "" : "s")} to {file.Name} — opens in any browser.");
        }
        catch { ShowStatus("Could not save the HTML. Check the location and try again."); }
    }

    // Stage 1 collaboration (#collab): fold other devices' ops into the local
    // library; refresh whatever the merge touched.
    private readonly DispatcherTimer _syncTimer = new();
    private void RunForeignMerge()
    {
        try
        {
            var changed = SyncLog.MergeForeign(_library);
            if (changed.Count == 0) return;
            BuildTree();
            if (GalleryPanel.Visibility == Visibility.Visible) BuildGallery();
            if (_curPage != null && changed.Contains(_curPage.Id))
            {
                Surface.FlushTexts();
                Surface.LoadPage(_curPage);   // re-render the merged content
            }
            ScheduleSave();                    // persist the merged state
            ShowStatus("Synced changes from another device.");
        }
        catch { }
    }

    // ---- toolbar visibility (#topbar) ------------------------------------
    // Two independent filters: the user's own show/hide choices, and context
    // (pen-only buttons are pointless while the text or lasso tool is active).
    // Element names only; the checkbox labels live in Strings as "Tool.<name>".
    private static readonly string[] OptionalTools =
    {
        "ToolSpace", "TouchDrawToggle", "ToolComment", "ShapeBtn", "MouseModeBtn",
        "BtnHistory", "VoiceBtn", "BtnAi", "ZoomBtn", "PageSettingsBtn", "ExportBtn", "BtnCalc",
    };

    private void ApplyToolbarVisibility()
    {
        bool pen = _toolTag == "Pen";
        void Set(FrameworkElement? el, string key, bool inContext = true)
        {
            if (el == null) return;
            bool on = !_library.HiddenTools.Contains(key) && inContext;
            el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        try
        {
            Set(ToolSpace, "ToolSpace");
            Set(TouchDrawToggle, "TouchDrawToggle", pen);
            Set(ToolComment, "ToolComment");
            Set(ShapeBtn, "ShapeBtn", pen);
            Set(MouseModeBtn, "MouseModeBtn", pen);
            Set(BtnHistory, "BtnHistory");
            Set(VoiceBtn, "VoiceBtn");
            Set(BtnAi, "BtnAi");
            Set(ZoomBtn, "ZoomBtn");
            Set(PageSettingsBtn, "PageSettingsBtn");
            Set(ExportBtn, "ExportBtn");
            Set(BtnCalc, "BtnCalc");
        }
        catch { }
    }

    private void ShortcutsMenu_Click(object sender, RoutedEventArgs e) => ToggleShortcutsPanel();

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 6, Width = 320 };
        panel.Children.Add(new TextBlock { Text = "Quill", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Pen-first lecture notes.", Opacity = 0.75 });
        panel.Children.Add(new TextBlock
        {
            Text = $"Notebooks are stored in {LibraryStore.Dir}",
            FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });
        await new ContentDialog
        {
            Title = "About",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        }.ShowAsync();
    }

    // Pushes the real monitor width (in DIPs, DPI-corrected) to the surface so
    // new text boxes can cap their growth at half the physical screen (#15).
    private void UpdateScreenMetrics()
    {
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            double scale = RootGrid?.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            Surface.ScreenWidthDip = area.OuterBounds.Width / scale;
        }
        catch { }
    }

    // ---- touch-screen mode (#36): larger tap targets on every toolbar ----
    private void ApplyTouchMode(bool on)
    {
        void Walk(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var ch = VisualTreeHelper.GetChild(root, i);
                switch (ch)
                {
                    case Microsoft.UI.Xaml.Controls.Primitives.ButtonBase bb:
                        bb.MinWidth = on ? 44 : 0;
                        bb.MinHeight = on ? 42 : 0;
                        break;
                    case ComboBox cb:
                        cb.MinHeight = on ? 42 : 0;
                        break;
                    case Slider sl:
                        sl.MinHeight = on ? 42 : 0;
                        break;
                }
                Walk(ch);
            }
        }
        // reverted to the original coverage on request — touch mode behaves
        // exactly as it did before the batch-10 changes, PLUS the three buttons
        // that used to be missed (they sit outside these roots):
        foreach (var root in new FrameworkElement[] { TopBarScroll, FormatBarScroll, PenRow, MinimalButtons, CalcPanel, NotebookPanel })
            try { Walk(root); } catch { }
        // the minimal-UI and full-screen buttons live in the pinned strip, not
        // the scrolling top bar; the gallery search button sits beside New
        // notebook / Close, which must stay normal size — enlarge only these
        // three so they match the rest of touch mode (#touch)
        try { Walk(TopBarPinned); } catch { }
        try { GallerySearchBtn.MinWidth = on ? 44 : 0; GallerySearchBtn.MinHeight = on ? 42 : 0; } catch { }
    }


    // =======================================================================
    // AI assistant (#25-batch2): summaries, action items, smart tags, Q&A and
    // a writing assistant over the current page, via the provider configured
    // in Settings (Claude / OpenAI / Gemini / local OpenAI-compatible server).
    // =======================================================================
    private string PageContextText()
    {
        if (_curPage == null) return "";
        Surface.FlushTexts();
        var texts = string.Join("\n\n",
            _curPage.Texts.Select(t => RtfToText(t.Rtf)).Where(s => !string.IsNullOrWhiteSpace(s)));
        var ocr = string.IsNullOrWhiteSpace(_curPage.OcrText) ? "" : "\n\n[Handwriting]: " + _curPage.OcrText.Trim();
        return (texts + ocr).Trim();
    }

    private async Task<string?> RunAiAsync(string system, string user)
    {
        if (_library.AiProvider is null or "" or "None")
        {
            ShowStatus("Pick an AI provider in Settings first.");
            return null;
        }
        var key = AiService.GetKey(_library.AiProvider);
        if (string.IsNullOrEmpty(key) && _library.AiProvider != "Local")
        {
            ShowStatus("Add your API key in Settings — AI assistant.");
            return null;
        }
        ShowStatus("Asking the AI…");
        try
        {
            var result = await AiService.CompleteAsync(_library.AiProvider, _library.AiModel, _library.AiEndpoint, key, system, user);
            ShowStatus("Done.");
            return result;
        }
        catch (Exception ex)
        {
            ShowStatus("AI request failed: " + ex.Message);
            return null;
        }
    }

    private async Task ShowAiResultAsync(string title, string text)
    {
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var scroller = new ScrollViewer { Content = body, MaxHeight = 420, MaxWidth = 520 };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = scroller,
            PrimaryButtonText = "Insert onto page",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && _curPage != null)
        {
            var centre = Surface.ScreenToWorld(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2));
            var (df, ds2) = EffectiveTextDefaults();
            Surface.AddTextElement(centre.X - 180, centre.Y - 40, 400,
                PlainToRtf(text, df, ds2, ContrastHexForPage()));
        }
    }

    // =======================================================================
    // AI chat window (#22-batch3): a floating conversation with history that
    // can SEE the page — each message optionally attaches a PNG snapshot of
    // the canvas (ink included), not an OCR/text approximation.
    // =======================================================================
    private readonly List<(string Role, string Text)> _aiChat = new();
    private bool _aiBusy;

    private void AiChat_Click(object sender, RoutedEventArgs e)
    {
        if (AiPanel.Visibility == Visibility.Visible) { FadeOut(AiPanel); return; }
        FadeIn(AiPanel, slideX: 18);
        RebuildAiMessages();
        AiInput.Focus(FocusState.Programmatic);
    }

    private void AiClose_Click(object sender, RoutedEventArgs e) => FadeOut(AiPanel);

    private void AiClear_Click(object sender, RoutedEventArgs e)
    {
        _aiChat.Clear();
        RebuildAiMessages();
    }

    private void AiInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            _ = SendAiChatAsync();
        }
    }

    private void AiSend_Click(object sender, RoutedEventArgs e) => _ = SendAiChatAsync();

    private void AddAiBubble(string role, string text)
    {
        bool user = role == "user";
        var accent = ColorUtil.Parse(_library.AccentColor);
        var bubble = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            MaxWidth = 300,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = user
                ? new SolidColorBrush(Color.FromArgb(60, accent.R, accent.G, accent.B))
                : new SolidColorBrush(Color.FromArgb(28, 128, 128, 128)),
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                IsTextSelectionEnabled = true
                // Foreground inherits the theme ink
            }
        };
        AiMessages.Children.Add(bubble);
    }

    private void RebuildAiMessages()
    {
        AiMessages.Children.Clear();
        if (_aiChat.Count == 0)
            AiMessages.Children.Add(new TextBlock
            {
                Text = "Ask anything about this page. With the eye toggled on, the AI sees your actual ink and drawings.",
                FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap
            });
        foreach (var (role, text) in _aiChat) AddAiBubble(role, text);
        AiScroll.UpdateLayout();
        AiScroll.ChangeView(null, AiScroll.ScrollableHeight, null, true);
    }

    private async Task SendAiChatAsync()
    {
        if (_aiBusy) return;
        var q = AiInput.Text.Trim();
        if (q.Length == 0) return;
        if (_library.AiProvider is null or "" or "None")
        { ShowStatus("Pick an AI provider in Settings first."); return; }
        var key = AiService.GetKey(_library.AiProvider);
        if (string.IsNullOrEmpty(key) && _library.AiProvider != "Local")
        { ShowStatus("Add your API key in Settings first."); return; }

        AiInput.Text = "";
        if (_aiPendingText.Length > 0) { q += _aiPendingText; _aiPendingText = ""; }
        _aiChat.Add(("user", q));
        RebuildAiMessages();
        AddAiBubble("assistant", "thinking…");
        AiScroll.UpdateLayout();
        AiScroll.ChangeView(null, AiScroll.ScrollableHeight, null, true);

        var atts = new List<AiService.AiAttachment>();
        if (AiSeePage.IsChecked == true)
        {
            try
            {
                if (_library.AiProvider == "Local")
                {
                    // local vision models take images, not PDFs
                    var cap = await CapturePageAsync();
                    if (cap != null)
                        atts.Add(new AiService.AiAttachment(
                            MiniPng.FromBgra(cap.Value.Pixels, cap.Value.Width, cap.Value.Height),
                            "image/png", "page.png"));
                }
                else if (_curPage != null)
                {
                    // the page as a vector PDF: real geometry and selectable
                    // text instead of a flat screenshot (#9-batch4)
                    var vp = await CollectVectorPagesAsync(new List<NotePage> { _curPage });
                    if (vp.Count > 0)
                        atts.Add(new AiService.AiAttachment(PdfExporter.CreateVector(vp), "application/pdf", "page.pdf"));
                }
            }
            catch { }
        }
        atts.AddRange(_aiPendingFiles);   // user-attached files ride along (#11-batch4)

        _aiBusy = true;
        try
        {
            var history = _aiChat.Count > 12 ? _aiChat.GetRange(_aiChat.Count - 12, 12) : _aiChat;
            var reply = await AiService.ChatAsync(
                _library.AiProvider, _library.AiModel, _library.AiEndpoint, key,
                AiSystem + " You may be shown the user's page as an attached PDF or image, plus files they attach - read them directly.",
                history, atts);
            _aiChat.Add(("assistant", reply.Trim()));
        }
        catch (Exception ex)
        {
            _aiChat.Add(("assistant", "[request failed] " + ex.Message));
        }
        finally
        {
            _aiBusy = false;
            _aiPendingFiles.Clear();
            _aiPendingText = "";
            UpdateAiAttachChip();
            RebuildAiMessages();
        }
    }

    // ---- user file attachments for the AI chat (#11-batch4) ----
    private readonly List<AiService.AiAttachment> _aiPendingFiles = new();
    private string _aiPendingText = "";
    private readonly List<string> _aiAttachNames = new();

    private async void AiAttach_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf", ".txt", ".md" })
                picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file == null) return;
            var ext2 = System.IO.Path.GetExtension(file.Path).ToLowerInvariant();
            if (ext2 is ".txt" or ".md")
            {
                // plain text rides along inside the message itself
                var txt = await System.IO.File.ReadAllTextAsync(file.Path);
                if (txt.Length > 60000) txt = txt[..60000];
                _aiPendingText += "\n\n[Attached file " + file.Name + "]\n" + txt;
            }
            else
            {
                string mime = ext2 switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    ".pdf" => "application/pdf",
                    _ => "image/png"
                };
                _aiPendingFiles.Add(new AiService.AiAttachment(
                    await System.IO.File.ReadAllBytesAsync(file.Path), mime, file.Name));
            }
            _aiAttachNames.Add(file.Name);
            UpdateAiAttachChip();
        }
        catch (Exception ex) { ShowStatus("Could not attach: " + ex.Message); }
    }

    private void UpdateAiAttachChip()
    {
        if (_aiPendingFiles.Count == 0 && _aiPendingText.Length == 0) _aiAttachNames.Clear();
        AiAttachChip.Text = _aiAttachNames.Count == 0 ? "" : "Attached: " + string.Join(", ", _aiAttachNames);
        AiAttachChip.Visibility = _aiAttachNames.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private const string AiSystem = "You are a study assistant inside Quill, a pen-first lecture notes app. Be concise and concrete. Use plain text (no markdown syntax).";

    private async void AiSummarize_Click(object sender, RoutedEventArgs e)
    {
        var ctx = PageContextText();
        if (ctx.Length == 0) { ShowStatus("This page has no text to summarise (handwriting needs OCR first: lasso it and convert)."); return; }
        var r = await RunAiAsync(AiSystem, "Summarise these lecture notes in a short paragraph followed by 3-6 bullet points:\n\n" + ctx);
        if (r != null) await ShowAiResultAsync("Summary", r);
    }

    private async void AiActions_Click(object sender, RoutedEventArgs e)
    {
        var ctx = PageContextText();
        if (ctx.Length == 0) { ShowStatus("This page has no text to scan for action items."); return; }
        var r = await RunAiAsync(AiSystem, "List every action item, task, deadline or follow-up implied by these notes, one per line starting with '- ':\n\n" + ctx);
        if (r != null) await ShowAiResultAsync("Action items", r);
    }

    private async void AiTags_Click(object sender, RoutedEventArgs e)
    {
        var ctx = PageContextText();
        if (ctx.Length == 0) { ShowStatus("This page has no text to tag."); return; }
        var r = await RunAiAsync(AiSystem, "Suggest 3-8 short topic tags for these notes, comma-separated, lowercase:\n\n" + ctx);
        if (r != null) await ShowAiResultAsync("Smart tags", r);
    }

    private async void AiAsk_Click(object sender, RoutedEventArgs e)
    {
        var q = await PromptAsync("Ask about this page", "");
        if (string.IsNullOrWhiteSpace(q)) return;
        var ctx = PageContextText();
        var r = await RunAiAsync(AiSystem, "Notes:\n" + ctx + "\n\nQuestion: " + q + "\nAnswer using the notes where possible.");
        if (r != null) await ShowAiResultAsync(q, r);
    }

    private async void AiImprove_Click(object sender, RoutedEventArgs e)
    {
        // opening the flyout steals focus before the click lands, so the box
        // is no longer "active" — fall back to the last focused box, whose
        // document still remembers its selection (#12-batch4)
        var box = Surface.ActiveTextBox ?? Surface.LastTextBox;
        if (box == null || !HasTextSelection(box))
        {
            ShowStatus("Select some text in a text box first.");
            return;
        }
        box.Document.Selection.GetText(Microsoft.UI.Text.TextGetOptions.None, out string selected);
        if (string.IsNullOrWhiteSpace(selected)) { ShowStatus("Select some text in a text box first."); return; }
        var r = await RunAiAsync(AiSystem, "Rewrite this text to be clearer and better written, keeping its meaning and language. Return ONLY the rewritten text:\n\n" + selected);
        if (r != null)
        {
            try { box.Document.Selection.TypeText(r.Trim()); ShowStatus("Rewritten."); }
            catch { await ShowAiResultAsync("Rewritten text", r); }
        }
    }

    // Resolves a [[Note Name]] to a page by name across every notebook and
    // opens it; ambiguity opens the first match and says how many exist (#31-batch2).
    private void OpenLinkedPage(string name)
    {
        var matches = new List<(Notebook nb, Section sec, NotePage pg)>();
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                foreach (var pg in sec.Pages)
                    if (string.Equals(pg.Name, name, StringComparison.OrdinalIgnoreCase))
                        matches.Add((nb, sec, pg));
        if (matches.Count == 0)
        {
            ShowStatus($"No page named “{name}” — create one and the link will work.");
            return;
        }
        var (n0, s0, p0) = matches[0];
        SwitchToPage(n0, s0, p0);
        if (GalleryPanel.Visibility == Visibility.Visible) CloseGallery();
        ShowStatus(matches.Count == 1
            ? $"Opened “{p0.Name}”."
            : $"{matches.Count} pages are named “{name}” — opened the one in “{n0.Name}”.");
    }

    // =======================================================================
    // Command palette (Ctrl+K, #30-batch2): keyboard-driven hub to jump to any
    // page or run any common action without touching the mouse.
    // =======================================================================
    private sealed record PaletteCmd(string Label, Action Run);

    private void PaletteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        _ = ShowCommandPaletteAsync();
    }

    private async Task ShowCommandPaletteAsync()
    {
        var cmds = new List<PaletteCmd>();
        void Add(string label, Action run) => cmds.Add(new PaletteCmd(label, run));

        Add("New page", () => AddPage_Click(this, new RoutedEventArgs()));
        Add("New section", () => AddSection_Click(this, new RoutedEventArgs()));
        Add("New notebook", () => AddNotebook_Click(this, new RoutedEventArgs()));
        Add("Notebook gallery", () => ShowGallery(launcher: false));
        Add("Settings", () => Settings_Click(this, new RoutedEventArgs()));
        Add("Toggle light / dark theme", () => Theme_Click(this, new RoutedEventArgs()));
        Add("Toggle full screen", () => Fullscreen_Click(this, new RoutedEventArgs()));
        Add("Minimal UI (hide all panels)", () => HideUi_Click(this, new RoutedEventArgs()));
        Add("Undo", () => Surface.Undo());
        Add("Redo", () => Surface.Redo());
        Add("Zoom to fit", () => ZoomFit_Click(this, new RoutedEventArgs()));
        Add("Reset zoom to 100%", () => ZoomReset_Click(this, new RoutedEventArgs()));
        Add("Export page as PDF", () => ExportVectorPdf_Click(this, new RoutedEventArgs()));
        Add("Export page as PNG", () => ExportPng_Click(this, new RoutedEventArgs()));
        Add("Export page as SVG (vector)", () => ExportSvg_Click(this, new RoutedEventArgs()));
        Add("Export page as HTML (vector)", () => ExportHtml_Click(this, new RoutedEventArgs()));
        Add("Import PDF as section", () => ImportPdf_Click(this, new RoutedEventArgs()));
        Add("Search all notes", () =>
        {
            try
            {
                FrameworkElement anchorEl =
                    GalleryPanel.Visibility == Visibility.Visible ? GallerySearchBtn :
                    NotebookPanel.Visibility == Visibility.Visible ? SearchBtn : TopBar;
                SearchBtn.Flyout?.ShowAt(anchorEl);
                SearchBox.Focus(FocusState.Programmatic);
            }
            catch { }
        });
        Add("Tool: Pen", () => SelectTool("Pen"));
        Add("Tool: Text", () => SelectTool("Text"));
        Add("Tool: Lasso select", () => SelectTool("Select"));
        Add("Tool: Insert free space", () => SelectTool("FreeSpace"));
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                foreach (var pg in sec.Pages)
                {
                    var (n0, s0, p0) = (nb, sec, pg);
                    Add($"Open: {n0.Name} ▸ {s0.Name} ▸ {p0.Name}", () =>
                    {
                        SwitchToPage(n0, s0, p0);
                        if (GalleryPanel.Visibility == Visibility.Visible) CloseGallery();
                    });
                }

        var box = new TextBox { PlaceholderText = "Type a command or page name…" };
        var list = new ListView { MaxHeight = 320, IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };
        void Refill()
        {
            var words = box.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = cmds.Where(c => words.All(w => c.Label.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(12).ToList();
            list.Items.Clear();
            foreach (var c in hits)
                list.Items.Add(new ListViewItem { Content = new TextBlock { Text = c.Label, TextTrimming = TextTrimming.CharacterEllipsis }, Tag = c });
            if (list.Items.Count > 0) list.SelectedIndex = 0;
        }
        Refill();
        box.TextChanged += (_, _) => Refill();

        var panel = new StackPanel { Width = 480, Spacing = 8 };
        panel.Children.Add(box);
        panel.Children.Add(list);
        var dlg = new ContentDialog { Title = "Command palette", Content = panel, CloseButtonText = "Cancel", XamlRoot = RootGrid.XamlRoot };

        void Run(object? item)
        {
            if (item is ListViewItem lvi && lvi.Tag is PaletteCmd c)
            {
                dlg.Hide();
                try { c.Run(); } catch { }
            }
        }
        list.ItemClick += (_, e) => Run(e.ClickedItem);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            { Run(list.SelectedItem ?? (list.Items.Count > 0 ? list.Items[0] : null)); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Down && list.Items.Count > 0)
            { list.SelectedIndex = Math.Min(list.SelectedIndex + 1, list.Items.Count - 1); e.Handled = true; }
            else if (e.Key == Windows.System.VirtualKey.Up && list.SelectedIndex > 0)
            { list.SelectedIndex--; e.Handled = true; }
        };
        dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        await dlg.ShowAsync();
    }

    // Voice-to-text (#26-batch2): Windows' built-in dictation engine.
    private async void Dictate_Click(object sender, RoutedEventArgs e)
    {
        if (BtnDictate.IsChecked == true)
        {
            ShowStatus("Starting dictation…");
            bool ok = await _dictation.StartAsync();
            if (!ok)
            {
                BtnDictate.IsChecked = false;
                ShowStatus("Dictation couldn't start: " + (_dictation.LastError ?? "unknown error."));
                return;
            }
            ShowStatus("Dictating — speak, and the text lands in the focused text box (or a new one).");
        }
        else
        {
            await _dictation.StopAsync();
            ShowStatus("Dictation stopped.");
        }
    }

    private void ShowCanvasContextMenu(Point pos)
    {
        var menu = new MenuFlyout();
        var box = Surface.ActiveTextBox;
        bool textSel = box != null && box.FocusState != FocusState.Unfocused &&
                       HasTextSelection(box);
        var world = Surface.ScreenToWorld(pos);

        var copy = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "" } };
        copy.IsEnabled = textSel || Surface.HasCanvasSelection;
        copy.Click += (_, _) => ContextCopy();
        menu.Items.Add(copy);

        var cut = new MenuFlyoutItem { Text = "Cut", Icon = new FontIcon { Glyph = "" } };
        cut.IsEnabled = textSel || Surface.HasCanvasSelection;
        cut.Click += (_, _) => ContextCut();
        menu.Items.Add(cut);

        var paste = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "" } };
        paste.Click += (_, _) => ContextPaste(world);
        menu.Items.Add(paste);

        if (box != null && box.FocusState != FocusState.Unfocused)
        {
            var pastePlain = new MenuFlyoutItem { Text = "Paste as plain text" };
            pastePlain.Click += (_, _) => PastePlainText();
            menu.Items.Add(pastePlain);
        }
        // right-clicked a typed equation -> reopen it in the editor with its
        // LaTeX already loaded (#27-batch2)
        var eqShape = Surface.EquationShapeAt(world);
        if (eqShape != null)
        {
            var editEq = new MenuFlyoutItem { Text = "Edit equation…" };
            editEq.Click += async (_, _) => await InsertOrEditEquationAsync(eqShape);
            menu.Items.Add(editEq);
        }

        // [[Note Name]] links (#31-batch2): right-clicking a text box that
        // contains link syntax offers to jump to each linked page
        foreach (var linkName in Surface.TextLinksAt(world))
        {
            var ln = linkName;
            var go = new MenuFlyoutItem { Text = $"Open [[{ln}]]" };
            go.Click += (_, _) => OpenLinkedPage(ln);
            menu.Items.Add(go);
        }

        // right-clicked an x-y / x-y-z axes shape -> rename its axis labels (#28-batch2)
        var axShape = Surface.AxesShapeAt(world);
        if (axShape != null)
        {
            var editAx = new MenuFlyoutItem { Text = "Axis labels…" };
            editAx.Click += async (_, _) => await EditAxisLabelsAsync(axShape);
            menu.Items.Add(editAx);
        }

        if (Surface.HasCanvasSelection)
        {
            var del = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
            del.Click += (_, _) => Surface.DeleteSelection();
            menu.Items.Add(del);
        }

        // Handwriting → text / maths for lasso-selected ink (#35)
        if (Surface.SelectedStrokes.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var toText = new MenuFlyoutItem { Text = "Convert handwriting to text", Icon = new FontIcon { Glyph = "" } };
            toText.Click += async (_, _) => await ConvertSelectionAsync(math: false);
            menu.Items.Add(toText);
            var toMath = new MenuFlyoutItem { Text = "Convert handwriting to maths (evaluate)", Icon = new FontIcon { Glyph = "" } };
            toMath.Click += async (_, _) => await ConvertSelectionAsync(math: true);
            menu.Items.Add(toMath);
        }

        // Word-like table actions, positional to the clicked cell (#49)
        if (Surface.ActiveShape is { Kind: ShapeKind.Table } table)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var cell = Surface.TableCellAt(table, world);
            void AddItem(string txt, Action act, bool enabled = true)
            {
                var it = new MenuFlyoutItem { Text = txt, IsEnabled = enabled };
                it.Click += (_, _) => act();
                menu.Items.Add(it);
            }
            if (cell is (int cr, int cc))
            {
                AddItem("Add row above", () => Surface.TableInsertRow(table, cr));
                AddItem("Add row below", () => Surface.TableInsertRow(table, cr + 1));
                AddItem("Add column left", () => Surface.TableInsertColumn(table, cc));
                AddItem("Add column right", () => Surface.TableInsertColumn(table, cc + 1));
                menu.Items.Add(new MenuFlyoutSeparator());
                AddItem("Select this row", () => Surface.SelectTableRow(table, cr));
                AddItem("Select this column", () => Surface.SelectTableColumn(table, cc));
                menu.Items.Add(new MenuFlyoutSeparator());
                AddItem("Delete this row", () => Surface.TableDeleteRow(table, cr), table.TRows > 1);
                AddItem("Delete this column", () => Surface.TableDeleteColumn(table, cc), table.TCols > 1);
            }
            else
            {
                AddItem("Add row below", () => Surface.TableInsertRow(table, Math.Max(1, table.TRows)));
                AddItem("Add column right", () => Surface.TableInsertColumn(table, Math.Max(1, table.TCols)));
            }

            var selectedCells = Surface.GetSelectedTableCells(table);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddItem("Toggle header row", () => Surface.TableToggleHeaderRow(table));
            AddItem("Merge selected cells", () => Surface.TableMergeSelectedCells(table), selectedCells.Count > 1);
            AddItem("Split cell", () => Surface.TableSplitSelectedCell(table), selectedCells.Count == 1 && (selectedCells[0].CellColSpan > 1 || selectedCells[0].CellRowSpan > 1));
            AddItem("Cell background…", async () =>
            {
                var picked = await PickColorAsync("Cell background fill", Colors.White);
                if (picked is Color c) Surface.TableSetSelectedCellsFill(table, ColorUtil.ToHex(c));
            }, selectedCells.Count > 0);
            AddItem("Cell border…", async () =>
            {
                var picked = await PickColorAsync("Cell border colour", Colors.Black);
                if (picked is Color c) Surface.TableSetSelectedCellsBorder(table, ColorUtil.ToHex(c), 2.5f);
            }, selectedCells.Count > 0);
        }

        // Word-style formatting when text is selected: style, font, size (punto).
        if (textSel)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var style = new MenuFlyoutSubItem { Text = "Style" };
            AddClick(style, "Bold", FormatBold_Click);
            AddClick(style, "Italic", FormatItalic_Click);
            AddClick(style, "Underline", FormatUnderline_Click);
            AddClick(style, "Strikethrough", FormatStrike_Click);
            AddClick(style, "Superscript", FormatSuper_Click);
            AddClick(style, "Subscript", FormatSub_Click);
            menu.Items.Add(style);

            var font = new MenuFlyoutSubItem { Text = "Font" };
            foreach (var f in Fonts)
            {
                var name = f;
                var it = new MenuFlyoutItem { Text = f };
                it.Click += (_, _) => SetSelectionFont(name);
                font.Items.Add(it);
            }
            menu.Items.Add(font);

            var size = new MenuFlyoutSubItem { Text = "Size (punto)" };
            foreach (var sz in FontSizes)
            {
                var s = sz;
                var it = new MenuFlyoutItem { Text = sz };
                it.Click += (_, _) => SetSelectionSize(s);
                size.Items.Add(it);
            }
            menu.Items.Add(size);
        }

        menu.ShowAt(Surface, new FlyoutShowOptions { Position = pos });
    }

    private static void AddClick(MenuFlyoutSubItem parent, string text, RoutedEventHandler handler)
    {
        var it = new MenuFlyoutItem { Text = text };
        it.Click += handler;
        parent.Items.Add(it);
    }

    private static bool HasTextSelection(RichEditBox box)
    {
        var sel = box.Document.Selection;
        return sel.StartPosition != sel.EndPosition;
    }

    private async void ContextCopy()
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused && HasTextSelection(box))
            box.Document.Selection.Copy();
        else
        {
            Surface.CommitActiveSelection();
            if (Surface.HasCanvasSelection)
            {
                Surface.CopySelection();
                await PushSelectionToClipboardAsync();
                ShowStatus("Copied — right-click where you want to paste.");
            }
        }
    }

    private async void ContextCut()
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused && HasTextSelection(box))
        {
            box.Document.Selection.Cut();
        }
        else
        {
            Surface.CommitActiveSelection();
            if (Surface.HasCanvasSelection)
            {
                Surface.CopySelection();
                await PushSelectionToClipboardAsync();
                Surface.DeleteSelection();
                ShowStatus("Cut — right-click where you want to paste.");
            }
        }
    }

    private async void ContextPaste(System.Numerics.Vector2 world)
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused)
        {
            try { box.Document.Selection.Paste(0); } catch { }
            return;
        }
        bool hasBitmap = false;
        try
        {
            hasBitmap = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
        }
        catch { }
        if (hasBitmap) { await PasteImageAsync(world); return; }
        if (InkSurface.HasCanvasClipboard) { Surface.PasteCanvasAt(world); return; }
        ShowStatus("Nothing to paste.");
    }

    private async Task PushSelectionToClipboardAsync()
    {
        try
        {
            var cap = await Surface.CaptureSelectionAsync();
            if (cap == null) return;
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            enc.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)cap.Value.Width, (uint)cap.Value.Height, 96, 96, cap.Value.Pixels);
            await enc.FlushAsync();
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromStream(stream));
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
        }
        catch { }
    }

    private void SetSelectionFont(string font)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) { s.CharacterFormat.Name = font; Surface.ActiveTextBox?.Focus(FocusState.Programmatic); }
    }

    private void SetSelectionSize(string sizeStr)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null && float.TryParse(sizeStr, out float v))
        {
            s.CharacterFormat.Size = v;
            Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
        }
    }

    // =======================================================================
    // Calculator
    // =======================================================================
    private void Calc_Toggle(object sender, RoutedEventArgs e)
    {
        if (BtnCalc.IsChecked == true)
        {
            FadeIn(CalcPanel);
            CalcInput.Focus(FocusState.Programmatic);
        }
        else FadeOut(CalcPanel);
    }

    private void Calc_Close(object sender, RoutedEventArgs e)
    {
        BtnCalc.IsChecked = false;
        FadeOut(CalcPanel);
    }

    private void CalcMode_Click(object sender, RoutedEventArgs e) => BuildCalcButtons();

    private string CalcModeName => CalcModeBox?.SelectedItem as string ?? "Scientific";

    private void CalcModeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        ApplyCalcMode();
        BuildCalcButtons();
    }

    private void ApplyCalcMode()
    {
        string m = CalcModeName;
        bool buttons = m is "Standard" or "Scientific" or "Programmer";
        CalcHistory.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcInput.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcToggles.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        bool sci = m == "Scientific";
        CalcDeg.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        Calc2nd.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        CalcHyp.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        // the mode hosts share one slot: the outgoing one must collapse on the
        // spot or the panel jumps taller for the length of a fade
        foreach (var (host, wanted) in new (FrameworkElement, bool)[]
                 { (ProgHost, m == "Programmer"), (GraphHost, m == "Graphing"), (ConvHost, m == "Converter") })
        {
            if (!wanted) { host.Visibility = Visibility.Collapsed; host.Opacity = 1; }
            else FadeIn(host, 150, pop: false, slideY: 8);
        }
        CalcError.Text = "";
        if (m == "Graphing") GraphCanvas.Invalidate();
    }

    private int ProgBaseNum => (ProgBase?.SelectedItem as string) switch
    {
        "HEX" => 16,
        "OCT" => 8,
        "BIN" => 2,
        _ => 10
    };

    private void BuildCalcButtons()
    {
        if (CalcButtons == null) return;
        CalcButtons.Children.Clear();
        if (_library.TouchMode)
            DispatcherQueue.TryEnqueue(() => ApplyTouchMode(true));
        if (CalcModeName == "Programmer")
        {
            BuildProgButtons();
            return;
        }
        if (CalcModeName == "Standard")
        {
            BuildButtonRows(new List<(string, string)[]>
            {
                new[] { ("(", "("), (")", ")"), ("C", "C"), ("⌫", "⌫"), ("÷", "÷") },
                new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("√", "√(") },
                new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), ("x²", "^2") },
                new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("1/x", "1/(") },
                new[] { ("+/−", "±"), ("0", "0"), (".", "."), ("=", "=") }
            }, null);
            return;
        }
        bool inv = Calc2nd?.IsChecked == true;
        bool hyp = CalcHyp?.IsChecked == true;

        string Trig(string baseName)
        {
            var n = baseName;
            if (hyp) n += "h";
            if (inv) n = "a" + n;
            return n;
        }
        string TrigLabel(string baseName)
        {
            var n = baseName + (hyp ? "h" : "");
            return inv ? n + "⁻¹" : n;
        }

        var rows = new List<(string Label, string Token)[]>
        {
            new[] { (TrigLabel("sin"), Trig("sin") + "("), (TrigLabel("cos"), Trig("cos") + "("),
                    (TrigLabel("tan"), Trig("tan") + "("), (TrigLabel("sec"), Trig("sec") + "("),
                    (TrigLabel("csc"), Trig("csc") + "("), (TrigLabel("cot"), Trig("cot") + "(") },
            new[] { ("π", "π"), ("e", "e"), ("x²", "^2"), ("xʸ", "^"), ("10ˣ", "10^"), ("√", "√(") },
            new[] { ("log", "log("), ("ln", "ln("), ("eˣ", "exp("), ("|x|", "abs("), ("n!", "!"), ("mod", " mod ") },
            new[] { ("(", "("), (")", ")"), ("C", "C"), ("⌫", "⌫"), ("÷", "÷") },
            new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("1/x", "1/(") },
            new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), ("ʸ√x", "^(1/") },
            new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("=", "=") },
            new[] { ("+/−", "±"), ("0", "0"), (".", ".") }
        };

        BuildButtonRows(rows, null);
    }

    private void BuildButtonRows(List<(string Label, string Token)[]> rows, Func<string, bool>? enabled)
    {
        foreach (var row in rows)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var (label, token) in row)
            {
                var b = new Button
                {
                    Content = label,
                    Tag = token,
                    Width = row.Length >= 6 ? 48 : 58,
                    Height = 32,
                    FontSize = 13,
                    Padding = new Thickness(0)
                };
                if (token == "=") b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                if (enabled != null) b.IsEnabled = enabled(label);
                b.Click += Calc_Click;
                sp.Children.Add(b);
            }
            CalcButtons.Children.Add(sp);
        }
    }

    private void BuildProgButtons()
    {
        int nb = ProgBaseNum;
        bool DigitOk(string d)
        {
            if (d.Length == 1 && d[0] >= 'A' && d[0] <= 'F') return nb == 16;
            if (d.Length == 1 && char.IsDigit(d[0]))
            {
                int v = d[0] - '0';
                return v < nb;
            }
            return true;
        }
        BuildButtonRows(new List<(string, string)[]>
        {
            new[] { ("A", "A"), ("B", "B"), ("C", "C₁"), ("D", "D"), ("E", "E"), ("F", "F") },
            new[] { ("AND", " and "), ("OR", " or "), ("XOR", " xor "), ("NOT", "not("), ("mod", " mod ") },
            new[] { ("(", "("), (")", ")"), ("CE", "C"), ("⌫", "⌫"), ("÷", "÷") },
            new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("<<", "<<") },
            new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), (">>", ">>") },
            new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("=", "=") },
            new[] { ("+/−", "±"), ("0", "0") }
        }, label => label switch
        {
            "AND" or "OR" or "XOR" or "NOT" or "mod" or "(" or ")" or "CE" or "⌫" or "÷" or "×"
                or "<<" or ">>" or "−" or "+" or "=" or "+/−" => true,
            "C" => true,
            _ => DigitOk(label)
        });
    }

    // Physical constants page (#18-batch3): tap to insert at the caret; users
    // can save their own. Values avoid bare e-notation because the calc parser
    // would read "6.02e23" as 6.02*euler*23 — powers of ten are written as 10^n.
    private static readonly (string Name, string Value, string Hint)[] BuiltinConstants =
    {
        ("g",    "9.80665",              "standard gravity (m/s^2)"),
        ("R",    "8.314462618",          "gas constant (J/(mol*K))"),
        ("N_A",  "6.02214076*10^23",     "Avogadro (1/mol)"),
        ("c",    "2.99792458*10^8",      "speed of light (m/s)"),
        ("h",    "6.62607015*10^-34",    "Planck (J*s)"),
        ("k_B",  "1.380649*10^-23",      "Boltzmann (J/K)"),
        ("G",    "6.6743*10^-11",        "gravitation (N*m^2/kg^2)"),
        ("eps0", "8.8541878128*10^-12",  "vacuum permittivity (F/m)"),
        ("m_e",  "9.1093837015*10^-31",  "electron mass (kg)"),
        ("q_e",  "1.602176634*10^-19",   "elementary charge (C)"),
    };

    private void CalcConst_Click(object sender, RoutedEventArgs e)
    {
        void InsertIntoInput(string val)
        {
            int p = Math.Clamp(CalcInput.SelectionStart, 0, CalcInput.Text.Length);
            CalcInput.Text = CalcInput.Text.Insert(p, val);
            CalcInput.SelectionStart = p + val.Length;
            CalcInput.Focus(FocusState.Programmatic);
        }

        var menu = new MenuFlyout();
        foreach (var (name, value, hint) in BuiltinConstants)
        {
            var item = new MenuFlyoutItem { Text = $"{name} = {value}   ({hint})" };
            item.Click += (_, _) => InsertIntoInput(value);
            menu.Items.Add(item);
        }
        if (_library.CalcConstants.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            foreach (var entry in _library.CalcConstants)
            {
                var parts = entry.Split('=', 2);
                if (parts.Length != 2) continue;
                var val = parts[1];
                var item = new MenuFlyoutItem { Text = $"{parts[0]} = {val}" };
                item.Click += (_, _) => InsertIntoInput(val);
                menu.Items.Add(item);
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var add = new MenuFlyoutItem { Text = "Save current input as a constant…" };
        add.Click += async (_, _) =>
        {
            var val = CalcInput.Text.Trim();
            if (val.Length == 0) { ShowStatus("Type the value into the calculator first."); return; }
            var name = await PromptAsync("Constant name", "");
            if (string.IsNullOrWhiteSpace(name)) return;
            _library.CalcConstants.RemoveAll(x => x.StartsWith(name.Trim() + "=", StringComparison.OrdinalIgnoreCase));
            _library.CalcConstants.Add($"{name.Trim()}={val}");
            ScheduleSave();
            ShowStatus($"Saved constant {name.Trim()} = {val}.");
        };
        menu.Items.Add(add);
        if (_library.CalcConstants.Count > 0)
        {
            var removeSub = new MenuFlyoutSubItem { Text = "Remove a constant" };
            foreach (var entry in _library.CalcConstants.ToList())
            {
                var en = entry;
                var rem = new MenuFlyoutItem { Text = en };
                rem.Click += (_, _) => { _library.CalcConstants.Remove(en); ScheduleSave(); };
                removeSub.Items.Add(rem);
            }
            menu.Items.Add(removeSub);
        }
        menu.ShowAt(CalcConstBtn);
    }

    private static string FormatBase(long v, int b)
    {
        if (b == 10) return v.ToString();
        string raw = Convert.ToString(v, b).ToUpperInvariant();
        return raw;
    }

    private void ProgBase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        if (_progHasValue) CalcInput.Text = FormatBase(_progValue, ProgBaseNum);
        BuildCalcButtons();
    }

    private void ProgEvaluate()
    {
        var expr = CalcInput.Text.Trim();
        if (expr.Length == 0) return;
        if (ProgCalc.TryEvaluate(expr, ProgBaseNum, out long v, out string error))
        {
            _progValue = v;
            _progHasValue = true;
            ProgHex.Text = "HEX  " + FormatBase(v, 16);
            ProgDec.Text = "DEC  " + v;
            ProgOct.Text = "OCT  " + FormatBase(v, 8);
            ProgBin.Text = "BIN  " + FormatBase(v, 2);
            var items = CalcHistory.ItemsSource as List<string> ?? new List<string>();
            items.Insert(0, $"{expr} = {FormatBase(v, ProgBaseNum)}");
            if (items.Count > 60) items.RemoveAt(items.Count - 1);
            CalcHistory.ItemsSource = null;
            // The scientific calculator (RefreshCalcHistory) populates Items
            // directly; ItemsSource cannot be assigned while Items is non-empty,
            // so clear it first or WinUI throws InvalidOperationException.
            CalcHistory.Items.Clear();
            CalcHistory.ItemsSource = items;
            CalcInput.Text = FormatBase(v, ProgBaseNum);
        }
        else
        {
            CalcError.Text = error;
        }
    }

    // ---- graphing ----
    private void GraphInput_Changed(object sender, TextChangedEventArgs e) => GraphCanvas.Invalidate();

    private void GraphZoomIn_Click(object sender, RoutedEventArgs e) => GraphSetRange(0.5);
    private void GraphZoomOut_Click(object sender, RoutedEventArgs e) => GraphSetRange(2.0);

    private void GraphReset_Click(object sender, RoutedEventArgs e)
    {
        _gxMin = -10; _gxMax = 10;
        GraphRangeText.Text = "x: −10 … 10";
        GraphCanvas.Invalidate();
    }

    private void GraphSetRange(double factor)
    {
        double c = (_gxMin + _gxMax) / 2, half = (_gxMax - _gxMin) / 2 * factor;
        half = Math.Clamp(half, 0.5, 1e6);
        _gxMin = c - half;
        _gxMax = c + half;
        GraphRangeText.Text = $"x: {_gxMin:0.##} … {_gxMax:0.##}";
        GraphCanvas.Invalidate();
    }

    private void GraphCanvas_Draw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
        Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float W = (float)sender.ActualWidth, H = (float)sender.ActualHeight;
        if (W < 10 || H < 10) return;

        var axis = Color.FromArgb(140, 128, 128, 128);
        var exprs = (GraphInput.Text ?? "")
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).Take(4).ToArray();

        int n = Math.Max(64, (int)W);
        var curves = new List<double?[]>();
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var ex in exprs)
        {
            var ys = new double?[n];
            for (int i = 0; i < n; i++)
            {
                double x = _gxMin + (_gxMax - _gxMin) * i / (n - 1);
                if (CalcEngine.TryEvaluate(ex, false, x, out double y, out _) &&
                    !double.IsNaN(y) && !double.IsInfinity(y) && Math.Abs(y) < 1e9)
                {
                    ys[i] = y;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                }
            }
            curves.Add(ys);
        }
        if (yMin > yMax) { yMin = -10; yMax = 10; }
        if (Math.Abs(yMax - yMin) < 1e-12) { yMin -= 1; yMax += 1; }
        double pad = (yMax - yMin) * 0.08;
        yMin -= pad; yMax += pad;

        float Sx(double x) => (float)((x - _gxMin) / (_gxMax - _gxMin) * W);
        float Sy(double y) => (float)(H - (y - yMin) / (yMax - yMin) * H);

        if (yMin < 0 && yMax > 0)
            ds.DrawLine(0, Sy(0), W, Sy(0), axis, 1f);
        if (_gxMin < 0 && _gxMax > 0)
            ds.DrawLine(Sx(0), 0, Sx(0), H, axis, 1f);

        var palette = new[]
        {
            Color.FromArgb(255, 217, 119, 87),
            Color.FromArgb(255, 106, 155, 204),
            Color.FromArgb(255, 120, 140, 93),
            Color.FromArgb(255, 176, 174, 165)
        };
        for (int c = 0; c < curves.Count; c++)
        {
            var ys = curves[c];
            var col = palette[c % palette.Length];
            for (int i = 1; i < n; i++)
            {
                if (ys[i - 1] is double y0 && ys[i] is double y1)
                {
                    double x0 = _gxMin + (_gxMax - _gxMin) * (i - 1) / (n - 1);
                    double x1 = _gxMin + (_gxMax - _gxMin) * i / (n - 1);
                    ds.DrawLine(Sx(x0), Sy(y0), Sx(x1), Sy(y1), col, 1.8f);
                }
            }
        }
        ds.DrawText($"y: {yMin:0.##} … {yMax:0.##}  (radians)",
            new System.Numerics.Vector2(6, 4), axis, _graphLabelFormat);
    }

    // ---- converter ----
    private static readonly string[] ConvCategories =
        { "Length", "Mass", "Temperature", "Area", "Volume", "Speed", "Time", "Data", "Energy", "Power", "Pressure", "Angle" };

    private static readonly Dictionary<string, (string Unit, double Factor)[]> ConvData = new()
    {
        ["Length"] = new[] { ("millimetre", 0.001), ("centimetre", 0.01), ("metre", 1.0), ("kilometre", 1000.0), ("inch", 0.0254), ("foot", 0.3048), ("yard", 0.9144), ("mile", 1609.344) },
        ["Mass"] = new[] { ("milligram", 1e-6), ("gram", 0.001), ("kilogram", 1.0), ("tonne", 1000.0), ("ounce", 0.0283495), ("pound", 0.453592), ("stone", 6.35029) },
        ["Area"] = new[] { ("cm²", 1e-4), ("m²", 1.0), ("km²", 1e6), ("in²", 0.00064516), ("ft²", 0.092903), ("acre", 4046.8564), ("hectare", 10000.0) },
        ["Volume"] = new[] { ("millilitre", 0.001), ("litre", 1.0), ("m³", 1000.0), ("teaspoon", 0.00492892), ("tablespoon", 0.0147868), ("cup", 0.24), ("pint (US)", 0.473176), ("gallon (US)", 3.78541) },
        ["Speed"] = new[] { ("m/s", 1.0), ("km/h", 0.2777778), ("mph", 0.44704), ("knot", 0.514444) },
        ["Time"] = new[] { ("millisecond", 0.001), ("second", 1.0), ("minute", 60.0), ("hour", 3600.0), ("day", 86400.0), ("week", 604800.0) },
        ["Data"] = new[] { ("bit", 0.125), ("byte", 1.0), ("kB", 1e3), ("MB", 1e6), ("GB", 1e9), ("TB", 1e12), ("KiB", 1024.0), ("MiB", 1048576.0), ("GiB", 1073741824.0) },
        ["Energy"] = new[] { ("joule", 1.0), ("kilojoule", 1000.0), ("calorie", 4.184), ("kcal", 4184.0), ("watt-hour", 3600.0), ("kWh", 3.6e6) },
        ["Power"] = new[] { ("watt", 1.0), ("kilowatt", 1000.0), ("megawatt", 1e6), ("horsepower", 745.7) },
        ["Pressure"] = new[] { ("pascal", 1.0), ("kPa", 1000.0), ("bar", 1e5), ("atm", 101325.0), ("psi", 6894.757), ("mmHg", 133.322) },
        ["Angle"] = new[] { ("degree", 1.0), ("radian", 57.29577951), ("gradian", 0.9), ("turn", 360.0) }
    };

    private static readonly string[] TempUnits = { "Celsius", "Fahrenheit", "Kelvin" };

    private void FillConvUnits()
    {
        if (ConvCat.SelectedItem is not string cat) return;
        string[] units = cat == "Temperature" ? TempUnits : ConvData[cat].Select(u => u.Unit).ToArray();
        ConvFrom.ItemsSource = units;
        ConvTo.ItemsSource = units;
        ConvFrom.SelectedIndex = 0;
        ConvTo.SelectedIndex = Math.Min(1, units.Length - 1);
    }

    private void ConvCat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        FillConvUnits();
        DoConvert();
    }

    private void Conv_Changed(object sender, object e)
    {
        if (!_calcReady) return;
        DoConvert();
    }

    private void ConvSwap_Click(object sender, RoutedEventArgs e)
    {
        (ConvFrom.SelectedIndex, ConvTo.SelectedIndex) = (ConvTo.SelectedIndex, ConvFrom.SelectedIndex);
        DoConvert();
    }

    private void DoConvert()
    {
        if (ConvCat.SelectedItem is not string cat ||
            ConvFrom.SelectedItem is not string fu ||
            ConvTo.SelectedItem is not string tu)
        {
            return;
        }
        var raw = ConvInput.Text.Trim();
        if (raw.Length == 0) { ConvResult.Text = ""; return; }
        if (!CalcEngine.TryEvaluate(raw, true, out double v, out _))
        {
            ConvResult.Text = "…";
            return;
        }

        double result;
        if (cat == "Temperature")
        {
            double c = fu switch
            {
                "Fahrenheit" => (v - 32) * 5 / 9,
                "Kelvin" => v - 273.15,
                _ => v
            };
            result = tu switch
            {
                "Fahrenheit" => c * 9 / 5 + 32,
                "Kelvin" => c + 273.15,
                _ => c
            };
        }
        else
        {
            var units = ConvData[cat];
            double f1 = units.First(u => u.Unit == fu).Factor;
            double f2 = units.First(u => u.Unit == tu).Factor;
            result = v * f1 / f2;
        }
        ConvResult.Text = $"= {result:G10} {tu}";
    }

    private async Task RenamePageFromTitleAsync()
    {
        if (_curPage == null) return;
        var name = await PromptAsync("Rename page", _curPage.Name);
        if (name == null) return;
        _curPage.Name = name;
        BuildTree();
        if (_curNb != null && _curSec != null)
            CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
        Surface.Refresh();
        ScheduleSave();
    }

    private async Task EditPageDateAsync()
    {
        if (_curPage == null) return;
        var local = new DateTime(_curPage.CreatedTicks, DateTimeKind.Utc).ToLocalTime();
        var datePick = new DatePicker { Date = new DateTimeOffset(local.Date) };
        var timePick = new TimePicker { Time = local.TimeOfDay, ClockIdentifier = "24HourClock" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(datePick);
        panel.Children.Add(timePick);
        var dlg = new ContentDialog
        {
            Title = "Page date and time",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var dt = datePick.Date.Date + timePick.Time;
        _curPage.CreatedTicks = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime().Ticks;
        Surface.Refresh();
        ScheduleSave();
    }

    private void Calc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string token) return;
        CalcError.Text = "";
        switch (token)
        {
            case "C":
                CalcInput.Text = "";
                break;
            case "⌫":
                if (CalcInput.Text.Length > 0)
                    CalcInput.Text = CalcInput.Text[..^1];
                break;
            case "=":
                if (CalcModeName == "Programmer") ProgEvaluate();
                else CalcEvaluate();
                break;
            case "C₁": // programmer hex digit C
                CalcInput.Text += "C";
                break;
            case "±":
                CalcInput.Text = CalcInput.Text.StartsWith("-(") && CalcInput.Text.EndsWith(")")
                    ? CalcInput.Text[2..^1]
                    : (CalcInput.Text.Length > 0 ? $"-({CalcInput.Text})" : "-");
                break;
            default:
                CalcInput.Text += token;
                break;
        }
        if (token != "=") CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    // ---- calculator upgrades (#47): Ans, memory, variables, live preview,
    //      persistent history with insert-onto-page ----
    private double? _calcAns;
    private double _calcMem;
    private readonly Dictionary<string, double> _calcVars = new();

    private static readonly HashSet<string> CalcReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "sin", "cos", "tan", "asin", "acos", "atan", "sinh", "cosh", "tanh",
        "log", "ln", "sqrt", "abs", "exp", "pi", "e", "ans", "mod", "floor", "ceil", "round"
    };

    // Substitutes Ans and user variables into the expression; a value directly
    // after a digit or ')' gets an implicit multiply so "3x" works.
    private string PrepCalcExpr(string expr)
    {
        if (_calcAns is double ans) expr = SubstIdent(expr, "Ans", ans);
        foreach (var kv in _calcVars.OrderByDescending(k => k.Key.Length))
            expr = SubstIdent(expr, kv.Key, kv.Value);
        return expr;
    }

    private static string SubstIdent(string input, string name, double value)
    {
        var lit = value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
        return System.Text.RegularExpressions.Regex.Replace(input,
            "(?<![A-Za-z_])" + System.Text.RegularExpressions.Regex.Escape(name) + "(?![A-Za-z0-9_])",
            m => (m.Index > 0 && (char.IsDigit(input[m.Index - 1]) || input[m.Index - 1] == ')') ? "*(" : "(") + lit + ")",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private void CalcEvaluate()
    {
        var expr = CalcInput.Text.Trim();
        if (expr.Length == 0) return;

        // variable assignment: name = expression
        var m = System.Text.RegularExpressions.Regex.Match(expr, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$");
        if (m.Success && !CalcReserved.Contains(m.Groups[1].Value))
        {
            if (CalcEngine.TryEvaluate(PrepCalcExpr(m.Groups[2].Value), CalcDeg.IsChecked == true, out double vv, out string verr))
            {
                _calcVars[m.Groups[1].Value] = vv;
                PersistCalcVars();
                _calcAns = vv;
                AddCalcHistory($"{m.Groups[1].Value} = {vv.ToString("G12")}");
                CalcInput.Text = "";
                CalcPreview.Text = "";
                RefreshCalcVars();
            }
            else CalcError.Text = verr;
            return;
        }

        if (CalcEngine.TryEvaluate(PrepCalcExpr(expr), CalcDeg.IsChecked == true, out double result, out string error))
        {
            // Invariant culture: the result is written back into CalcInput and
            // re-parsed by CalcEngine (which parses with '.'). On a tr-TR machine
            // the default ToString yields "0,125", which then fails to re-evaluate.
            string res = result.ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
            _calcAns = result;
            AddCalcHistory($"{expr} = {res}");
            CalcInput.Text = res;
            CalcInput.SelectionStart = CalcInput.Text.Length;
            CalcPreview.Text = "";
        }
        else
        {
            CalcError.Text = error;
        }
    }

    private void AddCalcHistory(string entry)
    {
        _library.CalcHistory.Insert(0, entry);
        if (_library.CalcHistory.Count > 60) _library.CalcHistory.RemoveAt(_library.CalcHistory.Count - 1);
        RefreshCalcHistory();
        ScheduleSave();
    }

    private void RefreshCalcHistory()
    {
        CalcHistory.ItemsSource = null;
        CalcHistory.Items.Clear();
        foreach (var entry in _library.CalcHistory)
        {
            var g = new Grid { Tag = entry };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tbx = new TextBlock { Text = entry, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var ins = new Button
            {
                Content = new TextBlock { Text = "↳", FontSize = 12 },
                Padding = new Thickness(6, 0, 6, 0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(ins, "Insert onto the page");
            var entryCopy = entry;
            ins.Click += (_, _) =>
            {
                var centre = Surface.ScreenToWorld(new Point(Surface.ActualWidth / 2, Surface.ActualHeight / 2));
                Surface.AddTextElement(centre.X - 120, centre.Y - 14, 320,
                    PlainToRtf(entryCopy, _library.DefaultFont, (float)_library.DefaultFontSize, ContrastHexForPage()));
                ShowStatus("Inserted onto the page.");
            };
            Grid.SetColumn(tbx, 0);
            Grid.SetColumn(ins, 1);
            g.Children.Add(tbx);
            g.Children.Add(ins);
            CalcHistory.Items.Add(g);
        }
    }

    // Variables survive restarts via the library (#A7). Values round-trip in
    // invariant culture — same "0,125" pitfall as CalcEvaluate's result field.
    private void PersistCalcVars()
    {
        _library.CalcVars = _calcVars
            .Select(kv => $"{kv.Key}={kv.Value.ToString("G12", System.Globalization.CultureInfo.InvariantCulture)}")
            .ToList();
        ScheduleSave();
    }

    private void RestoreCalcVars()
    {
        foreach (var entry in _library.CalcVars)
        {
            var parts = entry.Split('=', 2);
            if (parts.Length == 2 && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                _calcVars[parts[0]] = v;
        }
        if (_calcVars.Count > 0) RefreshCalcVars();
    }

    private void RefreshCalcVars()
    {
        CalcVarsPanel.Children.Clear();
        foreach (var kv in _calcVars.OrderBy(k => k.Key))
        {
            var chip = new Button { Content = $"{kv.Key} = {kv.Value:G6}", FontSize = 11, Padding = new Thickness(6, 2, 6, 2) };
            ToolTipService.SetToolTip(chip, "Tap to insert into the expression · right-click to remove");
            var name = kv.Key;
            chip.Click += (_, _) =>
            {
                CalcInput.Text += name;
                CalcInput.SelectionStart = CalcInput.Text.Length;
            };
            var fly = new MenuFlyout();
            var rm = new MenuFlyoutItem { Text = "Remove " + name };
            rm.Click += (_, _) => { _calcVars.Remove(name); PersistCalcVars(); RefreshCalcVars(); };
            fly.Items.Add(rm);
            chip.ContextFlyout = fly;
            CalcVarsPanel.Children.Add(chip);
        }
        CalcVarsPanel.Visibility = _calcVars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CalcInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        CalcError.Text = "";
        if (CalcModeName == "Programmer") { CalcPreview.Text = ""; return; }
        var txt = CalcInput.Text.Trim();
        if (txt.Length == 0) { CalcPreview.Text = ""; return; }
        var m = System.Text.RegularExpressions.Regex.Match(txt, @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$");
        var expr = m.Success && !CalcReserved.Contains(m.Groups[1].Value) ? m.Groups[2].Value : txt;
        CalcPreview.Text = CalcEngine.TryEvaluate(PrepCalcExpr(expr), CalcDeg.IsChecked == true, out double v, out _)
            ? "= " + v.ToString("G12")
            : "";
    }

    private void CalcAns_Click(object sender, RoutedEventArgs e)
    {
        CalcInput.Text += "Ans";
        CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    private void CalcMemClear_Click(object sender, RoutedEventArgs e) { _calcMem = 0; ShowStatus("Memory cleared."); }

    private void CalcMemRecall_Click(object sender, RoutedEventArgs e)
    {
        CalcInput.Text += _calcMem.ToString("G12", System.Globalization.CultureInfo.InvariantCulture);
        CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    private void CalcMemPlus_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCalcValue() is double v) { _calcMem += v; ShowStatus($"M = {_calcMem:G12}"); }
    }

    private void CalcMemMinus_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentCalcValue() is double v) { _calcMem -= v; ShowStatus($"M = {_calcMem:G12}"); }
    }

    private double? CurrentCalcValue()
    {
        var txt = CalcInput.Text.Trim();
        if (txt.Length > 0 && CalcEngine.TryEvaluate(PrepCalcExpr(txt), CalcDeg.IsChecked == true, out double v, out _)) return v;
        return _calcAns;
    }

    private void CalcInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CalcEvaluate();
            e.Handled = true;
        }
    }

    private bool TextBoxFocused =>
        Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused;

    private async void CopyAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextBoxFocused) { args.Handled = false; return; } // text box keeps its own Ctrl+C
        Surface.CommitActiveSelection();
        if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            await PushSelectionToClipboardAsync();
            ShowStatus("Copied.");
            args.Handled = true;
        }
        else args.Handled = false;
    }

    private async void CutAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextBoxFocused) { args.Handled = false; return; }
        Surface.CommitActiveSelection();
        if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            await PushSelectionToClipboardAsync();
            Surface.DeleteSelection();
            ShowStatus("Cut.");
            args.Handled = true;
        }
        else args.Handled = false;
    }

    private void PasteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        bool hasBitmap = false, hasText = false;
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            hasBitmap = content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
            hasText = content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text);
        }
        catch { }

        if (hasBitmap)
        {
            // images always become resizable canvas objects
            args.Handled = true;
            _ = PasteImageAsync();
            return;
        }
        // A blinking Text caret is not yet a focused box, but paste must still
        // land there — materialise it and paste into it (#5, VERY IMPORTANT).
        if (TextBoxFocused || (Surface.HasPendingText && hasText))
        {
            args.Handled = true;
            _ = HandleTextPasteAsync(args);
            return;
        }
        if (InkSurface.HasCanvasClipboard)
        {
            // paste copied / cut writings or shapes near the centre of the view
            Surface.PasteCanvasAtViewCenter();
            args.Handled = true;
            return;
        }
        args.Handled = false;
    }

    private async Task HandleTextPasteAsync(KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();

                // a pending Text caret becomes the paste target (#5-batch4)
                var target = Surface.ActiveTextBox;
                if (target == null && Surface.HasPendingText) target = Surface.MaterializePendingText();

                // Check if it looks like markdown
                bool isMarkdown = text.Contains("**") || text.Contains("~~") || text.Contains("`") ||
                                  text.Split('\n').Any(l => l.StartsWith("#") || l.StartsWith("- ") || l.StartsWith("* "));

                if (isMarkdown && target != null)
                {
                    string rtf = MarkdownToRtf(text);
                    target.Document.Selection.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, rtf);
                    ShowStatus("Markdown pasted as rich text.");
                }
                else
                {
                    target?.Document.Selection.Paste(0);
                }
            }
        }
        catch { }
    }

    private string MarkdownToRtf(string md)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\deflang1033{\fonttbl{\f0\fnil\fcharset0 " + _library.DefaultFont + @";}{\f1\fnil\fcharset0 Courier New;}}");
        sb.Append(@"{\colortbl ;\red26\green26\blue26;}");
        sb.Append(@"\viewkind4\uc1\pars ");

        var lines = md.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                sb.Append(@"\par ");
                continue;
            }

            // Headings
            if (line.StartsWith("# "))
            {
                sb.Append(@"\fs40\b ");
                sb.Append(EscapeRtf(line[2..]));
                sb.Append(@"\b0\fs" + (int)(_library.DefaultFontSize * 2) + @"\par ");
                continue;
            }
            if (line.StartsWith("## "))
            {
                sb.Append(@"\fs32\b ");
                sb.Append(EscapeRtf(line[3..]));
                sb.Append(@"\b0\fs" + (int)(_library.DefaultFontSize * 2) + @"\par ");
                continue;
            }
            if (line.StartsWith("### "))
            {
                sb.Append(@"\fs28\b ");
                sb.Append(EscapeRtf(line[4..]));
                sb.Append(@"\b0\fs" + (int)(_library.DefaultFontSize * 2) + @"\par ");
                continue;
            }

            // List items
            bool isList = false;
            string content = line;
            if (line.StartsWith("- "))
            {
                isList = true;
                content = line[2..];
            }
            else if (line.StartsWith("* "))
            {
                isList = true;
                content = line[2..];
            }

            if (isList)
            {
                sb.Append(@"{\pntext\f0\'B7\tab}{\*\pn\pnlvlblt\pnf0\pnindent0{\pntxtb\'B7}}\fi-360\li360 ");
            }

            string formatted = FormatInlineRtf(content);
            sb.Append(formatted);
            sb.Append(@"\par ");
        }
        sb.Append("}");
        return sb.ToString();
    }

    private string EscapeRtf(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c == '\\' || c == '{' || c == '}') sb.Append('\\').Append(c);
            else if (c > 127) sb.Append(@"\u" + (int)c + "?");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private string FormatInlineRtf(string s)
    {
        string r = EscapeRtf(s);
        r = ReplacePattern(r, "**", @"\b ", @"\b0 ");
        r = ReplacePattern(r, "__", @"\b ", @"\b0 ");
        r = ReplacePattern(r, "*", @"\i ", @"\i0 ");
        r = ReplacePattern(r, "_", @"\i ", @"\i0 ");
        r = ReplacePattern(r, "~~", @"\strike ", @"\strike0 ");
        r = ReplacePattern(r, "`", @"{\f1 ", @" }");
        return r;
    }

    private string ReplacePattern(string text, string pattern, string startRtf, string endRtf)
    {
        var sb = new System.Text.StringBuilder();
        int pos = 0;
        bool state = false;
        while (pos < text.Length)
        {
            int idx = text.IndexOf(pattern, pos);
            if (idx < 0)
            {
                sb.Append(text[pos..]);
                break;
            }
            sb.Append(text[pos..idx]);
            sb.Append(state ? endRtf : startRtf);
            state = !state;
            pos = idx + pattern.Length;
        }
        return sb.ToString();
    }

    private async Task PasteImageAsync(System.Numerics.Vector2? worldTopLeft = null)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                ShowStatus("Clipboard has no image to paste.");
                return;
            }
            var streamRef = await content.GetBitmapAsync();
            using var stream = await streamRef.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var dir = System.IO.Path.Combine(LibraryStore.Dir, "assets");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.png");
            using (var outStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, outStream.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(software);
                await encoder.FlushAsync();
            }

            if (worldTopLeft is { } tl)
            {
                // pasted via the context menu at a specific point
                Surface.InsertImageAt(path, decoder.PixelWidth, decoder.PixelHeight, tl);
            }
            else
            {
                // Insert first so the image can consume a pending text caret as its
                // position, then switch to Pen so pen and touch behave as normal,
                // but keep the mouse in Auto/Move mode to allow mouse dragging.
                Surface.InsertImage(path, decoder.PixelWidth, decoder.PixelHeight);
                SelectTool("Pen");
                if (Surface.MouseMode == MouseMode.Grab)
                {
                    SetMouseMode(MouseMode.Auto);
                }
            }
            ShowStatus("Image pasted — drag it with the mouse to move, drag a corner to resize.");
        }
        catch
        {
            ShowStatus("Could not paste that image.");
        }
    }

    private void CalcHistory_Click(object sender, ItemClickEventArgs e)
    {
        // rows are Grids carrying the entry text in Tag (#47)
        var entry = (e.ClickedItem as FrameworkElement)?.Tag as string ?? e.ClickedItem as string;
        if (entry == null) return;
        int idx = entry.LastIndexOf("= ", StringComparison.Ordinal);
        if (idx >= 0) CalcInput.Text += entry[(idx + 2)..];
        CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    // =======================================================================
    // Export
    // =======================================================================
    private async Task<(byte[] Pixels, int Width, int Height)?> CaptureViewportAsync()
    {
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Surface);
            var buffer = await rtb.GetPixelsAsync();
            return (buffer.ToArray(), rtb.PixelWidth, rtb.PixelHeight);
        }
        catch
        {
            ShowStatus("Could not capture the page. Try again.");
            return null;
        }
    }

    // Export the WHOLE page (#13): fit all content into the viewport, render, then
    // restore the user's original view.
    private async Task<(byte[] Pixels, int Width, int Height)?> CapturePageAsync()
    {
        var saved = Surface.GetView();
        try
        {
            // drop text focus so a focused box's grip/handles aren't captured
            if (Surface.ActiveTextBox != null) ExportBtn.Focus(FocusState.Programmatic);
            Surface.FitToContent(28);
            await Task.Delay(110); // let the Win2D canvas + text layer re-render
            return await CaptureViewportAsync();
        }
        finally
        {
            Surface.SetView(saved.Offset, saved.Zoom);
        }
    }

    private async Task<StorageFile?> PickSaveFileAsync(string extension, string typeName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _curPage?.Name ?? "page"
        };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSaveFileAsync();
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        Surface.FlushTexts();
        var capture = await CapturePageAsync();
        if (capture == null) return;
        var file = await PickSaveFileAsync(".png", "PNG image");
        if (file == null) return;

        try
        {
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)capture.Value.Width, (uint)capture.Value.Height,
                96, 96, capture.Value.Pixels);
            await encoder.FlushAsync();
            ShowStatus($"Exported {file.Name}");
        }
        catch
        {
            ShowStatus("Could not save the PNG. Check the location and try again.");
        }
    }

    // =======================================================================
    // Lecture audio recording and playback (#roadmap: audio recording)
    // =======================================================================
    private async void SyncAudioPlaybackStateForCurrentPage()
    {
        if (_audioRecorder.IsRecording)
        {
            await _audioRecorder.StopRecordingAsync();
            AudioRecordBtn.IsChecked = false;
            RecordDot.Fill = new SolidColorBrush(Colors.Red);
        }
        _audioPlayer.Pause();
        _audioPlayer.Close();

        Surface.AudioPlayheadPosition = null;
        Surface.RecordingStartTicks = null;

        if (_curPage != null && !string.IsNullOrEmpty(_curPage.AudioFile) && System.IO.File.Exists(_curPage.AudioFile))
        {
            try
            {
                await _audioPlayer.OpenAsync(_curPage.AudioFile);
                AudioPlayBtn.IsEnabled = true;
                AudioSlider.IsEnabled = true;
                AudioSpeedBtn.IsEnabled = true;
                AudioSaveBtn.IsEnabled = true;
                AudioTimeText.Text = "0:00";
                Surface.RecordingStartTicks = _curPage.AudioStartTicks;
            }
            catch
            {
                AudioPlayBtn.IsEnabled = false;
                AudioSlider.IsEnabled = false;
                AudioSpeedBtn.IsEnabled = false;
                AudioSaveBtn.IsEnabled = false;
                AudioTimeText.Text = "0:00";
            }
        }
        else
        {
            AudioPlayBtn.IsEnabled = false;
            AudioSlider.IsEnabled = false;
            AudioSpeedBtn.IsEnabled = false;
            AudioSaveBtn.IsEnabled = false;
            AudioTimeText.Text = "0:00";
        }
        AudioPlayIcon.Glyph = "\uE768"; // Play icon
        AudioSlider.Value = 0;
        AudioSpeedBtn.Content = "1.0x";
    }

    private void UpdateAudioPlayerPosition(TimeSpan position)
    {
        if (_updatingAudioSlider) return;
        _updatingAudioSlider = true;
        try
        {
            var duration = _audioPlayer.Duration;
            if (duration.TotalSeconds > 0)
            {
                AudioSlider.Value = (position.TotalSeconds / duration.TotalSeconds) * 100.0;
            }
            AudioTimeText.Text = position.ToString(@"m\:ss");

            // Sync with InkSurface drawing!
            Surface.AudioPlayheadPosition = position;
            Surface.Refresh(); // Redraw canvas
        }
        finally
        {
            _updatingAudioSlider = false;
        }
    }

    private void SeekAudioToStroke(PenStroke stroke)
    {
        if (_curPage == null || _curPage.AudioStartTicks == 0 || string.IsNullOrEmpty(_curPage.AudioFile)) return;
        var pos = Quill.Services.AudioPlayer.StrokeTicksToAudioPosition(stroke.CreatedTicks, _curPage.AudioStartTicks);
        _audioPlayer.SeekTo(pos);
        if (!_audioPlayer.IsPlaying)
        {
            _audioPlayer.Play();
            AudioPlayIcon.Glyph = "\uE71A"; // Pause icon
        }
    }

    private async void AudioRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;

        if (AudioRecordBtn.IsChecked == true)
        {
            // Start recording
            var dir = System.IO.Path.Combine(LibraryStore.Dir, "audio");
            Directory.CreateDirectory(dir);
            var filePath = System.IO.Path.Combine(dir, $"{_curPage.Id}.m4a");

            try
            {
                // Stop active player
                _audioPlayer.Pause();

                await _audioRecorder.StartRecordingAsync(filePath);
                _curPage.AudioFile = filePath;
                _curPage.AudioStartTicks = _audioRecorder.RecordingStartTicks;
                Surface.RecordingStartTicks = _curPage.AudioStartTicks;
                ScheduleSave();

                // Glow red dot animation
                RecordDot.Fill = new SolidColorBrush(Colors.DarkRed);
                ShowStatus("Recording started…");
            }
            catch (Exception ex)
            {
                AudioRecordBtn.IsChecked = false;
                ShowStatus($"Failed to start recording: {ex.Message}");
            }
        }
        else
        {
            // Stop recording
            try
            {
                var duration = await _audioRecorder.StopRecordingAsync();
                RecordDot.Fill = new SolidColorBrush(Colors.Red);
                ShowStatus($"Recording saved ({duration.TotalSeconds:F0}s).");
                SyncAudioPlaybackStateForCurrentPage();
            }
            catch (Exception ex)
            {
                ShowStatus($"Error saving recording: {ex.Message}");
            }
        }
    }

    private void AudioPlay_Click(object sender, RoutedEventArgs e)
    {
        if (!_audioPlayer.IsPlaying)
        {
            _audioPlayer.Play();
            AudioPlayIcon.Glyph = "\uE71A"; // Pause icon
        }
        else
        {
            _audioPlayer.Pause();
            AudioPlayIcon.Glyph = "\uE768"; // Play icon
            // pausing must not leave later ink hidden by the playhead filter (#55)
            Surface.AudioPlayheadPosition = null;
            Surface.Refresh();
        }
    }

    private void AudioSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingAudioSlider) return;
        var duration = _audioPlayer.Duration;
        if (duration.TotalSeconds > 0)
        {
            var targetPos = TimeSpan.FromSeconds((AudioSlider.Value / 100.0) * duration.TotalSeconds);
            _audioPlayer.SeekTo(targetPos);
            AudioTimeText.Text = targetPos.ToString(@"m\:ss");
            Surface.AudioPlayheadPosition = targetPos;
            Surface.Refresh();
        }
    }

    private void AudioSpeed_Click(object sender, RoutedEventArgs e)
    {
        double currentRate = _audioPlayer.PlaybackRate;
        double nextRate = 1.0;
        if (currentRate == 1.0) nextRate = 1.5;
        else if (currentRate == 1.5) nextRate = 2.0;
        else if (currentRate == 2.0) nextRate = 0.5;
        else nextRate = 1.0;

        _audioPlayer.PlaybackRate = nextRate;
        AudioSpeedBtn.Content = $"{nextRate:0.0}x";
    }

    private async void AudioSave_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || string.IsNullOrEmpty(_curPage.AudioFile) || !System.IO.File.Exists(_curPage.AudioFile))
        {
            ShowStatus("No audio recording available for this page.");
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.FileTypeChoices.Add("M4A audio", new List<string> { ".m4a" });
            picker.SuggestedFileName = $"{_curPage.Name}_audio";

            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));

            var destFile = await picker.PickSaveFileAsync();
            if (destFile != null)
            {
                var srcFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(_curPage.AudioFile);
                await srcFile.CopyAndReplaceAsync(destFile);
                ShowStatus("Audio file saved successfully.");
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Failed to save audio file: {ex.Message}");
        }
    }

    private void BtnAudioRecording_Click(object sender, RoutedEventArgs e)
    {
        // no slide: the panel is draggable, and a translate animation would
        // snap it back off wherever the user parked it
        if (BtnAudioRecording.IsChecked == true) FadeIn(AudioFloatingPanel, 160);
        else FadeOut(AudioFloatingPanel, 130);
    }

}
