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
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// CONCEPTS-REF §12.6 — the on-canvas vanishing-point editor.
///
/// <para>An OVERLAY rather than a mode inside <see cref="InkSurface"/>. The
/// surface is seven thousand lines of drawing and pointer routing that every
/// stroke in the app goes through, and a modal editing state threaded into it
/// would put the whole of inking at risk for four draggable handles. Here the
/// handles are real elements with real hit-testing, real pointer capture and a
/// real four-way cursor, and the surface is not touched at all.</para>
///
/// <para>The root is <b>not</b> hit-testable; only the handles and the Done
/// label are. So this never covers the chrome, never swallows a press meant for
/// the dial, and cannot leave the app unable to draw if it is somehow left
/// up.</para>
///
/// <para>The fade is §11.19's, reached through
/// <see cref="ColorPickerService.SetExternalDimming"/> rather than
/// reimplemented: there is one dimming state, one event and one host handler,
/// and nothing here touches ChromeBars or ChromeUi.</para>
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
    }

    // §12.6's sizes.
    private const double RingD = 16;      // the red vanishing-point ring
    private const double HitD = 40;       // its grab area
    private const double CrossD = 22;     // the centre crosshair

    /// <summary>The red of §12.6. A literal instruction — the horizon and the
    /// rings turn RED while editing — and the one colour on this surface that is
    /// deliberately not derived from the page.</summary>
    private static readonly Color EditRed = Color.FromArgb(0xFF, 0xE0, 0x32, 0x2C);

    private readonly Host _h;
    private readonly Grid _root;
    private readonly Canvas _layer;
    private readonly Border _bar;
    private readonly Rectangle _horizon;
    private readonly Ellipse _cone;
    private readonly List<FrameworkElement> _handles = new();

    public bool IsActive { get; private set; }

    /// <summary>Raised after <see cref="End"/>, so the caller can put whatever it
    /// dismissed back.</summary>
    public Action? Closed { get; set; }

    public static GridPointEditor Attach(Panel host, Host h) => new(host, h);

    private GridPointEditor(Panel host, Host h)
    {
        _h = h;

        _layer = new Canvas { IsHitTestVisible = true };

        _horizon = new Rectangle
        {
            Height = 2,
            Fill = new SolidColorBrush(EditRed),
            IsHitTestVisible = false,
        };
        _cone = new Ellipse
        {
            Stroke = new SolidColorBrush(Colors.Transparent),
            StrokeThickness = 1,
            Fill = null,
            IsHitTestVisible = false,
        };

        _bar = BuildBar();

        _root = new Grid
        {
            // Only the handles and the Done bar take input. The canvas, the dial
            // and every bar underneath keep theirs.
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _root.Children.Add(_layer);
        _root.Children.Add(_bar);
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
        // §12.6: the radial dial fades exactly as it does for the colour wheel,
        // while the page and the grid stay at full strength. Reusing the picker's
        // own state means one mechanism, not two.
        try { ColorPickerService.SetExternalDimming(true); } catch { }
        Sync();
    }

    public void End()
    {
        if (!IsActive) return;
        IsActive = false;
        _root.Visibility = Visibility.Collapsed;
        try { ColorPickerService.SetExternalDimming(false); } catch { }
        try { _h.Save(); } catch { }
        Closed?.Invoke();
    }

    private void OnViewChanged()
    {
        if (IsActive) Sync();
    }

    // =======================================================================
    // Layout — every handle is placed from the model each time
    // =======================================================================
    /// <summary>Re-places the handles from the page. Called on every view change
    /// and after every drag, so the guides and the handles cannot drift apart.</summary>
    public void Sync()
    {
        if (!IsActive) return;
        var def = _h.Page()?.Perspective;
        if (def == null || def.Vps.Count == 0) { End(); return; }

        _layer.Children.Clear();
        _handles.Clear();

        double w = _root.ActualWidth > 1 ? _root.ActualWidth : _h.Surface.ActualWidth;
        double h = _root.ActualHeight > 1 ? _root.ActualHeight : _h.Surface.ActualHeight;
        if (w < 4 || h < 4) return;

        var horizonPt = ToScreen(new Vector2(0, (float)def.HorizonY));

        // ---- the cone of vision, in the GRID's own colour, not red ---------
        var centre = CentreOf(def);
        var centreS = ToScreen(centre);
        double radius = ConeRadius(def) * _h.Surface.ViewZoom;
        _cone.Stroke = new SolidColorBrush(GuideColour());
        _cone.Width = radius * 2;
        _cone.Height = radius * 2;
        Canvas.SetLeft(_cone, centreS.X - radius);
        Canvas.SetTop(_cone, centreS.Y - radius);
        _layer.Children.Add(_cone);

        // ---- the horizon, red, spanning the full width --------------------
        _horizon.Width = w;
        Canvas.SetLeft(_horizon, 0);
        Canvas.SetTop(_horizon, horizonPt.Y - 1);
        _layer.Children.Add(_horizon);

        // ---- one red unfilled ring per vanishing point --------------------
        for (int i = 0; i < def.Vps.Count && i < 3; i++)
        {
            int idx = i;
            bool onHorizon = !(def.Vps.Count == 3 && idx == 2);
            var ring = MakeRing();
            Place(ring, ToScreen(new Vector2((float)def.Vps[idx].X, (float)def.Vps[idx].Y)));
            Drag(ring, (dxWorld, dyWorld) => MoveVp(idx, onHorizon, dxWorld, dyWorld));
            _layer.Children.Add(ring);
            _handles.Add(ring);
        }

        // ---- the centre crosshair: drags the WHOLE grid --------------------
        var cross = MakeCross();
        Place(cross, centreS);
        Drag(cross, MoveAll);
        _layer.Children.Add(cross);
        _handles.Add(cross);
    }

    private void Place(FrameworkElement el, Vector2 screen)
    {
        Canvas.SetLeft(el, screen.X - el.Width / 2);
        Canvas.SetTop(el, screen.Y - el.Height / 2);
    }

    private Vector2 ToScreen(Vector2 world) =>
        world * _h.Surface.ViewZoom + _h.Surface.ViewOffset;

    /// <summary>The grid's centre point: the middle of the outer pair, which for
    /// a symmetric preset is the centre of vision. A 1-point grid has only its
    /// one point, and that IS its centre.</summary>
    private static Vector2 CentreOf(PerspectiveDef def)
    {
        if (def.Vps.Count == 1)
            return new Vector2((float)def.Vps[0].X, (float)def.Vps[0].Y);
        return new Vector2(
            (float)((def.Vps[0].X + def.Vps[1].X) * 0.5),
            (float)def.HorizonY);
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

    /// <summary>The grid's own colour — the custom one when the page carries it,
    /// otherwise the automatic wash the guides are already drawn in.</summary>
    private Color GuideColour()
    {
        var page = _h.Page();
        if (page?.GridColor is { Length: > 0 } hex)
        {
            try { return PageTheme.WithAlpha(ColorUtil.Parse(hex), 0xB0); } catch { }
        }
        var ground = PaperTextures.Ground(page);
        return ColorUtil.IsDark(ground)
            ? Color.FromArgb(0x8C, 0xE2, 0xF1, 0xFF)
            : Color.FromArgb(0x7A, 0x00, 0x10, 0x26);
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
            Background = new SolidColorBrush(Colors.Transparent);
            IsHitTestVisible = true;
            try { ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll); }
            catch { }
        }
    }

    /// <summary>§12.6: "a red ring — an unfilled circle ~16 DIP".</summary>
    private static Handle MakeRing()
    {
        var host = new Handle(HitD);
        host.Children.Add(new Ellipse
        {
            Width = RingD,
            Height = RingD,
            Stroke = new SolidColorBrush(EditRed),
            StrokeThickness = 2,
            Fill = null,          // unfilled — the ring is the whole mark
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        return host;
    }

    /// <summary>The centre crosshair. Authored geometry on the 24 grid, never a
    /// glyph: a cross with a small open ring where the arms meet.</summary>
    private const string CrossGeometry =
        "M12 2 V8 " +
        "M12 16 V22 " +
        "M2 12 H8 " +
        "M16 12 H22 " +
        "M12 12 m -3.2,0 a 3.2,3.2 0 1 0 6.4,0 a 3.2,3.2 0 1 0 -6.4,0";

    private static Handle MakeCross()
    {
        var host = new Handle(HitD);
        var mark = Icons.Mark(CrossGeometry, EditRed, CrossD, stroked: true, thickness: 1.8);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.IsHitTestVisible = false;
        host.Children.Add(mark);
        return host;
    }

    /// <summary>Pointer capture drag, reported in WORLD units so the callers do
    /// not each have to divide by the zoom.</summary>
    private void Drag(FrameworkElement el, Action<double, double> moved)
    {
        Point last = default;
        bool down = false;

        el.PointerPressed += (_, e) =>
        {
            down = el.CapturePointer(e.Pointer);
            last = e.GetCurrentPoint(_root).Position;
            e.Handled = true;
        };
        el.PointerMoved += (_, e) =>
        {
            if (!down) return;
            var now = e.GetCurrentPoint(_root).Position;
            double z = Math.Max(0.01f, _h.Surface.ViewZoom);
            moved((now.X - last.X) / z, (now.Y - last.Y) / z);
            last = now;
            e.Handled = true;
        };
        void Up(PointerRoutedEventArgs e)
        {
            if (!down) return;
            down = false;
            try { el.ReleasePointerCapture(e.Pointer); } catch { }
            try { _h.Save(); } catch { }
        }
        el.PointerReleased += (_, e) => Up(e);
        el.PointerCanceled += (_, e) => Up(e);
        el.PointerCaptureLost += (_, _) => down = false;
    }

    /// <summary>Drags one vanishing point. A point that sits ON the horizon keeps
    /// sitting on it: sideways moves it alone, and a vertical move takes the
    /// horizon — and therefore every other on-horizon point — with it, because a
    /// horizon that some of its points have left is not a horizon.</summary>
    private void MoveVp(int idx, bool onHorizon, double dx, double dy)
    {
        var def = _h.Page()?.Perspective;
        if (def == null || idx >= def.Vps.Count) return;

        if (!onHorizon)
        {
            def.Vps[idx] = new CanvasPoint(def.Vps[idx].X + dx, def.Vps[idx].Y + dy);
        }
        else
        {
            def.HorizonY += dy;
            for (int i = 0; i < def.Vps.Count; i++)
            {
                bool sits = !(def.Vps.Count == 3 && i == 2);
                if (!sits) { def.Vps[i] = new CanvasPoint(def.Vps[i].X, def.Vps[i].Y + dy); continue; }
                double nx = def.Vps[i].X + (i == idx ? dx : 0);
                def.Vps[i] = new CanvasPoint(nx, def.HorizonY);
            }
        }
        Push();
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
        try { _h.Changed(); } catch { }
        Sync();
    }

    // =======================================================================
    // §12.6's top-centre label
    // =======================================================================
    private Border BuildBar()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(new TextBlock
        {
            Text = "Editing Grid.",
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x14, 0x14)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var done = new Button
        {
            Content = new TextBlock
            {
                Text = "Done",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(PageTheme.Accent),
            },
            Padding = new Thickness(10, 2, 10, 2),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        done.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBackgroundPointerOver"] =
            new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0));
        done.Resources["ButtonBackgroundPressed"] =
            new SolidColorBrush(Color.FromArgb(0x2A, 0, 0, 0));
        done.Resources["ButtonBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        done.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(done, "Done editing grid");
        done.Click += (_, _) => End();
        row.Children.Add(done);

        var bar = new Border
        {
            Child = row,
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 8, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 18, 0, 0),
            // The root is not hit-testable, so the one thing on it that must be
            // pressable says so for itself.
            IsHitTestVisible = true,
        };
        try
        {
            bar.Shadow = new ThemeShadow();
            bar.Translation = new Vector3(0, 0, 18);
        }
        catch { }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(bar, "Editing Grid.");
        return bar;
    }
}
