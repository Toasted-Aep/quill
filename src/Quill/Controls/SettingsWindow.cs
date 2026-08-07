using Quill.Helpers;
using Quill.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// The SETTINGS TENANT of <see cref="FloatingWindow"/> — the Concepts-style
/// Workspace / Interaction surface. It owns no chrome of its own: the window
/// supplies the drag bar, the close and info buttons, the category divider and
/// the resize grips, and this class only builds the two tabs' content. A later
/// tool / pen-library panel plugs into the same window the same way.
///
/// <para><b>Workspace</b> carries the three collapsible sections from the
/// reference — Canvas, Artboard, Measurements — with the circular swatch rows
/// for the page background and the grid kind. Every swatch renders a REAL
/// preview: the paper swatches show a crop of the actual baked texture and the
/// grid swatches draw the actual grid pattern, both through
/// <see cref="PaperTextures"/>.</para>
///
/// <para><b>Interaction</b> hosts the whole of the existing settings panel, so
/// nothing that was reachable before this rebuild became unreachable after it.
/// MainWindow hands it over through <see cref="Host.FillLegacySettings"/>, which
/// is why the rebuild cost the contended MainWindow file only a handful of
/// lines.</para>
/// </summary>
public sealed class SettingsWindow
{
    public sealed class Host
    {
        public required Func<Library> Library { get; init; }
        public required Func<NotePage?> Page { get; init; }
        /// <summary>(paperId, backgroundHex). Either may be null: null paper = the
        /// plain-colour page, null hex = keep the page's colour.</summary>
        public required Action<string?, string?> SetPaper { get; init; }
        public required Action<FrameworkElement, Color, Action<Color>> PickColor { get; init; }
        public required Action<GridType> SetGrid { get; init; }
        public required Action<double> SetGridSpacing { get; init; }
        public required Action<string?> SetGridColor { get; init; }
        public required Action<PageSizePreset, double, double, PageSizeUnit> SetPageSize { get; init; }
        public required Action<bool> SetLandscape { get; init; }
        public required Action<double> SetUnitsPerInch { get; init; }
        public required Action ApplyTheme { get; init; }
        public required Action Save { get; init; }
        /// <summary>Touch draw — whether a finger marks or pans. It used to be a
        /// top-bar toggle; V3 K.14 moves it in here as an on/off switch.</summary>
        public required Func<bool> TouchDraw { get; init; }
        public required Action<bool> SetTouchDraw { get; init; }
        /// <summary>Fills the given panel with the pre-existing settings controls.</summary>
        public required Action<Panel> FillLegacySettings { get; init; }
        public required Action<string> Status { get; init; }
    }

    private const double SwatchDiameter = 52;
    private const double SwatchCell = 68;

    // ---- docked-panel geometry (docs/CONCEPTS-UI-REFERENCE.md 1.6) --------
    /// <summary>Measured: left edge x 2083 of 2880 physical -> 797 px -> 398.5
    /// DIP, flush to the window's right edge, full height below the title bar.</summary>
    private const double DockWidth = 398.5;
    /// <summary>The soft edge runs x 2020..2083 physical, i.e. about 31 DIP, and
    /// it is on the LEFT EDGE ONLY - which is one of the four reasons the
    /// reference concludes the panel is docked rather than floating.</summary>
    private const double DockShadow = 31;
    /// <summary>Sampled from an empty region of the panel.</summary>
    private static readonly Color DockBackground = Color.FromArgb(0xFF, 0x03, 0x03, 0x03);

    private readonly Host _h;

    // DOCKED, full stop (V3 K.13: "revert the settings floating window - docked
    // stays"). The floating variant is gone from this class: there is no flag,
    // no second code path and no way to get it back by accident. FloatingWindow
    // itself is untouched and still very much alive - the export pane and the
    // Objects library are its tenants.
    private readonly Grid _dock;

