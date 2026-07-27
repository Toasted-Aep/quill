using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Shape = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Helpers;

/// <summary>
/// The single source of Quill's own icon geometry (UI-SPEC-V3 A.8: "dial icons
/// differ from the top-bar icons — they must match").
///
/// Before this file the app carried two sets: the top bar mixed Segoe Fluent
/// FontIcon glyphs with inline XAML PathIcon data, while the radial dial drew a
/// second, hand-written vector set. They drifted, and the dial's pen and text
/// marks looked nothing like the buttons they replaced. Everything now comes
/// from the constants below — <see cref="BindTopBar"/> pushes them into the top
/// bar's PathIcons at startup and <see cref="ToolWheel"/> reads the very same
/// strings — so there is exactly one place to change a mark.
///
/// All geometry is authored on the app's 24x24 grid, filled (never stroked)
/// unless a mark IS a stroke, and never a font glyph or an emoji.
/// </summary>
public static class Icons
{
    // ---- tools -----------------------------------------------------------

    /// Pen: the app's own ballpoint silhouette, shared with the pen chips.
    public const string Pen =
        "M14.8 6.4 a1.8 1.8 0 0 1 2.6 2.6 L10.8 15.6 8.2 13 Z M8.2 13 10.8 15.6 4.8 19 Z " +
        "M16.6 4.2 18.6 6.2 19.8 5 A1.7 1.7 0 0 0 17.8 3 Z";

    /// Text: a serif "A" on its baseline, drawn rather than borrowed from a font.
    public const string Text =
        "M11.1 3.5 H12.9 L19 20.5 H16.9 L15.2 15.6 H8.8 L7.1 20.5 H5 Z M9.4 13.8 H14.6 L12 6.4 Z";

    /// Selection: the marching-ants ring with a cursor, as shipped in the top bar.
    public const string Select =
        "M6.23 12.96 L5.60 14.21 L8.01 15.43 L8.64 14.18 Z M4.17 9.77 L2.90 10.36 L4.04 12.81 L5.31 12.22 Z " +
        "M5.31 6.78 L4.04 6.19 L2.90 8.64 L4.17 9.23 Z M8.64 4.82 L8.01 3.57 L5.60 4.79 L6.23 6.04 Z " +
        "M13.35 4.40 L13.35 3.00 L10.65 3.00 L10.65 4.40 Z M17.77 6.04 L18.40 4.79 L15.99 3.57 L15.36 4.82 Z " +
        "M19.83 9.23 L21.10 8.64 L19.96 6.19 L18.69 6.78 Z M18.69 12.22 L19.96 12.81 L21.10 10.36 L19.83 9.77 Z " +
        "M15.36 14.18 L15.99 15.43 L18.40 14.21 L17.77 12.96 Z M10.5 15.6 a1.5 1.5 0 1 0 3 0 a1.5 1.5 0 1 0 -3 0 Z " +
        "M11.9 16.9 C11.9 18.3 10.2 18.6 9.3 19.4 C8.5 20.1 8.6 21.1 9.3 21.8 L7.9 22.2 C6.9 21.1 7.1 19.6 8.3 18.7 " +
        "C9.4 17.9 10.4 17.8 10.4 16.8 Z";

    /// Insert free space: two content rules pushed apart by a double arrow.
    public const string FreeSpace =
        "M3 4.4 H21 V6 H3 Z M3 18 H21 V19.6 H3 Z M12 7.2 15 10.6 13 10.6 13 13.4 15 13.4 12 16.8 9 13.4 " +
        "11 13.4 11 10.6 9 10.6 Z";

    /// Eraser: the wedge block with its worn corner.
    public const string Eraser =
        "M13.6 3.5 20.5 10.4 11.4 19.5 H20.5 V21.5 H7.5 L3.5 17.5 A2 2 0 0 1 3.5 14.7 Z " +
        "M6.3 14.7 11.4 19.8 14.2 17 9.1 11.9 Z";

    /// Fill (paint bucket + drip). New mark for the fill slot the spec asks for.
    public const string Fill =
        "M9.55 2.0 L7.9 3.65 L9.9 5.65 L2.95 12.6 A2.0 2.0 0 0 0 2.95 15.4 L8.6 21.05 " +
        "A2.0 2.0 0 0 0 11.4 21.05 L19.3 13.15 Z " +
        "M20.8 15.1 C21.9 16.7 22.5 17.85 22.5 18.65 A1.75 1.75 0 0 1 19.0 18.65 C19.0 17.85 19.6 16.7 20.8 15.1 Z";

    // ---- commands --------------------------------------------------------

