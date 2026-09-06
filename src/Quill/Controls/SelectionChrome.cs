using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE SELECTION PRESENTATION (CONCEPTS-REF 16.2, extended by 16.9).
///
/// <para><b>One presentation, three subjects.</b> 16.9: "a selected stroke gets
/// everything 16.2 gives an attachment... One selection presentation, three
/// kinds of subject." So this class knows nothing about attachments. It reads
/// <see cref="SelectionState"/>, which says what is selected and what it
/// supports, and draws the same four things around whatever that is:</para>
///
/// <list type="bullet">
/// <item>a floating bar centred above - paperclip, padlock, duplicate, waste
/// bin, a DIVIDER, flip horizontal, flip vertical;</item>
/// <item>four small hollow circles at the bounding box's corners;</item>
/// <item><b>full-canvas guide lines projected from the box</b> - verticals at
/// its left and right running the whole viewport height, horizontals at its top
/// and bottom running the whole width. <b>Not a box on the bounds.</b> An
/// earlier version of 16.2 said it was a box and was wrong; these read as
/// alignment guides, which is a different statement about the same
/// rectangle. <b>Amendment (Concepts reference photo, position-while-typing):
/// the four guides are the one piece of this list EDITING also draws</b> - see
/// the paragraph below on why that is a narrower carve-out than it looks;</item>
/// <item>a centred bottom row - Rotate, Scale, Filter, each an icon with its
/// word beside it.</item>
/// </list>
///
/// <para><b>Which marks are live follows 16.3's rule, not the bar's own.</b> A
/// subject that cannot do a thing greys that thing's control - the paperclip is
/// live only for an attachment, because only an attachment has a file behind it;
/// the waste bin and the flips grey while the selection is locked. That is the
/// same sentence 16.3 applies to the dial, applied here, so a user meets one
/// rule in the app rather than two.</para>
///
/// <para><b>ONE BAR, TWO TRIGGERS (11.9 folded in here rather than beside
/// it).</b> 11.9 asks for "quick-action buttons above the text bubble ... a
/// Cancel Editing affordance with a red X, and a row of attach / duplicate /
/// lock / delete marks". Those are this bar's marks, minus the flips, plus one -
/// but they belong to a DIFFERENT STATE, and that is the whole reason this
/// class grew a mode rather than a sibling:</para>
///
/// <list type="bullet">
/// <item><b>SELECTED</b> is <see cref="SelectionState"/>, published by
/// <c>InkSurface.PublishSelection</c> and reached only by the lasso, a click on
/// a stroke (16.10), a paste, Select All and the table row/column selectors.
/// Nothing puts a text box into <c>_selTexts</c> because it was tapped into, so
/// <b>a bubble being typed in publishes no selection and this bar has never
/// appeared for one</b>.</item>
/// <item><b>EDITING</b> is <c>InkSurface.ActiveTextBox</c> - a RichEditBox with
/// the caret in it. It publishes nothing, and the surface it raises today is
/// the pinned top FormatBar.</item>
/// </list>
///
/// <para><b>"Cancel Editing" is what settles which of the two 11.9 means</b>:
/// you cannot cancel editing a box you are not editing, and a lasso-selected
/// text box has no caret and no focus. So 11.9 is the editing state - a second
/// trigger, not a second bar. Folding it in here rather than building a fourth
/// floating strip buys three things a sibling class could not: the two can never
/// be on screen at once, because one object shows one bar; the editing bar
/// inherits <see cref="Metrics"/>, so a later resize of the quick actions moves
/// both; and there is one plate, one mark factory and one greying rule in the
/// app rather than two that drift.</para>
///
/// <para><b>Precedence: SELECTION WINS.</b> A text box can be lassoed and then
/// tapped into, and then both states are true. The selected one is the stronger
/// statement - it is the one carrying handles, guides and the flips - so it is
/// the one that draws. Escape still leaves the box either way.</para>
///
/// <para><b>The editing bar draws NOTHING below the bubble except the four
/// guides</b> - no corner circles, no Rotate / Scale / Filter row. This is an
/// AMENDMENT to the original ruling, not its reversal, and the difference is
/// worth stating precisely because the original reasoning was sound and still
/// holds for the two things it still excludes:</para>
///
/// <list type="bullet">
/// <item>the corner circles are HANDLES - grips that promise a drag will scale
/// or move the box from that corner. Editing has no such gesture (a caret does
/// not scale), so drawing them would offer an action the state cannot perform,
/// which is worse than offering none;</item>
/// <item>the Rotate / Scale / Filter row is a MODE SWITCH - it changes what a
/// drag on the box does next. Editing is already a mode a drag cannot leave by
/// touching the box (the drag would be a text selection), so the row has
/// nothing to attach to.</item>
/// </list>
///
/// <para>The guides fail that same test differently: they assert nothing and
/// offer no gesture. 16.2 already describes them as "alignment guides, not a
/// selection outline" - a position readout, not a control - and the Concepts
/// reference photo this amendment is answering shows exactly that reading in
/// its own editing state: four lines and a quick-action bar, nothing else. A
/// user typing a caption at the edge of a photo needs to see where that box's
/// edges fall against the rest of the page as much as - arguably more than - a
/// user who has merely selected it and is not currently looking at the
/// keyboard. So the carve-out is one line item, not a re-opening of 11.9's
/// question: the bar's marks are still the only INTERACTIVE thing this mode
/// offers, and 11.9's "nothing else" is now read as "no other CONTROL" rather
/// than "no other pixel" - the reading the reference photo itself supports.</para>
///
/// <para>17.9 then says the same thing for the whole presentation - "quick
/// actions stay above the subject, but nothing floats below it" - and moves
/// that row to the bottom of the screen as a mode bar. <b>That move is not made
/// here</b>; it is a separate piece of work with its own owner, and this file
/// deliberately leaves <c>_row</c> where it is rather than half-moving it. The
/// editing state simply never had a row to move, and this amendment does not
/// give it one - the row and the handles stay exactly as absent as they were.</para>
///
/// <para><b>17.12 resizes the quick actions by +80%, and nothing in this file
/// stands in its way.</b> Every size the editing bar uses comes from
/// <see cref="Metrics"/> - the same <c>MarkSize</c>, <c>MarkCell</c> and
/// <c>BarHeight</c> the selection bar uses, with the label derived from
/// <c>MarkSize</c> by a dimensionless ratio. One edit to those constants moves
/// both bars, which is the second reason 11.9 is a mode here rather than a
/// surface of its own with a second set of numbers.</para>
///
/// <para><b>Three hit-testing traps this file is built around</b>, all of which
/// have killed overlays in this codebase before:</para>
/// <list type="number">
/// <item><c>IsHitTestVisible = false</c> PROPAGATES to the whole subtree. It is
/// set on the guides and the corner circles - which are decoration and must
/// never intercept a pointer meant for the canvas - and never on the layer, the
/// bar or the row.</item>
/// <item>A <c>null</c> Background is TRANSPARENT TO HIT-TESTING. That is exactly
/// what the layer wants (the canvas underneath must keep every event it would
/// have had) and exactly what the bar and the row must not have, or a click
/// would fall straight through them onto the page.</item>
/// <item><c>PathIcon</c> fills its Data and cannot stroke, so every mark here
/// comes from <see cref="Icons.Mark"/> - which also refuses to clip, unlike a
/// Path in a Grid.</item>
/// </list>
/// </summary>
public sealed class SelectionChrome
{
    public sealed class Host
    {
        public required Action Duplicate { get; init; }
        public required Action Delete { get; init; }
        public required Action ToggleLock { get; init; }
        public required Action<bool> Flip { get; init; }
        public required Action Rotate { get; init; }
        /// <summary>Live only when the subject is a single attachment - it is
        /// the one subject with a file behind it to replace.</summary>
        public required Action ReplaceAttachment { get; init; }
        public required Func<bool> IsBlocked { get; init; }

