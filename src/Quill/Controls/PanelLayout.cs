using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;

namespace Quill.Controls;

/// <summary>
/// THE ONE PLACE PANELS AGREE ABOUT SPACE (UI-SPEC-V3 K.21 — the user's starred
/// request: "every panel moves out of the way dynamically when something
/// overlaps it").
///
/// <para><b>What it is.</b> A registry plus a small deterministic solver. Every
/// surface that occupies canvas space registers its element once. Some are
/// <i>obstacles</i> — the two status-bar clusters, the radial dial, the docked
/// settings panel — which are never moved because something else owns their
/// placement. The rest are <i>movable</i>: the Notebooks window and the bare
/// Layers / Precision / Objects / Comments panes. When anything is shown,
/// hidden, resized or dragged, <see cref="Invalidate"/> reflows the movable set
/// so no two of them, and none of them and an obstacle, overlap.</para>
///
/// <para><b>The solver.</b> Deliberately NOT a physics simulation. Each movable
/// panel is placed in registration order, always starting from its own HOME
/// position (its corner region, or wherever the user last dragged it — a drag
/// re-homes it, so the layout never fights the user). If the home rectangle is
/// clear it stays there. If it is not, the solver generates at most four
/// candidates per blocker — flush right of it, flush left, flush below, flush
/// above — clamps each into the host, and takes the nearest one to home that is
/// clear. If no single move clears everything it tries one more level, and if
/// that fails too it leaves the panel where it is rather than hiding it or
/// pushing it off-screen. With six participants that is a few dozen rectangle
/// intersections: microseconds, and the same input always yields the same
/// layout.</para>
///
/// <para><b>The animation</b> is a 170 ms slide, done with a
/// <see cref="TranslateTransform"/> from the OLD position to zero after the new
/// margin is already committed — so the layout is correct on the very first
/// frame and a storyboard that fails to run can never strand a panel in the
/// wrong place. Reduce-motion skips it entirely.</para>
/// </summary>
public sealed class PanelLayout
{
    /// <summary>Which corner a movable panel calls home. The solver always
    /// prefers to leave a panel in its own region.</summary>
    public enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>Breathing room left between two rectangles, in DIPs.</summary>
    private const double Gap = 10;
    /// <summary>Overlaps smaller than this are not worth moving for (a hairline
    /// of antialiasing, a 1 DIP divider).</summary>
    private const double Slack = 2;
    private const double EdgeMargin = 14;

    private sealed class Entry
    {
        public required string Id { get; init; }
        /// <summary>Null for a VIRTUAL participant — one that reports its own
        /// rectangle instead of being an element in the host's tree.</summary>
        public FrameworkElement? El { get; init; }
        /// <summary>A virtual participant's bounds, in host coordinates, or null
        /// when it is not on screen.</summary>
        public Func<Rect?>? Bounds { get; init; }
        public Anchor Home { get; init; }
        public bool Movable { get; init; }
        public int Order { get; init; }
        /// <summary>Where the user dragged it, in host coordinates, or null for
        /// "use the corner". A drag re-homes the panel permanently, so the
        /// solver never drags it back.</summary>
        public Point? Pinned { get; set; }
        public double LastX, LastY;
        public bool Placed;
    }

    private readonly Panel _host;
    private readonly List<Entry> _entries = new();
    private readonly Func<bool> _reduceMotion;
    private bool _pending;
    private bool _reflowing;
    private readonly List<Storyboard> _anims = new();

    public PanelLayout(Panel host, Func<bool> reduceMotion)
    {
        _host = host;
        _reduceMotion = reduceMotion;
        _host.SizeChanged += (_, _) => Invalidate();
    }

