using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Quill.Helpers;
using Quill.Models;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// The EXPORT TENANT of <see cref="FloatingWindow"/> (UI-SPEC-V3 J) — the second
/// tenant after <see cref="SettingsWindow"/>, and deliberately built the same
/// way: the window owns the chrome (close upper-left, info upper-right, the
/// top-centre drag bar, the category divider row and the resize grips) and this
/// class only fills the content.
///
/// <para>The pane is the reference layout, top to bottom: <b>Format</b> — a row
/// of circular buttons with sub-labels, the selected one bold inside a ring,
/// with a one-line description of it underneath; <b>Region</b> — Screenshot,
/// Entire Drawing, Selection and a DYNAMIC final chip that is the page's own
/// size ("A4" on an A4 page, "Artboard" on an infinite one); <b>Options</b> —
/// Include Background and Include Grid as #78a19c toggle sliders;
/// <b>Details</b> — the true output size and a 100/200/400% scale; and the
/// #3282aa primary action.</para>
///
/// <para><b>Nothing here fakes a file.</b> Quill can write JPG, PNG, SVG, the
/// native .quill bundle, a flattened PDF and a vector PDF, and those are wired
/// through to the real exporters. It has no DXF writer and no layer model, so
/// DXF and PSD are SHOWN — the reference has them — but disabled, with a
/// tooltip that says why. The same rule governs the region chips and the two
/// option toggles: a combination the pipeline cannot honour is disabled and
/// explains itself rather than silently producing the wrong file.</para>
/// </summary>
public sealed class ExportWindow
{
    public sealed class Host
    {
        public required Func<InkSurface> Surface { get; init; }
        public required Func<NotePage?> Page { get; init; }
        public required Func<Notebook?> Notebook { get; init; }
        public required Func<Section?> Section { get; init; }
        /// <summary>(extension, file-type name) -> the picked file. Lives on the
        /// host because a picker has to be initialised with the window HWND.</summary>
        public required Func<string, string, Task<StorageFile?>> PickSave { get; init; }
        public required Action<string> Status { get; init; }
    }

    // =====================================================================
    // Formats
    // =====================================================================
    private enum Fmt { Jpg, Png, Svg, Dxf, Psd, Quill, PdfFlat, PdfVector }

    /// <summary>How a format is produced. Raster goes through a real capture of
    /// the surface; Vector through Quill's vector page model; Native writes the
    /// page's own JSON.</summary>
    private enum Kind { Raster, Vector, Native, None }

    private sealed record Def(
        Fmt Id, string Label, string Sub, Kind Kind,
        string Ext, string TypeName, string Desc, string? Disabled = null);

    private static readonly Def[] Formats =
    {
        new(Fmt.Jpg, "JPG", "Compressed", Kind.Raster, ".jpg", "JPEG image",
            "A compressed picture of the drawing. The smallest file here; flat colour and fine ink soften a little."),
        new(Fmt.Png, "PNG", "Lossless", Kind.Raster, ".png", "PNG image",
            "A lossless picture with transparency. The safe choice for sharing a drawing as an image."),
        new(Fmt.Svg, "SVG", "Vector", Kind.Vector, ".svg", "SVG vector image",
            "True vector paths in an open format — crisp at any size and editable in any vector app."),
        new(Fmt.Dxf, "DXF", "Vector", Kind.None, ".dxf", "DXF drawing",
            "CAD interchange vector paths.",
            "Quill has no DXF writer yet. It is shown here so the row matches the design, but exporting it would produce a file that is not really DXF."),
        new(Fmt.Psd, "PSD", "Lossless", Kind.None, ".psd", "Photoshop document",
            "A layered Photoshop document.",
            "Quill has neither a layer model nor a PSD writer yet, so there is nothing honest to put in the layers of a PSD."),
        new(Fmt.Quill, ".quill", "Native", Kind.Native, ".quill", "Quill page",
            "Quill's own format: every stroke, image and text box exactly as stored, and re-importable with no loss."),
        new(Fmt.PdfFlat, "PDF", "Flattened", Kind.Raster, ".pdf", "PDF document",
            "A PDF whose page is one flattened image. Prints exactly as it looks; the ink is no longer selectable."),
        new(Fmt.PdfVector, "PDF", "Vector Paths", Kind.Vector, ".pdf", "PDF document",
            "A PDF of real vector paths with selectable text — ink stays crisp at any zoom."),
    };

