using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE BRUSHES PANEL — CONCEPTS-REF-2026-08-07 §4, and the first instalment of
/// the long-standing "pen library" request (pens and brushes in the manner of
/// Krita, Fresco and above all Concepts).
///
/// <para>It is the fourth tenant of <see cref="FloatingWindow"/>, after Settings,
/// Export and the Objects library, and it takes that family's chrome whole: the
/// close X upper-left with the window's name beside it, the info (i) upper-right,
/// the drag pill top-centre, corner resize grips. §4 describes exactly that
/// header, so there is nothing to build here but the contents.</para>
///
/// <para><b>The preview strip is the point of the panel.</b> §4 puts a full-width
/// ~205 DIP band at the top, on the transparency checkerboard, carrying "a sample
/// stroke of the <i>currently selected</i> brush drawn live". That is drawn by
/// <see cref="InkSurface.RenderStrokeTo"/> — the SAME renderer the page uses, on
/// a real <see cref="PenStroke"/> with the real pen's type, colour, size,
/// opacity, sensitivity and pressure curve. It is not a picture of a stroke and
/// it is not a XAML path: change the size on the dial and the sample changes
/// with it, because it is the same code drawing the same object.</para>
///
/// <para><b>The contents are Quill's, not Concepts'.</b> The reference's cells
/// read <c>Pen · Fountain · Dynamic Pen · Fixed Width</c> and its tool row reads
/// <c>Selection · Nudge · Slice · Hard Mask</c>. Those are Concepts' brushes.
/// What §4 fixes is the LAYOUT and the TYPOGRAPHY; the content is Quill's own
/// <see cref="PenType"/> — all fourteen members — and Quill's own tools, whose
/// eraser styles happen to line up with the reference's tool row almost exactly.
/// Each Basics cell's silhouette is likewise the real mark that brush leaves,
/// rendered at 40 DIP by the real renderer rather than hand-authored.</para>
///
/// <para><b>Selecting a brush actually selects it.</b> Every cell routes through
/// <c>Host.ApplyPreset</c> / <c>Host.SelectTool</c> — the very delegates
/// <see cref="ToolWheel"/> and <see cref="PenBar"/> are given, pointing at
/// MainWindow's own ApplyPreset — so the dial, the pen row and this panel cannot
/// disagree about what is selected, and MainWindow's ToolUiChanged brings the
/// panel back in step when the selection is made somewhere else.</para>
///
/// <para><b>What this is NOT.</b> It is the shell and the wiring for a pen
/// library, not a set of new brush engines. Every cell here drives one of the
/// fourteen engines Quill already has. Adding a genuinely new brush — a Krita
/// bristle, a Fresco oil, a Concepts Dynamic Pen — is renderer work in
/// InkSurface plus a parameter model PenPreset does not have yet, and the
/// Subscribed band says so on the surface rather than listing packs that cannot
/// be installed.</para>
/// </summary>
public sealed class BrushesWindow
{
    // ---- §4 geometry -----------------------------------------------------
    private const double PreviewH = 205;   // §4 item 2: "~205 DIP tall"
    private const double CheckSq = 8;      // §8's transparency checkerboard
    private const double BandH = 30;       // §4 items 3 and 6: the 30 DIP bands
    private const double CellMark = 40;    // §4 item 4: "silhouette ~40 DIP"
    private const double CellW = 74;
    private const double CellGap = 10;
    private const double Pad = 18;         // the panel's own gutter; the strip is full-bleed

    // §4's type, all of it through T() so §10.5 item 22's developer scale
    // reaches this panel as well as Settings.
    private const double BandSize = 30;    // "My Brushes" / "Subscribed"
    private const double GroupSize = 16;   // "Basics" / "Tools"
    private const double CellNameSize = 13;
    private const double PackNameSize = 17;
    private const double PackDescSize = 14;

    /// <summary>Everything the panel needs from MainWindow. Deliberately the same
    /// shape as <see cref="PenBar.Host"/> and <see cref="ToolWheel.Host"/> so the
    /// three tool surfaces are handed the same delegates and cannot drift.</summary>
    public sealed class Host
    {
        public required Func<Library> Library { get; init; }
        public required Func<Guid?> ActivePreset { get; init; }
        public required Func<string> ToolTag { get; init; }
        public required Action<PenPreset> ApplyPreset { get; init; }
        public required Action<string> SelectTool { get; init; }
        public required Action Save { get; init; }
        public required Action<string> Status { get; init; }
        /// <summary>The eraser's behaviour, which §4's Tools row is mostly made
        /// of. Optional so an older host construction still compiles.</summary>
        public Action<EraserStyle>? SetEraserStyle { get; init; }
    }

    /// <summary>WHICH SLOT a chosen brush should land in — Reference 11.24
    /// item 1, and the thing this panel's public surface had no way to say.
    ///
    /// <para>Without it the only assignment the library could perform was to
    /// the LIVE selection, which is why 11.22 item 4 stopped half-built: the
    /// dial's <c>+</c> cells assign to a slot, and a picker that applied to the
    /// active tool instead would have silently destroyed their only assignment
    /// route. Everything here is therefore expressed as "the slot", and the two
    /// callers mean different things by it — a dial sector holds a preset id, a
    /// pen-row cell IS a preset — which is why <see cref="Pick"/> reports both
    /// the type that was chosen and a preset of that type resolved out of the
    /// library. The sector takes the preset; the row retypes its own pen.</para>
    ///
    /// <para>Nothing the dial's own assignment flyout could do is lost by
    /// pointing right-click here: <see cref="More"/> reaches that flyout, and
    /// press-and-hold on a sector still opens it directly.</para></summary>
    public sealed class Target
    {
        /// <summary>What the slot is, in words — the banner and the status line
        /// both say it, so the reader can see the library is aimed somewhere
        /// other than the pen in their hand.</summary>
        public required string Label { get; init; }

