// =========================================================================
// TEMPORARY — Phase 1a substrate proof (OILPAINT-SPEC §9).
//
// "Prove the substrate with a single hard-coded tile of flat red before
// writing EmitDab." This file is that scaffolding and nothing else: a
// hard-coded stamp, a Ctrl+Shift+F9 trigger, and an env-var-driven timeline
// that walks the view through pan / zoom / tile-seam / ink-order states and
// hand-shakes with an external capture script.
//
// TO REMOVE: delete this file and the single `PaintProbeInit();` line in the
// InkSurface constructor. Nothing in the production paint path references it.
// =========================================================================
using System.Numerics;
using Quill.Models;
using Quill.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Quill.Controls;

public sealed partial class InkSurface
{
    // Deliberately straddles the tile boundary at world 512 on BOTH axes, so a
    // single stamp exercises four tiles and any seam error shows immediately.
    private static readonly Rect ProbePaintRect = new(400, 400, 300, 300);

    private string? _probeDir;
    private string _probeMode = "full";
    private int _probeStep;
    private string? _probeWaitFor;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _probeTimer;

    private void PaintProbeInit()
    {
        var stamp = new KeyboardAccelerator
        {
            Key = VirtualKey.F9,
            Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift
        };
        stamp.Invoked += (_, a) => { a.Handled = true; DebugStampPaint(ProbePaintRect, 0); };
        KeyboardAccelerators.Add(stamp);

        _probeDir = Environment.GetEnvironmentVariable("QUILL_PAINT_PROBE");
        if (string.IsNullOrEmpty(_probeDir)) return;
        _probeMode = Environment.GetEnvironmentVariable("QUILL_PAINT_PROBE_MODE") ?? "full";
        _probePrefix = Environment.GetEnvironmentVariable("QUILL_PAINT_PROBE_TAG") ?? "09-reloaded";
        Loaded += (_, _) =>
        {
            _probeTimer = DispatcherQueue.CreateTimer();
            _probeTimer.Interval = TimeSpan.FromMilliseconds(200);
            _probeTimer.IsRepeating = true;
            _probeTimer.Tick += (_, _) => ProbeTick();
            _probeTimer.Start();
        };
    }

    /// <summary>Stamps a hard-coded flat-colour square straight into the tile
    /// store. Variant 1 shifts colour and position so a second write is visibly
    /// distinguishable from the first.</summary>
    public void DebugStampPaint(Rect r, int variant)
    {
        if (_page == null) return;
        var store = EnsurePaintStore(_page);
        var body = variant == 0 ? Color.FromArgb(255, 214, 40, 24) : Color.FromArgb(255, 30, 90, 210);
        store.PaintWorld(_canvas, r, (ds, _) =>
        {
            ds.FillRectangle(r, body);
            // border + centre cross: a world-position error, a seam gap and a
            // doubled blit are each obvious at a glance
            ds.DrawRectangle(new Rect(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12),
                             Color.FromArgb(255, 16, 16, 16), 4f);
            float cx = (float)(r.X + r.Width / 2), cy = (float)(r.Y + r.Height / 2);
            var white = Color.FromArgb(255, 255, 255, 255);
            ds.DrawLine(cx - 40, cy, cx + 40, cy, white, 3f);
            ds.DrawLine(cx, cy - 40, cx, cy + 40, white, 3f);
        });
        // a flat height slab under the stamp — enough to light later, and enough
        // to prove the 16F -> U16 -> 16F round trip through .qtile
        store.PaintWorldHeight(_canvas, r, (ds, _) =>
        {
            const byte h = 90;   // ~0.35 normalised
            ds.FillRectangle(r, Color.FromArgb(h, h, h, h));
        });
        _page.HasPaint = true;
        ContentChanged?.Invoke();   // persists HasPaint; the spatial index is untouched
        _canvas.Invalidate();
    }

    // ---- the scripted timeline -------------------------------------------

    private void ProbeTick()
    {
        if (_page == null || ActualWidth < 100 || _probeDir == null) return;
        // hand-shake: hold until the capture script acknowledges the last step
        if (_probeWaitFor != null)
        {
            if (!File.Exists(Path.Combine(_probeDir, _probeWaitFor + ".ok"))) return;
            _probeWaitFor = null;
        }

        switch (_probeMode)
        {
            case "full": ProbeFull(); break;
            case "kill": ProbeKill(); break;
            default: ProbeReload(); break;
        }
    }

