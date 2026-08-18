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

    /// <summary>Undo, redesigned - reference 11.2 item 14, head re-authored
    /// for 16.6.
    ///
    /// <para>The mark above is a straight arrow that hooks round a loop, which
    /// at the 18 DIP the dial's centre buttons run at reads as a knot. This one
    /// is what an undo control actually is: a three-quarter ROTATION. A 284
    /// degree band of mid radius 8.15 and half-width 0.56 about (12, 12.3),
    /// swept from -52 to 232 degrees, with an open chevron head where it stops.
    /// Redo is this mirrored, exactly as before, so the pair still cannot drift
    /// apart.</para></summary>
    public const string UndoRound =
        // FILLED OUTLINE, not a stroke: the top bar draws these through
        // PathIcon, which fills its Data and cannot stroke, so one geometry has
        // to serve both PathIcon and Icons.Mark.
        //
        // The outline is FLATTENED to line segments. Caps built from `A`
        // commands bulge inward when the sweep flag is wrong and yield bowtie
        // geometry - that shipped once as a blob. Sampling the offset curve
        // removes the class of bug: no flags remain to get backwards.
        //
        // 16.6, "the arrow tip is longer on the outer side". The head was a
        // textbook isoceles chevron - both arms 4.9 long, both swept back 54
        // degrees - and still lopsided, because the shaft is an ARC. A chevron
        // pinned at the arc's END throws its outer tip further out than its
        // inner tip goes in, by exactly
        //
        //     r_out + r_in - 2R  =  1.233 units  =  1.08 px at SatSize 21
        //
        // which is the complaint, measured. Equalising it by bending the arms
        // to different angles (the obvious fix) reads WORSE - the eye reads a
        // chevron by its arm angles first - so the head stays a perfect mirror
        // about the shaft and is instead SLID ALONG THE RADIUS until the two
        // tips are equidistant from the ring: vertex radius 7.580 rather than
        // 8.150. Arms come down 4.9 -> 4.4 and the sweep 54 -> 52 degrees, the
        // smallest change that leaves the head legible at 21 DIP. Measured on
        // a 64x raster of the finished fill, caps and joins included:
        //
        //     ink reaches 3.227 past the outer edge, 3.226 inside the inner
        //     one - 0.001 units, a thousandth of a pixel at 21 DIP.
        //
        // F1 IS LOAD-BEARING. The head is one closed outline and the band is
        // another, wound the same way, so NONZERO unions them. The default
        // fill rule is even-odd, under which the old head's two overlapping arm
        // capsules XOR'd a white notch out of their own vertex.
        //
        // WRAPPING MATTERS HERE. This literal is split across lines, and a break
        // that lands between a coordinate's x and y - with no space carried
        // across the join - fuses them into a number that cannot parse, and the
        // whole mark silently renders BLANK. That shipped too. Breaks are now
        // only ever between whole commands, and each chunk keeps its trailing
        // space. scratchpad/verify_icons.py reassembles every literal and
        // re-parses it; run it after ANY edit here.
        //
        // Generated by scratchpad/gen_undo.py; regenerate rather than hand-edit.
        "F1 M17.36 5.44 L17.71 5.73 L18.05 6.03 L18.37 6.36 L18.67 6.7 L18.95 7.05 " +
        "L19.22 7.42 L19.46 7.8 L19.68 8.2 L19.89 8.61 L20.07 9.02 L20.23 9.45 " +
        "L20.37 9.88 L20.48 10.32 L20.57 10.77 L20.64 11.21 L20.69 11.67 L20.71 12.12 " +
        "L20.71 12.58 L20.68 13.03 L20.63 13.48 L20.56 13.93 L20.46 14.37 " +
        "L20.34 14.81 L20.2 15.24 L20.03 15.67 L19.85 16.08 L19.64 16.48 L19.41 16.88 " +
        "L19.16 17.26 L18.89 17.62 L18.61 17.98 L18.3 18.31 L17.98 18.63 L17.64 18.94 " +
        "L17.29 19.22 L16.92 19.49 L16.54 19.74 L16.14 19.96 L15.74 20.17 " +
        "L15.32 20.35 L14.9 20.51 L14.47 20.65 L14.03 20.77 L13.58 20.87 L13.13 20.94 " +
        "L12.68 20.98 L12.23 21.01 L11.77 21.01 L11.32 20.98 L10.87 20.94 " +
        "L10.42 20.87 L9.97 20.77 L9.53 20.65 L9.1 20.51 L8.68 20.35 L8.26 20.17 " +
        "L7.86 19.96 L7.46 19.74 L7.08 19.49 L6.71 19.22 L6.36 18.94 L6.02 18.63 " +
        "L5.7 18.31 L5.39 17.98 L5.11 17.62 L4.84 17.26 L4.59 16.88 L4.36 16.48 " +
        "L4.15 16.08 L3.97 15.67 L3.8 15.24 L3.66 14.81 L3.54 14.37 L3.44 13.93 " +
        "L3.37 13.48 L3.32 13.03 L3.29 12.58 L3.29 12.12 L3.31 11.67 L3.36 11.21 " +
        "L3.43 10.77 L3.52 10.32 L3.63 9.88 L3.77 9.45 L3.93 9.02 L4.11 8.61 " +
        "L4.32 8.2 L4.54 7.8 L4.78 7.42 L5.05 7.05 L5.33 6.7 L5.63 6.36 L5.95 6.03 " +
        "L6.29 5.73 L6.64 5.44 L6.79 5.35 L6.96 5.32 L7.14 5.34 L7.3 5.41 L7.42 5.53 " +
        "L7.51 5.69 L7.54 5.86 L7.52 6.03 L7.45 6.19 L7.33 6.32 L7.02 6.57 L6.73 6.84 " +
        "L6.45 7.12 L6.19 7.42 L5.94 7.73 L5.71 8.05 L5.5 8.38 L5.3 8.73 L5.13 9.08 " +
        "L4.97 9.44 L4.83 9.81 L4.71 10.19 L4.61 10.57 L4.53 10.96 L4.47 11.35 " +
        "L4.43 11.75 L4.41 12.14 L4.41 12.54 L4.44 12.94 L4.48 13.33 L4.54 13.72 " +
        "L4.63 14.11 L4.73 14.49 L4.86 14.86 L5 15.23 L5.16 15.59 L5.34 15.95 " +
        "L5.54 16.29 L5.76 16.62 L5.99 16.94 L6.24 17.25 L6.51 17.54 L6.79 17.82 " +
        "L7.09 18.08 L7.39 18.33 L7.71 18.56 L8.05 18.78 L8.39 18.98 L8.74 19.16 " +
        "L9.11 19.32 L9.47 19.46 L9.85 19.58 L10.23 19.68 L10.62 19.76 L11.01 19.83 " +
        "L11.41 19.87 L11.8 19.89 L12.2 19.89 L12.59 19.87 L12.99 19.83 L13.38 19.76 " +
        "L13.77 19.68 L14.15 19.58 L14.53 19.46 L14.89 19.32 L15.26 19.16 " +
        "L15.61 18.98 L15.95 18.78 L16.29 18.56 L16.61 18.33 L16.91 18.08 " +
        "L17.21 17.82 L17.49 17.54 L17.76 17.25 L18.01 16.94 L18.24 16.62 " +
        "L18.46 16.29 L18.66 15.95 L18.84 15.59 L19 15.23 L19.14 14.86 L19.27 14.49 " +
        "L19.37 14.11 L19.46 13.72 L19.52 13.33 L19.56 12.94 L19.59 12.54 " +
        "L19.59 12.14 L19.57 11.75 L19.53 11.35 L19.47 10.96 L19.39 10.57 " +
        "L19.29 10.19 L19.17 9.81 L19.03 9.44 L18.87 9.08 L18.7 8.73 L18.5 8.38 " +
        "L18.29 8.05 L18.06 7.73 L17.81 7.42 L17.55 7.12 L17.27 6.84 L16.98 6.57 " +
        "L16.67 6.32 L16.55 6.19 L16.48 6.03 L16.46 5.86 L16.49 5.69 L16.58 5.53 " +
        "L16.7 5.41 L16.86 5.34 L17.04 5.32 L17.21 5.35 Z M7.54 5.81 L7.61 5.84 " +
        "L7.67 5.88 L7.72 5.93 L7.77 5.98 L7.82 6.04 L7.85 6.11 L7.87 6.18 L7.89 6.25 " +
        "L7.89 6.33 L7.89 10.73 L7.87 10.9 L7.79 11.06 L7.66 11.18 L7.51 11.26 " +
        "L7.33 11.29 L7.16 11.26 L7 11.18 L6.88 11.06 L6.8 10.9 L6.77 10.73 " +
        "L6.77 6.33 L6.78 6.4 L6.79 6.47 L6.82 6.54 L6.85 6.61 L6.89 6.67 L6.94 6.73 " +
        "L7 6.77 L7.06 6.82 L7.13 6.85 L7.2 6.87 L2.93 5.81 L2.77 5.74 L2.63 5.62 " +
        "L2.54 5.47 L2.51 5.3 L2.52 5.13 L2.59 4.97 L2.7 4.83 L2.85 4.74 L3.02 4.7 " +
        "L3.2 4.72 L7.47 5.78 Z";

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

    /// <summary>Ruler (11.4 item 29): a straightedge tilted 20 degrees with
    /// three notches cut into its measuring edge. The tilt is the point - this
    /// tool's whole character is that it turns - and the notches are part of the
    /// OUTLINE rather than punched subpaths, so the mark survives being filled
    /// at any size instead of relying on an even-odd hole that closes up.</summary>
    public const string Ruler =
        "M 2.00 12.02 L 5.81 10.63 L 6.97 13.83 L 8.38 13.32 L 7.22 10.12 L 10.13 9.06 " +
        "L 11.30 12.26 L 12.70 11.74 L 11.54 8.55 L 14.45 7.49 L 15.62 10.68 L 17.03 10.17 " +
        "L 15.86 6.98 L 19.67 5.59 L 22.00 11.98 L 4.33 18.41 Z";

    /// <summary>Eyedropper (11.4 item 28): the same silhouette
    /// <c>ColorWheel.DrawEyedropper</c> paints - a bulb on a 45 degree shaft
    /// tapering to a point - expressed as a path so the dial, the pen row and
    /// the Brushes library all show the mark the wheel already uses.
    ///
    /// <para><c>F1</c> (nonzero) because the bulb OVERLAPS the shaft: under the
    /// default even-odd rule the join would punch a hole exactly where the two
    /// meet, which is the middle of the mark.</para></summary>
    public const string Eyedropper =
        "F1 M 3.20 20.80 L 4.80 15.40 L 13.00 7.20 L 16.60 10.80 L 8.40 19.00 Z " +
        "M 13.32 6.01 L 16.01 3.32 A 2.60 2.60 0 0 1 19.69 3.32 L 20.68 4.31 " +
        "A 2.60 2.60 0 0 1 20.68 7.99 L 17.99 10.68 A 2.60 2.60 0 0 1 14.31 10.68 " +
        "L 13.32 9.69 A 2.60 2.60 0 0 1 13.32 6.01 Z";

    /// <summary>Mix (10.8): two discs overlapping, drawn even-odd so the lens
    /// where they meet reads as a THIRD colour rather than as a blob. That lens
    /// is the whole tool - two inks going in, one coming out - and it is the one
    /// part of the mark that must not fill, which is why this keeps the default
    /// rule while the eyedropper above does not.</summary>
    public const string Mix =
        "M 2.10 12.00 A 6.60 6.60 0 1 0 15.30 12.00 A 6.60 6.60 0 1 0 2.10 12.00 Z " +
        "M 8.70 12.00 A 6.60 6.60 0 1 0 21.90 12.00 A 6.60 6.60 0 1 0 8.70 12.00 Z";

    /// <summary>Tool tag (the same strings ToolType uses) to its mark.</summary>
    public static string Tool(string tag) => tag switch
    {
        "Pen" => Pen,
        "Text" => Text,
        "Select" => Select,
        "Eraser" => Eraser,
        "FreeSpace" => FreeSpace,
        "Fill" => Fill,
        "Eyedropper" => Eyedropper,
        "Ruler" => Ruler,
        "Mix" => Mix,
        _ => Pen,
    };

    // ---- chrome: the two floating bars, the panels and the export pane ----
    // Everything below is authored on the same 24x24 grid as the marks above and
    // filled unless the mark IS a stroke. UI-SPEC-V3 I/J introduced a whole new
    // set of buttons (gallery, layers, precision, objects, zoom, tilt, lock,
    // import, export, settings) and they belong in this file, not in a second
    // private set inside the control that happens to draw them first.

    /// Notebook gallery: the approved notebook silhouette (spine + rule lines),
    /// identical to the minimal-UI notebook button so the two never drift.
    public const string Notebook =
        "M4.5 2.2 H17 C19 2.2 20.6 3.8 20.6 5.8 V18.2 C20.6 20.2 19 21.8 17 21.8 H4.5 Z " +
        "M7 2.2 H8.2 V21.8 H7 Z M10.4 7.2 H17.6 V8.7 H10.4 Z M10.4 11.2 H17.6 V12.7 H10.4 Z";

    /// Layers: the solid top plate over two thinner plates below it.
    public const string Layers =
        "M12 2.2 22.2 7.3 12 12.4 1.8 7.3 Z " +
        "M12 14.1 20.4 9.9 22.2 10.8 12 15.9 1.8 10.8 3.6 9.9 Z " +
        "M12 17.6 20.4 13.4 22.2 14.3 12 19.4 1.8 14.3 3.6 13.4 Z";

    /// Precision: a ranging reticle — ring, four ticks and a centre dot.
    public const string Precision =
        "M12 1.6 A10.4 10.4 0 1 1 11.99 1.6 Z M12 3.4 A8.6 8.6 0 1 0 12.01 3.4 Z " +
        "M11.2 5.2 H12.8 V9.8 H11.2 Z M11.2 14.2 H12.8 V18.8 H11.2 Z " +
        "M5.2 11.2 H9.8 V12.8 H5.2 Z M14.2 11.2 H18.8 V12.8 H14.2 Z " +
        "M12 10.4 a1.6 1.6 0 1 1 -0.01 0 Z";

    /// Objects: two overlapping outlined plates — a group of things on the page.
    public const string Objects =
        "M2.6 2.6 H14 V14 H2.6 Z M4.2 4.2 V12.4 H12.4 V4.2 Z " +
        "M10 10 H21.4 V21.4 H10 Z M11.6 11.6 V19.8 H19.8 V11.6 Z";

    /// Lock, shackle closed. Its open twin below shares the same body, so the
    /// two read as one control changing state rather than two icons.
    public const string LockClosed =
        "M12 2.2 A5.2 5.2 0 0 1 17.2 7.4 V10 H15.4 V7.4 A3.4 3.4 0 0 0 8.6 7.4 V10 H6.8 V7.4 A5.2 5.2 0 0 1 12 2.2 Z " +
        "M5.4 10 H18.6 A1.5 1.5 0 0 1 20.1 11.5 V20.3 A1.5 1.5 0 0 1 18.6 21.8 H5.4 A1.5 1.5 0 0 1 3.9 20.3 V11.5 A1.5 1.5 0 0 1 5.4 10 Z " +
        "M12 13.6 a1.8 1.8 0 1 1 -0.01 0 Z M11.2 15.8 H12.8 V18.8 H11.2 Z";

    public const string LockOpen =
        "M6.8 10 V6.6 A5.2 5.2 0 0 1 17.2 6.6 V7.8 H15.4 V6.6 A3.4 3.4 0 0 0 8.6 6.6 V10 Z " +
        "M5.4 10 H18.6 A1.5 1.5 0 0 1 20.1 11.5 V20.3 A1.5 1.5 0 0 1 18.6 21.8 H5.4 A1.5 1.5 0 0 1 3.9 20.3 V11.5 A1.5 1.5 0 0 1 5.4 10 Z " +
        "M12 13.6 a1.8 1.8 0 1 1 -0.01 0 Z M11.2 15.8 H12.8 V18.8 H11.2 Z";

    /// Import: an arrow dropping into an open tray.
    public const string Import =
        "M10.6 2.6 H13.4 V11.2 H16.8 L12 16.8 L7.2 11.2 H10.6 Z " +
        "M3.8 14.4 H6.6 V19 H17.4 V14.4 H20.2 V20.4 A1.4 1.4 0 0 1 18.8 21.8 H5.2 A1.4 1.4 0 0 1 3.8 20.4 Z";

    /// Export: the same tray, the arrow leaving it.
    public const string Export =
        "M10.6 16.4 H13.4 V7.8 H16.8 L12 2.2 L7.2 7.8 H10.6 Z " +
        "M3.8 14.4 H6.6 V19 H17.4 V14.4 H20.2 V20.4 A1.4 1.4 0 0 1 18.8 21.8 H5.2 A1.4 1.4 0 0 1 3.8 20.4 Z";

    /// Settings: an eight-tooth gear, generated on the 24 grid so the teeth are
    /// evenly spaced rather than eyeballed, with the hub cut by the fill rule.
    public const string Settings =
        "M 19.78 9.40 L 22.31 9.53 L 22.31 14.47 L 19.78 14.60 L 19.34 15.67 L 21.04 17.54 " +
        "L 17.54 21.04 L 15.67 19.34 L 14.60 19.78 L 14.47 22.31 L 9.53 22.31 L 9.40 19.78 " +
        "L 8.33 19.34 L 6.46 21.04 L 2.96 17.54 L 4.66 15.67 L 4.22 14.60 L 1.69 14.47 " +
        "L 1.69 9.53 L 4.22 9.40 L 4.66 8.33 L 2.96 6.46 L 6.46 2.96 L 8.33 4.66 L 9.40 4.22 " +
        "L 9.53 1.69 L 14.47 1.69 L 14.60 4.22 L 15.67 4.66 L 17.54 2.96 L 21.04 6.46 L 19.34 8.33 Z " +
        "M 7.60 12.00 A 4.4 4.4 0 1 0 16.40 12.00 A 4.4 4.4 0 1 0 7.60 12.00 Z";

    /// Zoom: the magnifier, for the live zoom readout.
    public const string Zoom =
        "M10.2 2.4 a7.8 7.8 0 1 1 -0.01 0 Z M10.2 4.4 a5.8 5.8 0 1 0 0.01 0 Z " +
        "M15.5 14.1 L21.6 20.2 L20.2 21.6 L14.1 15.5 Z";

    /// Tilt: a protractor angle — baseline, ray and the swept arc between them.
    /// Stroked, because the mark IS three lines.
    public const string Tilt = "M3 19 H21 M3 19 L17.6 8.4 M9.8 19 A6.9 6.9 0 0 0 12.9 13.2";

    /// Camera, for "take a photo" in the import menu.
    public const string Camera =
        "M9.2 3.4 H14.8 L16.1 5.6 H19.6 A1.7 1.7 0 0 1 21.3 7.3 V18.5 A1.7 1.7 0 0 1 19.6 20.2 " +
        "H4.4 A1.7 1.7 0 0 1 2.7 18.5 V7.3 A1.7 1.7 0 0 1 4.4 5.6 H7.9 Z " +
        "M12 8.4 a4.6 4.6 0 1 0 0.01 0 Z M12 10.2 a2.8 2.8 0 1 1 -0.01 0 Z";

    /// Clipboard, for "paste from clipboard".
    public const string Clipboard =
        "M9 2.2 H15 A1.3 1.3 0 0 1 16.3 3.5 V4.6 H17.9 A1.7 1.7 0 0 1 19.6 6.3 V20.1 " +
        "A1.7 1.7 0 0 1 17.9 21.8 H6.1 A1.7 1.7 0 0 1 4.4 20.1 V6.3 A1.7 1.7 0 0 1 6.1 4.6 H7.7 " +
        "V3.5 A1.3 1.3 0 0 1 9 2.2 Z " +
        "M6.2 6.4 V20 H17.8 V6.4 H16.3 V7.5 A1.3 1.3 0 0 1 15 8.8 H9 A1.3 1.3 0 0 1 7.7 7.5 V6.4 Z";

    /// A document, for "from file".
    public const string File =
        "M5.8 2.2 H14.2 L19.4 7.4 V21.8 H5.8 Z M7.4 3.8 V20.2 H17.8 V9 H12.6 V3.8 Z " +
        "M14.2 4.5 L17.1 7.4 H14.2 Z";

    /// Comment: the approved speech bubble with two rule lines.
    public const string Comment =
        "M4 3.5 H20 A1.5 1.5 0 0 1 21.5 5 V15 A1.5 1.5 0 0 1 20 16.5 H10 L5.5 20.5 V16.5 H4 " +
        "A1.5 1.5 0 0 1 2.5 15 V5 A1.5 1.5 0 0 1 4 3.5 Z " +
        "M6.5 8 H17.5 V9.4 H6.5 Z M6.5 11 H14 V12.4 H6.5 Z";

    /// Touch draw: the approved finger-and-cuff silhouette.
    public const string TouchDraw =
        "M10.9 4.1 a1.55 1.55 0 0 1 3.1 0 V11 h0.5 c3.2 0 5.7 2.3 5.7 5.2 C20.2 19.6 17.5 22 14.2 22 " +
        "h-1.3 c-1.8 0 -3.4 -0.85 -4.4 -2.3 L6.2 16.4 a1.55 1.55 0 0 1 2.5 -1.8 l1.2 1.65 Z " +
        "M2.1 8.9 C3.5 7.3 5.2 7.3 6.6 8.9 C7.5 9.9 8.3 10.1 9.3 9.6 L9.9 11.2 C8.4 12 6.9 11.6 5.7 10.3 " +
        "C4.8 9.3 4.2 9.4 3.4 10.2 Z";

    /// Insert shape: the approved outlined square + triangle pair.
    public const string Shape =
        "M3.1 4.8 H13.5 V15.2 H3.1 Z M4.7 6.4 V13.6 H11.9 V6.4 Z " +
        "M14.5 9 20.9 19.2 8.1 19.2 Z M11 17.6 H18 L14.5 12 Z";

    /// Edit history: a clock face with its two hands.
    public const string History =
        "M12 2.4 A9.6 9.6 0 1 1 11.99 2.4 Z M12 4.2 A7.8 7.8 0 1 0 12.01 4.2 Z " +
        "M11.2 6.6 H12.8 V12.1 L16.7 14.3 L15.9 15.7 L11.2 13.1 Z";

    /// Grid, for the precision panel. Nonzero so the crossings stay filled
    /// instead of punching a hole at every intersection.
    public const string Grid =
        "F1 M3 3.6 H21 V5 H3 Z M3 9.3 H21 V10.7 H3 Z M3 15 H21 V16.4 H3 Z " +
        "M3.6 3 H5 V21 H3.6 Z M9.3 3 H10.7 V21 H9.3 Z M15 3 H16.4 V21 H15 Z";

    /// Snap: a horseshoe magnet.
    public const string Snap =
        "M12 3.2 A7.4 7.4 0 0 1 19.4 10.6 V20.4 H14.6 V10.6 A2.6 2.6 0 0 0 9.4 10.6 V20.4 H4.6 V10.6 " +
        "A7.4 7.4 0 0 1 12 3.2 Z";

    /// Measure: the approved diagonal ruler with its tick cut-outs.
    public const string Measure =
        "M3 17.4 L17.4 3 21 6.6 6.6 21 Z M7.4 14.2 9 15.8 8.2 16.6 6.6 15 Z " +
        "M10.6 11 12.2 12.6 11.4 13.4 9.8 11.8 Z M13.8 7.8 15.4 9.4 14.6 10.2 13 8.6 Z";

    /// Guide: the two axes plus a dashed guide running off them.
    public const string Guide =
        "F1 M3 3 H4.6 V21 H3 Z M3 19.4 H21 V21 H3 Z " +
        "M7.4 6.2 H9.6 V7.8 H7.4 Z M11.2 6.2 H13.4 V7.8 H11.2 Z " +
        "M15 6.2 H17.2 V7.8 H15 Z M18.8 6.2 H21 V7.8 H18.8 Z";

    /// Recognition: a clean shape with the snap spark beside it.
    public const string Recognition =
        "M2.8 5 H15.2 V17.4 H2.8 Z M4.4 6.6 V15.8 H13.6 V6.6 Z " +
        "M18.6 2.2 L19.7 5.5 L23 6.6 L19.7 7.7 L18.6 11 L17.5 7.7 L14.2 6.6 L17.5 5.5 Z";

    /// Rename: the nib pencil over its baseline.
    public const string Rename =
        "M3.4 16.6 L15.2 4.8 L19.2 8.8 L7.4 20.6 H3.4 Z " +
        "M16.6 3.4 L18 2 A2.4 2.4 0 0 1 22 6 L20.6 7.4 Z";

    /// Chevron, for the collapsible rows. Stroked.
    public const string ChevronDown = "M5 8.5 L12 15.5 L19 8.5";

    // ---- fullscreen chrome (CONCEPTS-REF 15.3) ---------------------------
    // Both literals below are DELIBERATELY ONE LINE EACH, however wide that
    // makes them. A C# path literal split with `+` renders BLANK if a break
    // lands between a coordinate's x and y - the two numbers fuse into one that
    // cannot parse, and it still compiles - which has already shipped once here
    // (see UndoRound's remarks). A literal that is never broken cannot fuse.
    // Generated by scratchpad/gen_fullscreen_icons.py; regenerate rather than
    // hand-edit.

    /// <summary>The fullscreen bracket, `[ ]` in 15.3's transcription: four
    /// corner brackets 7 long and 2 thick, inset 3.5 from each edge of the grid.
    /// This is the mark the app's OWN top bar carries in fullscreen, and per
    /// 15.3 it stays visible the whole time - it is the ordinary way back out,
    /// while the hover strip is the shortcut.
    ///
    /// <para><c>F1</c> (nonzero) because each corner is TWO overlapping rects
    /// and the default even-odd rule would punch a hole at all four elbows -
    /// exactly where the mark's weight has to be.</para></summary>
    public const string Fullscreen =
        "F1 M3.5 3.5 H10.5 V5.5 H3.5 Z M3.5 3.5 H5.5 V10.5 H3.5 Z M13.5 3.5 H20.5 V5.5 H13.5 Z M18.5 3.5 H20.5 V10.5 H18.5 Z M3.5 18.5 H10.5 V20.5 H3.5 Z M3.5 13.5 H5.5 V20.5 H3.5 Z M13.5 18.5 H20.5 V20.5 H13.5 Z M18.5 13.5 H20.5 V20.5 H18.5 Z";

    /// <summary>EXIT FULLSCREEN, and 15.3 is explicit that this is NOT the
    /// windowed restore-down mark: two arrows pointing inward at each other
    /// diagonally. Tails in the top-right and bottom-left corners, heads meeting
    /// near the centre with a gap between them, so the pair reads as
    /// CONTRACTING rather than as a decoration.
    ///
    /// <para>A FILLED outline rather than a stroke, because the heads are solid
    /// triangles - and because <c>PathIcon</c> fills its Data and cannot stroke,
    /// so one geometry has to serve both PathIcon and <see cref="Mark"/>. Each
    /// arrow is a single seven-point polygon (tip, two barbs, two shaft
    /// shoulders, two tail corners), so nothing overlaps within an arrow;
    /// <c>F1</c> guards the pair against a rule change upstream.</para>
    ///
    /// <para><b>The weights were chosen at the size the mark DRAWS, not at a
    /// size where every version looks fine.</b> The hover strip runs its marks
    /// at 15 DIP, which is 15 px on a 100% display, and 11.23 already records
    /// what happens to a fine feature there: the palette's wells and the star's
    /// arms both lost their read long before they were too small to see. Four
    /// candidates were rasterised at 15 / 16 / 22 / 30 px, antialiased and
    /// through Stretch.Uniform's extent fit, which is what
    /// <see cref="Filled"/> actually applies. Shaft half-width 1.9 against head
    /// half-width 4.3 is the one that keeps the barbs distinguishable from the
    /// shaft at 15 px without the heads clubbing up at 30. Thinner (1.4) reads
    /// as a dashed diagonal at 15; thicker (2.1) merges head into shaft. The
    /// tails also pull in to 4.2 from the corner rather than 3.0: a shorter
    /// arrow is scaled UP more by the extent fit, so it lands heavier for the
    /// same nominal size.</para></summary>
    public const string FullscreenExit =
        "F1 M12.6 11.4 L13.24 4.68 L14.93 6.38 L18.46 2.86 L21.14 5.54 L17.62 9.07 L19.32 10.76 Z M11.4 12.6 L10.76 19.32 L9.07 17.62 L5.54 21.14 L2.86 18.46 L6.38 14.93 L4.68 13.24 Z";

    // ---- the selection presentation (CONCEPTS-REF 16.2 / 16.9) -----------
    // ONE presentation, three subjects: an attachment, a text box and a drawn
    // stroke all get the same bar, the same handles and the same bottom row, so
    // these marks are named for what they DO rather than for what they act on.
    //
    // Every literal below is ONE LINE, however wide that makes the file. A path
    // literal split with `+` renders BLANK if a break lands between a
    // coordinate's x and its y - the two numbers fuse into one that cannot
    // parse, and it still compiles - which has already shipped twice here (see
    // UndoRound and Fullscreen). A literal that is never broken cannot fuse.
    // Generated by scratchpad/gen_selection_icons.py; regenerate rather than
    // hand-edit, and run scratchpad/verify_icons.py after any change.
    //
    // THE PADLOCK IS NOT HERE. 16.2's bar wants one, and `LockClosed` above
    // already IS that mark; a second padlock is exactly the drift this file
    // exists to prevent.

    /// <summary>the wire folded back on itself twice, as an OUTLINE - PathIcon
    /// fills its Data and cannot stroke, so a mark that IS a wire has to be the
    /// wire's boundary. Extent x 4.93..19.07, y 2.50..21.50 on the 24 grid.</summary>
    public const string Paperclip =
        "M 4.93 8.06 L 4.93 10.43 L 4.93 12.81 L 4.93 15.18 L 4.93 17.6 L 4.98 18.18 L 5.12 18.78 L 5.36 19.35 L 5.68 19.88 L 6.08 20.35 L 6.55 20.75 L 7.08 21.07 L 7.65 21.31 L 8.25 21.45 L 8.87 21.5 L 9.49 21.45 L 10.09 21.31 L 10.66 21.07 L 11.19 20.75 L 11.66 20.35 L 12.06 19.88 L 12.38 19.35 L 12.62 18.78 L 12.76 18.18 L 12.81 17.6 L 12.81 15.4 L 12.81 13.24 L 12.81 11.08 L 12.81 8.92 L 12.81 6.81 L 12.83 6.45 L 12.91 6.15 L 13.03 5.86 L 13.19 5.59 L 13.39 5.35 L 13.63 5.15 L 13.9 4.98 L 14.19 4.86 L 14.49 4.79 L 14.81 4.77 L 15.12 4.79 L 15.42 4.86 L 15.71 4.98 L 15.98 5.15 L 16.22 5.35 L 16.42 5.59 L 16.59 5.86 L 16.71 6.15 L 16.78 6.45 L 16.8 6.81 L 16.8 9.25 L 16.8 11.73 L 16.8 14.21 L 16.8 16.7 L 16.86 17.05 L 17.02 17.36 L 17.27 17.61 L 17.59 17.77 L 17.94 17.83 L 18.29 17.77 L 18.6 17.61 L 18.85 17.36 L 19.02 17.05 L 19.07 16.7 L 19.07 14.21 L 19.07 11.73 L 19.07 9.25 L 19.07 6.72 L 19.02 6.1 L 18.86 5.45 L 18.61 4.83 L 18.26 4.26 L 17.82 3.75 L 17.31 3.31 L 16.74 2.96 L 16.12 2.71 L 15.47 2.55 L 14.81 2.5 L 14.14 2.55 L 13.49 2.71 L 12.87 2.96 L 12.3 3.31 L 11.79 3.75 L 11.36 4.26 L 11.01 4.83 L 10.75 5.45 L 10.6 6.1 L 10.54 6.72 L 10.54 8.92 L 10.54 11.08 L 10.54 13.24 L 10.54 15.4 L 10.54 17.52 L 10.52 17.82 L 10.46 18.08 L 10.36 18.32 L 10.22 18.54 L 10.05 18.74 L 9.85 18.91 L 9.63 19.05 L 9.39 19.15 L 9.13 19.21 L 8.87 19.23 L 8.61 19.21 L 8.35 19.15 L 8.11 19.05 L 7.89 18.91 L 7.69 18.74 L 7.52 18.54 L 7.38 18.32 L 7.28 18.08 L 7.22 17.82 L 7.2 17.52 L 7.2 15.18 L 7.2 12.81 L 7.2 10.43 L 7.2 8.06 L 7.14 7.71 L 6.98 7.39 L 6.73 7.14 L 6.41 6.98 L 6.06 6.93 L 5.71 6.98 L 5.4 7.14 L 5.15 7.39 L 4.98 7.71 L 4.93 8.06 Z";

    /// <summary>Duplicate: one sheet, and the CORNER of a second behind it. The
    /// one behind is an L rather than a whole square, so the pair cannot be read
    /// as <see cref="Objects"/>, which is literally two equal hollow squares and
    /// sits a few DIP away in the same app. Extent 2.50..21.50 both ways.</summary>
    public const string Duplicate =
        "M 7.54 7.54 L 21.5 7.54 L 21.5 21.5 L 7.54 21.5 Z M 9.48 9.48 L 19.55 9.48 L 19.55 19.55 L 9.48 19.55 Z M 2.5 2.5 L 16.46 2.5 L 16.46 4.45 L 4.45 4.45 L 4.45 16.46 L 2.5 16.46 Z";

    /// <summary>Waste bin: lid, squared handle, tapered hollow body, two rules.
    /// The rules sit INSIDE the body's punched hole, so under the default
    /// even-odd rule they are three levels deep - odd - and fill. Nothing
    /// overlaps anything, so no fill rule can turn this into a blob.</summary>
    public const string WasteBin =
        "M 3.31 5.33 L 20.69 5.33 L 20.69 7.15 L 3.31 7.15 Z M 9.17 2.5 L 14.83 2.5 L 14.83 5.33 L 13.11 5.33 L 13.11 4.32 L 10.89 4.32 L 10.89 5.33 L 9.17 5.33 Z M 5.33 8.56 L 18.67 8.56 L 17.56 21.5 L 6.44 21.5 Z M 7.05 10.28 L 16.95 10.28 L 16.04 19.78 L 7.96 19.78 Z M 10.18 12 L 11.49 12 L 11.49 18.06 L 10.18 18.06 Z M 12.51 12 L 13.82 12 L 13.82 18.06 L 12.51 18.06 Z";

    /// <summary>Flip horizontal: a dashed MIRROR LINE with a solid wing on one
    /// side and a hollow wing on the other - the solid is the object, the hollow
    /// is its image. Its vertical twin is generated from the same numbers
    /// through a quarter turn, so the two can never drift apart.</summary>
    public const string FlipHorizontal =
        "M 11.31 2.5 L 12.69 2.5 L 12.69 4.61 L 11.31 4.61 Z M 11.31 6.72 L 12.69 6.72 L 12.69 8.83 L 11.31 8.83 Z M 11.31 10.94 L 12.69 10.94 L 12.69 13.06 L 11.31 13.06 Z M 11.31 15.17 L 12.69 15.17 L 12.69 17.28 L 11.31 17.28 Z M 11.31 19.39 L 12.69 19.39 L 12.69 21.5 L 11.31 21.5 Z M 9.62 4.68 L 9.62 19.32 L 2.5 12 Z M 14.38 4.68 L 14.38 19.32 L 21.5 12 Z M 16.06 8.24 L 16.06 15.76 L 19.52 12 Z";

    /// <summary>Flip vertical - <see cref="FlipHorizontal"/> through a quarter turn.</summary>
    public const string FlipVertical =
        "M 2.5 11.31 L 2.5 12.69 L 4.61 12.69 L 4.61 11.31 Z M 6.72 11.31 L 6.72 12.69 L 8.83 12.69 L 8.83 11.31 Z M 10.94 11.31 L 10.94 12.69 L 13.06 12.69 L 13.06 11.31 Z M 15.17 11.31 L 15.17 12.69 L 17.28 12.69 L 17.28 11.31 Z M 19.39 11.31 L 19.39 12.69 L 21.5 12.69 L 21.5 11.31 Z M 4.68 9.62 L 19.32 9.62 L 12 2.5 Z M 4.68 14.38 L 19.32 14.38 L 12 21.5 Z M 8.24 16.06 L 15.76 16.06 L 12 19.52 Z";

    /// <summary>Rotate: a 285 degree band with a solid triangular head and a
    /// pivot dot. Deliberately NOT <see cref="UndoRound"/> - that is a thin
    /// round-capped ring with an OPEN chevron, and with the dial up the two sit
    /// within a few DIP of each other on screen. A filled band with a solid head
    /// and a pivot reads as "this object turns", not as "step back through
    /// history".
    ///
    /// <para><c>F1</c> (nonzero) because the head overlaps the band; even-odd
    /// would punch a hole exactly at the join.</para></summary>
    public const string Rotate =
        "F1 M 17.87 5.37 L 18.43 5.88 L 18.95 6.44 L 19.41 7.03 L 19.83 7.67 L 20.19 8.33 L 20.5 9.02 L 20.75 9.74 L 20.94 10.47 L 21.06 11.22 L 21.13 11.97 L 21.13 12.73 L 21.07 13.48 L 20.94 14.23 L 20.76 14.97 L 20.52 15.68 L 20.21 16.38 L 19.85 17.04 L 19.44 17.68 L 18.97 18.27 L 18.46 18.83 L 17.9 19.34 L 17.31 19.81 L 16.67 20.22 L 16.01 20.58 L 15.31 20.89 L 14.6 21.13 L 13.86 21.32 L 13.11 21.44 L 12.36 21.5 L 11.6 21.5 L 10.85 21.43 L 10.1 21.31 L 9.37 21.12 L 8.65 20.87 L 7.96 20.56 L 7.3 20.2 L 6.66 19.79 L 6.07 19.32 L 5.51 18.8 L 5 18.24 L 4.54 17.64 L 4.13 17.01 L 3.77 16.34 L 3.47 15.65 L 3.23 14.93 L 3.05 14.19 L 2.93 13.45 L 2.87 12.69 L 2.88 11.93 L 2.94 11.18 L 3.07 10.43 L 3.26 9.7 L 3.52 8.99 L 3.83 8.3 L 4.19 7.63 L 4.61 7 L 5.08 6.41 L 5.6 5.86 L 6.16 5.35 L 6.76 4.89 L 7.98 6.63 L 7.52 6.98 L 7.09 7.37 L 6.69 7.8 L 6.33 8.25 L 6.01 8.73 L 5.73 9.24 L 5.49 9.77 L 5.3 10.32 L 5.15 10.88 L 5.05 11.46 L 5 12.04 L 4.99 12.62 L 5.04 13.2 L 5.13 13.77 L 5.27 14.33 L 5.46 14.88 L 5.69 15.42 L 5.96 15.93 L 6.28 16.42 L 6.63 16.88 L 7.02 17.31 L 7.45 17.7 L 7.9 18.06 L 8.39 18.38 L 8.9 18.66 L 9.43 18.89 L 9.98 19.09 L 10.54 19.23 L 11.12 19.33 L 11.69 19.38 L 12.28 19.38 L 12.86 19.33 L 13.43 19.24 L 13.99 19.09 L 14.54 18.91 L 15.07 18.67 L 15.59 18.4 L 16.07 18.08 L 16.53 17.72 L 16.96 17.33 L 17.35 16.9 L 17.71 16.44 L 18.03 15.96 L 18.3 15.44 L 18.53 14.91 L 18.72 14.36 L 18.86 13.8 L 18.96 13.23 L 19.01 12.65 L 19 12.07 L 18.96 11.49 L 18.86 10.91 L 18.71 10.35 L 18.52 9.8 L 18.29 9.27 L 18.01 8.76 L 17.69 8.28 L 17.33 7.82 L 16.94 7.39 L 16.51 7 Z M 19.51 3.42 L 14.87 8.95 L 12.8 2.5 Z M 13.81 12.37 L 13.72 12.93 L 13.46 13.43 L 13.06 13.83 L 12.56 14.09 L 12 14.18 L 11.44 14.09 L 10.94 13.83 L 10.54 13.43 L 10.28 12.93 L 10.19 12.37 L 10.28 11.81 L 10.54 11.31 L 10.94 10.91 L 11.44 10.65 L 12 10.57 L 12.56 10.65 L 13.06 10.91 L 13.46 11.31 L 13.72 11.81 Z";

    /// <summary>Scale: the corner-drag arrow, both heads. <c>F1</c> because each
    /// head OVERLAPS the shaft and even-odd would punch a hole at both joins;
    /// all three subpaths are wound the same way, so nonzero unions them.</summary>
    public const string Scale =
        "F1 M 3.91 5.59 L 18.41 20.09 L 20.09 18.41 L 5.59 3.91 Z M 21.5 21.5 L 21.5 14.75 L 14.75 21.5 Z M 2.5 2.5 L 2.5 9.25 L 9.25 2.5 Z";

    /// <summary>Filter: a funnel - what a filter does to what passes through it.
    /// One closed polygon, so no fill rule can break it.</summary>
    public const string Filter =
        "M 2.5 2.7 L 21.5 2.7 L 13.92 11.8 L 13.92 19.48 L 10.08 21.3 L 10.08 11.8 Z";

    // ---- pen stroke silhouettes (UI-SPEC-V3 K.8) -------------------------
    // A pen slot on the dial does not show the PEN — it shows THE MARK THE PEN
    // LEAVES, drawn in that pen's own colour. These are hand-authored
    // silhouettes of one short stroke running from lower-left to upper-right on
    // the same 24x24 grid as everything else, filled (they are the outline of
    // the mark, not a line), so they scale into a dial sector without the
    // stroke weight drifting. They are deliberately NOT a live render: a slot is
    // 22 DIP across and a real stroke at that size reads as a smudge.

    /// Even width, round ends — a ballpoint / rollerball / monoline trace.
    public const string StrokeRound =
        "M4.35 19.55 C8.2 15.1 12.4 10.6 17.9 5.55 A2.05 2.05 0 0 1 20.65 8.55 " +
        "C15.35 13.4 11.3 17.75 7.6 22.05 A2.05 2.05 0 0 1 4.35 19.55 Z";

    /// Thin taper — a fountain nib: hairline at both ends, swelling in the belly.
    public const string StrokeTaper =
        "M2.9 20.9 C8.6 15.9 14.8 10.4 21.5 5.1 C15.4 11.8 9.6 17.2 2.9 20.9 Z";

    /// Chisel — a marker / felt tip / calligraphy nib: a broad band whose two
    /// ends are cut off parallel at the nib angle.
    public const string StrokeChisel =
        "M2.2 21.4 L9.6 12.6 L21.8 3.4 L14.4 12.2 Z";

    /// Grainy edge — a pencil: the band breaks up along both edges and sheds a
    /// few specks of graphite where the tooth of the paper caught it.
    public const string StrokeGrain =
        "M3.1 20.6 L5.9 18.4 L8.0 19.0 L10.7 16.6 L13.0 17.1 L15.6 14.9 L18.0 15.3 " +
        "L20.9 12.9 L22.0 14.9 L19.4 17.0 L17.1 16.6 L14.7 18.8 L12.3 18.3 L9.9 20.5 " +
        "L7.7 20.0 L4.6 22.6 Z " +
        "M7.4 15.9 L8.9 15.4 L9.2 16.8 L7.7 17.3 Z " +
        "M13.1 11.9 L14.6 11.4 L14.9 12.8 L13.4 13.3 Z " +
        "M18.6 8.2 L20.1 7.7 L20.4 9.1 L18.9 9.6 Z";

    /// <summary>The silhouette for a pen type. Anything not explicitly listed
    /// falls to the round trace, which is what an unremarkable pen leaves.</summary>
    public static string PenStroke(Models.PenType pen) => pen switch
    {
        Models.PenType.Fountain or Models.PenType.Brush or Models.PenType.Watercolor => StrokeTaper,
        Models.PenType.Marker or Models.PenType.FeltTip
            or Models.PenType.Calligraphy or Models.PenType.Highlighter => StrokeChisel,
        Models.PenType.Pencil or Models.PenType.Crayon => StrokeGrain,
        _ => StrokeRound,
    };
    /// Close, for the bare canvas panes. Stroked, because the mark IS two lines.
    public const string Close = "M5 5 L19 19 M19 5 L5 19";

    /// AI assistant: the four-point spark with its small companion — the mark
    /// the top bar already carried, lifted here so the status bar's AI button
    /// and the top bar's cannot drift (V3 K.18 moves it beside Import).
    /// Authored on the 24 grid rather than the 16 the inline copy used.
    public const string Ai =
        "M12 1.5 L14.4 9.6 L22.5 12 L14.4 14.4 L12 22.5 L9.6 14.4 L1.5 12 L9.6 9.6 Z " +
        "M19.5 2.25 L20.4 5.1 L23.25 6 L20.4 6.9 L19.5 9.75 L18.6 6.9 L15.75 6 L18.6 5.1 Z";

    // ---- tools that used to live on the top bar (9.7) --------------------

    /// Dictation: a microphone - capsule head, cradle arc, stem and base, as one
    /// filled outline. Authored, not a glyph.
    public const string Microphone =
        "M12 2.4 A3.2 3.2 0 0 1 15.2 5.6 V11.6 A3.2 3.2 0 0 1 8.8 11.6 V5.6 " +
        "A3.2 3.2 0 0 1 12 2.4 Z " +
        "M6.2 10.6 H7.9 A4.1 4.1 0 0 0 16.1 10.6 H17.8 A5.8 5.8 0 0 1 12.85 16.3 " +
        "V19.2 H15.6 V20.9 H8.4 V19.2 H11.15 V16.3 A5.8 5.8 0 0 1 6.2 10.6 Z";

    /// Recording: the record mark - a ring with a filled dot inside it. F1 so the
    /// reversed inner circle cuts the ring rather than filling it.
    public const string Record =
        "F1 M12 2.6 A9.4 9.4 0 0 1 12 21.4 A9.4 9.4 0 0 1 12 2.6 Z " +
        "M12 4.6 A7.4 7.4 0 0 0 12 19.4 A7.4 7.4 0 0 0 12 4.6 Z " +
        "M12 8.2 A3.8 3.8 0 0 1 12 15.8 A3.8 3.8 0 0 1 12 8.2 Z";

    // ---- the value popover and the inner disc ----------------------------

    /// Decrement, for the value popover's label row. Stroked: the mark IS a rule.
    public const string Minus = "M5 12 H19";

    /// Increment. Stroked, and deliberately the same rule plus its upright, so
    /// the pair reads as one control rather than as two unrelated marks.
    public const string Plus = "M5 12 H19 M12 5 V19";

    /// The Wheel interface option (Settings > Tool Setup > Interface): a ring cut
    /// into sectors by four separators, with the dial's centre dot inside it.
    /// F1 so the reversed inner circle cuts the annulus rather than filling it.
    public const string SurfaceWheel =
        "F1 M12 1.6 A10.4 10.4 0 0 1 12 22.4 A10.4 10.4 0 0 1 12 1.6 Z " +
        "M12 7.4 A4.6 4.6 0 0 0 12 16.6 A4.6 4.6 0 0 0 12 7.4 Z " +
        "M11.4 2.2 H12.6 V7.0 H11.4 Z M11.4 17.0 H12.6 V21.8 H11.4 Z " +
        "M2.2 11.4 H7.0 V12.6 H2.2 Z M17.0 11.4 H21.8 V12.6 H17.0 Z " +
        "M12 9.6 A2.4 2.4 0 0 1 12 14.4 A2.4 2.4 0 0 1 12 9.6 Z";

    /// The Bar interface option: a tall rounded panel divided into three cells,
    /// which is exactly what section 2 describes.
    public const string SurfaceBar =
        "F1 M8.2 1.8 H15.8 A2.6 2.6 0 0 1 18.4 4.4 V19.6 A2.6 2.6 0 0 1 15.8 22.2 " +
        "H8.2 A2.6 2.6 0 0 1 5.6 19.6 V4.4 A2.6 2.6 0 0 1 8.2 1.8 Z " +
        "M8.2 3.4 A1.0 1.0 0 0 0 7.2 4.4 V19.6 A1.0 1.0 0 0 0 8.2 20.6 H15.8 " +
        "A1.0 1.0 0 0 0 16.8 19.6 V4.4 A1.0 1.0 0 0 0 15.8 3.4 Z " +
        "M7.2 8.6 H16.8 V9.8 H7.2 Z M7.2 14.2 H16.8 V15.4 H7.2 Z";
    /// <summary>THE COLOUR PICKER (Reference 11.20 item 12, chosen in 11.23 as
    /// "candidate B, the palette"). A tilted oval with a thumb hole near the
    /// right rim and wells arcing along the far edge. <b>Distinct from the
    /// eyedropper</b>, which is a different tool and a different mark: the
    /// dropper SAMPLES a colour off the page, this one OPENS the picker.
    ///
    /// <para>A FILLED OUTLINE, because <c>PathIcon</c> fills its Data and cannot
    /// stroke, and the top bar draws its marks through PathIcon. The holes are
    /// inner subpaths and punch through under the default even-odd rule.</para>
    ///
    /// <para><b>The wells are the size they are because they were measured.</b>
    /// 11.23 records the design at four wells of radius 1.42 on a 10.6 x 7.9
    /// oval and rules that it "degrades toward a solid blob at the smallest
    /// size" - and rasterising says why: at 18 DIP such a well covers THREE
    /// pixels, so it is open at its centre and still invisible. Three wells of
    /// 1.9 on an 11.5 x 8.1 oval carry six clear pixels each, and the thumb is
    /// 2.7 rather than 2.45 so that one hole still reads as a thumb when the
    /// others have shrunk to specks. Four wells cannot do both at once: at
    /// radius 2.0 they merge into one slot, and at 1.55 they are back to two
    /// pixels. The generator and every measurement are in
    /// obj/agent-patches/pickericon.py.</para></summary>
    public const string Palette =
        "M1.19 15.93 A11.50 8.10 -20 1 1 22.81 8.07 A11.50 8.10 -20 1 1 1.19 15.93 Z " +
        "M13.90 14.40 A2.70 2.70 0 1 0 19.30 14.40 A2.70 2.70 0 1 0 13.90 14.40 Z " +
        "M3.88 12.61 A1.90 1.90 0 1 0 7.68 12.61 A1.90 1.90 0 1 0 3.88 12.61 Z M7.04 8.45 " +
        "A1.90 1.90 0 1 0 10.84 8.45 A1.90 1.90 0 1 0 7.04 8.45 Z M12.80 7.32 A1.90 1.90 0 1 0 16.60 7.32 " +
        "A1.90 1.90 0 1 0 12.80 7.32 Z";

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

    /// <summary>A mark that CANNOT be clipped and CANNOT drift (UI-SPEC-V3 K.5).
    /// 
    /// <para>Both defects come from the same place. A <c>Path</c> sizes itself to
    /// its GEOMETRY EXTENT, so a mark whose ink does not touch all four edges of
    /// the 24 grid ends up a different scale from its neighbours; and
    /// <c>Stretch.Uniform</c> fits the geometry's CENTRELINE to the box while the
    /// pen thickness is applied afterwards, so half of a stroked mark's width
    /// falls outside the arrange rect and the layout clips it. That is precisely
    /// the Smoothness glyph, the one stroked mark in the dial, which was never
    /// confirmed fixed - it is a wave spanning x 2..22 and y 7.4..17.2, so
    /// Stretch.Uniform blew it up to the full box and then cut ~1 DIP off each
    /// end.</para>
    ///
    /// <para>This factory refuses both mechanisms. There is no Stretch: the mark
    /// is drawn at its authored 24-grid coordinates and scaled by size/24 with a
    /// render transform, which is a paint-time operation layout never sees. The
    /// host is a <see cref="Canvas"/> because a Canvas arranges a child at its
    /// own desired size and never imposes a layout clip, unlike the Grid the
    /// shipped marks sat in. A stroke that leaves the grid therefore overflows
    /// harmlessly instead of being erased, and every mark keeps the weight and
    /// the position the 24 grid gave it.</para></summary>
    public static FrameworkElement Mark(string data, Color colour, double size,
                                        bool stroked = false, double thickness = 2, bool mirror = false)
    {
        var host = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        try
        {
            var p = new Shape { Data = Geo(data), Stretch = Stretch.None };
            if (stroked)
            {
                p.Stroke = new SolidColorBrush(colour);
                // Authored thickness is in GRID units, so it scales with the mark.
                p.StrokeThickness = thickness;
                p.StrokeStartLineCap = PenLineCap.Round;
                p.StrokeEndLineCap = PenLineCap.Round;
                p.StrokeLineJoin = PenLineJoin.Round;
            }
            else p.Fill = new SolidColorBrush(colour);

            double k = size / 24.0;
            var tg = new TransformGroup();
            // Mirror about the grid's own centre line FIRST, so redo lands exactly
            // where undo does rather than off to the left by 24 units.
            if (mirror)
            {
                tg.Children.Add(new ScaleTransform { ScaleX = -1 });
                tg.Children.Add(new TranslateTransform { X = 24 });
            }
            tg.Children.Add(new ScaleTransform { ScaleX = k, ScaleY = k });
            p.RenderTransform = tg;
            Canvas.SetLeft(p, 0);
            Canvas.SetTop(p, 0);
            host.Children.Add(p);
        }
        catch { }
        return host;
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
        // the top bar draws the same ring the dial does (user: "apply this
        // icon everywhere in the app")
        Set(undo, UndoRound);
        Set(redo, UndoRound, mirror: true);
    }
}
