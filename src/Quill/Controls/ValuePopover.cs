using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// The value popover — CONCEPTS-REF-2026-08-07 §1.7, which closes UI-SPEC-V3 §K
/// item 4.
///
/// Tapping or scrubbing size / opacity / smoothness opens this. Before it there
/// was a drag on a dial sector and NO UI AT ALL: nothing to read, nothing to aim
/// at, and no way to hit a round number. The reference's answer is a horizontal
/// card carrying four things, in this order top to bottom:
///
///   1. PRESET CHIPS  — `0%` `50%` `70%` `100%`. The active one sits in a filled
///      rounded chip; the rest are bare muted text. One tap is the fast path.
///   2. SLIDER        — a 2 DIP track, a tick at every preset, and a filled
///      round knob at the current value. This is the continuous path, and it is
///      draggable directly rather than only through the sector scrub.
///   3. LABEL ROW     — the property name in 11 DIP letter-spaced caps, centred,
///      with a decrement mark at the far left and an increment mark at the far
///      right for the one-step case.
///   4. TOOL TOOLTIP  — a small dark chip below-left naming the tool the value
///      belongs to, so the card is never ambiguous when two pens differ.
///
/// It is deliberately NOT a Flyout: a Flyout is modal-ish, takes focus, and
/// closes on the first pointer press anywhere — all three of which make it
/// useless as something you scrub against while watching the ink preview. This
/// is a plain element the host parks in its own overlay, and the host decides
/// when it goes away.
///
/// Both tool surfaces use this one control: the dial docks it to the right of
/// its inner disc (§1.7) and the pen bar opens it from a row of its attached
/// settings popover (§2.1), so the two can never drift apart.
/// </summary>
public sealed class ValuePopover
{
    // §1.7: "Rounded rect ~344 x 116 DIP, radius 10".
    public const double W = 344, H = 116;
    private const double Pad = 14;
    private const double TrackH = 2;        // §1.7: a 2 DIP track
    private const double KnobR = 7;         // §1.7: a filled round knob, radius 7
    private const double TickH = 9;

    /// <summary>What the popover is editing. The caller owns the value; this
    /// control only reads and writes it through the delegates.</summary>
    public sealed class Spec
    {
        /// <summary>Caption for the label row — shown in letter-spaced caps.</summary>
        public required string Name { get; init; }
        /// <summary>The tool the value belongs to, for the tooltip chip.</summary>
        public required string ToolName { get; init; }
        /// <summary>Current value, normalised 0..1.</summary>
        public required Func<double> Get { get; init; }
        /// <summary>Commit a normalised 0..1 value. Fires live while dragging.</summary>
        public required Action<double> Set { get; init; }
        /// <summary>Formats a normalised value for the chips and the readout.</summary>
        public required Func<double, string> Format { get; init; }
        /// <summary>Preset stops, normalised. §1.7's reference set is
        /// 0 / 0.5 / 0.7 / 1; a size property supplies its own.</summary>
        public required double[] Presets { get; init; }
        /// <summary>One nudge from the +/- marks, in normalised units.</summary>
        public double Step { get; init; } = 0.05;
    }

    private readonly Border _card;
    private readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid _sliderArea = new() { Height = 26 };
    private readonly Rectangle _track = new() { Height = TrackH, RadiusX = 1, RadiusY = 1,
                                                HorizontalAlignment = HorizontalAlignment.Left,
                                                VerticalAlignment = VerticalAlignment.Center };
    private readonly Canvas _ticks = new() { IsHitTestVisible = false };
    private readonly Ellipse _knob = new() { Width = KnobR * 2, Height = KnobR * 2,
                                             HorizontalAlignment = HorizontalAlignment.Left,
                                             VerticalAlignment = VerticalAlignment.Center,
                                             IsHitTestVisible = false };
    private readonly TextBlock _caption = new();
    private readonly ContentControl _minus = new(), _plus = new();
    private readonly Border _tip;
    private readonly TextBlock _tipText = new();
    private readonly Grid _root;

    private Spec? _spec;
    private bool _dragging;
    private uint? _pointer;

    /// <summary>The element the host parks in its overlay. One instance, one
    /// parent, for the life of the control — WinUI allows an element exactly one
    /// parent and re-adding a live one throws.</summary>
    public FrameworkElement Element => _root;

    public bool IsOpen => _root.Visibility == Visibility.Visible;

    /// <summary>The property currently open, or null. The host uses this to keep
    /// its own highlight in step.</summary>
    public string? OpenProperty { get; private set; }

    /// <summary>Raised whenever the value changes from inside the popover, so
    /// the host can repaint the surface behind it.</summary>
    public event Action? ValueChanged;