        /// <summary>A brush was chosen FOR THE SLOT. The <see cref="PenType"/>
        /// picked, and a preset of that type resolved out of the library — the
        /// slot's own pen when it already matches, else an existing pen of that
        /// type, else one just added (so this stays a library rather than a
        /// mode switch, exactly as <c>ChooseBrush</c> does for the live
        /// selection).</summary>
        public required Action<PenType, PenPreset> Pick { get; init; }

        /// <summary>What the slot holds now. The strip then previews THE SLOT
        /// rather than the active tool, and a brush cell can prefer the slot's
        /// own pen when its type already matches.</summary>
        public Func<PenPreset?>? Current { get; init; }

        /// <summary><b>11.25 item 1: brushes and tools come together.</b> The one
        /// surface has to be able to assign either, so this is REQUIRED for a
        /// slot that can hold a tool — every dial sector — rather than being an
        /// optional extra. It stays nullable for the one target that genuinely
        /// cannot take a tool: a pen-row cell IS a preset, and there is no way
        /// to store Slice in a PenPreset, so §4's Tools row is left out there
        /// rather than offered and quietly ignored.</summary>
        public Action<string, EraserStyle?>? PickTool { get; init; }

        /// <summary>Empty the slot. Null means the slot cannot be empty.</summary>
        public Action? Clear { get; init; }

        /// <summary>The caller's own fuller assignment surface — for the dial,
        /// the flyout that lists pens BY NAME, tools, commands and Empty. Null
        /// means there is nothing more to reach.</summary>
        public Action? More { get; init; }
    }

    private readonly Host _h;
    private readonly InkSurface _surface;
    private readonly FloatingWindow _win;

    /// <summary>The panel this window was attached to. Kept only so the
    /// page-press dismissal (11.25 item 2) can find the XamlRoot's content,
    /// which is null at construction time and has to be hooked on first show.</summary>
    private readonly Panel _hostPanel;

    /// <summary>The slot the library is currently aimed at, or null for the
    /// ordinary "apply to what I am drawing with" behaviour.</summary>
    private Target? _target;

    /// <summary>Whether the panel was already open in ordinary mode when a
    /// target arrived. A library opened AS a picker closes once it has picked;
    /// one that was already on screen goes back to being the library.</summary>
    private bool _wasLive;

    // Built FRESH on every rebuild, never reused. WinUI allows an element one
    // parent, so a field-level Image added to a second BuildBody's tree throws —
    // and the throw is swallowed by the window's build guard, which would leave
    // the panel showing "could not be built" the first time anything refreshed it.
    private Image _preview = NewPreviewImage();
    private Border _previewHost = new() { Height = PreviewH };

    private static Image NewPreviewImage() => new()
    {
        Height = PreviewH,
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
    };

    /// <summary>Rendered 40 DIP marks, keyed by pen type and ink. Fourteen live
    /// renders on every rebuild would be the same defect §10.5 item 20 names in
    /// Settings, so they are made once and kept until the ink moves.</summary>
    private readonly Dictionary<string, ImageSource> _marks = new();

    /// <summary>Guards the ApplyPreset → ToolUiChanged → Refresh → rebuild loop:
    /// the tap already knows it is going to rebuild.</summary>
    private bool _building;

    private double FontScale => PanelFonts.ScaleFor(_h.Library(), "Brushes");
    private double T(double dip) => Math.Round(dip * FontScale, 1);

    private static Color Ink => PageTheme.OnSurface;
    private static Color Muted => PageTheme.OnSurfaceMuted;
    private static Color Line => PageTheme.Outline;
    private static Color PanelFill => PageTheme.Panel;
    private static SolidColorBrush B(Color c) => new(c);

    public static BrushesWindow Attach(Panel host, InkSurface surface, Host h) => new(host, surface, h);

    private BrushesWindow(Panel host, InkSurface surface, Host h)
    {
        _h = h;
        _surface = surface;
        _hostPanel = host;

        _win = FloatingWindow.Attach(host, 432, 690);
        _win.Title = "Brushes";
        _win.OpenOn = FloatingWindow.Side.Right;
        // §4 is full-bleed: the preview strip spans the window and the two bands
        // run edge to edge, so the window's own gutter has to come off and each
        // section supplies its own.
        _win.ContentPadding = new Thickness(0);
        _win.InfoRequested = () => _h.Status(
            "The strip at the top is the selected brush drawn live by the page's own renderer. " +
            "Basics picks the brush; Tools picks what a drag does instead of marking.");

        _win.SetTabs(new (string, Func<FrameworkElement>)[] { ("Brushes", BuildBody) });

        PageTheme.Changed += () => { if (IsOpen) { _marks.Clear(); Refresh(); } };
        // Closing the panel by any route — the header X, Hide(), a dismissal —
        // abandons the aim. A stale target would otherwise reappear the next
        // time the library opened and quietly assign to a slot nobody named.
        _win.Closed = () => _target = null;
    }