    private static Def Find(Fmt f) => Formats.First(d => d.Id == f);

    // =====================================================================
    // Regions
    // =====================================================================
    private enum Region { Screenshot, Drawing, Selection, Artboard }

    private readonly Host _h;
    private readonly FloatingWindow _win;

    private Fmt _fmt = Fmt.Png;
    private Region _region = Region.Drawing;
    private bool _includeBg = true;
    private bool _includeGrid = true;
    private int _scale = 1;
    private bool _busy;

    // The pane's live regions. Their CONTENTS are rebuilt in place by Sync() so
    // picking a chip never scrolls the pane back to the top; the containers
    // themselves are re-created by BuildRoot(), because a WinUI element may have
    // exactly one parent and the window rebuilds its tab content on a theme
    // change - re-adding the same instance to a fresh root throws.
    private StackPanel _formatRow = ChromeUi.Row(0);
    private TextBlock _formatDesc = ChromeUi.Caption("");
    private StackPanel _regionRow = ChromeUi.Row(6);
    private StackPanel _optionRows = new() { Spacing = 0 };
    private StackPanel _scaleRow = ChromeUi.Row(6);
    private TextBlock _sizeText = ChromeUi.Label("", strong: true);
    private TextBlock _note = ChromeUi.Caption("");
    private Grid _actionHost = new();

    public static ExportWindow Attach(Panel host, Host h) => new(host, h);

    private ExportWindow(Panel host, Host h)
    {
        _h = h;
        _win = FloatingWindow.Attach(host, 512, 660);
        _win.Title = "Export";
        _win.InfoRequested = () => _h.Status(
            "Pick a format, then the area to export and what to include. Anything Quill cannot write yet is switched off and says why.");
        _win.SetTabs(new (string, Func<FrameworkElement>)[] { ("Export", BuildRoot) });
    }

    public bool IsOpen => _win.IsOpen;
    public void Show() { _win.Show(); Sync(); }
    public void Hide() => _win.Hide();
    public void Toggle() { _win.Toggle(); if (_win.IsOpen) Sync(); }
    /// <summary>Rebuild after a theme change — this surface captures its colours
    /// at build time exactly like the settings window.</summary>
    public void Refresh() { if (_win.IsOpen) _win.RefreshContent(); }

    // =====================================================================
    // Layout
    // =====================================================================
    private FrameworkElement BuildRoot()
    {
        _formatRow = ChromeUi.Row(0);
        _formatDesc = ChromeUi.Caption("");
        _regionRow = ChromeUi.Row(6);
        _optionRows = new StackPanel { Spacing = 0 };
        _scaleRow = ChromeUi.Row(6);
        _sizeText = ChromeUi.Label("", strong: true);
        _note = ChromeUi.Caption("");
        _actionHost = new Grid();

        var root = new StackPanel { Spacing = 2 };

        root.Children.Add(ChromeUi.Heading("Format"));
        root.Children.Add(ChromeUi.HScroll(_formatRow));
        root.Children.Add(_formatDesc);

        root.Children.Add(ChromeUi.Rule());
        root.Children.Add(ChromeUi.Heading("Region"));
        root.Children.Add(ChromeUi.Caption("Select the area you'd like to export"));
        root.Children.Add(ChromeUi.HScroll(_regionRow));

        root.Children.Add(ChromeUi.Rule());
        root.Children.Add(ChromeUi.Heading("Options"));
        root.Children.Add(ChromeUi.Caption("Select anything you'd like to include in the file"));
        root.Children.Add(_optionRows);

        root.Children.Add(ChromeUi.Rule());
        root.Children.Add(ChromeUi.Heading("Details"));

        var sizeRow = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        sizeRow.Children.Add(ChromeUi.Label("Output Size"));
        _sizeText.HorizontalAlignment = HorizontalAlignment.Right;
        sizeRow.Children.Add(_sizeText);
        root.Children.Add(sizeRow);

        var scaleWrap = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        scaleWrap.Children.Add(ChromeUi.Label("Scale"));
        _scaleRow.HorizontalAlignment = HorizontalAlignment.Right;
        scaleWrap.Children.Add(_scaleRow);
        root.Children.Add(scaleWrap);

        root.Children.Add(_note);

        _actionHost.Margin = new Thickness(0, 14, 0, 4);
        root.Children.Add(_actionHost);

        Sync();
        return root;
    }

