using System.Numerics;
using Quill.Controls;
using Quill.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Services;

/// <summary>
/// The single home for Quill's colour picker. Owns the one <see cref="ColorWheel"/>
/// instance, the overlay it floats in, and the wiring to the app's recents list,
/// canvas eyedropper and persisted mode — so every call site (pen chips, page
/// background, grid colour, accent, table cells, and the radial dial on another
/// branch) shares one picker rather than each newing up its own.
///
/// ── LIFECYCLE ─────────────────────────────────────────────────────────────
/// MainWindow calls <see cref="Configure"/> ONCE at startup with the host
/// wiring. After that, anyone — including code on other branches — opens the
/// picker with a screen point and a callback:
///
///     ColorPickerService.Open(
///         rootPoint,                 // a HINT, not a mount point: the ring
///                                    // centres itself in the viewport and the
///                                    // point only biases which way it faces
///         currentColour,
///         picked => ApplyColour(picked));   // fires live on every change
///
/// <see cref="Open"/> is safe to call before <see cref="Configure"/> (it simply
/// no-ops), so the radial dial can be wired to it today and light up the moment
/// the host is configured. Nothing else is required of the caller.
/// </summary>
public static class ColorPickerService
{
    /// <summary>
    /// The host wiring MainWindow supplies once. Everything the picker needs
    /// from the app lives behind these delegates, so the service itself never
    /// reaches into the visual tree or the library directly.
    /// </summary>
    public sealed class HostConfig
    {
        /// The panel the overlay is parented into — normally the root grid.
        /// Its bounds define "root coordinates" for <see cref="Open"/>.
        public required Panel Overlay { get; init; }
        /// Recently used colours, newest first. Read when the picker opens.
        public required Func<IReadOnlyList<Color>> GetRecents { get; init; }
        /// Push a committed colour to the MRU (dedup + cap handled by the host).
        public required Action<Color> PushRecent { get; init; }
        /// Logical eyedropper: the colour under a point given in overlay
        /// coordinates, or null if nothing is there. The host does the
        /// overlay→canvas coordinate transform.
        public required Func<Point, Color?> Sample { get; init; }
        /// Persisted mode get/set. The setter should schedule a save.
        public required Func<ColorWheelMode> GetMode { get; init; }
        public required Action<ColorWheelMode> SetMode { get; init; }
    }

    private static HostConfig? _host;
    private static ColorWheel? _wheel;
    private static Grid? _overlay;
    private static Action<Color>? _onChanged;
    private static Action? _onClosed;
    private static Color _committed;
    private static bool _closing;   // exit animation in flight

    public static bool IsOpen { get; private set; }

    public static void Configure(HostConfig host) => _host = host;

    /// <summary>
    /// Opens the picker, seeded with <paramref name="current"/>. The ring
    /// centres itself in the viewport — it is its own surface, not a flyout
    /// hanging off the caller — and <paramref name="rootPoint"/> is kept only
    /// as a hint for which way the picker's hub faces.
    /// <paramref name="onChanged"/> fires on every change — swatch tap, slider
    /// drag, eyedropper — so treat it as "the colour is now this". Optional
    /// <paramref name="onClosed"/> fires once when the picker is dismissed.
    /// </summary>
    public static void Open(Point rootPoint, Color current,
        Action<Color> onChanged, Action? onClosed = null, bool centreOnPoint = false)
    {
        if (_host == null) return;   // not yet configured — safe no-op
        // Re-opened while a previous exit is still playing: drop that animation
        // on the floor (without firing its completion) and finalise the old
        // session now, so the new one starts from a clean slate.
        _wheel?.CancelAnimation();
        Finish();

        var wheel = _wheel ??= BuildWheel();
        _onChanged = onChanged;
        _onClosed = onClosed;
        _committed = current;

        wheel.Recents = _host.GetRecents();
        wheel.Mode = _host.GetMode();
        wheel.Color = current;
        // The colour a mix starts from is whatever the caller was using (K.12).
        wheel.BaseColor = current;
        // K.2 / K.9: opened from the radial dial's centre disc, or from the pen
        // row's picker when the dial is off, the ring is CENTRED ON that control
        // rather than merely leaning toward it.
        wheel.CenterOnAnchor = centreOnPoint;
        wheel.Anchor = new Vector2((float)rootPoint.X, (float)rootPoint.Y);

        var overlay = _overlay ??= BuildOverlay(wheel);
        if (overlay.Parent == null)
        {
            _host.Overlay.Children.Add(overlay);
            Grid.SetRowSpan(overlay, 3);          // span the root grid's rows
            Canvas.SetZIndex(overlay, 150);        // above canvas, below veils
        }
        overlay.Visibility = Visibility.Visible;
        IsOpen = true;
        wheel.BeginEnter();   // the reference's staggered gravity drop
    }

    /// <summary>
    /// Dismisses the picker. The exit animation plays first: the overlay is
    /// not hidden, and onClosed is not raised, until it has finished.
    /// </summary>
    public static void Close()
    {
        if (!IsOpen || _closing) return;
        if (_wheel == null) { Finish(); return; }
        _closing = true;
        _wheel.BeginExit(Finish);
    }

    // The actual teardown, once nothing is animating.
    private static void Finish()
    {
        _closing = false;
        if (!IsOpen) return;
        IsOpen = false;
        if (_overlay != null) _overlay.Visibility = Visibility.Collapsed;
        _host?.PushRecent(_committed);
        var closed = _onClosed;
        _onChanged = null;
        _onClosed = null;
        closed?.Invoke();
    }

    private static ColorWheel BuildWheel()
    {
        var w = new ColorWheel();
        w.ColorChanged += c =>
        {
            _committed = c;
            _onChanged?.Invoke(c);
        };
        w.ModeChanged += m => _host?.SetMode(m);
        w.SampleRequested += p => w.ApplySample(_host?.Sample(p));
        w.Dismissed += Close;
        // V3 K.11: the wheel closes itself the moment a colour is chosen. The
        // eyedropper deliberately does NOT go through here - it is a repeatable
        // tool and closing on every sample would make it unusable.
        w.Picked += Close;
        return w;
    }

    private static Grid BuildOverlay(ColorWheel wheel)
    {
        // No scrim: like the reference, the ring floats over the live canvas so
        // the eyedropper can see what it samples. The wheel's own transparent
        // background catches the tap-away that dismisses it.
        var g = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        g.Children.Add(wheel);
        return g;
    }
}
