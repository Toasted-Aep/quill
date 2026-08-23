using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Quill.Helpers;
using Quill.Services;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// Quill's general-purpose floating panel: a liquid-glass, rounded, DRAGGABLE and
/// RESIZABLE window that floats over the canvas inside the app's own visual tree
/// (no second HWND, so it inherits the theme, the acrylic and the accent for
/// free, and it can never be lost behind the main window).
///
/// <para>It is deliberately CONTENT-AGNOSTIC — the settings surface is only its
/// first tenant, and a tool / pen-library panel is expected to be the next. A
/// host supplies tabs as <c>(label, builder)</c> pairs and the window owns the
/// chrome: the drag bar, the close and info buttons, the category divider row and
/// the resize grips.</para>
///
/// <para><b>Chrome layout</b> (as specified):
/// close button UPPER-LEFT, info/help button UPPER-RIGHT, a short drag bar
/// centred at the TOP MIDDLE, and directly below them the category divider row
/// carrying the tabs. Eight iPadOS-style resize indicators sit on the corners and
/// edge midpoints; they fade in when the pointer is over the window.</para>
///
/// <para>Every icon is authored vector geometry — never a glyph font, never an
/// emoji.</para>
/// </summary>
public sealed class FloatingWindow
{
    // ---- chrome geometry (DIPs) ----
    /// <summary>The window's corner radius. Exposed so a tenant painting a
    /// full-bleed element against the top edge (the Brushes panel's preview
    /// strip) can clip itself to the same curve rather than guessing it.</summary>
    internal const double TopRadius = 16;
    private const double Radius = TopRadius;
    private const double HeaderH = 40;
    private const double MinW = 320, MinH = 260;
    /// <summary>The plate's own border. Named because §14.3's corner geometry is
    /// measured from the panel's INNER edge, which is this far in.</summary>
    private const double PanelBorder = 1.5;
    /// <summary>The header's inset. Named for the same reason: the corner targets
    /// undo it for themselves and must undo exactly it.</summary>
    private const double HeadPadX = 8, HeadPadY = 6;

    /// <summary>§17.5, the corner targets. The inner corner's radius is the outer
    /// radius less the border — an inset rounded rect keeps the arc's CENTRE and
    /// spends the inset on the radius — so this is the curve the targets follow.
    ///
    /// <para>FOLLOW, not avoid. That is the whole of what §17.5 changes.</para></summary>
    private const double InnerRadius = Radius - PanelBorder;

    /// <summary>§17.5: "A square covering the WHOLE corner of the panel, with ONE
    /// corner rounded to follow the panel's own radius and the other three
    /// square." So the side is the full header height — the panel's inner edge to
    /// the header's inner boundary — with nothing subtracted.
    ///
    /// <para><b>What §14.3 did, and why this is not a repeat of it.</b> §14.3 was
    /// itself a fix for a target that overshot: the button had been given
    /// negative margins to "cover the whole corner" and they pushed a SQUARE
    /// corner out past the panel's round one, so a press on the page just outside
    /// the curve landed on Close. §14.3's answer was to pull the square back to
    /// the arc's 45 degree point (<c>r − r/√2</c>, plus half a DIP of guard
    /// against layout rounding) — the largest axis-aligned rectangle that fits
    /// inside the curve. That stopped the overshoot and cost about 5.7 DIP on
    /// each axis, which is why the marks then sat in a corner they no longer
    /// filled. It was reasoned to be forced: "a composition clip does not
    /// participate in XAML hit testing, so a rounded target is not on offer".
    /// </para>
    ///
    /// <para>That premise was too strong. <c>UIElement.Clip</c> is indeed
    /// rectangle-only, but a <see cref="Path"/>'s FILL is ordinary hit-test
    /// geometry — a filled shape is hit inside its geometry and nowhere else. So
    /// the target can be the exact rounded square after all: see
    /// <see cref="CornerButton"/>, which gives the button a transparent Path of
    /// this shape and NO background of its own. The square fills its corner and
    /// still cannot protrude past the curve, because past the curve there is no
    /// geometry to hit.</para></summary>
    private static readonly double CornerSide = HeaderH;

    /// <summary>§11.6 item 42: "must leave a margin at the page edge".</summary>
    private const double EdgeGap = 14;

    /// <summary>§11.6 item 42: the band the top bar's two clusters occupy — the
    /// gallery / page name / Layers / Precision / Objects group on the left and
    /// the zoom / AI / Import / Export / Settings group on the right. A floating
    /// window opens directly below it and can be neither dragged nor resized over
    /// it. Derived from the bar's own measured metrics rather than hard-coded, so
    /// §11.5 item 31's thicker top bar carries this down with it.</summary>
    private static double TopBand =>
        ChromeBars.Metrics.RowTop + ChromeBars.Metrics.IconPitch + 8;

    /// <summary>The page host's top-left in the POPUP's coordinate space.
    ///
    /// <para>`_popup` carries a XamlRoot, so its offsets are measured from the
    /// window's origin, while every limit below is derived from `_host` - the
    /// page host, which starts under the top bar. Without this the two spaces
    /// differ by the bar's height: the top anchor lands inside the bar instead
    /// of below it (so the panel covers the top-right cluster), and the bottom
    /// limit is short by the same amount (so the panel cannot be dragged clear).
    /// Both symptoms, one cause.</para></summary>
    private Point HostOrigin
    {
        get
        {
            try { return _host.TransformToVisual(null).TransformPoint(new Point(0, 0)); }
            catch { return new Point(0, 0); }
        }
    }

    private readonly Panel _host;
    // A POPUP, not an in-tree overlay: a popup is composited into the XamlRoot's
    // own popup layer, so it is guaranteed to float above the Win2D canvas, the
    // toolbars and the docked panels without depending on Z-index bookkeeping
    // inside a Grid it does not own.
    private readonly Popup _popup;
    private readonly Border _panel;            // the window itself
    private readonly Border _tabRow;
    private readonly StackPanel _tabStrip;
    private readonly ScrollViewer _scroller;
    private readonly Grid _gripLayer;
    private readonly TextBlock _title;
    private Border? _dragPill;
    /// <summary>The two corner marks' hosts, kept so <see cref="PaintPanel"/> can
    /// re-ink them on a page change — see <see cref="PaintCornerMark"/>.</summary>
    private readonly ContentControl _closeMark, _infoMark;

