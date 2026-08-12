using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
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

        // ---- §11.11's Stylus and Gestures tabs -----------------------------
        /// <summary>Push the eraser's behaviour and width at the live surface, and
        /// the shape recogniser's switch. Optional only so an older host
        /// construction still compiles; without them those rows would store a
        /// preference that changed nothing until the next launch.</summary>
        public Action<EraserStyle>? SetEraserStyle { get; init; }
        public Action<double>? SetEraserSize { get; init; }
        public Action<bool>? SetShapeRecognition { get; init; }

        /// <summary>The pen the Stylus tab's pressure curve belongs to, and the
        /// call that makes an edit to it live. Deliberately the SAME ApplyPreset
        /// the dial, the pen row and the Brushes panel are handed, so a curve
        /// edited here is the curve those three are drawing with.</summary>
        public Func<PenPreset?>? ActivePen { get; init; }
        public Action<PenPreset>? ApplyPen { get; init; }

        /// <summary>Grid opacity 0..1 (UI-SPEC-V3 §C). Null = the control hides.</summary>
        public Action<double>? SetGridOpacity { get; init; }
        /// <summary>Reinstall the keyboard layout after this panel changes the
        /// preset, the overrides or the master switch.</summary>
        public Action? ApplyKeyPreset { get; init; }

        // ---- 12: the grid editor -------------------------------------------
        /// <summary>Push a whole <see cref="GridSpec"/> at the page. ONE call
        /// rather than eight setters, because a 12.4 preset moves several fields
        /// at once and eight round trips would be eight canvas invalidations for
        /// one tap.</summary>
        public Action<GridSpec>? ApplyGrid { get; init; }

        /// <summary>12.5 moves 1-Point / 2-Point / 3-Point into the Grid Type
        /// row. The count (0 = none) is all this panel knows; MainWindow owns the
        /// CANVAS coordinates the points are placed at, because only it knows
        /// where the view and the artboard are.</summary>
        public Action<int>? SetPerspective { get; init; }

        /// <summary>12.6: dismiss the panel and enter the on-canvas
        /// vanishing-point editor. Null = the button does not appear.</summary>
        public Action? EditPoints { get; init; }
    }

    // =======================================================================
    // Geometry, straight off CONCEPTS-REF-2026-08-07 §3 / §9
    // =======================================================================
    private const double SwatchD = 69;    // background + grid circles (§3.1)
    private const double PresetD = 86;    // §12.2's preset circles
    private const double PreviewH = 300;  // §12.1's live preview strip
    private const double BackH = 46;      // §12.1's Back pill
    private const double TitleSize = 34;  // §12.1 item 4's page heading
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

    /// <summary>The panel this window was attached to. Kept only for its
    /// XamlRoot: §12.1's preview strip and §12.2's thumbnails are
    /// CanvasImageSources and go soft when they are rasterised at 96 DPI on a
    /// 125% display, and the strip is built before it has been arranged, so it
    /// has no XamlRoot of its own to ask.</summary>
    private readonly Panel? _root;

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

    // =======================================================================
    // §12 - the grid editor page
    // =======================================================================
    /// <summary>Non-null while the Workspace tab is showing a grid's EDITOR PAGE
    /// instead of its sections (§12.1). Navigation, not a repaint: going in and
    /// coming back are the only two things that legitimately rebuild this tab.</summary>
    private GridKind? _gridPage;

    /// <summary>The spec the open page is editing. One instance, mutated in
    /// place by every control on the page and handed straight to the preview -
    /// which is what makes a slider drag cost one image redraw rather than the
    /// wholesale rebuild §11.1 item 1 calls a 5/5 defect.</summary>
    private GridSpec? _spec;

    /// <summary>Which page <see cref="_spec"/> was read from.
    ///
    /// <para>The grid editor page is a SNAPSHOT: <see cref="GridSpec.FromPage"/>
    /// copies the page's fields into a bag that every control on the page then
    /// mutates in place, and <see cref="CommitGrid"/> writes the whole bag back
    /// through <see cref="Host.ApplyGrid"/> - which targets whatever page is
    /// CURRENT. So the page it was read from is part of the spec's identity and
    /// has to be carried with it.</para>
    ///
    /// <para>Without this the editor was a live weapon aimed at whatever page
    /// happened to be up: open it on page A, switch to page B, touch any control,
    /// and A's ENTIRE spec landed on B - spacing, divisions, weight, opacity,
    /// colour, angle, orientation, confine, and worst of all the KIND, which sets
    /// <c>p.Grid</c> and NULLS <c>p.Perspective</c>. A page the user was not even
    /// editing could lose its vanishing points to a nudge of the opacity slider
    /// on another page.</para></summary>
    private Guid? _specPageId;

    /// <summary>Set while a rebuild is already posted, so a run of page turns
    /// costs one rebuild rather than one each.</summary>
    private bool _rebuildQueued;

    /// <summary>The preview strip's surface and the element that sizes it. The
    /// strip is repainted by assigning a new CanvasImageSource to the Image;
    /// nothing above it in the tree is touched, so the reader does not move and
    /// no control is rebuilt.</summary>
    private Image? _previewImg;
    private Border? _previewHost;

    /// <summary>Re-bind hooks for the page's controls: a §12.4 preset moves the
    /// spacing and the divisions at once, and the boxes and sliders that show
    /// them have to follow WITHOUT being rebuilt. Each control registers how to
    /// repaint itself from the spec.</summary>
    private readonly List<Action> _gridBind = new();

    /// <summary>Guards the value box / slider round trip: setting the box's Text
    /// from the slider must not be read back as a fresh edit.</summary>
    private bool _gridSyncing;

    /// <summary>The window's default scroller inset, restored when the grid page
    /// closes. The page itself runs at zero so §12.1's preview strip can be the
    /// full panel width.</summary>
    /// <summary>§13.3: <i>"reduce page margins by 30%"</i>. Everything that sets
    /// the text column's left/right inset is a multiple of this, so the heading,
    /// the caption, the slider and the value box cannot drift apart - they all
    /// move together or none of them does.</summary>
    private const double MarginScale = 0.7;

    /// <summary>The window scroller's own inset. Vertical is untouched: §13.3
    /// asks for the PAGE margins, which is the left and the right.</summary>
    private static readonly Thickness ContentInset = new(14 * MarginScale, 10, 10 * MarginScale, 14);

    /// <summary>The tab body's own left/right padding, inside the scroller's.
    /// The two together are the whole distance from the panel's edge to the text,
    /// which is what §13.3's strips have to reach back across.</summary>
    private const double BodyPadX = 20 * MarginScale;

    public static SettingsWindow Attach(Panel host, Host h) => new(host, h);

    private SettingsWindow(Panel host, Host h)
    {
        _h = h;
        _root = host;
        // Wide enough for the legacy panel the Interaction tab still hosts (it
        // builds itself at 480 DIP), and tall like the reference.
        _win = FloatingWindow.Attach(host, 516, 724);
        _win.Title = "Settings";
        // §13.3's margin. Applied HERE and not only on the way out of the grid
        // editor page, which is the only place it used to be set - so the panel
        // ran on FloatingWindow's own default for its whole life and the
        // reduction never reached the screen.
        try { _win.ContentPadding = ContentInset; } catch { }
        _win.InfoRequested = () => _h.Status(
            "Workspace is the page: its paper, its grid, its artboard and the units everything is measured in. Interaction is the keyboard, the pen and what a finger does.");
        _win.Closed = () => DockChanged?.Invoke();
        // §11.11: four tabs, decided by the user. Workspace is judged correct as
        // built; Interaction was "messy" and is split three ways — what a finger
        // and a mouse do stays here, multi-touch gestures get their own tab, and
        // everything about the pen gets a third.
        _win.SetTabs(new (string, Func<FrameworkElement>)[]
        {
            ("Workspace", BuildWorkspace),
            ("Interaction", BuildInteraction),
            ("Gestures", BuildGestureTab),
            ("Stylus", BuildStylus),
        });

        // The two things that can repaint this panel from outside it. The ground
        // change is a full repaint - every colour in the panel moved. The tool
        // surface is one section's worth.
        // A ground move is the WINDOW's rebuild to do, not this panel's.
        // FloatingWindow subscribes to the same event first and answers it with
        // RefreshContent(preserveScroll: true); this handler used to call
        // Refresh() as well, which rebuilt a second time - and the second
        // rebuild snapshots the scroller's offset to preserve it, at a moment
        // when the first rebuild has already swapped the content out and its own
        // restore has not landed. It preserved a zero, and the panel went to the
        // top while looking like it had done the right thing.
        //
        // All this has left to do is drop the rendered swatches, which were
        // painted against the old ground.
        PageTheme.Changed += () => { if (IsOpen) _previews.Clear(); };
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
        // Re-asserted: the grid editor page runs the scroller's padding at zero
        // for its full-bleed preview strip, and a panel hidden while that page
        // was open would come back edge-to-edge everywhere.
        if (_gridPage == null) { try { _win.ContentPadding = ContentInset; } catch { } }
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
    /// <summary>Re-draw ONLY the section the mouse-mode circles live in.
    ///
    /// <para>CONCEPTS-REF 11.1 item 1 - the user's 5/5 defect - is a panel that
    /// rebuilds wholesale on every change. <see cref="Refresh"/> is exactly that
    /// rebuild (it is for a theme move, where the whole palette really has
    /// changed), so the mouse-mode circles must NOT go through it just because
    /// the mode can now be set from the dial as well as from here. This is the
    /// surgical path, and it is a no-op while the panel is closed.</para></summary>
    public void TouchMouseMode()
    {
        if (IsOpen) Touch("Keyboard & Mouse");
    }

    /// <summary>Run something that MAY move the ground, and end with exactly one
    /// rebuild however it goes.
    ///
    /// <para>Picking a paper, a custom page colour or a theme moves the ground,
    /// which raises <see cref="PageTheme.Changed"/> synchronously, which the
    /// WINDOW answers with its own scroll-preserving rebuild. Calling
    /// <see cref="Refresh"/> afterwards therefore rebuilt a second time, and a
    /// second rebuild reads the scroll offset back out of a scroller whose
    /// content the first one has just replaced - so it preserves a zero. The
    /// window's revision counter is the only honest way to ask whether the
    /// rebuild has already happened.</para>
    ///
    /// <para>Picking the colour that is already set moves nothing, so no event is
    /// raised and this does the single rebuild itself - the selection ring still
    /// has to move.</para></summary>
    private void GroundAction(Action mutate)
    {
        // Dropped BEFORE the mutation: the window may rebuild inside it, and a
        // swatch rendered against the old ground must not be handed to it. Every
        // key in this cache carries the ground it was painted for, so a stale
        // entry could never be returned anyway - this only stops it being kept.
        _previews.Clear();
        int rev = _win.ContentRevision;
        try { mutate(); } catch { }
        if (_win.ContentRevision == rev) _win.RefreshContent(preserveScroll: true);
    }

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
        // §12.1: a grid's page REPLACES the tab's content. The window's own
        // header - close, tabs, (i) - sits above this and is untouched.
        if (_gridPage is GridKind open) return BuildGridPage(open);
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

        // §12.1: Edit Grid opens the EDITOR PAGE for whichever grid the page is
        // on. It used to reveal an inline block of three sliders; §12 replaces
        // that entirely, so there is nothing left here to toggle.
        editGrid.Click += (_, _) =>
        {
            if (GridSpec.KindOf(_h.Page()) is GridKind k) OpenGridPage(k);
            else _h.Status("Pick a grid first — there is nothing to edit on a page with no grid.");
        };

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
                GroundAction(() => _h.SetPaper(o.Id, o.Background)), fill: fill));
        }

        return HRow(strip, "paper");
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
                GroundAction(() => _h.SetPaper(null, stored));
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
                    GroundAction(() =>
                    {
                        _h.SetPaper(null, hex);
                        _h.Save();
                    });
                });
            }
            catch { _h.Status("The colour picker could not be opened here."); }
        }, fill: B(swatchColor));

        ToolTipService.SetToolTip(cell, everSet
            ? (selected ? "Press again to edit this colour." : "Applies your custom colour. Press it again to edit it.")
            : "Pick a custom page colour.");
        return cell;
    }

    /// <summary>The Grid Type row (§3.1), with §12.5's three perspective kinds
    /// folded in beside the lattices. <c>null</c> is No Grid.
    ///
    /// <para>The perspective kinds are NOT GridType members and must never
    /// become any: that enum serialises as its integer and the model comment is
    /// explicit that perspective is an overlay on <c>NotePage.Perspective</c>.
    /// §12.5 asks for them in this ROW, which is a UI question, not a storage
    /// one.</para></summary>
    private static readonly GridKind?[] GridRowKinds =
    {
        null, GridKind.Dot, GridKind.Graph, GridKind.Lined, GridKind.Isometric,
        // 12.10: the triangle grid was missing from the row entirely.
        GridKind.Triangle,
        GridKind.OnePoint, GridKind.TwoPoint, GridKind.ThreePoint,
    };

    private FrameworkElement BuildGridRow()
    {
        var strip = Strip();
        var page = _h.Page();
        var ground = PaperTextures.Ground(page);
        var current = GridSpec.KindOf(page);
        float dpi = Dpi();

        // The row repaints its own selection rather than asking the section to
        // rebuild. A rebuild would be correct-looking and WRONG: this strip
        // scrolls horizontally (§12.5 added three more circles to it), and
        // rebuilding it throws the reader back to No Grid every time they pick
        // something - the same defect as §11.1 item 1, one axis over.
        var paints = new List<(GridKind? Kind, Action<bool, Brush?> Paint)>();

        foreach (var entry in GridRowKinds)
        {
            var k = entry;
            string label = k is GridKind gk ? GridPresets.RowLabel(gk) : "No Grid";

            Brush fill;
            if (k is GridKind kind)
            {
                // Each circle shows its OWN grid, drawn under a default spec for
                // that kind - which is what makes the three perspective circles
                // read as a horizon with fans rather than as one shared glyph.
                var thumb = new GridSpec { Kind = kind };
                GridPresets.Apply(thumb, GridPresets.Names(kind).FirstOrDefault() ?? "Custom");
                fill = Cached($"gk:{kind}:{ColorUtil.ToHex(ground)}",
                              () => PreviewBrush(GridArt.Thumb(thumb, ground, (float)SwatchD, dpi)))
                       ?? B(ground);
            }
            else fill = B(ground);

            Action<bool, Brush?>? paint = null;
            strip.Children.Add(Circle(SwatchD, label, current == k, () =>
            {
                // §9.5's idiom, already the law elsewhere in this panel: the first
                // press APPLIES, a press on the one already selected EDITS.
                if (current == k && k is GridKind already) { OpenGridPage(already); return; }
                SelectGridKind(k);
                current = k;
                foreach (var (kind2, repaint) in paints) repaint(kind2 == current, null);
            }, fill: fill, bind: p => paint = p));

            if (paint is { } got) paints.Add((k, got));
        }
        return HRow(strip, "gridtype");
    }

    /// <summary>One-of selection across the whole row. A perspective kind and a
    /// lattice CAN coexist in the model, and the Precision menu still offers that
    /// - but §12.5 puts them in one row of circles, and a row of circles that
    /// lights up two at once is not a choice.</summary>
    private void SelectGridKind(GridKind? kind)
    {
        if (kind is GridKind k && k is GridKind.OnePoint or GridKind.TwoPoint or GridKind.ThreePoint)
        {
            _h.SetGrid(GridType.None);
            _h.SetPerspective?.Invoke(k == GridKind.OnePoint ? 1 : k == GridKind.TwoPoint ? 2 : 3);
            return;
        }
        _h.SetPerspective?.Invoke(0);
        // 12.10: picking one of the two angled grids seeds ITS default angle -
        // 30 for isometric, 60 for the equilateral triangle case - rather than
        // carrying the other one's over.
        if (kind is GridKind.Isometric or GridKind.Triangle && _h.ApplyGrid != null)
        {
            var seed = GridSpec.FromPage(_h.Page(), kind.Value);
            seed.Angle = GridSpec.DefaultAngle(kind.Value);
            seed.Preset = "Custom";
            _h.ApplyGrid(seed);
            return;
        }
        _h.SetGrid(kind switch
        {
            GridKind.Dot => GridType.Dotted,
            GridKind.Lined => GridType.Lines,
            GridKind.Graph => GridType.Square,
            GridKind.Isometric => GridType.Isometric,
            GridKind.Triangle => GridType.Triangle,
            _ => GridType.None,
        });
    }

    // =======================================================================
    // §12 - THE GRID EDITOR PAGE
    //
    // §12.1's shell: the window header (which this class does not own) stays,
    // then a live preview strip, then a Back pill straddling its lower edge,
    // then the grid's name at 34 DIP bold, then §12.3's sections.
    //
    // Only two things in this region rebuild the tab: going IN and coming BACK.
    // Every control below the strip mutates ONE GridSpec and repaints ONE image
    // - §11.1 item 1's 5/5 defect is a panel that rebuilds wholesale on every
    // change, and a preview that follows a slider drag is exactly the shape of
    // change that would reintroduce it.
    // =======================================================================

    /// <summary>§12.1/§12.2's paper-white controls: the Back pill, the value
    /// boxes and the slider knob.
    ///
    /// <para><b>These were literally <c>Colors.White</c>, and that was right
    /// until the ground moved under it.</b> The case for white rested on this
    /// window being hue-neutral - §6 said panels are flat whatever the page's
    /// colour, and <see cref="PageTheme.Panel"/> was a neutral ramp, so a white
    /// field could not clash with anything. It was measured that way: on a
    /// Blueprint page the panel body sampled #3D3D3D with zero chroma at every
    /// point. <c>bc7deb6</c> changed that. Panel now CARRIES THE GROUND'S HUE -
    /// cream on warm papers, dark navy on Blueprint, dark brown on Brown Paper -
    /// so the premise is gone and a neutral white field is now the one surface
    /// on the page belonging to nothing.</para>
    ///
    /// <para>So they are derived, and derived from the GROUND rather than from
    /// the panel, because what §12.2 is describing is paper: a raised light
    /// field that a near-black label sits on. Lightening the ground keeps the
    /// reference's reading - on an ivory page this lands within a shade of white
    /// exactly as the captures show - while giving a Blueprint page a cool paper
    /// and a Brown Paper page a warm one, which is what every other surface in
    /// this window now does.</para>
    ///
    /// <para>The ink is the field taken almost to black, so §12.1's
    /// <i>"near-black"</i> comes out of the same family rather than being a
    /// second, unrelated grey.</para>
    ///
    /// <para>Properties, not constants: the ground moves while the panel is
    /// alive, and a <c>static readonly</c> would freeze the first page's
    /// paper.</para></summary>
    private static Color FieldFill => Mix(PageTheme.Ground, Colors.White,
                                          PageTheme.IsDark ? 0.86 : 0.72);

    private static Color FieldInk => Mix(FieldFill, Colors.Black, 0.90);

    private void OpenGridPage(GridKind kind)
    {
        BindGridPage(kind);
        _win.RefreshContent();      // a NAVIGATION: the top is the right place to land
    }

    /// <summary>Point the editor page at the CURRENT page's grid, without
    /// touching the tree. Split out of <see cref="OpenGridPage"/> so opening the
    /// page and following a page switch cannot drift apart: the rebind is the
    /// whole of what "open" ever did besides the rebuild, and a partial rebind is
    /// exactly the bug this guards.</summary>
    private void BindGridPage(GridKind kind)
    {
        _gridPage = kind;
        _spec = GridSpec.FromPage(_h.Page(), kind);
        _specPageId = _h.Page()?.Id;
        // Every element of the page is rebuilt from scratch, so nothing may be
        // left pointing at the old tree - WinUI enforces one parent per element.
        _previewImg = null;
        _previewHost = null;
        _gridBind.Clear();
        // The strip is full-bleed, so the window's own scroller inset comes off
        // for the duration and goes back on the way out.
        try { _win.ContentPadding = new Thickness(0); } catch { }
    }

    private void CloseGridPage()
    {
        DropGridPage();
        _win.RefreshContent();
    }

    /// <summary>Forget the open grid page WITHOUT rebuilding. The state half of
    /// <see cref="CloseGridPage"/>, so a page switch under a CLOSED panel can
    /// drop a spec that no longer belongs to anything - a closed panel's stale
    /// spec is exactly as dangerous as an open one's the moment it is shown
    /// again, and <see cref="Show"/> rebuilds anyway.</summary>
    private void DropGridPage()
    {
        _gridPage = null;
        _spec = null;
        _specPageId = null;
        _previewImg = null;
        _previewHost = null;
        _gridBind.Clear();
        try { _win.ContentPadding = ContentInset; } catch { }
    }

    /// <summary>The page under this panel changed. Called by MainWindow from the
    /// ONE place <c>_curPage</c> is assigned.
    ///
    /// <para><b>Why this exists.</b> Every other control in the Workspace tab
    /// writes through a host delegate that targets the CURRENT page, so a stale
    /// tab is merely wrong-LOOKING - the ring sits under the paper the previous
    /// page used, and the next tap still does the right thing to the right page.
    /// The grid editor is the one surface that edits a snapshot, so a stale one
    /// writes the OLD page's settings onto the NEW page.</para>
    ///
    /// <para><b>The editor FOLLOWS rather than closing.</b> The rest of this
    /// panel is already live-bound to whatever page is up, so an editor pinned to
    /// the page you left is the anomaly, not the fix. The rebind is complete
    /// because it is the SAME path <see cref="OpenGridPage"/> takes - a fresh
    /// <see cref="GridSpec.FromPage"/> and a full rebuild of the page's controls,
    /// which is needed regardless: the new page's grid may be a different KIND,
    /// and 12.3 gives each kind a different set of controls.</para>
    ///
    /// <para><b>The one case that cannot follow</b> is a new page with no grid at
    /// all. There is nothing to edit, so the page closes back to the Workspace
    /// sections rather than showing an editor for a grid that is not there.</para>
    ///
    /// <para><b>The rebuild is POSTED, not run here.</b> A page turn also pushes
    /// the new ground, and a ground that actually moved makes the WINDOW rebuild
    /// itself synchronously. Two rebuilds back to back is the 11.1 item 1 defect
    /// in its subtlest form: the second one reads the scroll offset back out of a
    /// scroller whose content the first has just replaced and whose restoring
    /// ChangeView has not landed, so it preserves a zero and the panel jumps to
    /// the top while looking like it did the right thing. Letting the message
    /// pump drain first means the offset is settled before it is read. The STATE
    /// is rebound synchronously, so no write can go astray in the meantime.</para></summary>
    public void OnPageChanged()
    {
        _restoreArmed = false;
        if (_gridPage != null)
        {
            if (GridSpec.KindOf(_h.Page()) is GridKind kind) BindGridPage(kind);
            else DropGridPage();
        }
        if (IsOpen) QueueRebuild();
    }

    /// <summary>One rebuild after the current message has drained, however many
    /// times it is asked for.</summary>
    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        var q = _root?.DispatcherQueue;
        if (q == null) return;
        _rebuildQueued = true;
        try
        {
            q.TryEnqueue(() =>
            {
                _rebuildQueued = false;
                if (!IsOpen) return;
                // A grid page is a NAVIGATION and lands at the top; the sections
                // are a repaint and keep the reader where they were.
                _win.RefreshContent(preserveScroll: _gridPage == null);
            });
        }
        catch { _rebuildQueued = false; }
    }

    private FrameworkElement BuildGridPage(GridKind kind)
    {
        // Every element on this page is created HERE. WinUI enforces one parent
        // per element, so nothing may be held on a field across a rebuild.
        _gridBind.Clear();
        if (_spec == null)
        {
            _spec = GridSpec.FromPage(_h.Page(), kind);
            _specPageId = _h.Page()?.Id;
        }
        var spec = _spec;
        spec.Kind = kind;
        var parts = GridPresets.Parts(kind);

        var root = new Grid
        {
            Background = B(PanelFill),
            RequestedTheme = PageTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(PreviewH) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 1. the live preview strip (§12.1 item 2) --------------------
        var host = new Border { Height = PreviewH, Background = B(BandColour()) };
        var img = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        host.Child = img;
        _previewHost = host;
        _previewImg = img;
        host.SizeChanged += (_, _) => RedrawPreview();
        Grid.SetRow(host, 0);
        root.Children.Add(host);

        // ---- 2. the page body (§12.1 items 4-5) --------------------------
        // Top margin clears the Back pill's overhang: it hangs half of its
        // height below the strip and the heading starts under that.
        var body = new StackPanel { Margin = new Thickness(BodyPadX, BackH / 2 + 18, BodyPadX, 30) };

        body.Children.Add(new TextBlock
        {
            Text = GridPresets.Title(kind),
            FontSize = T(TitleSize),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = B(Ink),
            Margin = new Thickness(0, 0, 0, 4),
        });

        // §12.3's order, read across the table: preset, spacing, divisions,
        // vanishing, density, line weight, colour, opacity, orientation,
        // confine. A part this kind does not carry is ABSENT, never disabled.
        if ((parts & GridPart.Preset) != 0)
        {
            body.Children.Add(SubHead("Presets"));
            body.Children.Add(BuildPresetRow(spec));
        }

        var unit = ActiveUnit();
        double upi = _h.Page()?.UnitsPerInch > 0 ? _h.Page()!.UnitsPerInch : 96;

        if ((parts & GridPart.Spacing) != 0)
            body.Children.Add(NumRow("Spacing",
                "Set the spacing of your grid. The units are determined by the document units.",
                4, 400, 1,
                () => spec.Spacing, v => spec.Spacing = v,
                v => $"{FromWorld(v, unit, upi):0.#} {PageSizes.Abbrev(unit)}",
                t => ParseLeadingNumber(t) is double d ? ToWorld(d, unit, upi) : null));

        if ((parts & GridPart.Divisions) != 0)
            body.Children.Add(NumRow("Divisions",
                "Set the number of divisions between main lines. Set value to 1 to only show the main lines.",
                1, 32, 1,
                () => spec.Divisions, v => spec.Divisions = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString("0"),
                t => ParseLeadingNumber(t)));

        // 12.10's angle row, between spacing and the rest: it is the second
        // thing that decides what an isometric or a triangle grid IS.
        //
        // 12.11: the caption says which family STAYS PUT, because the control
        // reads as a duplicate of Orientation further down this same page until
        // you know that. It is the difference the user reported missing.
        if ((parts & GridPart.Angle) != 0)
            body.Children.Add(NumRow("Angle",
                kind == GridKind.Triangle
                    ? "Set the slope of the diagonals. The horizontal lines stay where they " +
                      "are, so this changes the shape of each triangle. 60\u00b0 is equilateral."
                    : "Set the slope of the diagonals. The vertical lines stay where they " +
                      "are, so this changes the shape of each cell. 30\u00b0 is true isometric.",
                GridLattice.MinAngle, GridLattice.MaxAngle, 1,
                () => spec.Angle, v => spec.Angle = v,
                v => $"{v:0.#}\u00b0",
                t => ParseLeadingNumber(t)));

        if ((parts & GridPart.Vanishing) != 0)
            body.Children.Add(BuildVanishingBlock());

        if ((parts & GridPart.Density) != 0)
            body.Children.Add(NumRow("Density",
                "Set the number of vanishing lines per point.",
                4, 96, 1,
                () => spec.Density, v => spec.Density = (int)Math.Round(v),
                v => ((int)Math.Round(v)).ToString("0"),
                t => ParseLeadingNumber(t)));

        if ((parts & GridPart.Weight) != 0)
            body.Children.Add(NumRow("Line Weight", null,
                0.25, 8, 0.25,
                () => spec.Weight, v => spec.Weight = v,
                v => $"{v:0.##} pts",
                t => ParseLeadingNumber(t)));

        if ((parts & GridPart.Colour) != 0)
            body.Children.Add(BuildGridColourBlock(spec));

        if ((parts & GridPart.Opacity) != 0)
            body.Children.Add(NumRow("Opacity", null,
                0, 100, 1,
                () => spec.Opacity * 100, v => spec.Opacity = v / 100.0,
                v => $"{(int)Math.Round(v)}%",
                t => ParseLeadingNumber(t)));

        // §12.3, stated by the user in words: "if rotation does not change the
        // form, do not have it in page." A square grid and a dot grid are the
        // same grid after a 90 degree turn, so this block is not built for them
        // at all - it is not built and disabled, it is not there.
        if ((parts & GridPart.Orientation) != 0)
            body.Children.Add(BuildOrientationBlock(spec));

        if ((parts & GridPart.Confine) != 0)
            body.Children.Add(BuildConfineRow(spec));

        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // ---- 3. the Back pill, LAST ---------------------------------------
        // Added last so it paints over the body it hangs into: a Grid draws its
        // children in the order they were added, whatever row they sit in.
        var back = BuildBackPill();
        Grid.SetRow(back, 0);
        root.Children.Add(back);

        RedrawPreview();
        return root;
    }

    /// <summary>§12.1 item 3, copied exactly: a white pill with fully rounded
    /// ends (radius = half its height), ~46 DIP tall, inset 20 DIP from the
    /// panel's left edge, straddling the preview strip's bottom edge half in and
    /// half out, "&lt; Back" in near-black ~17 DIP, a soft shadow, no border.</summary>
    private FrameworkElement BuildBackPill()
    {
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chev = Icons.Mark(ChevronLeftGeometry, FieldInk, 15, stroked: true, thickness: 1.9);
        chev.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(chev);
        label.Children.Add(new TextBlock
        {
            Text = "Back",
            FontSize = T(17),
            Foreground = B(FieldInk),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var pill = new Button
        {
            Content = label,
            Height = BackH,
            MinWidth = 0,
            Padding = new Thickness(34, 0, 34, 0),
            CornerRadius = new CornerRadius(BackH / 2),
            Background = B(FieldFill),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Half in, half out: the pill's centre line IS the strip's edge.
            // Inset with the page rather than at §12.1's literal 20: it sits
            // directly above the heading, and §13.3 moved the heading.
            Margin = new Thickness(BodyPadX, 0, 0, -BackH / 2),
        };
        // The stock Button repaints its own Background in the pointer-over and
        // pressed states, and those brushes come from the element theme - which
        // on a dark panel would turn this pill grey the moment it is hovered.
        pill.Resources["ButtonBackground"] = B(FieldFill);
        pill.Resources["ButtonBackgroundPointerOver"] = B(Mix(FieldFill, FieldInk, 0.07));
        pill.Resources["ButtonBackgroundPressed"] = B(Mix(FieldFill, FieldInk, 0.14));
        pill.Resources["ButtonBorderBrush"] = B(Colors.Transparent);
        pill.Resources["ButtonBorderBrushPointerOver"] = B(Colors.Transparent);
        pill.Resources["ButtonBorderBrushPressed"] = B(Colors.Transparent);

        try
        {
            pill.Shadow = new ThemeShadow();
            pill.Translation = new System.Numerics.Vector3(0, 0, 20);
        }
        catch { }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pill, "Back");
        pill.Click += (_, _) => CloseGridPage();
        return pill;
    }

    /// <summary>§12.1: "a band a shade darker than the page". On a near-black
    /// page there is no room below, so the honest reading of the same
    /// instruction is a shade OFF the page rather than a shade under it.</summary>
    private Color BandColour()
    {
        var ground = PaperTextures.Ground(_h.Page());
        return ColorUtil.IsDark(ground) ? Mix(ground, Colors.White, 0.07)
                                        : Mix(ground, Colors.Black, 0.07);
    }

    /// <summary>Repaints the preview strip and NOTHING ELSE. No element is
    /// created, no section is rebuilt and the scroller is never touched - which
    /// is what lets this run on every frame of a slider drag.</summary>
    private void RedrawPreview()
    {
        if (_previewImg == null || _spec == null) return;
        try
        {
            float w = (float)(_previewHost?.ActualWidth ?? 0);
            if (w < 64) w = 480;
            var band = BandColour();
            if (_previewHost != null) _previewHost.Background = B(band);
            _previewImg.Source = GridArt.Strip(_spec, band, w, (float)PreviewH, Dpi());
        }
        catch
        {
            // A device-lost or a zero-sized arrange is not worth a broken panel;
            // the strip simply stays as it was until the next redraw.
        }
    }

    /// <summary>Pushes the whole spec at the page and repaints the strip. One
    /// host call per change rather than one per field, so a preset that moves
    /// four numbers still costs the canvas a single invalidation.</summary>
    private void CommitGrid()
    {
        if (_spec == null) return;
        // The spec is a snapshot of ONE page. If the page moved under it, this
        // call would write that page's settings onto a DIFFERENT one, which is
        // data loss on a page the user is not even looking at - so it is refused
        // outright here rather than merely being made unlikely by the
        // notification OnPageChanged depends on. Belt and braces on purpose: the
        // notification is one line in an eleven-thousand-line file and this is
        // the only check that survives it being dropped.
        if (_h.Page()?.Id != _specPageId)
        {
            OnPageChanged();     // rebinds the state now, rebuilds on the pump
            return;
        }
        try { _h.ApplyGrid?.Invoke(_spec); } catch { }
        RedrawPreview();
    }

    /// <summary>Re-reads every control on the page from the spec, in place. This
    /// is the surgical alternative to a rebuild: a §12.4 preset moves the
    /// spacing and the divisions at once and the boxes and sliders that show
    /// them have to follow without a single element being constructed.</summary>
    private void SyncGridControls()
    {
        if (_gridSyncing) return;
        _gridSyncing = true;
        try { foreach (var a in _gridBind) { try { a(); } catch { } } }
        finally { _gridSyncing = false; }
    }

    private float Dpi()
    {
        try
        {
            var xr = _previewHost?.XamlRoot ?? _root?.XamlRoot;
            if (xr != null) return (float)(96 * xr.RasterizationScale);
        }
        catch { }
        return 96f;
    }

    // ---- §12.2's preset circles ------------------------------------------
    /// <summary>~86 DIP circles, each containing a miniature of THAT PRESET's own
    /// grid (§12.2) - drawn under its own configuration, which is why the
    /// perspective thumbnails differ from one another and why one shared glyph
    /// would not do (§12.4).</summary>
    private FrameworkElement BuildPresetRow(GridSpec spec)
    {
        var strip = Strip();
        var band = BandColour();
        string bandKey = ColorUtil.ToHex(band);
        float dpi = Dpi();

        foreach (var name in GridPresets.Names(spec.Kind))
        {
            string n = name;
            bool isCustom = n == "Custom";

            Brush FillFor()
            {
                var thumb = spec.Clone();
                GridPresets.Apply(thumb, n);      // "Custom" leaves the spec alone
                thumb.Confine = false;            // the ring is the frame already
                var made = PreviewBrush(GridArt.Thumb(thumb, band, (float)PresetD, dpi));
                return made ?? B(band);
            }

            // Everything but Custom is a fixed picture and is cached; Custom IS
            // the live configuration, so it is re-rendered whenever a control
            // moves rather than cached into staleness.
            Brush fill = isCustom
                ? FillFor()
                : Cached($"pp:{spec.Kind}:{n}:{bandKey}", FillFor) ?? B(band);

            Action<bool, Brush?>? paint = null;
            strip.Children.Add(Circle(PresetD, n, spec.Preset == n,
                () => ApplyPreset(n), fill: fill,
                bind: p => paint = p));

            if (paint is { } repaint)
                _gridBind.Add(() => repaint(_spec?.Preset == n, isCustom ? FillFor() : null));
        }
        return HRow(strip, "presets");
    }

    private void ApplyPreset(string name)
    {
        if (_spec == null) return;
        GridPresets.Apply(_spec, name);
        SyncGridControls();
        CommitGrid();
    }

    // ---- §12.2's numeric rows --------------------------------------------
    /// <summary>A bold label, an optional grey caption, a RIGHT-ALIGNED TYPEABLE
    /// value box, and a full-width slider beneath (§12.2). The box is editable
    /// directly: the slider is never the only way in.</summary>
    private FrameworkElement NumRow(string label, string? caption,
                                    double min, double max, double step,
                                    Func<double> get, Action<double> set,
                                    Func<double, string> fmt, Func<string, double?> parse)
    {
        var box = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = T(SubHeadSize),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = B(Ink),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var field = ValueBox(label);
        Grid.SetColumn(field, 1);
        head.Children.Add(field);
        box.Children.Add(head);

        if (caption != null)
        {
            var cap = Caption(caption);
            cap.Margin = new Thickness(0, 6, 0, 2);
            box.Children.Add(cap);
        }

        double Snap(double v)
        {
            v = Math.Clamp(v, min, max);
            return step > 0 ? Math.Clamp(Math.Round(v / step) * step, min, max) : v;
        }

        Bar bar = null!;
        void Push(double raw, bool fromBox)
        {
            double v = Snap(raw);
            set(v);
            // A control moved off a preset is exactly what §12.4's trailing
            // "Custom" means, so the row follows the reader rather than lying.
            if (_spec != null) _spec.Preset = "Custom";
            if (fromBox) bar.Set(v);
            field.Text = fmt(v);
            SyncGridControls();
            CommitGrid();
        }

        bar = TrackBar(min, max, step, Snap(get()), v => Push(v, false), label);
        box.Children.Add(bar.Root);

        field.Text = fmt(get());

        void CommitBox()
        {
            if (_gridSyncing) return;
            if (parse(field.Text) is double v) Push(v, true);
            else { field.Text = fmt(get()); bar.Set(get()); }
        }
        field.LostFocus += (_, _) => CommitBox();
        field.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            CommitBox();
            e.Handled = true;
        };

        _gridBind.Add(() => { field.Text = fmt(get()); bar.Set(get()); });
        return box;
    }

    /// <summary>§12.2's value box: a white rounded field with a hairline border.
    /// The stock TextBox repaints its own background in the pointer-over and
    /// focused states from the element theme, so the states are pinned through
    /// the control's own resource dictionary rather than by setting Background
    /// and hoping - a local value loses to a visual-state setter.</summary>
    private TextBox ValueBox(string name)
    {
        var tb = new TextBox
        {
            MinWidth = 104,
            Height = 36,
            FontSize = T(BodySize),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var fill = B(FieldFill);
        var ink = B(FieldInk);
        var hair = B(Color.FromArgb(0x3D, 0x00, 0x00, 0x00));
        foreach (var k in new[] { "TextControlBackground", "TextControlBackgroundPointerOver",
                                  "TextControlBackgroundFocused", "TextControlBackgroundDisabled" })
            tb.Resources[k] = fill;
        foreach (var k in new[] { "TextControlForeground", "TextControlForegroundPointerOver",
                                  "TextControlForegroundFocused", "TextControlForegroundDisabled" })
            tb.Resources[k] = ink;
        foreach (var k in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                                  "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            tb.Resources[k] = hair;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tb, name);
        return tb;
    }

    /// <summary>§12.2's slider: a 2 DIP OnSurface track with a white round knob
    /// and a hairline. Authored rather than a restyled stock Slider - the track
    /// thickness, the knob fill and the knob's border are all template parts,
    /// and reaching them means replacing the template anyway.</summary>
    private sealed class Bar
    {
        public required FrameworkElement Root;
        /// <summary>Moves the knob WITHOUT raising the change callback, so the
        /// re-bind pass cannot feed a value back into the control that produced
        /// it.</summary>
        public required Action<double> Set;
    }

    private Bar TrackBar(double min, double max, double step, double value,
                         Action<double> changed, string name)
    {
        const double KnobD = 21;
        double v = value;

        var host = new Grid
        {
            Height = 34,
            Margin = new Thickness(0, 10, 0, 0),
            Background = B(Colors.Transparent),
            IsTabStop = true,
            UseSystemFocusVisuals = true,
        };
        var track = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = B(Ink),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(KnobD / 2, 0, KnobD / 2, 0),
        };
        var knob = new Border
        {
            Width = KnobD,
            Height = KnobD,
            CornerRadius = new CornerRadius(KnobD / 2),
            Background = B(FieldFill),
            BorderThickness = new Thickness(1),
            BorderBrush = B(Color.FromArgb(0x4A, 0x00, 0x00, 0x00)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slide = new TranslateTransform();
        knob.RenderTransform = slide;
        host.Children.Add(track);
        host.Children.Add(knob);

        double Travel() => Math.Max(1, host.ActualWidth - KnobD);
        void Place() => slide.X = (max > min ? (v - min) / (max - min) : 0) * Travel();

        void SetSilently(double nv)
        {
            v = Math.Clamp(nv, min, max);
            Place();
        }

        void Report(double nv)
        {
            double snapped = step > 0 ? Math.Clamp(Math.Round(nv / step) * step, min, max)
                                      : Math.Clamp(nv, min, max);
            if (Math.Abs(snapped - v) < 1e-9) return;
            v = snapped;
            Place();
            try { changed(v); } catch { }
        }

        void FromX(double x) => Report(min + (max - min) * Math.Clamp((x - KnobD / 2) / Travel(), 0, 1));

        // Dragging is tracked by the CAPTURE, not by the button state: a pen or
        // a finger reports no left button, and a mouse that leaves the control
        // mid-drag must keep steering it.
        bool dragging = false;
        host.SizeChanged += (_, _) => Place();
        host.PointerPressed += (_, e) =>
        {
            dragging = host.CapturePointer(e.Pointer);
            host.Focus(FocusState.Pointer);
            FromX(e.GetCurrentPoint(host).Position.X);
            e.Handled = true;
        };
        host.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            FromX(e.GetCurrentPoint(host).Position.X);
        };
        void Release(PointerRoutedEventArgs e)
        {
            dragging = false;
            try { host.ReleasePointerCapture(e.Pointer); } catch { }
        }
        host.PointerReleased += (_, e) => Release(e);
        host.PointerCanceled += (_, e) => Release(e);
        host.PointerCaptureLost += (_, _) => dragging = false;
        host.KeyDown += (_, e) =>
        {
            double d = step > 0 ? step : (max - min) / 100;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                case Windows.System.VirtualKey.Down: Report(v - d); e.Handled = true; break;
                case Windows.System.VirtualKey.Right:
                case Windows.System.VirtualKey.Up: Report(v + d); e.Handled = true; break;
                case Windows.System.VirtualKey.Home: Report(min); e.Handled = true; break;
                case Windows.System.VirtualKey.End: Report(max); e.Handled = true; break;
            }
        };

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(host, name);
        host.Loaded += (_, _) => Place();
        Place();
        return new Bar { Root = host, Set = SetSilently };
    }

    // ---- §12.2's colour, orientation and confine blocks -------------------
    private FrameworkElement BuildGridColourBlock(GridSpec spec)
    {
        var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        box.Children.Add(SubHead("Color"));
        box.Children.Add(Caption(
            "Automatic color adapts to your background color. Custom colors are independent of the background color."));

        var strip = Strip();
        var band = BandColour();

        Action<bool, Brush?>? paintAuto = null;
        Action<bool, Brush?>? paintCustom = null;

        strip.Children.Add(Circle(PresetD, "Automatic", string.IsNullOrEmpty(spec.Colour), () =>
        {
            spec.Colour = null;
            SyncGridControls();
            CommitGrid();
        }, bind: p => paintAuto = p));

        FrameworkElement custom = null!;
        Brush CustomFill() => B(string.IsNullOrEmpty(spec.Colour)
            ? PageTheme.WithAlpha(GridArt.InkFor(spec, band), 255)
            : ColorUtil.Parse(spec.Colour!));

        custom = Circle(PresetD, "Custom", !string.IsNullOrEmpty(spec.Colour), () =>
        {
            // Guarded exactly as the paper row's custom cell is: the anchor lives
            // in a Popup, which is a sibling of the root rather than a
            // descendant, and an exception out of a Click handler is caught by
            // nothing above it.
            try
            {
                var seed = string.IsNullOrEmpty(spec.Colour)
                    ? PageTheme.WithAlpha(GridArt.InkFor(spec, band), 255)
                    : ColorUtil.Parse(spec.Colour!);
                _h.PickColor(custom, seed, c =>
                {
                    spec.Colour = ColorUtil.ToHex(c);
                    SyncGridControls();
                    CommitGrid();
                });
            }
            catch { _h.Status("The colour picker could not be opened here."); }
        }, fill: CustomFill(), bind: p => paintCustom = p);
        strip.Children.Add(custom);

        _gridBind.Add(() =>
        {
            paintAuto?.Invoke(string.IsNullOrEmpty(spec.Colour), null);
            paintCustom?.Invoke(!string.IsNullOrEmpty(spec.Colour), CustomFill());
        });

        box.Children.Add(HRow(strip, "gridcolour"));
        return box;
    }

    /// <summary>§12.2's Orientation circles - a rounded rect ruled horizontally
    /// and one ruled vertically. Built ONLY for the kinds §12.3 gives it to.</summary>
    private FrameworkElement BuildOrientationBlock(GridSpec spec)
    {
        var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        box.Children.Add(SubHead("Orientation"));

        var strip = Strip();
        Action<bool, Brush?>? paintL = null;
        Action<bool, Brush?>? paintP = null;

        strip.Children.Add(Circle(PresetD, "Landscape", !spec.Portrait, () =>
        {
            spec.Portrait = false;
            SyncGridControls();
            CommitGrid();
        }, inner: Icons.Mark(LandscapeGeometry, Ink, 46, stroked: true, thickness: 1.5),
           bind: p => paintL = p));

        strip.Children.Add(Circle(PresetD, "Portrait", spec.Portrait, () =>
        {
            spec.Portrait = true;
            SyncGridControls();
            CommitGrid();
        }, inner: Icons.Mark(PortraitGeometry, Ink, 46, stroked: true, thickness: 1.5),
           bind: p => paintP = p));

        _gridBind.Add(() =>
        {
            paintL?.Invoke(!spec.Portrait, null);
            paintP?.Invoke(spec.Portrait, null);
        });

        box.Children.Add(HRow(strip, "orientation"));
        return box;
    }

    /// <summary>§12.2: a square CHECKBOX, not a toggle. The stock control is the
    /// right one - it is square, it is keyboard-reachable and it carries the
    /// toggle pattern that a hand-drawn box would have to reimplement.</summary>
    private FrameworkElement BuildConfineRow(GridSpec spec)
    {
        var cb = new CheckBox
        {
            IsChecked = spec.Confine,
            MinWidth = 0,
            Margin = new Thickness(0, 24, 0, 0),
            Content = new TextBlock
            {
                Text = "Only show the grid lines inside the artboard.",
                FontSize = T(BodySize),
                Foreground = B(Ink),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        void Flip()
        {
            spec.Confine = cb.IsChecked == true;
            CommitGrid();
        }
        cb.Checked += (_, _) => Flip();
        cb.Unchecked += (_, _) => Flip();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cb, "Confine to artboard");
        _gridBind.Add(() => cb.IsChecked = spec.Confine);
        return cb;
    }

    /// <summary>§12.6: the Vanishing Points block. The label is in Accent, bold,
    /// ~17 DIP, plain on the panel ground; the filled SurfaceAlt form the earlier
    /// capture shows is built here as its hover/pressed state.</summary>
    private FrameworkElement BuildVanishingBlock()
    {
        var box = new StackPanel { Margin = new Thickness(0, 22, 0, 0) };
        box.Children.Add(SubHead("Vanishing Points"));

        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = "Edit Points",
                FontSize = T(17),
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = B(Accent),
            },
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(-14, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = B(Colors.Transparent),
            IsEnabled = _h.EditPoints != null,
        };
        btn.Resources["ButtonBackground"] = B(Colors.Transparent);
        btn.Resources["ButtonBackgroundPointerOver"] = B(PanelAlt);
        btn.Resources["ButtonBackgroundPressed"] = B(PanelAlt);
        btn.Resources["ButtonBorderBrush"] = B(Colors.Transparent);
        btn.Resources["ButtonBorderBrushPointerOver"] = B(Colors.Transparent);
        btn.Resources["ButtonBorderBrushPressed"] = B(Colors.Transparent);
        btn.Click += (_, _) =>
        {
            // §12.6: pressing it DISMISSES the panel and enters the on-canvas
            // editor. The panel is put away first so the mode it opens is not
            // hidden behind the window that asked for it.
            CommitGrid();
            Hide();
            try { _h.EditPoints?.Invoke(); } catch { }
        };
        box.Children.Add(btn);

        box.Children.Add(Caption(
            "You can edit the vanishing points with a tap & hold on canvas or by activating the grid layer."));
        return box;
    }

    // ---- small maths ------------------------------------------------------
    /// <summary>The inverse of <see cref="FromWorld"/>: a typed measurement in
    /// the document's units back into world units.</summary>
    private static double ToWorld(double v, PageSizeUnit u, double upi)
    {
        double perInch = upi > 0 ? upi : 96.0;
        return u switch
        {
            PageSizeUnit.Pixels => v,
            PageSizeUnit.Points => v / 72.0 * perInch,
            PageSizeUnit.Inches => v * perInch,
            PageSizeUnit.Feet => v * 12.0 * perInch,
            PageSizeUnit.Yards => v * 36.0 * perInch,
            PageSizeUnit.Miles => v * 63360.0 * perInch,
            PageSizeUnit.Millimeters => v / 25.4 * perInch,
            PageSizeUnit.Centimeters => v / 2.54 * perInch,
            PageSizeUnit.Meters => v / 0.0254 * perInch,
            PageSizeUnit.Kilometers => v / 0.0000254 * perInch,
            _ => v,
        };
    }

    /// <summary>Reads the number a value box was typed with, ignoring whatever
    /// unit was left on the end - "100 mm", "100mm" and "100" are one answer.</summary>
    private static double? ParseLeadingNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int i = 0;
        var s = text.Trim();
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',' ||
                                (i == 0 && (s[i] == '-' || s[i] == '+')))) i++;
        var head = s[..i].Replace(',', '.');
        return double.TryParse(head, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? v : null;
    }

    // ---- authored marks for §12.2's Orientation circles (24 grid) ---------
    /// A rounded rect wider than it is tall, ruled horizontally.
    private const string LandscapeGeometry =
        "M4.5 5 H19.5 A2.5 2.5 0 0 1 22 7.5 V16.5 A2.5 2.5 0 0 1 19.5 19 H4.5 A2.5 2.5 0 0 1 2 16.5 V7.5 A2.5 2.5 0 0 1 4.5 5 Z " +
        "M5.5 9 H18.5 " +
        "M5.5 12 H18.5 " +
        "M5.5 15 H18.5";

    /// The same rect stood on end, ruled vertically.
    private const string PortraitGeometry =
        "M7.5 2 H16.5 A2.5 2.5 0 0 1 19 4.5 V19.5 A2.5 2.5 0 0 1 16.5 22 H7.5 A2.5 2.5 0 0 1 5 19.5 V4.5 A2.5 2.5 0 0 1 7.5 2 Z " +
        "M9 5.5 V18.5 " +
        "M12 5.5 V18.5 " +
        "M15 5.5 V18.5";

    /// The Back pill's chevron.
    private const string ChevronLeftGeometry = "M15.5 4 L8 12 L15.5 20";

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
        box.Children.Add(HRow(chips, "artboard"));

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

        box.Children.Add(HRow(groups, "measure"));

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
        return HRow(strip, "units");
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
        box.Children.Add(HRow(strip, "toolsetup"));
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
                // thing in the app that knows what the ground now is - and moving
                // the ground is what rebuilds this panel, so it must not be
                // rebuilt a second time on the way out.
                GroundAction(() =>
                {
                    _h.ApplyTheme();
                    _h.Save();
                });
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

        box.Children.Add(HRow(strip, "appearance"));
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
        box.Children.Add(HRow(all, "developer"));

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
        // Quill's own settings — storage, language, AI, accent, key rebinds — have
        // no home among §11.11's four headings, so they stay at the foot of this
        // tab where the "Edit shortcuts" link already points at them.
        stack.Children.Add(Section("All Quill Settings", false, BuildLegacy));
        return Body(stack);
    }

    // =======================================================================
    // GESTURES  (§11.11)
    // =======================================================================
    /// <summary>§11.11 gives this tab "Tap &amp; Hold, Draw &amp; Hold, and the
    /// Two / Three / Four Finger Tap rows". Everything Quill can actually
    /// dispatch is live; everything it cannot is shown, switched off, and says
    /// why — the same rule the rest of this panel applies, and the reason there
    /// is no invented default anywhere in here.</summary>
    private FrameworkElement BuildGestureTab()
    {
        _page = "Gestures";
        var stack = new StackPanel();
        stack.Children.Add(Section("Draw & Hold", true, BuildDrawHold));
        stack.Children.Add(Section("Tap & Hold", false, BuildTapHold));
        stack.Children.Add(Section("Multi-finger taps", true, BuildGestures));
        return Body(stack);
    }

    /// <summary>Draw &amp; Hold. Shape recognition is REAL — hold still at the end
    /// of a stroke and InkSurface snaps it to a clean shape — so this is a live
    /// switch rather than a recorded preference.</summary>
    private UIElement BuildDrawHold()
    {
        var lib = _h.Library();
        var box = new StackPanel();
        box.Children.Add(Caption(
            "Hold the pen still at the end of a stroke and Quill straightens what you drew into a clean shape."));
        box.Children.Add(ToggleRow("Enable shape recognition", lib.ShapeRecognition, v =>
        {
            lib.ShapeRecognition = v;
            _h.SetShapeRecognition?.Invoke(v);
            _h.Save();
        }, tip: "Live: this is the same switch the Precision panel's Recognition row drives."));

        box.Children.Add(Spacer(6));
        box.Children.Add(SubHead("Activation time"));
        box.Children.Add(Caption(
            "How long to hold still before the shape snaps. Quill's recogniser uses a fixed dwell today, so this " +
            "is switched off rather than shown as a setting that would not change anything."));
        var dwell = new Slider { Minimum = 200, Maximum = 1200, StepFrequency = 50, Value = 500, IsEnabled = false };
        box.Children.Add(dwell);
        return box;
    }

    /// <summary>Tap &amp; Hold. Quill dispatches none of these yet — there is no
    /// press-and-hold router on the canvas — so nothing here is pre-selected.
    /// §11.11's own instruction about the dial's new cells applies just as well
    /// to a settings row: leave it blank rather than choose for the user.</summary>
    private UIElement BuildTapHold()
    {
        var box = new StackPanel();
        box.Children.Add(Caption(
            "What a press-and-hold on the canvas does. Quill has no press-and-hold router yet, so these are shown " +
            "unset and switched off — none of them is selected for you, and none of them is faked."));

        var strip = Strip();
        foreach (var (label, glyph, stroked) in new (string, string, bool)[]
        {
            ("Last Used", Icons.History, false),
            ("Do Nothing", DoNothingGeometry, true),
            ("Lasso", Icons.Select, false),
            ("Item Picker", Icons.Objects, false),
            ("Color Picker", Icons.Fill, false),
        })
        {
            strip.Children.Add(Circle(SwatchD, label, false, () => { },
                inner: Icons.Mark(glyph, Muted, 30, stroked: stroked, thickness: 2), enabled: false));
        }
        box.Children.Add(HRow(strip, "taphold"));

        box.Children.Add(Spacer(10));
        box.Children.Add(SubHead("Activation time"));
        box.Children.Add(new Slider { Minimum = 200, Maximum = 1200, StepFrequency = 50, Value = 450, IsEnabled = false });
        box.Children.Add(ToggleRow("Highlight selection", false, _ => { }, enabled: false,
            tip: "Needs the press-and-hold router above."));
        return box;
    }

    // =======================================================================
    // STYLUS  (§11.11)
    // =======================================================================
    private FrameworkElement BuildStylus()
    {
        _page = "Stylus";
        var stack = new StackPanel();
        stack.Children.Add(Section("Pressure Response", true, BuildPressure));
        stack.Children.Add(Section("Preferences", true, BuildStylusPrefs));
        stack.Children.Add(Section("Eraser Action", true, BuildEraserAction));
        stack.Children.Add(Section("Side Button", false, BuildSideButton));
        return Body(stack);
    }

    /// <summary>§11.6 item 37's "Pressure Response as a two-handle range slider
    /// (0% - 100%)". The two handles ARE Quill's own model: PressureCurve2 pins
    /// its control points at input 0 and input 100 and lets only their OUTPUTS
    /// move, so the range's low handle is Out0 and its high handle is Out100.
    ///
    /// <para>Written to the ACTIVE pen and baked to the six-float form the
    /// renderer reads before the preset is re-applied — otherwise the setting
    /// would store correctly and change nothing, which is worse than not
    /// offering it.</para></summary>
    /// <summary>The pen the Stylus tab edits. Null when the host predates these
    /// hooks or when the library has no pens at all, and every control below
    /// switches itself off rather than editing something that is not there.</summary>
    private PenPreset? StylusPen() => _h.ActivePen?.Invoke();

    private UIElement BuildPressure()
    {
        var box = new StackPanel();
        var pen = _h.ApplyPen == null ? null : StylusPen();
        if (pen == null)
        {
            box.Children.Add(Caption("No pen is selected, so there is no pressure curve to edit."));
            return box;
        }

        var r = pen.PressureResponse ?? PressureCurve2.FromLegacy(pen.PressureCurve) ?? new PressureCurve2();
        box.Children.Add(Caption(
            $"How hard you press maps to how wide {(string.IsNullOrWhiteSpace(pen.Name) ? "this pen" : pen.Name)} draws. " +
            "The low handle is the width at no pressure, the high handle the width at full pressure."));

        var readout = Label($"{r.Out0 * 100:0}% - {r.Out100 * 100:0}%");
        box.Children.Add(readout);

        void Commit()
        {
            pen.PressureResponse = r;
            // Baked down: the renderer reads the legacy six floats, so a v2 curve
            // that is not sampled into them is a setting with no effect.
            pen.PressureCurve = r.ToLegacyPoints();
            readout.Text = $"{r.Out0 * 100:0}% - {r.Out100 * 100:0}%";
            _h.ApplyPen?.Invoke(pen);
            _h.Save();
        }

        var lo = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Round(r.Out0 * 100) };
        var hi = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, Value = Math.Round(r.Out100 * 100) };
        lo.ValueChanged += (_, e) =>
        {
            if (e.NewValue > hi.Value) { lo.Value = hi.Value; return; }
            r.Out0 = (float)(e.NewValue / 100.0);
            Commit();
        };
        hi.ValueChanged += (_, e) =>
        {
            if (e.NewValue < lo.Value) { hi.Value = lo.Value; return; }
            r.Out100 = (float)(e.NewValue / 100.0);
            Commit();
        };
        box.Children.Add(Label("Width at no pressure"));
        box.Children.Add(lo);
        box.Children.Add(Label("Width at full pressure"));
        box.Children.Add(hi);
        return box;
    }

    private UIElement BuildStylusPrefs()
    {
        var box = new StackPanel();
        var pen = _h.ApplyPen == null ? null : StylusPen();

        // Sens is the renderer's own pressure multiplier and ApplyPreset pushes
        // it, so this one is genuinely live.
        box.Children.Add(ToggleRow("Enable pressure", pen != null && pen.Sens > 0.01f, v =>
        {
            if (pen == null) return;
            pen.Sens = v ? 1f : 0f;
            _h.ApplyPen?.Invoke(pen);
            _h.Save();
        }, enabled: pen != null,
           tip: "Off draws every stroke at the pen's full width, whatever the pen reports."));

        box.Children.Add(ToggleRow("Enable tilt", false, _ => { }, enabled: false,
            tip: "Quill does not read pen tilt from Windows yet, so there is nothing for this to switch."));
        box.Children.Add(ToggleRow("Enable tap & hold", false, _ => { }, enabled: false,
            tip: "Needs the press-and-hold router the Gestures tab describes."));
        box.Children.Add(ToggleRow("Enable artboard drag", false, _ => { }, enabled: false,
            tip: "The artboard is a reference frame for exports today; it cannot be dragged."));
        box.Children.Add(ToggleRow("Enable hover brush previews", false, _ => { }, enabled: false,
            tip: "Quill draws no hover preview under the pen yet."));
        return box;
    }

    /// <summary>§11.6 item 37's Eraser Action row, and it is entirely real:
    /// Quill's EraserStyle already has all four behaviours and EraserSize already
    /// drives the eraser.</summary>
    private UIElement BuildEraserAction()
    {
        var lib = _h.Library();
        var box = new StackPanel();
        box.Children.Add(Caption("What the eraser does to what it touches."));

        var strip = Strip();
        foreach (var (style, label, glyph, stroked, tip) in EraserActions)
        {
            var st = style;
            bool on = lib.LastEraserStyle == st;
            var cell = Circle(SwatchD, label, on, () =>
            {
                lib.LastEraserStyle = st;
                _h.SetEraserStyle?.Invoke(st);
                _h.Save();
                Touch("Eraser Action");
            }, inner: Icons.Mark(glyph, on ? Ink : Muted, 30, stroked: stroked, thickness: 2));
            ToolTipService.SetToolTip(cell, tip);
            strip.Children.Add(cell);
        }
        box.Children.Add(HRow(strip, "eraser"));

        box.Children.Add(Spacer(10));
        box.Children.Add(SubHead("Size"));
        var size = Label(lib.EraserSize <= 0 ? "Follows the pen" : $"{lib.EraserSize:0} px");
        box.Children.Add(size);
        var slider = new Slider { Minimum = 0, Maximum = 80, StepFrequency = 1, Value = Math.Clamp(lib.EraserSize, 0, 80) };
        slider.ValueChanged += (_, e) =>
        {
            lib.EraserSize = Math.Round(e.NewValue);
            size.Text = lib.EraserSize <= 0 ? "Follows the pen" : $"{lib.EraserSize:0} px";
            _h.SetEraserSize?.Invoke(lib.EraserSize);
            _h.Save();
        };
        box.Children.Add(slider);
        box.Children.Add(Caption("Zero lets the eraser follow the active pen's width."));
        return box;
    }

    private static readonly (EraserStyle Style, string Label, string Glyph, bool Stroked, string Tip)[] EraserActions =
    {
        (EraserStyle.HardMask, "Hard Mask", Icons.Eraser, false, "Removes everything under the cursor outright."),
        (EraserStyle.SoftMask, "Soft Mask", SoftMaskGeometry, true, "Fades coverage by distance from the centre."),
        (EraserStyle.Slice, "Slice", SliceGeometry, true, "Cuts a stroke in two where you cross it, without thinning it."),
        (EraserStyle.Nudge, "Nudge", NudgeGeometry, true, "Pushes ink out of the way instead of deleting it."),
    };

    /// <summary>§11.6 item 37's "Side Button / Right Mouse Button" row. The pen's
    /// barrel button is the same input as the top-button gestures already stored
    /// in GestureBindings, so this drives those rather than inventing a second
    /// store that could disagree with them.</summary>
    private UIElement BuildSideButton()
    {
        var box = new StackPanel();
        box.Children.Add(Caption(
            "The pen's barrel button and the right mouse button. Windows does not deliver barrel presses to Quill " +
            "yet, so these are recorded and take effect the moment that input lands — nothing here is faked in the " +
            "meantime, and nothing is chosen for you."));
        box.Children.Add(BuildGestureRows(TopButtonGestures));
        return box;
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
                    // No Touch here: SetMouseMode is MainWindow's, and it calls
                    // TouchMouseMode for EVERY caller - this panel, the dial's
                    // mouse-mode cell, and the tool-change path. Repeating it
                    // would rebuild the section twice on one tap.
                    _h.SetMouseMode!(mode.Tag);
                    _h.Save();
                }, inner: Icons.Mark(mode.Glyph, on ? Ink : Muted, 34,
                                     stroked: mode.Stroked, thickness: 1.7));
                ToolTipService.SetToolTip(cell, mode.Tip);
                modes.Children.Add(cell);
            }
            box.Children.Add(HRow(modes, "keyboard"));
        }

        return box;
    }

    /// <summary>§10.5 item 29. Tags are <c>MouseMode</c>'s own member names, so
    /// the host can round-trip them through Enum.Parse without a second table.</summary>
    /// Soft mask: the eraser's disc with a feathered rim.
    private const string SoftMaskGeometry =
        "M12 7.6 A4.4 4.4 0 1 1 11.99 7.6 Z " +
        "M12 3.9 A8.1 8.1 0 1 1 11.99 3.9 Z " +
        "M3.1 12 A8.9 8.9 0 0 1 12 3.1 M20.9 12 A8.9 8.9 0 0 1 12 20.9";

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
        box.Children.Add(HRow(strip, "touch"));
        return box;
    }

    private static readonly (string Tag, string Label)[] TopButtonGestures =
    {
        ("TopButtonClick", "Click"),
        ("TopButtonDouble", "Double click"),
        ("TopButtonHold", "Long press"),
    };

    private static readonly (string Tag, string Label)[] FingerGestures =
    {
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
        var box = new StackPanel { Spacing = 2 };
        box.Children.Add(Caption(
            "Quill does not receive multi-finger taps from Windows yet, so these are recorded and will take effect the moment that input lands. Nothing here is faked in the meantime, and nothing is chosen for you."));
        box.Children.Add(BuildGestureRows(FingerGestures));
        return box;
    }

    private FrameworkElement BuildGestureRows((string Tag, string Label)[] rows)
    {
        var lib = _h.Library();
        var box = new StackPanel { Spacing = 2 };

        foreach (var g in rows)
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
        var panel = new StackPanel { Spacing = 12 };
        try { _h.FillLegacySettings(panel); }
        catch { panel.Children.Add(Caption("These settings could not be loaded.")); }
        // §11.6 item 45: "all Quill-specific settings must match the rest of the
        // panel's styling." These controls are built by MainWindow — the same
        // block that used to be a ContentDialog — so they arrive wearing the
        // dialog's type and the dialog's greys: 15 DIP semibold headers, 12 DIP
        // descriptions dimmed with Opacity rather than coloured, and fields at
        // stock radii. Rather than fork three hundred lines into this class and
        // let the two drift, the adopted tree is normalised onto THIS panel's
        // vocabulary: the same type scale, the same Ink and OnSurfaceMuted, the
        // same field radius and the same margins as every section above it.
        try { Adopt(panel); } catch { }
        panel.Width = double.NaN;      // MainWindow builds it at a fixed 480
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        return panel;
    }

    /// <summary>Re-dresses a tree this panel did not build.
    ///
    /// <para>Classification is by the role the original size and weight signal,
    /// not by position: a heading is bold or 15 DIP and up, a caption is 12.5 DIP
    /// and down, everything else is body. That reads the intent MainWindow's
    /// builder already expressed instead of requiring it to be re-expressed.</para>
    ///
    /// <para>Walks the LOGICAL containers by type. VisualTreeHelper would find
    /// nothing useful: this runs before the tree is arranged, so no control has a
    /// template yet and only Content / Child / Children are reachable.</para></summary>
    private void Adopt(DependencyObject? node)
    {
        switch (node)
        {
            case null:
                return;

            case TextBlock t:
            {
                bool head = t.FontWeight.Weight >= Microsoft.UI.Text.FontWeights.SemiBold.Weight
                            || t.FontSize >= 15;
                bool caption = !head && t.FontSize <= 12.5;
                t.FontSize = T(head ? SubHeadSize : caption ? CaptionSize : BodySize);
                t.Foreground = B(caption ? Muted : Ink);
                // The dialog dimmed its captions with Opacity; this panel colours
                // them, and the two together would double the fade.
                t.Opacity = 1;
                t.TextWrapping = TextWrapping.Wrap;
                if (head)
                {
                    t.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                    t.Margin = new Thickness(0, 14, 0, 0);
                }
                else if (caption)
                {
                    t.LineHeight = T(CaptionSize) * 1.4;
                    // §11.6 item 39 reaches this block too.
                    t.Margin = new Thickness(0, 4, 0, 10);
                }
                return;
            }

            case Slider s:
                s.FontSize = T(BodySize);
                break;

            case TextBox tb:
                tb.FontSize = T(BodySize);
                tb.CornerRadius = new CornerRadius(10);
                break;

            case Button btn:
                btn.FontSize = T(14);
                btn.CornerRadius = new CornerRadius(11);
                btn.Padding = new Thickness(14, 6, 14, 6);
                break;

            case Control c:
                // ComboBox, ToggleSwitch, CheckBox and the rest keep their stock chrome — the panel supplies the type, WinUI supplies the control.
                c.FontSize = T(BodySize);
                break;
        }

        switch (node)
        {
            case Panel p:
                foreach (var child in p.Children) Adopt(child);
                break;
            case Border b:
                Adopt(b.Child);
                break;
            case ContentControl cc:
                Adopt(cc.Content as DependencyObject);
                break;
            case ItemsControl ic:
                foreach (var item in ic.Items) Adopt(item as DependencyObject);
                break;
        }
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
            Padding = new Thickness(BodyPadX, 8, BodyPadX, 24),
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
    /// <summary>Where each named strip was scrolled to, across rebuilds.
    ///
    /// <para>A rebuilt <c>ScrollViewer</c> starts at 0,0 on BOTH axes. The window
    /// preserves the panel's VERTICAL offset across a repaint (§10.5 item 20) and
    /// nothing preserved the strips' horizontal ones, so <i>"if I scrolled
    /// sideways returns to the left"</i> was the same fault one axis over and was
    /// never going to be fixed by the vertical path. Keyed by what the row IS
    /// rather than by the element, because the element is exactly what does not
    /// survive.</para></summary>
    private readonly Dictionary<string, double> _stripX = new();

    private FrameworkElement HRow(UIElement content, string? key = null)
    {
        // §10.5 item 25: the wheel, the rail and the chaining are STRIP
        // behaviour, not settings-panel behaviour, so they come from the one
        // place that has them (StripScroll) rather than being re-specified here.
        var sv = StripScroll.Horizontal(content);

        // §13.3: "make circular icons ignore the margins and go straight to the
        // end of the page". The VIEWPORT bleeds back across the whole inset, so a
        // circle passes under the panel's edge instead of being clipped short at
        // an invisible inner boundary - but the CONTENT keeps a leading inset of
        // the same size, so at rest the first circle still sits under its own
        // heading. Moving the strip bodily left, first item included, is the way
        // to get this wrong: every row would then be out of line with its title.
        //
        // Measured from the window rather than assumed, because the grid editor
        // page runs with the scroller's padding at zero (§12.1's full-bleed
        // preview strip) and the section tabs do not.
        double bleedL = BodyPadX, bleedR = BodyPadX;
        try { bleedL += _win.ContentPadding.Left; bleedR += _win.ContentPadding.Right; } catch { }
        sv.Margin = new Thickness(-bleedL, 0, -bleedR, 0);
        if (content is FrameworkElement row) row.Margin = new Thickness(bleedL, 0, 0, 0);

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

        if (key is { Length: > 0 }) Remember(sv, key);

        // The rule under the strip is NOT bled: §13.3 wants it spanning the
        // strip's content, and a rule that ran edge to edge under an inset
        // heading would read as offset from everything above it.
        var box = new StackPanel();
        box.Children.Add(sv);
        box.Children.Add(track);
        return box;
    }

    /// <summary>Gives one strip a memory of its own horizontal offset.
    ///
    /// <para>The restore has the same race the window's vertical one has: a
    /// ScrollViewer has no extent until its new content is measured, and a
    /// <c>ChangeView</c> issued before that silently clamps to zero and reports
    /// success. Measure, ask, then ask again on the next layout pass.</para>
    ///
    /// <para>The zero a freshly built scroller reports is NOT the reader moving
    /// to the left, so it is ignored until the row has settled - otherwise the
    /// rebuild would overwrite the very offset this is trying to keep. Once a
    /// real offset has been seen, a genuine scroll back to zero is recorded
    /// normally.</para></summary>
    private void Remember(ScrollViewer sv, string key)
    {
        bool settling = true;

        void Record()
        {
            // Nothing overflows: there is no position to have an opinion about,
            // and recording zero here would forget a real one from a wider layout.
            if (sv.ExtentWidth <= sv.ViewportWidth + 0.5) return;
            double x = sv.HorizontalOffset;
            if (settling)
            {
                if (x <= 0.5) return;
                settling = false;
            }
            _stripX[key] = x;
        }

        sv.ViewChanged += (_, _) => Record();

        sv.Loaded += (_, _) =>
        {
            double want = _stripX.TryGetValue(key, out double x) ? x : 0;
            if (want <= 0.5) { settling = false; return; }
            try { sv.UpdateLayout(); } catch { }
            try { sv.ChangeView(want, null, null, true); } catch { }
            void Once(object? _, object __)
            {
                sv.LayoutUpdated -= Once;
                try { sv.ChangeView(want, null, null, true); } catch { }
                settling = false;
            }
            sv.LayoutUpdated += Once;
        };
    }

    /// <summary>One circular option (§3.1, §9.9).
    ///
    /// <para>Selected = a 2 DIP <c>OnSurface</c> ring and a bold caption;
    /// unselected = a hairline <c>Outline</c> ring and a muted caption. §9.9
    /// confirms the option circles are UNFILLED — the ring is the whole mark —
    /// so <paramref name="fill"/> is supplied only by the rows whose subject IS a
    /// colour or a texture: the page backgrounds, the grids and the theme.</para></summary>
    /// <param name="bind">Hands the caller a delegate that repaints this
    /// circle's SELECTION (and, optionally, its fill) in place. §12's preset,
    /// colour and orientation rows change which circle is lit on every tap, and
    /// rebuilding a row to move a ring is the wholesale rebuild §11.1 item 1
    /// forbids.</param>
    private FrameworkElement Circle(double d, string caption, bool selected, Action tap,
                                    Brush? fill = null, UIElement? inner = null, bool enabled = true,
                                    Action<Action<bool, Brush?>>? bind = null)
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

        // Brush-level writes only, never a mutation of a live brush: WinUI
        // caches those and the change would not take.
        bind?.Invoke((on, newFill) =>
        {
            ring.Stroke = B(on ? Ink : Line);
            ring.StrokeThickness = on ? 2 : 1;
            if (newFill != null) ring.Fill = newFill;
            text.Foreground = B(on ? Ink : Muted);
            text.FontWeight = on ? Microsoft.UI.Text.FontWeights.Bold
                                 : Microsoft.UI.Text.FontWeights.Normal;
        });

        // A bare Button, not a Tapped handler. These circles are the panel's
        // primary controls and as StackPanels they had no keyboard focus, no
        // invoke pattern and no accessible role. The Button also cannot be fired
        // by a sideways drag: the strip's ScrollViewer takes the pointer capture
        // when it starts scrolling and the Button raises no Click — which is what
        // used to change the paper while the reader was only scrolling the row.
        return StripScroll.Bare(cell, caption, tap, enabled);
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