    public ValuePopover()
    {
        // ---- 1. preset chips ------------------------------------------
        _chips.HorizontalAlignment = HorizontalAlignment.Center;

        // ---- 2. slider ------------------------------------------------
        // A hand-built slider rather than the WinUI one: §1.7 asks for tick
        // marks at the presets and a bare 2 DIP track, and restyling Slider to
        // that costs more than the forty lines below.
        _sliderArea.Background = new SolidColorBrush(Colors.Transparent);   // hit-testable
        _sliderArea.Children.Add(_track);
        _sliderArea.Children.Add(_ticks);
        _sliderArea.Children.Add(_knob);
        _sliderArea.PointerPressed += OnTrackPressed;
        _sliderArea.PointerMoved += OnTrackMoved;
        _sliderArea.PointerReleased += OnTrackReleased;
        _sliderArea.PointerCaptureLost += (_, _) => { _dragging = false; _pointer = null; };
        _sliderArea.SizeChanged += (_, _) => Sync();

        // ---- 3. label row ---------------------------------------------
        _caption.FontSize = 11;
        _caption.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _caption.CharacterSpacing = 220;     // 1/1000 em — §1.7's letter-spaced caps
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _caption.VerticalAlignment = VerticalAlignment.Center;
        _minus.VerticalAlignment = VerticalAlignment.Center;
        _minus.HorizontalAlignment = HorizontalAlignment.Left;
        _plus.VerticalAlignment = VerticalAlignment.Center;
        _plus.HorizontalAlignment = HorizontalAlignment.Right;
        Nudge(_minus, -1);
        Nudge(_plus, +1);
        var labelRow = new Grid { Height = 20 };
        labelRow.Children.Add(_minus);
        labelRow.Children.Add(_caption);
        labelRow.Children.Add(_plus);

        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_chips);
        stack.Children.Add(_sliderArea);
        stack.Children.Add(labelRow);

        _card = new Border
        {
            Child = stack,
            Width = W,
            Height = H,
            Padding = new Thickness(Pad, 10, Pad, 10),
            CornerRadius = new CornerRadius(10),     // §1.7: radius 10, no border
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // ---- 4. the tool-name tooltip, below-left of the card ----------
        _tipText.FontSize = 11;
        _tipText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _tip = new Border
        {
            Child = _tipText,
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(7),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(6, H + 6, 0, 0),
            IsHitTestVisible = false,
        };

        _root = new Grid
        {
            Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Width = W,
            Height = H + 34,
        };
        _root.Children.Add(_card);
        _root.Children.Add(_tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_root, "ValuePopover");
        Canvas.SetZIndex(_root, 80);

        PageTheme.Changed += Paint;
    }

    // ===================================================================
    // Open / close / place
    // ===================================================================

    /// <summary>Show the popover for <paramref name="spec"/> at
    /// <paramref name="topLeft"/> in the host's coordinates. Re-opening on a
    /// different property while it is up simply re-targets it.</summary>
    public void Open(Spec spec, string property, Point topLeft, double hostW, double hostH)
    {
        _spec = spec;
        OpenProperty = property;
        _root.Visibility = Visibility.Visible;
        Place(topLeft, hostW, hostH);
        Rebuild();
        Paint();
    }

    public void Close()
    {
        if (!IsOpen) return;
        _root.Visibility = Visibility.Collapsed;
        _dragging = false;
        _pointer = null;
        _spec = null;
        OpenProperty = null;
    }

    /// <summary>Re-park without changing what is shown — for a host resize.</summary>
    public void Place(Point topLeft, double hostW, double hostH)
    {
        double x = Math.Clamp(topLeft.X, 8, Math.Max(8, hostW - W - 8));
        double y = Math.Clamp(topLeft.Y, 8, Math.Max(8, hostH - (H + 34) - 8));
        _root.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>Pull the readout back in line with the value the host holds —
    /// called when the sector scrub, not the popover, moved it.</summary>
    public void Sync()
    {
        if (_spec == null) return;
        double t = Math.Clamp(_spec.Get(), 0, 1);
        double w = Math.Max(1, _sliderArea.ActualWidth);
        _track.Width = w;
        _knob.Margin = new Thickness(t * (w - KnobR * 2), 0, 0, 0);
        BuildTicks(w);
        for (int i = 0; i < _chips.Children.Count; i++)
            if (_chips.Children[i] is Border b && b.Tag is double preset)
                PaintChip(b, Math.Abs(preset - t) < 0.005);
    }

    // ===================================================================
    // Content
    // ===================================================================
    private void Rebuild()
    {
        if (_spec == null) return;
        _caption.Text = _spec.Name.ToUpperInvariant();
        _tipText.Text = _spec.ToolName;

        _chips.Children.Clear();
        foreach (double preset in _spec.Presets)
        {
            double p = preset;
            var text = new TextBlock
            {
                Text = _spec.Format(p),
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var chip = new Border
            {
                Child = text,
                Tag = p,
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(9),
                // Transparent, not null: a null background does not hit-test and
                // the bare chips would be unclickable.
                Background = new SolidColorBrush(Colors.Transparent),
            };
            chip.PointerPressed += (_, e) => { Commit(p); e.Handled = true; };
            _chips.Children.Add(chip);
        }
        Sync();
    }

    private void BuildTicks(double w)
    {
        _ticks.Children.Clear();
        if (_spec == null) return;
        _ticks.Width = w;
        _ticks.Height = _sliderArea.ActualHeight;
        double mid = _ticks.Height / 2;
        foreach (double p in _spec.Presets)
        {
            // Darker than the track, not lighter: a tick painted at a LOWER
            // alpha than the 55% track it crosses is invisible by construction,
            // which is exactly how it looked on screen.
            var t = new Rectangle
            {
                Width = 2,
                Height = TickH,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnSurface, 210)),
            };
            Canvas.SetLeft(t, KnobR + p * (w - KnobR * 2) - 1);
            Canvas.SetTop(t, mid - TickH / 2);
            _ticks.Children.Add(t);
        }
    }

