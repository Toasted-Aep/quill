using Quill.Helpers;
using Quill.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE TWO FLOATING BARS (UI-SPEC-V3 I). When the radial dial is the tool
/// surface the top bar stops being the place tools live, and two liquid-glass
/// bars take over:
///
/// <para><b>Top-left</b>, sitting directly ABOVE the docked dial: the
/// notebook-gallery icon, the live page name (renamable from here — and the
/// page surface stops drawing its own name and date the moment this bar is
/// up), then a TRANSPARENT DIVIDER, then Layers, Precision and Objects.</para>
///
/// <para><b>Top-right</b>: the zoom and tilt readouts with a lock, a
/// transparent divider, then Import, Export and Settings.</para>
///
/// <para>The "transparent divider" is literal: each bar is TWO glass panels
/// with a real gap between them, so the page shows through the seam exactly as
/// it does in the reference — not a hairline drawn on one continuous panel.</para>
///
/// <para><b>Layout constants live in <see cref="Metrics"/>, in one block</b>, so
/// the measured-reference pass can true them up without hunting through the
/// build code.</para>
///
/// <para>The dial docks 15 DIPs from the top edge, so there is no "above" until
/// it moves: this class pushes its measured height into
/// <see cref="ToolWheel.TopInset"/> and the dial re-parks below it.</para>
/// </summary>
public sealed class ChromeBars
{
    /// <summary>Every number the two clusters are laid out with, in ONE block.
    /// All of it is measured — docs/CONCEPTS-UI-REFERENCE.md §1.3 / §1.7 — so a
    /// refinement pass changes numbers here and nowhere else.</summary>
    public static class Metrics
    {
        /// <summary><b>THE ONE SWITCH.</b> The reference proves Concepts' two
        /// clusters are BARE: five vertical luminance profiles across the whole
        /// status-bar band return mean 0.00, max 0, one unique value — pixel
        /// identical to the canvas behind them (§1.3 "PROOF", §1.7). Concepts'
        /// own "floating UI" wording means drawn OVER the canvas, not a floating
        /// card. Set this to true to get the liquid-glass cards back; it is the
        /// only line that has to change.</summary>
        public const bool GlassBars = false;

        // ---- measured (docs/CONCEPTS-UI-REFERENCE.md §1.3, §1.7) ----
        /// <summary>Hit target per icon. Measured 84 physical px on both
        /// clusters, uniform.</summary>
        public const double IconPitch = 42;
        /// <summary>The glyph itself inside that target. Measured 27–32 phys.</summary>
        public const double GlyphSize = 16;
        /// <summary>Symmetric left and right margins. Measured 61.5 / 62.5 phys
        /// from the window edge to the first / last glyph centre.</summary>
        public const double EdgeMargin = 31;
        /// <summary>Row centre sits 31 DIP below the title bar, so a 42 DIP
        /// target starts 10 DIP down.</summary>
        public const double RowTop = 31 - IconPitch / 2;
        /// <summary>The divider rule: 1 x 16 DIP, LEFT CLUSTER ONLY, between the
        /// gallery icon and the page name. Colour below.</summary>
        public const double DividerW = 1, DividerH = 16;
        /// <summary>Gaps around the divider: 27 phys from the glyph, 23 phys to
        /// the name — 13.5 and 11.5 DIP.</summary>
        public const double DividerGapL = 13.5, DividerGapR = 11.5;
        /// <summary>Active-menu indicator: TWO SEPARATE underlines, one centred
        /// under each active toggle — never one bar spanning the group.</summary>
        public const double UnderlineW = 40, UnderlineH = 2;
        /// <summary>Top of the wheel, 52 DIP below the title bar (§1.4).</summary>
        public const double DialRimTop = 52;

        public const double TitleMaxWidth = 190;
        public const double ReadoutWidth = 52;

        // ---- glass mode only (ignored while GlassBars is false) ----
        public const double PanelGap = 8;
        public const double BarRadius = 17;
    }

    public sealed class Host
    {
        /// <summary>The page-editing delegate bundle the settings window already
        /// owns. Sharing it — instead of declaring a second copy — is what keeps
        /// MainWindow's footprint to one wiring block.</summary>
        public required Func<SettingsWindow.Host> PageOps { get; init; }
        public required Func<InkSurface> Surface { get; init; }
        public required Func<Notebook?> Notebook { get; init; }
        public required Func<Section?> Section { get; init; }
        public required ToolWheel Wheel { get; init; }
        /// <summary>The other tool surface (section 2). Gets the same headroom as
        /// the dial - the chrome bar sits above WHICHEVER of the two is up.</summary>
        public PenBar? Bar { get; init; }

        public required Action OpenGallery { get; init; }
        public required Action RenamePage { get; init; }
        public required Action OpenSettings { get; init; }
        /// <summary>DIPs of the right edge the docked settings panel is
        /// covering, or 0. The right cluster slides clear of it.</summary>
        public required Func<double> RightDockWidth { get; init; }

        /// <summary>The AI menu, handed over whole from the top bar. The button
        /// moves to the LEFT of Import (V3 K.18) rather than being rebuilt, so
        /// the two can never offer different commands.</summary>
        public required Func<FlyoutBase?> AiMenu { get; init; }

        /// <summary>Comment mode — the Comments pane owns the switch now that the
        /// top-bar toggle is gone (V3 K.17).</summary>
        public required Func<bool> CommentMode { get; init; }
        public required Action<bool> SetCommentMode { get; init; }

        /// <summary>Reduce-motion, so the layout solver's slide can stand down.</summary>
        public required Func<bool> ReduceMotion { get; init; }

