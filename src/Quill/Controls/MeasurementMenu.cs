using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE MEASUREMENT MENU — CONCEPTS-REF-2026-08-07 §17.1, laid out on the
/// measured geometry in docs/CONCEPTS-UI-REFERENCE.md §4.7.
///
/// <para>The zoom and tilt readouts in the top bar used to be two separate
/// gestures with no surface behind them: a tap on zoom snapped straight back to
/// 100% and a tap on tilt did nothing at all. §17.1 gives them one panel —
/// titled <b>Measurement</b>, with an (i) mark at its right — carrying a
/// <b>Zoom</b> section and a <b>Rotation</b> section, each with a live value, a
/// row of presets, and <b>its own padlock</b>.</para>
///
/// <para><b>TWO LOCKS, NOT ONE.</b> The old bar had a single lock glyph that
/// only ever governed zoom, which is what §4.7 calls out: "Concepts uses two
/// independent locks, one per value, not a single shared lock". They are held as
/// two separate flags on <see cref="ChromeBars"/> and neither can be reached
/// through the other.</para>
///
/// <para><b>NO CHROME.</b> Not a card, not a flyout, not a rounded rectangle —
/// bare marks and text directly over the canvas. That is not a style choice
/// here: UI-REFERENCE §1.1 is the measured proof, sampling behind Concepts'
/// status bar and getting canvas plus grid dots and no intervening surface, and
/// it names the Measurement popup specifically. It is the same treatment
/// <see cref="CanvasPane"/> gives Layers and Precision, and the reason this is
/// not a <see cref="Flyout"/> is the one recorded there: a FlyoutPresenter can
/// only ever approximate "no surface", and the bar's own rebuild unparents the
/// button a flyout would be positioned against.</para>
///
/// <para><b>Row geometry is MEASURED, not chosen.</b> §4.7 gives seven ink bands
/// in physical px at the 200% scaling §0.2 anchors everything to. Halved to DIP
/// and taken as band CENTRES relative to the title's, the pitches come out
/// 39.5 / 26.75 / 34.75 / 36.5 / 27 / 35 — i.e. one pitch for title-to-heading,
/// one for heading-to-value and one for value-to-chips, repeated. Those three
/// numbers are <see cref="Metrics"/>; the per-row margins below are derived from
/// them and the row heights, so retuning a row height cannot silently move a
/// band off its measurement.</para>
/// </summary>
public sealed class MeasurementMenu
{
    /// <summary>Every number this panel is laid out with, in ONE block, exactly
    /// as <see cref="ChromeBars.Metrics"/> does it — so the measured-reference
    /// pass has one place to true them up.</summary>
    public static class Metrics
    {
        // ---- type, from UI-REFERENCE §5.2's measured hierarchy --------
        /// <summary>"Panel section title (Measurement, Artboard, Canvas) | 34
        /// phys | 24 DIP | Bold".</summary>
        public const double TitleSize = 24;
        /// <summary>"Panel sub-heading (Background, Artboard Size) | ~20 phys |
        /// 15 DIP | Semibold" — which is what Zoom and Rotation are.</summary>
        public const double HeadingSize = 15;
        /// <summary>The live value. Bigger than the status bar's own 14, because
        /// §4.7 measures this band at 31 phys against the bar's 17.</summary>
        public const double ValueSize = 17;
        /// <summary>"Chip labels (1:64, 100%) | ~20 phys | 14 DIP | Regular;
        /// Semibold when selected".</summary>
        public const double ChipSize = 14;

        // ---- marks ---------------------------------------------------
        public const double InfoSize = 16, MarkSize = 16, PadlockSize = 16;

        // ---- rows ----------------------------------------------------
        public const double TitleRowH = 32, HeadingRowH = 20, ValueRowH = 26, ChipRowH = 26;

