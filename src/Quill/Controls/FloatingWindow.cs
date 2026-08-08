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
    private const double Radius = 16;
    private const double HeaderH = 38;
    private const double GripThickness = 9;    // invisible hit band along each edge
    private const double MinW = 320, MinH = 260;

    private readonly Panel _host;
    // A POPUP, not an in-tree overlay: a popup is composited into the XamlRoot's
    // own popup layer, so it is guaranteed to float above the Win2D canvas, the
    // toolbars and the docked panels without depending on Z-index bookkeeping
    // inside a Grid it does not own.
    private readonly Popup _popup;
    private readonly Border _panel;            // the window itself
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
        PaintPanel();
        // The window survives page turns, so it follows the page rather than
        // freezing at the ground it was built on.
        PageTheme.Changed += OnGroundChanged;

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

        _title = new TextBlock
        {
            FontSize = 12.5,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(40, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        header.Children.Add(_title);
        shell.Children.Add(header);

        // ---- category divider row (the tabs)
        var tabRow = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 2, 14, 0),
        };
        Bind(tabRow, Border.BorderBrushProperty, "HairlineBrush", theme: true);
        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        tabRow.Child = _tabStrip;
        Grid.SetRow(tabRow, 1);
        shell.Children.Add(tabRow);

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
        try { _panel.RequestedTheme = Theme; } catch { }
    }

    private void OnGroundChanged()
    {
        PaintPanel();
        // The content captured the old palette at build time; throw it away.
        RefreshContent();
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
    public void RefreshContent()
    {
        _built.Clear();
        if (IsOpen) ShowTab(_active);
    }

    private void BuildTabStrip()
    {
        _tabStrip.Children.Clear();
        for (int i = 0; i < _tabs.Count; i++)
        {
            int idx = i;
            var label = new TextBlock
            {
                Text = _tabs[i].Label,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6),
                FontWeight = i == _active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Opacity = i == _active ? 1.0 : 0.6,
            };
            var underline = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = i == _active ? Visibility.Visible : Visibility.Collapsed,
            };
            Bind(underline, Border.BackgroundProperty, "BrandOrangeBrush", theme: false);

            var cell = new Grid { Margin = new Thickness(0, 0, 18, 0), Background = new SolidColorBrush(Colors.Transparent) };
            cell.Children.Add(label);
            cell.Children.Add(underline);
            cell.Tapped += (_, _) => ShowTab(idx);
            _tabStrip.Children.Add(cell);
        }
    }

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        _active = index;
        BuildTabStrip();
        if (!_built.TryGetValue(index, out var content))
        {
            try { content = _tabs[index].Build(); }
            catch { content = new TextBlock { Text = "This section could not be built." }; }
            _built[index] = content;
        }
        _scroller.Content = content;
        _scroller.ChangeView(null, 0, null, true);
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

    // Anchored to an edge, below the toolbars — the reference position.
    private void PlaceAnchored()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0)
        {
            // the layer has not been measured yet — place on the first arrange
            _host.SizeChanged += FirstPlacement;
            return;
        }
        _popup.HorizontalOffset = OpenOn == Side.Left
            ? 18
            : Math.Max(12, hostW - _panel.Width - 18);
        _popup.VerticalOffset = Math.Max(12, Math.Min(96, hostH - _panel.Height - 24));
        _placed = true;
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

    // A window dragged off-screen is a window the user has lost: always leave a
    // grabbable strip of the header visible.
    private void ClampIntoView()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW <= 0 || hostH <= 0) return;
        _popup.HorizontalOffset = Math.Clamp(_popup.HorizontalOffset, -_panel.Width + 120, hostW - 120);
        _popup.VerticalOffset = Math.Clamp(_popup.VerticalOffset, 0, Math.Max(0, hostH - HeaderH));
    }

    // =======================================================================
    // Resize grips — iPadOS-style corner brackets + edge pills
    // =======================================================================
    private void BuildGrips()
    {
        // corners: rounded brackets that echo the window's own 16px radius
        AddCorner(HorizontalAlignment.Left, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthwestSoutheast, -1, -1);
        AddCorner(HorizontalAlignment.Right, VerticalAlignment.Top, InputSystemCursorShape.SizeNortheastSouthwest, +1, -1);
        AddCorner(HorizontalAlignment.Left, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNortheastSouthwest, -1, +1);
        AddCorner(HorizontalAlignment.Right, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthwestSoutheast, +1, +1);
        // edges: short pills at the midpoints
        AddEdge(HorizontalAlignment.Stretch, VerticalAlignment.Top, InputSystemCursorShape.SizeNorthSouth, 0, -1);
        AddEdge(HorizontalAlignment.Stretch, VerticalAlignment.Bottom, InputSystemCursorShape.SizeNorthSouth, 0, +1);
        AddEdge(HorizontalAlignment.Left, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast, -1, 0);
        AddEdge(HorizontalAlignment.Right, VerticalAlignment.Stretch, InputSystemCursorShape.SizeWestEast, +1, 0);
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

    private void AddEdge(HorizontalAlignment h, VerticalAlignment v, InputSystemCursorShape shape, int sx, int sy)
    {
        bool horizontal = sx == 0;
        var grip = new Grip(shape)
        {
            Width = horizontal ? 64 : GripThickness,
            Height = horizontal ? GripThickness : 64,
            HorizontalAlignment = horizontal ? HorizontalAlignment.Center : h,
            VerticalAlignment = horizontal ? v : VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        grip.SetMark(new Border
        {
            Width = horizontal ? 34 : 3,
            Height = horizontal ? 3 : 34,
            CornerRadius = new CornerRadius(1.5),
            Background = GripBrush(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
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
            if (sx < 0)
            {
                double w = Math.Max(MinW, _panel.Width - dx);
                _popup.HorizontalOffset += _panel.Width - w;
                _panel.Width = w;
            }
            else if (sx > 0)
            {
                _panel.Width = Math.Max(MinW, _panel.Width + dx);
            }
            if (sy < 0)
            {
                double h = Math.Max(MinH, _panel.Height - dy);
                _popup.VerticalOffset += _panel.Height - h;
                _panel.Height = h;
            }
            else if (sy > 0)
            {
                _panel.Height = Math.Max(MinH, _panel.Height + dy);
            }
            ClampIntoView();
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
