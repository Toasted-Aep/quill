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
/// rectangle;</item>
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
    }

    /// <summary>Every number this surface is laid out with, in one block, like
    /// <see cref="ChromeBars.Metrics"/> and <see cref="FullscreenChrome.Metrics"/>.</summary>
    public static class Metrics
    {
        /// <summary>Bar marks. 16 DIP inside a 30 DIP cell - the same ratio the
        /// top bar runs (a 16 DIP mark in a 26 DIP box, section 9.6) with a
        /// little more air, because this bar floats over the drawing rather than
        /// sitting in a rule-bounded strip.</summary>
        public const double MarkSize = 16, MarkCell = 30, BarHeight = 34;
        /// <summary>Bottom-row marks are smaller than the bar's: they carry a
        /// word beside them, and a mark that matches its label's cap height
        /// reads as one token rather than as an icon with a caption.</summary>
        public const double RowMarkSize = 15, RowFontSize = 12.5;
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
        /// <summary>Keep both plates this far inside the viewport, so a selection
        /// dragged to an edge does not push its own controls off screen.</summary>
        public const double EdgeInset = 8;
    }

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
    private readonly Border _row;
    private readonly StackPanel _barItems = new() { Orientation = Orientation.Horizontal, Spacing = 0 };
    private readonly StackPanel _rowItems = new() { Orientation = Orientation.Horizontal, Spacing = 10 };

    private bool _shown;

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
        _row = Plate(_rowItems, 0);
        _row.Padding = new Thickness(12, 5, 12, 5);
        _layer.Children.Add(_bar);
        _layer.Children.Add(_row);
        _bar.SizeChanged += (_, _) => Place();
        _row.SizeChanged += (_, _) => Place();

        SelectionState.Changed += Sync;
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
        var guide = PageTheme.WithAlpha(PageTheme.OnSurface, Metrics.GuideAlpha);
        foreach (var g in _guides) g.Fill = new SolidColorBrush(guide);
        foreach (var e in _handles)
        {
            // Hollow: the page's own surface inside, a rule around it. A ring
            // with no fill at all vanishes the moment it lands on dark ink,
            // which at the corner of a bounding box is where it usually lands.
            e.Fill = new SolidColorBrush(PageTheme.Surface);
            e.Stroke = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnSurface, 150));
        }
        foreach (var p in new[] { _bar, _row })
        {
            p.Background = new SolidColorBrush(PageTheme.Panel);
            p.BorderBrush = new SolidColorBrush(PageTheme.Outline);
        }
        Build();
    }

    private void Build()
    {
        var s = SelectionState.Current;
        bool locked = _surface.SelectionLocked;
        bool attach = _surface.SelectedAttachment != null;
        var ink = PageTheme.OnSurface;

        _barItems.Children.Clear();
        // 16.2's order, left to right, and the divider is part of it: the four
        // marks before it act on the object's existence, the two after it act on
        // its orientation.
        _barItems.Children.Add(Mark(Icons.Paperclip, "Replace attachment", attach, _h.ReplaceAttachment, ink,
            deadTip: "Only an attachment has a file to replace"));
        _barItems.Children.Add(Mark(locked ? Icons.LockClosed : Icons.LockOpen,
            locked ? "Unlock" : "Lock", true, _h.ToggleLock, ink));
        _barItems.Children.Add(Mark(Icons.Duplicate, "Duplicate", true, _h.Duplicate, ink));
        _barItems.Children.Add(Mark(Icons.WasteBin, "Delete", !locked, _h.Delete, ink,
            deadTip: "The selection is locked"));
        _barItems.Children.Add(Divider());
        _barItems.Children.Add(Mark(Icons.FlipHorizontal, "Flip horizontal", !locked, () => _h.Flip(true), ink,
            deadTip: "The selection is locked"));
        _barItems.Children.Add(Mark(Icons.FlipVertical, "Flip vertical", !locked, () => _h.Flip(false), ink,
            deadTip: "The selection is locked"));

        _rowItems.Children.Clear();
        _rowItems.Children.Add(Word(Icons.Rotate, "Rotate", !locked, _h.Rotate, ink,
            deadTip: "The selection is locked"));
        // SCALE AND FILTER ARE PRESENT AND DEAD, and that is a decision rather
        // than an omission. 16.2 specifies this row's CONTENT and nowhere in
        // section 16 says what pressing either one does. Scaling already exists
        // as the corner drag, so a second route needs an interaction the
        // reference does not describe; and Quill has no image filters at all, so
        // Filter has nothing to open. Both grey on the same rule the rest of
        // this surface follows - a control the subject cannot act through says
        // so - rather than looking live and doing nothing, which 16.3 calls out
        // by name as the worse of the two.
        _rowItems.Children.Add(Word(Icons.Scale, "Scale", false, () => { }, ink,
            deadTip: "Drag a corner handle to scale"));
        _rowItems.Children.Add(Word(Icons.Filter, "Filter", false, () => { }, ink,
            deadTip: "No filters yet"));

        _ = s;   // the subject drives WHICH marks are live via the flags above
    }

    private FrameworkElement Divider() => new Rectangle
    {
        Width = 1,
        Height = Metrics.DividerHeight,
        Margin = new Thickness(5, 0, 5, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(PageTheme.Outline),
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

    private Button Word(string geometry, string label, bool live, Action click, Color ink, string? deadTip = null)
    {
        var paint = live ? ink : PageTheme.WithAlpha(ink, 70);
        var art = Icons.Mark(geometry, paint, Metrics.RowMarkSize);
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
            FontSize = Metrics.RowFontSize,
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
        };
        // A disabled Button still shows a tooltip in WinUI, which is the point:
        // a control that greys should be able to say why.
        ToolTipService.SetToolTip(b, live ? tip : deadTip ?? tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        if (live) b.Click += (_, _) => click();
        return b;
    }

    // =====================================================================
    // Placement
    // =====================================================================

    /// <summary>Re-ask the host whether this presentation is allowed on screen.
    /// The host owns that answer - <see cref="Host.IsBlocked"/> - so the host is
    /// also what knows when it has changed, and calls this.</summary>
    public void Refresh() => Sync();

    private void Sync()
    {
        bool want = SelectionState.Current.Any && !_h.IsBlocked();
        if (want != _shown)
        {
            _shown = want;
            _layer.Visibility = want ? Visibility.Visible : Visibility.Collapsed;
        }
        if (!want) return;
        Build();     // lock state and subject kind decide which marks are live
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
        bool want = SelectionState.Current.Any && !_h.IsBlocked();
        if (want != _shown) { Sync(); return; }
        Place();
    }

    private void Place()
    {
        if (!_shown) return;
        var w = _surface.SubjectBoundsWorld;
        if (w.IsEmpty) return;

        double vw = _host.ActualWidth, vh = _host.ActualHeight;
        if (vw <= 0 || vh <= 0) return;

        var tl = _surface.WorldToScreen(new Vector2((float)w.Left, (float)w.Top));
        var br = _surface.WorldToScreen(new Vector2((float)w.Right, (float)w.Bottom));
        double x0 = tl.X, y0 = tl.Y, x1 = br.X, y1 = br.Y;
        double cx = (x0 + x1) / 2;

        // THE GUIDES. Full-canvas, projected from the box: verticals at its left
        // and right running the whole viewport height, horizontals at its top and
        // bottom running the whole width. 16.2 is explicit that this is NOT a box
        // on the bounds, so nothing here is clipped to the rectangle.
        Line(_guides[0], x0, 0, Metrics.GuideThickness, vh);
        Line(_guides[1], x1, 0, Metrics.GuideThickness, vh);
        Line(_guides[2], 0, y0, vw, Metrics.GuideThickness);
        Line(_guides[3], 0, y1, vw, Metrics.GuideThickness);

        // THE CORNER CIRCLES, on the box's own corners.
        double r = Metrics.HandleSize / 2;
        Put(_handles[0], x0 - r, y0 - r);
        Put(_handles[1], x1 - r, y0 - r);
        Put(_handles[2], x1 - r, y1 - r);
        Put(_handles[3], x0 - r, y1 - r);

        // THE BAR, centred above; THE ROW, centred below. Both clamped inside the
        // viewport, because a selection dragged against an edge must not push its
        // own controls off screen - the alternative is a bar the user can see the
        // edge of and cannot reach.
        double bw = _bar.ActualWidth > 0 ? _bar.ActualWidth : _bar.DesiredSize.Width;
        double bh = _bar.ActualHeight > 0 ? _bar.ActualHeight : Metrics.BarHeight;
        Put(_bar, Clamp(cx - bw / 2, vw - bw), Clamp(y0 - Metrics.Gap - bh, vh - bh));

        double rw = _row.ActualWidth > 0 ? _row.ActualWidth : _row.DesiredSize.Width;
        double rh = _row.ActualHeight > 0 ? _row.ActualHeight : 30;
        Put(_row, Clamp(cx - rw / 2, vw - rw), Clamp(y1 + Metrics.Gap, vh - rh));
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
