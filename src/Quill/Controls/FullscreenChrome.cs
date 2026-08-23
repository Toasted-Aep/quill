using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE HOVER-REVEALED WINDOW CONTROLS (CONCEPTS-REF 15.3).
///
/// <para>In fullscreen the caption bar is gone, so minimise / exit-fullscreen /
/// close have nowhere to live. The reference answer: <b>reach the top edge of
/// the screen and a strip slides in over the top-right</b>, drawn on top of the
/// app's own top bar and covering the right end of its cluster. It hides again
/// the moment the pointer leaves it. Fullscreen only — windowed there is a real
/// caption bar and an overlay would be a second, conflicting one.</para>
///
/// <para><b>There is no reveal RECTANGLE.</b> 15.3 warns that "the reveal region
/// must not swallow ink" — a stroke begun at the very top of the canvas has to
/// survive it — and names the pen row's dead right-click as the same class of
/// fault. So this class never puts an element over the canvas. It attaches ONE
/// passive <c>PointerMoved</c> listener to the root grid with
/// <c>handledEventsToo: true</c>, reads the pointer's y, and never sets
/// <c>Handled</c>. Nothing joins the hit-test path until the strip is actually
/// up, and while it is down its <c>Visibility</c> is Collapsed, which takes it
/// out of hit-testing entirely.</para>
///
/// <para>The reveal is additionally gated on <c>!Pointer.IsInContact</c>: a
/// stroke that starts lower down and travels up to the top edge must not pop a
/// strip out under the nib mid-stroke.</para>
///
/// <para><b>The strip's fill is a FIXED dark. It is NOT theme-derived.</b>
/// This was built the other way first, deriving from <c>PageTheme.Panel</c> on
/// the argument that §0 makes every surface derive from the page ground, and
/// the user reversed it. Do not reinstate the derivation from that argument:
/// the point that settles it was already conceded below, for close-hover red.
/// These are the OS window controls, borrowed. Windows' own caption buttons do
/// not track the colour of your document, and the carve-out that red already
/// needed generalises from the one destructive button to the whole strip.
/// Matching the capture and matching the platform convention agree here, which
/// is why the disagreement with §0 is worth taking.</para>
///
/// <para>The BORDER stays themed, and that is not an inconsistency left behind
/// by the change. The fill's job is to read as OS chrome, which is
/// page-independent. The border's job is to separate the strip from the page
/// behind it, which is page-RELATIVE: on a light page the dark strip already
/// separates itself and the rule barely shows, whereas on Darkprint the strip
/// and the page sit within a few levels of each other and that rule is the only
/// thing dividing them. A fixed border would vanish on exactly the ground that
/// needs it most.</para>
///
/// <para><b>It slides DOWN out of the screen edge and retracts back up.</b> The
/// user chose that over sliding in from the right, because it is the same
/// gesture that revealed it: the pointer pushes at the top edge and the strip
/// comes down to meet it. See <see cref="Tick"/> for why the motion is a
/// hand-pumped tween rather than a Storyboard or a composition animation.</para>
/// </summary>
public sealed class FullscreenChrome
{
    public sealed class Host
    {
        public required Action Minimise { get; init; }
        /// <summary>The MIDDLE mark. 15.3: exit fullscreen, not restore-down.</summary>
        public required Action ExitFullscreen { get; init; }
        public required Action Close { get; init; }
        public required Func<bool> ReduceMotion { get; init; }
    }

