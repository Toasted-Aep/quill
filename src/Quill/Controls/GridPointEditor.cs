using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// CONCEPTS-REF §12.6 as corrected by §12.8 — the on-canvas vanishing-point
/// editor.
///
/// <para><b>The horizon tilts.</b> §12.6 implied the points slide along a level
/// horizontal; §12.8's mid-drag capture shows they do not. The horizon IS the
/// line through the two on-horizon points, so dragging either one rotates it and
/// the whole grid re-solves under the new geometry — live, on every pointer
/// move, not on release. A pale blue dashed line stays at the level position so
/// the amount of tilt is readable at a glance.</para>
///
/// <para><b>An overlay, not a mode inside <see cref="InkSurface"/>.</b> The
/// surface is seven thousand lines of drawing and pointer routing that every
/// stroke in the app goes through. Here the handles are real elements with real
/// hit-testing, real pointer capture and a real four-way cursor.</para>
///
/// <para><b>Two things that made the overlay dead to input, both fixed here and
/// both worth naming.</b> First, <c>IsHitTestVisible = false</c> on the root: it
/// looks like the way to say "let presses through to the page", and it
/// propagates to the ENTIRE subtree, so no handle inside it can ever be pressed.
/// A null <c>Background</c> says the same thing correctly — the panel itself is
/// not a hit target while its children still are, and no scrim is painted over
/// the page. Second, rebuilding the handles on every change: <see cref="Sync"/>
/// used to recreate them, which destroys the element holding the pointer capture
/// halfway through a drag. The handles are now built ONCE and only moved.</para>
///
/// <para>The fade is §11.19's, reached through
/// <see cref="ColorPickerService.SetExternalDimming"/> rather than
/// reimplemented, so nothing here touches ChromeBars or ChromeUi.</para>
/// </summary>
public sealed class GridPointEditor
{
    public sealed class Host
    {
        public required InkSurface Surface { get; init; }
        public required Func<NotePage?> Page { get; init; }
        /// <summary>Repaint the canvas — the guides themselves are drawn by the
        /// surface, from the same model this edits.</summary>
        public required Action Changed { get; init; }
        public required Action Save { get; init; }

        /// <summary>Registered chrome by id, for §12.8's tilt readout. READ only:
        /// the top bar belongs to ChromeBars and this class does not modify that
        /// file — it borrows two cells for the duration of the mode and hands
        /// them straight back.</summary>
        public Func<string, FrameworkElement?>? Chrome { get; init; }

        /// <summary>Let the chrome repaint its own readouts on the way out.</summary>
        public Action? SyncChrome { get; init; }
    }

    // §12.6's sizes.
    private const double RingD = 16;      // the vanishing-point ring
    private const double HitD = 44;       // its grab area
    private const double CrossD = 22;     // the centre crosshair

    /// <summary>§12.6's red. A literal instruction — the horizon and the rings
    /// turn RED while editing — and the one colour on this surface that is
    /// deliberately not derived from the page.</summary>
    private static readonly Color EditRed = Color.FromArgb(0xFF, 0xE0, 0x32, 0x2C);

    /// <summary>§12.8's pale blue: the cone of vision and the level-horizon
    /// dashed line. Both belong to the GRID rather than to the controls, which is
    /// the whole reason they are not red.</summary>
    private static readonly Color PaleBlue = Color.FromArgb(0xB4, 0x6E, 0x9A, 0xC8);

    /// <summary>Below this the horizon counts as level and §12.8's dashed
    /// reference line is not drawn — it exists to show a tilt, and a dashed line
    /// exactly under the red one is just a second horizon.</summary>
    private const double LevelEpsilonDeg = 0.05;

    /// <summary>12.9: the rotation arc spans "roughly plus or minus 30 degrees
    /// about the horizontal" on the circle's RIGHT side - measured about the
    /// horizon, so the grip turns with what it turns.</summary>
    private const double GripSpanDeg = 30;
    /// <summary>How wide the arc is to the pointer. The mark stays 3 DIP.</summary>
    private const double GripGrab = 26;

