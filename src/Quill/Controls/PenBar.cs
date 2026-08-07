using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// The pen row — CONCEPTS-REF-2026-08-07 §2, the "Bar" palette. The alternative
/// tool surface to the radial dial, chosen in Settings ▸ Tool Setup ▸ Interface
/// and switched by <see cref="ToolSurfaceService"/>.
///
/// ── WHAT IT IS ───────────────────────────────────────────────────────────
/// A VERTICAL rounded panel, radius 16, ~86 DIP wide, one ~86 DIP cell per
/// tool. Each cell is the tool's stroke silhouette (~34 DIP) with its size
/// label beneath. The reference marks the active cell in a way that is easy to
/// get wrong and worth stating plainly: it is a FULL-CELL-WIDTH 2 DIP RULE IN
/// OnSurface, DRAWN BETWEEN THE SILHOUETTE AND THE LABEL. No fill, no tint, no
/// selection halo. A tool with no size shows the silhouette alone.
///
/// Undo floats BELOW the panel, outside it, with the same bare treatment as the
/// dial's satellites (§1.6) — it is not a cell and has no background.
///
/// ── §2.1 THE ATTACHED SETTINGS POPOVER ───────────────────────────────────
/// A second, narrower panel docked to the right of the FIRST cell, one step
/// darker than the bar, stacking size / opacity / smoothness / colour. It uses
/// the same glyphs as the dial's inner disc and the same
/// <see cref="ValuePopover"/>, so the two surfaces cannot drift: change a
/// number in one and the other reads it back identically.
///
/// ── WHY A NEW CONTROL ────────────────────────────────────────────────────
/// The old pen row was a horizontal Border in MainWindow.xaml with its own
/// colour button, its own collapse chip and its own dock logic. §2 describes a
/// different object in a different orientation with a different selection cue
/// and an attached panel the old one never had, so this is a rebuild rather
/// than a restyle. It is code-built for the same reason the dial is: the cells
/// are data-driven from the library's pens, and there is no XAML that can be
/// authored once for a list whose length the user controls.
/// </summary>
public sealed class PenBar
{
    // §2: "Width ~86 DIP", "One cell per tool, ~86 DIP tall".
    private const double BarW = 86;
    private const double CellH = 86;
    private const double MarkBox = 34;          // §2 "the stroke silhouette (~34 DIP)"
    private const double LabelSize = 13;        // §2 "the size label in 13 DIP beneath"
    private const double RuleH = 2;             // §2 the active cell's rule
    private const double Radius = 16;

    // §2.1 the attached settings popover.
    private const double SetW = 96;
    private const double SetRadius = 14;
    private const double SetGlyph = 24;   // see ToolWheel.SetBox: marks are no longer stretched
    private const double DotSize = 34;          // §2.1 "the colour dot (filled, ~34 DIP)"

    private const double SatSize = 30;          // undo, same treatment as §1.6
    private const int TapMs = 450;
    private const double TapSlop = 8;

    private enum Prop { Size = 0, Opacity = 1, Smooth = 2 }

    /// <summary>Everything the bar needs from MainWindow. Deliberately the same
    /// shape as <see cref="ToolWheel.Host"/> so MainWindow builds one object and
    /// hands it to whichever surface is up.</summary>
    public sealed class Host
    {
        public required Func<Library> Library { get; init; }
        public required Func<Guid?> ActivePreset { get; init; }
        public required Func<string> ToolTag { get; init; }
        public required Action<PenPreset> ApplyPreset { get; init; }
        public required Action<string> SelectTool { get; init; }
        public required Func<bool> ReduceMotion { get; init; }
        public required Action Save { get; init; }
    }

    /// <summary>The COPIC wheel, wired exactly as the dial's is.</summary>
    public Action<Point, Color, Action<Color>, Action?>? ColourPickerHook { get; set; }

    /// <summary>The top-bar element keys the bar has taken over, so a tool that
    /// lives here is not also offered on the bar above.</summary>
    public event Action<IReadOnlySet<string>>? OwnedKeysChanged;