    // =====================================================================
    // State -> UI. Every chip that the chosen format cannot honour is
    // DISABLED and carries the reason; nothing is quietly dropped.
    // =====================================================================
    private void Sync()
    {
        var def = Find(_fmt);

        // ---- format row -------------------------------------------------
        _formatRow.Children.Clear();
        foreach (var d in Formats)
        {
            var f = d;
            bool enabled = f.Disabled == null;
            _formatRow.Children.Add(ChromeUi.CircleOption(
                f.Label, f.Sub, _fmt == f.Id, enabled,
                enabled ? f.Desc : f.Desc + "  —  " + f.Disabled,
                () => { _fmt = f.Id; ClampRegion(); Sync(); }));
        }
        _formatDesc.Text = def.Disabled == null ? def.Desc : def.Desc + "  " + def.Disabled;

        // ---- region row ---------------------------------------------------
        _regionRow.Children.Clear();
        foreach (var (r, label, tip, on) in RegionOptions())
        {
            var rr = r;
            _regionRow.Children.Add(ChromeUi.Chip(label, _region == rr, () => { _region = rr; Sync(); }, on, tip));
        }

        // ---- options ------------------------------------------------------
        _optionRows.Children.Clear();
        var (bgOn, bgWhy) = BackgroundOption(def);
        var (gridOn, gridWhy) = GridOption(def);
        _optionRows.Children.Add(ChromeUi.ToggleRow("Include Background",
            bgOn && _includeBg || !bgOn && ForcedBackground(def),
            v => { _includeBg = v; Sync(); }, bgOn, bgWhy));
        _optionRows.Children.Add(ChromeUi.ToggleRow("Include Grid",
            gridOn && _includeGrid, v => { _includeGrid = v; Sync(); }, gridOn, gridWhy));

        // ---- details ------------------------------------------------------
        _scaleRow.Children.Clear();
        bool scalable = def.Kind == Kind.Raster;
        foreach (int s in new[] { 1, 2, 4 })
        {
            int ss = s;
            _scaleRow.Children.Add(ChromeUi.Chip($"{s * 100}%", _scale == ss, () => { _scale = ss; Sync(); },
                scalable, scalable ? null : "Scale applies to the pixel formats. " + def.Label + " is not rendered to pixels."));
        }

        var size = OutputSize(def);
        _sizeText.Text = size == null ? "—" : $"{size.Value.W} x {size.Value.H} px  @ 96 ppi";
        _note.Text = DetailNote(def);

        // ---- action -------------------------------------------------------
        _actionHost.Children.Clear();
        if (def.Disabled != null)
        {
            var blocked = ChromeUi.PrimaryButton("Export " + def.Label, () => { });
            blocked.IsEnabled = false;
            ToolTipService.SetToolTip(blocked, def.Disabled);
            _actionHost.Children.Add(blocked);
        }
        else
        {
            var go = ChromeUi.PrimaryButton(_busy ? "Exporting…" : "Export " + def.Label, () => _ = RunAsync());
            go.IsEnabled = !_busy && _h.Page() != null;
            _actionHost.Children.Add(go);
        }
    }

    /// <summary>A format change can strand the region on a chip it cannot use.</summary>
    private void ClampRegion()
    {
        foreach (var (r, _, _, on) in RegionOptions())
            if (r == _region && on) return;
        _region = Region.Drawing;
    }

