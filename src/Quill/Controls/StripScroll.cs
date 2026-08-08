using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Quill.Controls;

/// <summary>
/// The one implementation of a HORIZONTALLY SCROLLING STRIP, shared by every
/// surface that has one: the settings swatch and unit rows, the Brushes panel's
/// Basics and Tools strips, and the Objects library's per-pack bands.
///
/// <para><b>Why it exists.</b> CONCEPTS-REF-2026-08-07 §10.5 item 25 — "a
/// vertical mouse wheel over a horizontally-scrolling strip must scroll it
/// horizontally ... swatches, units, brushes, objects" — is one behaviour asked
/// for on four surfaces, and item 24's "Objects library glitches when scrolled
/// sideways" is a second. Both are properties of the STRIP, not of any one
/// panel, so they are fixed once here rather than four times badly.</para>
///
/// <para><b>What a bare ScrollViewer gets wrong.</b> Three things, all of which
/// bite only when the strip is nested inside the floating window's own vertical
/// scroller, which is exactly where all four of these live:</para>
/// <list type="number">
/// <item><b>The wheel does nothing.</b> WinUI maps a plain wheel to the VERTICAL
/// axis. On a scroller whose vertical axis is disabled the event is simply
/// passed to the parent, so the page scrolls away underneath a strip the user
/// was trying to move. The handler below re-aims it — and only claims the event
/// while the strip actually has somewhere to go, so a strip that fits keeps
/// scrolling the panel behind it.</item>
/// <item><b>Scroll chaining.</b> With chaining left on, a horizontal fling that
/// reaches the end of the strip is handed to the ancestor scroller, which lurches
/// the whole panel sideways-then-vertically. Horizontal chaining is turned off;
/// vertical chaining stays ON, so a vertical drag STARTED on the strip still
/// scrolls the panel, which is what a reader expects.</item>
/// <item><b>No rail.</b> Without one, a drag a few degrees off the horizontal is
/// read as a two-axis manipulation and the strip judders.</item>
/// </list>
/// </summary>
internal static class StripScroll
{
    /// <summary>Wheel notches are 120 units; a notch should move a strip by about
    /// one cell rather than by one pixel or by a whole viewport.</summary>
    private const double PerNotch = 88;

    /// <summary>A horizontal strip carrying <paramref name="content"/>.</summary>
    public static ScrollViewer Horizontal(UIElement content)
    {
        var sv = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Attach(sv);
        return sv;
    }

    /// <summary>Gives an existing scroller the strip behaviour. Idempotent in the
    /// only sense that matters — calling it twice would double the wheel step, so
    /// callers hand it a scroller they own.</summary>
    public static void Attach(ScrollViewer sv)
    {
        sv.IsHorizontalRailEnabled = true;
        sv.IsVerticalRailEnabled = true;
        sv.IsHorizontalScrollChainingEnabled = false;
        sv.IsVerticalScrollChainingEnabled = true;

        // handledEventsToo: the ScrollViewer template marks the wheel handled on
        // its way past in some states, and a handler that never runs is worse
        // than no handler at all because it looks wired up.
        sv.AddHandler(UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((s, e) => OnWheel(sv, e)),
            handledEventsToo: true);
    }

    private static void OnWheel(ScrollViewer sv, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        try
        {
            double span = sv.ExtentWidth - sv.ViewportWidth;
            if (span <= 0.5) return;                 // nothing to scroll: let the panel have it

            var p = e.GetCurrentPoint(sv).Properties;
            // A horizontal (tilt / trackpad) wheel already means sideways, so it
            // wins outright; otherwise the vertical wheel is re-aimed.
            double notches = p.MouseWheelDelta / 120.0;
            bool horizontal = p.IsHorizontalMouseWheel;
            double target = sv.HorizontalOffset + (horizontal ? notches : -notches) * PerNotch;
            target = System.Math.Clamp(target, 0, span);
            if (System.Math.Abs(target - sv.HorizontalOffset) < 0.5) return;

            sv.ChangeView(target, null, null, false);
            e.Handled = true;
        }
        catch { }
    }

    /// <summary>A bare, accessible hit target: a Button stripped of every visual
    /// a Button normally brings, so it still reads as the plain circle / cell the
    /// reference draws.
    ///
    /// <para><b>Why a Button and not a Tapped handler.</b> The option circles in
    /// Settings and the brush cells in the Brushes panel are the panels' primary
    /// controls, and as bare StackPanels with Tapped handlers they had no
    /// keyboard focus, no invoke pattern and no accessible role — unreachable for
    /// a screen reader, and impossible to drive from a test. A Button also gets
    /// the drag-safety for free: when the strip beneath it starts a manipulation
    /// the ScrollViewer takes the pointer capture, the Button loses it, and no
    /// Click is raised — which is the behaviour <see cref="Tap"/> has to
    /// hand-roll for elements that are not Buttons.</para></summary>
    public static Button Bare(UIElement content, string name, Action click, bool enabled = true)
    {
        var b = new Button
        {
            Content = content,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = enabled,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => { try { click(); } catch { } };
        return b;
    }

    /// <summary>A tap that a SCROLL cannot trigger.
    ///
    /// <para>The strips are dragged sideways with a finger or a pen, and a
    /// <c>Tapped</c> handler on a cell inside one fires at the end of a slow drag
    /// — which in the Objects library means a shape lands on the page because the
    /// user scrolled the row (§10.5 item 24). Pointer press/release with a
    /// movement threshold cannot: past the slop it is a scroll and nothing
    /// else.</para></summary>
    public static void Tap(FrameworkElement el, Action action, double slop = 8)
    {
        Point at = default;
        bool armed = false;
        el.PointerPressed += (s, e) =>
        {
            at = e.GetCurrentPoint((UIElement)s).Position;
            armed = true;
        };
        el.PointerCanceled += (_, _) => armed = false;
        el.PointerCaptureLost += (_, _) => armed = false;
        el.PointerReleased += (s, e) =>
        {
            if (!armed) return;
            armed = false;
            var p = e.GetCurrentPoint((UIElement)s).Position;
            double dx = p.X - at.X, dy = p.Y - at.Y;
            if (dx * dx + dy * dy > slop * slop) return;
            try { action(); } catch { }
        };
    }
}