    public bool IsOpen => _win.IsOpen;
    public Windows.Foundation.Rect? Bounds => _win.Bounds;
    public void Hide() => _win.Hide();

    /// <summary>Whether the library is currently aimed at a slot.</summary>
    public bool IsTargeting => _target != null;

    /// <summary>The chrome button. A library that is aimed somewhere returns to
    /// being the ordinary library rather than closing, so the toggle cannot
    /// leave the reader looking at a panel that assigns to an invisible slot.</summary>
    public void Toggle() { if (IsOpen && _target == null) Hide(); else Show(); }

    public void Show()
    {
        _target = null;
        HookPagePress();
        _win.RefreshContent();
        _win.Show();
        DrawPreview();
    }

    /// <summary>Reference 11.24 item 1 — <b>open the library aimed at a slot.</b>
    /// Every cell then assigns to <paramref name="target"/> instead of to the
    /// live selection, the strip previews what the slot holds, and a banner
    /// says so. This is what 11.22 item 4's right-click and the dial's
    /// <c>+</c> cells both call.
    ///
    /// <para><b>11.25 item 3: idempotent with respect to the window.</b>
    /// Right-clicking a second tool must RE-AIM the panel that is already open,
    /// not open another one and not close and reopen it. So the window is only
    /// shown when it is closed; when it is already up, the target is swapped in
    /// place and the content rebuilt with the reader's scroll position kept —
    /// the banner, the highlighted cell and the strip all switch to the new
    /// slot while the panel itself does not move or blink.</para></summary>
    public void ShowFor(Target target)
    {
        HookPagePress();
        bool open = IsOpen;
        // Only recomputed on the way IN to targeting. Re-aiming an already
        // aimed panel must not forget that it was the reader's own library
        // before the first right-click, or the second pick would close a panel
        // that ought to go back to the live selection.
        if (_target == null) _wasLive = open;
        _target = target;
        _win.RefreshContent(preserveScroll: open);
        if (!open) _win.Show();
        DrawPreview();
    }

    // =======================================================================
    // 11.25 item 2 — a press on the page closes the panel
    // =======================================================================
    /// <summary>Hooked once, on the XamlRoot's content, so a press anywhere in
    /// the window is seen whether or not something nearer the pointer already
    /// handled it. XamlRoot is null in the constructor, so this runs on the
    /// first show rather than at attach time.</summary>
    private UIElement? _rootHooked;