        // ---- the three measured pitches, band centre to band centre --
        /// <summary>Title to the first heading. §4.7: (171.5+180)/2 −
        /// (128+144.5)/2 = 39.5.</summary>
        public const double TitleToHeading = 39.5;
        /// <summary>Heading to its value row. Measured 26.75 and 27 for the two
        /// sections; they are the same row twice, so they get one number.</summary>
        public const double HeadingToValue = 27;
        /// <summary>Value row to its chip row. Measured 34.75 and 35.</summary>
        public const double ValueToChips = 35;
        /// <summary>A section's chips to the next section's heading. Measured
        /// 36.5 — deliberately wider than <see cref="TitleToHeading"/> is
        /// narrow, which is what separates the two sections.</summary>
        public const double ChipsToHeading = 36.5;

        /// <summary>Chip-to-chip gap and the filled chip's padding.</summary>
        public const double ChipGap = 6, ChipPadX = 9, ChipPadY = 3, ChipRadius = 8;

        /// <summary>Wide enough for `10% 100% 250% 1600%` at
        /// <see cref="ChipSize"/> with the padding above, and for the title plus
        /// its (i) mark. Not measured — §4.7 gives the panel's left edge but no
        /// width — so it is derived from the widest row it has to hold.</summary>
        public const double Width = 240;

        /// <summary>Clearance under the top bar's cluster. §4.7 puts the panel
        /// "directly below the status-bar readout, right side".</summary>
        public const double GapUnderBar = 6;
    }

    /// <summary>What the panel reads and writes. The caller owns every one of
    /// these; this control holds no state of its own except which section a
    /// pointer is over.</summary>
    public sealed class Host
    {
        public required Func<float> Zoom { get; init; }
        public required Action<float> SetZoom { get; init; }
        public required Func<bool> ZoomLocked { get; init; }
        public required Action<bool> SetZoomLocked { get; init; }

        /// <summary>Canvas tilt in degrees. Always 0 today — see
        /// <see cref="RotationUnavailable"/>.</summary>
        public required Func<double> Tilt { get; init; }
        public required Func<bool> TiltLocked { get; init; }
        public required Action<bool> SetTiltLocked { get; init; }

        public required Action<string> Status { get; init; }

        /// <summary>A rectangle, in the host's coordinates, that a press must NOT
        /// dismiss the panel from — the bar's right cluster, which carries the
        /// readouts that toggle it.
        ///
        /// <para>Without this the two mechanisms fight, and they fight in a way
        /// that is invisible from either side. A press bubbles to the host BEFORE
        /// the cell's Tapped gesture is recognised, so tapping an open panel's own
        /// readout ran dismissal first and Toggle second: the panel closed and
        /// immediately reopened, and it could never be shut from the control that
        /// opened it. Excluding the cluster geometrically fixes it once, rather
        /// than with a "ignore the next press" flag whose lifetime is wrong the
        /// first time a press lands somewhere that does not bubble here.</para></summary>
        public Func<Rect?>? KeepOpenOver { get; init; }
    }

    /// <summary>Why the Rotation section's non-zero presets are disabled rather
    /// than absent or fake.
    ///
    /// <para>Quill has no canvas rotation: the view transform is a scale and a
    /// translate, and the existing tilt readout's tooltip already says so at
    /// length. A `90°` chip that turned the readout without turning the canvas
    /// would be exactly the lie that tooltip refuses. So the chips are PRESENT,
    /// DISABLED and honest about why — the same treatment the import menu's
    /// "Take a photo" gets — rather than a section that pretends the feature is
    /// there or one that quietly omits half of what §17.1 asks for.</para></summary>
    private const string RotationUnavailable =
        "Canvas rotation is not implemented. Quill can zoom and pan but cannot turn the canvas, so " +
        "every rotation except 0° is unavailable — a preset that moved this readout without moving " +
        "the canvas behind it would be a lie.";

    private readonly Panel _hostPanel;
    private readonly Host _h;
    private readonly Grid _root;
    private readonly StackPanel _stack = new();

    // ALL SIX ARE REPLACED BY Build(), NEVER RE-PARENTED. A WinUI element may
    // have exactly one parent, and adding a live one to a freshly built row on
    // the SECOND Build throws — which in ChromeBars silently emptied both bars
    // and skipped everything after the throw. Build() therefore assigns new
    // instances rather than re-using these, and the fields exist only so Sync()
    // can reach the current ones.
    private TextBlock _zoomValue = new();
    private TextBlock _tiltValue = new();
    private ContentControl _zoomLock = new();
    private ContentControl _tiltLock = new();
    private StackPanel _zoomChips = ChipStrip();
    private StackPanel _tiltChips = ChipStrip();