        // ---- 11.9: the same four commands, on the box being EDITED ---------
        // Deliberately NOT the four above. Duplicate, Delete and ToggleLock all
        // read _selected / _selShapes / _selTexts, every one of which is empty
        // while a box is merely being typed in - so pointing the editing bar at
        // them would give it four marks that quietly did nothing. They are also
        // not folded into those methods as a fallback: Delete and Ctrl+D reach
        // DeleteSelection and DuplicateSelection from the keyboard, and a
        // fallback would turn the Delete key, pressed mid-sentence, into "throw
        // this text box away".

        /// <summary>11.9's red X. Cancels EDITING, never the text.</summary>
        public required Action CancelEditing { get; init; }
        public required Action DuplicateEditingText { get; init; }
        public required Action ToggleEditingTextLock { get; init; }
        public required Action DeleteEditingText { get; init; }

        // ---- 17.9: the mode bar -------------------------------------------
        // Getters as well as setters, because these are MODES: the bar draws
        // each one's current state, and a bar that only set state would have to
        // keep a second copy of it and then disagree with the surface that acts
        // on it. Same shape as the Measurement menu's locks.

        /// <summary>17.9 item 1. On, a drag around the subject turns it and a tap
        /// turns a quarter - which is 17.11's rotate tool, aimed at this
        /// selection, rather than a second way to do the same thing.</summary>
        public required Func<bool> RotateMode { get; init; }
        public required Action<bool> SetRotateMode { get; init; }
        /// <summary>17.9 item 2. On, the whole selection box is a scale grip.</summary>
        public required Func<bool> ScaleMode { get; init; }
        public required Action<bool> SetScaleMode { get; init; }
        /// <summary>17.9 item 2's second half: uniform, or free aspect.</summary>
        public required Func<bool> Stretch { get; init; }
        public required Action<bool> SetStretch { get; init; }
        /// <summary>17.9 item 3. Opens the colour picker AND descends the bottom
        /// menu into the picker's own - which is what gives that menu its back
        /// button, since it is then covering this one.</summary>
        public required Action OpenFilter { get; init; }
    }

    /// <summary>Which of the two states is on screen. One field, because one
    /// object draws one bar - see the class remarks for why 11.9 is a mode here
    /// rather than a fourth floating surface.</summary>
    private enum Mode { None, Selection, Editing }

    /// <summary>Every number this surface is laid out with, in one block, like
    /// <see cref="ChromeBars.Metrics"/> and <see cref="FullscreenChrome.Metrics"/>.</summary>
    public static class Metrics
    {
        /// <summary><b>17.16: the quick actions are 40% smaller</b> - 1.8 x 0.60.
        /// One factor, applied to the three numbers the buttons are made of, so
        /// the ratio between them - and everything derived from it, including
        /// <see cref="LabelToMark"/> - is untouched by the resize.
        ///
        /// <para>It moves BOTH modes. 11.9's editing bar and 16.2's selection bar
        /// are one bar with two triggers and take their sizes from here, which is
        /// the second reason that is a mode rather than a second surface. 17.16
        /// says so in as many words - "it drives both quick-action modes, which
        /// is why it exists as one number; do not split it".</para>
        ///
        /// <para>1.08 is close to unscaled, so the marks were re-rendered at the
        /// 17.28 DIP this now asks for rather than assumed to survive it - the
        /// lesson 11.23 paid for.</para></summary>
        public const double QuickScale = 1.08;
        /// <summary>Bar marks. 16 DIP inside a 30 DIP cell - the same ratio the
        /// top bar runs (a 16 DIP mark in a 26 DIP box, section 9.6) with a
        /// little more air, because this bar floats over the drawing rather than
        /// sitting in a rule-bounded strip.</summary>
        public const double MarkSize = 16 * QuickScale,
                            MarkCell = 30 * QuickScale,
                            BarHeight = 34 * QuickScale;
        /// <summary>What the bottom row was before 17.9 moved it to the bottom of
        /// the screen. Kept as the origin of <see cref="LabelToMark"/> and of
        /// <see cref="BottomMenu.Metrics"/>'s own doubling; nothing lays out
        /// against them here any more.</summary>
        public const double RowMarkSize = 15, RowFontSize = 12.5;
        /// <summary>The word beside a mark ON THE BAR - 11.9's "Cancel Editing".
        ///
        /// <para>NOT a new size decision. 0.833 is the ratio the bottom row
        /// already runs between its word and its mark (12.5 / 15), and a ratio
        /// is dimensionless - so it survived 17.12 scaling the quick actions by
        /// +80% and the bottom menu by +100%, and 17.16 scaling both back down,
        /// without either number being touched here. Expressed as its own constant rather than read off
        /// <see cref="RowFontSize"/> and <see cref="RowMarkSize"/> because 17.9
        /// moves that row off the subject entirely and to the bottom of the
        /// screen; this must not have to move with it.</para></summary>
        public const double LabelToMark = 0.833;
        public static double LabelSize => MarkSize * LabelToMark;
        /// <summary>Clearance from the bounding box to the bar and to the row.
        /// Enough that neither touches a corner circle at any zoom.</summary>
        public const double Gap = 14;
        /// <summary>The corner circles. 9 DIP across with a 1.5 DIP rule - small
        /// enough to mark a corner rather than decorate it, large enough that
        /// the ring still reads as a ring at 100%.</summary>
        public const double HandleSize = 9, HandleStroke = 1.5;
        /// <summary>The guides. One DIP and low-contrast on purpose: 16.2 calls
        /// them "thin and low-contrast; they read as alignment guides, not as a
        /// selection outline", and a heavier rule turns them straight back into
        /// the box the section says they are not.</summary>
        public const double GuideThickness = 1;
        public const byte GuideAlpha = 56;
        /// <summary>The divider between the destructive group and the flips.</summary>
        public const double DividerHeight = 18;