        /// <summary>Renders a run of pages into the shared vector-page model.
        /// This is the SAME call the top bar's section and notebook PDF exports
        /// already use, so the export pane's Current Section / Current Notebook
        /// regions (V3 K.22) are the real multi-page path rather than a second
        /// implementation that could disagree with it.</summary>
        public required Func<IReadOnlyList<NotePage>, Task<List<Services.PdfVectorPage>>> CollectVectors { get; init; }

        /// <summary>Place a shape on the page — the Objects library's only way of
        /// putting something down, and the SAME call the shape menu makes
        /// (V3 L).</summary>
        public required Action<ShapeKind, bool> InsertShape { get; init; }

        /// <summary>Import: the existing "PDF as section" path.</summary>
        public required Action ImportPdf { get; init; }
        /// <summary>Import: the existing clipboard-image paste path.</summary>
        public required Action PasteImage { get; init; }
        /// <summary>Opens a file picker filtered to the given extensions.</summary>
        public required Func<string[], Task<StorageFile?>> PickOpen { get; init; }
        /// <summary>Save picker for the export pane, initialised with the HWND.</summary>
        public required Func<string, string, Task<StorageFile?>> PickSave { get; init; }

        /// <summary>Perspective guides already exist; the combo owns the whole
        /// path (placement + the recentre button), so the panel drives it rather
        /// than re-implementing it.</summary>
        public required Func<int> PerspectiveVps { get; init; }
        public required Action<int> SetPerspective { get; init; }
    }

    /// <summary>Top-bar elements these bars supersede while they are up. Raised
    /// on show/hide so <c>ApplyToolbarVisibility</c> can fold them into the same
    /// filter the dial's own slots already use (V3 A.9 / I).</summary>
    public event Action<IReadOnlySet<string>>? OwnedKeysChanged;

    private static readonly HashSet<string> Owned = new(StringComparer.Ordinal)
    {
        "ZoomBtn",          // the right bar carries the live zoom readout
        "ExportBtn",        // the right bar opens the export pane
        "PageSettingsBtn",  // background/grid/page size live in Settings + Precision
        // ---- V3 K.14 / K.16 / K.17 / K.18: the four orphans the legacy top bar
        // was still carrying once the dial took over. Each has a new home, and
        // leaving the old button up as well is exactly the duplication the user
        // reported.
        "TouchDrawToggle",  // K.14 -> Settings > Interaction, as an on/off toggle
        "ShapeBtn",         // K.16 -> the dial's Shape slot. Its 24px mark (square
                            //         + triangle) is all but identical to the
                            //         Objects mark in the left cluster, which is
                            //         why it read as a duplicate Objects button.
        "ToolComment",      // K.17 -> the Comments pane, opened from the dial
        "BtnAi",            // K.18 -> the right cluster, immediately left of Import
    };
    private static readonly HashSet<string> None = new(StringComparer.Ordinal);

    private readonly Grid _host;
    private readonly Host _h;

    private readonly Border _left;
    private readonly Border _right;
    private readonly StackPanel _leftRow = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly StackPanel _rightRow = new() { Orientation = Orientation.Horizontal, Spacing = 0 };

    // Rebuilt by Build(), never re-parented. A WinUI element may have exactly
    // one parent: keeping one TextBlock instance and adding it to a freshly
    // built row on the SECOND Build throws, which silently emptied both bars
    // (Build clears the rows first) and skipped everything after it - the dial
    // inset and the top-bar hand-back included.
    private TextBlock _title = new();
    private TextBlock _zoomText = new();
    private TextBlock _tiltText = new();

    private ExportWindow? _export;

    // The bare canvas panes (V3 K.19/K.20) and the one solver they all share
    // (K.21). Built lazily on first use so a session that never opens Layers
    // never pays for it.
    private readonly PanelLayout _layout;
    private CanvasPane? _layersPane, _precisionPane, _commentsPane;

    /// <summary>The solver every panel registers with. MainWindow hands it the
    /// Notebooks window and the dial so the K.21 rule covers those too.</summary>
    public PanelLayout Layout => _layout;

    private bool _on;
    private bool _zoomLocked;
    private float _lockedZoom = 1f;
    private bool _reasserting;

    public static ChromeBars Attach(Grid host, Host h) => new(host, h);