    private readonly Host _h;
    private readonly Grid _root;
    private readonly Canvas _layer;
    private readonly Border _bar;
    private readonly TextBlock _barLabel;
    private readonly Line _horizon;
    private readonly Line _level;
    private readonly Ellipse _cone;
    /// <summary>12.9's rotation grip: a red arc on the cone circle's rim. Two
    /// paths with the same geometry - a wide transparent one underneath that is
    /// the grab area, and the thin red one you actually see.</summary>
    private readonly ArcGrip _gripHit;
    private readonly Microsoft.UI.Xaml.Shapes.Path _grip;

    /// <summary>Built ONCE per session and only ever moved. Recreating a handle
    /// mid-drag destroys the element that holds the pointer capture, which is
    /// what made dragging drop half its movement.</summary>
    private readonly List<Handle> _rings = new();
    private Handle? _cross;
    private int _builtFor = -1;      // the VP count the handles were built for

    public bool IsActive { get; private set; }

    /// <summary>Raised after <see cref="End"/>, so the caller can put back
    /// whatever it dismissed.</summary>
    public Action? Closed { get; set; }

    public static GridPointEditor Attach(Panel host, Host h) => new(host, h);

    private GridPointEditor(Panel host, Host h)
    {
        _h = h;

        // No Background on either: a null background is already invisible to hit
        // testing, so presses land on the canvas, the dial and the bars exactly
        // as they did before — while the handles on top keep theirs. No scrim is
        // painted over the page (§11.19).
        _layer = new Canvas();

        _horizon = new Line
        {
            Stroke = new SolidColorBrush(EditRed),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        _level = new Line
        {
            Stroke = new SolidColorBrush(PaleBlue),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 6, 5 },
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _cone = new Ellipse
        {
            Stroke = new SolidColorBrush(PaleBlue),
            StrokeThickness = 1.2,
            Fill = null,
            IsHitTestVisible = false,
        };
        _gripHit = new ArcGrip
        {
            // A TRANSPARENT stroke, not a null one: null is invisible to hit
            // testing, transparent is a real target. This is the grab area and
            // it is deliberately far wider than the mark it carries.
            Stroke = new SolidColorBrush(Colors.Transparent),
            StrokeThickness = GripGrab,
            Fill = null,
        };
        _grip = new Microsoft.UI.Xaml.Shapes.Path
        {
            Stroke = new SolidColorBrush(EditRed),
            StrokeThickness = 3,
            Fill = null,
            IsHitTestVisible = false,
        };

        (_bar, _barLabel) = BuildBar();

        _root = new Grid
        {
            Background = null,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_layer);
        _root.Children.Add(_bar);
        // Above the dial (60), the panes (65) and the status clusters (70): a
        // modal editing mode whose handles sit under another layer is a mode
        // nobody can use. It costs the chrome nothing, because everything here
        // except the handles themselves is transparent to hit testing.
        Canvas.SetZIndex(_root, 95);
        host.Children.Add(_root);

        _h.Surface.ViewChanged += OnViewChanged;
        _root.SizeChanged += (_, _) => Sync();
    }

    // =======================================================================
    // Mode
    // =======================================================================
    public void Begin()
    {
        if (IsActive) return;
        var def = _h.Page()?.Perspective;
        if (def == null || def.Vps.Count == 0) return;

        IsActive = true;
        _root.Visibility = Visibility.Visible;
        _barLabel.Foreground = new SolidColorBrush(PageTheme.OnSurface);
        _bar.Background = new SolidColorBrush(PageTheme.Panel);
        // §12.6: the radial dial fades exactly as it does for the colour wheel,
        // while the page and the grid stay at full strength. Reusing the picker's
        // own state means one mechanism, not two.
        try { ColorPickerService.SetExternalDimming(true); } catch { }
        Build(def);
        Sync();
    }

    public void End()
    {
        if (!IsActive) return;
        IsActive = false;
        _root.Visibility = Visibility.Collapsed;
        try { ColorPickerService.SetExternalDimming(false); } catch { }
        RestoreReadout();
        try { _h.Save(); } catch { }
        Closed?.Invoke();
    }

    private void OnViewChanged()
    {
        if (IsActive) Sync();
    }

    // =======================================================================
    // Build — once per session, or when the point count changes
    // =======================================================================
    private void Build(PerspectiveDef def)
    {
        _layer.Children.Clear();
        _rings.Clear();
        _cross = null;

        _layer.Children.Add(_cone);
        _layer.Children.Add(_level);
        _layer.Children.Add(_horizon);
        _layer.Children.Add(_gripHit);
        _layer.Children.Add(_grip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_gripHit, "Rotate horizon");
        DragAbs(_gripHit, null, RotateTo, null);

        int n = Math.Min(def.Vps.Count, 3);
        for (int i = 0; i < n; i++)
        {
            int idx = i;
            var ring = MakeRing();
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ring, $"Vanishing point {idx + 1}");
            Drag(ring,
                 () => Fill(idx, true),
                 (dx, dy) => MoveVp(idx, dx, dy),
                 () => Fill(idx, false));
            _layer.Children.Add(ring);
            _rings.Add(ring);
        }

        _cross = MakeCross();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_cross, "Move grid");
        Drag(_cross, null, MoveAll, null);
        _layer.Children.Add(_cross);