    private static StackPanel ChipStrip() =>
        new() { Orientation = Orientation.Horizontal, Spacing = Metrics.ChipGap };

    /// <summary>The zoom presets. `10%` and `1600%` are NOT literals: §17.1 says
    /// to read <see cref="InkSurface.MinZoom"/> / <see cref="InkSurface.MaxZoom"/>
    /// because the range was consolidated from three copies at two values
    /// "precisely so a fourth would not appear". The two interior stops are the
    /// reference's own and have no constant to read.</summary>
    private static readonly float[] ZoomPresets =
        { InkSurface.MinZoom, 1f, 2.5f, InkSurface.MaxZoom };

    private static readonly double[] TiltPresets = { 0, 90, 180, 270 };

    /// <summary>DIPs of the right edge a docked panel is covering. The panel
    /// slides clear of it exactly as the bar's right cluster does.</summary>
    public double RightDockWidth { get; set; }

    public bool IsOpen => _root.Visibility == Visibility.Visible;

    /// <summary>The element the host parks. Exposed so <see cref="ChromeBars"/>
    /// can register it with <see cref="PanelLayout"/> as an immovable obstacle,
    /// which is what keeps the bare canvas panes from opening underneath it.</summary>
    public FrameworkElement Root => _root;

    public MeasurementMenu(Panel hostPanel, Host h)
    {
        _hostPanel = hostPanel;
        _h = h;

        _root = new Grid
        {
            Width = Metrics.Width,
            // BARE, and every one of these omissions is deliberate and measured
            // (UI-REFERENCE §1.1): no Background, no BorderBrush, no
            // CornerRadius, no shadow, no blur.
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Children = { _stack },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_root, "MeasurementMenu");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_root, "Measurement");
        // Between the dial (60) and the status-bar clusters (70), for the same
        // reason CanvasPane sits there: the panel must never swallow the readout
        // that toggles it.
        Canvas.SetZIndex(_root, 65);
        _hostPanel.Children.Add(_root);

        // NON-MODAL, DISMISSED BY A TAP ELSEWHERE (UI-REFERENCE §4.7's own
        // "Non-modal | Tap elsewhere"). The listener is PASSIVE in exactly the
        // sense FullscreenChrome's is: handledEventsToo so it still sees presses
        // the canvas has marked handled, and it NEVER sets Handled itself, so
        // not one pointer event is taken away from the ink. §15.3's "the reveal
        // region must not swallow ink" is the same fault class and this is the
        // same discipline.
        _hostPanel.AddHandler(UIElement.PointerPressedEvent,
                              new PointerEventHandler(OnHostPointerPressed), true);