        /// <summary>THE GAP BETWEEN QUICK ACTIONS, and it is a safety number
        /// rather than a styling one.
        ///
        /// <para>The bar used to run at <c>Spacing = 0</c>: seven 32.4 x 36.7 DIP
        /// targets ABUTTING, with Delete sharing an edge with Duplicate. Two
        /// commands one pixel apart, one of which cannot be taken back, and no
        /// dead space at all between them — a press that lands one DIP wide of
        /// Duplicate lands ON Delete. The user's ruling: space them.</para>
        ///
        /// <para>6 is the app's small-separation unit — it is the gap
        /// <see cref="BottomMenu.Metrics.MarkToWord"/> puts between a mark and
        /// its word — taken on this surface's own
        /// <see cref="QuickScale"/> so it tracks the bar if the bar is ever
        /// resized again. It is a gap between TARGETS, so it is genuinely dead:
        /// the cell IS the button, and nothing is laid over the space between
        /// two cells.</para></summary>
        public const double MarkGap = 6 * QuickScale;
        /// <summary>Keep both plates this far inside the viewport, so a selection
        /// dragged to an edge does not push its own controls off screen.</summary>
        public const double EdgeInset = 8;
    }

    /// <summary>11.9's red X, as a PAIR rather than a constant, and the pair is
    /// measured rather than picked.
    ///
    /// <para><c>#C42B1C</c> is the red this app already carries - it is
    /// <c>FullscreenChrome.CloseHot</c>, Windows' own close-hover red - and
    /// reusing it beats introducing a second red. But one red cannot serve both
    /// ends of the panel range, and against the WORST case at each end the
    /// contrast is:</para>
    ///
    /// <code>
    ///                    light L*95      dark L*29
    ///   #C42B1C            5.01            1.72     &lt;- fails on dark
    ///   #FF6C50            2.48            3.48     &lt;- fails on light
    /// </code>
    ///
    /// <para>So the light end keeps the app's red and the dark end takes the
    /// same Lab hue and chroma lifted +22 L*, which is the smallest lift that
    /// clears the 3:1 floor for a non-text mark against an L* 29 panel. Both
    /// worst cases are stated because the middle of a range is not where a
    /// palette fails - the ends are.</para>
    ///
    /// <para><b>§27 restated the range and re-keyed the pick.</b> The L* 95..97.5
    /// light / 13..29 dark figures above were the SUPERSEDED <c>PageTheme</c>
    /// ramp's and are kept only because the two contrast measurements were taken
    /// against them; §27's panel is a page-derived mix and over the nine shipped
    /// papers it runs L* 27.51 (Darkprint) to L* 81.33 (Plain White), which is
    /// wider at the light end and lands inside the dark figure above. What
    /// actually changed here is the SELECTOR: <c>PageTheme.IsDark</c> asks about
    /// the SHELL, and this red stands on <c>_bar</c>, which is Panel. On the
    /// default install those two disagree, and the disagreement picks the red
    /// that fails.</para>
    ///
    /// <para>Deliberately not derived from <see cref="PageTheme.Accent"/>: the
    /// accent is the user's own choice and can be any hue, including green. A
    /// mark 11.9 specifies as red has to be red.</para></summary>
    private static readonly Color CancelRedLight = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);
    private static readonly Color CancelRedDark = Color.FromArgb(0xFF, 0xFF, 0x6C, 0x50);
    private static Color CancelRed => PageTheme.PanelIsDark ? CancelRedDark : CancelRedLight;

    private readonly Grid _host;
    private readonly InkSurface _surface;
    private readonly Host _h;

    // A Canvas, not a Grid: a Canvas arranges each child at its own desired size
    // and never imposes a layout clip, which is the same reason Icons.Mark hosts
    // its art in one.
    private readonly Canvas _layer = new();
    private readonly Rectangle[] _guides = new Rectangle[4];
    private readonly Ellipse[] _handles = new Ellipse[4];
    private readonly Border _bar;
    /// <summary><b>17.9's mode bar, and still this presentation's row.</b> The
    /// row was Rotate / Scale / Filter floating under the subject; 17.9 moves it
    /// to the bottom of the SCREEN and makes its three marks modes. It is not
    /// re-homed into another class on the way, because it still appears with a
    /// selection, disappears with it, and greys on the same locked rule as the
    /// bar above - it is the selection presentation's row that now lives
    /// somewhere else. <see cref="BottomMenu"/> owns only where it goes and what
    /// may cover it.</summary>
    private readonly Border _row;
    private readonly StackPanel _barItems =
        new() { Orientation = Orientation.Horizontal, Spacing = Metrics.MarkGap };
    private readonly StackPanel _rowItems = BottomMenu.Items();

    private Mode _mode = Mode.None;

    public static SelectionChrome Attach(Grid host, InkSurface surface, Host h) => new(host, surface, h);

    private SelectionChrome(Grid host, InkSurface surface, Host h)
    {
        _host = host;
        _surface = surface;
        _h = h;

        // A null Background: the layer spans the whole canvas area and must be
        // invisible to hit-testing, or it would swallow every stroke. Its
        // CHILDREN still hit-test normally, which is what the bar needs.
        _layer.Background = null;
        _layer.Visibility = Visibility.Collapsed;
        Grid.SetRow(_layer, 0);
        Grid.SetRowSpan(_layer, Math.Max(1, host.RowDefinitions.Count));
        // Above the floating panels (10-12), below the pen row and the dial (60)
        // and the bare-canvas pane (65): the selection's controls belong over the
        // page, never over the tool surfaces.
        Canvas.SetZIndex(_layer, 40);
        host.Children.Add(_layer);

        for (int i = 0; i < 4; i++)
        {
            // IsHitTestVisible = false is right HERE and nowhere else in this
            // file: a guide is decoration, and the canvas's own corner-scale hit
            // test lives at the same place in world space.
            _guides[i] = new Rectangle { IsHitTestVisible = false };
            _layer.Children.Add(_guides[i]);
        }
        for (int i = 0; i < 4; i++)
        {
            // THE CORNER CIRCLES. Geometry only here; both colours are
            // Repaint()'s, because they are page-derived and the page turns
            // under a handle that was constructed once - see PageTheme.Changed
            // below, and see Repaint() for why the fill and the rule are ONE
            // co-keyed pair (PageSurface + OnPage) rather than two choices.
            //
            // StrokeThickness is centred on the ellipse path, so the rule
            // straddles the boundary: its inner half composites over Fill and
            // its outer half over whatever the handle landed on. That is why
            // Repaint() measures the rule twice - against the disc and against
            // the paper - and why a token that satisfies only one of the two is
            // not a fix.
            _handles[i] = new Ellipse
            {
                Width = Metrics.HandleSize,
                Height = Metrics.HandleSize,
                StrokeThickness = Metrics.HandleStroke,
                IsHitTestVisible = false,
            };
            _layer.Children.Add(_handles[i]);
        }

        _bar = Plate(_barItems, Metrics.BarHeight);
        // 17.9: the row is NOT added to this layer and is NOT placed by Place().
        // It goes to the bottom of the screen, which is a different surface with
        // a different origin - see BottomMenu, and see _row's own remarks for why
        // it is still built here.
        _row = BottomMenu.Plate(_rowItems);
        _layer.Children.Add(_bar);
        _bar.SizeChanged += (_, _) => Place();
        // The stack changed under the row: whether it is the visible page, and
        // whether the page above it shows a back button, are BottomMenu's answers
        // and they change without the selection changing.
        BottomMenu.Changed += Build;

        SelectionState.Changed += Sync;
        // 11.9's trigger. Which box is being edited, and where that box is, are
        // two separate questions and neither answers the other: ActiveTextChanged
        // fires when the caret moves to another bubble (mode, and which subject),
        // EditingTextGeometryChanged when the bubble it is already in grows a
        // line or is dragged (placement only).
        _surface.ActiveTextChanged += _ => Sync();
        _surface.EditingTextGeometryChanged += OnEditingMoved;
        _surface.ViewChanged += OnViewMoved;
        // The subject can move while the view holds still - a drag, a live
        // scale, and the recompute after the drop. Place() rather than Sync():
        // WHAT is selected has not changed, only where it is, so re-running the
        // blocked check and rebuilding the bar's buttons would be waste.
        _surface.SubjectMoved += Place;
        _host.SizeChanged += (_, _) => Place();
        PageTheme.Changed += Repaint;

        Repaint();
        Sync();
    }

    private static Border Plate(FrameworkElement child, double height) => new()
    {
        Child = child,
        Height = height > 0 ? height : double.NaN,
        CornerRadius = new CornerRadius(10),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(4, 0, 4, 0),
        // NEVER null: a null Background is transparent to hit-testing and every
        // press on this plate would land on the page behind it.
        Background = new SolidColorBrush(Colors.Transparent),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    // =====================================================================
    // Paint
    // =====================================================================

    public void Repaint()
    {
        // §0, caught by the Editing-mode carve-out: this used to read OnSurface,
        // which PageTheme keys to the SHELL's ground (Ground / IsDark), not to
        // the page these guides are actually drawn over. On the default install
        // ThemeSource is "Manual" - a pinned shell colour that knows nothing
        // about the loaded paper - so shell and page routinely disagree, and a
        // shell-keyed ink drawn on the page is exactly the split pair 2.1 wrote
        // OnPage/PageOutline to close for the bare-canvas case. Measured: a
        // pinned dark shell under Plain White paper took this guide from
        // 1.614:1 to 1.017:1 - the guide's alpha-56 ink landing one unit off the
        // page colour it sits on, functionally invisible. OnPage is keyed to
        // PageGround instead, so it cannot invert this way regardless of what
        // the shell is pinned to; across the nine shipped papers at
        // GuideAlpha=56 it now holds 1.377:1 (Blueprint) to 1.958:1 (Darkprint).
        // Still deliberately faint - see GuideAlpha's own remarks, this is a
        // thin alignment guide, not a mark held to the 3:1 non-text floor - but
        // faint on purpose is different from accidentally gone.
        var guide = PageTheme.WithAlpha(PageTheme.OnPage, Metrics.GuideAlpha);
        foreach (var g in _guides) g.Fill = new SolidColorBrush(guide);
        foreach (var e in _handles)
        {
            // Hollow: THE PAGE'S OWN SURFACE INSIDE, A RULE AROUND IT. A ring
            // with no fill at all vanishes the moment it lands on dark ink,
            // which at the corner of a bounding box is where it usually lands.
            //
            // 39.8, the audit §39.6 flagged and did not run: that sentence says
            // "the page's own surface", and until now the code said Surface /
            // OnSurface - the SHELL's raise and the SHELL's ink. Same §0 defect
            // as the guides four lines up, in a pair rather than in one mark,
            // and it fails in whichever direction the mismatch runs. Measured
            // over the three grounds MainWindow.ResolveGround can pin
            // (#F7F6F1 / #0F0E10 / #000000) against the nine shipped papers:
            //
            //   pinned dark shell + Plain White   rule vs paper  1.053:1
            //   pinned dark shell + Transparent   rule vs paper  1.000:1  <- exact
            //   default light shell + Darkprint   rule vs paper  1.184:1
            //   OLED shell + Darkprint            disc vs paper  1.021:1
            //
            // 1.000:1 is the rule rendering the exact colour of the paper it is
            // supposed to sit on. The handle does not disappear outright - when
            // the rule goes the disc is over-strong (11.9:1 on white paper, a
            // shell-coloured blob) and when the disc goes the rule is - but
            // neither is the faint raised circle 16.2 describes, and one of the
            // two is always gone.
            //
            // BOTH halves move together, and that is the point rather than an
            // extra: re-keying only the ink to OnPage relocates the break
            // instead of closing it - a dark shell's Surface under a white page
            // would then take the page's DARK ink and the rule falls to 1.194:1
            // against its own disc. PageSurface is Surface's raise evaluated on
            // PageGround, so the pair is co-keyed again and is BYTE-IDENTICAL to
            // what shipped whenever ThemeSource is "Page". Across the nine
            // papers it now holds, for every pinned shell: disc vs paper
            // 1.488:1-1.845:1, rule vs paper 2.274:1-5.441:1, rule vs disc
            // 1.541:1-3.994:1.
            //
            // Blueprint and Brown Paper are the floor of that last range and
            // are NOT a regression this introduced - a mid-tone ground raises
            // its surface TOWARD the light ink §7 rules those two papers carry,
            // and Page mode has always shown exactly 1.567:1 / 1.541:1 there.
            // PagePlate.PanelInk's best-of would lift them; that is §27's
            // heavier endpoint and a ruling about what a handle IS, so it is
            // flagged here rather than folded into a re-key.
            e.Fill = new SolidColorBrush(PageTheme.PageSurface);
            e.Stroke = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnPage, 150));
        }
        // §27: the two PLATES are Panel, so their rules are PanelOutline. The
        // guides and handles above are NOT - they are drawn straight onto the
        // page over the subject, so they take the page-keyed tokens instead:
        // OnPage for the guides, PageSurface/OnPage for the handles. The two
        // groups are three lines apart and take different tokens on purpose -
        // and the difference is now which SHAPE of page-keyed token each needs,
        // not which GROUND each is keyed to. Nothing in this method reads a
        // shell-keyed colour any more; if a line here ever does again, that is
        // the bug.
        foreach (var p in new[] { _bar, _row })
        {
            p.Background = new SolidColorBrush(PageTheme.Panel);
            p.BorderBrush = new SolidColorBrush(PageTheme.PanelOutline);
        }
        Build();
    }

    private void Build()
    {
        if (_mode == Mode.Editing) { BuildEditingBar(); return; }
        BuildSelectionBar();
    }

    /// <summary>11.9's bar. THE SAME FOUR MARKS AS THE SELECTION BAR'S FIRST
    /// GROUP, IN THE SAME ORDER, with the red X in front of them.
    ///
    /// <para><b>16.2's order is used, not 11.9's, and that is a decision.</b>
    /// 11.9 lists "attach / duplicate / lock / delete"; 16.2 enumerates the same
    /// four as "a paperclip, a padlock, a duplicate mark, a waste bin". The two
    /// disagree on whether the padlock or the duplicate comes second. Both are
    /// transcriptions of the reference, so one of them is a slip - and the same
    /// four marks appearing in two different orders in one app, on two surfaces
    /// a user reaches for the same object, is a defect whichever way it is
    /// resolved. 16.2's is taken because it is the more recent reading and the
    /// more careful one (a measured capture, enumerated left to right, against a
    /// one-line item in a numbered list). Flagged in the report rather than
    /// settled silently.</para>
    ///
    /// <para><b>The paperclip is present and dead</b>, on this file's existing
    /// rule rather than a new one: only an attachment has a file behind it to
    /// replace, and a text box is not one. 11.9 lists it, so it is drawn; it
    /// cannot act, so it greys and says why.</para>
    ///
    /// <para><b>The flips are not here.</b> 11.9 does not list them, and a
    /// mirrored text box is unreadable - which is presumably why.</para></summary>
    private void BuildEditingBar()
    {
        bool locked = _surface.EditingTextLocked;
        // §27: every mark below is added to _barItems, which is _bar's child, and
        // _bar is Panel.
        var ink = PageTheme.OnPanel;

        _barItems.Children.Clear();
        // STROKED, and through Icons.Mark rather than Icons.Stroked: Close IS
        // two crossed lines, and Stretch.Uniform (which is what Icons.Stroked
        // applies) fits the CENTRELINE to the box and then adds the pen width
        // outside it, so the layout clips half the stroke off each end. That is
        // the defect Icons.Mark was written to refuse. Thickness is in GRID
        // units there, so 3 on the 24 grid is 2 DIP at MarkSize 16 and stays
        // proportional if the marks are ever resized.
        _barItems.Children.Add(Word(Icons.Close, "Cancel Editing", true, _h.CancelEditing, CancelRed,
                                    stroked: true, size: Metrics.MarkSize));
        _barItems.Children.Add(Divider());
        _barItems.Children.Add(Mark(Icons.Paperclip, "Replace attachment", false, () => { }, ink,
            deadTip: "Only an attachment has a file to replace"));
        _barItems.Children.Add(Mark(locked ? Icons.LockClosed : Icons.LockOpen,
            locked ? "Unlock" : "Lock", true, _h.ToggleEditingTextLock, ink));
        _barItems.Children.Add(Mark(Icons.Duplicate, "Duplicate", true, _h.DuplicateEditingText, ink));
        // Delete last here too, behind a divider. 11.9's bar and 16.2's are one
        // bar with two triggers — Metrics.QuickScale says so in as many words —
        // so a safety ruling that applies to one applies to both, or the same
        // press means two different things depending on which mode you are in.
        // 11.9 does not list the flips, so this bar has nothing between
        // Duplicate and Delete but the divider; that is still a fence where
        // there was a shared edge.
        _barItems.Children.Add(Divider());
        _barItems.Children.Add(Mark(Icons.WasteBin, "Delete", !locked, _h.DeleteEditingText, ink,
            deadTip: "This text box is locked"));
    }

    private void BuildSelectionBar()
    {
        var s = SelectionState.Current;
        bool locked = _surface.SelectionLocked;
        bool attach = _surface.SelectedAttachment != null;
        // §27: same as BuildEditingBar - _barItems is inside the Panel plate.
        var ink = PageTheme.OnPanel;

        _barItems.Children.Clear();
        // 16.2's order, left to right — WITH DELETE MOVED TO THE END, at the
        // user's ruling, and the dividers carrying the grouping.
        //
        // 16.2 put the four "existence" marks together and fenced the two
        // orientation marks off behind a divider, which left Delete sharing an
        // edge with Duplicate at Spacing = 0. Duplicate and Delete are the two
        // commands most easily confused for one another, they were the two
        // closest together, and exactly one of them cannot be taken back. So
        // Delete goes to the far end, behind its own divider: the last thing in
        // the row, reached deliberately, with the whole flip group and two
        // dividers between it and Duplicate. Nothing about WHAT the marks do
        // changed; the reading of 16.2's grouping did, and this is the one that
        // now stands.
        _barItems.Children.Add(Mark(Icons.Paperclip, "Replace attachment", attach, _h.ReplaceAttachment, ink,
            deadTip: "Only an attachment has a file to replace"));
        _barItems.Children.Add(Mark(locked ? Icons.LockClosed : Icons.LockOpen,
            locked ? "Unlock" : "Lock", true, _h.ToggleLock, ink));
        _barItems.Children.Add(Mark(Icons.Duplicate, "Duplicate", true, _h.Duplicate, ink));
        _barItems.Children.Add(Divider());
        _barItems.Children.Add(Mark(Icons.FlipHorizontal, "Flip horizontal", !locked, () => _h.Flip(true), ink,
            deadTip: "The selection is locked"));
        _barItems.Children.Add(Mark(Icons.FlipVertical, "Flip vertical", !locked, () => _h.Flip(false), ink,
            deadTip: "The selection is locked"));
        _barItems.Children.Add(Divider());
        _barItems.Children.Add(Mark(Icons.WasteBin, "Delete", !locked, _h.Delete, ink,
            deadTip: "The selection is locked"));

        BuildModeBar(locked);

        _ = s;   // the subject drives WHICH marks are live via the flags above
    }

    /// <summary>17.9's three-part mode bar: Rotate on/off, Scale on/off and
    /// stretch, Filter into the colour picker.
    ///
    /// <para><b>A mode that is on grows the control that qualifies it, to its
    /// right.</b> Rotate on adds a quarter turn; Scale on adds uniform/stretch.
    /// That is not invented here - it is the shape 17.10 gives the mouse tool,
    /// where choosing Lasso makes Partial/Complete appear beside it - so the two
    /// menus teach the same gesture rather than each teaching its own.</para>
    ///
    /// <para><b>No back button.</b> Not by omission: this is the bottom of the
    /// stack, so <see cref="BottomMenu.Lead"/> yields nothing for it, and it is
    /// asked the same question every other page is asked.</para></summary>
    private void BuildModeBar(bool locked)
    {
        var menu = BottomMenu.Current;
        _rowItems.Children.Clear();
        if (menu == null) return;
        foreach (var lead in menu.Lead(BottomPage.Modes)) _rowItems.Children.Add(lead);

        bool rotate = _h.RotateMode();
        _rowItems.Children.Add(BottomMenu.Cell(Icons.Rotate, "Rotate", rotate,
            () => _h.SetRotateMode(!rotate), live: !locked,
            tip: locked ? "The selection is locked"
                        : "Drag around the subject to turn it; tap it to turn a quarter"));
        if (rotate && !locked)
            _rowItems.Children.Add(BottomMenu.Cell(Icons.UndoRound, "Quarter", false,
                _h.Rotate, tip: "Turn a quarter, without dragging"));

        bool scale = _h.ScaleMode();
        _rowItems.Children.Add(BottomMenu.Cell(Icons.Scale, "Scale", scale,
            () => _h.SetScaleMode(!scale), live: !locked,
            tip: locked ? "The selection is locked"
                        : "Drag anywhere in the box to resize it from the opposite corner"));
        if (scale && !locked)
        {
            bool stretch = _h.Stretch();
            _rowItems.Children.Add(BottomMenu.Toggle(stretch,
                Icons.Scale, "Uniform", Icons.ScaleStretch, "Stretch",
                on => _h.SetStretch(on),
                tip: "Uniform keeps the aspect; stretch frees it"));
        }

        _rowItems.Children.Add(BottomMenu.Cell(Icons.Filter, "Filter", false, _h.OpenFilter,
            tip: "Colour and alpha"));
    }

    private FrameworkElement Divider() => new Rectangle
    {
        Width = 1,
        Height = Metrics.DividerHeight,
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        // §27: a fence between marks that are themselves on the Panel plate.
        Fill = new SolidColorBrush(PageTheme.PanelOutline),
        IsHitTestVisible = false,
    };

    private Button Mark(string geometry, string tip, bool live, Action click, Color ink, string? deadTip = null)
    {
        var art = Icons.Mark(geometry, live ? ink : PageTheme.WithAlpha(ink, 70), Metrics.MarkSize);
        art.HorizontalAlignment = HorizontalAlignment.Center;
        art.VerticalAlignment = VerticalAlignment.Center;
        var cell = new Grid
        {
            Width = Metrics.MarkCell,
            Height = Metrics.BarHeight,
            // Same reason as the plate's own: this cell IS the target.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        cell.Children.Add(art);
        return Press(cell, tip, live, click, deadTip);
    }

    /// <summary>A mark with its word beside it. The bottom row's items, and
    /// 11.9's Cancel Editing.
    ///
    /// <para><paramref name="stroked"/> exists for that one caller:
    /// <see cref="Icons.Close"/> IS two crossed lines and has no outline to
    /// fill. It is reused rather than a second X being authored, on the same
    /// rule the padlock follows in Icons.cs - a second copy of a mark this app
    /// already has is exactly the drift that file exists to prevent.</para></summary>
    private Button Word(string geometry, string label, bool live, Action click, Color ink,
                        string? deadTip = null, bool stroked = false, double? size = null)
    {
        var paint = live ? ink : PageTheme.WithAlpha(ink, 70);
        double s = size ?? Metrics.RowMarkSize;
        var art = Icons.Mark(geometry, paint, s, stroked, thickness: 3);
        art.VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(4, 4, 4, 4),
        };
        stack.Children.Add(art);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = size == null ? Metrics.RowFontSize : Metrics.LabelSize,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(paint),
        });
        return Press(stack, label, live, click, deadTip);
    }

    private Button Press(FrameworkElement content, string tip, bool live, Action click, string? deadTip)
    {
        var b = new Button
        {
            Content = content,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = live,
            // THE TRAP THAT WOULD HAVE KILLED 11.9's BAR. A Button takes focus
            // when it is clicked, and the subject of the editing bar IS the
            // focused RichEditBox - so without this, pressing Duplicate would
            // blur the box, InkSurface would clear ActiveTextBox, and the bar
            // would vanish from under the pointer before its Click ever ran.
            // MainWindow's FormatBar sets AllowFocusOnInteraction="False" on
            // every one of its buttons for exactly this reason; this is that
            // same line. Cancel Editing does its blur explicitly in the handler
            // rather than relying on the side effect, so the one command that
            // WANTS the box blurred does not depend on click ordering either.
            AllowFocusOnInteraction = false,
        };
        // A disabled Button still shows a tooltip in WinUI, which is the point:
        // a control that greys should be able to say why.
        ToolTipService.SetToolTip(b, live ? tip : deadTip ?? tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        if (live)
            b.Click += (_, _) =>
            {
                click();
                // The selection bar is rebuilt by SelectionState.Changed, which
                // every one of its commands raises. The EDITING bar has no such
                // event - toggling the padlock changes nothing SelectionState
                // knows about - so it re-asks here. Rebuilding a bar from inside
                // one of its own buttons' Click is what the selected path
                // already does (Duplicate -> ClearSelection -> PublishSelection
                // -> Changed -> Sync -> Build), so this is the same shape, not a
                // new risk.
                if (_mode == Mode.Editing) Sync();
            };
        return b;
    }

    // =====================================================================
    // Placement
    // =====================================================================

    /// <summary>Re-ask the host whether this presentation is allowed on screen.
    /// The host owns that answer - <see cref="Host.IsBlocked"/> - so the host is
    /// also what knows when it has changed, and calls this.</summary>
    public void Refresh() => Sync();

    /// <summary>Which state is on screen. SELECTION WINS when both are true - a
    /// text box can be lassoed and then tapped into, and the selected reading is
    /// the stronger one: it is the one with handles, guides and the flips.
    ///
    /// <para>Blocked beats both, unchanged: the COPIC wheel covers the canvas
    /// (9.3) and an export moves the view before capturing it, and a floating
    /// bar must not survive either.</para></summary>
    private Mode Wanted()
    {
        if (_h.IsBlocked()) return Mode.None;
        if (SelectionState.Current.Any) return Mode.Selection;
        // Both halves are asked. EditingText is null for a table cell and for a
        // box that has just been torn down, and the bounds are empty until the
        // container has been through a layout pass - a bar placed off an empty
        // rect would land in the corner of the canvas for one frame.
        if (_surface.EditingText != null && !_surface.EditingTextBoundsWorld.IsEmpty)
            return Mode.Editing;
        return Mode.None;
    }

    private void Sync()
    {
        var want = Wanted();
        if (want != _mode)
        {
            _mode = want;
            _layer.Visibility = want == Mode.None ? Visibility.Collapsed : Visibility.Visible;
            // Amendment: the guides now stand for EITHER mode - a position
            // readout is exactly what the Concepts reference uses them for
            // while a box is being typed into, not just while it is selected.
            // The corner circles and the bottom row are unchanged: they are
            // handles and a mode switch, not a readout, and Editing has no drag
            // gesture on the box for either of them to attach to (see the class
            // remarks). So they still stand down while editing rather than
            // being drawn around a box the user is typing in.
            var showGuides = want != Mode.None ? Visibility.Visible : Visibility.Collapsed;
            var deco = want == Mode.Selection ? Visibility.Visible : Visibility.Collapsed;
            foreach (var g in _guides) g.Visibility = showGuides;
            foreach (var e in _handles) e.Visibility = deco;
            _row.Visibility = deco;
            // 17.9: and the row is PUBLISHED or RETRACTED, because it no longer
            // lives on this layer and collapsing a plate the bottom surface is
            // still showing would leave that surface displaying nothing rather
            // than falling through to the page underneath.
            if (want == Mode.Selection) BottomMenu.Current?.Publish(BottomPage.Modes, _row);
            else BottomMenu.Current?.Retract(BottomPage.Modes);
        }
        if (want == Mode.None) return;
        Build();     // lock state and subject kind decide which marks are live
        Place();
    }

    /// <summary>The bubble being edited grew a line, or its grip was dragged.
    /// Placement only - the subject has not changed, so nothing is rebuilt.</summary>
    private void OnEditingMoved()
    {
        if (_mode != Mode.Editing) { Sync(); return; }
        Place();
    }

    /// <summary>Everything here is in CANVAS-AREA (screen) DIP, converted from
    /// the selection's WORLD bounds through the surface's own
    /// <see cref="InkSurface.WorldToScreen"/> - not through a second copy of the
    /// pan/zoom arithmetic, which is how an overlay drifts from the thing it is
    /// framing at the far end of a 0.1x-16x zoom range.</summary>
    /// <summary>A view change is USUALLY a pan or a zoom, and then only the
    /// placement moves. But an export changes the view too, and an export is one
    /// of the states <see cref="Host.IsBlocked"/> names - so this re-asks the
    /// cheap question first and only falls through to the full sync when the
    /// answer has actually flipped. Two property reads per pan frame, against a
    /// rebuild of every button on the bar.</summary>
    private void OnViewMoved()
    {
        if (Wanted() != _mode) { Sync(); return; }
        Place();
    }

    private void Place()
    {
        if (_mode == Mode.None) return;
        // 11.9 places its bar above THE BUBBLE, which is a different rectangle
        // from the selection's: a focused text box publishes no selection, so
        // SubjectBoundsWorld is empty while it is being edited. Both are world
        // rects and both go through the same WorldToScreen below, so there is
        // still exactly one copy of the pan/zoom arithmetic.
        var w = _mode == Mode.Editing ? _surface.EditingTextBoundsWorld : _surface.SubjectBoundsWorld;
        if (w.IsEmpty) return;

        double vw = _host.ActualWidth, vh = _host.ActualHeight;
        if (vw <= 0 || vh <= 0) return;

        var tl = _surface.WorldToScreen(new Vector2((float)w.Left, (float)w.Top));
        var br = _surface.WorldToScreen(new Vector2((float)w.Right, (float)w.Bottom));
        double x0 = tl.X, y0 = tl.Y, x1 = br.X, y1 = br.Y;
        double cx = (x0 + x1) / 2;

        // THE BAR, centred above the subject. The ONE piece both states share,
        // and the only piece 11.9 asks for. Clamped inside the viewport, because
        // a subject dragged against an edge must not push its own controls off
        // screen - the alternative is a bar the user can see the edge of and
        // cannot reach.
        //
        // THE FORMAT BAR CANNOT COLLIDE WITH THIS, and that is structural rather
        // than lucky. MainWindow's FormatBar lives in Grid.Row 1 and this layer
        // is hosted on CanvasArea, which is Grid.Row 2; the clamp above holds the
        // bar EdgeInset inside the canvas area's own top edge, which is already
        // below the format bar's bottom. So a text box dragged to the top of the
        // page gets its quick actions tucked under the format bar rather than
        // over it, and 15.3's fullscreen-strip collision - which is about the
        // format bar's own top margin - is untouched by any of this.
        double bw = _bar.ActualWidth > 0 ? _bar.ActualWidth : _bar.DesiredSize.Width;
        double bh = _bar.ActualHeight > 0 ? _bar.ActualHeight : Metrics.BarHeight;
        Put(_bar, Clamp(cx - bw / 2, vw - bw), Clamp(y0 - Metrics.Gap - bh, vh - bh));

        // THE GUIDES. Full-canvas, projected from the box: verticals at its left
        // and right running the whole viewport height, horizontals at its top and
        // bottom running the whole width. 16.2 is explicit that this is NOT a box
        // on the bounds, so nothing here is clipped to the rectangle.
        //
        // Amendment: placed for BOTH modes, not gated on Mode.Selection below -
        // this is the one element group Editing also shows (see the class
        // remarks and Sync()). It is placed from the same w/x0/y0/x1/y1 this
        // method already derived above through WorldToScreen, which is why
        // Editing's guides track live as the bubble is typed into and resized:
        // OnEditingMoved and SubjectMoved both call Place(), not just Sync(),
        // for exactly this reason (see their own remarks).
        //
        // AXIS-ALIGNED BY CONSTRUCTION, not by a separate decision made here. A
        // rotated box's Editing bounds come from EditingTextBoundsWorld, whose
        // own doc is explicit that "a rotated bubble reports its unrotated
        // box" - the same approximation SubjectBoundsWorld makes for a rotated
        // shape - so x0/y0/x1/y1 above are already the unrotated AABB before
        // this method ever sees them. That keeps a 37.6 deg text box's guides
        // upright, exactly matching 16.2/17.8's own axis-aligned selection
        // marquee rather than introducing a second, tilted kind of guide this
        // file would be the only place that drew. Kept axis-aligned rather than
        // rotated for the same reason the bar stays horizontal above a tilted
        // box: a guide's whole job is to compare the subject's edge to the rest
        // of the page, and an edge is only comparable to a horizontal ruler and
        // a vertical one if it reads the same way regardless of which way its
        // own box is turned.
        Line(_guides[0], x0, 0, Metrics.GuideThickness, vh);
        Line(_guides[1], x1, 0, Metrics.GuideThickness, vh);
        Line(_guides[2], 0, y0, vw, Metrics.GuideThickness);
        Line(_guides[3], 0, y1, vw, Metrics.GuideThickness);

        // Everything below still describes a SELECTION only and is collapsed
        // while editing: corner circles are handles and the row is a mode
        // switch, neither of which the Editing state has a gesture for (see the
        // class remarks on why the guides' carve-out does not extend to them).
        if (_mode != Mode.Selection) return;

        // THE CORNER CIRCLES, on the box's own corners.
        double r = Metrics.HandleSize / 2;
        Put(_handles[0], x0 - r, y0 - r);
        Put(_handles[1], x1 - r, y0 - r);
        Put(_handles[2], x1 - r, y1 - r);
        Put(_handles[3], x0 - r, y1 - r);

        // NOTHING FLOATS BELOW THE SUBJECT (17.9). The row that used to be
        // centred here is now the screen-bottom mode bar and is placed by
        // BottomMenu against the window, not against these bounds - so there is
        // deliberately no third use of WorldToScreen in this method.
    }

    private static double Clamp(double v, double max) =>
        Math.Max(Metrics.EdgeInset, Math.Min(v, Math.Max(Metrics.EdgeInset, max - Metrics.EdgeInset)));

    private static void Put(FrameworkElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
    }

    private static void Line(Rectangle r, double x, double y, double w, double h)
    {
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
        Put(r, x, y);
    }
}