    /// <summary>Every number the strip is laid out and animated with, in one
    /// block, like <see cref="ChromeBars.Metrics"/>.</summary>
    public static class Metrics
    {
        /// <summary>DIPs of the screen top that ARM the reveal. A few — enough
        /// that a mouse flung at the edge (which the OS clamps to y = 0) always
        /// lands in it, small enough that it is not a band the user crosses by
        /// accident on the way somewhere else.</summary>
        public const double RevealBand = 4;
        /// <summary>THE HYSTERESIS. Once the strip is up, hugging the top edge
        /// keeps it up out to here — two and a half times the band that armed
        /// it. Without the gap, a pointer resting at y ≈ RevealBand would sample
        /// alternately just inside and just outside it and the strip would
        /// flutter. A dwell timer would also stop the flutter, but it would put
        /// latency into a gesture whose whole point is that it is immediate, so
        /// the dead band is the mechanism and the reveal stays instant.</summary>
        public const double KeepBand = 14;
        /// <summary>Tolerance around the strip's own rectangle, for the same
        /// reason: leaving it must be a decision, not a tremor.</summary>
        public const double StripSlack = 6;
        /// <summary>Caption-bar proportions: the same 32-ish band Windows uses,
        /// so the strip reads as a title bar rather than as a floating card.</summary>
        public const double StripHeight = 34;
        /// <summary>Hit target per mark. Wider than tall, again like a caption
        /// button rather than like the bars' 42 DIP square slots.</summary>
        public const double MarkPitch = 46;
        /// <summary>How many marks <see cref="Build"/> puts in the strip:
        /// minimise, exit fullscreen, close. Build asserts against this in debug
        /// builds, so the two cannot drift apart silently.</summary>
        public const int MarkCount = 3;
        /// <summary>The strip's width, DERIVED rather than written down.
        ///
        /// <para>§17.15 reserves this much of the top bar's right end so the
        /// strip can never come down over a live control. That reservation and
        /// this strip have to agree, and a second copy of "138" in MainWindow is
        /// exactly how two numbers that must agree stop agreeing — the same
        /// reasoning that already made the format bar read
        /// <see cref="StripHeight"/> from here instead of writing 34.</para></summary>
        public const double StripWidth = MarkPitch * MarkCount;
        /// <summary>15 DIP, and the exit-fullscreen mark's weights were chosen
        /// against exactly this number rather than against a large preview —
        /// see <see cref="Icons.FullscreenExit"/>.</summary>
        public const double GlyphSize = 15;
        /// <summary>QUILL'S OWN MOTION, not a duration invented here. 190 out /
        /// 130 back are the app's menu open/close timings (MenuAnim.cs, commit
        /// 9d0d6cf "Menu animations"), and <see cref="Ease"/> is that file's
        /// open curve. Reusing them is what keeps this strip in the same
        /// vocabulary as every flyout in the app.
        ///
        /// <para>They now live in <see cref="Motion"/>, because CONCEPTS-REF
        /// 16.7 asks for the same curve a third time for the page fade and three
        /// copies of three numbers is how a house style stops being one. These
        /// aliases stay so the strip's own metrics still read in one block.</para></summary>
        public const double OpenMs = Motion.OpenMs, CloseMs = Motion.CloseMs;
        /// <summary>Bottom-left corner only: the strip is flush against the top
        /// and right edges of the screen and only its inner corner is free.</summary>
        public const double InnerRadius = 10;
    }