    private ChromeBars(Grid host, Host h)
    {
        _host = host;
        _h = h;
        _layout = new PanelLayout(host, h.ReduceMotion);

        // The hosts carry NO background and NO border: in bare mode there is
        // no surface at all, and in glass mode each CLUSTER supplies its own.
        // The left margin is measured to the first GLYPH centre, so the host
        // backs off by half the hit target to put the glyph on 31 DIP.
        double inset = Metrics.EdgeMargin - Metrics.IconPitch / 2;
        _left = new Border
        {
            Child = _leftRow,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(inset, Metrics.RowTop, 0, 0),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _right = new Border
        {
            Child = _rightRow,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, Metrics.RowTop, inset, 0),
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        // Above the dial layer (ZIndex 60) so a bar is never swallowed by it.
        Canvas.SetZIndex(_left, 70);
        Canvas.SetZIndex(_right, 70);
        _host.Children.Add(_left);
        _host.Children.Add(_right);

        // Both clusters are OBSTACLES, never movable: their positions are the
        // measured reference (31 DIP margins, 31 DIP row centre) and the whole
        // point of the panels moving is that these stay put (K.21).
        _layout.Register("chrome-left", _left);
        _layout.Register("chrome-right", _right);

        Build();

        // The bar's own height is the dial's headroom; measure it rather than
        // hard-coding, because touch mode and a long page name both change it.
        _left.SizeChanged += (_, _) => PushInset();

        // The widgets read their ink from the LIVE tree, not from a static.
        ChromeUi.ThemeSource = _host;
        var surface = _h.Surface();
        surface.ViewChanged += OnViewChanged;
        // The Layers pane counts what is on the page and the Comments pane lists
        // its pins: both are reports, so they have to follow the page rather than
        // freeze at the moment they were opened. Coalesced onto the dispatcher so
        // a stroke in progress does not rebuild a panel per sample.
        surface.ContentChanged += ScheduleContentRefresh;
        _host.ActualThemeChanged += (_, _) => { ChromeUi.ThemeSource = _host; Refresh(); };
    }

    // =====================================================================
    // Show / hide
    // =====================================================================

    /// <summary>Shown exactly when the radial dial is the tool surface.</summary>
    public void SetVisible(bool on)
    {
        if (_on == on) { if (on) SyncReadouts(); return; }
        _on = on;
        _left.Visibility = _right.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // The bar replaces the page's own name-and-date header (V3 B / I).
        try { _h.Surface().ShowPageHeader = !on; _h.Surface().Refresh(); } catch { }

        if (!on)
        {
            try { _h.Wheel.TopInset = 0; } catch { }
            _export?.Hide();
            // The panes belong to the bars: leaving Layers on canvas after the
            // dial is switched off would strand a panel with no way to close it.
            _layersPane?.Hide();
            _precisionPane?.Hide();
            _commentsPane?.Hide();
            _objects?.Hide();
            OwnedKeysChanged?.Invoke(None);
            return;
        }
        // Guarded: this runs from ApplyPenRowVisibility, which runs from
        // SwitchToPage, which runs from FinishStartup - a throw here used to
        // take the whole of startup with it.
        try { Refresh(); } catch { }
        PushInset();
        ApplyDockInset();
        OwnedKeysChanged?.Invoke(Owned);
    }

    /// <summary>Rebuild after a theme or page change — this surface captures its
    /// colours at build time exactly like the settings window and the dial.</summary>
    public void Refresh()
    {
        Build();
        SyncReadouts();
        PushInset();
        _export?.Refresh();
        // The panes capture their ink at build time too.
        foreach (var p in new[] { _layersPane, _precisionPane, _commentsPane })
        {
            p?.Repaint();
            p?.RefreshIfOpen();
        }
        _objects?.Refresh();
        _layout.Invalidate();
    }

    /// <summary>Slides the right cluster clear of the docked settings panel, so
    /// the glyph that opened it can still close it. A docked panel has no title
    /// bar and therefore no close button of its own - by design - so the toggle
    /// that opened it must stay reachable.</summary>
    public void ApplyDockInset()
    {
        try
        {
            double inset = Metrics.EdgeMargin - Metrics.IconPitch / 2;
            _right.Margin = new Thickness(0, Metrics.RowTop, inset + _h.RightDockWidth(), 0);
        }
        catch { }
    }

    /// <summary>Cheap update for the things that change constantly.</summary>
    public void SyncReadouts()
    {
        try
        {
            var page = _h.PageOps().Page();
            _title.Text = string.IsNullOrWhiteSpace(page?.Name) ? "Untitled page" : page!.Name;
            _zoomText.Text = $"{Math.Round(_h.Surface().ViewZoom * 100)}%";
            // Quill has no canvas rotation yet, so this is an honest constant
            // rather than a number invented to fill the slot.
            _tiltText.Text = "0°";
        }
        catch { }
    }

    private void PushInset()
    {
        if (!_on) return;
        // Measured: the wheel's rim starts 52 DIP below the title bar (§1.4).
        // Expressed as an inset off the wheel's own resting dock so this class
        // never hard-codes the wheel's padding.
        try { _h.Wheel.TopInset = Math.Max(0, Metrics.DialRimTop - ToolWheel.RestingRimTop); } catch { }
        // The bar docks from its own top edge rather than from a rim, so it takes
        // the measured offset directly.
        try { if (_h.Bar != null) _h.Bar.TopInset = Metrics.DialRimTop; } catch { }
    }

    // =====================================================================
    // Build
    // =====================================================================
    private void Build()
    {
        _leftRow.Children.Clear();
        _rightRow.Children.Clear();

        // ---- LEFT CLUSTER: gallery | divider | page name | layers precision objects
        var left = ChromeUi.Row(0);
        left.VerticalAlignment = VerticalAlignment.Center;
        left.Children.Add(BarButton(Icons.Notebook, "Notebook gallery", _h.OpenGallery));
        left.Children.Add(Divider());

        _title = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Metrics.TitleMaxWidth,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        };

        // The page title is renamable FROM THE BAR (V3 B) - the whole cell is
        // the target, and it carries the pencil so it does not read as a label.
        var titleCell = ChromeUi.Row(6);
        titleCell.Padding = new Thickness(0, 0, Metrics.IconPitch / 2, 0);
        titleCell.VerticalAlignment = VerticalAlignment.Center;
        titleCell.Background = new SolidColorBrush(Colors.Transparent);
        titleCell.Children.Add(_title);
        var pencil = ChromeUi.Mark(Icons.Rename, 12);
        if (pencil != null) { pencil.Opacity = 0.5; titleCell.Children.Add(pencil); }
        ToolTipService.SetToolTip(titleCell, "Rename this page");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(titleCell, "Page name - tap to rename");
        titleCell.Tapped += (_, _) => _h.RenamePage();
        left.Children.Add(titleCell);

        left.Children.Add(PanelButton(Icons.Layers, "Layers", () => Layers));
        left.Children.Add(PanelButton(Icons.Precision, "Precision", () => Precision));
        // Objects is NOT a bare canvas pane: V3 L gives it the old resizable
        // "iPad-like" floating window instead, on the left.
        left.Children.Add(ObjectsButton());

        _leftRow.Children.Add(Cluster(left));

        // ---- RIGHT CLUSTER: lock zoom tilt | AI import export settings
        var right = ChromeUi.Row(0);
        right.VerticalAlignment = VerticalAlignment.Center;
        foreach (var el in BuildViewReadout()) right.Children.Add(el);
        // K.18: the AI button sits immediately to the LEFT of Import. It carries
        // the top bar's own flyout rather than a second copy of the menu.
        var ai = BarButton(Icons.Ai, "AI assistant — summarise, tag, ask, improve", () => { });
        try { ai.Flyout = _h.AiMenu(); } catch { }
        right.Children.Add(ai);
        right.Children.Add(BarMenuButton(Icons.Import, "Import", BuildImportMenu()));
        right.Children.Add(BarButton(Icons.Export, "Export", OpenExport));
        right.Children.Add(BarButton(Icons.Settings, "Settings", _h.OpenSettings));

        _rightRow.Children.Add(Cluster(right));

        SyncReadouts();
    }

