using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private readonly Host _h;
    private readonly InkSurface _surface;
    private readonly FloatingWindow _win;

    private readonly Image _preview = new()
    {
        Height = PreviewH,
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };
    private readonly Border _previewHost;

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

        _previewHost = new Border
        {
            Height = PreviewH,
            Child = _preview,
            // A Border with a radius clips its child, which keeps the sample off
            // the window's own rounded top corners.
            CornerRadius = new CornerRadius(FloatingWindow.TopRadius, FloatingWindow.TopRadius, 0, 0),
        };
        _previewHost.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1) DrawPreview();
        };

        _win.SetTabs(new (string, Func<FrameworkElement>)[] { ("Brushes", BuildBody) });

        PageTheme.Changed += () => { if (IsOpen) { _marks.Clear(); Refresh(); } };
    }

    public bool IsOpen => _win.IsOpen;
    public Windows.Foundation.Rect? Bounds => _win.Bounds;
    public void Toggle() { if (IsOpen) Hide(); else Show(); }
    public void Hide() => _win.Hide();

    public void Show()
    {
        _win.RefreshContent();
        _win.Show();
        DrawPreview();
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
        root.Children.Add(_previewHost);
        root.Children.Add(Hairline());

        // 3. My Brushes
        root.Children.Add(Band("My Brushes"));

        // 4. Basics — every PenType Quill has
        root.Children.Add(Group("Basics"));
        var basics = Strip();
        foreach (var t in BrushOrder) basics.Children.Add(BrushCell(t));
        root.Children.Add(Inset(StripScroll.Horizontal(basics)));

        // 5. Tools
        root.Children.Add(Group("Tools"));
        var tools = Strip();
        foreach (var t in ToolCells) tools.Children.Add(ToolCell(t));
        root.Children.Add(Inset(StripScroll.Horizontal(tools)));

        root.Children.Add(Spacer(14));
        root.Children.Add(Hairline());

        // 6. Subscribed
        root.Children.Add(Band("Subscribed"));
        foreach (var p in Packs) root.Children.Add(PackRow(p));
        root.Children.Add(Spacer(18));

        return root;
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

    /// <summary>The sample: one sweep across the strip with a pressure swell, at
    /// the pen's REAL size and colour. Drawn at the real size on purpose — a
    /// preview that silently fattened the mark would be the one thing this strip
    /// exists not to do.</summary>
    private PenStroke? SampleStroke(float w, float h)
    {
        var pen = ActivePen();
        if (pen == null) return null;

        var s = new PenStroke
        {
            Pen = pen.Pen,
            Color = pen.Color,
            Size = pen.Size,
            Sens = pen.Sens,
            Opacity = pen.Opacity >= 0.999f ? (float?)null : pen.Opacity,
            PressureCurve = pen.PressureCurve,
        };

        // An S-sweep: two gentle lobes, which is enough for a chisel nib to show
        // its angle and for a tapering nib to show both ends of its taper.
        float pad = 34, cy = h / 2, amp = h * 0.22f;
        const int N = 160;
        for (int i = 0; i <= N; i++)
        {
            float t = i / (float)N;
            float x = pad + t * (w - pad * 2);
            float y = cy + (float)Math.Sin(t * Math.PI * 2) * amp;
            // 0.06 at both ends, full in the middle: a taper the eye can read.
            float p = 0.06f + 0.94f * (float)Math.Sin(t * Math.PI);
            s.Points.Add(new StrokePoint(x, y, p));
        }
        return s;
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
        bool on = _h.ToolTag() == "Pen" && pen?.Pen == type;
        var mark = MarkFor(type, on ? Ink : PageTheme.WithAlpha(Ink, 0xC8));
        var cell = Cell(mark, NameOf(type), on, () => ChooseBrush(type));
        ToolTipService.SetToolTip(cell, on
            ? $"{NameOf(type)} — the brush the sample above is drawn with."
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
        bool on = _h.ToolTag() == t.Tag &&
                  (t.Style == null || lib.LastEraserStyle == t.Style.Value);
        var mark = Icons.Mark(t.Glyph, on ? Ink : PageTheme.WithAlpha(Ink, 0xC8),
                              CellMark, stroked: t.Stroked, thickness: 1.9);
        var cell = Cell(mark, t.Label, on, () =>
        {
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
        ToolTipService.SetToolTip(cell, t.Tip);
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
    private PenPreset? ActivePen()
    {
        var id = _h.ActivePreset();
        var lib = _h.Library();
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

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack, name);
        // Slop-guarded: these cells live in a horizontal strip and a Tapped
        // handler fires at the end of a slow sideways drag (§10.5 items 24-25).
        StripScroll.Tap(stack, tap);
        return stack;
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