    private readonly (string Label, Func<FrameworkElement> Build)[] _tabs;
    private readonly StackPanel _dockTabs = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly ScrollViewer _dockBody = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(18, 10, 14, 18),
    };
    private int _active;

    /// <summary>The settings surface: DOCKED RIGHT, 398.5 DIP, #030303, full
    /// height below the title bar, soft edge on the left only — the measured
    /// reference (docs/CONCEPTS-UI-REFERENCE.md §1.6).</summary>
    public static SettingsWindow Attach(Panel host, Host h) => new(host, h);

    private SettingsWindow(Panel host, Host h)
    {
        _h = h;
        _tabs = new (string, Func<FrameworkElement>)[]
        {
            ("Workspace", BuildWorkspace),
            ("Interaction", BuildInteraction),
        };
        _dock = BuildDock();
        host.Children.Add(_dock);
    }

    // =======================================================================
    // The docked panel: flush right, full height, no title bar, no grips, and
    // a shadow on the LEFT EDGE ONLY.
    // =======================================================================
    private Grid BuildDock()
    {
        var root = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed,
        };
        Canvas.SetZIndex(root, 80);
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DockShadow) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DockWidth) });

        // Left-edge shadow. A fresh brush every build, never a mutated one:
        // WinUI caches GradientStop changes and only brush-level writes repaint.
        var shade = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, 0, 0, 0), Offset = 0 });
        shade.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x38, 0, 0, 0), Offset = 1 });
        var shadow = new Border { Background = shade, IsHitTestVisible = false };
        Grid.SetColumn(shadow, 0);
        root.Children.Add(shadow);

        var panel = new Border { Background = new SolidColorBrush(DockBackground) };
        Grid.SetColumn(panel, 1);
        root.Children.Add(panel);

        var shell = new Grid();
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Child = shell;

        // The tab row stays; the title bar and the resize grips do not.
        var tabRow = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 14, 14, 0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Child = _dockTabs,
        };
        shell.Children.Add(tabRow);

        Grid.SetRow(_dockBody, 1);
        shell.Children.Add(_dockBody);

        BuildDockTabs();
        ShowTab(0);
        return root;
    }

    private void BuildDockTabs()
    {
        _dockTabs.Children.Clear();
        for (int i = 0; i < _tabs.Length; i++)
        {
            int idx = i;
            var label = new TextBlock
            {
                Text = _tabs[i].Label,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF4, 0xF2, 0xEC)),
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
            try
            {
                if (Application.Current.Resources.TryGetValue("BrandOrangeBrush", out var b) && b is Brush br)
                    underline.Background = br;
            }
            catch { }
            var cell = new Grid { Margin = new Thickness(0, 0, 18, 0), Background = new SolidColorBrush(Colors.Transparent) };
            cell.Children.Add(label);
            cell.Children.Add(underline);
            cell.Tapped += (_, _) => ShowTab(idx);
            _dockTabs.Children.Add(cell);
        }
    }

    private void ShowTab(int index)
    {
        if (index < 0 || index >= _tabs.Length) return;
        _active = index;
        BuildDockTabs();
        FrameworkElement content;
        try { content = _tabs[index].Build(); }
        catch { content = new TextBlock { Text = "This section could not be built." }; }
        // The docked panel paints its own near-black ground, so its contents are
        // always the dark theme regardless of what the page has done to the app.
        content.RequestedTheme = ElementTheme.Dark;
        _dockBody.Content = content;
        _dockBody.ChangeView(null, 0, null, true);
    }

    public bool IsOpen => _dock.Visibility == Visibility.Visible;

    /// <summary>How much of the host's right edge this surface is covering, in
    /// DIPs. The bare status bar's right cluster shifts left by this much while
    /// the panel is docked open, so the Settings glyph that opened it stays
    /// clickable to close it again - a docked panel with no title bar has no
    /// close button of its own by design.</summary>
    public double OccupiedRightWidth => IsOpen ? DockWidth + DockShadow : 0;

    /// <summary>Raised after the docked panel opens or closes.</summary>
    public Action? DockChanged { get; set; }

    public void Toggle()
    {
        if (IsOpen) Hide(); else Show();
    }

    public void Show()
    {
        ShowTab(_active);
        _dock.Visibility = Visibility.Visible;
        DockChanged?.Invoke();
    }

    public void Hide()
    {
        _dock.Visibility = Visibility.Collapsed;
        DockChanged?.Invoke();
    }

    /// <summary>Rebuild after a theme change — this surface captures its colours at
    /// build time exactly like the tree, the pen strip and the gallery.</summary>
    public void Refresh()
    {
        if (IsOpen) ShowTab(_active);
    }

    // =======================================================================
    // WORKSPACE
    // =======================================================================
    private FrameworkElement BuildWorkspace()
    {
        var root = new StackPanel { Spacing = 6 };
        root.Children.Add(Section("Canvas", true, BuildCanvasSection()));
        root.Children.Add(Section("Artboard", false, BuildArtboardSection()));
        root.Children.Add(Section("Measurements", false, BuildMeasurementsSection()));
        return root;
    }

    private FrameworkElement BuildCanvasSection()
    {
        var panel = new StackPanel { Spacing = 4 };
        var page = _h.Page();

        // ---- Background -------------------------------------------------
        panel.Children.Add(Heading("Background"));
        panel.Children.Add(Caption("Standard paper or custom background color?"));
        panel.Children.Add(BuildPaperRow());

        // The page's ground is what decides the app's light/dark when the user
        // has opted into "Follow page background", so the switch lives right
        // under the swatches that drive it.
        panel.Children.Add(BuildThemeSourceRow());

        // ---- Grid Type ---------------------------------------------------
        var gridHead = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        gridHead.Children.Add(Heading("Grid Type"));
        var editGrid = new HyperlinkButton
        {
            Content = "Edit Grid",
            FontSize = 12,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        gridHead.Children.Add(editGrid);
        panel.Children.Add(gridHead);
        panel.Children.Add(Caption("You can quickly toggle the grid in the Precision or Layers menus."));
        panel.Children.Add(BuildGridRow());

        var gridEditor = BuildGridEditor(page);
        gridEditor.Visibility = Visibility.Collapsed;
        editGrid.Click += (_, _) =>
            gridEditor.Visibility = gridEditor.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        panel.Children.Add(gridEditor);

        return panel;
    }

    // ---- the circular paper swatches ------------------------------------
    private FrameworkElement BuildPaperRow()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var page = _h.Page();
        string? paper = page?.Paper;
        var pageBg = ColorUtil.Parse(page?.Background ?? "#FFFFFF");

        foreach (var opt in PaperTextures.Options)
        {
            var o = opt;
            bool selected;
            if (o.CustomColor)
                selected = string.IsNullOrEmpty(paper) &&
                           !string.Equals(page?.Background, "#FFFFFF", StringComparison.OrdinalIgnoreCase);
            else if (string.IsNullOrEmpty(o.Id))
                selected = string.IsNullOrEmpty(paper) &&
                           string.Equals(page?.Background, o.Background, StringComparison.OrdinalIgnoreCase);
            else
                selected = string.Equals(paper, o.Id, StringComparison.Ordinal);

            // "Custom Color" previews the page's own colour; everything else
            // previews the real baked texture (or its flat ground for Plain).
            var ground = o.CustomColor ? pageBg : PaperTextures.Ground(o.Id, ColorUtil.Parse(o.Background));
            Brush fill = string.IsNullOrEmpty(o.Id)
                ? new SolidColorBrush(ground)
                : PreviewBrush(PaperTextures.Preview(o.Id, ground, (float)SwatchDiameter)) ?? new SolidColorBrush(ground);

            var cell = Swatch(o.Label, fill, selected, () =>
            {
                if (o.CustomColor)
                {
                    var anchor = _h.Page() != null ? strip : strip;
                    _h.PickColor(anchor, ColorUtil.Parse(_h.Page()?.Background ?? "#FAF9F5"), c =>
                    {
                        _h.SetPaper(null, ColorUtil.ToHex(c));
                        Refresh();
                    });
                    return;
                }
                _h.SetPaper(o.Id, o.Background);
                Refresh();
            });
            strip.Children.Add(cell);
        }

        return HScroll(strip);
    }

    // ---- the circular grid swatches -------------------------------------
    private static readonly (GridType Kind, string Label)[] GridKinds =
    {
        (GridType.None, "No Grid"),
        (GridType.Dotted, "Dot Grid"),
        (GridType.Square, "Graph Paper"),
        (GridType.Lines, "Lined Paper"),
        (GridType.Isometric, "Isometric"),
        (GridType.Triangle, "Triangle"),
    };

    private FrameworkElement BuildGridRow()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var page = _h.Page();
        var ground = PaperTextures.Ground(page);
        var ink = ColorUtil.IsDark(ground)
            ? Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x8C, 0x00, 0x00, 0x00);

        foreach (var (kind, label) in GridKinds)
        {
            var k = kind;
            bool selected = page != null && page.Grid == k;
            Brush fill = PreviewBrush(PaperTextures.GridPreview(k, ground, ink, (float)SwatchDiameter))
                         ?? new SolidColorBrush(ground);
            strip.Children.Add(Swatch(label, fill, selected, () => { _h.SetGrid(k); Refresh(); }));
        }
        return HScroll(strip);
    }

    private FrameworkElement BuildGridEditor(NotePage? page)
    {
        var box = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        var spacing = new Slider
        {
            Minimum = 16,
            Maximum = 96,
            StepFrequency = 4,
            Header = "Grid spacing",
            Value = Math.Clamp(page?.GridSpacing ?? 32, 16, 96),
        };
        spacing.ValueChanged += (_, e) => _h.SetGridSpacing(e.NewValue);
        box.Children.Add(spacing);

        box.Children.Add(Caption("Grid colour"));
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var auto = new Button { Content = "Auto", FontSize = 11, Width = 52, Height = 26 };
        auto.Click += (_, _) => _h.SetGridColor(null);
        colours.Children.Add(auto);
        foreach (var hex in new[] { "#8C8C8C", "#5B8DEF", "#4CAF7D", "#E2A93B", "#D96D6D" })
        {
            var h = hex;
            var b = new Button
            {
                Width = 34,
                Height = 26,
                Background = new SolidColorBrush(ColorUtil.Parse(h)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x5A, 0x80, 0x80, 0x80)),
            };
            b.Click += (_, _) => _h.SetGridColor(h);
            colours.Children.Add(b);
        }
        box.Children.Add(colours);
        return box;
    }

    // ---- theme source ----------------------------------------------------
    /// <summary>The app theme as COLOURED CIRCLES (V3 K.24), not a dropdown —
    /// the same circular-swatch idiom the page backgrounds and the grids above
    /// it already use. Each swatch is PAINTED WITH THE THEME IT SELECTS: Light
    /// is the ivory page over near-black ink, Dark is the reverse, Follow
    /// Windows is split down the middle, and Follow page shows the page's own
    /// ground — so the row is a preview, not four identical dots with words
    /// under them.</summary>
    private FrameworkElement BuildThemeSourceRow()
    {
        var lib = _h.Library();
        var box = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
        box.Children.Add(Heading("App Theme"));
        box.Children.Add(Caption("Light or dark, or let the paper decide."));

        var light = Color.FromArgb(0xFF, 0xFA, 0xF9, 0xF5);
        var dark = Color.FromArgb(0xFF, 0x1B, 0x1A, 0x18);
        var pageGround = PaperTextures.Ground(_h.Page());

        string cur = lib.ThemeSource == "Page" ? "Page" : lib.Theme;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };

        void Add(string tag, string label, Brush fill, string tip)
        {
            var cell = Swatch(label, fill, cur == tag, () =>
            {
                if (tag == "Page") lib.ThemeSource = "Page";
                else { lib.ThemeSource = "Manual"; lib.Theme = tag; }
                _h.ApplyTheme();
                _h.Save();
                Refresh();
            });
            ToolTipService.SetToolTip(cell, tip);
            strip.Children.Add(cell);
        }

        Add("Page", "Follow page", new SolidColorBrush(pageGround),
            "The page decides: a dark, Blueprint or Darkprint page puts Quill in dark mode; white, Lightweight or Brown paper puts it in light mode.");
        Add("Light", "Light", new SolidColorBrush(light), "Always the light skin, whatever the paper is.");
        Add("Dark", "Dark", new SolidColorBrush(dark), "Always the dark skin, whatever the paper is.");
        Add("System", "Follow Windows", Split(light, dark), "Track the Windows light/dark setting.");

        box.Children.Add(HScroll(strip));
        return box;
    }

    /// <summary>A hard half-and-half fill for the "Follow Windows" swatch. A
    /// gradient with two stops at the same offset, built FRESH every time —
    /// WinUI caches GradientStop mutations, so a shared brush that is poked
    /// later would not repaint.</summary>
    private static Brush Split(Color a, Color b)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        g.GradientStops.Add(new GradientStop { Color = a, Offset = 0 });
        g.GradientStops.Add(new GradientStop { Color = a, Offset = 0.5 });
        g.GradientStops.Add(new GradientStop { Color = b, Offset = 0.5 });
        g.GradientStops.Add(new GradientStop { Color = b, Offset = 1 });
        return g;
    }

    // =======================================================================
    // ARTBOARD
    // =======================================================================
    private FrameworkElement BuildArtboardSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Heading("Artboard Size"));
        panel.Children.Add(Caption("Set a reference frame for easier exports."));

        var page = _h.Page();
        bool infinite = page == null || page.PageSize == PageSizePreset.Infinite;
        PageSizes.TryResolve(page ?? new NotePage(), out double rw, out double rh);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        var wBox = DimBox(infinite ? null : rw);
        var hBox = DimBox(infinite ? null : rh);
        row.Children.Add(new TextBlock { Text = "W:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Opacity = 0.8 });
        row.Children.Add(wBox);
        row.Children.Add(new TextBlock { Text = "H:", VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Opacity = 0.8, Margin = new Thickness(6, 0, 0, 0) });
        row.Children.Add(hBox);

        var swap = new Button
        {
            Width = 34,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = Icon(SwapGeometry, 17),
        };
        ToolTipService.SetToolTip(swap, "Swap orientation");
        swap.Click += (_, _) =>
        {
            var p = _h.Page();
            if (p == null) return;
            _h.SetLandscape(!p.PageLandscape);
            Refresh();
        };
        row.Children.Add(swap);
        panel.Children.Add(row);

        void Commit()
        {
            if (!double.TryParse(wBox.Text, out double w) || !double.TryParse(hBox.Text, out double h) ||
                w <= 0 || h <= 0) return;
            _h.SetPageSize(PageSizePreset.Custom, w, h, PageSizeUnit.Pixels);
            Refresh();
        }
        wBox.LostFocus += (_, _) => Commit();
        hBox.LostFocus += (_, _) => Commit();

        // ---- preset chips ------------------------------------------------
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        chips.Children.Add(Chip("Infinite", infinite, () =>
        {
            _h.SetPageSize(PageSizePreset.Infinite, 0, 0, PageSizeUnit.Pixels);
            Refresh();
        }));
        chips.Children.Add(Chip("1024x768",
            page is { PageSize: PageSizePreset.Custom, PageWidth: 1024, PageHeight: 768 },
            () => { _h.SetPageSize(PageSizePreset.Custom, 1024, 768, PageSizeUnit.Pixels); Refresh(); }));
        chips.Children.Add(Chip("A4", page?.PageSize == PageSizePreset.A4,
            () => { _h.SetPageSize(PageSizePreset.A4, 0, 0, PageSizeUnit.Pixels); Refresh(); }));
        chips.Children.Add(Chip("1080p", page?.PageSize == PageSizePreset.Screen1080p,
            () => { _h.SetPageSize(PageSizePreset.Screen1080p, 0, 0, PageSizeUnit.Pixels); Refresh(); }));

        // "..." — the whole PageSizes table, so nothing the app already supports
        // is hidden behind the four shortcuts above.
        var more = new Button
        {
            Content = Icon(EllipsisGeometry, 16),
            Width = 34,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(more, "All page sizes");
        var flyout = new MenuFlyout();
        foreach (var d in PageSizes.Table)
        {
            var def = d;
            var item = new MenuFlyoutItem { Text = def.Name, FontSize = 12 };
            item.Click += (_, _) =>
            {
                _h.SetPageSize(def.Preset, def.Width, def.Height, def.Unit);
                Refresh();
            };
            flyout.Items.Add(item);
        }
        more.Flyout = flyout;
        chips.Children.Add(more);
        panel.Children.Add(HScroll(chips));

        return panel;
    }

    // An infinite dimension shows the infinity sign, exactly as in the reference.
    private static TextBox DimBox(double? value) => new()
    {
        Width = 84,
        Height = 32,
        FontSize = 13,
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Text = value is double v ? Math.Round(v).ToString() : "∞",
    };

    // =======================================================================
    // MEASUREMENTS
    // =======================================================================
    private FrameworkElement BuildMeasurementsSection()
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Heading("Drawing Scale"));
        panel.Children.Add(Caption("How many world units make one inch. This is what keeps millimetre and inch page presets, printing and PDF export physically exact."));
        var page = _h.Page();
        var upi = new Slider
        {
            Minimum = 48,
            Maximum = 300,
            StepFrequency = 6,
            Header = "Units per inch",
            Value = Math.Clamp(page?.UnitsPerInch > 0 ? page!.UnitsPerInch : 96, 48, 300),
        };
        upi.ValueChanged += (_, e) => _h.SetUnitsPerInch(e.NewValue);
        panel.Children.Add(upi);

        // ---- Display units, as CIRCLES (V3 K.23) -------------------------
        // The same circular-swatch idiom as the paper and grid rows above, and
        // grouped Digital / Metric / Imperial exactly as the spec lists them.
        // Each circle carries its own abbreviation, so the row reads as a set of
        // units rather than a set of dots.
        panel.Children.Add(Heading("Display Units"));
        panel.Children.Add(Caption("The unit a custom artboard size is entered in."));

        var current = page?.PageUnit ?? PageSizeUnit.Pixels;
        foreach (var (group, units) in PageSizes.UnitGroups)
        {
            panel.Children.Add(new TextBlock
            {
                Text = group,
                FontSize = 11.5,
                Opacity = 0.55,
                Margin = new Thickness(0, 8, 0, 0),
            });
            var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            foreach (var u in units)
            {
                var unit = u;
                var cell = Swatch(PageSizes.UnitName(unit), UnitFace(unit, current == unit), current == unit, () =>
                {
                    var p = _h.Page();
                    if (p == null) return;
                    _h.SetPageSize(p.PageSize, p.PageWidth, p.PageHeight, unit);
                    Refresh();
                }, badge: PageSizes.Abbrev(unit));
                ToolTipService.SetToolTip(cell,
                    page == null
                        ? "No page is open."
                        : $"Enter custom artboard sizes in {PageSizes.UnitName(unit).ToLowerInvariant()}.");
                cell.IsHitTestVisible = page != null;
                cell.Opacity = page != null ? 1 : 0.45;
                strip.Children.Add(cell);
            }
            panel.Children.Add(HScroll(strip));
        }
        return panel;
    }

    /// <summary>The disc behind a unit's abbreviation. Neutral, and a touch
    /// warmer when selected so the ring is not the only signal.</summary>
    private static Brush UnitFace(PageSizeUnit u, bool selected) =>
        new SolidColorBrush(selected
            ? Color.FromArgb(0x38, 0xD9, 0x77, 0x57)
            : Color.FromArgb(0x22, 0x9A, 0x9A, 0x9A));

    // =======================================================================
    // INTERACTION — everything the previous settings dialog carried
    // =======================================================================
    private FrameworkElement BuildInteraction()
    {
        var panel = new StackPanel { Spacing = 10 };

        // ---- Touch Input (V3 C / K.14) -----------------------------------
        // Touch draw used to be a top-bar toggle. It decides what a FINGER does,
        // which is a preference, not a per-stroke command, so it belongs here.
        var touch = new StackPanel { Spacing = 2 };
        touch.Children.Add(Heading("Touch Input"));
        touch.Children.Add(ChromeUi.ToggleRow("Touch draw", _h.TouchDraw(), v =>
        {
            _h.SetTouchDraw(v);
            _h.Save();
        }, tip: "On: a finger or the mouse marks the page, exactly like the pen. Off: a finger pans and zooms the canvas and only the pen draws."));
        touch.Children.Add(Caption(
            "Off is the pen-first default — your palm and your fingers move the page, and only the pen leaves ink."));
        panel.Children.Add(touch);

        try { _h.FillLegacySettings(panel); }
        catch { panel.Children.Add(Caption("These settings could not be loaded.")); }
        return panel;
    }

    // =======================================================================
    // Building blocks
    // =======================================================================
    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Opacity = 0.62,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 1, 0, 4),
    };

    private static ScrollViewer HScroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollMode = ScrollMode.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    private static Brush? PreviewBrush(Microsoft.Graphics.Canvas.UI.Xaml.CanvasImageSource? src) =>
        src == null ? null : new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill };

    /// <summary>One circular option button: the preview inside the circle, the
    /// label underneath, and — when selected — a bold label over an underline.
    /// <paramref name="badge"/> draws a short caption INSIDE the disc, which is
    /// what turns the same control into a unit swatch ("mm", "cm", "yd").</summary>
    private static FrameworkElement Swatch(string label, Brush fill, bool selected, Action onTap, string? badge = null)
    {
        var circle = new Ellipse
        {
            Width = SwatchDiameter,
            Height = SwatchDiameter,
            Fill = fill,
            StrokeThickness = selected ? 2.2 : 1,
            Stroke = new SolidColorBrush(selected
                ? Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A)
                : Color.FromArgb(0x59, 0x9A, 0x9A, 0x9A)),
        };
        if (selected)
        {
            // the selection ring reads on both themes: accent, not near-black
            try
            {
                if (Application.Current.Resources.TryGetValue("BrandOrangeBrush", out var b) && b is Brush br)
                    circle.Stroke = br;
            }
            catch { }
        }

        var text = new TextBlock
        {
            Text = label,
            FontSize = 10.5,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Opacity = selected ? 1 : 0.78,
        };

        var underline = new Border
        {
            Height = 2,
            Width = 22,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = selected ? Visibility.Visible : Visibility.Collapsed,
            Background = circle.Stroke,
        };

        var stack = new StackPanel
        {
            Width = SwatchCell,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2, 4, 2, 4),
        };
        var disc = new Grid { Width = SwatchDiameter, Height = SwatchDiameter, HorizontalAlignment = HorizontalAlignment.Center };
        disc.Children.Add(circle);
        if (!string.IsNullOrEmpty(badge))
            disc.Children.Add(new TextBlock
            {
                Text = badge,
                FontSize = 15,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            });
        stack.Children.Add(disc);
        stack.Children.Add(text);
        stack.Children.Add(underline);
        ToolTipService.SetToolTip(stack, label);
        stack.Tapped += (_, _) => onTap();
        return stack;
    }

    private static FrameworkElement Chip(string label, bool selected, Action onTap)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            },
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(selected ? 1.4 : 0),
            Background = new SolidColorBrush(selected
                ? Color.FromArgb(0x2E, 0x9A, 0x9A, 0x9A)
                : Colors.Transparent),
        };
        if (selected)
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("BrandOrangeBrush", out var br) && br is Brush brush)
                    b.BorderBrush = brush;
            }
            catch { }
        }
        b.Click += (_, _) => onTap();
        return b;
    }

    /// <summary>A collapsible section with the chevron on the right, as in the
    /// reference. Starts expanded or collapsed per <paramref name="expanded"/>.</summary>
    private static FrameworkElement Section(string title, bool expanded, UIElement content)
    {
        var chevron = new Path
        {
            Data = FloatingWindow.ParseGeometry("M 3,6 L 8,11 L 13,6"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = expanded ? 180 : 0 },
        };
        ApplyInk(chevron);

        var head = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 10, 2, 8),
        };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(chevron);

        var body = new Border
        {
            Child = (UIElement?)content,
            Margin = new Thickness(0, 0, 0, 6),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
        };

        head.Tapped += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (chevron.RenderTransform is RotateTransform rt) rt.Angle = open ? 180 : 0;
        };

        var section = new StackPanel();
        section.Children.Add(head);
        section.Children.Add(body);
        var divider = new Border { Height = 1, Opacity = 0.5 };
        try
        {
            string dict = FloatingWindow.ThemeDictionaryKey;
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dict, out var d) &&
                d is ResourceDictionary rd && rd.TryGetValue("HairlineBrush", out var hb) && hb is Brush hbr)
                divider.Background = hbr;
        }
        catch { }
        section.Children.Add(divider);
        return section;
    }

    // ---- authored vector icons (never an emoji, never a glyph font) ------
    private const string SwapGeometry =
        "M 2,5 L 12,5 M 9.2,2.2 L 12,5 L 9.2,7.8 M 15,11 L 5,11 M 7.8,8.2 L 5,11 L 7.8,13.8";
    private const string EllipsisGeometry =
        "M 3,8 m -1.5,0 a 1.5,1.5 0 1 0 3,0 a 1.5,1.5 0 1 0 -3,0 " +
        "M 8,8 m -1.5,0 a 1.5,1.5 0 1 0 3,0 a 1.5,1.5 0 1 0 -3,0 " +
        "M 13,8 m -1.5,0 a 1.5,1.5 0 1 0 3,0 a 1.5,1.5 0 1 0 -3,0";

    private static FrameworkElement Icon(string geometry, double size)
    {
        var p = new Path
        {
            Data = FloatingWindow.ParseGeometry(geometry),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.None,
        };
        ApplyInk(p);
        return p;
    }

    private static void ApplyInk(Shape shape)
    {
        try
        {
            string dict = FloatingWindow.ThemeDictionaryKey;
            if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dict, out var d) &&
                d is ResourceDictionary rd && rd.TryGetValue("InkBrush", out var v) && v is Brush b)
            {
                shape.Stroke = b;
                return;
            }
        }
        catch { }
        shape.Stroke = new SolidColorBrush(Color.FromArgb(0xCC, 0x88, 0x88, 0x88));
    }
}
