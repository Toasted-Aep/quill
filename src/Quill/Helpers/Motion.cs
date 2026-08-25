namespace Quill.Helpers;

/// <summary>
/// QUILL'S OWN MOTION, in one place.
///
/// <para>190 ms out and 130 ms back on a cubic-bezier (0.12, 0.9) to (0.2, 1.0)
/// are the app's menu open/close timings, from <c>MenuAnim.cs</c> (commit
/// 9d0d6cf, "Menu animations"). Anything in the app that eases should ease like
/// every flyout in it rather than inventing a duration, and the way that has
/// been done so far is to copy the three numbers and the solver into whichever
/// file needed them - <see cref="Controls.FullscreenChrome"/> did, and
/// CONCEPTS-REF 16.7 asks for the same curve again for the page fade. A third
/// copy is drift, so this is the copy and both callers read it.</para>
///
/// <para><b>The DURATIONS are shared; the CURVES are not, any more.</b> 190 / 130
/// is the app's tempo and everything keeps it. But a curve is chosen for what it
/// drives: a menu wants to be open before you look at it, and a page fade wants
/// to be watched. So <see cref="Ease"/> stays the menu curve, verbatim, and the
/// page fade reads <see cref="FadeEase"/> — which is what a visual pass measuring
/// the fade actually asked for. Nothing that opens or closes should reach for the
/// second one, and nothing that changes a colour over time should reach for the
/// first.</para>
/// </summary>
public static class Motion
{
    /// <summary>Milliseconds for the OUT direction — appearing, arriving,
    /// fading to grey.</summary>
    public const double OpenMs = 190;

    /// <summary>Milliseconds for the BACK direction. Shorter on purpose: a thing
    /// leaving should not hold the eye as long as a thing arriving.</summary>
    public const double CloseMs = 130;

    /// <summary>The app's open curve — cubic-bezier (0.12, 0.9) to (0.2, 1.0).
    ///
    /// <para>Solved by BISECTION because a cubic Bezier's x is not invertible in
    /// closed form; 18 halvings resolve x to about 4e-6, which is far under a
    /// pixel of anything this drives and far under one step of an 8-bit colour
    /// channel, which is what CONCEPTS-REF 16.7's page fade uses it for.</para>
    ///
    /// <para><b>One curve serves both directions</b>, and that is deliberate
    /// rather than lazy: a tween that can reverse mid-flight — the page fade
    /// reverses whenever a selection is dropped before the fade finishes —
    /// stays continuous across the turn only if the same easing maps t in both
    /// directions. Two curves would make Ease(t) jump at the moment of
    /// reversal.</para></summary>
    public static double Ease(double t) => Bezier(t, 0.12, 0.9, 0.2, 1.0);

    /// <summary>The page fade's curve — cubic-bezier (0.4, 0.3) to (0.6, 0.7).
    /// NOT <see cref="Ease"/>, and the difference is the whole point.
    ///
    /// <para><b>Why the menu curve was wrong here.</b> (0.12, 0.9) rises almost
    /// vertically out of zero: it is built to make a flyout feel already-open by
    /// the time the eye finds it. Driving a COLOUR with it does the same thing to
    /// the colour, and measurement said so — 84 % of the way to grey a quarter of
    /// the way through the 190 ms in, and on the 130 ms out only 3.4 % of the
    /// page's own colour back at the halfway mark, the rest arriving in a snap
    /// over the last ~20 ms. Both directions read as an instant change with a
    /// long dead tail, which is the opposite of "slowly turn grey".</para>
    ///
    /// <para><b>What this one is.</b> Symmetric about (0.5, 0.5) and never far
    /// from the diagonal: 22 % / 50 % / 78 % at the quarter, half and three
    /// quarter marks, against linear's 25 / 50 / 75. So the grey arrives and
    /// leaves at a rate the eye reads as steady, while the two control points
    /// still take the corners off the start and the stop so it does not begin or
    /// end with a jolt. The DURATIONS are unchanged — this is 190 ms in and
    /// 130 ms out, same as everything else; only the shape between them moved.
    /// </para>
    ///
    /// <para><b>Symmetry is load-bearing, for the same reason
    /// <see cref="Ease"/> is used in both directions.</b> The fade reverses
    /// mid-flight whenever a selection is dropped before it finishes, and the
    /// thing being animated is a single scalar that walks toward 1 or toward 0.
    /// One curve mapping that scalar keeps the colour continuous across the turn;
    /// a curve that was also symmetric makes the in and the out read as the same
    /// motion run backwards, which is what "even in both directions" asks
    /// for.</para></summary>
    public static double FadeEase(double t) => Bezier(t, 0.4, 0.3, 0.6, 0.7);

    /// <summary>The solver both curves above share.
    ///
    /// <para>BISECTION, because a cubic Bezier's x is not invertible in closed
    /// form; 18 halvings resolve x to about 4e-6, which is far under a pixel of
    /// anything this drives and far under one step of an 8-bit colour channel,
    /// which is what CONCEPTS-REF 16.7's page fade uses it for.</para></summary>
    private static double Bezier(double t, double x1, double y1, double x2, double y2)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        static double Bez(double u, double p1, double p2)
        {
            double m = 1 - u;
            return (3 * m * m * u * p1) + (3 * m * u * u * p2) + (u * u * u);
        }
        double lo = 0, hi = 1, u = t;
        for (int i = 0; i < 18; i++)
        {
            u = (lo + hi) / 2;
            if (Bez(u, x1, x2) < t) lo = u; else hi = u;
        }
        return Bez(u, y1, y2);
    }

    /// <summary>One frame of a hand-pumped tween: move <paramref name="t"/>
    /// toward <paramref name="target"/> by however much wall time has passed.
    ///
    /// <para>A frame that took absurdly long — a breakpoint, a stalled GPU —
    /// must not teleport whatever this drives, so the elapsed time is clamped to
    /// about four frames' worth before it is used.</para></summary>
    public static double Step(double t, double target, double elapsedMs, double durationMs)
    {
        double step = System.Math.Clamp(elapsedMs, 0, 64) / durationMs;
        return System.Math.Abs(target - t) <= step
            ? target
            : t + System.Math.Sign(target - t) * step;
    }
}
