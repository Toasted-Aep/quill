namespace Quill.Models;

// ===========================================================================
// THE LIVE PEN GESTURE'S DECISIONS (CONCEPTS-REF 58.11)
//
// InkSurface is WinUI and Win2D and cannot be linked by a console harness, so
// every DECISION it makes about a pen gesture that is still under the pen lives
// here, as plain functions over plain values, and tools/LayerRoundTrip runs
// them. InkSurface asks these and does what they answer; what the harness
// cannot see is that it asks them at every site, which was read and compiled.
// ===========================================================================

/// <summary>Every way a pen gesture that carries the OIL brush can end
/// (58.10.2's table, made total in 58.11). Each member is a site in
/// InkSurface that calls <c>EndOilGesture</c> with it.</summary>
public enum GestureEnd
{
    /// <summary>The pen lifts: CommitGesture from PointerReleased.</summary>
    Lift,
    /// <summary>The pointer is cancelled or loses capture: CommitGesture from
    /// PointerCaptureLost / PointerCanceled.</summary>
    PointerLost,
    /// <summary>ResetGesture reached with the brush still live - a reset that
    /// is none of the named ends below.</summary>
    Reset,
    /// <summary>A second pointer takes the gesture over to PAN: a middle-mouse
    /// press, a mouse press in the Grab mouse mode, a press with the Pan tool.</summary>
    TakeoverPan,
    /// <summary>A second pointer takes it over to ERASE: the Eraser tool, or a
    /// pen's eraser end.</summary>
    TakeoverErase,
    /// <summary>A second pointer takes it over for anything else: a right-button
    /// or barrel lasso, a touch grab of the selection, the mouse modes, another
    /// pen stroke, rotate, free space.</summary>
    TakeoverOther,
    /// <summary>The tool is switched while the gesture is live (SetTool).</summary>
    ToolSwitch,
    /// <summary>The page is switched (LoadPage), before <c>_page</c> changes.</summary>
    PageSwitch,
    /// <summary>Undo pressed mid-stroke (58.11 ruling R4).</summary>
    Undo,
    /// <summary>Redo pressed mid-stroke (R4 extended; an ASSUMPTION the owner
    /// can overturn, 58.11.4).</summary>
    Redo,
    /// <summary>The window closes or the surface unloads (FlushPaint).</summary>
    Close,
    /// <summary>The graphics device is lost (CreateResources, NewDevice).</summary>
    DeviceLoss,
}

/// <summary>What ending the oil brush does with the wet scratch.</summary>
public enum OilEnd
{
    /// <summary>Not an end: the brush carries on. No <see cref="GestureEnd"/>
    /// answers this; it is what every takeover answered before 58.11, which is
    /// how a pan left the brush live and its scratch drawn on top.</summary>
    KeepPainting,
    /// <summary>Settle the scratch into the tiles, exactly as a lift does:
    /// one PaintTilesAction on the undo stack.</summary>
    Commit,
    /// <summary>R4: drop the scratch, take back whatever a mid-gesture flush
    /// already composited, and touch no history.</summary>
    Discard,
    /// <summary>The device is gone with the scratch on it: nothing can be
    /// committed or restored, so the brush is only disposed.</summary>
    Drop,
}

/// <summary>What an Undo or Redo request does.</summary>
public enum HistoryKeyOutcome
{
    /// <summary>Run the undo or redo, as always.</summary>
    RunHistory,
    /// <summary>R4: cancel the stroke under the pen and leave history alone.</summary>
    CancelStroke,
}