    private IEnumerable<(Region R, string Label, string? Tip, bool Enabled)> RegionOptions()
    {
        var def = Find(_fmt);
        var s = _h.Surface();
        bool raster = def.Kind == Kind.Raster;
        string vectorWhy = "Quill's vector page always carries the whole drawing, so " + def.Label + " ignores the other regions.";
        string nativeWhy = "The native format always carries the whole page.";
        string? why = def.Kind switch
        {
            Kind.Vector => vectorWhy,
            Kind.Native => nativeWhy,
            Kind.None => def.Disabled,
            _ => null,
        };

        yield return (Region.Screenshot, "Screenshot",
            why ?? "Exactly what is on screen right now, at its on-screen size.", raster);

        yield return (Region.Drawing, "Entire Drawing",
            def.Kind == Kind.None ? def.Disabled : "Everything on the page, framed tightly.",
            def.Kind != Kind.None);

        bool hasSel = s.HasSelection && !s.SelectionBoundsWorld.IsEmpty;
        yield return (Region.Selection, "Selection",
            !hasSel ? "Nothing is selected. Lasso some ink first." : why ?? "Just the selected ink.",
            raster && hasSel);

        // The DYNAMIC chip: the page's own size, named after the page.
        yield return (Region.Artboard, ArtboardLabel(),
            why ?? (ArtboardRect() == null
                ? "This page is infinite, so its artboard is whatever you have drawn."
                : "The page's own artboard rectangle."),
            raster);
    }

    /// <summary>"A4" when the page is A4, "Artboard" when it is infinite — and
    /// the preset's real name for everything in between.</summary>
    private string ArtboardLabel()
    {
        var p = _h.Page();
        if (p == null || p.PageSize == PageSizePreset.Infinite) return "Artboard";
        if (p.PageSize == PageSizePreset.Custom)
            return PageSizes.TryResolve(p, out double w, out double h) ? $"{Math.Round(w)}x{Math.Round(h)}" : "Artboard";
        return PageSizes.Find(p.PageSize)?.Name ?? "Artboard";
    }

    private Rect? ArtboardRect()
    {
        var p = _h.Page();
        if (p == null || !PageSizes.TryResolve(p, out double w, out double h)) return null;
        return new Rect(0, 0, w, h);
    }

    // ---- option availability -------------------------------------------
    private static bool ForcedBackground(Def d) => d.Id == Fmt.Jpg;

    private static (bool On, string? Why) BackgroundOption(Def d) => d.Kind switch
    {
        Kind.Raster when d.Id == Fmt.Jpg =>
            (false, "JPEG has no transparency, so the background is always written."),
        Kind.Raster => (true, null),
        Kind.Vector when d.Id == Fmt.Svg => (true, null),
        Kind.Vector => (false, "Quill's PDF writer always paints the page colour behind the paths."),
        Kind.Native => (false, "The native format stores the page's background as data; it is never dropped."),
        _ => (false, d.Disabled),
    };

    private static (bool On, string? Why) GridOption(Def d) => d.Kind switch
    {
        Kind.Raster => (true, null),
        Kind.Vector => (false, "Quill's vector page carries ink, images and text. The grid is drawn by the canvas and has no vector geometry to export."),
        Kind.Native => (false, "The native format stores the grid setting itself, so it is always kept."),
        _ => (false, d.Disabled),
    };

    // =====================================================================
    // Output size — computed from the SAME numbers the export uses, so the
    // Details block is a promise rather than a decoration.
    // =====================================================================
    private (int W, int H)? OutputSize(Def def)
    {
        var s = _h.Surface();
        if (_h.Page() == null) return null;
        if (def.Kind == Kind.Native) return null;

        if (def.Kind == Kind.Vector)
        {
            var c = s.ContentBoundsWorld();
            if (c == null) return null;
            return ((int)Math.Round(c.Value.Width + 56), (int)Math.Round(c.Value.Height + 56));
        }

        if (_region == Region.Screenshot)
            return ((int)Math.Round(s.ActualWidth * _scale), (int)Math.Round(s.ActualHeight * _scale));

        var r = RegionRect();
        if (r == null) return null;
        return ((int)Math.Round(r.Value.Width * _scale), (int)Math.Round(r.Value.Height * _scale));
    }