        PageTheme.Changed += Rebuild;
        Build();
    }

    // =====================================================================
    // Show / hide
    // =====================================================================
    public void Toggle() { if (IsOpen) Hide(); else Show(); }

    public void Show()
    {
        // VISIBLE FIRST, THEN BUILT. Sync() is guarded on IsOpen so that a
        // closed panel costs nothing on every ViewChanged, and Build() ends by
        // calling it — so building while still Collapsed filled in the layout
        // and then skipped every value, padlock and chip fill in it. The panel
        // came up correct in shape and blank in content, and only on the FIRST
        // open, which is exactly the kind of fault that survives a demo.
        _root.Visibility = Visibility.Visible;
        Rebuild();
        Reposition();
    }

    public void Hide()
    {
        if (!IsOpen) return;
        _root.Visibility = Visibility.Collapsed;
    }

    /// <summary>Parks the panel directly under the bar's right cluster. The
    /// cluster's own band is <c>RowTop + IconPitch</c>, and both numbers are read
    /// from <see cref="ChromeBars.Metrics"/> rather than copied, so a retune of
    /// the bar carries this panel down with it.</summary>
    public void Reposition()
    {
        double inset = ChromeBars.Metrics.EdgeMargin - ChromeBars.Metrics.IconPitch / 2;
        double top = ChromeBars.Metrics.RowTop + ChromeBars.Metrics.IconPitch + Metrics.GapUnderBar;
        _root.Margin = new Thickness(0, top, inset + RightDockWidth, 0);
    }

    private void OnHostPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsOpen) return;
        try
        {
            // A press ON the panel is the panel's own business, and a press on
            // the cluster that owns the readouts belongs to the readout's own
            // Toggle (see Host.KeepOpenOver). Anything else closes it.
            var p = e.GetCurrentPoint(_hostPanel).Position;
            if (Bounds().Contains(p)) return;
            if (_h.KeepOpenOver?.Invoke() is { } keep && keep.Contains(p)) return;
            Hide();
        }
        catch { }
    }

    private Rect Bounds()
    {
        try
        {
            var o = _root.TransformToVisual(_hostPanel).TransformPoint(new Point(0, 0));
            return new Rect(o.X, o.Y, _root.ActualWidth, _root.ActualHeight);
        }
        catch { return new Rect(0, 0, 0, 0); }
    }

    // =====================================================================
    // Build
    // =====================================================================

    /// <summary>Rebuilt rather than repainted on a theme change, like every
    /// other code-built surface in this app: they capture their colours at build
    /// time.</summary>
    public void Rebuild() { if (_root.Parent != null) Build(); }

    private void Build()
    {
        _stack.Children.Clear();

        // ---- title: "Measurement" + the (i) mark ----------------------
        var title = new TextBlock
        {
            Text = "Measurement",
            FontSize = Metrics.TitleSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        };
        var info = new ContentControl
        {
            Content = Icons.Mark(Icons.Info, ChromeUi.Dim, Metrics.InfoSize),
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        ToolTipService.SetToolTip(info,
            "Zoom and rotation for this page. Each has its own padlock: locking one leaves the other free.");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(info, "About measurement");
        var titleRow = new Grid { Height = Metrics.TitleRowH };
        titleRow.Children.Add(title);
        titleRow.Children.Add(info);
        _stack.Children.Add(titleRow);

        // ---- Zoom ------------------------------------------------------
        _stack.Children.Add(Heading("Zoom", Pitch(Metrics.TitleToHeading, Metrics.TitleRowH, Metrics.HeadingRowH)));
        _zoomValue = ValueText();
        _zoomLock = LockButton(
            () => _h.ZoomLocked(),
            on =>
            {
                _h.SetZoomLocked(on);
                _h.Status(on ? $"Zoom locked at {Percent(_h.Zoom())}." : "Zoom unlocked.");
            },
            "zoom");
        _stack.Children.Add(ValueRow(Icons.Zoom, stroked: false, _zoomValue, _zoomLock,
                                     Pitch(Metrics.HeadingToValue, Metrics.HeadingRowH, Metrics.ValueRowH)));
        _zoomChips = ChipStrip();
        _stack.Children.Add(ChipRow(_zoomChips, Pitch(Metrics.ValueToChips, Metrics.ValueRowH, Metrics.ChipRowH)));

        // ---- Rotation --------------------------------------------------
        _stack.Children.Add(Heading("Rotation", Pitch(Metrics.ChipsToHeading, Metrics.ChipRowH, Metrics.HeadingRowH)));
        _tiltValue = ValueText();
        _tiltLock = LockButton(
            () => _h.TiltLocked(),
            on =>
            {
                _h.SetTiltLocked(on);
                // Honest about what the lock is holding. It is REAL state — it
                // shows in the bar and it re-lays the row out, which is §17.1's
                // own observable behaviour — but with rotation always 0 there is
                // nothing yet for it to hold still.
                _h.Status(on
                    ? "Rotation locked at 0°. Canvas rotation is not implemented, so the lock has nothing to hold yet."
                    : "Rotation unlocked.");
            },
            "rotation");
        // Icons.Rotate is the ring-and-chevron mark the dial uses for "this
        // object turns", which is what §17.1's "a rotate mark" is. Icons.Tilt is
        // a protractor and reads as an angle MEASUREMENT, not as a rotation.
        _stack.Children.Add(ValueRow(Icons.Rotate, stroked: false, _tiltValue, _tiltLock,
                                     Pitch(Metrics.HeadingToValue, Metrics.HeadingRowH, Metrics.ValueRowH)));
        _tiltChips = ChipStrip();
        _stack.Children.Add(ChipRow(_tiltChips, Pitch(Metrics.ValueToChips, Metrics.ValueRowH, Metrics.ChipRowH)));

        BuildChips();
        Sync();
    }

    /// <summary>The gap that puts two rows' ink bands at a MEASURED pitch. The
    /// pitch is centre to centre, so the margin between them is the pitch less
    /// half of each row — which is why changing a row height cannot move a band
    /// off its measurement.</summary>
    private static double Pitch(double pitch, double prevH, double thisH)
        => Math.Max(0, pitch - prevH / 2 - thisH / 2);

    private static TextBlock Heading(string text, double top) => new()
    {
        Text = text,
        FontSize = Metrics.HeadingSize,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Height = Metrics.HeadingRowH,
        Margin = new Thickness(0, top, 0, 0),
        Foreground = new SolidColorBrush(ChromeUi.Ink),
    };

    private static TextBlock ValueText() => new()
    {
        FontSize = Metrics.ValueSize,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(ChromeUi.Ink),
    };

    private static Grid ValueRow(string mark, bool stroked, TextBlock value, ContentControl padlock, double top)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        row.Children.Add(Icons.Mark(mark, ChromeUi.Ink, Metrics.MarkSize, stroked, thickness: 2));
        row.Children.Add(value);

        var host = new Grid { Height = Metrics.ValueRowH, Margin = new Thickness(0, top, 0, 0) };
        host.Children.Add(row);
        padlock.HorizontalAlignment = HorizontalAlignment.Left;
        // Parked to the RIGHT of the value by a margin rather than by being the
        // third child of the StackPanel: the padlock must not move when the
        // value's width changes from "10%" to "1600%", or the one control in the
        // row that is a TARGET would slide out from under the pointer.
        // 16 mark + 8 gap + 62 of value column clears "1600%" at 17 DIP with a
        // gap left over; the box is 26 wide and centred on the mark inside it.
        padlock.Margin = new Thickness(Metrics.MarkSize + 8 + 62, 0, 0, 0);
        host.Children.Add(padlock);
        return host;
    }

    private static Grid ChipRow(StackPanel chips, double top)
    {
        chips.VerticalAlignment = VerticalAlignment.Center;
        chips.HorizontalAlignment = HorizontalAlignment.Left;
        return new Grid { Height = Metrics.ChipRowH, Margin = new Thickness(0, top, 0, 0), Children = { chips } };
    }

    private ContentControl LockButton(Func<bool> get, Action<bool> set, string what)
    {
        var b = new ContentControl
        {
            IsTabStop = true,
            VerticalAlignment = VerticalAlignment.Center,
            // A 16 DIP mark is not a 16 DIP TARGET. The padlock is the one
            // control in its row, so it gets a real box around the mark — and
            // Transparent rather than null, because a null background does not
            // hit-test and the box would be decoration.
            Width = 26,
            Height = 26,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        b.Tapped += (_, e) => { set(!get()); Sync(); e.Handled = true; };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, "Lock " + what);
        return b;
    }

    private void BuildChips()
    {
        _zoomChips.Children.Clear();
        foreach (float z in ZoomPresets)
        {
            float target = z;
            _zoomChips.Children.Add(Chip(Percent(target), () =>
            {
                if (_h.ZoomLocked())
                {
                    // The padlock is two rows up and is the thing to press. A
                    // preset that quietly overrode it would make the lock mean
                    // less than it says.
                    _h.Status($"Zoom is locked at {Percent(_h.Zoom())} — unlock it to change the level.");
                    return;
                }
                _h.SetZoom(target);
                Sync();
            }));
        }

        _tiltChips.Children.Clear();
        foreach (double t in TiltPresets)
        {
            double target = t;
            bool available = target == 0;
            _tiltChips.Children.Add(Chip(Degrees(target),
                                         () => _h.Status(RotationUnavailable),
                                         available ? null : RotationUnavailable,
                                         enabled: available));
        }
    }

    private static Border Chip(string label, Action tap, string? tip = null, bool enabled = true)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = Metrics.ChipSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var chip = new Border
        {
            Child = text,
            Tag = label,
            Padding = new Thickness(Metrics.ChipPadX, Metrics.ChipPadY, Metrics.ChipPadX, Metrics.ChipPadY),
            CornerRadius = new CornerRadius(Metrics.ChipRadius),
            // Transparent and not null: a null background does not hit-test, and
            // the bare chips are the ones that most need to be pressable.
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = enabled ? 1 : 0.45,
        };
        if (tip != null) ToolTipService.SetToolTip(chip, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, label);
        chip.Tapped += (_, e) => { tap(); e.Handled = true; };
        return chip;
    }

    // =====================================================================
    // Live state
    // =====================================================================

    /// <summary>Pulls every readout, padlock and chip back in line with the state
    /// the host holds. Cheap enough to call from the surface's ViewChanged.</summary>
    public void Sync()
    {
        if (!IsOpen) return;
        try
        {
            float zoom = _h.Zoom();
            double tilt = _h.Tilt();
            _zoomValue.Text = Percent(zoom);
            _tiltValue.Text = Degrees(tilt);

            PaintLock(_zoomLock, _h.ZoomLocked(), "zoom", Percent(zoom));
            PaintLock(_tiltLock, _h.TiltLocked(), "rotation", Degrees(tilt));

            bool zoomLocked = _h.ZoomLocked();
            for (int i = 0; i < _zoomChips.Children.Count && i < ZoomPresets.Length; i++)
                if (_zoomChips.Children[i] is Border b)
                {
                    PaintChip(b, Math.Abs(ZoomPresets[i] - zoom) < 0.0005f, enabled: !zoomLocked);
                    if (zoomLocked)
                        ToolTipService.SetToolTip(b, $"Zoom is locked at {Percent(zoom)} — unlock it to change the level.");
                }

            for (int i = 0; i < _tiltChips.Children.Count && i < TiltPresets.Length; i++)
                if (_tiltChips.Children[i] is Border b)
                    PaintChip(b, Math.Abs(TiltPresets[i] - tilt) < 0.001, enabled: TiltPresets[i] == 0);
        }
        catch { }
    }

    private static void PaintLock(ContentControl host, bool locked, string what, string value)
    {
        host.Content = Icons.Mark(locked ? Icons.LockClosed : Icons.LockOpen,
                                  locked ? ChromeUi.Ink : ChromeUi.Dim, Metrics.PadlockSize);
        ToolTipService.SetToolTip(host, locked
            ? $"{value} is held — tap to unlock {what}"
            : $"Lock {what} at {value}");
    }

    /// <summary>§4.7: "The selected chip has a dark rounded-rect background;
    /// others are bare text."
    ///
    /// <para>The raise is taken from the PAGE toward the ink rather than from
    /// <c>PageTheme.Surface</c>, because this chip sits on the page and not on a
    /// surface — the panel has no ground of its own. That is also what makes one
    /// rule serve both ends of §4.7's "dark": on a dark page the raise lightens,
    /// on a light page it darkens, and either way it is the same distance from
    /// the paper it is drawn on.</para></summary>
    private static void PaintChip(Border chip, bool active, bool enabled)
    {
        chip.Background = new SolidColorBrush(active
            ? Mix(PageTheme.Ground, PageTheme.OnSurface, 0.16)
            : Colors.Transparent);
        chip.Opacity = enabled ? 1 : 0.45;
        if (chip.Child is TextBlock t)
        {
            t.Foreground = new SolidColorBrush(active ? PageTheme.OnSurface : PageTheme.OnSurfaceMuted);
            t.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold      // §5.2: "Semibold when selected"
                : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(255,
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    /// <summary>One formatter for a zoom level, shared with the bar's readout and
    /// the hover pill so the three can never disagree about rounding.</summary>
    public static string Percent(double zoom) => $"{Math.Round(zoom * 100)}%";

    public static string Degrees(double deg) => $"{Math.Round(deg)}°";
}