    private void Nudge(ContentControl host, int dir)
    {
        host.IsTabStop = false;
        host.Background = new SolidColorBrush(Colors.Transparent);
        host.PointerPressed += (_, e) =>
        {
            if (_spec == null) return;
            Commit(Math.Clamp(_spec.Get() + dir * _spec.Step, 0, 1));
            e.Handled = true;
        };
    }

    private void Commit(double t)
    {
        if (_spec == null) return;
        _spec.Set(Math.Clamp(t, 0, 1));
        Sync();
        ValueChanged?.Invoke();
    }

    // ===================================================================
    // Slider input
    // ===================================================================
    private double At(Point p)
    {
        double w = Math.Max(1, _sliderArea.ActualWidth) - KnobR * 2;
        return Math.Clamp((p.X - KnobR) / w, 0, 1);
    }

    private void OnTrackPressed(object sender, PointerRoutedEventArgs e)
    {
        // Capture can throw when the pointer already belongs to somebody else;
        // arm only once it has actually landed, or the control goes deaf on a
        // pointer id it does not own.
        bool got;
        try { got = _sliderArea.CapturePointer(e.Pointer); } catch (ArgumentException) { got = false; }
        if (!got) return;
        _dragging = true;
        _pointer = e.Pointer.PointerId;
        Commit(At(e.GetCurrentPoint(_sliderArea).Position));
        e.Handled = true;
    }

    private void OnTrackMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _pointer) return;
        Commit(At(e.GetCurrentPoint(_sliderArea).Position));
        e.Handled = true;
    }

    private void OnTrackReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pointer != null && e.Pointer.PointerId != _pointer) return;
        _dragging = false;
        _pointer = null;
        try { _sliderArea.ReleasePointerCapture(e.Pointer); } catch { }
        e.Handled = true;
    }

    // ===================================================================
    // Theme — every colour from PageTheme, none of them hard-coded
    // ===================================================================
    private void Paint()
    {
        // §1.7 asks for "Surface at 78% with a blur". The BLUR is the half of
        // that which cannot be had here: an AcrylicBrush over a Win2D swap chain
        // samples it a frame late and smears every time the ink moves
        // underneath. Without a blur, 78% is not translucency - it is the page's
        // own text reading straight through the card, which is exactly how it
        // looked on screen and made the numbers unreadable. The card is opaque
        // until there is a blur to put behind it.
        //
        // Opacity is pinned on the way past for the same reason: this element is
        // parked in whichever surface's layer is up, and a layer that fades
        // itself in would otherwise multiply straight through the card.
        _card.Background = new SolidColorBrush(PageTheme.Surface);
        _card.Opacity = 1;
        _root.Opacity = 1;
        _card.BorderThickness = new Thickness(0);
        _caption.Foreground = new SolidColorBrush(PageTheme.OnSurfaceMuted);
        _track.Fill = new SolidColorBrush(PageTheme.WithAlpha(PageTheme.OnSurface, 140));
        _knob.Fill = new SolidColorBrush(PageTheme.OnSurface);

        // The tooltip is DARK in the reference whatever the page is, so it reads
        // as a system label rather than as another panel.
        _tip.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1A, 0x1A, 0x1A));
        _tipText.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));

        var mark = PageTheme.OnSurfaceMuted;
        _minus.Content = Icons.Mark(Icons.Minus, mark, 16, stroked: true, thickness: 2);
        _plus.Content = Icons.Mark(Icons.Plus, mark, 16, stroked: true, thickness: 2);

        for (int i = 0; i < _chips.Children.Count; i++)
            if (_chips.Children[i] is Border b && b.Tag is double preset && _spec != null)
                PaintChip(b, Math.Abs(preset - Math.Clamp(_spec.Get(), 0, 1)) < 0.005);
        Sync();
    }

    private static void PaintChip(Border chip, bool active)
    {
        // §1.7: the active chip is a filled rounded chip in a RAISED surface with
        // OnSurface text; the others are bare OnSurfaceMuted with no fill at all.
        //
        // SurfaceAlt is only 4 L* from Surface - it is the right token for a
        // heading band on the PAGE, and invisible for a chip sitting ON Surface,
        // which is how it came out on screen. The raise is taken toward
        // OnSurface instead, so it reads on a paper, a blue or a kraft ground
        // alike rather than only on one of them.
        chip.Background = new SolidColorBrush(active ? Mix(PageTheme.Surface, PageTheme.OnSurface, 0.14) : Colors.Transparent);
        if (chip.Child is TextBlock t)
            t.Foreground = new SolidColorBrush(active ? PageTheme.OnSurface : PageTheme.OnSurfaceMuted);
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