public static class GestureRules
{
    /// <summary>
    /// 58.11: THE GESTURE-END TABLE, total. Every <see cref="GestureEnd"/> ends
    /// the brush - none answers <see cref="OilEnd.KeepPainting"/> - so there is
    /// no way for a gesture to go while its brush stays live. Undo and Redo
    /// DISCARD (R4); a lost device DROPS; every other end COMMITS, as a lift
    /// does. InkSurface's <c>EndOilGesture</c> asks this, and returns at once
    /// when the brush is not live, which is what makes it end exactly once.
    /// </summary>
    public static OilEnd OilOutcome(GestureEnd why) => why switch
    {
        GestureEnd.Undo => OilEnd.Discard,
        GestureEnd.Redo => OilEnd.Discard,
        GestureEnd.DeviceLoss => OilEnd.Drop,
        GestureEnd.Lift => OilEnd.Commit,
        GestureEnd.PointerLost => OilEnd.Commit,
        GestureEnd.Reset => OilEnd.Commit,
        GestureEnd.TakeoverPan => OilEnd.Commit,
        GestureEnd.TakeoverErase => OilEnd.Commit,
        GestureEnd.TakeoverOther => OilEnd.Commit,
        GestureEnd.ToolSwitch => OilEnd.Commit,
        GestureEnd.PageSwitch => OilEnd.Commit,
        GestureEnd.Close => OilEnd.Commit,
        _ => OilEnd.Commit,
    };

    /// <summary>Whether ending the brush must also END THE PEN GESTURE that
    /// carried it (reset it and release capture). Not for a lift, a lost
    /// pointer or a reset - that gesture is already ending. For every other
    /// end, yes: otherwise the rest of the pointer stream, with oil no longer
    /// live, would draw and commit a VECTOR stroke. A takeover replaces the
    /// pen's gesture with the new pointer's.</summary>
    public static bool EndsPenGesture(GestureEnd why)
        => why is not (GestureEnd.Lift or GestureEnd.PointerLost or GestureEnd.Reset);

    /// <summary>Which takeover a press is, by the tool it routes to.</summary>
    public static GestureEnd TakeoverFor(ToolType tool) => tool switch
    {
        ToolType.Pan => GestureEnd.TakeoverPan,
        ToolType.Eraser => GestureEnd.TakeoverErase,
        _ => GestureEnd.TakeoverOther,
    };

    /// <summary>
    /// R4: is a pen or brush STROKE in progress - the thing an undo pressed now
    /// must cancel? A live oil brush always is. Otherwise: a pointer is down,
    /// its gesture is the PEN's (not a selection grab reached with the pen,
    /// which re-routes to the Mouse gesture), and it holds wet ink or a shape
    /// the hold has snapped it to. Every pen type - vector, ruler, oil.
    /// </summary>
    public static bool StrokeInProgress(bool pointerDown, bool penGesture, bool hasWet,
                                        bool shapeAdjust, bool oilActive)
        => oilActive || (pointerDown && penGesture && (hasWet || shapeAdjust));

    /// <summary>R4: what Undo (or Redo) does. With a stroke in progress it
    /// cancels that stroke and runs NO history - nothing pushed, nothing
    /// popped, the redo stack kept. Otherwise it runs, as always.</summary>
    public static HistoryKeyOutcome OnHistoryKey(bool strokeInProgress)
        => strokeInProgress ? HistoryKeyOutcome.CancelStroke : HistoryKeyOutcome.RunHistory;

    /// <summary>
    /// SHAPE RECOGNITION's hold: may holding the pen still turn the wet stroke
    /// into a shape now? Never while the OIL brush is live (58.11): an oil
    /// stroke is raster paint, not a squiggle, and snapping it made the lift
    /// commit BOTH a vector shape and the painted raster, with the brush still
    /// live after it (paint v2 design finding O2).
    /// </summary>
    public static bool HoldMaySnap(bool recognitionOn, bool penGesture, bool shapeAdjust,
                                   bool rulerMode, bool hasWet, bool oilActive)
        => recognitionOn && penGesture && !shapeAdjust && !rulerMode && hasWet && !oilActive;

    /// <summary>
    /// R1: does a press with the pen CREATE CONTENT ON THE ACTIVE LAYER? A
    /// vector stroke does (and a ruler stroke with any pen, oil included, is a
    /// vector stroke). Oil paint does not: paint is tiles outside the layer
    /// model, one height below every layer (§53, 58.2), so it lands on no layer
    /// and a hidden active layer does not hide it. So R1's gate is asked for
    /// the first and not the second.
    /// </summary>
    public static bool PenPressLandsOnLayer(PenType pen, bool rulerMode)
        => !(pen == PenType.Oil && !rulerMode);
}
