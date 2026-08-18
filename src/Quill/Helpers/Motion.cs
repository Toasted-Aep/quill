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
    public static double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        const double X1 = 0.12, Y1 = 0.9, X2 = 0.2, Y2 = 1.0;
        static double Bez(double u, double p1, double p2)
        {
            double m = 1 - u;
            return (3 * m * m * u * p1) + (3 * m * u * u * p2) + (u * u * u);
        }
        double lo = 0, hi = 1, u = t;
        for (int i = 0; i < 18; i++)
        {
            u = (lo + hi) / 2;
            if (Bez(u, X1, X2) < t) lo = u; else hi = u;
        }
        return Bez(u, Y1, Y2);
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