    /// Undo: a curved arrow doubling back. Redo is this mark mirrored, so the
    /// pair can never drift apart.
    public const string Undo =
        "M8.6 3.6 L1.8 9.6 L8.6 15.6 V11.4 H13.8 A4.2 4.2 0 1 1 13.8 19.8 H8.6 V22.4 H13.8 " +
        "A6.8 6.8 0 1 0 13.8 8.8 H8.6 Z";

    /// Mouse mode: the arrow cursor.
    public const string Mouse = "M6 2.6 L6 19.8 L10.3 15.8 L13.1 21.6 L15.8 20.3 L13 14.6 L18.7 14.3 Z";

    // ---- the three setting arcs -----------------------------------------

    /// Size: three rules, thick to thin — the shipped weight mark.
    public const string Size = "M4 6.2 H20 V9.4 H4 Z M4 11.6 H20 V13.6 H4 Z M4 15.8 H20 V16.9 H4 Z";

    /// Opacity: a disc whose right half is solid and whose left half is an open
    /// ring — coverage. F1 (nonzero) so the reversed inner circle cuts the hole
    /// and the half-disc still fills it.
    public const string Opacity =
        "F1 M12 2.6 A9.4 9.4 0 0 1 12 21.4 A9.4 9.4 0 0 1 12 2.6 Z " +
        "M12 4.7 A7.3 7.3 0 0 0 12 19.3 A7.3 7.3 0 0 0 12 4.7 Z " +
        "M12 4.7 A7.3 7.3 0 0 1 12 19.3 Z";

    /// Smoothness: a jagged trace on the left resolving into a smooth wave on
    /// the right. Stroked, because that is what the mark IS.
    public const string Smoothness =
        "M2 17.2 L4.6 8.2 L6.6 15.8 L8.6 7.4 L10.8 14.2 C12.9 14.2 13.3 8.4 15.7 8.4 C18.1 8.4 18.6 15 22 15";

    /// <summary>Tool tag (the same strings ToolType uses) to its mark.</summary>
    public static string Tool(string tag) => tag switch
    {
        "Pen" => Pen,
        "Text" => Text,
        "Select" => Select,
        "Eraser" => Eraser,
        "FreeSpace" => FreeSpace,
        "Fill" => Fill,
        _ => Pen,
    };

    // ---- factories -------------------------------------------------------

    /// <summary>Parses path mini-language into a Geometry. XamlReader is the only
    /// parser WinUI exposes; the Data is detached afterwards because a Geometry
    /// cannot be parented to two Paths at once.</summary>
    public static Geometry Geo(string data)
    {
        var p = (Shape)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "'/>");
        var geo = p.Data;
        p.Data = null;
        return geo!;
    }

    /// <summary>A filled mark, centred in a size x size box. Stretch.Uniform plus
    /// an explicit box is what keeps a mark centred inside a dial sector: without
    /// it a Path sizes to its geometry EXTENT, which is off-centre for every mark
    /// whose ink does not touch all four edges of the 24 grid (UI-SPEC-V3 A.7).</summary>
    public static Shape? Filled(string data, Color fill, double size, bool mirror = false)
    {
        try
        {
            var p = new Shape
            {
                Data = Geo(data),
                Fill = new SolidColorBrush(fill),
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (mirror)
            {
                p.RenderTransformOrigin = new Point(0.5, 0.5);
                p.RenderTransform = new ScaleTransform { ScaleX = -1 };
            }
            return p;
        }
        catch { return null; }
    }

    /// <summary>A stroked mark, for the marks that are genuinely a line.</summary>
    public static Shape? Stroked(string data, Color colour, double size, double thickness)
    {
        try
        {
            return new Shape
            {
                Data = Geo(data),
                Stroke = new SolidColorBrush(colour),
                StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        catch { return null; }
    }

    /// <summary>Pushes the canonical marks into the top bar's PathIcons at
    /// startup. One call keeps MainWindow's footprint to a single line while
    /// guaranteeing the bar and the dial draw the same geometry.</summary>
    public static void BindTopBar(PathIcon? pen, PathIcon? text, PathIcon? select,
                                  PathIcon? space, PathIcon? undo, PathIcon? redo)
    {
        void Set(PathIcon? icon, string data, bool mirror = false)
        {
            if (icon == null) return;
            try
            {
                icon.Data = Geo(data);
                icon.Width = 24;
                icon.Height = 24;
                if (!mirror) return;
                icon.RenderTransformOrigin = new Point(0.5, 0.5);
                icon.RenderTransform = new ScaleTransform { ScaleX = -1 };
            }
            catch { }
        }
        Set(pen, Pen);
        Set(text, Text);
        Set(select, Select);
        Set(space, FreeSpace);
        Set(undo, Undo);
        Set(redo, Undo, mirror: true);
    }
}
