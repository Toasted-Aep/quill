using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
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

    private readonly List<(string Label, Func<FrameworkElement> Build)> _tabs = new();
    private readonly Dictionary<int, FrameworkElement> _built = new();
    private int _active;
    private bool _placed;

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
        ActiveRoot ??= host.XamlRoot;

        _panel = new Border
        {
            Width = width,
            Height = height,
            MinWidth = MinW,
            MinHeight = MinH,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1.5),
        };
        Bind(_panel, Border.BorderBrushProperty, "GlassEdgeBrush", theme: false);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderH) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _panel.Child = shell;

        // ---- header: close (upper-left), drag bar (top middle), info (upper-right)
        var header = new Grid { Padding = new Thickness(8, 6, 8, 0) };

        var close = IconButton(CloseGeometry, "Close");
        close.HorizontalAlignment = HorizontalAlignment.Left;
        close.Click += (_, _) => Hide();
        header.Children.Add(close);

        var info = IconButton(InfoGeometry, "About these settings");
        info.HorizontalAlignment = HorizontalAlignment.Right;
        info.Click += (_, _) => InfoRequested?.Invoke();
        header.Children.Add(info);

        // The grab area is wider and taller than the visible bar so a pen or a
        // finger does not have to hit a 5px target.
        var grab = new Grid
        {
            Width = 132,
            Height = HeaderH - 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(grab, "Drag to move");
        var bar = new Border
        {
            Width = 52,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
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
        _host.SizeChanged += (_, _) => ClampIntoView();
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
        if (!_placed) PlaceAnchored();
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

    /// <summary>Which edge the window first appears against. The export pane
    /// keeps the reference's right edge; the Objects library opens on the LEFT
    /// (UI-SPEC-V3 L), which is where Concepts puts it. Only the FIRST placement
    /// uses this — once the user drags the window, its own position wins.</summary>
    public enum Side { Right, Left }

    public Side OpenOn { get; set; } = Side.Right;

    // Anchored to an edge, directly below the top bar — the reference position,
    // and §11.6 item 42's "they open as high as possible".
    private void PlaceAnchored()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0)
        {
            // the layer has not been measured yet — place on the first arrange
            _host.SizeChanged += FirstPlacement;
            return;
        }
        var (maxW, maxH) = MaxSize();
        _panel.Width = Math.Min(_panel.Width, maxW);
        _panel.Height = Math.Min(_panel.Height, maxH);

        _popup.HorizontalOffset = OpenOn == Side.Left
            ? EdgeGap
            : Math.Max(EdgeGap, hostW - _panel.Width - EdgeGap);
        _popup.VerticalOffset = TopBand;
        _placed = true;
        Constrain();
    }

    private void FirstPlacement(object sender, SizeChangedEventArgs e)
    {
        _host.SizeChanged -= FirstPlacement;
        PlaceAnchored();
    }

    private void MoveBy(double dx, double dy)
    {
        _popup.HorizontalOffset += dx;
        _popup.VerticalOffset += dy;
        _placed = true;
        ClampIntoView();
    }

    /// <summary>§11.6 item 42. This used to let the window hang most of the way
    /// off the page as long as 120 DIP of header stayed grabbable, and to sit at
    /// y = 0 — straight over the top bar's two clusters. It now keeps the whole
    /// window on the page, inside its margins, and below the chrome.</summary>
    private void ClampIntoView() => Constrain();

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
    /// minus its edge margins, and minus the top-bar band it may not enter.</summary>
    private (double W, double H) MaxSize()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return (double.PositiveInfinity, double.PositiveInfinity);
        return (Math.Max(MinW, hostW - EdgeGap * 2),
                Math.Max(MinH, hostH - TopBand - EdgeGap));
    }

    /// <summary>Brings the window inside every constraint at once — size first,
    /// then position, because clamping a position against a size that is itself
    /// out of bounds gives the wrong answer.</summary>
    private void Constrain()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return;

        var (maxW, maxH) = MaxSize();
        if (_panel.Width > maxW) _panel.Width = maxW;
        if (_panel.Height > maxH) _panel.Height = maxH;

        double left = Math.Max(EdgeGap, hostW - _panel.Width - EdgeGap);
        _popup.HorizontalOffset = Math.Clamp(_popup.HorizontalOffset, EdgeGap, left);
        double top = Math.Max(TopBand, hostH - _panel.Height - EdgeGap);
        _popup.VerticalOffset = Math.Clamp(_popup.VerticalOffset, TopBand, top);
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
            double dx = e.Delta.Translation.X, dy = e.Delta.Translation.Y;
            var (maxW, maxH) = MaxSize();
            if (sx < 0)
            {
                double w = Math.Clamp(_panel.Width - dx, MinW, maxW);
                _popup.HorizontalOffset += _panel.Width - w;
                _panel.Width = w;
            }
            else if (sx > 0)
            {
                _panel.Width = Math.Clamp(_panel.Width + dx, MinW, maxW);
            }
            _ = maxH;   // used by the vertical arm below
            if (sy < 0)
            {
                double h = Math.Clamp(_panel.Height - dy, MinH, maxH);
                _popup.VerticalOffset += _panel.Height - h;
                _panel.Height = h;
            }
            else if (sy > 0)
            {
                _panel.Height = Math.Clamp(_panel.Height + dy, MinH, maxH);
            }
            Constrain();
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

    private const string CloseGeometry = "M 4,4 L 12,12 M 12,4 L 4,12";
    private const string InfoGeometry =
        "M 8,1.2 A 6.8,6.8 0 1 1 7.99,1.2 Z M 8,6.9 L 8,12.2 M 8,3.7 L 8,4.9";

    private static Button IconButton(string geometry, string tip)
    {
        var p = new Path
        {
            Data = ParseGeometry(geometry),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
        };
        Bind(p, Shape.StrokeProperty, "InkBrush", theme: true);
        var b = new Button
        {
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = p,
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

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
