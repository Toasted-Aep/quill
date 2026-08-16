using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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
/// <c>Handled</c>. Nothing is added to the hit-test path until the strip is
/// actually up, and while it is down its <c>Visibility</c> is Collapsed, which
/// takes it out of hit-testing entirely.</para>
///
/// <para>The reveal is additionally gated on <c>!Pointer.IsInContact</c>: a
/// stroke that starts lower down and travels up to the top edge must not pop a
/// strip out under the nib mid-stroke.</para>
///
/// <para><b>Colour tracks the page, it is not hardcoded dark.</b> 15.3 asks for
/// that decision to be made deliberately: the capture reads as an opaque dark
/// slab, but the capture is of a white page, so it cannot settle the question.
/// §0 does — every surface in the shell derives from the page ground and new
/// chrome may not invent a second theme source. The strip therefore takes
/// <see cref="PageTheme.Panel"/>, which is the token §6/§13 assign to opaque
/// floating chrome (Settings / Export / Brushes / Objects) rather than
/// <c>Surface</c>, which is a raised TINT of the page and would let the cluster
/// underneath show through — and this strip's whole job is to occlude that
/// cluster. §13.1's table guarantees Panel against OnSurface at no worse than
/// 12.1:1 on every ground, so the three marks stay legible on paper, on
/// Blueprint and on Darkprint alike. On a near-white page it comes out light
/// rather than dark; that is the theme contract disagreeing with one screenshot
/// of one page, and the contract wins.</para>
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

    /// <summary>Every number the strip is laid out with, in one block, like
    /// <see cref="ChromeBars.Metrics"/>.</summary>
    public static class Metrics
    {
        /// <summary>DIPs of the screen top that arm the reveal. A few — enough
        /// that a mouse flung at the edge (which the OS clamps to y = 0) always
        /// lands in it, small enough that it is not a band the user crosses by
        /// accident on the way somewhere else.</summary>
        public const double RevealBand = 4;
        /// <summary>Caption-bar proportions: the same 32-ish band Windows uses,
        /// so the strip reads as a title bar rather than as a floating card.</summary>
        public const double StripHeight = 34;
        /// <summary>Hit target per mark. Wider than tall, again like a caption
        /// button rather than like the bars' 42 DIP square slots.</summary>
        public const double MarkPitch = 46;
        public const double GlyphSize = 15;
        /// <summary>Slide distance and duration. The strip comes DOWN out of the
        /// screen edge, so it travels its own height.</summary>
        public const double SlideMs = 130, HideMs = 110;
        /// <summary>Bottom-left corner only: the strip is flush against the top
        /// and right edges of the screen and only its inner corner is free.</summary>
        public const double InnerRadius = 10;
    }

    /// <summary>Close goes red on hover — the one caption affordance users
    /// expect, and the same <c>#C42B1C</c> the windowed caption button in
    /// MainWindow already uses. Deliberately NOT themed: this is Windows' own
    /// signal for "this closes the app", and re-deriving it per page would make
    /// the most destructive control the least recognisable.</summary>
    private static readonly Color CloseHot = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);

    private readonly Grid _root;
    private readonly Host _h;
    private readonly Border _strip;
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly TranslateTransform _slide = new();

    private Storyboard? _sb;
    private bool _active;      // fullscreen
    private bool _shown;

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
        // Leaving the strip is what hides it (15.3). Belt and braces alongside
        // the geometric test in OnRootPointerMoved, which covers the pointer
        // that leaves without ever generating a move inside the strip.
        _strip.PointerExited += (_, _) => Hide();

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
        _strip.Background = new SolidColorBrush(PageTheme.Panel);
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
    }

    private Button Mark(string geometry, string tip, Action click, bool stroked = false, bool close = false)
    {
        var ink = PageTheme.OnSurface;
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
                if (art != null) art.Stroke = new SolidColorBrush(PageTheme.OnSurface);
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
            // Mid-stroke the strip must not appear under the nib. A stroke that
            // starts below and is dragged up to the top edge keeps drawing.
            bool drawing = e.Pointer.IsInContact;
            if (!drawing && p.Y <= Metrics.RevealBand) { Show(); return; }
            if (_shown && !OverStrip(p)) Hide();
        }
        catch { }
    }

    private bool OverStrip(Point p)
    {
        if (_strip.ActualWidth <= 0) return false;
        double right = _root.ActualWidth;
        double left = right - _strip.ActualWidth;
        return p.X >= left - 1 && p.X <= right && p.Y >= 0 && p.Y <= Metrics.StripHeight;
    }

    private void Show()
    {
        if (_shown) return;
        _shown = true;
        Repaint();
        _strip.Visibility = Visibility.Visible;
        if (_h.ReduceMotion()) { _slide.Y = 0; return; }
        Animate(-Metrics.StripHeight, 0, Metrics.SlideMs, collapse: false);
    }

    private void Hide()
    {
        if (!_shown) return;
        _shown = false;
        if (_h.ReduceMotion()) { HideNow(); return; }
        Animate(_slide.Y, -Metrics.StripHeight, Metrics.HideMs, collapse: true);
    }

    private void HideNow()
    {
        _shown = false;
        // A finished Storyboard HOLDS its end value (FillBehavior.HoldEnd is the
        // default) and keeps overriding direct sets until it is stopped. Without
        // this Stop the slide would be stuck wherever the last animation left it
        // and the strip would never come back.
        try { _sb?.Stop(); } catch { }
        _strip.Visibility = Visibility.Collapsed;
        _slide.Y = -Metrics.StripHeight;
    }

    private void Animate(double from, double to, double ms, bool collapse)
    {
        try
        {
            try { _sb?.Stop(); } catch { }
            _slide.Y = from;
            var a = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            // The canonical form: target the ELEMENT and walk to the transform's
            // property. Targeting the TranslateTransform directly resolves in
            // some hosts and silently no-ops in others.
            Storyboard.SetTarget(a, _strip);
            Storyboard.SetTargetProperty(a, "(UIElement.RenderTransform).(TranslateTransform.Y)");
            var sb = new Storyboard();
            sb.Children.Add(a);
            if (collapse)
                sb.Completed += (_, _) => { if (!_shown) _strip.Visibility = Visibility.Collapsed; };
            _sb = sb;
            sb.Begin();
        }
        catch
        {
            _slide.Y = to;
            if (collapse && !_shown) _strip.Visibility = Visibility.Collapsed;
        }
    }
}
