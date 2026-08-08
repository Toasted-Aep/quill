using System.Globalization;
using Quill.Models;

namespace Quill.Services;

/// <summary>
/// The type scale of Quill's floating panels — CONCEPTS-REF-2026-08-07 §10.5
/// item 22: <i>"Panel font is too big. Reduce it, and add a developer setting
/// that allows changing the font of specific pages."</i>
///
/// <para>Both halves of that are one mechanism. §3.1 fixes the RATIOS a panel's
/// type has — a 30 DIP section heading over a 17 DIP sub-heading over a 15 DIP
/// caption — and those ratios are what make the reference read the way it does.
/// So the panels do not hard-code smaller numbers; they multiply §3.1's numbers
/// by a single factor, which defaults to <see cref="Default"/> and can be
/// overridden per PAGE (Workspace, Interaction, Brushes, Objects, Export).</para>
///
/// <para>A page name that has no override falls through to the library-wide
/// scale, and a library-wide scale that has never been set falls through to
/// <see cref="Default"/> — so a settings file written before this existed comes
/// up at the new, smaller size rather than at the old one.</para>
/// </summary>
public static class PanelFonts
{
    /// <summary>The new default. Measurably smaller than §3.1's literal numbers
    /// without collapsing the scale: a 30 DIP heading lands at 25.5, a 15 DIP
    /// caption at 12.8.</summary>
    public const double Default = 0.85;

    /// <summary>Below this a caption stops being readable and above it the panel
    /// is back where the user complained about it; the developer control clamps
    /// to the same window so a typo cannot make the panel unusable.</summary>
    public const double Min = 0.60, Max = 1.30;

    /// <summary>The pages a scale can be pinned to — and ONLY the pages that
    /// actually read it. The Objects library and the Export pane build their type
    /// through <c>ChromeUi</c> rather than through this, so listing them would be
    /// a developer control that does nothing; they join the list when they are
    /// moved onto the same scale.</summary>
    public static readonly string[] Pages =
    {
        "Workspace", "Interaction", "Brushes",
    };

    /// <summary>The multiplier for one page.</summary>
    public static double ScaleFor(Library? lib, string page)
    {
        if (lib == null) return Default;
        if (Override(lib, page) is double d) return Clamp(d);
        return Clamp(lib.PanelFontScale <= 0 ? Default : lib.PanelFontScale);
    }

    /// <summary>The page's own pinned scale, or null when it inherits.</summary>
    public static double? Override(Library? lib, string page)
    {
        if (lib == null) return null;
        foreach (var s in lib.PanelFontOverrides)
        {
            int i = s.IndexOf('=');
            if (i <= 0 || !string.Equals(s[..i], page, StringComparison.OrdinalIgnoreCase)) continue;
            return double.TryParse(s[(i + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? Clamp(v) : null;
        }
        return null;
    }

    /// <summary>Pins a page's scale, or clears it with null so the page inherits
    /// the library-wide one again.</summary>
    public static void SetOverride(Library lib, string page, double? scale)
    {
        lib.PanelFontOverrides.RemoveAll(s =>
        {
            int i = s.IndexOf('=');
            return i > 0 && string.Equals(s[..i], page, StringComparison.OrdinalIgnoreCase);
        });
        if (scale is double v)
            lib.PanelFontOverrides.Add(page + "=" + Clamp(v).ToString("0.##", CultureInfo.InvariantCulture));
    }

    public static double Clamp(double v) => Math.Clamp(v, Min, Max);
}