    private void ProbeFull()
    {
        switch (_probeStep++)
        {
            case 0: SetProbeView(1f, -300, -300); break;
            case 1: DebugStampPaint(ProbePaintRect, 0); break;
            case 2: Signal("01-stamped-zoom1.00"); break;
            // (b) pan: the square must travel with the page, not with the screen
            case 3: SetProbeView(1f, -437, -391); break;
            case 4: Signal("02-panned-zoom1.00"); break;
            // (b) zoom out and in: the transform-composition test
            case 5: SetProbeView(0.45f, -60, -60); break;
            case 6: Signal("03-zoomout-0.45"); break;
            case 7: SetProbeView(2.25f, -820, -820); break;
            case 8: Signal("04-zoomin-2.25"); break;
            // (c) straddle the CanvasVirtualControl's own 512px tile seams:
            // at zoom 1 with offset 0 the square covers screen 400..700, which
            // crosses the seam at 512 on both axes
            case 9: SetProbeView(1f, 0, 0); break;
            case 10: Signal("05-cvc-tile-seam"); break;
            // (d) order: a thick shape under the paint, a pen stroke over it
            case 11: AddOrderProbeContent(); break;
            case 12: Signal("06-shape-under-ink-over"); break;
            case 13: Signal("07-done"); break;
            default: _probeTimer?.Stop(); break;
        }
    }

    private void ProbeReload()
    {
        switch (_probeStep++)
        {
            // identical view to step 01, so the reloaded capture can be diffed
            // against the freshly stamped one pixel for pixel
            case 0: SetProbeView(1f, -300, -300); break;
            case 1: case 2: case 3: break;   // slack for the async tile load
            case 4: Signal($"{_probePrefix}-{_paint?.TileCount ?? 0}tiles"); break;
            case 5: Signal("07-done"); break;
            default: _probeTimer?.Stop(); break;
        }
    }

    /// <summary>Kills the process WHILE the tile writer is in flight. Nothing
    /// short of this exercises the case the .bak exists for.</summary>
    private void ProbeKill()
    {
        switch (_probeStep++)
        {
            case 0: SetProbeView(1f, -300, -300); break;
            // overlaps all four tiles run 1 already wrote, so the writer is
            // rotating real .bak files when it dies
            case 1: DebugStampPaint(new Rect(450, 450, 400, 400), 1); break;
            case 2: _paint?.FlushBlocking(); break;   // generation 2 lands; .bak = generation 1
            case 3: DebugStampPaint(new Rect(450, 450, 400, 400), 0); break;
            case 4:
                _paint?.BeginFlush();                // generation 3 write starts NOW
                Signal("08-prekill");                // the script kills us mid-flush
                break;
            default: break;
        }
    }

    private string _probePrefix = "09-reloaded";

    private void SetProbeView(float zoom, float ox, float oy)
    {
        ViewZoom = zoom;
        ViewOffset = new Vector2(ox, oy);
        OnViewChanged();
        _canvas.Invalidate();
    }

    /// <summary>A thick-bordered rect that runs under the painted square and a
    /// pen stroke that runs over it. Added straight to the page WITHOUT
    /// ContentChanged so the probe never persists them.</summary>
    private void AddOrderProbeContent()
    {
        if (_page == null) return;
        _page.Shapes.Add(new ShapeElement
        {
            Kind = ShapeKind.Rect, X = 330, Y = 470, W = 440, H = 160,
            Color = "#1E7F3C", Size = 14f
        });
        var stroke = new PenStroke { Pen = PenType.Standard, Color = "#0B1FA8", Size = 9f, Sens = 0f };
        for (int i = 0; i <= 40; i++)
            stroke.Points.Add(new StrokePoint
            {
                X = 350f + i * 10f,
                Y = 470f + MathF.Sin(i * 0.35f) * 55f + 90f,
                Pressure = 0.8f
            });
        _page.Strokes.Add(stroke);
        _canvas.Invalidate();
    }

    private void Signal(string name)
    {
        if (_probeDir == null) return;
        try
        {
            Directory.CreateDirectory(_probeDir);
            var mem = PaintMemory;
            File.WriteAllText(Path.Combine(_probeDir, name + ".ready"),
                $"zoom={ViewZoom:F2} offset={ViewOffset.X:F0},{ViewOffset.Y:F0} " +
                $"tiles={mem.Tiles} gpuBytes={mem.Bytes} page={_page?.Id:N}");
            _probeWaitFor = name;
        }
        catch { }
    }
}