    private readonly List<(string Label, Func<FrameworkElement> Build)> _tabs = new();
    private readonly Dictionary<int, FrameworkElement> _built = new();
    private int _active;
    /// <summary>THE STORED POSITION — AN INSET FROM THE SIDE THIS WINDOW IS
    /// ANCHORED TO (<see cref="OpenOn"/>). Null until something chooses one, in
    /// which case the resolver reads <see cref="EdgeGap"/> in its place.
    ///
    /// <para>This, with <see cref="_insetTop"/> and <see cref="_wantW"/> /
    /// <see cref="_wantH"/>, is the ONLY source of truth for this window's
    /// geometry. The popup's offsets and the panel's Width / Height are DERIVED
    /// from it against the current host on every change, and are never read back
    /// as state. That is the model the user specified: "make it so that when panel
    /// gets resized the distance from the side they're on gets remembered, and the
    /// panels move accordingly" (§15.4e).</para>
    ///
    /// <para>An inset, and not an absolute offset to be shifted. The absolute
    /// version needed a remembered host origin to shift AGAINST (<c>_lastOrg</c>)
    /// and a flag to choose between shifting and re-anchoring (<c>_userPlaced</c>,
    /// via <c>KeepsOwnPosition</c>), and the two call sites that read them
    /// disagreed — §15.4b item 2 measured a 387 DIP gap opening up under a panel
    /// nobody had touched. An inset has nothing to accumulate and nothing to
    /// disagree about, and it subsumes BOTH of that fix's arms: the corner an
    /// untouched panel wants IS inset <see cref="EdgeGap"/>, so preserving the
    /// inset re-anchors it, while preserving a dragged panel's inset holds it
    /// exactly where it was put. One rule, both behaviours.</para></summary>
    private double? _insetSide;

    /// <summary>The inset from the host's TOP — the vertical half of the pair.
    /// Null defaults to <see cref="TopBand"/>, the band §11.6 item 42 keeps clear.
    /// The default is resolved LATE rather than banked at first placement, so a
    /// window nobody has moved follows that band if §11.5 item 31's thicker top
    /// bar ever moves it.</summary>
    private double? _insetTop;

    /// <summary>THE SIZE THE WINDOW WANTS, which is not always the size it has.
    ///
    /// <para>A host with no room for it is rendered smaller — §11.6 item 42's
    /// limits are not negotiable — but this pair is left ALONE, so the window
    /// returns to the wanted size when the room comes back. §15.4b recorded the
    /// destructive version as an accepted limitation: "clamping into smaller
    /// windowed bounds is one-way, so a panel sized to full fullscreen height
    /// stays at the clamped size on return". Keeping the intent separate from the
    /// render is the whole of what retires it.</para></summary>
    private double _wantW, _wantH;

    /// <summary>Raised when the info / help button is pressed.</summary>
    public Action? InfoRequested { get; set; }
    /// <summary>Raised after the window is closed.</summary>
    public Action? Closed { get; set; }

    public bool IsOpen => _popup.IsOpen;

    /// <summary>Bumped every time the active tab's content is rebuilt. A tenant
    /// whose own Refresh may or may not have already triggered a rebuild (setting
    /// the ground raises PageTheme.Changed, which this window answers) compares
    /// this before and after to decide whether a second rebuild is needed —
    /// which is how §10.5 item 20's triple rebuild is held down to one.</summary>
    public int ContentRevision { get; private set; }

    /// <summary>Where this window is sitting, in the HOST's coordinates, or null
    /// when it is closed. A popup is composited outside the host's visual tree,
    /// so its position cannot be read with TransformToVisual — but the popup's
    /// own offsets are already expressed relative to the host, which is exactly
    /// what <see cref="PanelLayout"/> needs to route the canvas panels around
    /// this window (UI-SPEC-V3 K.21).</summary>
    public Windows.Foundation.Rect? Bounds => IsOpen
        ? new Windows.Foundation.Rect(_popup.HorizontalOffset, _popup.VerticalOffset,
                                      _panel.Width, _panel.Height)
        : null;

    public string Title
    {
        get => _title.Text;
        set => _title.Text = value;
    }

    public static FloatingWindow Attach(Panel host, double width = 432, double height = 620)
        => new(host, width, height);

