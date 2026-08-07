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