        _builtFor = n;
    }

    /// <summary>§12.8: idle is an UNFILLED ring, the one being dragged is a
    /// FILLED dot of the same size. That is the only difference — no halo, no
    /// size change.</summary>
    private void Fill(int idx, bool on)
    {
        if (idx < 0 || idx >= _rings.Count) return;
        if (_rings[idx].Children.Count > 0 && _rings[idx].Children[0] is Ellipse e)
            e.Fill = on ? new SolidColorBrush(EditRed) : null;
    }

    // =======================================================================
    // Layout — the handles are MOVED, never rebuilt
    // =======================================================================
    /// <summary>Re-places everything from the page. Runs on every view change and
    /// on every pointer move of a drag, so the guides and the handles cannot
    /// drift apart — and touches no element's identity, so a capture held by a
    /// handle survives the whole gesture.</summary>
    public void Sync()
    {
        if (!IsActive) return;
        var def = _h.Page()?.Perspective;
        if (def == null || def.Vps.Count == 0) { End(); return; }
        if (Math.Min(def.Vps.Count, 3) != _builtFor) Build(def);

        double w = _root.ActualWidth > 1 ? _root.ActualWidth : _h.Surface.ActualWidth;
        double h = _root.ActualHeight > 1 ? _root.ActualHeight : _h.Surface.ActualHeight;
        if (w < 4 || h < 4) return;

        var centre = CentreOf(def);
        var centreS = ToScreen(centre);
        double tiltDeg = def.HorizonAngle;

        // ---- the horizon: the LINE THROUGH THE POINTS, tilt and all --------
        double slope = Math.Tan(tiltDeg * Math.PI / 180.0);
        _horizon.X1 = 0;
        _horizon.X2 = w;
        _horizon.Y1 = centreS.Y - centreS.X * slope;
        _horizon.Y2 = centreS.Y + (w - centreS.X) * slope;

        // ---- §12.8's level reference, only while the horizon is off level ---
        bool tilted = Math.Abs(tiltDeg) > LevelEpsilonDeg;
        _level.Visibility = tilted ? Visibility.Visible : Visibility.Collapsed;
        if (tilted)
        {
            double levelY = ToScreen(new Vector2(0, (float)def.HorizonY)).Y;
            _level.X1 = 0; _level.X2 = w;
            _level.Y1 = levelY; _level.Y2 = levelY;
        }

        // ---- the cone of vision, pale blue (§12.8), not red ----------------
        double radius = ConeRadius(def) * _h.Surface.ViewZoom;
        _cone.Width = radius * 2;
        _cone.Height = radius * 2;
        Canvas.SetLeft(_cone, centreS.X - radius);
        Canvas.SetTop(_cone, centreS.Y - radius);

        // 12.9's rotation grip, on the rim, on the right, turning with the
        // horizon so it always sits where the horizon leaves the circle.
        var geo = ArcGeometry(centreS, radius, tiltDeg);
        _gripHit.Data = geo;
        _grip.Data = ArcGeometry(centreS, radius, tiltDeg);

        for (int i = 0; i < _rings.Count && i < def.Vps.Count; i++)
            Place(_rings[i], ToScreen(new Vector2((float)def.Vps[i].X, (float)def.Vps[i].Y)));

        if (_cross != null) Place(_cross, centreS);

        ShowReadout(tiltDeg);

        GeometryProbe.Write("GRIDEDIT",
            $"vps={def.Vps.Count} tilt={tiltDeg:F2} centre={centreS.X:F1},{centreS.Y:F1} " +
            $"cone={radius:F1} layer={w:F0}x{h:F0}");
    }

    private void Place(FrameworkElement el, Vector2 screen)
    {
        Canvas.SetLeft(el, screen.X - el.Width / 2);
        Canvas.SetTop(el, screen.Y - el.Height / 2);
    }

    private Vector2 ToScreen(Vector2 world) =>
        world * _h.Surface.ViewZoom + _h.Surface.ViewOffset;

    /// <summary>The arc on the circle's rim, centred on the horizon direction
    /// and spanning +/- <see cref="GripSpanDeg"/> about it.</summary>
    private static Microsoft.UI.Xaml.Media.PathGeometry ArcGeometry(
        Vector2 centre, double radius, double tiltDeg)
    {
        double a0 = (tiltDeg - GripSpanDeg) * Math.PI / 180.0;
        double a1 = (tiltDeg + GripSpanDeg) * Math.PI / 180.0;
        var p0 = new Windows.Foundation.Point(
            centre.X + radius * Math.Cos(a0), centre.Y + radius * Math.Sin(a0));
        var p1 = new Windows.Foundation.Point(
            centre.X + radius * Math.Cos(a1), centre.Y + radius * Math.Sin(a1));

        var fig = new Microsoft.UI.Xaml.Media.PathFigure { StartPoint = p0, IsClosed = false };
        fig.Segments.Add(new Microsoft.UI.Xaml.Media.ArcSegment
        {
            Point = p1,
            Size = new Windows.Foundation.Size(radius, radius),
            SweepDirection = Microsoft.UI.Xaml.Media.SweepDirection.Clockwise,
            IsLargeArc = false,
        });
        var geo = new Microsoft.UI.Xaml.Media.PathGeometry();
        geo.Figures.Add(fig);
        return geo;
    }

    /// <summary>The grid's centre: the middle of the outer pair, which for a
    /// symmetric configuration is the centre of vision. A 1-point grid has only
    /// its one point, and that IS its centre.</summary>
    private static Vector2 CentreOf(PerspectiveDef def)
    {
        if (def.Vps.Count == 1)
            return new Vector2((float)def.Vps[0].X, (float)def.Vps[0].Y);
        return new Vector2(
            (float)((def.Vps[0].X + def.Vps[1].X) * 0.5),
            (float)((def.Vps[0].Y + def.Vps[1].Y) * 0.5));
    }

    private double ConeRadius(PerspectiveDef def)
    {
        if (def.Vps.Count >= 2)
        {
            double dx = def.Vps[1].X - def.Vps[0].X;
            double dy = def.Vps[1].Y - def.Vps[0].Y;
            return Math.Max(24, Math.Sqrt(dx * dx + dy * dy) * 0.5);
        }
        double z = Math.Max(0.01f, _h.Surface.ViewZoom);
        return Math.Max(24, Math.Min(_h.Surface.ActualWidth, _h.Surface.ActualHeight) * 0.42 / z);
    }

    // =======================================================================
    // Handles
    // =======================================================================
    /// <summary>A four-way move cursor over a draggable handle (§12.6).
    /// <c>ProtectedCursor</c> is protected on UIElement, so the handle has to be
    /// its own type to set one.</summary>
    private sealed class Handle : Grid
    {
        public Handle(double size)
        {
            Width = size;
            Height = size;
            // A real brush, not null: this IS the grab area and it has to be a
            // hit target. Transparent, so nothing is painted over the page.
            Background = new SolidColorBrush(Colors.Transparent);
            try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
            catch { }
        }
    }

    /// <summary>§12.6: "a red ring — an unfilled circle ~16 DIP". §12.8 adds the
    /// dragged state: the same circle, filled.</summary>
    private static Handle MakeRing()
    {
        var host = new Handle(HitD);
        host.Children.Add(new Ellipse
        {
            Width = RingD,
            Height = RingD,
            Stroke = new SolidColorBrush(EditRed),
            StrokeThickness = 2,
            Fill = null,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        return host;
    }

    /// <summary>The centre crosshair, drawn much fainter than the red handles
    /// (§12.8). Authored geometry on the 24 grid, never a glyph.</summary>
    private const string CrossGeometry =
        "M12 2 V8 " +
        "M12 16 V22 " +
        "M2 12 H8 " +
        "M16 12 H22 " +
        "M12 12 m -3.2,0 a 3.2,3.2 0 1 0 6.4,0 a 3.2,3.2 0 1 0 -6.4,0";

    private static Handle MakeCross()
    {
        var host = new Handle(HitD);
        var mark = Icons.Mark(CrossGeometry, EditRed, CrossD, stroked: true, thickness: 1.6);
        mark.Opacity = 0.45;             // §12.8: much lighter than the handles
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.IsHitTestVisible = false;
        host.Children.Add(mark);
        return host;
    }

    /// <summary>Pointer-capture drag, reported in WORLD units so the callers do
    /// not each have to divide by the zoom.</summary>
    private void Drag(FrameworkElement el, Action? began, Action<double, double> moved, Action? ended)
    {
        Windows.Foundation.Point last = default;
        bool down = false;

        // Measured against the WINDOW, not against _root. The canvas area moves
        // when a bar appears above it (the text toolbar does exactly that), and a
        // reference frame that shifts mid-gesture turns into a phantom drag.
        el.PointerPressed += (_, e) =>
        {
            down = el.CapturePointer(e.Pointer);
            last = e.GetCurrentPoint(null).Position;
            if (down) began?.Invoke();
            e.Handled = true;
        };
        el.PointerMoved += (_, e) =>
        {
            if (!down) return;
            var now = e.GetCurrentPoint(null).Position;
            double dx = now.X - last.X, dy = now.Y - last.Y;
            // last is advanced BEFORE the callback. The callback repaints the
            // canvas and re-places this very handle, and anything that came back
            // through here first would apply the same delta a second time - which
            // is how a 90 DIP drag moved a point 650 world units.
            last = now;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) { e.Handled = true; return; }
            double z = Math.Max(0.01f, _h.Surface.ViewZoom);
            moved(dx / z, dy / z);
            e.Handled = true;
        };
        void Up(PointerRoutedEventArgs? e)
        {
            if (!down) return;
            down = false;
            ended?.Invoke();
            if (e != null) { try { el.ReleasePointerCapture(e.Pointer); } catch { } }
            try { _h.Save(); } catch { }
        }
        el.PointerReleased += (_, e) => Up(e);
        el.PointerCanceled += (_, e) => Up(e);
        el.PointerCaptureLost += (_, _) => Up(null);
    }

    /// <summary>§12.9, superseding §12.8: a vanishing point SLIDES ALONG the
    /// horizon and is CONSTRAINED to it. Dragging one never changes the angle -
    /// the pointer's movement is projected onto the horizon direction, which is
    /// the "snap to the horizon" the user asked for. Only the rotation grip on
    /// the cone circle's rim turns the line.</summary>
    private void MoveVp(int idx, double dx, double dy)
    {
        var def = _h.Page()?.Perspective;
        if (def == null || idx >= def.Vps.Count) return;

        // The zenith of a 3-point set is not on the horizon and is not
        // constrained by it.
        bool onHorizon = !(def.Vps.Count == 3 && idx == 2);
        if (!onHorizon)
        {
            def.Vps[idx] = new CanvasPoint(def.Vps[idx].X + dx, def.Vps[idx].Y + dy);
            Push();
            return;
        }

        double a = def.HorizonAngle * Math.PI / 180.0;
        double ux = Math.Cos(a), uy = Math.Sin(a);
        double along = dx * ux + dy * uy;          // the component ON the line
        def.Vps[idx] = new CanvasPoint(def.Vps[idx].X + along * ux,
                                       def.Vps[idx].Y + along * uy);
        def.HorizonY = CentreOf(def).Y;
        Push();
    }

    /// <summary>§12.9's rotation grip. The horizon turns about the CENTRE and
    /// every point turns with it, which keeps the points on the line without any
    /// snapping being needed - they never leave it.</summary>
    private void RotateTo(double worldX, double worldY)
    {
        var def = _h.Page()?.Perspective;
        if (def == null || def.Vps.Count == 0) return;

        var c = CentreOf(def);
        double want = Math.Atan2(worldY - c.Y, worldX - c.X) * 180.0 / Math.PI;
        // The arc is grabbed anywhere along its span, so the angle the pointer
        // reports is not the angle the horizon should take. The offset recorded
        // when the drag began is what keeps the arc under the finger.
        double next = want - _gripOffsetDeg;
        while (next > 180) next -= 360;
        while (next < -180) next += 360;
        double delta = next - def.HorizonAngle;
        if (Math.Abs(delta) < 1e-4) return;

        def.HorizonAngle = next;
        double r = delta * Math.PI / 180.0;
        double cs = Math.Cos(r), sn = Math.Sin(r);
        for (int i = 0; i < def.Vps.Count; i++)
        {
            double px = def.Vps[i].X - c.X, py = def.Vps[i].Y - c.Y;
            def.Vps[i] = new CanvasPoint(c.X + px * cs - py * sn,
                                         c.Y + px * sn + py * cs);
        }
        def.HorizonY = CentreOf(def).Y;
        Push();
    }

    private double _gripOffsetDeg;

    /// <summary>A four-way cursor over the rotation arc. <c>ProtectedCursor</c>
    /// is protected on UIElement, so the arc has to be its own type.</summary>
    private sealed class ArcGrip : Microsoft.UI.Xaml.Shapes.Path
    {
        public ArcGrip()
        {
            try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
            catch { }
        }
    }

    /// <summary>Like <see cref="Drag"/>, but reports the pointer's ABSOLUTE
    /// position in world units. A rotation is an angle, not a displacement, and
    /// integrating deltas into one drifts.</summary>
    private void DragAbs(FrameworkElement el, Action? began, Action<double, double> moved, Action? ended)
    {
        bool down = false;

        Vector2 World(PointerRoutedEventArgs e)
        {
            var p = e.GetCurrentPoint(_layer).Position;
            float z = Math.Max(0.01f, _h.Surface.ViewZoom);
            return (new Vector2((float)p.X, (float)p.Y) - _h.Surface.ViewOffset) / z;
        }

        el.PointerPressed += (_, e) =>
        {
            down = el.CapturePointer(e.Pointer);
            if (!down) return;
            var def = _h.Page()?.Perspective;
            if (def != null && def.Vps.Count > 0)
            {
                var c = CentreOf(def);
                var w = World(e);
                double at = Math.Atan2(w.Y - c.Y, w.X - c.X) * 180.0 / Math.PI;
                _gripOffsetDeg = at - def.HorizonAngle;
            }
            began?.Invoke();
            e.Handled = true;
        };
        el.PointerMoved += (_, e) =>
        {
            if (!down) return;
            var w = World(e);
            moved(w.X, w.Y);
            e.Handled = true;
        };
        void Up(PointerRoutedEventArgs? e)
        {
            if (!down) return;
            down = false;
            ended?.Invoke();
            if (e != null) { try { el.ReleasePointerCapture(e.Pointer); } catch { } }
            try { _h.Save(); } catch { }
        }
        el.PointerReleased += (_, e) => Up(e);
        el.PointerCanceled += (_, e) => Up(e);
        el.PointerCaptureLost += (_, _) => Up(null);
    }

    /// <summary>The crosshair moves the WHOLE grid.</summary>
    private void MoveAll(double dx, double dy)
    {
        var def = _h.Page()?.Perspective;
        if (def == null) return;
        def.HorizonY += dy;
        for (int i = 0; i < def.Vps.Count; i++)
            def.Vps[i] = new CanvasPoint(def.Vps[i].X + dx, def.Vps[i].Y + dy);
        Push();
    }

    private void Push()
    {
        // A point moved by hand is §12.4's "Custom" by definition. Clearing the
        // preset here is what stops the next commit from putting the points back
        // where the preset says they belong.
        if (_h.Page() is { } page) page.GridPreset = null;
        try { _h.Changed(); } catch { }     // live re-solve, every move (§12.8)
        Sync();
    }

    // =======================================================================
    // §12.8's tilt readout
    // =======================================================================
    // The top bar belongs to ChromeBars, which this class does not modify. The
    // two cells are found by the automation names that file already gives them,
    // borrowed for the duration of the mode and handed back on the way out.
    private FrameworkElement? _zoomCell, _tiltCell;
    private Visibility _zoomWas = Visibility.Visible;

    private void ShowReadout(double degrees)
    {
        try
        {
            FindCells();
            if (_zoomCell != null && _zoomCell.Visibility != Visibility.Collapsed)
            {
                _zoomWas = _zoomCell.Visibility;
                _zoomCell.Visibility = Visibility.Collapsed;   // §12.8: no zoom % here
            }
            if (TextIn(_tiltCell) is { } t) t.Text = $"{Math.Round(degrees)}°";
        }
        catch { }
    }

    private void RestoreReadout()
    {
        try
        {
            if (_zoomCell != null) _zoomCell.Visibility = _zoomWas;
            _zoomCell = null;
            _tiltCell = null;
            _h.SyncChrome?.Invoke();
        }
        catch { }
    }

    private void FindCells()
    {
        if (_zoomCell != null && _tiltCell != null) return;
        if (_h.Chrome?.Invoke("chrome-right") is not { } right) return;
        _zoomCell ??= ByName(right, "Zoom level");
        _tiltCell ??= ByName(right, "Canvas tilt (not implemented)");
    }

    private static FrameworkElement? ByName(DependencyObject node, string name)
    {
        if (node is FrameworkElement fe &&
            Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(fe) == name) return fe;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            if (ByName(VisualTreeHelper.GetChild(node, i), name) is { } hit) return hit;
        return null;
    }

    private static TextBlock? TextIn(DependencyObject? node)
    {
        if (node == null) return null;
        if (node is TextBlock t) return t;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            if (TextIn(VisualTreeHelper.GetChild(node, i)) is { } hit) return hit;
        return null;
    }

    // =======================================================================
    // §12.6's top-centre label
    // =======================================================================
    private (Border, TextBlock) BuildBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Editing Grid.",
            FontSize = 16,
            Foreground = new SolidColorBrush(PageTheme.OnSurface),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(label);

        var done = new Button
        {
            Content = new TextBlock
            {
                Text = "Done",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PageTheme.Accent),
            },
            Padding = new Thickness(12, 3, 12, 3),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        done.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBackgroundPointerOver"] =
            new SolidColorBrush(Color.FromArgb(0x1A, 0x80, 0x80, 0x80));
        done.Resources["ButtonBackgroundPressed"] =
            new SolidColorBrush(Color.FromArgb(0x2E, 0x80, 0x80, 0x80));
        done.Resources["ButtonBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(done, "Done editing grid");
        done.Click += (_, _) => End();
        row.Children.Add(done);

        // §12.8: build the plate from PageTheme.Panel. On paper it is the page's
        // own near-white and reads as no plate at all; on a coloured or dark
        // ground it separates. That satisfies both captures instead of forcing a
        // choice between them.
        var bar = new Border
        {
            Child = row,
            Background = new SolidColorBrush(PageTheme.Panel),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
        };
        try
        {
            bar.Shadow = new ThemeShadow();
            bar.Translation = new Vector3(0, 0, 18);
        }
        catch { }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, "Editing Grid.");
        return (bar, label);
    }
}