    private FloatingWindow(Panel host, double width, double height)
    {
        _host = host;
        // The tenant's requested size is an INTENT, not the rendered size: a host
        // too small for it renders it smaller without forgetting this.
        _wantW = width;
        _wantH = height;
        ActiveRoot ??= host.XamlRoot;
        // Self-correcting, rather than a call from whoever toggled fullscreen.
        // SetPresenter does not lay out synchronously and the caption row is
        // FADED away, so the host's geometry settles some frames after the
        // toggle - anything that re-placed at the call site would read the OLD
        // origin. Reacting to the host's own SizeChanged instead is by
        // construction after layout, and it covers every route that moves the
        // row (F11, the bracket, the strip's middle mark, Esc, the dial surface
        // going away) without any of them knowing this class exists.
        //
        // SizeChanged and not LayoutUpdated: the latter fires for every layout
        // pass in the tree, and this would do a TransformToVisual on each. The
        // host cannot move without also changing size here - folding the row
        // both raises the host and makes it taller.
        _host.SizeChanged += HostGeometryChanged;

        _panel = new Border
        {
            Width = width,
            Height = height,
            MinWidth = MinW,
            MinHeight = MinH,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(PanelBorder),
        };
        Bind(_panel, Border.BorderBrushProperty, "GlassEdgeBrush", theme: false);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panel.Child = shell;

        // ---- header: close (upper-left), drag bar (top middle), info (upper-right)
        var header = new Grid { Padding = new Thickness(HeadPadX, HeadPadY, HeadPadX, 0) };

        // 17.5. Each target is a square filling ITS OWN WHOLE CORNER of the panel
        // - panel inner edge to the header's inner boundary - with one corner
        // rounded to the panel's radius and the other three square.
        //
        // The two faults this has now been through. FIRST, the close button was
        // given margins of exactly the header's padding so it would "cover the
        // whole corner": that put its top-left ON the panel's inner edge, at a
        // point the ROUNDED CORNER has already cut away, so a SQUARE target hung
        // out past the curve and a press on the page just outside the corner
        // landed on Close. SECOND, 14.3 fixed that overshoot by pulling the
        // square back to the arc's 45 degree point - correct, but it cost ~5.7
        // DIP on each axis, so the marks then sat in a corner they no longer
        // filled, which is what 17.5 is answering.
        //
        // Neither is repeated, because the target is no longer a square that has
        // to choose between the two: it is the exact rounded-square outline, as a
        // Path fill, which is real hit-test geometry. See CornerButton.
        var close = CornerButton(Icons.Close, "Close", right: false, stroked: true, out _closeMark);
        close.Click += (_, _) => Hide();
        header.Children.Add(close);

        var info = CornerButton(Icons.Info, "About these settings", right: true, stroked: false, out _infoMark);
        info.Click += (_, _) => InfoRequested?.Invoke();
        header.Children.Add(info);

        // The grab area is wider and taller than the visible bar so a pen or a
        // finger does not have to hit a 5px target.
        var grab = new Grid
        {
            Width = 132,
            Height = HeaderH - 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            // Flush to the TOP of the panel, not the middle of the header. The
            // negative top margin cancels the header's 6 DIP inset for this
            // child alone, so the pill reads as part of the window's edge.
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(grab, "Drag to move");
        var bar = new Border
        {
            Width = 52,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            // sits just inside the top edge; the grab area around it stays tall
            // so a pen or finger still has a real target
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0),
            Background = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnSurface, 0x66)),
        };
        _dragPill = bar;
        grab.Children.Add(bar);
        grab.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        grab.ManipulationDelta += (_, e) => MoveBy(e.Delta.Translation.X, e.Delta.Translation.Y);
        header.Children.Add(grab);

        // Section 4 item 1 puts the window's NAME beside the close button, at the
        // weight of a real title rather than the 12.5 DIP 55%-opacity watermark
        // this was. Painted from PageTheme, not from an opacity on whatever the
        // inherited foreground happens to be.
        _title = new TextBlock
        {
            FontSize = 17,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        header.Children.Add(_title);
        shell.Children.Add(header);

        // ---- category divider row (the tabs)
        _tabRow = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 2, 14, 0),
        };
        // section 3 wants the tabs CENTRED, which a left-aligned strip is not.
        _tabStrip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _tabRow.Child = _tabStrip;
        Grid.SetRow(_tabRow, 1);
        shell.Children.Add(_tabRow);

        // ---- content
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(14, 10, 10, 14),
        };
        Grid.SetRow(_scroller, 2);
        shell.Children.Add(_scroller);

        // ---- resize grips, drawn over everything
        _gripLayer = new Grid { Opacity = 0 };
        Grid.SetRowSpan(_gripLayer, 3);
        shell.Children.Add(_gripLayer);
        BuildGrips();
        _panel.PointerEntered += (_, _) => FadeGrips(1);
        _panel.PointerExited += (_, _) => FadeGrips(0);

        // Painted only now: PaintPanel colours the title and the tab rule as well
        // as the plate, and neither of those existed a moment ago.
        PaintPanel();
        // The window survives page turns, so it follows the page rather than
        // freezing at the ground it was built on.
        PageTheme.Changed += OnGroundChanged;

        _popup = new Popup
        {
            Child = _panel,
            // the window may legitimately hang past the host's edge while dragged
            ShouldConstrainToRootBounds = false,
        };
        // ONE SizeChanged subscription, not two. `ClampIntoView` was hooked here
        // as well, and it clamped the very offsets `HostGeometryChanged` had just
        // shifted — a clamp cleaning up after a shift. Resolving from the insets
        // does the clamping on the way through (§15.4e), so a second pass has
        // nothing left to correct.
    }

    // =======================================================================
    // Theme
    // =======================================================================

    /// <summary>§7: the floating windows (Settings, Export, Brushes, Objects)
    /// take PageTheme.Panel, which is deliberately NEAR-NEUTRAL - the reference
    /// panels are a flat #F7F7F7 or #141414 whatever hue the page is. What the
    /// page decides here is WHICH of the two, and on Blueprint, Brown Paper and
    /// Darkprint that is the dark one with white text.</summary>
    private void PaintPanel()
    {
        // A brush-level assignment: CardBrushFloat is a SHARED acrylic that
        // ApplyLiquidness re-tints for the whole app, so mutating it here would
        // repaint surfaces this window does not own.
        _panel.Background = new SolidColorBrush(PageTheme.Panel);
        if (_dragPill != null)
            _dragPill.Background = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnSurface, 0x66));
        _title.Foreground = new SolidColorBrush(PageTheme.OnSurface);
        // The corner marks are re-inked HERE and not only at construction. They
        // used to take their colour from a one-shot theme-dictionary fetch, so a
        // page change repainted the ground, the title and the tab rule and left
        // these two marks inked for the page before it.
        PaintCornerMark(_closeMark, Icons.Close, stroked: true);
        PaintCornerMark(_infoMark, Icons.Info, stroked: false);
        _tabRow.BorderBrush = new SolidColorBrush(PageTheme.Outline);
        try { _panel.RequestedTheme = Theme; } catch { }
    }

    private void OnGroundChanged()
    {
        PaintPanel();
        // The content captured the old palette at build time; throw it away — but
        // a repaint is not a navigation, so the reader keeps their place (§10.5
        // item 20). A page turn or a paper swatch is the commonest way this fires
        // and it was the commonest way the panel jumped to the top.
        RefreshContent(preserveScroll: true);
    }

    // =======================================================================
    // Tabs / content
    // =======================================================================

    /// <summary>Installs the window's categories. Content is built lazily on first
    /// activation, so a tab the user never opens costs nothing.</summary>
    public void SetTabs(IEnumerable<(string Label, Func<FrameworkElement> Build)> tabs)
    {
        _tabs.Clear();
        _tabs.AddRange(tabs);
        _built.Clear();
        _active = 0;
        BuildTabStrip();
        ShowTab(0);
    }

    /// <summary>Bring one of <see cref="SetTabs"/>' categories up by NAME.
    /// Returns false when no tab carries that label, so a caller that names a
    /// tab which has been renamed finds out rather than silently landing on
    /// whichever tab happened to be active.
    ///
    /// <para>By label and not by index because the caller is in another file:
    /// 11.3 item 25's star asks for "the Colors tab", and an index would break
    /// the moment a tab is inserted before it.</para></summary>
    public bool SelectTab(string label)
    {
        int i = _tabs.FindIndex(t =>
            string.Equals(t.Label, label, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        ShowTab(i);
        return true;
    }

    /// <summary>Throws away the built content so the next activation rebuilds it —
    /// used after a theme or language change, exactly like the rest of Quill's
    /// code-built surfaces.</summary>
    public void RefreshContent() => RefreshContent(false);

    /// <summary>Rebuild the active tab, optionally landing the reader back where
    /// they were.
    ///
    /// <para>§10.5 item 20 — "Settings ... scrolls back to the top whenever an
    /// option is picked" — is exactly this method throwing the scroll offset
    /// away. A panel that jumps to the top on every tap is unusable however fast
    /// the rebuild underneath it is, so a tenant that knows its rebuild is a
    /// repaint rather than a navigation asks to keep the offset.</para></summary>
    public void RefreshContent(bool preserveScroll)
    {
        double keep = 0;
        try { keep = _scroller.VerticalOffset; } catch { }
        _built.Clear();
        if (IsOpen) ShowTab(_active, preserveScroll ? keep : null);
    }

    /// <summary>Which page's developer font scale this window's CONTENT takes
    /// (§11.6 item 40).
    ///
    /// <para>Left null by a tenant that authors its own type at scale — Settings
    /// and Brushes do, because §3.1 and §4 fix the RATIOS between their headings
    /// and their captions and those have to survive the shrink. A tenant whose
    /// type is uniform names its page here instead and the window scales the
    /// finished tree, which is the same setting reaching a panel that has no
    /// specified type scale of its own to preserve.</para></summary>
    public string? FontPage { get; set; }

    /// <summary>Multiplies every explicit font size in a freshly built tree.
    /// Applied ONLY on the build, never on a cached tree, or switching back to a
    /// tab would scale it a second time. Walks the logical containers by type
    /// rather than using VisualTreeHelper: content has not been arranged when
    /// this runs, so a Button's template does not exist yet and only its Content
    /// is reachable.</summary>
    internal static void ScaleFonts(DependencyObject? node, double k)
    {
        if (node == null || Math.Abs(k - 1) < 0.001) return;
        switch (node)
        {
            case TextBlock t:
                t.FontSize *= k;
                if (t.LineHeight > 0) t.LineHeight *= k;
                return;
            case Control c:
                c.FontSize *= k;
                break;
        }
        switch (node)
        {
            case Panel p:
                foreach (var child in p.Children) ScaleFonts(child, k);
                break;
            case Border b:
                ScaleFonts(b.Child, k);
                break;
            case ContentControl cc:
                ScaleFonts(cc.Content as DependencyObject, k);
                break;
            case ItemsControl ic:
                foreach (var item in ic.Items) ScaleFonts(item as DependencyObject, k);
                break;
        }
    }

    /// <summary>The scroller's inset. Zero lets a tenant paint edge-to-edge —
    /// the Brushes panel's preview strip and its section bands are full-bleed
    /// (§4), so they cannot live inside the window's own padding.</summary>
    public Thickness ContentPadding
    {
        get => _scroller.Padding;
        set => _scroller.Padding = value;
    }

    /// <summary>§3: the tabs are "centred, 17 DIP semibold; the active tab carries
    /// a 2 DIP OnSurface underline". What shipped was 14 DIP, left-aligned, with
    /// a BrandOrange rule — neither the size nor the colour, and because this is
    /// SHARED chrome the same miss was on Settings, Export and Objects at once.
    /// The underline is now the ink colour of whatever page is up.</summary>
    private void BuildTabStrip()
    {
        // One tab is not a choice: the row would be a lone label repeating the
        // header's own title, which is what the Brushes panel (§4) would show.
        _tabRow.Visibility = _tabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _tabStrip.Children.Clear();
        if (_tabs.Count < 2) return;

        for (int i = 0; i < _tabs.Count; i++)
        {
            int idx = i;
            bool on = i == _active;
            var label = new TextBlock
            {
                Text = _tabs[i].Label,
                FontSize = 17,
                Margin = new Thickness(0, 0, 0, 7),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(on ? PageTheme.OnSurface : PageTheme.OnSurfaceMuted),
            };
            var underline = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = new SolidColorBrush(PageTheme.OnSurface),
                Visibility = on ? Visibility.Visible : Visibility.Collapsed,
            };

            var art = new Grid();
            art.Children.Add(label);
            art.Children.Add(underline);

            // A BUTTON, not a bare Grid with a Tapped handler. The tab strip is
            // the only way to switch a floating window's page, and as a Grid it
            // had no keyboard focus, no invoke pattern and no name — unreachable
            // for a screen reader and untestable from UIA. Stripped to nothing
            // but its hit area so it still LOOKS like the reference's bare label.
            var cell = new Button
            {
                Content = art,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 0,
                MinHeight = 0,
                CornerRadius = new CornerRadius(4),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, _tabs[i].Label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(cell, "Tab_" + _tabs[i].Label);
            cell.Click += (_, _) => ShowTab(idx);
            _tabStrip.Children.Add(cell);
        }
    }

    private void ShowTab(int index, double? scrollTo = null)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _active = index;
        BuildTabStrip();
        if (!_built.TryGetValue(index, out var content))
        {
            try { content = _tabs[index].Build(); }
            catch { content = new TextBlock { Text = "This section could not be built." }; }
            // §11.6 item 40, for the tenants that do not author their own scale.
            if (FontPage is { Length: > 0 } page)
            {
                try { ScaleFonts(content, Services.PanelFonts.Scale(page)); } catch { }
            }
            _built[index] = content;
        }
        _scroller.Content = content;
        ContentRevision++;

        double target = scrollTo ?? 0;
        if (target <= 0.5) { _scroller.ChangeView(null, 0, null, true); return; }
        // The scroller has no extent until the new content has been measured, so
        // a ChangeView issued before that silently clamps to zero. Measure first,
        // then ask again on the next layout pass as the belt to that brace.
        try { _scroller.UpdateLayout(); } catch { }
        try { _scroller.ChangeView(null, target, null, true); } catch { }
        void Once(object? s, object e)
        {
            _scroller.LayoutUpdated -= Once;
            try { _scroller.ChangeView(null, target, null, true); } catch { }
        }
        _scroller.LayoutUpdated += Once;
    }

    // =======================================================================
    // Show / hide / placement
    // =======================================================================

    public void Toggle()
    {
        if (IsOpen) Hide(); else Show();
    }

    public void Show()
    {
        try { _popup.XamlRoot = _host.XamlRoot; ActiveRoot = _host.XamlRoot; } catch { }
        // A popup lives OUTSIDE the RootGrid subtree, so it does not inherit the
        // ElementTheme ApplyTheme sets there. Repaint from the CURRENT ground and
        // stamp the resolved theme onto the panel explicitly, or a window opened
        // on a Blueprint page would come up wearing the last page's palette.
        PaintPanel();
        // Resolve against the CURRENT host on every open. The host may be a
        // different size and in a different place than it was last time
        // (fullscreen toggled while this was closed), and the position is stored
        // as an inset from the host's own edge rather than in absolute
        // coordinates — so this is the SAME one line the host-change path runs.
        // No re-anchor arm and no `KeepsOwnPosition` test: a window nobody has
        // moved has null insets and lands in `OpenOn`'s corner, and one the user
        // dragged comes back at the distance they dragged it to (§15.4e).
        ApplyGeometry();
        if (_scroller.Content == null) ShowTab(_active);
        _popup.IsOpen = true;
        // The window is made visible OUTRIGHT and only then animated: a fade that
        // starts from Opacity 0 and whose storyboard silently fails to run leaves
        // an invisible window, which is indistinguishable from a broken feature.
        _panel.Opacity = 1;
        try
        {
            var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.35,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, _panel);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
            // held in a field: an unrooted storyboard can be collected mid-run
            _anim = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            _anim.Children.Add(fade);
            _anim.Completed += (_, _) => _panel.Opacity = 1;
            _anim.Begin();
        }
        catch { _panel.Opacity = 1; }
    }

    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _anim;

    public void Hide()
    {
        if (!IsOpen) return;
        _popup.IsOpen = false;
        Closed?.Invoke();
    }

    /// <summary>WHICH SIDE THIS WINDOW'S STORED DISTANCE IS MEASURED FROM. The
    /// export pane keeps the reference's right edge; the Objects library opens on
    /// the LEFT (UI-SPEC-V3 L), which is where Concepts puts it.
    ///
    /// <para>Not "only the first placement" any more. Under §15.4e the window
    /// remembers an INSET from this side, so this names the edge it holds its
    /// distance from for as long as it exists — and the edge a resize therefore
    /// does not move. A window the user drags still keeps its own position; what
    /// their drag changes is the NUMBER, not which side it is measured
    /// against.</para></summary>
    public enum Side { Right, Left }

    public Side OpenOn { get; set; } = Side.Right;

    // The reference position — anchored to an edge, directly below the top bar,
    // §11.6 item 42's "they open as high as possible" — is what the DEFAULT
    // insets come to, so there is no separate "place anchored" step to call.
    // `PlaceAnchored` was that step, and `Show` and `HostGeometryChanged` each
    // decided for themselves whether to run it; both now simply resolve.

    /// <summary>THE ONE RULE: the rendered rect is the stored intent resolved
    /// against the CURRENT host. Nothing is shifted by a delta, nothing
    /// accumulates, and nothing is re-anchored as a special case.
    ///
    /// <para>PURE — it reads <see cref="_insetSide"/>, <see cref="_insetTop"/>,
    /// <see cref="_wantW"/> and <see cref="_wantH"/> and writes none of them. That
    /// is what makes the clamping NON-DESTRUCTIVE: the window can be pulled into a
    /// host that cannot hold it without losing the geometry it is being pulled
    /// away from, so it returns to it exactly when the room comes back.</para>
    ///
    /// <para>Returns false when the host has not been measured yet. No one-shot
    /// re-try handler is needed for that (<c>FirstPlacement</c> was one): the
    /// permanent <c>SizeChanged</c> subscription fires the moment ActualWidth
    /// stops being zero, which is the same event the one-shot waited for.</para>
    ///
    /// <para>The ORDER is the order <c>Constrain</c> used and for the same reason:
    /// SIZE first, then the position derived from the insets against THAT size,
    /// then the position clamp. Deriving the position from the wanted width while
    /// rendering a clamped one would put the anchored edge in the wrong place and
    /// leave the position clamp to rescue it — which is exactly the "clamp
    /// standing in for the rule" that §15.4c had to unpick.</para></summary>
    private bool Resolve(out double left, out double top, out double w, out double h)
    {
        left = top = w = h = 0;
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return false;

        (w, h) = ConstrainSize(_wantW, _wantH);
        double side = _insetSide ?? EdgeGap;
        left = OpenOn == Side.Left ? side : hostW - w - side;
        top = _insetTop ?? TopBand;
        (left, top) = ConstrainPosition(left, top, w, h);
        return true;
    }

    /// <summary>Push the resolved rect at the popup — the only writer of the
    /// popup's offsets and of the panel's Width / Height.
    ///
    /// <para><see cref="HostOrigin"/> is read FRESH here rather than differenced
    /// against a remembered one. The offsets are absolute in the popup's space and
    /// every inset is relative to the host, so the origin is needed on every
    /// apply — but only as the current translation, never as a baseline. That is
    /// what retired <c>_lastOrg</c>: there is no delta to take.</para></summary>
    private void ApplyGeometry()
    {
        if (!Resolve(out double left, out double top, out double w, out double h)) return;
        var org = HostOrigin;
        _panel.Width = w;
        _panel.Height = h;
        _popup.HorizontalOffset = org.X + left;
        _popup.VerticalOffset = org.Y + top;
    }

    /// <summary>The anchored-side inset that a rect of this width, with its left
    /// edge here, comes to. One place, because a drag and a resize have to agree on
    /// what "the distance from the side they're on" means.
    ///
    /// <para>Measured against the width PASSED IN — the width on screen — not
    /// against <see cref="_wantW"/>. The user set the distance they could see.</para></summary>
    private double SideInsetOf(double left, double w)
        => OpenOn == Side.Left ? left : _host.ActualWidth - w - left;

    /// <summary>The host moved or resized under the window — entering or leaving
    /// fullscreen being the case that matters.
    ///
    /// <para>ONE LINE, AND IT IS <see cref="Show"/>'S LINE. Both paths used to
    /// decide for themselves whether to shift the old offsets or re-anchor to the
    /// corner, and §15.4c had to introduce a shared predicate to stop them drifting
    /// apart. There is nothing left for either to decide: the insets say where the
    /// window goes, and the current host says how much room there is.</para>
    ///
    /// <para>No <c>!_placed</c> early return either. A window that has never been
    /// positioned has no offsets left to corrupt — it has NULL insets, which
    /// resolve to the anchored corner of whatever host is up, which is exactly
    /// where it should open.</para>
    ///
    /// <para>Nor is this the fullscreen path only. An auto-placed panel re-anchors
    /// on every host size change including an ordinary drag of the window border,
    /// and a user-placed one holds its distance through the same — §15.4c named
    /// that consequence for its re-anchoring arm and it is unchanged here, because
    /// it is the rule rather than a side effect of it.</para></summary>
    private void HostGeometryChanged(object sender, SizeChangedEventArgs e)
    {
        try { ApplyGeometry(); } catch { }
    }

    private void MoveBy(double dx, double dy)
    {
        // Dragged from what the user can SEE, which is the resolved rect and not
        // the stored intent: when the host is too small to honour the insets the
        // window renders clamped, and a drag that started from the unreachable
        // stored position would jump on the first delta.
        if (!Resolve(out double left, out double top, out double w, out double h)) return;
        var (nl, nt) = ConstrainPosition(left + dx, top + dy, w, h);

        // Clamped, and the CLAMPED value is what gets banked, so a drag into the
        // page edge remembers the inset it actually reached. Storing the raw target
        // instead would give the drag a dead zone — shove 200 DIP past the edge and
        // the first 200 DIP back would move nothing — which is also what the old
        // in-place `Constrain()` did, so this is not a change of feel.
        //
        // THE SIZE IS NOT TOUCHED. A move is not a resize. Banking the rendered
        // size here would let a drag in a windowed host quietly discard the height
        // the user chose in a fullscreen one — the exact destruction §15.4e's
        // clamping rule exists to prevent, arriving through the drag handler
        // instead of through the clamp.
        //
        // And only the axis that actually MOVED is committed. A purely horizontal
        // drag in a host with no vertical slack would otherwise bank the clamped
        // top as though the user had chosen it, losing the one they chose when
        // there was room for it: the same destruction again, by the side door.
        if (nl != left) _insetSide = SideInsetOf(nl, w);
        if (nt != top) _insetTop = nt;
        ApplyGeometry();
    }

    // =======================================================================
    // Resize grips — iPadOS-style corner brackets + edge pills
    // =======================================================================
    /// <summary>§10.5 item 18: "Remove the side and top resize handles. Corner
    /// grips only — the corners already resize." The four edge pills were also
    /// the ones sitting over the header, where a grab meant to move the window
    /// resized it instead.</summary>
    private void BuildGrips()
    {
        // §11.6 item 41: no top, bottom or side EDGE handles — corners only.
        // §11.6 item 42 then takes the TOP two corners as well: a window that
        // opens as high as it is allowed to go has nothing to gain by growing
        // upward, and the top-left grip sat directly over the close button while
        // the top-right sat over the info button. Both remaining grips grow the
        // window away from the chrome it must not cover.
        AddCorner(HorizontalAlignment.Left, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNortheastSouthwest, -1, +1);
        AddCorner(HorizontalAlignment.Right, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthwestSoutheast, +1, +1);
    }

    // =======================================================================
    // §11.6 item 42 — the constraints every floating panel obeys
    // =======================================================================
    /// <summary>The largest this window may be on the current host: the page
    /// minus its edge margins, and minus the top-bar band it may not enter.
    ///
    /// <para>THE TOP BAND IS "ANOTHER PANEL ELEMENT" — it is the top bar's two
    /// clusters, and it is the one such element a floating window can currently
    /// meet. Any further reserved region (a dock, a second floating panel) belongs
    /// in this method and in <see cref="ConstrainPosition"/> beside it, and
    /// inherits the non-destructive behaviour for free: everything here is applied
    /// to a candidate rect on its way to the screen and never written back over
    /// the stored intent.</para></summary>
    private (double W, double H) MaxSize()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return (double.PositiveInfinity, double.PositiveInfinity);
        return (Math.Max(MinW, hostW - EdgeGap * 2),
                Math.Max(MinH, hostH - TopBand - EdgeGap));
    }

    /// <summary>The room clamp, applied to a SIZE and returned. Floored at
    /// <c>MinW</c> / <c>MinH</c> rather than at the room, because a host smaller
    /// than the window's minimum leaves the minimum winning — the window hangs
    /// over the margin rather than shrinking to nothing.</summary>
    private (double W, double H) ConstrainSize(double w, double h)
    {
        var (maxW, maxH) = MaxSize();
        return (Math.Clamp(w, MinW, maxW), Math.Clamp(h, MinH, maxH));
    }

    /// <summary>The room clamp, applied to a POSITION against a size already
    /// through <see cref="ConstrainSize"/>, and returned.
    ///
    /// <para>This is <c>Constrain</c>'s safety role, unchanged in substance: the
    /// whole window on the page, inside its margins, below the chrome. §11.6 item
    /// 42 still holds on every frame. What changed is that the answer is RETURNED
    /// instead of being written over the window's memory of where it was, so the
    /// clamp can no longer be mistaken for the positioning rule — §15.4c found the
    /// old one standing in for exactly that, and working in only the one direction
    /// where the host shrinks.</para>
    ///
    /// <para>Each upper bound is floored at its own lower bound so the clamp stays
    /// well-formed on a host with no room at all: <c>Math.Clamp</c> throws when min
    /// exceeds max, and a 320 DIP minimum window on a narrower host reaches that
    /// case.</para></summary>
    private (double L, double T) ConstrainPosition(double left, double top, double w, double h)
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return (left, top);
        return (Math.Clamp(left, EdgeGap, Math.Max(EdgeGap, hostW - w - EdgeGap)),
                Math.Clamp(top, TopBand, Math.Max(TopBand, hostH - h - EdgeGap)));
    }

    private static SolidColorBrush GripBrush() => new(PageTheme.WithAlpha(PageTheme.OnSurface, 0x8C));

    private void AddCorner(HorizontalAlignment h, VerticalAlignment v, InputSystemCursorShape shape, int sx, int sy)
    {
        var grip = new Grip(shape)
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = h,
            VerticalAlignment = v,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        // a quarter-round bracket, mirrored into the right corner by the scale
        var mark = new Path
        {
            Data = ParseGeometry("M 2,14 A 12,12 0 0 1 14,2"),
            Stroke = GripBrush(),
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = sx < 0 ? 1 : -1, ScaleY = sy < 0 ? 1 : -1 },
        };
        grip.SetMark(mark);
        HookResize(grip, sx, sy);
        _gripLayer.Children.Add(grip);
    }

    // sx/sy: -1 = this grip moves the left/top edge (so the window origin moves
    // too), +1 = the right/bottom edge, 0 = that axis is not resized.
    private void HookResize(FrameworkElement grip, int sx, int sy)
    {
        grip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        grip.ManipulationDelta += (_, e) =>
        {
            // THE GRABBED EDGE MOVES; THE OPPOSITE EDGE HOLDS STILL. §11.6 items
            // 41 and 42 left only the two BOTTOM corners, so the edge held is
            // always the top and — on a right-anchored window — the right. The
            // panel therefore grows INWARD, and the distance from the side it is
            // anchored to survives the resize untouched: rule 3 of §15.4e. Nothing
            // here arranges for that. It falls out of the inset being the thing
            // stored, because a resize that leaves the anchored edge alone cannot
            // change a distance measured to that edge.
            //
            // A resize is the user choosing this geometry just as much as a drag
            // is, which is what `_userPlaced` was set here to record. The flag has
            // no readers left: the stored inset and wanted size now carry
            // everything it was consulted for, and they carry it as NUMBERS rather
            // than as a mode, which is why the two placement paths can no longer
            // disagree about which arm to take (§15.4c).
            if (!Resolve(out double left, out double top, out double w, out double h)) return;
            double left0 = left, top0 = top;
            double dx = e.Delta.Translation.X, dy = e.Delta.Translation.Y;

            if (sx < 0)
            {
                double nw = ConstrainSize(w - dx, h).W;
                left += w - nw;         // the left edge is the one under the pointer
                w = nw;
            }
            else if (sx > 0)
            {
                w = ConstrainSize(w + dx, h).W;
            }
            if (sy < 0)
            {
                double nh = ConstrainSize(w, h - dy).H;
                top += h - nh;
                h = nh;
            }
            else if (sy > 0)
            {
                h = ConstrainSize(w, h + dy).H;
            }

            // The size IS the intent here, and both arms already clamped it.
            _wantW = w;
            _wantH = h;
            var (nl, nt) = ConstrainPosition(left, top, w, h);
            if (nl != left0) _insetSide = SideInsetOf(nl, w);
            if (nt != top0) _insetTop = nt;
            ApplyGeometry();
        };
    }

    private void FadeGrips(double to)
    {
        try
        {
            var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, _gripLayer);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, "Opacity");
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            sb.Children.Add(a);
            sb.Begin();
        }
        catch { _gripLayer.Opacity = to; }
    }

    // A hit-target that owns a pointer cursor. ProtectedCursor is only reachable
    // from a derived type, which is the whole reason this class exists (and why
    // it derives from UserControl: the WinUI primitives are all sealed).
    private sealed class Grip : UserControl
    {
        private readonly Grid _hit = new() { Background = new SolidColorBrush(Colors.Transparent) };

        public Grip(InputSystemCursorShape shape)
        {
            // UserControl's ContentPresenter does not reliably paint Background,
            // and an unpainted element is not hit-testable — so the grip's whole
            // area is a transparent Grid that the mark is parented into.
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Content = _hit;
            try { ProtectedCursor = InputSystemCursor.Create(shape); } catch { }
        }

        public void SetMark(UIElement mark) => _hit.Children.Add(mark);
    }

    // =======================================================================
    // Vector chrome icons (never a glyph font, never an emoji)
    // =======================================================================
    /// <summary>Path-markup to Geometry. Shared with the window's tenants so the
    /// same authored vector icons can be used inside the content.</summary>
    internal static Geometry ParseGeometry(string data)
    {
        var p = (Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        var geo = p.Data;
        p.Data = null;   // a Geometry cannot be parented to two Paths at once
        return geo!;
    }

    /// <summary>The size the two corner marks are DRAWN at, on
    /// <see cref="Icons"/>' 24-unit grid.
    ///
    /// <para>Both marks used to be private literals in this file on a 16-unit
    /// grid of their own — the only marks in the app not in
    /// <see cref="Icons"/> and not on the shared grid — so they could not be
    /// rendered by the offline checker and could drift from every other mark
    /// without anything noticing. They are now <see cref="Icons.Close"/> and
    /// <see cref="Icons.Info"/>.</para>
    ///
    /// <para><b>The move changes their drawn size, and here are the numbers.</b>
    /// Close was 16-grid ink spanning 4..12 = 8.00 DIP with a 1.5 DIP stroke =
    /// <b>9.50</b> DIP outer; it is now 24-grid ink spanning 5..19 = 14 units at
    /// 16 DIP = 9.33 DIP with a 2.25-unit stroke that renders 1.50 DIP =
    /// <b>10.83</b> DIP outer, so <b>+1.33 DIP (+14.0%)</b>. Info was a 6.8-unit
    /// radius circle = 13.60 DIP plus a 1.5 DIP stroke = <b>15.10</b> DIP outer;
    /// it is now a filled 10.2-unit-radius ring at 16 DIP = <b>13.60</b> DIP,
    /// so <b>−1.50 DIP (−9.9%)</b>.</para>
    ///
    /// <para>The pair is what improves: 9.50 against 15.10 was a ratio of 0.63 —
    /// a small cross beside a much larger circle, in matching corners of the same
    /// header — and it is now 10.83 against 13.60, a ratio of 0.80. A circled
    /// glyph reading slightly larger than a bare cross at the same nominal size
    /// is correct; two-thirds is not.</para></summary>
    private const double CornerMarkSize = 16;
    /// <summary>Stroke for <see cref="Icons.Close"/> in GRID units — Icons.Mark
    /// scales it with the mark, so 2.25 units renders 2.25 x 16/24 = 1.50 DIP,
    /// which is exactly the weight the old 16-grid cross was stroked at.</summary>
    private const double CornerMarkStroke = 2.25;

    /// <summary>§17.5's corner target: a square filling one whole top corner of
    /// the panel, with ONE corner rounded to the panel's own inner radius and the
    /// other three square.
    ///
    /// <para><b>The shape is the HIT REGION, not just the paint.</b> The button
    /// carries a <see cref="Path"/> of exactly that outline with a TRANSPARENT
    /// fill, and has <c>Background = null</c> itself. A transparent fill hit-tests
    /// and a null background does not, so what responds is the Path's geometry and
    /// only that — the square fills its corner and still cannot protrude past the
    /// panel's curve, because outside the curve there is nothing to hit. That is
    /// what lets §17.5 have the shape §14.3 could not give it without
    /// overshooting.</para>
    ///
    /// <para>The hover and pressed plates come out right for free: WinUI's Button
    /// template paints them on a ContentPresenter that takes
    /// <c>CornerRadius</c> from a TemplateBinding, so setting the button's own
    /// corner radius to the same one-rounded-corner shape makes the lit plate that
    /// shape too. The Path is what bootstraps the hover — with no background there
    /// would be nothing to enter.</para>
    ///
    /// <para>The margins undo the header's padding exactly, putting the target
    /// flush against the panel's inner edge, and the side runs to the header's
    /// inner boundary. Nothing is nudged by an eyeballed offset.</para></summary>
    private Button CornerButton(string geometry, string tip, bool right, bool stroked, out ContentControl glyphHost)
    {
        double r = InnerRadius, s = CornerSide;
        // One rounded corner, three square. Built here rather than as a literal
        // because it is a function of the panel's radius and the header's height.
        string outline = right
            ? $"M 0,0 L {s - r},0 A {r},{r} 0 0 1 {s},{r} L {s},{s} L 0,{s} Z"
            : $"M 0,{r} A {r},{r} 0 0 1 {r},0 L {s},0 L {s},{s} L 0,{s} Z";

        var hit = new Path
        {
            Data = ParseGeometry(outline),
            // Transparent, NOT null: a null fill is invisible to hit testing and
            // this shape IS the target.
            Fill = new SolidColorBrush(Colors.Transparent),
            Stretch = Stretch.None,
        };
        glyphHost = new ContentControl
        {
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        var box = new Grid { Width = s, Height = s, Children = { hit, glyphHost } };

        var b = new Button
        {
            Width = s,
            Height = s,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Background = null,
            BorderThickness = new Thickness(0),
            CornerRadius = right ? new CornerRadius(0, r, 0, 0) : new CornerRadius(r, 0, 0, 0),
            HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Margin = right
                ? new Thickness(0, -HeadPadY, -HeadPadX, 0)
                : new Thickness(-HeadPadX, -HeadPadY, 0, 0),
            Content = box,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        PaintCornerMark(glyphHost, geometry, stroked);
        return b;
    }

    /// <summary>Re-inks a corner mark from <see cref="PageTheme"/>.
    ///
    /// <para>Called from <see cref="PaintPanel"/> rather than only at
    /// construction. The old marks took their stroke from a ONE-SHOT theme
    /// dictionary fetch, which is a Brush object assigned once — so a page change
    /// repainted the panel's ground, its title and its tab rule and left these two
    /// marks inked for the page before it. On a light-to-dark turn that is a mark
    /// the same colour as the plate it sits on.</para></summary>
    private static void PaintCornerMark(ContentControl host, string geometry, bool stroked) =>
        host.Content = Icons.Mark(geometry, PageTheme.OnSurface, CornerMarkSize,
                                  stroked, CornerMarkStroke);

    /// <summary>The XamlRoot the app is showing in; the window records it so the
    /// theme lookups below can read the LIVE root element.</summary>
    internal static XamlRoot? ActiveRoot { get; set; }

    /// <summary>Which resolution the app is showing, for the STOCK WinUI controls
    /// that only understand light and dark.
    ///
    /// <para>It is answered by <see cref="PageTheme"/> and nothing else. Walking
    /// the visual tree for RootGrid.RequestedTheme was an independent second
    /// answer to the same question, and the two could disagree for a whole frame
    /// after a page turn - or permanently, since the page-derived threshold and
    /// the byte-average one put Brown Paper on opposite sides.</para></summary>
    internal static ElementTheme Theme =>
        PageTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;

    /// <summary>"Default" (the dark dictionary) or "Light", for the manual theme
    /// dictionary lookups the code-built surfaces do.</summary>
    internal static string ThemeDictionaryKey => Theme == ElementTheme.Dark ? "Default" : "Light";

    // Resource lookups are done as BINDINGS-BY-ASSIGNMENT against the live
    // dictionaries: the panel is rebuilt on a theme change (RefreshContent), so a
    // one-shot fetch is enough and avoids a permanent ThemeResource subscription.
    private static void Bind(DependencyObject target, DependencyProperty prop, string key, bool theme)
    {
        try
        {
            object? res = null;
            if (theme)
            {
                string dict = ThemeDictionaryKey;
                if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dict, out var d) &&
                    d is ResourceDictionary rd && rd.TryGetValue(key, out var v)) res = v;
            }
            if (res == null && Application.Current.Resources.TryGetValue(key, out var g)) res = g;
            if (res != null) target.SetValue(prop, res);
        }
        catch { }
    }
}