    /// <summary>Close goes red on hover — the one caption affordance users
    /// expect. <c>#C42B1C</c> is Windows' own close-hover red; it is introduced
    /// here and is NOT, as an earlier version of this comment claimed, already
    /// used by MainWindow's <c>BtnWinClose</c>, which is plain Transparent with
    /// no red hover at all. That windowed button arguably wants the same
    /// treatment, but it is a separate change and is not made here.
    ///
    /// <para>Deliberately not themed: this is Windows' signal for "this closes
    /// the app", and re-deriving it per page would make the most destructive
    /// control the least recognisable. It is also the precedent the whole strip
    /// now follows — see the class remarks.</para></summary>
    private static readonly Color CloseHot = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);

    /// <summary>The strip's ground and its marks, both fixed. Neither value is
    /// invented here: <c>#202020</c> is the ground Windows 11 gives its own dark
    /// caption bar, and <c>#F2F2F2</c> is the light ink Quill's dark themes
    /// already use.
    ///
    /// <para>Measured contrast is <b>14.6:1</b>, against the 12.1:1 floor
    /// §13.1's table guarantees for themed chrome. Dropping theme derivation did
    /// not drop the guarantee with it — it raised it, because a fixed pair
    /// cannot land on the worst case the way a derived one can. On close-hover
    /// the mark swaps to white rather than staying <c>StripInk</c>, so the pair
    /// that matters there is white on <c>#C42B1C</c> at <b>5.7:1</b> — past the
    /// 3:1 non-text needs. The red patch itself reads against the strip ground
    /// at only 2.9:1, which is a state cue rather than content and is the same
    /// margin Windows lives with on its own dark caption bar.</para></summary>
    private static readonly Color StripGround = Color.FromArgb(0xFF, 0x20, 0x20, 0x20);
    private static readonly Color StripInk = Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2);

    private readonly Grid _root;
    private readonly Host _h;
    private readonly Border _strip;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly TranslateTransform _slide = new();

    private bool _active;      // fullscreen
    private bool _shown;       // WANTED state; _t is where the strip actually is
    private double _t;         // 0 = fully retracted, 1 = fully out
    private bool _ticking;
    private long _lastTick;

    public static FullscreenChrome Attach(Grid root, Host h) => new(root, h);

    private FullscreenChrome(Grid root, Host h)
    {
        _root = root;
        _h = h;

        _strip = new Border
        {
            Child = _row,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Height = Metrics.StripHeight,
            CornerRadius = new CornerRadius(0, 0, 0, Metrics.InnerRadius),
            Visibility = Visibility.Collapsed,
            RenderTransform = _slide,
            // A null Background is TRANSPARENT TO HIT-TESTING, which would let
            // clicks fall through to the cluster this strip is covering. It is
            // repainted from PageTheme below; this is only the guarantee that it
            // is never null.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _slide.Y = -Metrics.StripHeight;
        // The strip spans every row: it has to sit over the app's own top bar,
        // not inside the canvas row underneath it.
        Grid.SetRow(_strip, 0);
        Grid.SetRowSpan(_strip, Math.Max(1, root.RowDefinitions.Count));
        // Above TopBar (100) and below LoadingVeil (200).
        Canvas.SetZIndex(_strip, 150);
        root.Children.Add(_strip);

        // The ONLY thing this class puts in the input path while the strip is
        // down. handledEventsToo, because InkSurface marks its pointer events
        // handled; this listener reads and never writes, so a stroke is
        // unaffected either way.
        _root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);
        // The pointer leaving the window stops generating moves, so the
        // geometric test below would never fire again and the strip would be
        // stranded up. This is the only other input this class listens to, and
        // it is on the ROOT, not over the canvas.
        _root.PointerExited += (_, _) => { if (_active) Hide(); };

        PageTheme.Changed += Repaint;
        Build();
    }

    /// <summary>Fullscreen on or off. Off hides the strip at once and stops the
    /// listener doing anything at all.</summary>
    public void SetActive(bool on)
    {
        if (_active == on) return;
        _active = on;
        if (!on) HideNow();
    }

    /// <summary>Rebuilt on a theme change like every other code-built surface —
    /// these capture their colours at build time.</summary>
    public void Repaint()
    {
        _strip.Background = new SolidColorBrush(StripGround);
        _strip.BorderBrush = new SolidColorBrush(PageTheme.Outline);
        // Only the two edges that meet the app get a rule; the other two are
        // flush against the screen. Order is L, T, R, B.
        _strip.BorderThickness = new Thickness(1, 0, 0, 1);
        Build();
    }

    private void Build()
    {
        _row.Children.Clear();
        _row.Children.Add(Mark(Icons.Minus, "Minimise", _h.Minimise, stroked: true));
        // 15.3, first of the four things to get right: the middle mark is EXIT
        // FULLSCREEN. Two arrows pointing inward at each other diagonally, on
        // the same 24 grid as every other mark - not the windowed double-square,
        // and not a font glyph.
        _row.Children.Add(Mark(Icons.FullscreenExit, "Exit full screen", _h.ExitFullscreen));
        _row.Children.Add(Mark(Icons.Close, "Close", _h.Close, stroked: true, close: true));
        // Metrics.StripWidth is MarkPitch * MarkCount, and §17.15's reservation in
        // the top bar is derived from it. If a fourth mark is ever added here and
        // the count is not moved with it, the reservation silently stops covering
        // the strip and the strip starts landing on a live control again.
        Debug.Assert(_row.Children.Count == Metrics.MarkCount,
                     "FullscreenChrome.Metrics.MarkCount must match what Build() adds.");
    }

    private Button Mark(string geometry, string tip, Action click, bool stroked = false, bool close = false)
    {
        var ink = StripInk;
        var art = stroked
            ? Icons.Stroked(geometry, ink, Metrics.GlyphSize, 1.5)
            : Icons.Filled(geometry, ink, Metrics.GlyphSize);
        var cell = new Grid
        {
            Width = Metrics.MarkPitch,
            Height = Metrics.StripHeight,
            // Same reason as the strip's own: a null Background is invisible to
            // the hit test, and this cell IS the target.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        if (art != null)
        {
            art.HorizontalAlignment = HorizontalAlignment.Center;
            art.VerticalAlignment = VerticalAlignment.Center;
            cell.Children.Add(art);
        }
        var b = new Button
        {
            Content = cell,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            // NOT IsHitTestVisible = false anywhere on this subtree: that flag
            // propagates to every descendant, which has silently killed overlays
            // in this codebase before. Only Icons.Mark's own art canvas opts out,
            // and it is a child of the target rather than the target itself.
            // The one place the flag IS used is the strip as a whole, in Apply(),
            // where the propagation is exactly what is wanted.
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => { HideNow(); click(); };
        if (close)
        {
            // Painted on the CELL, not on the Button. A Button's PointerOver
            // VisualState carries a Setter for Background, and a VisualState
            // setter beats a local value while the state is active - so the red
            // would be silently replaced by the theme's hover grey exactly when
            // it is wanted. The cell is the button's content and is drawn above
            // its own background, so it always wins.
            b.PointerEntered += (_, _) =>
            {
                cell.Background = new SolidColorBrush(CloseHot);
                if (art != null) art.Stroke = new SolidColorBrush(Colors.White);
            };
            b.PointerExited += (_, _) =>
            {
                cell.Background = new SolidColorBrush(Colors.Transparent);
                if (art != null) art.Stroke = new SolidColorBrush(StripInk);
            };
        }
        return b;
    }

    // =====================================================================
    // Reveal / hide
    // =====================================================================

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // NOTHING is ever marked handled in here. This is a listener, not a
        // handler: the canvas keeps every event it would otherwise have had.
        if (!_active) return;
        try
        {
            var p = e.GetCurrentPoint(_root).Position;
            if (!_shown)
            {
                // Mid-stroke the strip must not appear under the nib. A stroke
                // that starts below and is dragged up to the top edge keeps
                // drawing.
                if (!e.Pointer.IsInContact && p.Y <= Metrics.RevealBand) Show();
                return;
            }
            // Up already. TWO overlapping ways to stay up, both wider than the
            // band that armed the reveal, so a pointer sitting on either
            // boundary cannot oscillate the strip.
            if (p.Y <= Metrics.KeepBand) return;      // still hugging the top edge
            if (OverStrip(p)) return;                 // on the strip itself
            Hide();
        }
        catch { }
    }

    private bool OverStrip(Point p)
    {
        // Metrics.StripWidth, not a second `MarkPitch * 3`: this fallback, the
        // strip's real width and §17.15's reservation in the top bar are three
        // readings of one number.
        double w = _strip.ActualWidth > 0 ? _strip.ActualWidth : Metrics.StripWidth;
        double left = _root.ActualWidth - w - Metrics.StripSlack;
        return p.X >= left && p.Y <= Metrics.StripHeight + Metrics.StripSlack;
    }

    private void Show()
    {
        if (_shown) return;
        _shown = true;
        Repaint();
        _strip.Visibility = Visibility.Visible;
        if (_h.ReduceMotion()) { _t = 1; Apply(); return; }
        Pump();
    }

    private void Hide()
    {
        if (!_shown) return;
        _shown = false;
        if (_h.ReduceMotion()) { HideNow(); return; }
        Apply();     // inert immediately: nothing on the way out is clickable
        Pump();
    }

    private void HideNow()
    {
        _shown = false;
        StopTick();
        _t = 0;
        Apply();
        _strip.Visibility = Visibility.Collapsed;
    }

    // =====================================================================
    // The slide
    //
    // A hand-pumped tween rather than a Storyboard or a composition animation,
    // for two reasons the brief makes non-negotiable.
    //
    // REVERSIBLE MID-FLIGHT. The pointer routinely leaves before the strip has
    // finished arriving, and that has to turn round from wherever it is rather
    // than snap open and then close. A Storyboard cannot: it holds its end value
    // (FillBehavior.HoldEnd) so direct sets are ignored until it is stopped, and
    // Stop() reverts to the BASE value rather than the current one - so every
    // reversal would jump. Here the position is a pure function of _t, _t simply
    // starts moving the other way from where it is, and the position stays
    // continuous across the turn. One easing curve serves both directions for
    // exactly that reason: two curves would make Ease(_t) discontinuous at the
    // moment of reversal, which is a visible jump.
    //
    // HIT-TESTING TRACKS THE VISUAL. The strip is moved by a XAML
    // RenderTransform, which is part of the element's transform chain and so is
    // part of hit-testing: whatever has visibly arrived is exactly what is
    // clickable, no more and no less. A composition-visual Translation - the
    // mechanism MenuAnim uses for menus - is a render-time transform that
    // hit-testing does not follow, so the buttons would be clickable at their
    // final positions while still visibly sliding. Going the other way, a strip
    // that is on its way OUT is made inert outright, so a click can never land
    // on something that is leaving.
    // =====================================================================

    private void Apply()
    {
        _slide.Y = -Metrics.StripHeight * (1 - Ease(_t));
        // Propagation is the POINT here, unlike everywhere else in this file:
        // one flag makes the whole retracting strip inert.
        _strip.IsHitTestVisible = _shown;
    }

    private void Pump()
    {
        if (_ticking) return;
        _ticking = true;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += Tick;
    }

    private void StopTick()
    {
        if (!_ticking) return;
        _ticking = false;
        CompositionTarget.Rendering -= Tick;
    }

    private void Tick(object? sender, object e)
    {
        double target = _shown ? 1 : 0;
        long now = Stopwatch.GetTimestamp();
        double ms = (now - _lastTick) * 1000.0 / Stopwatch.Frequency;
        _lastTick = now;
        _t = Motion.Step(_t, target, ms, _shown ? Metrics.OpenMs : Metrics.CloseMs);
        Apply();
        if (_t != target) return;
        StopTick();
        if (_t <= 0) _strip.Visibility = Visibility.Collapsed;
    }

    /// <summary>The app's own open curve, now in <see cref="Motion.Ease"/> so the
    /// strip and CONCEPTS-REF 16.7's page fade cannot ease differently.</summary>
    private static double Ease(double t) => Motion.Ease(t);
}
