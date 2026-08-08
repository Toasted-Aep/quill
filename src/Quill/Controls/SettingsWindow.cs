using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// The SETTINGS TENANT of <see cref="FloatingWindow"/> — the Concepts-style
/// Workspace / Interaction surface, rebuilt to CONCEPTS-REF-2026-08-07 §3 and
/// its §9 revision pass.
///
/// <para><b>Floating, not docked.</b> An earlier pass read UI-SPEC-V3 K.13
/// backwards and nailed this panel to the right edge at 398.5 DIP. The user's
/// words on 2026-08-07 were "revert settings panel to the floating panel it used
/// to be (like exports)", so the surface is once again a tenant of the same
/// window the Export pane uses: drag pill top-centre, close X upper-left, info
/// (i) upper-right, resize grips in the corners. This class owns NO chrome — it
/// only fills the two tabs.</para>
///
/// <para><b>Colour.</b> Everything comes from <see cref="PageTheme"/>. The body
/// paints itself with <see cref="PageTheme.Panel"/> rather than the page's
/// <c>Surface</c>, because §7 measures the reference panels as a flat #F7F7F7 /
/// #141414 that does NOT carry the page's hue — unlike the dial and the pen bar,
/// which do. §9.9 is the acceptance case: near-black fill, near-white headings,
/// muted captions, unfilled option circles ringed in <c>Outline</c>, the selected
/// one ringed in full <c>OnSurface</c>.</para>
///
/// <para><b>Layout</b> is §3.1's order exactly: Canvas (Background, then Grid
/// Type with its Edit Grid link), Artboard, Measurements, Tool Setup — each a
/// collapsible 30 DIP bold heading with a chevron at the far right — then the
/// Appearance block that §K moved in here, and finally the centred
/// <c>Restore Default Settings</c> link §9.8 confirms.</para>
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

        // ---- optional, so an existing host construction still compiles -----
        /// <summary>Grid opacity 0..1 (UI-SPEC-V3 §C). Null = the control hides.</summary>
        public Action<double>? SetGridOpacity { get; init; }
        /// <summary>Reinstall the keyboard layout after this panel changes the
        /// preset, the overrides or the master switch.</summary>
        public Action? ApplyKeyPreset { get; init; }
    }

    // =======================================================================
    // Geometry, straight off CONCEPTS-REF-2026-08-07 §3 / §9
    // =======================================================================
    private const double SwatchD = 69;    // background + grid circles (§3.1)
    private const double UnitD = 80;      // unit + Wheel|Bar circles (§3.1, §9.8)
    // 3.1: circles "28 DIP apart". The caption's Width is d + 2*CapPad but its
    // margin is -CapPad either side, so the two cancel and a cell measures
    // exactly d wide - which makes the strip's Spacing the gap itself, with
    // no bleed to subtract. It was 22, which measured 22.
    private const double CellGap = 28;
    private const double CapPad = 6;      // caption bleed either side of a circle

    private const double HeadSize = 30;   // collapsible section heading (§3.1)
    private const double SubHeadSize = 17;
    private const double CaptionSize = 15;
    private const double SwatchCapSize = 13;
    private const double SubTabSize = 18;

    private const double TogW = 53, TogH = 35, TogKnob = 27;   // §3.2
    private static readonly Color ToggleOnColor = Color.FromArgb(0xFF, 0x78, 0xA1, 0x9C);

    // ---- theme shorthands. Never a hardcoded grey. ------------------------
    private static Color Ink => PageTheme.OnSurface;
    private static Color Muted => PageTheme.OnSurfaceMuted;
    private static Color Line => PageTheme.Outline;
    private static Color Accent => PageTheme.Accent;
    private static Color PanelFill => PageTheme.Panel;

    /// <summary>One step off the panel, for a selected chip. §3.1 says
    /// "SurfaceAlt", but SurfaceAlt carries the PAGE's hue while this window is
    /// deliberately neutral (§7) — so the faithful reading is a panel-relative
    /// step of the same size, not a blue chip on a grey panel.</summary>
    private static Color PanelAlt => Mix(PanelFill, Ink, 0.10);

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static SolidColorBrush B(Color c) => new(c);

    private readonly Host _h;
    private readonly FloatingWindow _win;

    /// <summary>Which sections the user has open. Held here and not in the tree
    /// so a theme change (which rebuilds every element) does not slam every
    /// section shut.</summary>
    private readonly Dictionary<string, bool> _open = new();

    /// <summary>Restore-defaults is armed by the first press and fires on the
    /// second, which is a confirmation without a modal dialog.</summary>
    private bool _restoreArmed;

    public static SettingsWindow Attach(Panel host, Host h) => new(host, h);

    private SettingsWindow(Panel host, Host h)
    {
        _h = h;
        // Wide enough for the legacy panel the Interaction tab still hosts (it
        // builds itself at 480 DIP), and tall like the reference.
        _win = FloatingWindow.Attach(host, 516, 724);
        _win.Title = "Settings";
        _win.InfoRequested = () => _h.Status(
            "Workspace is the page: its paper, its grid, its artboard and the units everything is measured in. Interaction is the keyboard, the pen and what a finger does.");
        _win.Closed = () => DockChanged?.Invoke();
        _win.SetTabs(new (string, Func<FrameworkElement>)[]
        {
            ("Workspace", BuildWorkspace),
            ("Interaction", BuildInteraction),
        });

        // The two things that can repaint this panel from outside it.
        PageTheme.Changed += () => { if (IsOpen) Refresh(); };
        ToolSurfaceService.Changed += _ => { if (IsOpen) Refresh(); };
    }

    // =======================================================================
    // Open / close. The API MainWindow already calls is kept intact.
    // =======================================================================
    public bool IsOpen => _win.IsOpen;

    /// <summary>Zero, permanently. The panel floats again (§3), so it covers no
    /// edge and the status bar's right cluster has nothing to dodge. Kept so
    /// ChromeBars' dock-inset wiring compiles and quietly becomes a no-op.</summary>
    public double OccupiedRightWidth => 0;

    /// <summary>Raised after the panel opens or closes.</summary>
    public Action? DockChanged { get; set; }

    public void Toggle()
    {
        if (IsOpen) Hide(); else Show();
    }

    public void Show()
    {
        SyncGround();
        _win.RefreshContent();
        _win.Show();
        DockChanged?.Invoke();
    }

    public void Hide()
    {
        _win.Hide();          // FloatingWindow raises Closed, which fires DockChanged
    }

    /// <summary>Rebuild after a theme or page change — this surface captures its
    /// colours at build time exactly like the tree, the pen strip and the dial.</summary>
    public void Refresh()
    {
        SyncGround();
        _restoreArmed = false;
        _win.RefreshContent();
    }

    /// <summary>Point <see cref="PageTheme"/> at the page this panel is about to
    /// describe. Cheap, idempotent, and it only raises Changed on a real move —
    /// without it the theme statics sit on their construction-time default and
    /// every surface that reads them paints for the wrong page.</summary>
    private void SyncGround()
    {
        try { PageTheme.SetGround(PaperTextures.Ground(_h.Page())); } catch { }
    }

    // =======================================================================
    // WORKSPACE  (§3.1)
    // =======================================================================
    private FrameworkElement BuildWorkspace()
    {
        var stack = new StackPanel();
        stack.Children.Add(Section("Canvas", true, BuildCanvas));
        stack.Children.Add(Section("Artboard", false, BuildArtboard));
        stack.Children.Add(Section("Measurements", false, BuildMeasurements));
        stack.Children.Add(Section("Tool Setup", false, BuildToolSetup));
        stack.Children.Add(Section("Appearance", false, BuildAppearance));
        stack.Children.Add(BuildRestoreRow());
        return Body(stack);
    }

    // ---- Canvas ----------------------------------------------------------
    private UIElement BuildCanvas()
    {
        var box = new StackPanel();

        box.Children.Add(SubHead("Background"));
        box.Children.Add(Caption("Standard paper or custom background color?"));
        box.Children.Add(BuildPaperRow());

        // ---- Grid Type, with the Edit Grid link on the same line ----------
        var head = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        head.Children.Add(SubHead("Grid Type"));
        var editGrid = new HyperlinkButton
        {
            Content = new TextBlock { Text = "Edit Grid", FontSize = 15, Foreground = B(Accent) },
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Children.Add(editGrid);
        box.Children.Add(head);
        box.Children.Add(Caption("You can quickly toggle the grid in the Precision or Layers menus."));
        box.Children.Add(BuildGridRow());

        var editor = BuildGridEditor();
        editor.Visibility = Visibility.Collapsed;
        editGrid.Click += (_, _) => editor.Visibility =
            editor.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        box.Children.Add(editor);

        return box;
    }

    /// <summary>The nine reference backgrounds in §3.1's order, then Custom
    /// Color — which the reference row does not carry but Quill does, so it goes
    /// LAST rather than displacing the first reference swatch.</summary>
    private FrameworkElement BuildPaperRow()
    {
        var strip = Strip();
        var page = _h.Page();
        var lib = _h.Library();
        string? paper = page?.Paper;

        var ordered = PaperTextures.Options.Where(o => !o.CustomColor)
                                           .Concat(PaperTextures.Options.Where(o => o.CustomColor));

        foreach (var opt in ordered)
        {
            var o = opt;

            if (o.CustomColor)
            {
                strip.Children.Add(BuildCustomColorCell(page, lib));
                continue;
            }

            bool selected = string.IsNullOrEmpty(o.Id)
                ? string.IsNullOrEmpty(paper) &&
                  string.Equals(page?.Background, o.Background, StringComparison.OrdinalIgnoreCase)
                : string.Equals(paper, o.Id, StringComparison.Ordinal);

            var ground = GroundOf(o);
            Brush fill = string.IsNullOrEmpty(o.Id)
                ? B(ground)
                : PreviewBrush(PaperTextures.Preview(o.Id, ground, (float)SwatchD)) ?? B(ground);

            strip.Children.Add(Circle(SwatchD, o.Label, selected, () =>
            {
                _h.SetPaper(o.Id, o.Background);
                Refresh();
            }, fill: fill));
        }

        return HRow(strip);
    }

    /// <summary>§9.5 — press once to APPLY, press again to EDIT.
    ///
    /// <para>This reverses UI-SPEC-V3 K.10, which had the swatch opening a picker
    /// on every press. The first press selects the colour the user already set;
    /// only a press while it is ALREADY selected opens the COPIC wheel. The one
    /// exception is a custom colour that has never been set — there is nothing to
    /// apply, so that first press opens the wheel.</para></summary>
    private FrameworkElement BuildCustomColorCell(NotePage? page, Library lib)
    {
        string stored = lib.CustomPageColor ?? "";
        bool everSet = !string.IsNullOrWhiteSpace(stored);
        var swatchColor = ColorUtil.Parse(everSet ? stored : (page?.Background ?? "#FAF9F5"));

        // Selected when the page is on a plain colour that is the stored custom
        // one — not merely "on some plain colour", or Plain White would light up
        // two swatches at once.
        bool selected = everSet &&
                        string.IsNullOrEmpty(page?.Paper) &&
                        string.Equals(page?.Background, stored, StringComparison.OrdinalIgnoreCase);

        FrameworkElement cell = null!;
        cell = Circle(SwatchD, "Custom Color", selected, () =>
        {
            if (everSet && !selected)
            {
                // FIRST press: apply what the user already chose. No picker.
                _h.SetPaper(null, stored);
                Refresh();
                return;
            }
            // SECOND press (or a colour never set): edit it.
            _h.PickColor(cell, swatchColor, c =>
            {
                string hex = ColorUtil.ToHex(c);
                lib.CustomPageColor = hex;
                _h.SetPaper(null, hex);
                _h.Save();
                Refresh();
            });
        }, fill: B(swatchColor));

        ToolTipService.SetToolTip(cell, everSet
            ? (selected ? "Press again to edit this colour." : "Applies your custom colour. Press it again to edit it.")
            : "Pick a custom page colour.");
        return cell;
    }

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
        var strip = Strip();
        var page = _h.Page();
        var ground = PaperTextures.Ground(page);
        var ink = ColorUtil.IsDark(ground)
            ? Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x8C, 0x00, 0x00, 0x00);

        foreach (var (kind, label) in GridKinds)
        {
            var k = kind;
            bool selected = page != null && page.Grid == k;
            Brush fill = PreviewBrush(PaperTextures.GridPreview(k, ground, ink, (float)SwatchD)) ?? B(ground);
            strip.Children.Add(Circle(SwatchD, label, selected, () => { _h.SetGrid(k); Refresh(); }, fill: fill));
        }
        return HRow(strip);
    }

    private FrameworkElement BuildGridEditor()
    {
        var page = _h.Page();
        var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 14, 0, 0) };

        box.Children.Add(Label("Grid spacing"));
        var spacing = new Slider
        {
            Minimum = 16,
            Maximum = 96,
            StepFrequency = 4,
            Value = Math.Clamp(page?.GridSpacing ?? 32, 16, 96),
            IsEnabled = page != null,
        };
        spacing.ValueChanged += (_, e) => _h.SetGridSpacing(e.NewValue);
        box.Children.Add(spacing);

        // ---- grid opacity (UI-SPEC-V3 §C) --------------------------------
        if (_h.SetGridOpacity != null)
        {
            var pct = (int)Math.Round(Math.Clamp(page?.GridOpacity ?? 1, 0, 1) * 100);
            var opacityLabel = Label($"Grid opacity — {pct}%");
            box.Children.Add(opacityLabel);
            var op = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                StepFrequency = 5,
                Value = pct,
                IsEnabled = page != null,
            };
            op.ValueChanged += (_, e) =>
            {
                opacityLabel.Text = $"Grid opacity — {(int)Math.Round(e.NewValue)}%";
                _h.SetGridOpacity!(e.NewValue / 100.0);
            };
            box.Children.Add(op);
        }

        box.Children.Add(Label("Grid colour"));
        var colours = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 0) };
        var auto = new Button { Content = "Auto", FontSize = 13, Height = 30, Padding = new Thickness(12, 0, 12, 0) };
        auto.Click += (_, _) => _h.SetGridColor(null);
        colours.Children.Add(auto);
        foreach (var hex in new[] { "#8C8C8C", "#5B8DEF", "#4CAF7D", "#E2A93B", "#D96D6D" })
        {
            var h = hex;
            var dot = new Ellipse
            {
                Width = 26,
                Height = 26,
                Fill = B(ColorUtil.Parse(h)),
                Stroke = B(Line),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var hit = new Grid { Background = B(Colors.Transparent), Width = 30, Height = 30 };
            hit.Children.Add(dot);
            hit.Tapped += (_, _) => _h.SetGridColor(h);
            colours.Children.Add(hit);
        }
        box.Children.Add(colours);
        return box;
    }

    // ---- Artboard --------------------------------------------------------
    private UIElement BuildArtboard()
    {
        var box = new StackPanel();
        box.Children.Add(SubHead("Artboard Size"));
        box.Children.Add(Caption("Set a reference frame for easier exports."));

        var page = _h.Page();
        bool infinite = page == null || page.PageSize == PageSizePreset.Infinite;
        var unit = ActiveUnit();
        double upi = page?.UnitsPerInch > 0 ? page!.UnitsPerInch : 96;
        // Declared up front: TryResolve's outs are behind a short-circuit, so the
        // compiler cannot prove they are assigned on the null-page path.
        double rw = 0, rh = 0;
        bool resolved = page != null && PageSizes.TryResolve(page, out rw, out rh);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var wBox = DimBox(infinite || !resolved ? null : FromWorld(rw, unit, upi));
        var hBox = DimBox(infinite || !resolved ? null : FromWorld(rh, unit, upi));
        row.Children.Add(FieldTag("W:"));
        row.Children.Add(wBox);
        row.Children.Add(FieldTag("H:"));
        row.Children.Add(hBox);

        var swap = new Button
        {
            Width = 38,
            Height = 36,
            Padding = new Thickness(0),
            Background = B(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Content = Icons.Mark(SwapGeometry, Ink, 20, stroked: true, thickness: 1.7),
            IsEnabled = page != null,
        };
        ToolTipService.SetToolTip(swap, "Swap width and height");
        swap.Click += (_, _) =>
        {
            var p = _h.Page();
            if (p == null) return;
            _h.SetLandscape(!p.PageLandscape);
            Refresh();
        };
        row.Children.Add(swap);
        box.Children.Add(row);

        // The live readout is what makes Display Format & Precision below a real
        // setting rather than a stored string nothing reads.
        var readout = Caption(resolved && !infinite
            ? $"{FormatMeasure(FromWorld(rw, unit, upi), unit)}  ×  {FormatMeasure(FromWorld(rh, unit, upi), unit)}"
            : "Infinite canvas — no page boundary.");
        readout.Margin = new Thickness(0, 8, 0, 0);
        box.Children.Add(readout);

        void Commit()
        {
            if (!double.TryParse(wBox.Text, out double w) || !double.TryParse(hBox.Text, out double h) ||
                w <= 0 || h <= 0) return;
            _h.SetPageSize(PageSizePreset.Custom, w, h, unit);
            Refresh();
        }
        wBox.LostFocus += (_, _) => Commit();
        hBox.LostFocus += (_, _) => Commit();

        // ---- preset chips -------------------------------------------------
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        chips.Children.Add(Chip("Infinite", infinite, () =>
        {
            _h.SetPageSize(PageSizePreset.Infinite, 0, 0, unit);
            Refresh();
        }));
        chips.Children.Add(Chip("1024x768",
            page is { PageSize: PageSizePreset.Custom, PageWidth: 1024, PageHeight: 768 },
            () => { _h.SetPageSize(PageSizePreset.Custom, 1024, 768, PageSizeUnit.Pixels); Refresh(); }));
        chips.Children.Add(Chip("A4", page?.PageSize == PageSizePreset.A4,
            () => { _h.SetPageSize(PageSizePreset.A4, 0, 0, unit); Refresh(); }));
        chips.Children.Add(Chip("1080p", page?.PageSize == PageSizePreset.Screen1080p,
            () => { _h.SetPageSize(PageSizePreset.Screen1080p, 0, 0, PageSizeUnit.Pixels); Refresh(); }));

        // "…" — the whole PageSizes table, so nothing the app already supports is
        // hidden behind the four shortcuts. The flyout is built ONCE, here: a
        // flyout whose Opening handler rebuilds the row it is anchored in
        // unparents its own anchor and dies silently.
        var more = new Button
        {
            Content = Icons.Mark(EllipsisGeometry, Ink, 18),
            Width = 38,
            Height = 32,
            Padding = new Thickness(0),
            Background = B(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(more, "All page sizes");
        var flyout = new MenuFlyout();
        foreach (var d in PageSizes.Table)
        {
            var def = d;
            var item = new MenuFlyoutItem { Text = def.Name, FontSize = 13 };
            item.Click += (_, _) => { _h.SetPageSize(def.Preset, def.Width, def.Height, def.Unit); Refresh(); };
            flyout.Items.Add(item);
        }
        more.Flyout = flyout;
        chips.Children.Add(more);
        box.Children.Add(HRow(chips));

        return box;
    }

    private TextBox DimBox(double? value) => new()
    {
        Width = 100,
        Height = 36,
        FontSize = 15,
        CornerRadius = new CornerRadius(10),
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Text = value is double v ? (Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.##")) : "∞",
    };

    private TextBlock FieldTag(string t) => new()
    {
        Text = t,
        FontSize = 15,
        Foreground = B(Muted),
        VerticalAlignment = VerticalAlignment.Center,
    };

    // =======================================================================
    // MEASUREMENTS  (§3.1)
    // =======================================================================
    /// <summary>One option in the Units row. <c>Tag</c> is what lands in
    /// <c>Library.MeasureUnit</c>; "" is the COMBINED option, which is why this
    /// is a string and not the enum — "m/cm/mm" is not a PageSizeUnit and never
    /// can be. <c>Maps</c> is the concrete unit the artboard fields use when the
    /// combined option is chosen.</summary>
    private sealed record UnitOpt(string Label, string Tag, PageSizeUnit Maps);

    private static readonly (string System, UnitOpt[] Units)[] UnitSystems =
    {
        ("Digital", new UnitOpt[]
        {
            new("px/pts", "", PageSizeUnit.Pixels),
            new("px", "px", PageSizeUnit.Pixels),
            new("pts", "pt", PageSizeUnit.Points),
        }),
        ("Metric", new UnitOpt[]
        {
            new("m/cm/mm", "", PageSizeUnit.Millimeters),
            new("mm", "mm", PageSizeUnit.Millimeters),
            new("cm", "cm", PageSizeUnit.Centimeters),
            new("m", "m", PageSizeUnit.Meters),
            new("km", "km", PageSizeUnit.Kilometers),
        }),
        ("Imperial", new UnitOpt[]
        {
            new("ft/in", "", PageSizeUnit.Inches),
            new("in", "in", PageSizeUnit.Inches),
            new("ft", "ft", PageSizeUnit.Feet),
            new("yds", "yd", PageSizeUnit.Yards),
            new("mi", "mi", PageSizeUnit.Miles),
        }),
    };

    private string ActiveSystem()
    {
        var s = _h.Library().MeasureSystem;
        return UnitSystems.Any(u => u.System == s) ? s : "Digital";
    }

    /// <summary>The concrete unit everything in the panel is displayed in —
    /// resolved from the library's system + unit pair, never from an INDEX. The
    /// unit list has been appended to twice (V3 K.23) and the one bug that came
    /// out of it was a control indexed as if the list still had three members.</summary>
    private PageSizeUnit ActiveUnit()
    {
        var lib = _h.Library();
        var set = UnitSystems.First(u => u.System == ActiveSystem()).Units;
        var pick = set.FirstOrDefault(u => u.Tag == (lib.MeasureUnit ?? "")) ?? set[0];
        return pick.Maps;
    }

    private UIElement BuildMeasurements()
    {
        var lib = _h.Library();
        var box = new StackPanel();

        box.Children.Add(SubHead("Units"));
        box.Children.Add(Caption("Any units displayed or entered on canvas will be converted to this system."));

        // ---- Digital | Metric | Imperial sub-tabs -------------------------
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24, Margin = new Thickness(0, 6, 0, 4) };
        var unitHost = new Border();          // rebuilt in place, so the tab does not scroll away
        string active = ActiveSystem();

        foreach (var (name, _) in UnitSystems)
        {
            string sys = name;
            bool on = sys == active;
            var t = new TextBlock
            {
                Text = sys,
                FontSize = SubTabSize,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = B(on ? Ink : Muted),
                Margin = new Thickness(0, 0, 0, 5),
            };
            var rule = new Border
            {
                Height = 2,
                Background = B(Ink),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = on ? Visibility.Visible : Visibility.Collapsed,
            };
            var cell = new Grid { Background = B(Colors.Transparent) };
            cell.Children.Add(t);
            cell.Children.Add(rule);
            cell.Tapped += (_, _) =>
            {
                lib.MeasureSystem = sys;
                lib.MeasureUnit = "";          // the combined option, always valid in any system
                _h.Save();
                Refresh();
            };
            tabs.Children.Add(cell);
        }
        box.Children.Add(tabs);

        unitHost.Child = BuildUnitCircles();
        box.Children.Add(unitHost);

        // ---- Display Format & Precision -----------------------------------
        box.Children.Add(Spacer(20));
        box.Children.Add(SubHead("Display Format & Precision"));
        box.Children.Add(Caption("Select your preferred notation."));

        var groups = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 6, 0, 0) };

        var fmt = Strip();
        fmt.Children.Add(Circle(SwatchD, "Full", lib.MeasureFormat == "Full",
            () => { lib.MeasureFormat = "Full"; _h.Save(); Refresh(); }, inner: DiscText("6.5\npixels", 12)));
        fmt.Children.Add(Circle(SwatchD, "Abbreviated", lib.MeasureFormat != "Full",
            () => { lib.MeasureFormat = "Abbreviated"; _h.Save(); Refresh(); }, inner: DiscText("6.5 px", 14)));
        groups.Children.Add(fmt);

        groups.Children.Add(new Border
        {
            Width = 1,
            Background = B(Line),
            Margin = new Thickness(0, 4, 0, 24),
        });

        var prec = Strip();
        prec.Children.Add(Circle(SwatchD, "Rounded", lib.MeasurePrecision != "Tenths",
            () => { lib.MeasurePrecision = "Rounded"; _h.Save(); Refresh(); }, inner: DiscText("6", 20)));
        prec.Children.Add(Circle(SwatchD, "Tenths", lib.MeasurePrecision == "Tenths",
            () => { lib.MeasurePrecision = "Tenths"; _h.Save(); Refresh(); }, inner: DiscText("6.0", 18)));
        groups.Children.Add(prec);

        box.Children.Add(HRow(groups));

        // ---- the two toggle rows ------------------------------------------
        box.Children.Add(Spacer(18));
        box.Children.Add(ToggleRow(
            "Show stroke length on the right side when drawing",
            lib.ShowStrokeLength,
            v => { lib.ShowStrokeLength = v; _h.Save(); },
            enabled: false,
            tip: "Quill does not draw a live length readout on the canvas yet, so this is switched off rather than pretending to work. The preference is kept for when it lands."));
        box.Children.Add(ToggleRow(
            "Show scale in the status bar for selections",
            lib.ShowSelectionScale,
            v => { lib.ShowSelectionScale = v; _h.Save(); },
            enabled: false,
            tip: "The status bar has no selection-scale readout yet. Shown here because the row belongs to this section, switched off because it would do nothing."));

        return box;
    }

    private FrameworkElement BuildUnitCircles()
    {
        var lib = _h.Library();
        var set = UnitSystems.First(u => u.System == ActiveSystem()).Units;
        string cur = lib.MeasureUnit ?? "";
        if (!set.Any(u => u.Tag == cur)) cur = "";

        var strip = Strip();
        foreach (var opt in set)
        {
            var o = opt;
            bool on = o.Tag == cur;
            // The combined option shows its stack on separate lines, as §3.1
            // describes it ("m / cm / mm").
            string face = o.Tag == "" ? o.Label.Replace("/", "\n") : o.Label;
            double size = o.Tag == "" ? 15 : 20;
            strip.Children.Add(Circle(UnitD, o.Tag == "" ? "Combined" : PageSizes.UnitName(o.Maps), on, () =>
            {
                lib.MeasureUnit = o.Tag;
                _h.Save();
                var p = _h.Page();
                // Keep the page's own entry unit in step, so the artboard fields
                // and the units row can never disagree. Routed through the host's
                // SetPageSize, which is the ONLY writer of PageUnit outside
                // MainWindow's own combo.
                if (p != null) _h.SetPageSize(p.PageSize, p.PageWidth, p.PageHeight, o.Maps);
                Refresh();
            }, inner: DiscText(face, size)));
        }
        return HRow(strip);
    }

    // ---- unit maths ------------------------------------------------------
    private static double FromWorld(double world, PageSizeUnit u, double upi)
    {
        double inches = upi > 0 ? world / upi : world / 96.0;
        return u switch
        {
            PageSizeUnit.Pixels => world,
            PageSizeUnit.Points => inches * 72.0,
            PageSizeUnit.Inches => inches,
            PageSizeUnit.Feet => inches / 12.0,
            PageSizeUnit.Yards => inches / 36.0,
            PageSizeUnit.Miles => inches / 63360.0,
            PageSizeUnit.Millimeters => inches * 25.4,
            PageSizeUnit.Centimeters => inches * 2.54,
            PageSizeUnit.Meters => inches * 0.0254,
            PageSizeUnit.Kilometers => inches * 0.0000254,
            _ => world,
        };
    }

    private string FormatMeasure(double v, PageSizeUnit u)
    {
        var lib = _h.Library();
        string num = lib.MeasurePrecision == "Tenths" ? v.ToString("0.0") : Math.Round(v).ToString("0");
        string unit = lib.MeasureFormat == "Full"
            ? PageSizes.UnitName(u).ToLowerInvariant()
            : PageSizes.Abbrev(u);
        return num + " " + unit;
    }

    // =======================================================================
    // TOOL SETUP  (§3.1, confirmed §9.8)
    // =======================================================================
    private UIElement BuildToolSetup()
    {
        var box = new StackPanel();
        box.Children.Add(SubHead("Interface"));
        box.Children.Add(Caption("Choose your preferred tool palette."));

        var cur = ToolSurfaceService.Current;
        var strip = Strip();
        strip.Children.Add(Circle(UnitD, "Wheel", cur == ToolSurface.Wheel,
            () => { ToolSurfaceService.Set(ToolSurface.Wheel); Refresh(); },
            inner: Icons.Mark(Icons.SurfaceWheel, cur == ToolSurface.Wheel ? Ink : Muted, 40)));
        strip.Children.Add(Circle(UnitD, "Bar", cur == ToolSurface.Bar,
            () => { ToolSurfaceService.Set(ToolSurface.Bar); Refresh(); },
            inner: Icons.Mark(Icons.SurfaceBar, cur == ToolSurface.Bar ? Ink : Muted, 40)));
        box.Children.Add(HRow(strip));
        return box;
    }

    // =======================================================================
    // APPEARANCE — the light/dark control §C moved in here
    // =======================================================================
    private UIElement BuildAppearance()
    {
        var lib = _h.Library();
        var box = new StackPanel();

        box.Children.Add(SubHead("Theme"));
        box.Children.Add(Caption("Light or dark, or let the page decide."));

        bool manual = lib.ThemeSource != "Page" && lib.Theme != "System";
        box.Children.Add(ToggleRow(
            "Dark appearance",
            lib.ThemeSource == "Page" ? PageTheme.IsDark : lib.Theme == "Dark",
            v =>
            {
                lib.ThemeSource = "Manual";
                lib.Theme = v ? "Dark" : "Light";
                _h.ApplyTheme();
                _h.Save();
                Refresh();
            },
            enabled: manual,
            tip: manual
                ? "The whole app's light/dark skin. Replaces the old dropdown (UI-SPEC-V3 §C)."
                : "Something else is deciding the theme right now — pick Manual below to drive it from this switch."));

        // K.24 wanted the theme as coloured circles. What the circles pick is WHO
        // decides; the slider above picks light or dark once the answer is "you".
        box.Children.Add(Spacer(8));
        box.Children.Add(Label("Decided by"));

        var light = Color.FromArgb(0xFF, 0xFA, 0xF9, 0xF5);
        var dark = Color.FromArgb(0xFF, 0x1B, 0x1A, 0x18);
        string cur = lib.ThemeSource == "Page" ? "Page" : lib.Theme == "System" ? "System" : "Manual";

        var strip = Strip();
        void Add(string tag, string label, Brush fill, string tip)
        {
            var cell = Circle(SwatchD, label, cur == tag, () =>
            {
                if (tag == "Page") lib.ThemeSource = "Page";
                else if (tag == "System") { lib.ThemeSource = "Manual"; lib.Theme = "System"; }
                else { lib.ThemeSource = "Manual"; if (lib.Theme == "System") lib.Theme = "Dark"; }
                _h.ApplyTheme();
                _h.Save();
                Refresh();
            }, fill: fill);
            ToolTipService.SetToolTip(cell, tip);
            strip.Children.Add(cell);
        }

        Add("Page", "The page", B(PaperTextures.Ground(_h.Page())),
            "A Blueprint, Brown or Darkprint page puts Quill in dark mode; a white or Lightweight page puts it in light mode.");
        Add("Manual", "You", B(lib.Theme == "Light" ? light : dark),
            "The switch above decides, whatever the paper is.");
        Add("System", "Windows", Split(light, dark),
            "Track the Windows light/dark setting.");

        box.Children.Add(HRow(strip));
        return box;
    }

    /// <summary>A hard half-and-half fill, built FRESH every time — WinUI caches
    /// GradientStop mutations, so a shared brush poked later would not repaint.</summary>
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
    // Restore Default Settings  (§9.8: Accent, centred, last row of Workspace)
    // =======================================================================
    private FrameworkElement BuildRestoreRow()
    {
        var link = new HyperlinkButton
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 6),
            Padding = new Thickness(6, 4, 6, 4),
        };
        var text = new TextBlock
        {
            Text = _restoreArmed ? "Press again to restore defaults" : "Restore Default Settings",
            FontSize = 15,
            Foreground = B(Accent),
            FontWeight = _restoreArmed ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        };
        link.Content = text;
        ToolTipService.SetToolTip(link,
            "Puts this panel's own settings back to their defaults — the page's grid and artboard, the units, the tool palette, the theme and the interaction settings. Your notebooks, pens and pages are not touched.");

        link.Click += (_, _) =>
        {
            // Two presses, not a modal: the second press is the confirmation.
            if (!_restoreArmed)
            {
                _restoreArmed = true;
                text.Text = "Press again to restore defaults";
                text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                return;
            }
            _restoreArmed = false;
            RestoreDefaults();
        };
        return link;
    }

    private void RestoreDefaults()
    {
        var lib = _h.Library();

        lib.MeasureSystem = "Digital";
        lib.MeasureUnit = "";
        lib.MeasureFormat = "Abbreviated";
        lib.MeasurePrecision = "Rounded";
        lib.ShowStrokeLength = false;
        lib.ShowSelectionScale = false;

        lib.KeyboardShortcuts = true;
        lib.KeyPreset = "Quill";
        lib.KeyOverrides.Clear();
        lib.FingerAction = "UseActiveTool";
        lib.GestureBindings.Clear();

        lib.ThemeSource = "Manual";
        lib.Theme = "Dark";

        ToolSurfaceService.Set(ToolSurface.Wheel);
        _h.SetTouchDraw(false);

        var p = _h.Page();
        if (p != null)
        {
            _h.SetGrid(GridType.None);
            _h.SetGridSpacing(32);
            _h.SetGridColor(null);
            _h.SetGridOpacity?.Invoke(1);
            _h.SetPageSize(PageSizePreset.Infinite, 0, 0, PageSizeUnit.Pixels);
        }

        try { _h.ApplyKeyPreset?.Invoke(); } catch { }
        _h.ApplyTheme();
        _h.Save();
        _h.Status("Settings restored to their defaults.");
        Refresh();
    }

    // =======================================================================
    // INTERACTION  (§3.3 / UI-SPEC-V3 §C)
    // =======================================================================
    private FrameworkElement BuildInteraction()
    {
        var stack = new StackPanel();
        stack.Children.Add(Section("Keyboard & Mouse", true, BuildKeyboard));
        stack.Children.Add(Section("Touch Input", true, BuildTouch));
        stack.Children.Add(Section("Gesture shortcuts", false, BuildGestures));
        stack.Children.Add(Section("All Quill Settings", false, BuildLegacy));
        return Body(stack);
    }

    private UIElement BuildKeyboard()
    {
        var lib = _h.Library();
        var box = new StackPanel();

        box.Children.Add(ToggleRow("Enable keyboard shortcuts", lib.KeyboardShortcuts, v =>
        {
            lib.KeyboardShortcuts = v;
            try { _h.ApplyKeyPreset?.Invoke(); } catch { }
            _h.Save();
        }, tip: "Off unbinds every accelerator at once, which is what you want while a shortcut fights another app."));

        box.Children.Add(Spacer(10));
        box.Children.Add(SubHead("Shortcut layout"));
        box.Children.Add(Caption("Start from a layout you already know, then change individual keys."));

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var id in new[] { "Quill", "OneNote", "Photoshop" })
        {
            string p = id;
            chips.Children.Add(Chip(p, string.Equals(lib.KeyPreset, p, StringComparison.OrdinalIgnoreCase), () =>
            {
                lib.KeyPreset = p;
                try { _h.ApplyKeyPreset?.Invoke(); } catch { }
                _h.Save();
                Refresh();
            }));
        }
        box.Children.Add(chips);

        var edit = new HyperlinkButton
        {
            Content = new TextBlock { Text = "Edit shortcuts", FontSize = 15, Foreground = B(Accent) },
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(edit, "Opens the per-command rebind list at the bottom of this tab.");
        edit.Click += (_, _) =>
        {
            _open["All Quill Settings"] = true;
            _h.Status("Shortcut rebinding is under “All Quill Settings”, at the foot of this tab.");
            Refresh();
        };
        box.Children.Add(edit);

        return box;
    }

    /// <summary>Finger Action (§3.3). Quill dispatches exactly two of these
    /// today: a finger either marks with the active tool or moves the page. The
    /// rest are SHOWN because the row is the reference's row, and DISABLED with
    /// a reason, which is the same rule the export pane applies to DXF and PSD —
    /// a control that silently does nothing is worse than one that says why.</summary>
    private static readonly (string Tag, string Label, string Glyph, bool Stroked, bool Live)[] FingerActions =
    {
        ("DoNothing",     "Do Nothing",     DoNothingGeometry, true,  true),
        ("UseActiveTool", "Use Active Tool", Icons.Pen,        false, true),
        ("PenCanvas",     "Pen Canvas",     Icons.TouchDraw,   false, false),
        ("Select",        "Select",         Icons.Select,      false, false),
        ("Nudge",         "Nudge",          NudgeGeometry,     true,  false),
        ("Slice",         "Slice",          SliceGeometry,     true,  false),
        ("Zoom",          "Zoom",           Icons.Zoom,        false, false),
        ("Rotate",        "Rotate",         RotateGeometry,    true,  false),
    };

    private UIElement BuildTouch()
    {
        var lib = _h.Library();
        var box = new StackPanel();

        // V3 K.14 — touch draw was a top-bar toggle; it is a preference, not a
        // per-stroke command, so it belongs here.
        box.Children.Add(ToggleRow("Touch draw", _h.TouchDraw(), v =>
        {
            _h.SetTouchDraw(v);
            lib.FingerAction = v ? "UseActiveTool" : "DoNothing";
            _h.Save();
            Refresh();
        }, tip: "On: a finger or the mouse marks the page, exactly like the pen. Off: a finger pans and zooms and only the pen draws."));
        box.Children.Add(Caption("Off is the pen-first default — your palm and your fingers move the page, and only the pen leaves ink."));

        box.Children.Add(Spacer(14));
        box.Children.Add(SubHead("Finger Action"));
        box.Children.Add(Caption("What a fingertip does when it touches the page."));

        string cur = lib.FingerAction ?? "UseActiveTool";
        if (!FingerActions.Any(f => f.Tag == cur)) cur = "UseActiveTool";

        var strip = Strip();
        foreach (var fa in FingerActions)
        {
            var f = fa;
            bool on = f.Tag == cur;
            var mark = Icons.Mark(f.Glyph, on ? Ink : Muted, 32, stroked: f.Stroked, thickness: 2);
            var cell = Circle(SwatchD, f.Label, on, () =>
            {
                if (!f.Live) return;
                lib.FingerAction = f.Tag;
                _h.SetTouchDraw(f.Tag == "UseActiveTool");
                _h.Save();
                Refresh();
            }, inner: mark, enabled: f.Live);
            ToolTipService.SetToolTip(cell, f.Live
                ? (f.Tag == "DoNothing"
                    ? "A finger pans and zooms the canvas and never marks it."
                    : "A finger marks the page with whatever tool is selected.")
                : $"Quill has no finger-dispatch for “{f.Label}” yet. It is shown so the row matches the reference, and switched off rather than pretending.");
            strip.Children.Add(cell);
        }
        box.Children.Add(HRow(strip));
        return box;
    }

    private static readonly (string Tag, string Label)[] Gestures =
    {
        ("TopButtonClick", "Top-button click"),
        ("TopButtonDouble", "Top-button double-click"),
        ("TopButtonHold", "Top-button hold"),
        ("TwoFingerTap", "Two-finger tap"),
        ("ThreeFingerTap", "Three-finger tap"),
        ("FourFingerTap", "Four-finger tap"),
    };

    private static readonly string[] GestureCommands =
    {
        "None", "Undo", "Redo", "Toggle grid", "Toggle eraser", "Fit to page", "Show tool palette",
    };

    private UIElement BuildGestures()
    {
        var lib = _h.Library();
        var box = new StackPanel { Spacing = 2 };
        box.Children.Add(Caption(
            "Quill does not receive pen-barrel presses or multi-finger taps from Windows yet, so these are recorded and will take effect the moment that input lands. Nothing here is faked in the meantime."));

        foreach (var g in Gestures)
        {
            var tag = g.Tag;
            string cur = ReadGesture(lib, tag);
            var row = new Grid { Padding = new Thickness(0, 7, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = g.Label,
                FontSize = 15,
                Foreground = B(Ink),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var combo = new ComboBox { Width = 168, FontSize = 14 };
            foreach (var c in GestureCommands) combo.Items.Add(new ComboBoxItem { Content = c, Tag = c });
            // Selected by TAG, never by index: the command list will grow.
            foreach (ComboBoxItem it in combo.Items)
                if ((string)it.Tag == cur) { combo.SelectedItem = it; break; }
            if (combo.SelectedItem == null) combo.SelectedItem = combo.Items[0];
            combo.SelectionChanged += (_, _) =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is not string cmd) return;
                WriteGesture(lib, tag, cmd);
                _h.Save();
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            box.Children.Add(row);
        }
        return box;
    }

    private static string ReadGesture(Library lib, string tag)
    {
        foreach (var s in lib.GestureBindings)
        {
            int i = s.IndexOf('=');
            if (i > 0 && s[..i] == tag) return s[(i + 1)..];
        }
        return "None";
    }

    private static void WriteGesture(Library lib, string tag, string cmd)
    {
        lib.GestureBindings.RemoveAll(s =>
        {
            int i = s.IndexOf('=');
            return i > 0 && s[..i] == tag;
        });
        if (cmd != "None") lib.GestureBindings.Add(tag + "=" + cmd);
    }

    /// <summary>Everything the pre-rebuild settings dialog carried, so nothing
    /// that was reachable before this pass became unreachable after it — the
    /// storage folder, the language, the AI provider, the accent, the per-command
    /// key rebinds. It is a section like any other, collapsed by default.</summary>
    private UIElement BuildLegacy()
    {
        var panel = new StackPanel { Spacing = 10 };
        try { _h.FillLegacySettings(panel); }
        catch { panel.Children.Add(Caption("These settings could not be loaded.")); }
        return panel;
    }

    // =======================================================================
    // Building blocks
    // =======================================================================
    /// <summary>The tab's outer surface. It paints PageTheme.Panel itself rather
    /// than leaning on the window's fill, so the ink chosen from the same source
    /// is guaranteed to contrast with what is actually behind it.</summary>
    private FrameworkElement Body(UIElement content)
    {
        var border = new Border
        {
            Background = B(PanelFill),
            Padding = new Thickness(20, 8, 20, 24),
            Child = content,
        };
        // Stock controls inside (TextBox, Slider, ComboBox, Button) resolve their
        // own brushes from the element theme, not from PageTheme — so tell them
        // which side of the line this panel is on.
        border.RequestedTheme = PageTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
        return border;
    }

    /// <summary>A collapsible section: 30 DIP bold heading, chevron at the far
    /// right, hairline beneath (§3.1).</summary>
    private FrameworkElement Section(string title, bool defaultOpen, Func<UIElement> build)
    {
        bool open = _open.TryGetValue(title, out bool o) ? o : defaultOpen;
        _open[title] = open;

        var chevron = Icons.Mark(Icons.ChevronDown, Ink, 22, stroked: true, thickness: 2);
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.VerticalAlignment = VerticalAlignment.Center;
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        chevron.RenderTransform = new RotateTransform { Angle = open ? 180 : 0 };

        var head = new Grid
        {
            Background = B(Colors.Transparent),
            Padding = new Thickness(0, 14, 0, 10),
        };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = HeadSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = B(Ink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(chevron);

        UIElement inner;
        try { inner = build(); }
        catch { inner = Caption("This section could not be built."); }

        var body = new Border
        {
            Child = inner,
            Margin = new Thickness(0, 0, 0, 10),
            Visibility = open ? Visibility.Visible : Visibility.Collapsed,
        };

        head.Tapped += (_, _) =>
        {
            bool now = body.Visibility != Visibility.Visible;
            body.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            _open[title] = now;
            if (chevron.RenderTransform is RotateTransform rt) rt.Angle = now ? 180 : 0;
        };

        var section = new StackPanel();
        section.Children.Add(head);
        section.Children.Add(body);
        section.Children.Add(new Border { Height = 1, Background = B(Line) });
        return section;
    }

    private TextBlock SubHead(string t) => new()
    {
        Text = t,
        FontSize = SubHeadSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = B(Ink),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private TextBlock Caption(string t) => new()
    {
        Text = t,
        FontSize = CaptionSize,
        Foreground = B(Muted),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 8),
    };

    private TextBlock Label(string t) => new()
    {
        Text = t,
        FontSize = 15,
        Foreground = B(Ink),
        Margin = new Thickness(0, 8, 0, 2),
    };

    private static FrameworkElement Spacer(double h) => new Border { Height = h };

    private static StackPanel Strip() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = CellGap,
    };

    /// <summary>A horizontally scrolling row with the proportional scroll
    /// indicator §3.1 puts under it — width proportional to the visible fraction,
    /// offset proportional to how far along the row is, in Outline. Hidden
    /// outright when nothing overflows, because an indicator that always spans
    /// the full width is just a rule.</summary>
    private FrameworkElement HRow(UIElement content)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var thumb = new Border
        {
            Height = 3,
            Width = 0,
            CornerRadius = new CornerRadius(1.5),
            Background = B(Line),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var track = new Grid { Height = 3, Margin = new Thickness(0, 10, 0, 0) };
        track.Children.Add(thumb);

        void Sync()
        {
            try
            {
                double vw = sv.ViewportWidth, ew = sv.ExtentWidth, tw = track.ActualWidth;
                if (tw <= 0 || vw <= 0 || ew <= vw + 0.5)
                {
                    track.Visibility = Visibility.Collapsed;
                    return;
                }
                track.Visibility = Visibility.Visible;
                double w = Math.Max(20, tw * Math.Clamp(vw / ew, 0.05, 1));
                thumb.Width = w;
                double travel = Math.Max(0, tw - w);
                double f = Math.Clamp(sv.HorizontalOffset / Math.Max(1, ew - vw), 0, 1);
                thumb.Margin = new Thickness(travel * f, 0, 0, 0);
            }
            catch { }
        }

        sv.ViewChanged += (_, _) => Sync();
        sv.SizeChanged += (_, _) => Sync();
        track.SizeChanged += (_, _) => Sync();
        sv.Loaded += (_, _) => Sync();

        var box = new StackPanel();
        box.Children.Add(sv);
        box.Children.Add(track);
        return box;
    }

    /// <summary>One circular option (§3.1, §9.9).
    ///
    /// <para>Selected = a 2 DIP <c>OnSurface</c> ring and a bold caption;
    /// unselected = a hairline <c>Outline</c> ring and a muted caption. §9.9
    /// confirms the option circles are UNFILLED — the ring is the whole mark —
    /// so <paramref name="fill"/> is supplied only by the rows whose subject IS a
    /// colour or a texture: the page backgrounds, the grids and the theme.</para></summary>
    private FrameworkElement Circle(double d, string caption, bool selected, Action tap,
                                    Brush? fill = null, UIElement? inner = null, bool enabled = true)
    {
        var ring = new Ellipse
        {
            Width = d,
            Height = d,
            Fill = fill ?? B(Colors.Transparent),
            Stroke = B(selected ? Ink : Line),
            StrokeThickness = selected ? 2 : 1,
        };

        var disc = new Grid { Width = d, Height = d, HorizontalAlignment = HorizontalAlignment.Center };
        disc.Children.Add(ring);
        if (inner != null) disc.Children.Add(inner);

        var text = new TextBlock
        {
            Text = caption,
            FontSize = SwatchCapSize,
            Width = d + CapPad * 2,
            Margin = new Thickness(-CapPad, 8, -CapPad, 0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Foreground = B(selected ? Ink : Muted),
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        };

        var cell = new StackPanel
        {
            Background = B(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = enabled ? 1 : 0.4,
        };
        cell.Children.Add(disc);
        cell.Children.Add(text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, caption);
        if (enabled) cell.Tapped += (_, _) => tap();
        return cell;
    }

    /// <summary>Text drawn INSIDE a circle — the unit abbreviations and the
    /// format samples. Newlines in <paramref name="t"/> stack, which is how the
    /// combined unit option shows "m / cm / mm".</summary>
    private TextBlock DiscText(string t, double size) => new()
    {
        Text = t,
        FontSize = size,
        LineHeight = size * 1.15,
        Foreground = B(Ink),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private FrameworkElement Chip(string label, bool selected, Action tap)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 14,
                Foreground = B(Ink),
                FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            },
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            BorderBrush = B(selected ? Ink : Line),
            Background = B(selected ? PanelAlt : Colors.Transparent),
        };
        b.Click += (_, _) => tap();
        return b;
    }

    // =======================================================================
    // The toggle switch (§3.2): 53 x 35, #78a19c when on, 120 ms ease
    // =======================================================================
    private FrameworkElement Toggle(bool on, Action<bool> changed, bool enabled = true)
    {
        bool state = on;
        double inset = (TogH - TogKnob) / 2;
        double travel = TogW - TogKnob - inset * 2;

        var track = new Border
        {
            Width = TogW,
            Height = TogH,
            CornerRadius = new CornerRadius(TogH / 2),
        };
        var knob = new Border
        {
            Width = TogKnob,
            Height = TogKnob,
            CornerRadius = new CornerRadius(TogKnob / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(inset, 0, 0, 0),
            Background = B(Colors.White),
            BorderThickness = new Thickness(1),
            BorderBrush = B(Color.FromArgb(0x2E, 0, 0, 0)),
        };
        var slide = new TranslateTransform();
        knob.RenderTransform = slide;

        // Held on the closure so the storyboard cannot be collected mid-run.
        Storyboard? run = null;

        void Paint(bool animate)
        {
            // Brush-level writes only: WinUI caches mutations to a live brush.
            track.Background = B(state
                ? (enabled ? ToggleOnColor : PageTheme.WithAlpha(ToggleOnColor, 0x66))
                : PageTheme.WithAlpha(Muted, 115));      // OnSurfaceMuted at 45%
            double to = state ? travel : 0;
            if (!animate) { slide.X = to; return; }
            try
            {
                var a = new DoubleAnimation
                {
                    To = to,
                    Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                };
                Storyboard.SetTarget(a, slide);
                Storyboard.SetTargetProperty(a, "X");
                run = new Storyboard();
                run.Children.Add(a);
                run.Completed += (_, _) => slide.X = to;
                run.Begin();
            }
            catch { slide.X = to; }
        }
        Paint(false);

        var host = new Grid
        {
            Width = TogW,
            Height = TogH,
            Background = B(Colors.Transparent),
            Opacity = enabled ? 1 : 0.45,
        };
        host.Children.Add(track);
        host.Children.Add(knob);
        if (enabled)
            host.Tapped += (_, _) =>
            {
                state = !state;
                Paint(true);
                try { changed(state); } catch { }
            };
        return host;
    }

    private FrameworkElement ToggleRow(string label, bool on, Action<bool> changed,
                                       bool enabled = true, string? tip = null)
    {
        var row = new Grid { Padding = new Thickness(0, 8, 0, 8), Background = B(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 15,
            Foreground = B(enabled ? Ink : Muted),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
        });

        var t = Toggle(on, changed, enabled);
        t.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(t, 1);
        row.Children.Add(t);

        if (tip != null) ToolTipService.SetToolTip(row, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, label);
        return row;
    }

    // =======================================================================
    // Bridges to other agents' surfaces
    // =======================================================================
    /// <summary>SHIM for <c>PaperTextures.GroundOf</c>, which the paper-texture
    /// rebuild is due to publish. It is not in this worktree yet, so the ground
    /// is resolved through the overload that IS there — which already answers
    /// with the fixed grounds for Blueprint / Brown / Darkprint and with the
    /// option's own colour for everything else. Delete the body and call
    /// <c>PaperTextures.GroundOf(o.Id)</c> the moment that lands.</summary>
    private static Color GroundOf(PaperOption o) =>
        PaperTextures.Ground(o.Id, ColorUtil.Parse(o.Background));

    private static Brush? PreviewBrush(Microsoft.Graphics.Canvas.UI.Xaml.CanvasImageSource? src) =>
        src == null ? null : new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill };

    // =======================================================================
    // Authored vector marks (never an emoji, never a glyph font). 24 grid.
    // =======================================================================
    private const string SwapGeometry =
        "M3 8 H18 M14.6 4.6 L18 8 L14.6 11.4 M21 16 H6 M9.4 12.6 L6 16 L9.4 19.4";

    private const string EllipsisGeometry =
        "M5 12 m -1.9,0 a 1.9,1.9 0 1 0 3.8,0 a 1.9,1.9 0 1 0 -3.8,0 " +
        "M12 12 m -1.9,0 a 1.9,1.9 0 1 0 3.8,0 a 1.9,1.9 0 1 0 -3.8,0 " +
        "M19 12 m -1.9,0 a 1.9,1.9 0 1 0 3.8,0 a 1.9,1.9 0 1 0 -3.8,0";

    /// Do Nothing: the barred circle.
    private const string DoNothingGeometry =
        "M12 3.4 A8.6 8.6 0 1 1 11.99 3.4 Z M5.9 5.9 L18.1 18.1";

    /// Nudge: the four-way move cross.
    private const string NudgeGeometry =
        "M12 3 V21 M3 12 H21 M12 3 L9.3 5.7 M12 3 L14.7 5.7 M12 21 L9.3 18.3 M12 21 L14.7 18.3 " +
        "M3 12 L5.7 9.3 M3 12 L5.7 14.7 M21 12 L18.3 9.3 M21 12 L18.3 14.7";

    /// Slice: the cut stroke with its crossing blade.
    private const string SliceGeometry = "M3.4 20.6 L20.6 3.4 M8.2 8.6 L15.4 15.8";

    /// Rotate: a three-quarter arc with an arrowhead on its open end.
    private const string RotateGeometry =
        "M19.6 12 A7.6 7.6 0 1 1 12 4.4 M12 4.4 L8.8 1.8 M12 4.4 L8.8 7.0";
}