    private Rect? RegionRect()
    {
        var s = _h.Surface();
        switch (_region)
        {
            case Region.Drawing: return s.ContentBoundsWorld();
            case Region.Selection:
                var sel = s.SelectionBoundsWorld;
                return s.HasSelection && !sel.IsEmpty ? sel : null;
            case Region.Artboard: return ArtboardRect() ?? s.ContentBoundsWorld();
            default: return null;
        }
    }

    private string DetailNote(Def def) => def.Kind switch
    {
        Kind.Native => "The native bundle keeps the page verbatim — size, scale and region do not apply.",
        Kind.Vector => "Vector output has no pixel size of its own; the figure above is the page's own units.",
        Kind.None => def.Disabled ?? "",
        _ => "",
    };

    // =====================================================================
    // Doing it
    // =====================================================================
    private async Task RunAsync()
    {
        if (_busy) return;
        var def = Find(_fmt);
        var page = _h.Page();
        if (page == null || def.Disabled != null) return;

        _busy = true;
        Sync();
        try
        {
            var file = await _h.PickSave(def.Ext, def.TypeName);
            if (file == null) return;
            switch (def.Kind)
            {
                case Kind.Raster: await WriteRasterAsync(def, file); break;
                case Kind.Vector: await WriteVectorAsync(def, file); break;
                case Kind.Native: await WriteNativeAsync(file); break;
            }
        }
        catch (Exception ex)
        {
            _h.Status("Export failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            Sync();
        }
    }

    // ---- raster ---------------------------------------------------------
    private async Task WriteRasterAsync(Def def, StorageFile file)
    {
        var cap = await CaptureAsync();
        if (cap == null) { _h.Status("Could not capture the page. Try again."); return; }
        var (px, w, h) = cap.Value;

        if (def.Id == Fmt.PdfFlat)
        {
            await FileIO.WriteBytesAsync(file, PdfExporter.Create(new[] { new PdfPageImage(w, h, px) }));
            _h.Status($"Exported {file.Name} — {w} x {h} px, flattened.");
            return;
        }

        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(
            def.Id == Fmt.Jpg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8,
            def.Id == Fmt.Jpg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Premultiplied,
            (uint)w, (uint)h, 96, 96, px);
        await encoder.FlushAsync();
        _h.Status($"Exported {file.Name} — {w} x {h} px.");
    }

    /// <summary>Renders the surface at the resolution the Details block promised
    /// and crops it to the chosen region. Every switch it flips — the chromeless
    /// flag, the two Omit flags and the view itself — is restored in the finally,
    /// so an export can never leave the editor in export state.</summary>
    private async Task<(byte[] Px, int W, int H)?> CaptureAsync()
    {
        var s = _h.Surface();
        if (s.ActualWidth < 10 || s.ActualHeight < 10) return null;

        s.FlushTexts();
        var saved = s.GetView();
        s.ExportChromeless = true;
        s.ExportOmitBackground = !_includeBg && !ForcedBackground(Find(_fmt));
        s.ExportOmitGrid = !_includeGrid;
        try
        {
            Rect? world = _region == Region.Screenshot ? null : RegionRect();
            if (world is { } r && r.Width > 0 && r.Height > 0) s.FitToRect(r, 0);

            var view = s.GetView();
            // The crop box in SCREEN units, and the render scale that makes the
            // cropped result exactly the promised pixel size.
            Rect crop;
            double renderScale;
            if (world is { } wr)
            {
                double x = wr.X * view.Zoom + view.Offset.X;
                double y = wr.Y * view.Zoom + view.Offset.Y;
                crop = new Rect(x, y, wr.Width * view.Zoom, wr.Height * view.Zoom);
                renderScale = view.Zoom <= 0 ? _scale : _scale / view.Zoom;
            }
            else
            {
                crop = new Rect(0, 0, s.ActualWidth, s.ActualHeight);
                renderScale = _scale;
            }

            // A render target has a hard texture ceiling; clamp rather than fail,
            // and let the status line report what was actually written.
            double maxDim = Math.Max(s.ActualWidth, s.ActualHeight) * renderScale;
            if (maxDim > 8192) renderScale *= 8192 / maxDim;

            s.Refresh();
            await Task.Delay(140);   // let Win2D and the text layer land

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(s, (int)Math.Round(s.ActualWidth * renderScale),
                                     (int)Math.Round(s.ActualHeight * renderScale));
            var buffer = (await rtb.GetPixelsAsync()).ToArray();
            int fw = rtb.PixelWidth, fh = rtb.PixelHeight;

            double sx = fw / s.ActualWidth, sy = fh / s.ActualHeight;
            int cx = (int)Math.Clamp(Math.Round(crop.X * sx), 0, fw - 1);
            int cy = (int)Math.Clamp(Math.Round(crop.Y * sy), 0, fh - 1);
            int cw = (int)Math.Clamp(Math.Round(crop.Width * sx), 1, fw - cx);
            int ch = (int)Math.Clamp(Math.Round(crop.Height * sy), 1, fh - cy);
            if (cx == 0 && cy == 0 && cw == fw && ch == fh) return (buffer, fw, fh);
            return (Crop(buffer, fw, cx, cy, cw, ch), cw, ch);
        }
        finally
        {
            s.ExportChromeless = false;
            s.ExportOmitBackground = false;
            s.ExportOmitGrid = false;
            s.SetView(saved.Offset, saved.Zoom);
            s.Refresh();
        }
    }

    private static byte[] Crop(byte[] src, int srcW, int x, int y, int w, int h)
    {
        var dst = new byte[w * h * 4];
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(src, ((y + row) * srcW + x) * 4, dst, row * w * 4, w * 4);
        return dst;
    }

    // ---- vector ---------------------------------------------------------
    private async Task WriteVectorAsync(Def def, StorageFile file)
    {
        var s = _h.Surface();
        var vp = await s.BuildVectorPageAsync(28);
        if (vp == null) { _h.Status("Nothing to export."); return; }

        // SVG paints pg.Background as a full-bleed rect, so "none" is a real
        // transparent background rather than a white one pretending to be one.
        if (def.Id == Fmt.Svg && !_includeBg) vp = vp with { Background = "none" };

        if (def.Id == Fmt.Svg)
        {
            await FileIO.WriteTextAsync(file, HtmlSvgExporter.PageToSvg(vp));
            _h.Status($"Exported {file.Name} — a true vector image, crisp at any zoom.");
            return;
        }
        await FileIO.WriteBytesAsync(file, PdfExporter.CreateVector(new[] { vp }));
        _h.Status($"Exported {file.Name} — vector paths, selectable text.");
    }

    // ---- native ---------------------------------------------------------
    /// <summary>The page inside a one-notebook library, which is exactly what
    /// Quill's own importer reads back (LibraryStore.Merge), so a .quill file
    /// round-trips instead of being a private snapshot.</summary>
    private async Task WriteNativeAsync(StorageFile file)
    {
        var page = _h.Page();
        if (page == null) return;
        _h.Surface().FlushTexts();

        var sec = new Section { Name = _h.Section()?.Name ?? "Section" };
        sec.Pages.Add(page);
        var nb = new Notebook
        {
            Name = _h.Notebook()?.Name ?? page.Name,
            Color = _h.Notebook()?.Color ?? "#D97757",
        };
        nb.Sections.Add(sec);
        var bundle = new Library();
        bundle.Notebooks.Add(nb);

        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions { WriteIndented = true });
        await FileIO.WriteTextAsync(file, json);
        _h.Status($"Exported {file.Name} — Quill's own format; open it with Settings ▸ Import.");
    }
}