    private readonly Grid _hostGrid;
    private readonly InkSurface _surface;
    private readonly Host _h;

    private readonly Grid _layer;
    private readonly StackPanel _cells = new();
    // The bar is a LIST of the user's own pens plus the tool cells, so its
    // natural height is however many pens they have - twelve of them is 1032 DIP
    // and runs off the bottom of any window, taking undo with it. The cells
    // scroll inside a panel capped to the viewport instead.
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Auto,
    };
    private readonly Border _bar;
    private readonly StackPanel _setRows = new() { Spacing = 0 };
    private readonly Border _settings;
    private readonly Canvas _undoArt = new();
    private readonly Border _undoHit;
    private readonly ValuePopover _popover = new();

    private bool _on;
    private double _scale = 1;
    private HashSet<string> _taken = new(StringComparer.Ordinal);
    private readonly List<string> _ids = new();

    private bool _want;
    private readonly HashSet<string> _blocks = new(StringComparer.Ordinal);

    /// <summary>Headroom the dock leaves at the top, in DIPs. The floating
    /// chrome bar sits above the tool surface, and without this the bar's first
    /// cell and its attached settings panel are drawn UNDER it - the chrome has
    /// the higher z-index, so it wins. ChromeBars pushes its measured height
    /// here exactly as it does for the dial.</summary>
    public double TopInset
    {
        get => _topInset;
        set
        {
            if (Math.Abs(_topInset - value) < 0.5) return;
            _topInset = value;
            if (_on) Place();
        }
    }
    private double _topInset;

    /// <summary>A host predicate that vetoes the bar. Same contract as
    /// <see cref="ToolWheel.IsBlocked"/> — the notebook gallery and the floating
    /// Notebooks window (V3 K.1, K.6) apply to BOTH surfaces, and a fix that
    /// only covered the dial would simply move the defect.</summary>
    public Func<bool>? IsBlocked { get; set; }

    public static PenBar Attach(Grid host, InkSurface surface, Host h) => new(host, surface, h);

    private PenBar(Grid host, InkSurface surface, Host h)
    {
        _hostGrid = host; _surface = surface; _h = h;

        _cells.Orientation = Orientation.Vertical;
        _scroll.Content = _cells;
        _bar = new Border
        {
            Child = _scroll,
            Width = BarW,
            CornerRadius = new CornerRadius(Radius),   // §2 radius 16
            BorderThickness = new Thickness(1),        // §2 hairline Outline
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_bar, "Tool bar");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_bar, "PenBar");

        _settings = new Border
        {
            Child = _setRows,
            Width = SetW,
            CornerRadius = new CornerRadius(SetRadius),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_settings, "PenBarSettings");

        // §2: undo floats BELOW the panel, outside it — a bare mark, no cell.
        _undoArt.Width = _undoArt.Height = SatSize;
        _undoArt.IsHitTestVisible = false;
        _undoHit = new Border
        {
            Child = _undoArt,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _undoHit.PointerPressed += (_, e) => e.Handled = true;
        _undoHit.PointerReleased += (_, e) => { e.Handled = true; if (_on) { _surface.Undo(); Refresh(); } };

        _layer = new Grid { Visibility = Visibility.Collapsed };
        Canvas.SetZIndex(_layer, 60);
        _layer.Children.Add(_bar);
        _layer.Children.Add(_settings);
        _layer.Children.Add(_undoHit);
        _popover.ValueChanged += Refresh;
        _layer.Children.Add(_popover.Element);
        _hostGrid.Children.Add(_layer);

        _hostGrid.SizeChanged += (_, _) => { if (_on) Place(); };
        PageTheme.Changed += () => { if (_on) Refresh(); };
        _surface.UndoManager.Changed += Refresh;
        ToolSurfaceService.Changed += _ => Apply();

        _hostGrid.Loaded += (_, _) => Place();
        if (_hostGrid.IsLoaded) Place();
    }

    // ===================================================================
    // Visibility - identical contract to the dial's, and for the same reason
    // ===================================================================
    public void SetVisible(bool on) { _want = on; Apply(); }

    public void Block(string reason, bool on)
    {
        if (on ? !_blocks.Add(reason) : !_blocks.Remove(reason)) return;
        Apply();
    }

    private bool Wanted =>
        _want
        && ToolSurfaceService.Current == ToolSurface.Bar
        && _blocks.Count == 0
        && !(IsBlocked?.Invoke() ?? false);

    public void Apply()
    {
        bool on = Wanted;
        if (_on == on) { if (on) { Place(); Refresh(); } return; }
        _on = on;
        if (!on) { Shut(); return; }
        _layer.Visibility = Visibility.Visible;
        _taken = new HashSet<string>(StringComparer.Ordinal) { " " };
        Place();
        Refresh();
        Slide();
    }

    private void Shut()
    {
        _popover.Close();
        _taken = new HashSet<string>(StringComparer.Ordinal);
        OwnedKeysChanged?.Invoke(_taken);
        _layer.Visibility = Visibility.Collapsed;
    }

    /// <summary>The same last line of defence the dial has: every painting entry
    /// point re-consults the vetoes, so no future caller can put the bar over
    /// the gallery by adding one more SetVisible(true).</summary>
    private bool Enforce()
    {
        if (!_on) return false;
        if (Wanted) return true;
        _on = false;
        Shut();
        return false;
    }

    private void Slide()
    {
        _bar.Opacity = 1;
        if (_h.ReduceMotion()) return;
        try
        {
            var sb = new Storyboard();
            var anim = new DoubleAnimation
            {
                From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(anim, _layer);
            Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
            sb.Begin();
        }
        catch { }
    }

    // ===================================================================
    // Placement
    // ===================================================================
    private void Place()
    {
        if (!Enforce()) return;
        double w = _hostGrid.ActualWidth, h = _hostGrid.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _scale = _h.Library().TouchMode ? 1.1 : 1.0;

        bool right = string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase);
        const double pad = 14;
        double top = pad + _topInset;
        // Room for undo below the panel, and never taller than what is left.
        double room = Math.Max(CellH, h - top - pad - (SatSize + 24));
        _cells.Measure(new Size(BarW, double.PositiveInfinity));
        double barH = Math.Clamp(_cells.DesiredSize.Height, CellH, room);
        _bar.Height = barH;

        double x = right ? Math.Max(pad, w - BarW - pad) : pad;
        double y = Math.Clamp(top + (h - top - barH) / 2, top, Math.Max(top, h - barH - pad - (SatSize + 24)));
        _bar.Margin = new Thickness(x, y, 0, 0);

        // §2.1: docked to the RIGHT of the first cell. On a right-hand dock that
        // would run off the window, so it goes to the left instead.
        double sx = right ? x - SetW - 8 : x + BarW + 8;
        _settings.Margin = new Thickness(Math.Clamp(sx, 4, Math.Max(4, w - SetW - 4)), y, 0, 0);

        // Undo, below the panel and outside it, centred on the bar's axis.
        double us = SatSize + 12;
        _undoHit.Margin = new Thickness(x + (BarW - us) / 2, y + barH + 8, 0, 0);

        PlacePopover();
    }

    /// <summary>The value popover opens beside the settings panel, clear of both
    /// panels, so it never covers the row it is describing.</summary>
    private void PlacePopover()
    {
        if (!_popover.IsOpen) return;
        bool right = string.Equals(_h.Library().PenDock, "Right", StringComparison.OrdinalIgnoreCase);
        double bx = _bar.Margin.Left, by = _bar.Margin.Top;
        double x = right ? bx - SetW - 12 - ValuePopover.W : bx + BarW + SetW + 16;
        _popover.Place(new Point(x, by + 8), _hostGrid.ActualWidth, _hostGrid.ActualHeight);
    }

    // ===================================================================
    // Paint
    // ===================================================================
    public void Refresh()
    {
        if (!Enforce()) return;
        var lib = _h.Library();
        _scale = lib.TouchMode ? 1.1 : 1.0;

        var onSurface = PageTheme.OnSurface;
        var muted = PageTheme.OnSurfaceMuted;

        _bar.Background = new SolidColorBrush(PageTheme.Surface);
        _bar.BorderBrush = new SolidColorBrush(PageTheme.Outline);
        // §2.1: "shaded one step darker than the bar" - which is exactly what
        // SurfaceAlt is for, and it stays right on a blue or a kraft page where
        // a hard-coded darker grey would not.
        _settings.Background = new SolidColorBrush(PageTheme.SurfaceAlt);

        // ---- the cells -------------------------------------------------
        _ids.Clear();
        _ids.AddRange(ResolveTools());
        _cells.Children.Clear();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _ids.Count; i++)
        {
            string id = _ids[i];
            _cells.Children.Add(BuildCell(id, IsActive(id), onSurface, muted));
            if (TopBarKey(id) is { } key) taken.Add(key);
        }
        if (!taken.SetEquals(_taken)) { _taken = taken; OwnedKeysChanged?.Invoke(_taken); }

        // ---- §2.1 the attached settings popover ------------------------
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        _setRows.Children.Clear();
        _setRows.Children.Add(SettingRow(Prop.Size, Icons.Size, false,
            Enabled(Prop.Size) ? (eraser ? (lib.EraserSize <= 0 ? Loc.T("Wheel.Auto") : $"{lib.EraserSize:0} px") : $"{ap!.Size:0.#} px") : "-",
            Enabled(Prop.Size), onSurface, muted));
        _setRows.Children.Add(SettingRow(Prop.Opacity, Icons.Opacity, false,
            Enabled(Prop.Opacity) ? $"{ap!.Opacity * 100:0}%" : "-", Enabled(Prop.Opacity), onSurface, muted));
        _setRows.Children.Add(SettingRow(Prop.Smooth, Icons.Smoothness, true,
            Enabled(Prop.Smooth) ? $"{ap!.Stabiliser * 100:0}%" : "-", Enabled(Prop.Smooth), onSurface, muted));
        _setRows.Children.Add(ColourRow());

        // ---- undo ------------------------------------------------------
        _undoArt.Children.Clear();
        _undoArt.Children.Add(Icons.Mark(Icons.Undo,
            _surface.UndoManager.CanUndo ? onSurface : PageTheme.WithAlpha(onSurface, 77), SatSize));

        _popover.Sync();
        Place();
    }

    /// <summary>One cell: silhouette, the active rule, then the size label.
    /// §2's selection cue in full — the rule is between the two, full cell
    /// width, 2 DIP, OnSurface, and the cell has no fill of its own at all.</summary>
    private FrameworkElement BuildCell(string id, bool active, Color onSurface, Color muted)
    {
        var pen = PenOf(id);
        var stack = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };

        var art = Art(id, pen, onSurface);
        if (art != null)
        {
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(art);
        }

        // The rule occupies its height whether or not it is drawn, so selecting a
        // tool does not shuffle every cell below it by 2 DIP.
        stack.Children.Add(new Rectangle
        {
            Height = RuleH,
            Width = BarW,
            Fill = new SolidColorBrush(active ? onSurface : Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        });

        // §2: "Tools without a size show the silhouette alone."
        if (pen != null)
            stack.Children.Add(new TextBlock
            {
                Text = pen.Size.ToString("0.#"),
                FontSize = LabelSize,
                FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = new SolidColorBrush(active ? onSurface : muted),
                HorizontalAlignment = HorizontalAlignment.Center,
            });

        var cell = new Border
        {
            Child = stack,
            Height = CellH,
            Background = new SolidColorBrush(Colors.Transparent),   // hit-testable, never a fill
            IsTapEnabled = true,
        };
        Tap(cell, () => Commit(id), () => ShowAssign(cell, id));
        return cell;
    }

    /// <summary>§2.1 row: the dial's own glyph, its value, and a tap that opens
    /// the §1.7 popover — the same control the dial opens.</summary>
    private FrameworkElement SettingRow(Prop p, string glyph, bool stroked, string value,
                                        bool enabled, Color onSurface, Color muted)
    {
        var col = enabled ? onSurface : muted;
        var stack = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
        var mark = Icons.Mark(glyph, col, SetGlyph, stroked: stroked, thickness: 2.1);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(mark);
        stack.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(col),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var row = new Border
        {
            Child = stack,
            Padding = new Thickness(6, 9, 6, 9),
            Background = new SolidColorBrush(Colors.Transparent),
            IsTapEnabled = true,
            Opacity = enabled ? 1 : 0.55,
        };
        if (enabled) Tap(row, () => OpenPopover(p), null);
        return row;
    }

    private FrameworkElement ColourRow()
    {
        var dot = new Ellipse
        {
            Width = DotSize, Height = DotSize,
            Fill = new SolidColorBrush(ActiveColour()),
            Stroke = new SolidColorBrush(PageTheme.Outline),
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var row = new Border
        {
            Child = dot,
            Padding = new Thickness(6, 8, 6, 12),
            Background = new SolidColorBrush(Colors.Transparent),
            IsTapEnabled = true,
        };
        Tap(row, ShowColourPicker, null);
        return row;
    }

    /// <summary>Tap and press-hold on one element. Pointer events rather than a
    /// Button so a press cannot reach InkSurface and start a stroke behind the
    /// bar — the same reason the dial routes everything through its shield.</summary>
    private static void Tap(UIElement el, Action tap, Action? hold)
    {
        long down = 0;
        Point at = default;
        bool armed = false;
        el.PointerPressed += (s, e) =>
        {
            down = Environment.TickCount64;
            at = e.GetCurrentPoint((UIElement)s).Position;
            armed = true;
            e.Handled = true;
        };
        el.PointerReleased += (s, e) =>
        {
            e.Handled = true;
            if (!armed) return;
            armed = false;
            var p = e.GetCurrentPoint((UIElement)s).Position;
            double dx = p.X - at.X, dy = p.Y - at.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > TapSlop) return;
            if (hold != null && Environment.TickCount64 - down >= TapMs) { hold(); return; }
            tap();
        };
        el.PointerCanceled += (_, _) => armed = false;
        el.PointerCaptureLost += (_, _) => armed = false;
        if (hold != null && el is FrameworkElement fe)
            fe.RightTapped += (_, e) => { e.Handled = true; hold(); };
    }

    // ===================================================================
    // Contents
    // ===================================================================

    /// <summary>The bar's tools: every pen in the library, then the three
    /// tool-kind cells the reference shows without a size (eraser, selection,
    /// text). The dial's eight-slot assignment does not apply — the bar is a
    /// LIST and scrolls with the library rather than being a fixed ring.</summary>
    private IEnumerable<string> ResolveTools()
    {
        foreach (var p in _h.Library().Pens) yield return "pen:" + p.Id;
        yield return "tool:Eraser";
        yield return "tool:Select";
        yield return "tool:Text";
    }

    private PenPreset? PenOf(string id)
    {
        if (!id.StartsWith("pen:", StringComparison.Ordinal)) return null;
        return Guid.TryParse(id.AsSpan(4), out var g)
            ? _h.Library().Pens.FirstOrDefault(x => x.Id == g) : null;
    }

    private bool IsActive(string id)
    {
        if (PenOf(id) is { } pen) return _h.ToolTag() == "Pen" && _h.ActivePreset() == pen.Id;
        if (id.StartsWith("tool:", StringComparison.Ordinal)) return _h.ToolTag() == id[5..];
        return false;
    }

    private static string? TopBarKey(string id)
    {
        if (id.StartsWith("pen:", StringComparison.Ordinal)) return "ToolPen";
        return id[5..] switch
        {
            "Text" => "ToolText",
            "Select" => "ToolSelect",
            "FreeSpace" => "ToolSpace",
            _ => null,
        };
    }

    private FrameworkElement? Art(string id, PenPreset? pen, Color fg)
    {
        try
        {
            if (pen != null)
            {
                // The same stroke silhouette the dial draws, in the pen's own
                // colour, falling back only when the contrast genuinely collapses
                // against the bar's Surface fill.
                var ink = ColorUtil.Parse(pen.Color);
                var paint = Math.Abs(Lum(ink) - Lum(PageTheme.Surface)) < 0.14 ? fg : ink;
                paint.A = (byte)Math.Clamp(255 * Math.Clamp(pen.Opacity, 0.2f, 1f), 60, 255);
                return Icons.Mark(Icons.PenStroke(pen.Pen), paint, MarkBox);
            }
            return Icons.Mark(Icons.Tool(id[5..]), fg, MarkBox);
        }
        catch { return null; }
    }

    private static double Lum(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private void Commit(string id)
    {
        if (PenOf(id) is { } pen) { _h.ApplyPreset(pen); return; }
        if (id.StartsWith("tool:", StringComparison.Ordinal)) _h.SelectTool(id[5..]);
    }

    /// <summary>Press-hold or right-click a pen cell to edit the pen it holds —
    /// the bar's equivalent of the dial's slot-assignment flyout. A LIST cannot
    /// be re-ordered by assignment, so this offers the pen's own colour instead,
    /// which is what the reference's long-press does.</summary>
    private void ShowAssign(FrameworkElement at, string id)
    {
        if (PenOf(id) is not { } pen) return;
        var start = ColorUtil.Parse(pen.Color);
        void ApplyColour(Color c)
        {
            pen.Color = ColorUtil.ToHex(c);
            _h.ApplyPreset(pen);
            _h.Save();
        }
        if (ColourPickerHook != null)
        {
            ColourPickerHook(RootPointOf(at), start, ApplyColour, Refresh);
            return;
        }
        var picker = new ColorPicker { Color = start, IsAlphaEnabled = false, Width = 288 };
        picker.ColorChanged += (_, e) => ApplyColour(e.NewColor);
        new Flyout { Content = picker }.ShowAt(at);
    }

    // ===================================================================
    // The three properties - identical maths to the dial's, so the two
    // surfaces read the same numbers back
    // ===================================================================
    private PenPreset? ActivePen()
    {
        var id = _h.ActivePreset();
        return id == null ? null : _h.Library().Pens.FirstOrDefault(x => x.Id == id);
    }

    private PenPreset? ToolPen() => _h.ToolTag() == "Pen" ? ActivePen() : null;

    private Color ActiveColour() =>
        _h.ToolTag() == "Pen" && ActivePen() is { } p ? ColorUtil.Parse(p.Color) : PageTheme.Surface;

    private bool Enabled(Prop p)
    {
        var ap = ToolPen();
        return p == Prop.Size ? ap != null || _h.ToolTag() == "Eraser" : ap != null;
    }

    private static double Norm01(double v, double lo, double hi) => Math.Clamp((v - lo) / (hi - lo), 0, 1);

    private double Value(Prop p)
    {
        var lib = _h.Library();
        var ap = ToolPen();
        bool eraser = _h.ToolTag() == "Eraser";
        return p switch
        {
            Prop.Size => eraser ? Norm01(lib.EraserSize, 0, 80) : ap == null ? 0 : Norm01(ap.Size, 1, 24),
            Prop.Opacity => ap == null ? 0 : Math.Clamp(ap.Opacity, 0, 1),
            _ => ap?.Stabiliser ?? 0,
        };
    }

    private void ApplySetting(Prop p, double t)
    {
        if (!Enabled(p)) return;
        var lib = _h.Library();
        var ap = ToolPen();
        switch (p)
        {
            case Prop.Size when _h.ToolTag() == "Eraser":
                lib.EraserSize = Math.Round(t * 80);
                _surface.EraserSize = lib.EraserSize;
                break;
            case Prop.Size when ap != null:
                ap.Size = (float)Math.Round(1 + t * 23, 1);
                _surface.PenSize = ap.Size;
                break;
            case Prop.Opacity when ap != null:
                ap.Opacity = (float)Math.Round(Math.Max(0.05, t), 2);
                _surface.PenOpacity = ap.Opacity;
                break;
            case Prop.Smooth when ap != null:
                ap.Stabiliser = (float)Math.Round(t, 2);
                _surface.PenStabiliser = ap.Stabiliser;
                break;
            default: return;
        }
        _h.Save();
        Refresh();
    }

    private void OpenPopover(Prop p)
    {
        bool eraser = _h.ToolTag() == "Eraser";
        var spec = p switch
        {
            Prop.Size => new ValuePopover.Spec
            {
                Name = Loc.T("Wheel.Set.Size"),
                ToolName = ToolName(),
                Get = () => Value(Prop.Size),
                Set = t => ApplySetting(Prop.Size, t),
                Format = t => eraser ? $"{t * 80:0}" : $"{1 + t * 23:0.#}",
                Presets = eraser
                    ? new[] { 0.10, 0.25, 0.50, 1.00 }
                    : new[] { Norm01(1, 1, 24), Norm01(3, 1, 24), Norm01(8, 1, 24), Norm01(16, 1, 24) },
                Step = 1.0 / 46,
            },
            Prop.Opacity => Percent(Loc.T("Wheel.Set.Opacity"), Prop.Opacity),
            _ => Percent(Loc.T("Wheel.Set.Smoothness"), Prop.Smooth),
        };
        _popover.Open(spec, p.ToString(), new Point(0, 0), _hostGrid.ActualWidth, _hostGrid.ActualHeight);
        PlacePopover();

        ValuePopover.Spec Percent(string name, Prop pr) => new()
        {
            Name = name,
            ToolName = ToolName(),
            Get = () => Value(pr),
            Set = t => ApplySetting(pr, t),
            Format = t => $"{t * 100:0}%",
            Presets = new[] { 0.0, 0.5, 0.7, 1.0 },
            Step = 0.05,
        };
    }

    private string ToolName()
    {
        if (_h.ToolTag() == "Pen" && ActivePen() is { } p && !string.IsNullOrWhiteSpace(p.Name)) return p.Name;
        return Loc.T("Wheel.Tool." + _h.ToolTag());
    }

    private void ShowColourPicker()
    {
        var ap = ActivePen() ?? _h.Library().Pens.FirstOrDefault();
        if (ap == null) return;
        var start = ColorUtil.Parse(ap.Color);
        void ApplyColour(Color c)
        {
            ap.Color = ColorUtil.ToHex(c);
            _h.ApplyPreset(ap);
            _h.Save();
        }
        if (ColourPickerHook != null) { ColourPickerHook(SettingsCentre(), start, ApplyColour, Refresh); return; }
        var picker = new ColorPicker { Color = start, IsAlphaEnabled = false, Width = 288 };
        picker.ColorChanged += (_, e) => ApplyColour(e.NewColor);
        new Flyout { Content = picker }.ShowAt(_settings);
    }

    /// <summary>V3 K.9: with the dial off, the COPIC ring is centred on the
    /// control that opened it — here, the settings panel's colour dot.</summary>
    private Point SettingsCentre() => RootPointOf(_settings);

    private Point RootPointOf(FrameworkElement el)
    {
        try
        {
            var t = el.TransformToVisual((UIElement?)_hostGrid.XamlRoot?.Content ?? _hostGrid);
            return t.TransformPoint(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        }
        catch { return new Point(_bar.Margin.Left + BarW / 2, _bar.Margin.Top + CellH / 2); }
    }

    /// <summary>The bar's bounds in host coordinates, for the shared panel
    /// overlap solver — panel and settings together, since they move as one.</summary>
    public Rect Bounds
    {
        get
        {
            if (!_on) return new Rect(0, 0, 0, 0);
            double x = Math.Min(_bar.Margin.Left, _settings.Margin.Left);
            double r = Math.Max(_bar.Margin.Left + BarW, _settings.Margin.Left + SetW);
            double h = Math.Max(_bar.ActualHeight, _settings.ActualHeight);
            return new Rect(x, _bar.Margin.Top, r - x, h);
        }
    }
}