    /// <summary>Bare by default. In glass mode the cluster gets the card back -
    /// one switch, per <see cref="Metrics.GlassBars"/>.</summary>
    private static FrameworkElement Cluster(UIElement content) =>
        Metrics.GlassBars
            ? ChromeUi.GlassPanel(content, Metrics.BarRadius)
            : new Border { Child = content, Background = new SolidColorBrush(Colors.Transparent) };

    /// <summary>One status-bar glyph on its measured 42 DIP hit target, with the
    /// 40 x 2 DIP underline slot beneath it. Bare: no background, no border.</summary>
    private static Grid Slot(FrameworkElement? art, string tip, bool underline)
    {
        var cell = new Grid
        {
            Width = Metrics.IconPitch,
            Height = Metrics.IconPitch,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        if (art != null)
        {
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(art);
        }
        if (underline)
            cell.Children.Add(new Border
            {
                Width = Metrics.UnderlineW,
                Height = Metrics.UnderlineH,
                CornerRadius = new CornerRadius(Metrics.UnderlineH / 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(ChromeUi.Ink),
            });
        ToolTipService.SetToolTip(cell, tip);
        return cell;
    }

    private static Button Bare(FrameworkElement content, string tip)
    {
        var b = new Button
        {
            Content = content,
            Width = Metrics.IconPitch,
            Height = Metrics.IconPitch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Metrics.IconPitch / 2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    private static Button BarButton(string geometry, string tip, Action click, bool stroked = false)
    {
        var art = stroked
            ? Icons.Stroked(geometry, ChromeUi.Ink, Metrics.GlyphSize, 1.6)
            : Icons.Filled(geometry, ChromeUi.Ink, Metrics.GlyphSize);
        var b = Bare(Slot(art, tip, underline: false), tip);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button BarMenuButton(string geometry, string tip, FlyoutBase flyout)
    {
        var b = BarButton(geometry, tip, () => { });
        b.Flyout = flyout;
        return b;
    }

    /// <summary>The measured 1 x 16 DIP rule, LEFT CLUSTER ONLY, sampled #262829.</summary>
    private static FrameworkElement Divider() => new Border
    {
        Width = Metrics.DividerW,
        Height = Metrics.DividerH,
        Margin = new Thickness(Metrics.DividerGapL, 0, Metrics.DividerGapR, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(ChromeUi.BarDivider),
    };

    /// <summary>A menu toggle. Its underline lights while its pane is on canvas -
    /// TWO SEPARATE underlines, one per active toggle, never one bar spanning the
    /// group (measured, reference 1.3).
    ///
    /// <para>It toggles a <see cref="CanvasPane"/>, NOT a flyout. The flyout
    /// version never opened: its <c>Opening</c> handler called <see cref="Build"/>
    /// to light this very underline, and Build clears the rows - unparenting the
    /// button WinUI was about to position the popup against, which aborts the
    /// popup silently. See CanvasPane's remarks.</para></summary>
    private Button PanelButton(string geometry, string label, Func<CanvasPane> pane)
    {
        var art = Icons.Filled(geometry, ChromeUi.Ink, Metrics.GlyphSize);
        bool open = false;
        try { open = PaneIfBuilt(label)?.IsOpen == true; } catch { }
        var b = Bare(Slot(art, label, underline: open), label);
        // Deferred to the click: building the pane inside Build() would create
        // all four panes on every repaint.
        b.Click += (_, _) => pane().Toggle();
        return b;
    }

    /// <summary>The pane behind a toggle, but only if it has been built - the
    /// underline must not be the thing that constructs it.</summary>
    private CanvasPane? PaneIfBuilt(string label) => label switch
    {
        "Layers" => _layersPane,
        "Precision" => _precisionPane,
        _ => null,
    };

    /// <summary>The Objects toggle. Same 42 DIP slot and same underline as its
    /// two neighbours, but it opens the floating Objects library (V3 L) rather
    /// than a bare canvas pane.</summary>
    private Button ObjectsButton()
    {
        var art = Icons.Filled(Icons.Objects, ChromeUi.Ink, Metrics.GlyphSize);
        var b = Bare(Slot(art, "Objects", underline: _objects?.IsOpen == true), "Objects");
        b.Click += (_, _) =>
        {
            ObjectsLibrary.Toggle();
            // The window has no state event of its own, so the underline and the
            // panel reflow both happen on the next tick, once IsOpen has settled.
            try
            {
                _host.DispatcherQueue.TryEnqueue(() =>
                {
                    try { Build(); } catch { }
                    _layout.Invalidate();
                });
            }
            catch { }
        };
        return b;
    }

    private ObjectsWindow? _objects;

    private ObjectsWindow ObjectsLibrary
    {
        get
        {
            if (_objects != null) return _objects;
            _objects = ObjectsWindow.Attach(_host, new ObjectsWindow.Host
            {
                Library = () => _h.PageOps().Library(),
                Save = () => _h.PageOps().Save(),
                InsertShape = _h.InsertShape,
                Status = s => _h.PageOps().Status(s),
            });
            // K.21 covers the Objects library too. It lives in the popup layer
            // rather than the canvas Grid, so it joins as a VIRTUAL obstacle that
            // reports its own rectangle: the bare panes move out from under it,
            // and it is never moved itself because the user drags it.
            _layout.RegisterRect("objects", () => _objects?.Bounds);
            return _objects;
        }
    }

    // =====================================================================
    // The four bare canvas panes (V3 K.19 / K.20). Layers and Precision go
    // BOTTOM-LEFT as the user asked; Objects and Comments join them in the same
    // corner and the solver tiles them upwards from there.
    // =====================================================================
    private CanvasPane Layers => _layersPane ??= MakePane(
        "Layers", "Layers", BuildLayersPanel, PanelLayout.Anchor.BottomLeft, order: 20, width: 320);

    private CanvasPane Precision => _precisionPane ??= MakePane(
        "Precision", "Precision", BuildPrecisionPanel, PanelLayout.Anchor.BottomLeft, order: 21, width: 340);

    private CanvasPane Comments => _commentsPane ??= MakePane(
        "Comments", "Comments", BuildCommentsPanel, PanelLayout.Anchor.BottomLeft, order: 23, width: 320);

    private CanvasPane MakePane(string id, string title, Func<FrameworkElement> build,
                                PanelLayout.Anchor home, int order, double width)
    {
        // A long panel scrolls inside itself rather than growing past the canvas;
        // the scroller is chrome-free too, so the pane stays bare.
        FrameworkElement Wrapped() => new ScrollViewer
        {
            MaxHeight = 460,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Colors.Transparent),
            Content = build(),
        };
        var pane = new CanvasPane(_host, _layout, id, title, Wrapped, home, order, width);
        // The underline under the toggle is the only feedback the bar gives, so
        // it has to follow the pane rather than the click.
        pane.StateChanged = () => { try { Build(); } catch { } };
        return pane;
    }

    /// <summary>Opens (or closes) the Comments pane. This is what the radial
    /// dial's Comment slot runs now that the top-bar toggle is gone (K.17).</summary>
    public void ToggleComments() => Comments.Toggle();

    /// <summary>True while the Comments pane is up — the dial lights its slot
    /// from this.</summary>
    public bool CommentsOpen => _commentsPane?.IsOpen == true;

    // ---- zoom / tilt readout, lockable -----------------------------------
    /// <summary>The right cluster's left half: the zoom lock, the live zoom
    /// readout and the tilt readout. Returned as a SEQUENCE so each glyph keeps
    /// its own measured 42 DIP slot rather than being packed into a card.
    /// Measured order (reference 1.3): lock, "100%", "0deg", then import.</summary>
    private IEnumerable<FrameworkElement> BuildViewReadout()
    {
        // The lock is REAL for zoom: while it is on, a stray pinch or Ctrl+wheel
        // is snapped straight back to the locked level.
        var lockBtn = BarButton(
            _zoomLocked ? Icons.LockClosed : Icons.LockOpen,
            _zoomLocked
                ? "Zoom locked at " + Math.Round(_h.Surface().ViewZoom * 100) + "% - tap to unlock"
                : "Lock the zoom level (tilt has nothing to lock until canvas rotation lands)",
            ToggleLock);
        yield return lockBtn;

        _zoomText = new TextBlock
        {
            FontSize = 12.5,
            MinWidth = Metrics.ReadoutWidth,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        };
        var zoomCell = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Height = Metrics.IconPitch,
            Children = { _zoomText },
        };
        ToolTipService.SetToolTip(zoomCell, "Zoom - tap to return to 100%");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(zoomCell, "Zoom level");
        zoomCell.Tapped += (_, _) =>
        {
            _h.Surface().SetViewZoom(1f);
            _lockedZoom = 1f;
            SyncReadouts();
        };
        yield return zoomCell;

        _tiltText = new TextBlock
        {
            FontSize = 12.5,
            MinWidth = 34,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ChromeUi.Dim),
        };
        var tiltCell = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Height = Metrics.IconPitch,
            Margin = new Thickness(0, 0, Metrics.IconPitch / 2, 0),
            Children = { _tiltText },
        };
        // DEFERRED, AND SAID SO (V3 K.26). Tilt is not a readout that needs
        // filling in - it needs canvas rotation, which Quill does not have. The
        // view transform is a scale and a translate: InkSurface converts between
        // screen and world in 62 separate inline expressions rather than through
        // its two helpers, and 51 more places build or test axis-aligned
        // rectangles that stop being valid the moment the canvas is turned. A
        // number here that moved while the eraser, the lasso and the text-box
        // hit-tests still assumed square would be worse than no number at all,
        // so it stays at zero and this tooltip says why.
        ToolTipService.SetToolTip(tiltCell,
            "Canvas tilt is not implemented. Quill can zoom and pan but cannot rotate the canvas, and a tilt " +
            "readout that moved without real rotation behind it would be a lie. It stays at 0 degrees.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tiltCell, "Canvas tilt (not implemented)");
        yield return tiltCell;
    }

    private void ToggleLock()
    {
        _zoomLocked = !_zoomLocked;
        _lockedZoom = _h.Surface().ViewZoom;
        Build();
        _h.PageOps().Status(_zoomLocked
            ? $"Zoom locked at {Math.Round(_lockedZoom * 100)}%."
            : "Zoom unlocked.");
    }

    private bool _contentRefreshPending;

    /// <summary>One rebuild per idle turn, however many edits landed. A pane that
    /// is closed costs nothing at all.</summary>
    private void ScheduleContentRefresh()
    {
        if (_contentRefreshPending) return;
        if (_layersPane?.IsOpen != true && _commentsPane?.IsOpen != true) return;
        _contentRefreshPending = true;
        try
        {
            if (!_host.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                    () => { _contentRefreshPending = false; RebuildOpenPanel(); }))
                _contentRefreshPending = false;
        }
        catch { _contentRefreshPending = false; }
    }

    private void OnViewChanged()
    {
        SyncReadouts();
        if (!_zoomLocked || _reasserting) return;
        var s = _h.Surface();
        if (Math.Abs(s.ViewZoom - _lockedZoom) < 0.001f) return;
        _reasserting = true;
        try { s.SetView(s.GetView().Offset, _lockedZoom); }
        finally { _reasserting = false; }
    }

    // =====================================================================
    // Import — a simple dropdown (V3 I / E)
    // =====================================================================
    private MenuFlyout BuildImportMenu()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(ChromeUi.MenuItem("From file…", Icons.File, () => _ = ImportFileAsync(),
            tip: "An image lands on the page; a PDF comes in as a section."));
        menu.Items.Add(ChromeUi.MenuItem("Paste from clipboard", Icons.Clipboard, _h.PasteImage));
        // No capture path exists in the app, so this is present and honest
        // rather than a button that opens nothing.
        menu.Items.Add(ChromeUi.MenuItem("Take a photo", Icons.Camera,
            () => _h.PageOps().Status("Camera capture is not available — Quill has no capture path yet."),
            enabled: false,
            tip: "Not available: Quill has no camera capture path yet."));
        return menu;
    }

    private async Task ImportFileAsync()
    {
        try
        {
            var file = await _h.PickOpen(new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".pdf" });
            if (file == null) return;
            if (file.Path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { _h.ImportPdf(); return; }

            // Copy into the library's own assets folder first, exactly as the
            // clipboard path does, so the page never points at a file the user
            // may move or delete.
            var dir = System.IO.Path.Combine(Services.LibraryStore.Dir, "assets");
            System.IO.Directory.CreateDirectory(dir);
            string ext = System.IO.Path.GetExtension(file.Path);
            string path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
            System.IO.File.Copy(file.Path, path, overwrite: true);

            using var stream = await file.OpenAsync(FileAccessMode.Read);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            _h.Surface().InsertImage(path, decoder.PixelWidth, decoder.PixelHeight);
            _h.PageOps().Status("Image placed — drag it to move, drag a corner to resize.");
        }
        catch (Exception ex)
        {
            _h.PageOps().Status("Could not import that file: " + ex.Message);
        }
    }

    // =====================================================================
    // Export pane
    // =====================================================================
    private void OpenExport()
    {
        _export ??= ExportWindow.Attach(_host, new ExportWindow.Host
        {
            Surface = _h.Surface,
            Page = () => _h.PageOps().Page(),
            Notebook = _h.Notebook,
            Section = _h.Section,
            PickSave = _h.PickSave,
            Status = s => _h.PageOps().Status(s),
            CollectVectors = _h.CollectVectors,
        });
        _export.Toggle();
    }

    // =====================================================================
    // LAYERS — no model exists, and this panel says so
    // =====================================================================
    private FrameworkElement BuildLayersPanel()
    {
        var panel = new StackPanel { Spacing = 4, Width = 320 };
        panel.Children.Add(ChromeUi.Heading("Layers"));
        panel.Children.Add(ChromeUi.Caption(
            "Quill has no layer model yet. Every stroke, shape and text box on a page lives in one single stack, " +
            "so there is nothing here to show, reorder, hide or lock."));
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(ChromeUi.Caption(
            "The button is here because the bar's shape is fixed by the design, and because layers are the " +
            "dependency several other features are waiting on: PSD export, per-layer visibility, the selection " +
            "tool's layer scope and the Objects panel's per-object rows all need it first."));

        // A disabled preview of the row this panel WILL carry, so the shape of
        // the feature is legible without pretending it works.
        var preview = new StackPanel { Spacing = 0, Opacity = 0.45, Margin = new Thickness(0, 6, 0, 0) };
        preview.Children.Add(ChromeUi.ToggleRow("Layer 1", true, _ => { }, enabled: false,
            tip: "Not available: there is no layer model to switch."));
        preview.Children.Add(ChromeUi.ToggleRow("Background", true, _ => { }, enabled: false,
            tip: "Not available: there is no layer model to switch."));
        panel.Children.Add(preview);

        // The page inventory used to be the Objects panel's job. Objects is now
        // the object LIBRARY (V3 L) - a place to get things from, not a report of
        // what is already down - so the count of what is on the page lands here,
        // in the panel about the page's own stack.
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(BuildInventory());
        return panel;
    }

    /// <summary>What is actually on this page, counted live.</summary>
    private FrameworkElement BuildInventory()
    {
        var page = _h.PageOps().Page();
        var s = _h.Surface();
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(ChromeUi.Heading("On this page"));

        if (page == null)
        {
            panel.Children.Add(ChromeUi.Caption("No page is open."));
            return panel;
        }

        int images = page.Shapes.Count(x => x.Kind == ShapeKind.Image);
        int tables = page.Shapes.Count(x => x.Kind == ShapeKind.Table);
        int shapes = page.Shapes.Count - images - tables;

        void Line(string geometry, string label, int n)
        {
            var row = ChromeUi.Row(8);
            row.Padding = new Thickness(0, 5, 0, 5);
            var mark = ChromeUi.Mark(geometry, 16);
            if (mark != null) { mark.Opacity = n > 0 ? 0.85 : 0.35; row.Children.Add(mark); }
            row.Children.Add(ChromeUi.Label(label));
            var count = ChromeUi.Label(n.ToString(), strong: true);
            count.HorizontalAlignment = HorizontalAlignment.Right;
            var g = new Grid();
            g.Children.Add(row);
            g.Children.Add(count);
            panel.Children.Add(g);
        }

        Line(Icons.Pen, "Strokes", page.Strokes.Count);
        Line(Icons.Shape, "Shapes", shapes);
        Line(Icons.Text, "Text boxes", page.Texts.Count);
        Line(Icons.Objects, "Images", images);
        Line(Icons.Grid, "Tables", tables);
        Line(Icons.Comment, "Comments", page.Comments.Count);
        panel.Children.Add(ChromeUi.Label(
            s.HasSelection ? $"{s.SelectedStrokes.Count} stroke(s) selected" : "Nothing selected", strong: true));
        return panel;
    }

    // =====================================================================
    // PRECISION — built for real where the feature exists, disabled and
    // explained where it does not
    // =====================================================================
    private FrameworkElement BuildPrecisionPanel()
    {
        var ops = _h.PageOps();
        var page = ops.Page();
        var panel = new StackPanel { Spacing = 2, Width = 340 };

        // ---- Grid (real) -------------------------------------------------
        panel.Children.Add(Row(Icons.Grid, "Grid"));
        panel.Children.Add(ChromeUi.Caption("The page's own gridlines. Spacing and colour also live in Settings ▸ Workspace."));

        var kinds = new (GridType Kind, string Label)[]
        {
            (GridType.None, "Off"), (GridType.Dotted, "Dots"), (GridType.Square, "Graph"),
            (GridType.Lines, "Lined"), (GridType.Isometric, "Isometric"), (GridType.Triangle, "Triangle"),
        };
        var gridChips = ChromeUi.Row(6);
        foreach (var (kind, label) in kinds)
        {
            var k = kind;
            gridChips.Children.Add(ChromeUi.Chip(label, page?.Grid == k, () =>
            {
                ops.SetGrid(k);
                RebuildOpenPanel();
            }, page != null));
        }
        panel.Children.Add(ChromeUi.HScroll(gridChips));

        var spacing = new Slider
        {
            Minimum = 16,
            Maximum = 96,
            StepFrequency = 4,
            Header = "Grid spacing",
            IsEnabled = page != null,
            Value = Math.Clamp(page?.GridSpacing ?? 32, 16, 96),
            Margin = new Thickness(0, 4, 0, 0),
        };
        spacing.ValueChanged += (_, e) => ops.SetGridSpacing(e.NewValue);
        panel.Children.Add(spacing);

        // ---- Snap (designed, not built) ----------------------------------
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(Row(Icons.Snap, "Snap"));
        const string snapWhy =
            "Not available yet: snapping is designed but unbuilt. Nothing in the ink pipeline quantises a " +
            "stroke's endpoints to the grid or to other geometry, so this switch would change nothing.";
        panel.Children.Add(ChromeUi.ToggleRow("Snap to grid", false, _ => { }, enabled: false, tip: snapWhy));
        panel.Children.Add(ChromeUi.ToggleRow("Snap to objects", false, _ => { }, enabled: false, tip: snapWhy));

        // ---- Measure (real) ----------------------------------------------
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(Row(Icons.Measure, "Measure"));
        panel.Children.Add(ChromeUi.Caption(
            "Drawing scale — how many world units make one inch. This is what keeps millimetre and inch page " +
            "presets, printing and PDF export physically exact."));
        var upi = new Slider
        {
            Minimum = 48,
            Maximum = 300,
            StepFrequency = 6,
            Header = "Units per inch",
            IsEnabled = page != null,
            Value = Math.Clamp(page?.UnitsPerInch > 0 ? page!.UnitsPerInch : 96, 48, 300),
        };
        upi.ValueChanged += (_, e) => ops.SetUnitsPerInch(e.NewValue);
        panel.Children.Add(upi);
        if (page != null)
        {
            string size = PageSizes.TryResolve(page, out double w, out double h)
                ? $"{Math.Round(w)} x {Math.Round(h)} units"
                : "Infinite canvas";
            panel.Children.Add(ChromeUi.Caption("This page: " + size));
        }

        // ---- Guides ------------------------------------------------------
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(Row(Icons.Guide, "Guides"));
        panel.Children.Add(ChromeUi.ToggleRow("Alignment guides", false, _ => { }, enabled: false,
            tip: "Not available yet: draggable rulers and alignment guides are designed but unbuilt."));
        // Perspective guides DO exist, and hiding the page-settings button would
        // otherwise make them unreachable — so they are surfaced here, live.
        panel.Children.Add(ChromeUi.Caption("Perspective guides are built, and live here:"));
        var vps = ChromeUi.Row(6);
        for (int i = 0; i <= 3; i++)
        {
            int n = i;
            vps.Children.Add(ChromeUi.Chip(n == 0 ? "Off" : $"{n}-point", _h.PerspectiveVps() == n,
                () => { _h.SetPerspective(n); RebuildOpenPanel(); }, page != null));
        }
        panel.Children.Add(vps);

        // ---- Recognition (real) ------------------------------------------
        panel.Children.Add(ChromeUi.Rule());
        panel.Children.Add(Row(Icons.Recognition, "Recognition"));
        var lib = ops.Library();
        panel.Children.Add(ChromeUi.ToggleRow("Shape recognition", lib.ShapeRecognition, v =>
        {
            lib.ShapeRecognition = v;
            _h.Surface().ShapeRecognition = v;
            ops.Save();
        }, tip: "Hold the pen still at the end of a stroke and Quill snaps the squiggle to a perfect shape."));
        return panel;
    }

    /// <summary>A pane's own control changed something the pane displays, so it
    /// rebuilds itself in place. It stays open — the flyout version had to close
    /// to re-render, which made every chip tap dismiss the panel.</summary>
    private void RebuildOpenPanel()
    {
        try
        {
            _layersPane?.RefreshIfOpen();
            _precisionPane?.RefreshIfOpen();
            _commentsPane?.RefreshIfOpen();
        }
        catch { }
    }

    // =====================================================================
    // COMMENTS — the page's own pins, and the switch that drops new ones
    // (V3 K.17: comments move to the tool window the dial can select)
    // =====================================================================
    private FrameworkElement BuildCommentsPanel()
    {
        var ops = _h.PageOps();
        var page = ops.Page();
        var s = _h.Surface();
        var panel = new StackPanel { Spacing = 2, Width = 300 };

        panel.Children.Add(ChromeUi.ToggleRow("Comment mode", _h.CommentMode(), v =>
        {
            _h.SetCommentMode(v);
            RebuildOpenPanel();
        }, tip: "Tap the page to drop a note pin, or tap a pin to read, resolve or delete it."));

        var lib = ops.Library();
        panel.Children.Add(ChromeUi.ToggleRow("Always show pins", lib.ShowCommentPins, v =>
        {
            lib.ShowCommentPins = v;
            s.ShowCommentsAlways = v;
            s.Refresh();
            ops.Save();
        }, tip: "Keep the pins visible even when comment mode is off."));

        panel.Children.Add(ChromeUi.Rule());

        if (page == null || page.Comments.Count == 0)
        {
            panel.Children.Add(ChromeUi.Caption(page == null
                ? "No page is open."
                : "No comments on this page yet. Switch comment mode on and tap the page to leave one."));
            return panel;
        }

        foreach (var c in page.Comments.OrderBy(x => x.CreatedTicks))
        {
            var comment = c;
            var row = new StackPanel { Spacing = 0, Padding = new Thickness(0, 6, 0, 6) };

            var head = new Grid();
            var when = new DateTime(comment.CreatedTicks, DateTimeKind.Utc).ToLocalTime();
            head.Children.Add(ChromeUi.Label(when.ToString("d MMM HH:mm"), strong: true));
            var mark = ChromeUi.Label(comment.Resolved ? "Resolved" : "Open");
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            mark.Opacity = 0.6;
            mark.FontSize = 11.5;
            head.Children.Add(mark);
            row.Children.Add(head);

            var text = ChromeUi.Caption(string.IsNullOrWhiteSpace(comment.Text) ? "(empty)" : comment.Text);
            text.Opacity = comment.Resolved ? 0.4 : 0.78;
            row.Children.Add(text);

            var actions = ChromeUi.Row(6);
            actions.Children.Add(ChromeUi.Chip("Show", false, () => Centre(comment)));
            actions.Children.Add(ChromeUi.Chip(comment.Resolved ? "Reopen" : "Resolve", false, () =>
            {
                s.ResolveComment(comment, !comment.Resolved);
                ops.Save();
                RebuildOpenPanel();
            }));
            actions.Children.Add(ChromeUi.Chip("Delete", false, () =>
            {
                s.DeleteComment(comment);
                ops.Save();
                RebuildOpenPanel();
            }));
            row.Children.Add(actions);
            panel.Children.Add(row);
            panel.Children.Add(ChromeUi.Rule());
        }
        return panel;
    }

    /// <summary>Brings a pin into view without changing the zoom — the user is
    /// reading a comment, not reframing the drawing.</summary>
    private void Centre(Models.PageComment c)
    {
        try
        {
            var s = _h.Surface();
            var v = s.GetView();
            var offset = new System.Numerics.Vector2(
                (float)(s.ActualWidth / 2 - c.X * v.Zoom),
                (float)(s.ActualHeight / 2 - c.Y * v.Zoom));
            s.SetView(offset, v.Zoom);
            s.Refresh();
        }
        catch { }
    }

    private static FrameworkElement Row(string geometry, string label)
    {
        var row = ChromeUi.Row(8);
        row.Margin = new Thickness(0, 8, 0, 0);
        var mark = ChromeUi.Mark(geometry, 16);
        if (mark != null) row.Children.Add(mark);
        row.Children.Add(ChromeUi.Heading(label));
        return row;
    }
}
