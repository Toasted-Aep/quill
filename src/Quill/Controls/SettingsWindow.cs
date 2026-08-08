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
        /// <summary>The ground the WHOLE SHELL derives from — which is not always
        /// the page's. MainWindow.ResolveGround honours <c>ThemeSource</c>: a
        /// pinned Light or Dark (and the true black under OLED) beats the paper.
        /// Without this the panel pushed the PAGE's ground into
        /// <see cref="PageTheme"/> every time it opened, which dragged a pinned
        /// theme back to the paper's colour and repainted the dial and the bars
        /// with it.</summary>
        public Func<Color>? Ground { get; init; }

        /// <summary>Mouse mode by its <c>MouseMode</c> enum name (§10.5 item 29,
        /// which moves the top bar's mouse-mode menu in here as circles).</summary>
        public Func<string>? MouseMode { get; init; }
        public Action<string>? SetMouseMode { get; init; }

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

    // §3.1's type scale. Every one of these goes through T() before it reaches a
    // TextBlock: §10.5 item 22 says the panel's font is too big, and what §3.1
    // actually pins is the RATIO between a heading, a sub-heading and a caption —
    // so one factor moves the whole scale and the reference's proportions survive.
    private const double HeadSize = 30;   // collapsible section heading (§3.1)
    private const double SubHeadSize = 17;
    private const double CaptionSize = 15;
    private const double SwatchCapSize = 13;
    private const double SubTabSize = 18;
    private const double BodySize = 15;   // labels, toggle rows, links, fields

    /// <summary>Which page's type scale is in force. Set by the tab builders, so
    /// the developer override in §10.5 item 22 can be per page.</summary>
    private string _page = "Workspace";

    private double T(double dip) => Math.Round(dip * PanelFonts.ScaleFor(_h.Library(), _page), 1);

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

    /// <summary>One collapsible section, kept so it can be rebuilt ON ITS OWN.
    /// §10.5 item 20's lag is a whole-panel rebuild on every tap; this is the
    /// handle that makes a tap cost one section instead.</summary>
    private sealed class Sec
    {
        public required Func<UIElement> Build;
        public required Border Body;
        /// <summary>Which panel page this section belongs to, so a section filled
        /// LATER — on expand, or on a Touch while the other tab is showing — is
        /// built at its own page's type scale rather than at whichever tab last
        /// ran its builder.</summary>
        public required string Page;
        /// <summary>Opens or closes the section, chevron and all. Supplied by
        /// <see cref="Section"/> so a link elsewhere in the panel can reveal a
        /// section without reaching into its head row.</summary>
        public Action<bool>? SetOpen;
        public bool Filled;
    }

    private readonly Dictionary<string, Sec> _sections = new();

    /// <summary>Re-entrancy guard on <see cref="Refresh"/>. Setting the ground
    /// raises PageTheme.Changed SYNCHRONOUSLY, and both this panel and the window
    /// it lives in listen — so an unguarded Refresh rebuilt the tab three times
    /// for one tap on a paper swatch.</summary>
    private bool _refreshing;

    /// <summary>Set when the measurement SYSTEM has just changed and no unit in
    /// the new system has been picked yet. §10.5 item 21: switching category must
    /// not auto-select the first item, and the combined option IS the first item,
    /// so "keep whatever was selected" is not enough on its own.</summary>
    private bool _unitUnchosen;

    /// <summary>Rendered paper and grid swatches, keyed by what they depend on.
    /// Each one is a Win2D CanvasImageSource; there are fifteen of them in the
    /// Canvas section alone, and rebuilding all fifteen on every tap was most of
    /// what §10.5 item 20 calls lag.</summary>
    private readonly Dictionary<string, Brush> _previews = new();

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

        // The two things that can repaint this panel from outside it. The ground
        // change is a full repaint - every colour in the panel moved. The tool
        // surface is one section's worth.
        PageTheme.Changed += () => { if (IsOpen) Refresh(); };
        ToolSurfaceService.Changed += _ => { if (IsOpen) Touch("Tool Setup"); };
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
    /// colours at build time exactly like the tree, the pen strip and the dial.
    ///
    /// <para>§10.5 item 20. Two things were wrong and both are here. The tab was
    /// rebuilt THREE times for one tap, because setting the ground raises
    /// PageTheme.Changed synchronously and this panel, the window around it and
    /// the caller all responded; and the rebuild reset the scroll offset, so the
    /// panel jumped to the top whenever anything was picked. Now: one rebuild,
    /// and the reader stays where they were.</para></summary>
    public void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _restoreArmed = false;
            _previews.Clear();          // the ground may have moved under them
            // Exactly ONE rebuild. Setting the ground raises PageTheme.Changed
            // synchronously and the window answers it with a rebuild of its own;
            // so might the caller, before ever reaching this method. The window's
            // revision counter is the only honest way to know whether the content
            // has already been rebuilt during this call.
            int rev = _win.ContentRevision;
            SyncGround();
            if (_win.ContentRevision == rev) _win.RefreshContent(preserveScroll: true);
        }
        finally { _refreshing = false; }
    }

    /// <summary>Rebuild NAMED SECTIONS and nothing else — the surgical update
    /// §10.5 item 20 asks for. A section that is currently collapsed is simply
    /// emptied, so it costs nothing now and is correct when it is next opened.
    /// Nothing here touches the scroller, so the reader does not move at all.</summary>
    private void Touch(params string[] titles)
    {
        foreach (var t in titles)
        {
            if (!_sections.TryGetValue(t, out var s)) continue;
            if (_open.TryGetValue(t, out bool open) && open) Fill(s);
            else { s.Body.Child = null; s.Filled = false; }
        }
    }

    private void Fill(Sec s)
    {
        string was = _page;
        _page = s.Page;
        try { s.Body.Child = s.Build(); }
        catch { s.Body.Child = Caption("This section could not be built."); }
        finally { _page = was; }
        s.Filled = true;
    }

    /// <summary>Reveals a section from elsewhere in the panel.</summary>
    private void Expand(string title)
    {
        _open[title] = true;
        if (_sections.TryGetValue(title, out var s)) s.SetOpen?.Invoke(true);
    }

    /// <summary>A rendered swatch, kept until the ground moves. Each one is a
    /// Win2D CanvasImageSource and there are fifteen in the Canvas section, so
    /// re-rendering them on every tap is most of what §10.5 item 20 calls lag.
    /// Brushes are not UIElements, so one can safely outlive the Ellipse it was
    /// painted into and be handed to its replacement.</summary>
    private Brush? Cached(string key, Func<Brush?> make)
    {
        if (_previews.TryGetValue(key, out var got)) return got;
        var made = make();
        if (made != null) _previews[key] = made;
        return made;
    }

    /// <summary>Point <see cref="PageTheme"/> at the ground the shell is on.
    ///
    /// <para>That is <b>not</b> unconditionally the page's. MainWindow owns the
    /// answer — <c>ResolveGround</c> honours <c>ThemeSource</c>, so a pinned
    /// Light or Dark (and the true black that OLED black puts under Dark) wins
    /// over the paper — and this panel asks it rather than deciding for itself.
    /// It used to push <c>PaperTextures.Ground(page)</c> in directly, which meant
    /// opening Settings on a pinned theme dragged the whole shell's palette back
    /// to the paper's colour. The page fallback is kept only for a host built
    /// before <see cref="Host.Ground"/> existed.</para></summary>
    private void SyncGround()
    {
        try { PageTheme.SetGround(_h.Ground?.Invoke() ?? PaperTextures.Ground(_h.Page())); } catch { }
    }

    // =======================================================================
    // WORKSPACE  (§3.1)
    // =======================================================================
    private FrameworkElement BuildWorkspace()
    {
        _page = "Workspace";
        var stack = new StackPanel();
        stack.Children.Add(Section("Canvas", true, BuildCanvas));
        stack.Children.Add(Section("Artboard", false, BuildArtboard));
        stack.Children.Add(Section("Measurements", false, BuildMeasurements));
        stack.Children.Add(Section("Tool Setup", false, BuildToolSetup));
        stack.Children.Add(Section("Appearance", false, BuildAppearance));
        stack.Children.Add(Section("Developer", false, BuildDeveloper));
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
            Content = new TextBlock { Text = "Edit Grid", FontSize = T(BodySize), Foreground = B(Accent) },
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
                : Cached($"p:{o.Id}:{ColorUtil.ToHex(ground)}",
                         () => PreviewBrush(PaperTextures.Preview(o.Id, ground, (float)SwatchD))) ?? B(ground);

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
            //
            // Guarded because the host resolves the anchor with
            // TransformToVisual(RootGrid) and THIS cell lives in a Popup, which
            // is a sibling of RootGrid rather than a descendant. The transform
            // does resolve through the shared XamlRoot, but an unparented or
            // not-yet-arranged anchor throws - and an exception out of a Tapped
            // handler is not caught by anything above it, so it would take the
            // window down rather than just failing to open a picker.
            try
            {
                _h.PickColor(cell, swatchColor, c =>
                {
                    string hex = ColorUtil.ToHex(c);
                    lib.CustomPageColor = hex;
                    _h.SetPaper(null, hex);
                    _h.Save();
                    Refresh();
                });
            }
            catch { _h.Status("The colour picker could not be opened here."); }
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
            Brush fill = Cached($"g:{k}:{ColorUtil.ToHex(ground)}:{ColorUtil.ToHex(ink)}",
                                () => PreviewBrush(PaperTextures.GridPreview(k, ground, ink, (float)SwatchD))) ?? B(ground);
            strip.Children.Add(Circle(SwatchD, label, selected, () => { _h.SetGrid(k); Touch("Canvas"); }, fill: fill));
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
        var auto = new Button { Content = "Auto", FontSize = T(13), Height = 30, Padding = new Thickness(12, 0, 12, 0) };
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
            Touch("Artboard");
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
            Touch("Artboard");
        }
        wBox.LostFocus += (_, _) => Commit();
        hBox.LostFocus += (_, _) => Commit();

        // ---- preset chips -------------------------------------------------
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        chips.Children.Add(Chip("Infinite", infinite, () =>
        {
            _h.SetPageSize(PageSizePreset.Infinite, 0, 0, unit);
            Touch("Artboard");
        }));
        chips.Children.Add(Chip("1024x768",
            page is { PageSize: PageSizePreset.Custom, PageWidth: 1024, PageHeight: 768 },
            () => { _h.SetPageSize(PageSizePreset.Custom, 1024, 768, PageSizeUnit.Pixels); Touch("Artboard"); }));
        chips.Children.Add(Chip("A4", page?.PageSize == PageSizePreset.A4,
            () => { _h.SetPageSize(PageSizePreset.A4, 0, 0, unit); Touch("Artboard"); }));
        chips.Children.Add(Chip("1080p", page?.PageSize == PageSizePreset.Screen1080p,
            () => { _h.SetPageSize(PageSizePreset.Screen1080p, 0, 0, PageSizeUnit.Pixels); Touch("Artboard"); }));

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
            var item = new MenuFlyoutItem { Text = def.Name, FontSize = T(13) };
            item.Click += (_, _) => { _h.SetPageSize(def.Preset, def.Width, def.Height, def.Unit); Touch("Artboard"); };
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
        FontSize = T(BodySize),
        CornerRadius = new CornerRadius(10),
        TextAlignment = TextAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
        Text = value is double v ? (Math.Abs(v - Math.Round(v)) < 0.05 ? Math.Round(v).ToString("0") : v.ToString("0.##")) : "∞",
    };

    private TextBlock FieldTag(string t) => new()
    {
        Text = t,
        FontSize = T(BodySize),
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
                FontSize = T(SubTabSize),
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
                // §10.5 item 21: switching category must NOT auto-select the first
                // item. This used to write "" — the COMBINED option, which is the
                // first circle — so every category switch silently made a choice.
                // The stored unit is left alone (ActiveUnit still resolves it for
                // the artboard maths) and the row shows nothing selected until
                // the reader picks in the new system.
                if (sys == ActiveSystem()) return;
                lib.MeasureSystem = sys;
                _unitUnchosen = true;
                _h.Save();
                Touch("Measurements", "Artboard");
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
            () => { lib.MeasureFormat = "Full"; _h.Save(); Touch("Measurements", "Artboard"); }, inner: DiscText("6.5\npixels", 12)));
        fmt.Children.Add(Circle(SwatchD, "Abbreviated", lib.MeasureFormat != "Full",
            () => { lib.MeasureFormat = "Abbreviated"; _h.Save(); Touch("Measurements", "Artboard"); }, inner: DiscText("6.5 px", 14)));
        groups.Children.Add(fmt);

        groups.Children.Add(new Border
        {
            Width = 1,
            Background = B(Line),
            Margin = new Thickness(0, 4, 0, 24),
        });

        var prec = Strip();
        prec.Children.Add(Circle(SwatchD, "Rounded", lib.MeasurePrecision != "Tenths",
            () => { lib.MeasurePrecision = "Rounded"; _h.Save(); Touch("Measurements", "Artboard"); }, inner: DiscText("6", 20)));
        prec.Children.Add(Circle(SwatchD, "Tenths", lib.MeasurePrecision == "Tenths",
            () => { lib.MeasurePrecision = "Tenths"; _h.Save(); Touch("Measurements", "Artboard"); }, inner: DiscText("6.0", 18)));
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
        // §10.5 item 21, on the paint side: after a category switch NOTHING is
        // selected until the reader chooses. The stored tag is still what
        // ActiveUnit resolves against, so the numbers never go undefined.
        bool chosen = !_unitUnchosen && set.Any(u => u.Tag == cur);

        var strip = Strip();
        foreach (var opt in set)
        {
            var o = opt;
            bool on = chosen && o.Tag == cur;
            // The combined option shows its stack on separate lines, as §3.1
            // describes it ("m / cm / mm").
            string face = o.Tag == "" ? o.Label.Replace("/", "\n") : o.Label;
            double size = o.Tag == "" ? 15 : 20;
            strip.Children.Add(Circle(UnitD, o.Tag == "" ? "Combined" : PageSizes.UnitName(o.Maps), on, () =>
            {
                lib.MeasureUnit = o.Tag;
                _unitUnchosen = false;
                _h.Save();
                var p = _h.Page();
                // Keep the page's own entry unit in step, so the artboard fields
                // and the units row can never disagree. Routed through the host's
                // SetPageSize, which is the ONLY writer of PageUnit outside
                // MainWindow's own combo.
                if (p != null) _h.SetPageSize(p.PageSize, p.PageWidth, p.PageHeight, o.Maps);
                Touch("Measurements", "Artboard");
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
        // ToolSurfaceService raises Changed, and this panel answers it with a
        // Touch of THIS section — so the tap does not also ask for a rebuild.
        strip.Children.Add(Circle(UnitD, "Wheel", cur == ToolSurface.Wheel,
            () => ToolSurfaceService.Set(ToolSurface.Wheel),
            inner: Icons.Mark(Icons.SurfaceWheel, cur == ToolSurface.Wheel ? Ink : Muted, 40)));
        strip.Children.Add(Circle(UnitD, "Bar", cur == ToolSurface.Bar,
            () => ToolSurfaceService.Set(ToolSurface.Bar),
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
        box.Children.Add(Caption("Light, dark, or let the page decide."));

        // §10.5 item 19: "delete the dark appearance toggle. The theme row gets a
        // white circle named Light and the existing black circle renamed Dark."
        //
        // The toggle was not merely redundant — it said the same thing as the
        // circle row beside it and the two could DISAGREE, because the switch
        // wrote Theme while the circles wrote ThemeSource. There is now one
        // control and one answer. Light and Dark are PINNED GROUNDS in exactly
        // the sense §6 means: the shell derives its whole palette from whichever
        // colour is chosen here, the same way it derives it from a page's paper.
        // Picking either sets ThemeSource = "Manual", which is what makes
        // MainWindow.ResolveGround stop reading the page.
        string cur = lib.ThemeSource == "Page" ? "Page"
                   : lib.Theme == "System" ? "System"
                   : lib.Theme == "Light" ? "Light" : "Dark";

        // The swatches are the grounds the shell REALLY lands on, taken from the
        // same two constants ResolveGround uses — OLED black included, so the
        // Dark circle is true black when the reader has that on rather than a
        // near-black that lies about it.
        var light = Color.FromArgb(0xFF, 0xF7, 0xF6, 0xF1);
        var dark = lib.OledBlack ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                                 : Color.FromArgb(0xFF, 0x0F, 0x0E, 0x10);

        var strip = Strip();
        void Add(string tag, string label, Brush fill, string tip)
        {
            var cell = Circle(SwatchD, label, cur == tag, () =>
            {
                switch (tag)
                {
                    case "Page":
                        lib.ThemeSource = "Page";
                        break;
                    case "System":
                        lib.ThemeSource = "Manual";
                        lib.Theme = "System";
                        break;
                    default:                       // Light | Dark: a pinned ground
                        lib.ThemeSource = "Manual";
                        lib.Theme = tag;
                        break;
                }
                // ApplyTheme re-derives through ResolveGround, which is the only
                // thing in the app that knows what the ground now is.
                _h.ApplyTheme();
                _h.Save();
                Refresh();
            }, fill: fill);
            ToolTipService.SetToolTip(cell, tip);
            strip.Children.Add(cell);
        }

        Add("Page", "The page", B(PaperTextures.Ground(_h.Page())),
            "A Blueprint, Brown or Darkprint page puts Quill in dark mode; a white or Lightweight page puts it in light mode.");
        Add("Light", "Light", B(light),
            "A pinned light ground, whatever the paper is.");
        Add("Dark", "Dark", B(dark), lib.OledBlack
            ? "A pinned dark ground. OLED black is on, so this one is true black."
            : "A pinned dark ground, whatever the paper is.");
        Add("System", "Windows", Split(light, dark),
            "Track the Windows light/dark setting.");

        box.Children.Add(HRow(strip));
        return box;
    }

    // =======================================================================
    // DEVELOPER  (§10.5 item 22)
    // =======================================================================
    /// <summary>"Panel font is too big. Reduce it, and add a developer setting
    /// that allows changing the font of specific pages." Both halves are one
    /// mechanism: §3.1's type scale is multiplied by a factor, which defaults to
    /// 85% and can be pinned per page.</summary>
    private UIElement BuildDeveloper()
    {
        var lib = _h.Library();
        var box = new StackPanel();

        box.Children.Add(SubHead("Panel text size"));
        box.Children.Add(Caption(
            "Scales every panel's type together. 100% is the reference's own numbers — 30 DIP section headings, " +
            "17 DIP sub-headings, 15 DIP captions — which measured too large in the app, so Quill ships at 85%."));

        var all = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        foreach (int pct in new[] { 70, 80, 85, 100, 115 })
        {
            int p = pct;
            bool on = (int)Math.Round(PanelFonts.Clamp(lib.PanelFontScale <= 0 ? PanelFonts.Default : lib.PanelFontScale) * 100) == p;
            all.Children.Add(Chip(p + "%", on, () =>
            {
                lib.PanelFontScale = p / 100.0;
                _h.Save();
                Refresh();          // every string in the panel just changed size
            }));
        }
        box.Children.Add(HRow(all));

        box.Children.Add(Spacer(16));
        box.Children.Add(SubHead("Per page"));
        box.Children.Add(Caption(
            "Pins one page's scale. Inherit follows the setting above. Only the pages listed here read it — a page " +
            "that ignored the setting would be a control that does nothing."));

        foreach (var pageName in PanelFonts.Pages)
        {
            string name = pageName;
            var row = new Grid { Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = T(BodySize),
                Foreground = B(Ink),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var combo = new ComboBox { Width = 132, FontSize = T(14) };
            combo.Items.Add(new ComboBoxItem { Content = "Inherit", Tag = "" });
            foreach (int pct in new[] { 70, 80, 85, 100, 115 })
                combo.Items.Add(new ComboBoxItem { Content = pct + "%", Tag = pct.ToString() });
            string want = PanelFonts.Override(lib, name) is double d
                ? ((int)Math.Round(d * 100)).ToString() : "";
            // Selected by TAG, never by index — the preset list will grow.
            foreach (ComboBoxItem it in combo.Items)
                if ((string)it.Tag == want) { combo.SelectedItem = it; break; }
            combo.SelectedItem ??= combo.Items[0];
            combo.SelectionChanged += (_, _) =>
            {
                if ((combo.SelectedItem as ComboBoxItem)?.Tag is not string tag) return;
                PanelFonts.SetOverride(lib, name,
                    string.IsNullOrEmpty(tag) ? null : int.Parse(tag) / 100.0);
                _h.Save();
                Refresh();
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            box.Children.Add(row);
        }

        box.Children.Add(Spacer(10));
        box.Children.Add(Caption(
            "Storage folder, language, AI provider and the per-command key rebinds are under “All Quill Settings” on " +
            "the Interaction tab."));
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
            FontSize = T(BodySize),
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
        _page = "Interaction";
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
                Touch("Keyboard & Mouse");
            }));
        }
        box.Children.Add(chips);

        var edit = new HyperlinkButton
        {
            Content = new TextBlock { Text = "Edit shortcuts", FontSize = T(BodySize), Foreground = B(Accent) },
            Padding = new Thickness(0),
            Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        ToolTipService.SetToolTip(edit, "Opens the per-command rebind list at the bottom of this tab.");
        edit.Click += (_, _) =>
        {
            // Reveals the section in place. It used to rebuild the whole tab,
            // which threw the reader back to the top of it (§10.5 item 20).
            Expand("All Quill Settings");
            _h.Status("Shortcut rebinding is under “All Quill Settings”, at the foot of this tab.");
        };
        box.Children.Add(edit);

        // ---- §10.5 item 29: mouse modes -----------------------------------
        // "Mouse modes move into the Interaction page, presented as circles like
        // the other option groups." They were a flyout on the top bar, which §5
        // is emptying of tools; this is where they land. Wired through the host
        // rather than read off InkSurface so the top bar's own state stays in
        // step — SetMouseMode is MainWindow's, the same call the flyout made.
        if (_h.MouseMode != null && _h.SetMouseMode != null)
        {
            box.Children.Add(Spacer(16));
            box.Children.Add(SubHead("Mouse Mode"));
            box.Children.Add(Caption("What a mouse drag does on the page. The pen is unaffected by this."));

            string mm = _h.MouseMode() ?? "Auto";
            var modes = Strip();
            foreach (var m in MouseModes)
            {
                var mode = m;
                bool on = string.Equals(mm, mode.Tag, StringComparison.Ordinal);
                var cell = Circle(UnitD, mode.Label, on, () =>
                {
                    _h.SetMouseMode!(mode.Tag);
                    _h.Save();
                    Touch("Keyboard & Mouse");
                }, inner: Icons.Mark(mode.Glyph, on ? Ink : Muted, 34,
                                     stroked: mode.Stroked, thickness: 1.7));
                ToolTipService.SetToolTip(cell, mode.Tip);
                modes.Children.Add(cell);
            }
            box.Children.Add(HRow(modes));
        }

        return box;
    }

    /// <summary>§10.5 item 29. Tags are <c>MouseMode</c>'s own member names, so
    /// the host can round-trip them through Enum.Parse without a second table.</summary>
    private static readonly (string Tag, string Label, string Glyph, bool Stroked, string Tip)[] MouseModes =
    {
        ("Auto", "Normal", Icons.Mouse, false,
            "Click to select or focus a text box; drag empty page to rubber-band a selection."),
        ("Grab", "Grab", GrabGeometry, true,
            "Drag anywhere to pan the page, as a finger does."),
        ("Select", "Select", Icons.Select, false,
            "Drag a box to lasso strokes, wherever the drag starts."),
        ("Move", "Move", NudgeGeometry, true,
            "Drag images and shapes; drag a handle to resize one."),
    };

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
            Touch("Touch Input");
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
                Touch("Touch Input");
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
                FontSize = T(BodySize),
                Foreground = B(Ink),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var combo = new ComboBox { Width = 168, FontSize = T(14) };
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
            // §10.5 item 23: bigger margins around section titles.
            Padding = new Thickness(0, 24, 0, 14),
        };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = T(HeadSize),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = B(Ink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(chevron);

        var body = new Border
        {
            Margin = new Thickness(0, 0, 0, 14),
            Visibility = open ? Visibility.Visible : Visibility.Collapsed,
        };
        var sec = new Sec { Build = build, Body = body, Page = _page };
        _sections[title] = sec;

        void SetOpen(bool now)
        {
            _open[title] = now;
            if (now && !sec.Filled) Fill(sec);
            body.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            if (chevron.RenderTransform is RotateTransform rt) rt.Angle = now ? 180 : 0;
        }
        sec.SetOpen = SetOpen;

        // LAZY. A collapsed section is not built until it is opened, which is the
        // other half of §10.5 item 20: "All Quill Settings" alone is the entire
        // pre-rebuild settings dialog, and it was being constructed on every
        // repaint of a tab whose reader had never opened it.
        if (open) Fill(sec);

        head.Tapped += (_, _) => SetOpen(body.Visibility != Visibility.Visible);

        var section = new StackPanel();
        section.Children.Add(head);
        section.Children.Add(body);
        section.Children.Add(new Border { Height = 1, Background = B(Line) });
        return section;
    }

    // §10.5 item 23 — "bigger margins around section titles and their explanation
    // lines" — is these three margins, and item 22 is these three sizes.
    private TextBlock SubHead(string t) => new()
    {
        Text = t,
        FontSize = T(SubHeadSize),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = B(Ink),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Left,
        Margin = new Thickness(0, 8, 0, 0),
    };

    private TextBlock Caption(string t) => new()
    {
        Text = t,
        FontSize = T(CaptionSize),
        Foreground = B(Muted),
        TextWrapping = TextWrapping.Wrap,
        LineHeight = T(CaptionSize) * 1.4,
        Margin = new Thickness(0, 6, 0, 16),
    };

    private TextBlock Label(string t) => new()
    {
        Text = t,
        FontSize = T(BodySize),
        Foreground = B(Ink),
        Margin = new Thickness(0, 12, 0, 4),
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
        // §10.5 item 25: the wheel, the rail and the chaining are STRIP
        // behaviour, not settings-panel behaviour, so they come from the one
        // place that has them (StripScroll) rather than being re-specified here.
        var sv = StripScroll.Horizontal(content);

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
            FontSize = T(SwatchCapSize),
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
        // Slop-guarded rather than Tapped: every one of these circles lives in a
        // horizontal strip, and a Tapped handler fires at the end of a slow
        // sideways drag — so scrolling the swatch row used to change the paper.
        if (enabled) StripScroll.Tap(cell, tap);
        return cell;
    }

    /// <summary>Text drawn INSIDE a circle — the unit abbreviations and the
    /// format samples. Newlines in <paramref name="t"/> stack, which is how the
    /// combined unit option shows "m / cm / mm".</summary>
    private TextBlock DiscText(string t, double size) => new()
    {
        Text = t,
        FontSize = T(size),
        LineHeight = T(size) * 1.15,
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
                FontSize = T(14),
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
            FontSize = T(BodySize),
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
    /// <summary>The swatch fill. No longer a shim: the paper rebuild has landed,
    /// and <c>PaperTextures.Ground(id, fallback)</c> now resolves through
    /// <c>PaperTextures.GroundOf(PaperKind)</c> itself — the fixed ground for
    /// Blueprint / Brown Paper / Darkprint, and the page’s own colour for the
    /// white stocks and the plain-colour page.
    ///
    /// <para>Kept as one call rather than inlined because it is the single place
    /// this window decides what colour a paper IS, and §6 makes that the only
    /// input to the whole theme derivation.</para></summary>
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

    /// Grab (§10.5 item 29): the open hand — four fingers and a thumb over the
    /// palm. Stroked, because the mark IS an outline.
    private const string GrabGeometry =
        "M7.7 13.2 V6.4 A1.35 1.35 0 0 1 10.4 6.4 V11.2 " +
        "M10.4 11.2 V4.8 A1.35 1.35 0 0 1 13.1 4.8 V11.2 " +
        "M13.1 11.2 V5.6 A1.35 1.35 0 0 1 15.8 5.6 V11.2 " +
        "M15.8 11.2 V7.8 A1.35 1.35 0 0 1 18.5 7.8 V14.4 " +
        "C18.5 18.4 15.7 21.4 12.1 21.4 C9.2 21.4 7.2 19.6 6.1 17.2 " +
        "L4.6 14.1 A1.4 1.4 0 0 1 7.0 12.7 L7.7 14.0";
}