    private void HookPagePress()
    {
        try
        {
            if (_hostPanel.XamlRoot?.Content is not UIElement root) return;
            if (ReferenceEquals(root, _rootHooked)) return;
            _rootHooked = root;
            root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnAnyPress), true);
        }
        catch { _rootHooked = null; }
    }

    /// <summary>11.25 item 2: "the panel should close when the page is clicked
    /// unlike the other floating panels."
    ///
    /// <para><b>It never sets Handled</b>, so the press that dismisses the panel
    /// still reaches the page and lays ink — and there is <b>no scrim</b>, which
    /// is the page-covering pattern §11.19 removed and the user does not want
    /// back. That is the same shape as the §11.22 item 3 settings card.</para>
    ///
    /// <para><b>Only a press on the PAGE dismisses, not any press.</b> A
    /// right-click on a dial sector or a pen-row cell is what OPENS this panel,
    /// and the shield's handler runs BEFORE this one — so treating every press
    /// as a dismissal would close the panel in the very gesture that aimed it,
    /// which is exactly the trap the settings card's own comment records. The
    /// test is therefore whether the press originated inside the ink surface:
    /// the dial floats OVER the canvas area, so its bounds cannot be used, but
    /// its shield is a different element and fails an ancestor walk.</para></summary>
    private void OnAnyPress(object sender, PointerRoutedEventArgs e)
    {
        if (!IsOpen) return;
        if (e.OriginalSource is not DependencyObject src) return;
        if (!OnThePage(src)) return;
        Hide();     // deliberately NOT Handled
    }

    /// <summary>True when <paramref name="src"/> is the ink surface or lives
    /// inside it. Walks the visual tree rather than testing a rectangle,
    /// because every floating surface in this app overlaps the page's.</summary>
    private bool OnThePage(DependencyObject src)
    {
        try
        {
            for (var d = src; d != null; d = VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, _surface)) return true;
        }
        catch { }
        return false;
    }

    /// <summary>The aim is spent. A library that was opened as a picker closes;
    /// one that was already on screen drops back to the live selection.</summary>
    private void EndTarget()
    {
        _target = null;
        if (_wasLive) { _win.RefreshContent(preserveScroll: true); DrawPreview(); }
        else _win.Hide();
        _wasLive = false;
    }

    /// <summary>Re-reads the selection. Called from MainWindow's ToolUiChanged, so
    /// picking a pen on the dial or the pen row moves this panel's highlight and
    /// redraws its sample.</summary>
    public void Refresh()
    {
        if (!IsOpen || _building) return;
        _win.RefreshContent(preserveScroll: true);
        DrawPreview();
    }

    // =======================================================================
    // Body — §4, top to bottom
    // =======================================================================
    private FrameworkElement BuildBody()
    {
        var root = new StackPanel { Background = B(PanelFill) };

        // 2. the live preview strip
        _preview = NewPreviewImage();
        _previewHost = new Border
        {
            Height = PreviewH,
            MinHeight = PreviewH,
            Child = _preview,
            // A Border with a radius clips its child, which keeps the sample off
            // the window's own rounded top corners.
            CornerRadius = new CornerRadius(FloatingWindow.TopRadius, FloatingWindow.TopRadius, 0, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_previewHost, "Brush preview");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_previewHost, "BrushPreview");
        // The strip is full-width, so its render has to follow the window's width
        // as the reader drags a corner grip.
        _previewHost.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) DrawPreview();
        };
        root.Children.Add(_previewHost);
        root.Children.Add(Hairline());

        // 11.24 item 1: when the library is aimed at a slot it has to SAY so,
        // directly under the strip it is changing the meaning of.
        if (_target is { } tgt) root.Children.Add(TargetBanner(tgt));

        // 3. My Brushes
        root.Children.Add(Band("My Brushes"));

        // 4. Basics — every PenType Quill has
        root.Children.Add(Group("Basics"));
        var basics = Strip();
        foreach (var t in BrushOrder) basics.Children.Add(BrushCell(t));
        root.Children.Add(Inset(StripScroll.Horizontal(basics)));

        // 5. Tools. 11.25 item 1: brushes and TOOLS travel together, so that
        // one right-click reaches either - every dial sector supplies PickTool
        // and therefore always shows this row. It is left out only for the pen
        // row, whose cells ARE presets and cannot store Slice; offering it
        // there would be a control that does nothing.
        if (_target == null || _target.PickTool != null)
        {
            root.Children.Add(Group("Tools"));
            var tools = Strip();
            foreach (var t in ToolCells) tools.Children.Add(ToolCell(t));
            root.Children.Add(Inset(StripScroll.Horizontal(tools)));
        }

        root.Children.Add(Spacer(14));
        root.Children.Add(Hairline());

        // 6. Subscribed
        root.Children.Add(Band("Subscribed"));
        foreach (var p in Packs) root.Children.Add(PackRow(p));
        root.Children.Add(Spacer(18));

        return root;
    }

    /// <summary>The targeting banner (11.24 item 1). It carries the slot's name
    /// and the ways out: back to the caller's fuller list, empty the slot, or
    /// abandon the aim. On SurfaceAlt, the same tinted plate §4 gives its bands,
    /// so it reads as part of the panel rather than as a dialog dropped on it.</summary>
    private FrameworkElement TargetBanner(Target t)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(new TextBlock
        {
            Text = $"Choosing for {t.Label}",
            FontSize = T(15),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = B(Ink),
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = t.PickTool != null
                ? "Pick a brush or a tool below and it goes there, not to the pen you are drawing with."
                : "Pick a brush below and it goes there, not to the pen you are drawing with.",
            FontSize = T(13),
            Foreground = B(Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = T(13) * 1.4,
        });

        var acts = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 8, 0, 0),
        };
        if (t.More != null)
            acts.Children.Add(Link("More\u2026", "BrushTargetMore",
                "Tools, commands and named pens — the caller's own full list.",
                () => { var m = t.More; EndTarget(); m(); }));
        if (t.Clear != null)
            acts.Children.Add(Link("Leave empty", "BrushTargetClear",
                "Empty the slot.",
                () => { var c = t.Clear; _h.Status($"{t.Label} emptied."); c(); EndTarget(); }));
        acts.Children.Add(Link("Cancel", "BrushTargetCancel",
            "Stop choosing for it and go back to the pen you are drawing with.",
            EndTarget));
        stack.Children.Add(acts);

        var border = new Border
        {
            Background = B(PageTheme.SurfaceAlt),
            Padding = new Thickness(Pad, 12, Pad, 12),
            Child = stack,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(border, "BrushTarget");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(border, $"Choosing for {t.Label}");
        return border;
    }

    /// <summary>A text action in the banner. A bare Button so it carries focus
    /// and an invoke pattern; Outline for the edge and OnSurface for the word,
    /// because §0 forbids a hardcoded grey.</summary>
    private FrameworkElement Link(string text, string id, string tip, Action run)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, FontSize = T(13), Foreground = B(Ink) },
            Background = B(Colors.Transparent),
            BorderBrush = B(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 3, 10, 4),
        };
        b.Click += (_, _) => run();
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(b, id);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    // =======================================================================
    // The live preview strip (§4 item 2)
    // =======================================================================
    /// <summary>Draws the checkerboard and one sample stroke of the selected
    /// brush, through <see cref="InkSurface.RenderStrokeTo"/>.
    ///
    /// <para>A <see cref="CanvasImageSource"/> rather than a CanvasControl,
    /// because this panel is a WinUI <c>Popup</c>: an image source is composited
    /// through the XAML tree that owns it and is what the settings swatches
    /// already prove works in this window family.</para></summary>
    private void DrawPreview()
    {
        try
        {
            float w = (float)Math.Max(64, _previewHost.ActualWidth > 1 ? _previewHost.ActualWidth : 432);
            float hgt = (float)PreviewH;
            var dev = CanvasDevice.GetSharedDevice();
            float dpi = 96f;
            try { if (_previewHost.XamlRoot is { } r) dpi = (float)(96 * r.RasterizationScale); } catch { }

            var src = new CanvasImageSource(dev, w, hgt, dpi);
            using (var ds = src.CreateDrawingSession(Colors.Transparent))
            {
                DrawCheckerboard(ds, w, hgt);
                var stroke = SampleStroke(w, hgt);
                if (stroke != null) _surface.RenderStrokeTo(ds, dev, stroke);
            }
            _preview.Source = src;
        }
        catch
        {
            // A device-lost or a zero-sized arrange is not worth a broken panel;
            // the strip simply stays as it was until the next redraw.
        }
    }

    /// <summary>§4's transparency checkerboard, in two steps off the panel's own
    /// fill so it reads on a near-white plate and on a near-black one alike —
    /// a fixed pair of greys would vanish into one of the two.</summary>
    private static void DrawCheckerboard(CanvasDrawingSession ds, float w, float h)
    {
        var a = Mix(PanelFill, Ink, 0.05);
        var b = Mix(PanelFill, Ink, 0.13);
        ds.Clear(a);
        for (int row = 0; row * CheckSq < h; row++)
            for (int col = row % 2; col * CheckSq < w; col += 2)
                ds.FillRectangle((float)(col * CheckSq), (float)(row * CheckSq),
                                 (float)CheckSq, (float)CheckSq, b);
    }

    /// <summary>The sample: one sweep across the strip with a pressure swell.
    ///
    /// <para><b>Reference 11.24 item 2 — the sweep adapts to the pen.</b> This
    /// used to lay out a FIXED sweep: <c>pad = 34</c> and an amplitude of
    /// <c>0.22 × 205 = 45.1</c>, while the stroke's width was the pen's real one
    /// and pressure peaked at 1.0 mid-sweep, so the widest point WAS
    /// <see cref="InkSurface.MaxStrokeWidth"/>. The band the sample occupies is
    /// <c>2 × amp + width</c>, and a Brush takes a factor of
    /// <c>0.12 + 3.2 × sens × pr²</c> — 3.32× at sens 1 — so past about size 35
    /// the band exceeded the 205 DIP strip, the "stroke" became a slab from edge
    /// to edge, and on a pale or low-opacity pen that is indistinguishable from
    /// an empty strip. That is what the user reported as blank previews.</para>
    ///
    /// <para>So the geometry is computed FROM the width rather than from
    /// constants, in the dial's order (11.1 item 2): lay a probe out, ask the
    /// renderer how wide it will really be drawn, and only then choose the
    /// amplitude, the padding and — when even a hairline sweep would not fit —
    /// the drawn size. The pen itself is never touched; only this one throwaway
    /// sample is scaled, exactly as <c>OnPreviewDraw</c> scales the dial's hoop.
    /// The accepted cost, stated in 11.23, is that the strip no longer conveys
    /// ABSOLUTE size: a 5 DIP and a 50 DIP pen read similarly. There is no
    /// caption and no scale label; what the strip conveys is the pen's SHAPE —
    /// its taper, its nib contrast, its grain, its colour and its opacity.</para></summary>
    private PenStroke? SampleStroke(float w, float h)
    {
        var pen = ActivePen();
        if (pen == null) return null;

        PenStroke Lay(float size, float pad, float amp)
        {
            var st = new PenStroke
            {
                Pen = pen.Pen,
                Color = pen.Color,
                Size = size,
                Sens = pen.Sens,
                Opacity = pen.Opacity >= 0.999f ? (float?)null : pen.Opacity,
                PressureCurve = pen.PressureCurve,
            };
            // An S-sweep: two gentle lobes, which is enough for a chisel nib to
            // show its angle and for a tapering nib to show both ends of its taper.
            float cy = h / 2;
            const int N = 160;
            for (int i = 0; i <= N; i++)
            {
                float t = i / (float)N;
                float x = pad + t * (w - pad * 2);
                float y = cy + (float)Math.Sin(t * Math.PI * 2) * amp;
                // 0.06 at both ends, full in the middle: a taper the eye can read.
                float p = 0.06f + 0.94f * (float)Math.Sin(t * Math.PI);
                st.Points.Add(new StrokePoint(x, y, p));
            }
            return st;
        }

        // Edge: the clear strip the mark must never touch, so a fat pen still
        // reads as a stroke ON a checkerboard rather than as a fill OF one.
        const float Edge = 5f;
        // The widest the mark may be DRAWN. The rest of the band is the sweep,
        // and a sweep with no room left is a straight bar - the very thing the
        // fixed layout produced - so the mark keeps under half the band.
        float band = Math.Max(24f, h - Edge * 2);
        float capW = band * 0.42f;

        // Pass 1: the probe exists only to be measured. Its own geometry barely
        // matters - SegmentWidth reads pressure and DIRECTION, and both are the
        // same shape in the final lay-out.
        float size = pen.Size;
        var probe = Lay(size, 34f, h * 0.22f);
        float width = Math.Max(0.5f, _surface.MaxStrokeWidth(probe));
        if (width > capW) { size *= capW / width; width = capW; }

        // Pass 2: amplitude and padding are what is LEFT once the width is
        // known. Amplitude never exceeds the old 0.22 h lobe, so a fine pen
        // draws exactly the sweep it always did; padding opens up for a fat one
        // so the taper at each end is not clipped by the strip's own edges.
        float amp = Math.Min(h * 0.22f, Math.Max(0f, (band - width) / 2f));
        float pad = Math.Clamp(width / 2f + Edge, 18f, w * 0.30f);
        var stroke = Lay(size, pad, amp);

        // A chisel or broad-edge nib takes its width from DIRECTION, so moving
        // the amplitude moves the width too. One correction pass settles it -
        // measuring the thing that will actually be drawn rather than the probe.
        float real = Math.Max(0.5f, _surface.MaxStrokeWidth(stroke));
        if (real > capW)
        {
            stroke.Size = size * (capW / real);
            width = capW;
            amp = Math.Min(h * 0.22f, Math.Max(0f, (band - width) / 2f));
            pad = Math.Clamp(width / 2f + Edge, 18f, w * 0.30f);
            var relaid = Lay(stroke.Size, pad, amp);
            stroke = relaid;
            real = Math.Max(0.5f, _surface.MaxStrokeWidth(stroke));
        }

        if (GeometryProbe.On)
            GeometryProbe.Write("BRUSHPREVIEW",
                $"pen={pen.Pen} penSize={pen.Size:F2} drawnSize={stroke.Size:F2} " +
                $"width={real:F2} cap={capW:F2} amp={amp:F2} pad={pad:F2} " +
                $"band={2 * amp + real:F2} strip={h:F0}x{w:F0}");

        return stroke;
    }

    // =======================================================================
    // Basics — Quill's fourteen brushes (§4 item 4)
    // =======================================================================
    /// <summary>Every <see cref="PenType"/> member, ordered as a pen tray rather
    /// than as a declaration: the everyday marks first, the wet and dry media
    /// after them. Adding a member to the enum without adding it here is a
    /// compile-time nothing, so <see cref="BrushCell"/> is driven off this array
    /// and a debug check keeps the two the same length.</summary>
    private static readonly PenType[] BrushOrder =
    {
        PenType.Standard, PenType.Ballpoint, PenType.Rollerball, PenType.Gel,
        PenType.Monoline, PenType.Fountain, PenType.Calligraphy, PenType.FeltTip,
        PenType.Marker, PenType.Highlighter, PenType.Brush, PenType.Watercolor,
        PenType.Pencil, PenType.Crayon,
    };

    /// <summary>Display names. Quill has no resource strings for the pen types —
    /// the enum member name is what everything else shows — so the two-word ones
    /// are spelt out here and the rest fall through to the member name.</summary>
    private static string NameOf(PenType t) => t switch
    {
        PenType.Standard => "Pen",
        PenType.FeltTip => "Felt Tip",
        PenType.Watercolor => "Watercolour",
        _ => t.ToString(),
    };

    private FrameworkElement BrushCell(PenType type)
    {
        var pen = ActivePen();
        // Aimed at a slot, the rule under a cell means "this is what the SLOT
        // holds" — the live tool is beside the point and testing ToolTag here
        // would light the wrong cell.
        bool on = _target != null ? pen?.Pen == type : _h.ToolTag() == "Pen" && pen?.Pen == type;
        var mark = MarkFor(type, on ? Ink : PageTheme.WithAlpha(Ink, 0xC8));
        var cell = Cell(mark, NameOf(type), on, () => ChooseBrush(type));
        ToolTipService.SetToolTip(cell, _target is { } t
            ? (on ? $"{NameOf(type)} — what {t.Label} holds now."
                  : $"Put the {NameOf(type).ToLowerInvariant()} in {t.Label}.")
            : on ? $"{NameOf(type)} — the brush the sample above is drawn with."
                 : $"Draw with the {NameOf(type).ToLowerInvariant()}.");
        return cell;
    }

    /// <summary>The 40 DIP silhouette §4 asks for — and it is the REAL mark, a
    /// short stroke of that brush rendered by the page's own renderer, not an
    /// authored path that could describe a brush the engine does not draw. Falls
    /// back to <see cref="Icons.PenStroke"/> if the device is unavailable.</summary>
    private FrameworkElement MarkFor(PenType type, Color ink)
    {
        string key = $"{type}:{ColorUtil.ToHex(ink)}";
        if (!_marks.TryGetValue(key, out var img))
        {
            img = RenderMark(type, ink)!;
            if (img != null) _marks[key] = img;
        }
        if (img == null) return Icons.Mark(Icons.PenStroke(type), ink, CellMark);
        return new Image
        {
            Source = img,
            Width = CellMark,
            Height = CellMark,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
    }

    private ImageSource? RenderMark(PenType type, Color ink)
    {
        try
        {
            const float box = (float)CellMark;
            var dev = CanvasDevice.GetSharedDevice();
            float dpi = 96f;
            try { if (_previewHost.XamlRoot is { } r) dpi = (float)(96 * r.RasterizationScale); } catch { }

            var src = new CanvasImageSource(dev, box, box, dpi);
            using (var ds = src.CreateDrawingSession(Colors.Transparent))
            {
                // Lower-left to upper-right, the same diagonal Icons' authored
                // silhouettes use, so a cell that falls back looks the same.
                var s = new PenStroke
                {
                    Pen = type,
                    Color = ColorUtil.ToHex(ink),
                    Size = 3.4f,
                    Sens = 1f,
                };
                const int N = 48;
                for (int i = 0; i <= N; i++)
                {
                    float t = i / (float)N;
                    s.Points.Add(new StrokePoint(
                        5 + t * (box - 10),
                        box - 6 - t * (box - 12),
                        0.15f + 0.85f * (float)Math.Sin(t * Math.PI)));
                }
                _surface.RenderStrokeTo(ds, dev, s);
            }
            return src;
        }
        catch { return null; }
    }

    /// <summary>§4: "Selecting a brush must actually select it."
    ///
    /// <para>Preferring a pen the reader already keeps of that type matters:
    /// tapping Fountain should land on THEIR fountain pen — its colour, its size,
    /// its pressure curve — and not on a fresh default that throws all three
    /// away. Only when the library has no pen of that type at all is one added,
    /// inheriting the current pen's colour and size, which is what makes this
    /// panel a LIBRARY rather than a mode switch. It is added once: the next tap
    /// finds it.</para></summary>
    private void ChooseBrush(PenType type)
    {
        var lib = _h.Library();
        var active = ActivePen();
        var pen = (active?.Pen == type ? active : null)
                  ?? lib.Pens.FirstOrDefault(p => p.Pen == type);

        if (pen == null)
        {
            pen = new PenPreset
            {
                Name = NameOf(type),
                Pen = type,
                Color = active?.Color ?? "#141413",
                Size = active?.Size ?? 3.5f,
                Opacity = active?.Opacity ?? 1f,
                Sens = active?.Sens ?? 1f,
            };
            lib.Pens.Add(pen);
            _h.Status($"{NameOf(type)} added to your pens.");
        }

        // 11.24 item 1: aimed at a slot, the pick goes to THE SLOT. Both the
        // type and the resolved preset go out, because a dial sector stores a
        // preset id while a pen-row cell IS a preset and only wants retyping.
        if (_target is { } t)
        {
            _building = true;
            try { t.Pick(type, pen); _h.Save(); }
            finally { _building = false; }
            _h.Status($"{NameOf(type)} \u2192 {t.Label}.");
            EndTarget();
            return;
        }

        _building = true;
        try { _h.ApplyPreset(pen); _h.Save(); }
        finally { _building = false; }
        _win.RefreshContent(preserveScroll: true);
        DrawPreview();
    }

    // =======================================================================
    // Tools (§4 item 5)
    // =======================================================================
    /// <summary>The reference's tool row is <c>Selection · Nudge · Slice · Hard
    /// Mask</c>, three of which are Concepts' eraser behaviours. Quill has those
    /// same behaviours as <see cref="EraserStyle"/> plus a fourth (Soft Mask),
    /// so the row is a near-exact match on Quill's own terms rather than a
    /// transcription of Concepts'.</summary>
    private sealed record ToolDef(string Label, string Tag, EraserStyle? Style, string Glyph, bool Stroked, string Tip);

    private static readonly ToolDef[] ToolCells =
    {
        new("Selection", "Select", null, Icons.Select, false,
            "Drag a lasso around strokes to move, scale or delete them."),
        new("Text", "Text", null, Icons.Text, false,
            "Place a text box and type into it."),
        new("Add space", "FreeSpace", null, Icons.FreeSpace, false,
            "Drag to push everything below apart and open room on the page."),
        new("Hard Mask", "Eraser", EraserStyle.HardMask, Icons.Eraser, false,
            "The eraser, removing everything under it outright."),
        new("Soft Mask", "Eraser", EraserStyle.SoftMask, SoftMaskGeometry, true,
            "The eraser, fading coverage by distance from the centre."),
        new("Slice", "Eraser", EraserStyle.Slice, SliceGeometry, true,
            "Cuts a stroke in two where you cross it, without thinning it."),
        new("Nudge", "Eraser", EraserStyle.Nudge, NudgeGeometry, true,
            "Pushes ink out of the way instead of deleting it."),
    };

    private FrameworkElement ToolCell(ToolDef t)
    {
        var lib = _h.Library();
        // Aimed at a slot there is no "current" tool cell to light: a slot holds
        // one id and the panel is not told which of the seven it is.
        bool on = _target == null && _h.ToolTag() == t.Tag &&
                  (t.Style == null || lib.LastEraserStyle == t.Style.Value);
        var mark = Icons.Mark(t.Glyph, on ? Ink : PageTheme.WithAlpha(Ink, 0xC8),
                              CellMark, stroked: t.Stroked, thickness: 1.9);
        var cell = Cell(mark, t.Label, on, () =>
        {
            if (_target is { PickTool: { } put } tgt)
            {
                _building = true;
                try { put(t.Tag, t.Style); _h.Save(); }
                finally { _building = false; }
                _h.Status($"{t.Label} \u2192 {tgt.Label}.");
                EndTarget();
                return;
            }
            if (t.Style is EraserStyle st)
            {
                lib.LastEraserStyle = st;
                _h.SetEraserStyle?.Invoke(st);
            }
            _building = true;
            try { _h.SelectTool(t.Tag); _h.Save(); }
            finally { _building = false; }
            _win.RefreshContent(preserveScroll: true);
            DrawPreview();
        });
        ToolTipService.SetToolTip(cell, _target is { } g ? $"Put {t.Label} in {g.Label}." : t.Tip);
        return cell;
    }

    // =======================================================================
    // Subscribed (§4 item 6)
    // =======================================================================
    /// <summary>§4's pack rows. Concepts sells brush packs here; Quill has no
    /// pack format, no store account and no network client, so rather than a
    /// grid of packs that could never be installed the band states what IS
    /// installed and what a real pen library would still need. The one row that
    /// carries a check is the one that is genuinely there.</summary>
    private sealed record PackDef(string Name, string Desc, bool Installed);

    private static readonly PackDef[] Packs =
    {
        new("Quill Basics",
            "The fourteen brush engines above, built into the app and always available.", true),
        new("Pressure & Tilt",
            "Per-pen pressure curves and stabiliser, edited from the dial or the pen editor.", true),
        new("Krita, Fresco and Concepts brushes",
            "Not installed. These need brush engines and a per-brush parameter model Quill has not got yet — no pack format, no store, and nothing here pretends otherwise.", false),
    };

    private FrameworkElement PackRow(PackDef p)
    {
        var row = new Grid { Padding = new Thickness(Pad, 12, Pad, 12), Background = B(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = p.Name,
            FontSize = T(PackNameSize),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = B(Ink),
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = p.Desc,
            FontSize = T(PackDescSize),
            Foreground = B(Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = T(PackDescSize) * 1.4,
            Margin = new Thickness(0, 3, 12, 0),
        });
        row.Children.Add(text);

        if (p.Installed)
        {
            var check = Icons.Mark(CheckGeometry, Ink, 20, stroked: true, thickness: 2.2);
            check.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(check, 1);
            row.Children.Add(check);
            ToolTipService.SetToolTip(row, "Installed.");
        }
        else ToolTipService.SetToolTip(row, "Not installed.");

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, p.Name);
        return row;
    }

    // =======================================================================
    // Building blocks
    // =======================================================================
    /// <summary>The pen the panel is ABOUT: the slot's own pen while the library
    /// is aimed at one, otherwise the live selection. Every cell's highlight and
    /// the whole preview strip read this, so aiming the library re-points the
    /// panel wholesale rather than in a handful of places that could drift.</summary>
    private PenPreset? ActivePen()
    {
        if (_target?.Current?.Invoke() is { } slot) return slot;
        var id = _h.ActivePreset();
        var lib = _h.Library();
        if (_target != null) return id == null ? null : lib.Pens.FirstOrDefault(p => p.Id == id);
        return (id == null ? null : lib.Pens.FirstOrDefault(p => p.Id == id)) ?? lib.Pens.FirstOrDefault();
    }

    /// <summary>§4 items 3 and 6: a 30 DIP bold band on SurfaceAlt. SurfaceAlt
    /// carries the PAGE's hue, which is right here — the band is the one place §4
    /// asks for a tinted plate rather than the panel's neutral fill.</summary>
    private FrameworkElement Band(string title) => new Border
    {
        Background = B(PageTheme.SurfaceAlt),
        Padding = new Thickness(Pad, 10, Pad, 10),
        MinHeight = BandH,
        Child = new TextBlock
        {
            Text = title,
            FontSize = T(BandSize),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = B(Ink),
        },
    };

    /// <summary>§4 item 4's group heading: 16 DIP semibold.</summary>
    private FrameworkElement Group(string title) => new TextBlock
    {
        Text = title,
        FontSize = T(GroupSize),
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = B(Ink),
        Margin = new Thickness(Pad, 18, Pad, 8),
    };

    private static StackPanel Strip() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = CellGap,
        Padding = new Thickness(Pad, 0, Pad, 0),
    };

    private static FrameworkElement Inset(UIElement content) => new Border { Child = content };

    /// <summary>One cell: the silhouette over its name. §4 gives no selected
    /// state for a cell, so it takes the pen row's (§2): a 2 DIP OnSurface rule
    /// under the mark, which reads without tinting the mark itself.</summary>
    private FrameworkElement Cell(FrameworkElement mark, string name, bool selected, Action tap)
    {
        mark.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel
        {
            Width = CellW,
            Background = B(Colors.Transparent),
        };
        stack.Children.Add(mark);
        stack.Children.Add(new Border
        {
            Height = 2,
            Width = CellW - 16,
            CornerRadius = new CornerRadius(1),
            // The rule occupies its height either way, so selecting a cell does
            // not shuffle every name in the row down by 2 DIP.
            Background = B(selected ? Ink : Colors.Transparent),
            Margin = new Thickness(0, 6, 0, 5),
        });
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = T(CellNameSize),
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = B(selected ? Ink : Muted),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        // A bare Button: keyboard focus, an invoke pattern and an accessible name,
        // and it cannot be fired by a sideways drag because the strip's scroller
        // takes the pointer capture before any Click is raised.
        return StripScroll.Bare(stack, name, tap);
    }

    private static FrameworkElement Hairline() => new Border { Height = 1, Background = B(Line) };

    private static FrameworkElement Spacer(double h) => new Border { Height = h };

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));

    // =======================================================================
    // Authored vector marks — never a glyph font, never an emoji. 24 grid.
    // =======================================================================
    /// Soft mask: the eraser's disc with a feathered rim, drawn as the disc plus
    /// two widening arcs rather than as a blur the mark cannot carry.
    private const string SoftMaskGeometry =
        "M12 7.6 A4.4 4.4 0 1 1 11.99 7.6 Z " +
        "M12 3.9 A8.1 8.1 0 1 1 11.99 3.9 Z " +
        "M3.1 12 A8.9 8.9 0 0 1 12 3.1 M20.9 12 A8.9 8.9 0 0 1 12 20.9";

    /// Slice: the cut stroke with its crossing blade.
    private const string SliceGeometry = "M3.4 20.6 L20.6 3.4 M8.2 8.6 L15.4 15.8";

    /// Nudge: the four-way move cross.
    private const string NudgeGeometry =
        "M12 3 V21 M3 12 H21 M12 3 L9.3 5.7 M12 3 L14.7 5.7 M12 21 L9.3 18.3 M12 21 L14.7 18.3 " +
        "M3 12 L5.7 9.3 M3 12 L5.7 14.7 M21 12 L18.3 9.3 M21 12 L18.3 14.7";

    /// The installed check (§4 item 6), stroked because the mark IS two lines.
    private const string CheckGeometry = "M4 12.6 L9.4 18 L20 6.4";
}