    /// <summary>Registers a surface. <paramref name="movable"/> false means the
    /// solver reads its bounds and routes around it but never touches it —
    /// correct for anything whose position another class owns (the dial parks
    /// itself off <c>TopInset</c>; the settings panel is docked to the right
    /// edge; the two clusters are pinned to the measured margins).</summary>
    public void Register(string id, FrameworkElement el, Anchor home = Anchor.TopLeft,
                         bool movable = false, int order = 0)
    {
        if (_entries.Any(e => e.Id == id)) return;
        var entry = new Entry { Id = id, El = el, Home = home, Movable = movable, Order = order };
        _entries.Add(entry);
        // A panel that grows (a longer notebook tree, a second layer row) has to
        // re-check its neighbours, but only when the SIZE actually changed -
        // moving an element also raises SizeChanged in some templates.
        el.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5 &&
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5) return;
            Invalidate();
        };
        // Showing or hiding a participant changes the free space for everyone
        // else, and Visibility raises no event of its own. Watching the property
        // here means no caller ever has to remember to reflow: the sidebar
        // toggle, the dial's own show/hide and the settings dock all just work.
        try { el.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Invalidate()); }
        catch { }
        Invalidate();
    }

    /// <summary>Registers an OBSTACLE that is not an element of the host — a
    /// window living in the XamlRoot's popup layer, which is where the Objects
    /// library and the export pane are composited. A popup's child cannot be
    /// transformed into the host's coordinate space reliably (a windowed popup
    /// is not in that visual tree at all), so it reports its own rectangle
    /// instead and the solver treats it exactly like any other obstacle.</summary>
    public void RegisterRect(string id, Func<Rect?> bounds)
    {
        if (_entries.Any(e => e.Id == id)) return;
        _entries.Add(new Entry { Id = id, Bounds = bounds, Movable = false });
        Invalidate();
    }

    /// <summary>Registers a surface this class cannot be handed a reference to,
    /// by finding it under <paramref name="root"/> by automation id. The radial
    /// dial is the case that needs it: <c>ToolWheel</c> owns its own placement
    /// and exposes no element, but it does tag its disc "ToolWheel", so the
    /// solver can read its bounds and route the Notebooks window around it
    /// without anything reaching into that file.</summary>
    public bool RegisterByAutomationId(string id, DependencyObject root, string automationId,
                                       Anchor home = Anchor.TopLeft, bool movable = false, int order = 0)
    {
        var el = Find(root, automationId);
        if (el == null) return false;
        Register(id, el, home, movable, order);
        return true;
    }

    private static FrameworkElement? Find(DependencyObject root, string automationId)
    {
        try
        {
            if (root is FrameworkElement fe &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(fe) == automationId)
                return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var hit = Find(VisualTreeHelper.GetChild(root, i), automationId);
                if (hit != null) return hit;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Re-homes a panel to where the user just dropped it, in host
    /// coordinates. From then on that is the position the solver defends.</summary>
    public void Pin(string id, Point where)
    {
        var e = _entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return;
        e.Pinned = where;
        Invalidate();
    }

    /// <summary>Forgets a drag, so the panel returns to its corner.</summary>
    public void Unpin(string id)
    {
        var e = _entries.FirstOrDefault(x => x.Id == id);
        if (e == null) return;
        e.Pinned = null;
        Invalidate();
    }

    /// <summary>Schedules one reflow. Safe to call from anywhere and as often as
    /// you like: the calls coalesce into a single pass on the next tick, after
    /// layout has settled, so a panel that was shown this frame is measured.</summary>
    public void Invalidate()
    {
        if (_pending || _reflowing) return;
        _pending = true;
        try
        {
            if (!_host.DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Reflow))
                _pending = false;
        }
        catch { _pending = false; }
    }

    // =====================================================================
    // The pass
    // =====================================================================
    private void Reflow()
    {
        _pending = false;
        if (_reflowing) return;
        _reflowing = true;
        try { ReflowCore(); }
        catch { }
        finally { _reflowing = false; }
    }

    private void ReflowCore()
    {
        double hostW = _host.ActualWidth, hostH = _host.ActualHeight;
        if (hostW < 80 || hostH < 80) return;

        var taken = new List<Rect>();

        // Obstacles first: they own their own placement, so everything else
        // routes around them.
        foreach (var e in _entries.Where(x => !x.Movable))
        {
            var r = RectOf(e);
            if (r != null) taken.Add(r.Value);
        }

        foreach (var e in _entries.Where(x => x.Movable).OrderBy(x => x.Order))
        {
            if (e.El == null || !IsShown(e.El)) { e.Placed = false; continue; }
            var size = SizeOf(e.El);
            if (size.Width < 4 || size.Height < 4) continue;

            var home = HomePoint(e, size, hostW, hostH);
            var want = new Rect(home.X, home.Y, size.Width, size.Height);
            var placed = Solve(want, taken, hostW, hostH);
            MoveTo(e, placed.X, placed.Y);
            taken.Add(placed);
        }
    }

    /// <summary>The nearest clear rectangle to <paramref name="want"/>. Returns
    /// <paramref name="want"/> itself when it is already clear, and also when
    /// nothing clear could be found — a panel left overlapping is a great deal
    /// better than a panel flung off the canvas.</summary>
    private static Rect Solve(Rect want, List<Rect> taken, double hostW, double hostH)
    {
        if (!Hits(want, taken)) return want;

        var first = Candidates(want, taken, hostW, hostH);
        foreach (var c in first)
            if (!Hits(c, taken)) return c;

        // One more level: a candidate that cleared its blocker but landed on a
        // second one usually only needs a single further nudge (the classic case
        // is the Notebooks window clearing the dial and then meeting the status
        // bar above it).
        foreach (var c in first)
            foreach (var d in Candidates(c, taken, hostW, hostH))
                if (!Hits(d, taken)) return d;

        return want;
    }

    private static List<Rect> Candidates(Rect r, List<Rect> taken, double hostW, double hostH)
    {
        var outp = new List<Rect>(taken.Count * 4);
        foreach (var o in taken)
        {
            if (!Overlaps(r, o)) continue;
            Add(o.Right + Gap, r.Y);
            Add(o.Left - r.Width - Gap, r.Y);
            Add(r.X, o.Bottom + Gap);
            Add(r.X, o.Top - r.Height - Gap);
        }
        // nearest to where it wanted to be, first
        outp.Sort((a, b) => Dist2(a, r).CompareTo(Dist2(b, r)));
        return outp;

        void Add(double x, double y)
        {
            // Never off the canvas: a panel the user cannot reach is worse than
            // one that overlaps.
            if (x < EdgeMargin - 0.5 || y < EdgeMargin - 0.5) return;
            if (x + r.Width > hostW - EdgeMargin + 0.5) return;
            if (y + r.Height > hostH - EdgeMargin + 0.5) return;
            outp.Add(new Rect(x, y, r.Width, r.Height));
        }
    }

    private static double Dist2(Rect a, Rect b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static bool Hits(Rect r, List<Rect> taken)
    {
        foreach (var o in taken) if (Overlaps(r, o)) return true;
        return false;
    }

    private static bool Overlaps(Rect a, Rect b) =>
        a.Left < b.Right - Slack && b.Left < a.Right - Slack &&
        a.Top < b.Bottom - Slack && b.Top < a.Bottom - Slack;

    // =====================================================================
    // Reading and writing positions
    // =====================================================================
    private static bool IsShown(FrameworkElement el)
    {
        try
        {
            if (el.Visibility != Visibility.Visible) return false;
            for (DependencyObject? d = el; d != null; d = VisualTreeHelper.GetParent(d))
                if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
            return el.ActualWidth > 1 && el.ActualHeight > 1;
        }
        catch { return false; }
    }

    private Rect? RectOf(Entry e)
    {
        try
        {
            if (e.El == null) return e.Bounds?.Invoke();
            if (!IsShown(e.El)) return null;
            var p = e.El.TransformToVisual(_host).TransformPoint(new Point(0, 0));
            return new Rect(p.X, p.Y, e.El.ActualWidth, e.El.ActualHeight);
        }
        catch { return null; }
    }

    private static Size SizeOf(FrameworkElement el) => new(el.ActualWidth, el.ActualHeight);

    private Point HomePoint(Entry e, Size size, double hostW, double hostH)
    {
        if (e.Pinned is { } p)
            return new Point(Math.Clamp(p.X, EdgeMargin, Math.Max(EdgeMargin, hostW - size.Width - EdgeMargin)),
                             Math.Clamp(p.Y, EdgeMargin, Math.Max(EdgeMargin, hostH - size.Height - EdgeMargin)));
        return e.Home switch
        {
            Anchor.TopRight => new Point(Math.Max(EdgeMargin, hostW - size.Width - EdgeMargin), EdgeMargin),
            Anchor.BottomLeft => new Point(EdgeMargin, Math.Max(EdgeMargin, hostH - size.Height - EdgeMargin)),
            Anchor.BottomRight => new Point(Math.Max(EdgeMargin, hostW - size.Width - EdgeMargin),
                                            Math.Max(EdgeMargin, hostH - size.Height - EdgeMargin)),
            _ => new Point(EdgeMargin, EdgeMargin),
        };
    }

    /// <summary>Commits the position as a margin (every movable panel is
    /// Left/Top aligned, which is what makes one number mean one thing) and then
    /// animates the OLD offset away.</summary>
    private void MoveTo(Entry e, double x, double y)
    {
        // Only movable entries reach here, and a movable entry always has an
        // element — a virtual participant reports a rectangle and is never moved.
        if (e.El is not { } el) return;

        double oldX = e.Placed ? e.LastX : x, oldY = e.Placed ? e.LastY : y;
        bool moved = e.Placed && (Math.Abs(oldX - x) > 0.5 || Math.Abs(oldY - y) > 0.5);

        el.HorizontalAlignment = HorizontalAlignment.Left;
        el.VerticalAlignment = VerticalAlignment.Top;
        el.Margin = new Thickness(x, y, 0, 0);
        e.LastX = x;
        e.LastY = y;
        e.Placed = true;

        if (!moved) return;
        bool still = false;
        try { still = _reduceMotion(); } catch { }
        if (still) return;

        try
        {
            var slide = el.RenderTransform as TranslateTransform;
            if (slide == null)
            {
                slide = new TranslateTransform();
                el.RenderTransform = slide;
            }
            slide.X = oldX - x;
            slide.Y = oldY - y;

            var sb = new Storyboard();
            sb.Children.Add(Leg(slide, "X", slide.X));
            sb.Children.Add(Leg(slide, "Y", slide.Y));
            sb.Completed += (_, _) => { slide.X = 0; slide.Y = 0; _anims.Remove(sb); };
            // held: an unrooted storyboard can be collected mid-run
            _anims.Add(sb);
            sb.Begin();
        }
        catch
        {
            if (el.RenderTransform is TranslateTransform t) { t.X = 0; t.Y = 0; }
        }

        static DoubleAnimation Leg(TranslateTransform t, string prop, double from)
        {
            var a = new DoubleAnimation
            {
                From = from,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(a, t);
            Storyboard.SetTargetProperty(a, prop);
            return a;
        }
    }
}
