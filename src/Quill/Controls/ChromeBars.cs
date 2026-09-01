using Quill.Helpers;
using Quill.Models;
using Quill.Services;
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
/// transparent divider, then Import, Export, Settings and Help (section 5's
/// `?`, which raises the generated shortcut sheet).</para>
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

        /// <summary><b>THE PRO SWITCH, and it is off.</b> 15.3 originally had a
        /// `PRO` badge ride down into the fullscreen cluster with the bracket;
        /// the user has since asked for it to come out — "remove pro button for
        /// now, keep it in code for possible reuse much later" — so the
        /// fullscreen cluster is now
        /// <c>[ ] | 10% 0deg down up gear ?</c>.
        ///
        /// <para>The badge's construction path is RETAINED AND LIVE, not
        /// commented out and not deleted: <see cref="ProBadge"/> still compiles,
        /// is still referenced, and turning this to true puts it back. Commented
        /// code bit-rots silently and does not survive the next refactor of the
        /// method it sat in; code that still compiles does.</para>
        ///
        /// <para>It is read in a condition alongside a field rather than on its
        /// own, so the compiler never sees a constant-false <c>if</c> and never
        /// reports the call unreachable — the build stays at zero
        /// warnings.</para></summary>
        public const bool ProBadgeVisible = false;

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
        /// <summary>The divider rule: 1 x 16 DIP. Left cluster, between the
        /// gallery icon and the page name — and, in fullscreen only, right
        /// cluster too (15.3). Colour below.</summary>
        public const double DividerW = 1, DividerH = 16;
        /// <summary>Gaps around the divider: 27 phys from the glyph, 23 phys to
        /// the name — 13.5 and 11.5 DIP.</summary>
        public const double DividerGapL = 13.5, DividerGapR = 11.5;
        /// <summary>Gaps around the FULLSCREEN divider, which is a different
        /// situation from the left cluster's: that one separates a 42 DIP slot
        /// from a text run and needs the measured 13.5/11.5, while this one sits
        /// between TWO 42 DIP slots, each already carrying ~13 DIP of its own
        /// padding around a 16 DIP glyph. Reusing 13.5/11.5 here would put ~51
        /// DIP between the bracket and the lock, roughly twice the gap 15.3's
        /// `[ ]  │  10%` shows.</summary>
        public const double FsDividerGapL = 3, FsDividerGapR = 3;
        /// <summary>Active-menu indicator: TWO SEPARATE underlines, one centred
        /// under each active toggle — never one bar spanning the group.</summary>
        public const double UnderlineW = 40, UnderlineH = 2;
        /// <summary>Top of the wheel, 52 DIP below the title bar (§1.4).</summary>
        public const double DialRimTop = 52;

        public const double TitleMaxWidth = 190;
        public const double ReadoutWidth = 52;

        // ---- §17.2, the corner buttons' ground ------------------------
        /// <summary>Size of a corner button's page-coloured plate, INSIDE its
        /// measured <see cref="IconPitch"/> hit target. A diameter when §17.2
        /// drew it as a circle; §17.18.2 makes it the SIDE of a square, and the
        /// number is unchanged so the row's spacing is too.
        ///
        /// <para>Smaller than the target on purpose. The row's spacing is 0 and
        /// the targets are 42 wide, so a plate drawn at the full target size
        /// would leave adjacent plates exactly tangent and the cluster would read
        /// as one continuous bar — which is the liquid-glass card §1.3's
        /// measurement ruled out. At 34 there are 8 DIP of page between plates,
        /// so each button reads as its own soft patch. THE HIT TARGET IS
        /// UNCHANGED: 42 is measured, and only what is painted moves.</para></summary>
        public const double GroundDiameter = 34;

        /// <summary>§17.18.2: THE CORNER FRAMES ARE SQUARES NOW, AND THIS IS THE
        /// RADIUS — <b>4 DIP</b> on a 34 DIP box, 11.8% of the side.
        ///
        /// <para>The section asks for the number to be stated rather than
        /// discovered, because the recent rulings have gone both ways: §17.16a
        /// took the bottom bar's chip to a hard 0, and §17.5's panel corner mark
        /// is a square with exactly one rounded corner — but §17.2's own family
        /// here still contains a stadium.</para>
        ///
        /// <para>4 is chosen off <see cref="ProBadge"/>, which is the nearest
        /// thing in this file: a small free-standing plate on the page, at
        /// <c>CornerRadius(4)</c>. The bottom bar's 0 is a FILL INSIDE a bar,
        /// where any radius leaves wedges of the bar's own colour at the corners
        /// — an argument that does not apply to a patch floating on the page. At
        /// 4 the plate reads square: the corners are taken off, not rounded.</para>
        ///
        /// <para>One line to change if the user wants a hard 0.</para></summary>
        public const double GroundCorner = 4;

        /// <summary>§17.2: the zoom and tilt readouts take "a rounded-end capsule
        /// sized to their text". Same height as the circles' diameter, so the two
        /// shapes read as one family; the width is whatever the text needs.
        ///
        /// <para>§17.18.2 leaves these alone on purpose: "they are sized to their
        /// text, and a square cannot be."</para></summary>
        public const double StadiumH = GroundDiameter, StadiumPadX = 10;

        // ---- glass mode only (ignored while GlassBars is false) ----
        public const double PanelGap = 8;
        public const double BarRadius = 17;
    }

    /// <summary>§17.2'S GROUND, AND THE ONE RULE ABOUT WHAT MAY BE DRAWN ON IT.
    ///
    /// <para>The corner buttons take "a background that mimics the page colour,
    /// so the button almost disappears into the page — but the page's grid and
    /// texture do NOT continue across it, and that discontinuity is what makes
    /// the button findable". So this is the PAGE's colour EXACTLY, not a raised
    /// or tinted variant of it: the plate is meant to be the same colour as the
    /// paper, and the only thing distinguishing it is that the grain and the grid
    /// stop at its edge. Raising it even a step would trade the effect §17.2
    /// describes for an ordinary button.</para>
    ///
    /// <para><b>§24: IT WAS READING THE WRONG SOURCE, AND THIS IS THE PARAGRAPH
    /// THAT SAID SO.</b> What stood here recorded a "known gap": that §17.18.1
    /// had observed the plate is invisible on a plain page with no grain or grid
    /// to interrupt, that §17.18.2 applied that observation to these frames, and
    /// that §17.19 then superseded §17.18.1 outright and left them with no rule
    /// to fix them. Every step of that is true and the conclusion was still
    /// wrong, because it accepted the premise that this line was reading the page
    /// at all. IT WAS NOT. It was <c>PageTheme.Ground</c>, and
    /// <c>MainWindow.ResolveGround</c> returns the paper only when
    /// <c>ThemeSource == "Page"</c> — a field that DEFAULTS to <c>"Manual"</c>.
    /// On a default install this plate was a fixed shell colour, measured
    /// byte-identical <c>#0F0E10</c> on OLED black, Darkprint, Brown Paper, a
    /// custom red, Blueprint and Plain White across three screen runs. On Plain
    /// White that is 18.77:1 and ΔL* 94.9 — the maximum contrast sRGB has, from
    /// the section that asked for a button which almost disappears. It was never
    /// a value that needed a nudge. It was the wrong source, and no adjustment to
    /// a wrong source could have been right on more than one paper at a
    /// time.</para>
    ///
    /// <para>So it reads <see cref="PagePlate"/> now — the LIVE page's ground,
    /// through the same shared formula the dial's plates use, at
    /// <see cref="PagePlate.Full"/>, the endpoint where the base grey drops out
    /// of the mix and the page's own colour is all that remains. §17.18.1's
    /// observation still stands and is still unanswered: on a plain black or
    /// plain white page there is no grain or grid for the plate to interrupt, so
    /// the frame is findable only by its mark. That is a question for the user,
    /// and the one thing to change if they want a lift is the <c>Full</c> passed
    /// below — <c>PagePlate.Tint</c> would give these frames the dial's grey
    /// instead.</para>
    ///
    /// <para>The discontinuity needs no code. The grid and the paper texture are
    /// painted by <see cref="InkSurface"/> into the Win2D canvas UNDERNEATH these
    /// WinUI elements, so an opaque patch of the ground colour interrupts them by
    /// construction. <see cref="PagePlate.Of"/> is always fully opaque, which is
    /// what makes that true.</para>
    ///
    /// <para><b>What may be drawn on it — and why the mark had to move too.</b>
    /// §17.4's fault was a mark whose contrast had been tested against a
    /// NEIGHBOURING surface's token, so it passed the test and still vanished on
    /// the ground it was actually drawn on. The mark here used to be
    /// <see cref="ChromeUi.Ink"/> = <c>PageTheme.OnSurface</c>, and the reason
    /// that was the right pairing was NOT that it read well — it was that
    /// <c>OnSurface</c> is selected by <c>IsDark</c>, i.e. keyed to
    /// <c>PageTheme.Ground</c>, the very ground this plate has just stopped
    /// using. Left alone it would have been §17.4 for a third time. So every mark
    /// these two bars draw is keyed to the PAGE now, through
    /// <see cref="BarInk"/>: not only the glyphs standing on a plate but the page
    /// name, the divider and the readouts as well, because all of them float over
    /// the page and always did — a bar that keys half its marks to the page and
    /// half to the shell is a rule nobody can hold. Measured over the nine
    /// shipped papers the mark-on-plate ratio runs 3.66:1 (Brown Paper) to
    /// 17.96:1 (Plain White), every one clear of the 3:1 floor a non-text mark
    /// needs. The table is in §24.</para></summary>
    private Color PageGround()
    {
        try { return PagePlate.Ground(_h.Surface().Page); }
        catch { return PageTheme.Ground; }
    }

    /// <summary>§17.2's plate: the page's own colour, through the shared
    /// formula's <see cref="PagePlate.Full"/> endpoint.
    ///
    /// <para>NOT static any more, and that is the whole of the change. A static
    /// factory had no live page to read, so it reached for the one global that
    /// looked like a page colour and was not one.</para></summary>
    private SolidColorBrush GroundBrush() => new(PagePlate.Of(PageGround(), PagePlate.Full));

    /// <summary>The mark for these two bars, keyed to the PAGE they float over
    /// rather than to the shell's ground. See <see cref="GroundBrush"/> for why
    /// those two part company and why reaching for the shell's token here is
    /// §17.4's defect wearing a new name.</summary>
    private Color BarInk() => PagePlate.Ink(PageGround());

    /// <summary>Secondary ink for the bars — the same relation
    /// <c>PageTheme.OnSurfaceMuted</c> has to <c>OnSurface</c>, carried onto the
    /// page-keyed mark instead of the shell-keyed one.</summary>
    private Color BarDim() => PageTheme.WithAlpha(BarInk(), 140);

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
        /// <summary>15.3: the `[ ]` bracket that LEADS the fullscreen cluster is
        /// the ordinary way back out of fullscreen — the hover strip is only the
        /// shortcut. It calls the same toggle the caption button and F11 do, so
        /// the three can never disagree about what fullscreen means.</summary>
        public required Action ToggleFullscreen { get; init; }
        public required Action RenamePage { get; init; }
        public required Action OpenSettings { get; init; }
        /// <summary>CONCEPTS-REF 5's `?`, the last mark in the right cluster.
        ///
        /// <para>A TOGGLE, not an open, and deliberately the SAME call F1 and the
        /// app menu's "Keyboard shortcuts" item make. The sheet it raises is
        /// generated from the accelerators that were actually installed
        /// (<c>MainWindow.ApplyKeyPreset</c> -> <c>BuildShortcutsSheet</c>), so
        /// Help cannot claim a key that no longer works, and three entry points
        /// to one surface cannot disagree about what Help is.</para></summary>
        public required Action ToggleHelp { get; init; }
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
    private bool _fullscreen;
    /// <summary>§17.1: TWO SEPARATE LOCKS, NOT ONE. The bar used to carry a
    /// single padlock glyph that only ever governed zoom, which is precisely
    /// what UI-REFERENCE §4.7 calls out — "Concepts uses two independent locks,
    /// one per value, not a single shared lock". Neither of these is reachable
    /// through the other, and the Measurement panel gives each its own
    /// padlock.</summary>
    private bool _zoomLocked;
    private bool _tiltLocked;
    private float _lockedZoom = 1f;
    private bool _reasserting;
    private MeasurementMenu? _measure;

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

        var surface = _h.Surface();
        surface.ViewChanged += OnViewChanged;
        // The Layers pane counts what is on the page and the Comments pane lists
        // its pins: both are reports, so they have to follow the page rather than
        // freeze at the moment they were opened. Coalesced onto the dispatcher so
        // a stroke in progress does not rebuild a panel per sample.
        surface.ContentChanged += ScheduleContentRefresh;
        // The bars are custom-drawn and capture their colours at build time, so
        // they repaint on the PAGE's ground moving - not on ActualTheme, which is
        // only the two-state shadow of it and never fires for blue -> brown.
        PageTheme.Changed += Refresh;
    }

    // =====================================================================
    // Show / hide
    // =====================================================================

    /// <summary>True while these bars are the app's top bar. MainWindow reads it
    /// to decide whether hiding the caption bar in fullscreen would leave the
    /// user with no chrome at all.</summary>
    public bool IsVisible => _on;

    /// <summary>Fullscreen changes the SHAPE of the right cluster (15.3): the
    /// bracket and its divider lead it, and the PRO slot joins the readouts.
    /// Cheap and idempotent; a rebuild only happens while the bars are up,
    /// because <see cref="SetVisible"/> rebuilds on the way in anyway.</summary>
    public void SetFullscreen(bool on)
    {
        if (_fullscreen == on) return;
        _fullscreen = on;
        if (!_on) return;
        try { Build(); } catch { }
        PushInset();
    }

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
            // The Measurement panel is opened from a readout in this very
            // cluster, so it is stranded by exactly the same argument.
            _measure?.Hide();
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
        _measure?.Rebuild();
        // The panes capture their ink at build time too.
        foreach (var p in new[] { _layersPane, _precisionPane, _commentsPane })
        {
            p?.Repaint();
            p?.RefreshIfOpen();
        }
        _objects?.Refresh();
        _layout.Invalidate();
    }

    /// <summary>CONCEPTS-REF 17.15 - how far the right cluster is held clear of
    /// the fullscreen reveal strip. Zero unless this cluster is the topmost bar
    /// on screen; MainWindow owns that question because it is the one that
    /// computes the fold.</summary>
    public double StripReserve { get; private set; }

    /// <summary>Set by ApplyFullscreenChrome. Guarded on a real change because
    /// ApplyFullscreenChrome is self-correcting and runs on SizeChanged and
    /// after every surface switch, so this is called far more often than it
    /// changes.</summary>
    public void SetStripReserve(double px)
    {
        if (Math.Abs(StripReserve - px) < 0.5) return;
        StripReserve = px;
        ApplyDockInset();
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
            _right.Margin = new Thickness(
                0, Metrics.RowTop, inset + _h.RightDockWidth() + StripReserve, 0);
            // The Measurement panel hangs off this cluster, so it slides with it
            // rather than being left behind under a docked settings panel.
            if (_measure != null)
            {
                // The strip reserve rides along: this panel hangs off the
                // cluster, so a cluster held clear of the strip that left its
                // panel behind would just move the misalignment one control on.
                _measure.RightDockWidth = _h.RightDockWidth() + StripReserve;
                _measure.Reposition();
            }
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
            // One formatter for the bar, the panel and the hover pill, so the
            // three can never disagree about rounding.
            _zoomText.Text = MeasurementMenu.Percent(_h.Surface().ViewZoom);
            // Quill has no canvas rotation yet, so this is an honest constant
            // rather than a number invented to fill the slot.
            _tiltText.Text = MeasurementMenu.Degrees(0);
            _measure?.Sync();
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
            Foreground = new SolidColorBrush(BarInk()),
        };

        // The page title is renamable FROM THE BAR (V3 B) - the whole cell is
        // the target, and it carries the pencil so it does not read as a label.
        var titleCell = ChromeUi.Row(6);
        titleCell.Padding = new Thickness(0, 0, Metrics.IconPitch / 2, 0);
        titleCell.VerticalAlignment = VerticalAlignment.Center;
        titleCell.Background = new SolidColorBrush(Colors.Transparent);
        titleCell.Children.Add(_title);
        var pencil = Icons.Filled(Icons.Rename, BarInk(), 12);
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
        //
        // 15.3, in FULLSCREEN, this cluster changes shape. The caption bar is
        // gone, and the fullscreen glyph migrates down into it:
        //
        //     [ ]  |  10%   0deg   down   up   gear   ?
        //
        // The bracket LEADS, and a thin vertical rule separates it from the zoom
        // readout. Neither exists windowed - the divider is there BECAUSE the
        // bracket moved in, so both are built behind the same flag rather than
        // being left up and hidden. `PRO` used to sit after the tilt readout and
        // was taken back out at the user's request; the divider is unaffected,
        // because it belongs to the bracket rather than to the badge.
        var right = ChromeUi.Row(0);
        right.VerticalAlignment = VerticalAlignment.Center;
        if (_fullscreen)
        {
            right.Children.Add(BarButton(Icons.Fullscreen, "Leave full screen (F11)", _h.ToggleFullscreen));
            right.Children.Add(Divider(Metrics.FsDividerGapL, Metrics.FsDividerGapR));
        }
        foreach (var el in BuildViewReadout()) right.Children.Add(el);
        if (_fullscreen && Metrics.ProBadgeVisible) right.Children.Add(ProBadge());
        // K.18: the AI button sits immediately to the LEFT of Import. It carries
        // the top bar's own flyout rather than a second copy of the menu.
        var ai = BarButton(Icons.Ai, "AI assistant — summarise, tag, ask, improve", () => { });
        try { ai.Flyout = _h.AiMenu(); } catch { }
        right.Children.Add(ai);
        right.Children.Add(BarMenuButton(Icons.Import, "Import", BuildImportMenu()));
        right.Children.Add(BarButton(Icons.Export, "Export", OpenExport));
        right.Children.Add(BarButton(Icons.Settings, "Settings", _h.OpenSettings));
        // Section 5 closes the right cluster with Help, and 15.3 transcribes the
        // same `?` at the end of the fullscreen cluster. It was missing from both
        // - 15.3 was transcribing the REFERENCE, and the mark it listed had never
        // been built here. One button serves both states, because this cluster IS
        // both states; only the bracket and the divider are conditional.
        right.Children.Add(BarButton(Icons.Help, "Help - keyboard shortcuts and gestures (F1)", _h.ToggleHelp));

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
    private Grid Slot(FrameworkElement? art, string tip, bool underline)
    {
        var cell = new Grid
        {
            Width = Metrics.IconPitch,
            Height = Metrics.IconPitch,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        // §17.2's plate, added FIRST so it sits behind the mark and the
        // underline. §17.18.2 makes it a SQUARE - "the circles become squares" -
        // at Metrics.GroundCorner, which is where that decision is written down.
        // The zoom and tilt readouts are not built here and keep their stadium.
        cell.Children.Add(new Border
        {
            Width = Metrics.GroundDiameter,
            Height = Metrics.GroundDiameter,
            CornerRadius = new CornerRadius(Metrics.GroundCorner),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = GroundBrush(),
            IsHitTestVisible = false,
        });
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
                Background = new SolidColorBrush(BarInk()),
            });
        ToolTipService.SetToolTip(cell, tip);
        return cell;
    }

    /// <summary>The 42 DIP hit target the plate sits inside. Its own background
    /// is transparent, but WinUI's default template still paints a pointer-over
    /// and pressed wash CLIPPED TO THIS RADIUS — so it was drawing a 42 DIP
    /// circle behind §17.2's plate. §17.18.2 squares the plate, and leaving this
    /// at a circle would put a round highlight around a square frame, which is
    /// the shape mismatch the section is removing. Same
    /// <see cref="Metrics.GroundCorner"/>, for the same reason.</summary>
    private static Button Bare(FrameworkElement content, string tip)
    {
        var b = new Button
        {
            Content = content,
            Width = Metrics.IconPitch,
            Height = Metrics.IconPitch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(Metrics.GroundCorner),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    private Button BarButton(string geometry, string tip, Action click, bool stroked = false)
    {
        var ink = BarInk();
        var art = stroked
            ? Icons.Stroked(geometry, ink, Metrics.GlyphSize, 1.6)
            : Icons.Filled(geometry, ink, Metrics.GlyphSize);
        var b = Bare(Slot(art, tip, underline: false), tip);
        b.Click += (_, _) => click();
        return b;
    }

    private Button BarMenuButton(string geometry, string tip, FlyoutBase flyout)
    {
        var b = BarButton(geometry, tip, () => { });
        b.Flyout = flyout;
        return b;
    }

    /// <summary>The measured 1 x 16 DIP rule, sampled #262829. Left cluster
    /// always; right cluster only in fullscreen, where 15.3 puts one between the
    /// migrated bracket and the zoom readout. The gaps are a parameter because
    /// the two situations are not the same measurement — see
    /// <see cref="Metrics.FsDividerGapL"/>.</summary>
    private FrameworkElement Divider(double gapL = Metrics.DividerGapL,
                                     double gapR = Metrics.DividerGapR) => new Border
    {
        Width = Metrics.DividerW,
        Height = Metrics.DividerH,
        Margin = new Thickness(gapL, 0, gapR, 0),
        VerticalAlignment = VerticalAlignment.Center,
        // §24: ChromeUi.BarDivider is the same alpha off the SHELL's ink. These
        // bars are keyed to the page now, and a rule between two page-keyed
        // glyphs cannot be the one element still keyed to the shell.
        Background = new SolidColorBrush(PageTheme.WithAlpha(BarInk(), 0x66)),
    };

    /// <summary>15.3's `PRO`, which in Concepts is the Pro Store button — it
    /// reads `PRO` once bought and `Go PRO` before, and rode down into this
    /// cluster in fullscreen exactly as the bracket does.
    ///
    /// <para><b>PARKED, NOT DEAD.</b> The user asked for the badge to come out
    /// of the cluster "for now" and for the code to stay "for possible reuse
    /// much later", so this method is still compiled and still called — behind
    /// <see cref="Metrics.ProBadgeVisible"/>, which is false. Flip that constant
    /// and the badge is back in its measured place after the tilt readout.
    /// Nothing here is commented out, because commented-out code does not
    /// survive the next refactor of the method it used to live in.</para>
    ///
    /// <para><b>Quill has no store, no account and no paid tier</b>, so if it is
    /// ever switched back on it stays deliberately INERT and deliberately quiet:
    /// muted ink, an outline rather than a fill, and a tooltip that says plainly
    /// there is nothing behind it — the same treatment the import menu's "Take a
    /// photo" gets, which is present, disabled and honest about why rather than
    /// absent or fake. It must not become a button that implies something can be
    /// purchased.</para></summary>
    private static FrameworkElement ProBadge()
    {
        var text = new TextBlock
        {
            Text = "PRO",
            FontSize = 10.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 90,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ChromeUi.Dim),
        };
        var pill = new Border
        {
            Child = text,
            Padding = new Thickness(6, 1, 5, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(ChromeUi.Wash(0x55)),
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var cell = new Grid
        {
            Height = Metrics.IconPitch,
            Margin = new Thickness(6, 0, 6, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            Children = { pill },
        };
        ToolTipService.SetToolTip(cell,
            "Concepts carries a Pro Store badge here, and reference 15.3 has it move into this cluster in " +
            "full screen. Quill has no store, no account and no paid tier, so the badge is inert: there is " +
            "nothing to buy and nothing it unlocks.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, "PRO badge (inert - Quill has no paid tier)");
        return cell;
    }

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
        var art = Icons.Filled(geometry, BarInk(), Metrics.GlyphSize);
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
        var art = Icons.Filled(Icons.Objects, BarInk(), Metrics.GlyphSize);
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

    /// <summary>CONCEPTS-REF 11.20 item 16. The Objects library's tiles are drawn
    /// with the LIVE pen, so they have to be rebuilt when the pen changes - a
    /// preview showing the pen the user put down two gestures ago is the "fixed
    /// preview style" the item is about. Costs nothing while the library is
    /// closed, and nothing at all in a session that never opens it.</summary>
    public void RefreshObjects() => _objects?.Refresh();

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
                // 11.20 item 16: the library's preview tiles are drawn with the
                // live pen. PageOps already carries it for the Stylus tab, so the
                // two surfaces read one answer rather than two.
                ActivePen = () => _h.PageOps().ActivePen?.Invoke(),
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
    /// <summary>The right cluster's left half: the live zoom readout and the tilt
    /// readout, each carrying its OWN padlock and each opening the Measurement
    /// panel (§17.1). Returned as a SEQUENCE so each keeps its own slot rather
    /// than being packed into a card.
    ///
    /// <para><b>The shared lock button is gone.</b> It was one glyph in its own
    /// 42 DIP slot that governed zoom only, and §17.1 replaces it with two
    /// independent locks living in the Measurement panel. What appears in the bar
    /// is a padlock BESIDE a value, and only while that value is locked.</para>
    ///
    /// <para><b>THE ROW RE-LAYS OUT; IT DOES NOT OVERLAP.</b> §17.1: "Locking
    /// tilt shifts the zoom readout sideways to make room for the lock glyph
    /// appearing beside the tilt value." That falls out of the construction
    /// rather than being arranged for — the padlock is a real child of the
    /// readout's StackPanel, so it takes real width, and the whole cluster is
    /// <see cref="HorizontalAlignment.Right"/>: widening the TILT cell therefore
    /// pushes everything to its left, and the zoom readout is what is to its
    /// left. Nothing is positioned absolutely and nothing can land on top of
    /// anything else.</para></summary>
    private IEnumerable<FrameworkElement> BuildViewReadout()
    {
        _zoomText = new TextBlock
        {
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(BarInk()),
        };
        yield return ReadoutCell(_zoomText, _zoomLocked, Metrics.ReadoutWidth,
                                 "Zoom", "Zoom — tap for the Measurement menu", 0);

        _tiltText = new TextBlock
        {
            FontSize = 12.5,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(BarDim()),
        };
        // DEFERRED, AND SAID SO (V3 K.26). Tilt is not a readout that needs
        // filling in - it needs canvas rotation, which Quill does not have. The
        // view transform is a scale and a translate: InkSurface converts between
        // screen and world in 62 separate inline expressions rather than through
        // its two helpers, and 51 more places build or test axis-aligned
        // rectangles that stop being valid the moment the canvas is turned. A
        // number here that moved while the eraser, the lasso and the text-box
        // hit-tests still assumed square would be worse than no number at all,
        // so it stays at zero and the panel's Rotation section says why.
        yield return ReadoutCell(_tiltText, _tiltLocked, 34,
                                 "Canvas tilt (not implemented)",
                                 "Canvas tilt — tap for the Measurement menu", Metrics.IconPitch / 2);
    }

    /// <summary>One readout: an optional padlock, then the value, in a cell that
    /// opens the Measurement panel and hovers the §17.1 pill.</summary>
    private FrameworkElement ReadoutCell(TextBlock value, bool locked, double minWidth,
                                         string automationName, string tip, double rightMargin)
    {
        value.MinWidth = minWidth;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        // Padlock BEFORE the value, matching the order §17.1 draws the hover
        // pill in: `[lock] 10%  [lock] 0°`.
        if (locked) row.Children.Add(Icons.Mark(Icons.LockClosed, BarInk(), 12));
        row.Children.Add(value);

        // §17.2: "The zoom and tilt readouts take a stadium shape — a rounded-end
        // capsule sized to their text." Sized to the text because the Border
        // wraps `row` rather than being given a width: the capsule grows when the
        // padlock joins the row, which is the same re-layout §17.1 asks for seen
        // from the other side.
        var stadium = new Border
        {
            Child = row,
            Height = Metrics.StadiumH,
            Padding = new Thickness(Metrics.StadiumPadX, 0, Metrics.StadiumPadX, 0),
            CornerRadius = new CornerRadius(Metrics.StadiumH / 2),
            VerticalAlignment = VerticalAlignment.Center,
            Background = GroundBrush(),
        };

        var cell = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
            Height = Metrics.IconPitch,
            Margin = new Thickness(0, 0, rightMargin, 0),
            Children = { stadium },
        };
        ToolTipService.SetToolTip(cell, HoverPill(tip));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, automationName);
        cell.Tapped += (_, e) => { Measurement.Toggle(); e.Handled = true; };
        return cell;
    }

    /// <summary>§17.1's hover treatment: "Hover shows the two readouts as a dark
    /// pill carrying a padlock beside each value — <c>[lock] 10%  [lock] 0°</c>".
    ///
    /// <para>A real <see cref="ToolTip"/> with its chrome stripped, rather than a
    /// hand-managed hover element: the framework already owns the open delay, the
    /// placement against the pointer and the dismissal, and re-implementing those
    /// on a bar that rebuilds itself is how a hover element gets stranded up.
    /// The pill is DARK on every page for the same reason ValuePopover's tool
    /// chip is — it reads as a system label rather than as another panel — which
    /// is also what §17.1's own word "dark" asks for.</para>
    ///
    /// <para><b>Filled on Opened, not at Build.</b> The zoom value moves on every
    /// pinch and Ctrl+wheel, and those run <see cref="SyncReadouts"/> only — they
    /// do NOT rebuild the bar, because rebuilding a cluster per wheel notch is
    /// what <c>SyncReadouts</c> exists to avoid. A pill whose text was captured
    /// at Build time would therefore show the zoom the user had when the bar was
    /// last built, which on a bar that rebuilds rarely is any number at all. The
    /// content is built each time the tip opens instead, which is the only moment
    /// it can be read.</para></summary>
    private ToolTip HoverPill(string caption)
    {
        var pill = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            // A stadium, like the readout grounds §17.2 gives these cells.
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A)),
        };
        var tip = new ToolTip
        {
            Content = pill,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };
        tip.Opened += (_, _) =>
        {
            try
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
                row.Children.Add(PillPair(_zoomLocked, MeasurementMenu.Percent(SafeZoom())));
                row.Children.Add(PillPair(_tiltLocked, MeasurementMenu.Degrees(0)));

                var stack = new StackPanel { Spacing = 3 };
                stack.Children.Add(row);
                stack.Children.Add(new TextBlock
                {
                    Text = caption,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xF2, 0xF2, 0xF2)),
                });
                pill.Child = stack;
            }
            catch { }
        };
        return tip;
    }

    private static FrameworkElement PillPair(bool locked, string value)
    {
        var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        var ink = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);
        pair.Children.Add(Icons.Mark(locked ? Icons.LockClosed : Icons.LockOpen,
                                     locked ? ink : Color.FromArgb(0x8C, 0xF2, 0xF2, 0xF2), 13));
        pair.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ink),
        });
        return pair;
    }

    private float SafeZoom()
    {
        try { return _h.Surface().ViewZoom; } catch { return 1f; }
    }

    /// <summary>The Measurement panel (§17.1), built on first use. A session that
    /// never taps a readout never pays for it.</summary>
    private MeasurementMenu Measurement
    {
        get
        {
            if (_measure != null) return _measure;
            _measure = new MeasurementMenu(_host, new MeasurementMenu.Host
            {
                Zoom = SafeZoom,
                SetZoom = z =>
                {
                    _h.Surface().SetViewZoom(z);
                    _lockedZoom = z;
                    SyncReadouts();
                },
                ZoomLocked = () => _zoomLocked,
                SetZoomLocked = on =>
                {
                    _zoomLocked = on;
                    _lockedZoom = SafeZoom();
                    // The BAR is what re-lays out, so the bar is what rebuilds.
                    Build();
                },
                // Always 0: there is no canvas rotation to read. The panel says
                // so rather than inventing a number.
                Tilt = () => 0,
                TiltLocked = () => _tiltLocked,
                SetTiltLocked = on =>
                {
                    _tiltLocked = on;
                    // §17.1's observable behaviour: the tilt padlock appearing in
                    // the row is what shifts the zoom readout sideways.
                    Build();
                },
                Status = s => { try { _h.PageOps().Status(s); } catch { } },
                KeepOpenOver = () =>
                {
                    try
                    {
                        var o = _right.TransformToVisual(_host).TransformPoint(new Windows.Foundation.Point(0, 0));
                        return new Windows.Foundation.Rect(o.X, o.Y, _right.ActualWidth, _right.ActualHeight);
                    }
                    catch { return null; }
                },
            });
            // An OBSTACLE, exactly as the two clusters are (K.21) and registered
            // exactly as they are: it is chrome the user did not place, so the
            // movable panes route around it and the solver never moves IT — which
            // matters, because its own position is derived from the bar's metrics
            // and a solver that re-homed it would fight that.
            _layout.Register("measurement", _measure.Root);
            return _measure;
        }
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
