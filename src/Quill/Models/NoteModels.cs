using System.Text.Json.Serialization;

namespace Quill.Models;

public enum PenType
{
    Standard, Brush, Fountain, Highlighter, Pencil, Marker, Calligraphy,
    Crayon, Watercolor, Monoline, Rollerball, Gel, Ballpoint, FeltTip
}
public enum ToolType { Pen, Eraser, Select, Text, FreeSpace }
// How the mouse behaves while the Pen tool is active. Auto = "normal mouse"
// (click to select/focus, drag empty space to rubber-band select).
public enum MouseMode { Auto, Grab, Select, Move }
public enum EraserMode { Point, Object }
// How the eraser TREATS what it touches, orthogonal to EraserMode's
// pixel-vs-object choice. HardMask is first so it is the zero value and an
// old settings file (which has no such field) keeps today's behaviour.
public enum EraserStyle
{
    HardMask,   // remove everything under the cursor outright (today's eraser)
    SoftMask,   // fade coverage by distance from the cursor centre
    Slice,      // cut the stroke at the crossing point without removing width
    Nudge       // push points out of the way instead of deleting them
}
// New kinds are APPENDED only: GridType serialises as its integer, so
// inserting a member would silently repaint every existing page's grid.
// Perspective is deliberately NOT a GridType (§7.4): it is a separate
// PerspectiveDef overlay on NotePage that coexists with any grid kind.
public enum GridType { None, Dotted, Square, Lines, Isometric, Triangle }
public enum ShapeKind
{
    Line, Rect, Ellipse, Triangle, AxesXY, AxesXYZ, Image, Arrow,
    RightTriangle, Diamond, Pentagon, Hexagon, Star, Parallelogram, Trapezoid,
    Table
}

public class StrokePoint
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Pressure { get; set; } = 0.5f;

    public StrokePoint() { }
    public StrokePoint(float x, float y, float pressure) { X = x; Y = y; Pressure = pressure; }
}

public class PenStroke
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public PenType Pen { get; set; } = PenType.Standard;
    public string Color { get; set; } = "#1A1A1A";
    public float Size { get; set; } = 3f;
    public float Sens { get; set; } = 1f;
    public List<StrokePoint> Points { get; set; } = new();
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    public List<float>? PressureCurve { get; set; }

    [JsonIgnore]
    public float MinY
    {
        get
        {
            float m = float.MaxValue;
            foreach (var p in Points) if (p.Y < m) m = p.Y;
            return m == float.MaxValue ? 0 : m;
        }
    }

    // Cached axis-aligned bounds for viewport culling. The cache is keyed on the
    // point count and the last point so it auto-refreshes when points are added,
    // moved (selection drag / insert-space) or fragmented by erasing.
    [JsonIgnore] private int _bCount = -1;
    [JsonIgnore] private float _bx0, _by0, _bx1, _by1, _sigX, _sigY;

    public void GetBounds(out float minX, out float minY, out float maxX, out float maxY)
    {
        int n = Points.Count;
        bool valid = _bCount == n;
        if (valid && n > 0)
        {
            var last = Points[n - 1];
            if (last.X != _sigX || last.Y != _sigY) valid = false;
        }
        if (!valid)
        {
            float mnX = float.MaxValue, mnY = float.MaxValue, mxX = float.MinValue, mxY = float.MinValue;
            foreach (var p in Points)
            {
                if (p.X < mnX) mnX = p.X;
                if (p.Y < mnY) mnY = p.Y;
                if (p.X > mxX) mxX = p.X;
                if (p.Y > mxY) mxY = p.Y;
            }
            if (n == 0) { mnX = mnY = mxX = mxY = 0; }
            _bx0 = mnX; _by0 = mnY; _bx1 = mxX; _by1 = mxY;
            _bCount = n;
            if (n > 0) { _sigX = Points[n - 1].X; _sigY = Points[n - 1].Y; }
        }
        minX = _bx0; minY = _by0; maxX = _bx1; maxY = _by1;
    }

    public PenStroke CloneWithPoints(List<StrokePoint> pts) => new()
    {
        Pen = Pen, Color = Color, Size = Size, Sens = Sens, Points = pts, CreatedTicks = CreatedTicks, PressureCurve = PressureCurve != null ? new List<float>(PressureCurve) : null
    };
}

public class ShapeElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ShapeKind Kind { get; set; } = ShapeKind.Rect;
    // X,Y = top-left of bounds (for Line: start point; W/H = signed delta)
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string Color { get; set; } = "#141413";
    public float Size { get; set; } = 3f;
    public string? ImagePath { get; set; }
    // For equation images: the LaTeX source it was rendered from, so the
    // equation can be reopened and edited instead of retyped (#27-batch2).
    public string? EquationLatex { get; set; }
    // Axes shapes only: custom axis labels; null = the default x/y/z (#28-batch2).
    public string? AxisLabelX { get; set; }
    public string? AxisLabelY { get; set; }
    public string? AxisLabelZ { get; set; }
    // Rotation in degrees about the shape's centre (#20).
    public double Rotation { get; set; }
    // Table shapes only: grid dimensions (#40) and, Word-style, individual
    // column widths / row heights (#49). Null lists = uniform grid.
    public int TRows { get; set; }
    public int TCols { get; set; }
    public List<double>? TColW { get; set; }
    public List<double>? TRowH { get; set; }
    // Per-cell fill/border styling (#roadmap: table enhancements).
    public string? FillColor { get; set; }
    public string? BorderColor { get; set; }
    public float? BorderWidth { get; set; }
    // Cell merge spans: 1 = no merge (default).
    public int MergeColSpan { get; set; } = 1;
    public int MergeRowSpan { get; set; } = 1;
    // Whether this is a bold header row (table shapes only).
    public bool HeaderRow { get; set; }
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
}

public class TextElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 280;
    // true once the user drags the width grip — opts out of auto-sizing (#15-batch4)
    public bool WidthPinned { get; set; }
    // Auto-grow ceiling in world units, snapshotted when the box is first built
    // (half the physical screen, capped at the window edge) so a later window
    // resize never changes an existing box's ceiling (#15). 0 = not yet computed.
    public double MaxWidth { get; set; }
    // Only boxes created after the auto-grow feature opt in — pre-existing
    // boxes keep their saved width so old notes never re-wrap (#15).
    public bool AutoWidth { get; set; }
    public string Rtf { get; set; } = "";
    public double Rotation { get; set; }
    // Cell membership for table shapes (#40): null = a free text box.
    public Guid? TableId { get; set; }
    public int TableRow { get; set; }
    public int TableCol { get; set; }
    // Cell styling and spans (#roadmap: table enhancements)
    public string? FillColor { get; set; }
    public string? BorderColor { get; set; }
    public float? BorderWidth { get; set; }
    public int CellColSpan { get; set; } = 1;
    public int CellRowSpan { get; set; } = 1;
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
}

public class PenPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Pen";
    public PenType Pen { get; set; } = PenType.Standard;
    public string Color { get; set; } = "#141413";
    public float Size { get; set; } = 3.5f;
    public float Sens { get; set; } = 1f;
    // Wet-ink stabiliser: 0 = off, 1 = maximum smoothing.
    public float Stabiliser { get; set; }
    // Custom pressure response curve control points (0–1 range).
    // null = linear (default). 3 floats = legacy soft/hard preset,
    // 6 floats = the three-point custom curve. Kept as-is: it is what the
    // renderer and every saved stroke already speak.
    public List<float>? PressureCurve { get; set; }
    // Two-point response curve (#curve v2). null = fall back to PressureCurve
    // above, so an existing preset behaves exactly as before.
    public PressureCurve2? PressureResponse { get; set; }
}

// Two-control-point pressure response. The first control point is pinned at
// input 0 and the second at input 100, so only their OUTPUT values are
// editable; Bend supplies the curvature that two pinned ends cannot express.
// Authoring model only — it is baked down to the existing 6-float form before
// it reaches a stroke, so per-stroke JSON does not grow (library.json is 53 MB).
public class PressureCurve2
{
    // Output (fraction of full pen width) at input 0. 0 = no ink at zero pressure.
    public float Out0 { get; set; }
    // Output at input 100. 1 = full width at maximum pressure.
    public float Out100 { get; set; } = 1f;
    // Curvature between the two ends: -1 concave, 0 straight (linear, today's
    // default), +1 convex.
    public float Bend { get; set; }

    /// <summary>Output for a 0–1 input, matching the renderer's convention.</summary>
    public float Evaluate(float input)
    {
        float t = Math.Clamp(input, 0f, 1f);
        // Bend warps t before the straight Out0→Out100 blend; b == 0 is exactly linear.
        float b = Math.Clamp(Bend, -1f, 1f);
        float w = t + b * t * (1f - t);
        return Out0 + (Out100 - Out0) * Math.Clamp(w, 0f, 1f);
    }

    /// <summary>Samples this curve into the 6-float (three x,y pairs) list the
    /// ink renderer already interpolates, so v2 needs no renderer change.</summary>
    public List<float> ToLegacyPoints() => new()
    {
        0.25f, Evaluate(0.25f),
        0.5f,  Evaluate(0.5f),
        0.75f, Evaluate(0.75f)
    };

    /// <summary>Best-effort read of an old 3- or 6-float curve as a v2 curve, for
    /// the one-way “edit this preset” migration. null in, null out.</summary>
    public static PressureCurve2? FromLegacy(List<float>? pts)
    {
        if (pts == null) return null;
        if (pts.Count >= 6)
        {
            // straight line through the outer two points, bend from the middle one
            float y0 = pts[1], y2 = pts[5], mid = pts[3];
            float lin = (y0 + y2) * 0.5f;
            return new PressureCurve2 { Out0 = 0f, Out100 = 1f, Bend = Math.Clamp((mid - lin) * 4f, -1f, 1f) };
        }
        if (pts.Count >= 3)
            return new PressureCurve2 { Out0 = 0f, Out100 = 1f, Bend = Math.Clamp((pts[1] - 0.5f) * 2f, -1f, 1f) };
        return null;
    }
}

public class NotePage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Page";
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    public double ViewX { get; set; }
    public double ViewY { get; set; }
    public double ViewZoom { get; set; } = 1;
    public string Background { get; set; } = "#FFFFFF";
    // Paper texture id (§7.3): null = smooth (today's behaviour). v1 ids are
    // "grain"/"canvas"/"coldpress"/"laid"; drawn between Clear(bg) and the grid,
    // inside the world transform. WhenWritingDefault so old pages cost 0 bytes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Paper { get; set; }
    public GridType Grid { get; set; } = GridType.None;
    public double GridSpacing { get; set; } = 32;
    // Custom gridline colour ("#RRGGBB"); null = automatic (contrast with background).
    public string? GridColor { get; set; }
    // Isometric / Triangle grids only: axis tilt in degrees off horizontal.
    // 30 is the isometric standard and is ignored by every other grid kind.
    public double GridAngle { get; set; } = 30;
    // Perspective overlay (§7.4): coexists with any GridType, so it is NOT a
    // GridType member. null = no perspective guides (default). Persisted only
    // once a perspective grid is actually placed; carried in PageMetaJson.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public PerspectiveDef? Perspective { get; set; }
    // Page size (#pagesize). Infinite is the default so existing pages keep
    // today's unbounded canvas; see PageSizes for the preset table.
    public PageSizePreset PageSize { get; set; } = PageSizePreset.Infinite;
    // Custom dimensions, used only when PageSize == Custom. 0 = unset.
    public double PageWidth { get; set; }
    public double PageHeight { get; set; }
    // Unit the custom dimensions are expressed in (presets carry their own unit).
    public PageSizeUnit PageUnit { get; set; } = PageSizeUnit.Pixels;
    // World units per inch (§7.1): 1 world unit = 1 DIP = 1/96 inch, so 96 keeps
    // physical (mm/inch) presets and print/export scale exact. Social/screen
    // presets are pixel-native and ignore it. (Named UnitsPerInch per §7.1.)
    public double UnitsPerInch { get; set; } = 96;
    // Swaps width and height of the resolved size. Presets are stored portrait.
    public bool PageLandscape { get; set; }
    public bool PenRowVisible { get; set; } = true;
    public double Width { get; set; } = 1500;
    public double Height { get; set; } = 2200;
    public List<PenStroke> Strokes { get; set; } = new();
    public List<TextElement> Texts { get; set; } = new();
    public List<ShapeElement> Shapes { get; set; } = new();
    // Cached handwriting recognition text for search indexing (#18).
    public string OcrText { get; set; } = "";
    // Audio recording: relative path to m4a file and UTC ticks when recording started.
    public string? AudioFile { get; set; }
    public long AudioStartTicks { get; set; }
    // Comment pins (#roadmap: staged collaboration — comments ship standalone).
    public List<PageComment> Comments { get; set; } = new();

    public override string ToString() => Name;
}

// A pinned note anchored to a spot on the page. Fully useful single-user
// (self-notes / TODO pins) and rides the future op-log sync for free.
public class PageComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? AnchorElementId { get; set; }   // reserved: stroke/shape/text it points at
    public double X { get; set; }
    public double Y { get; set; }
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    public bool Resolved { get; set; }
}

public class Section
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Section";
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    public List<NotePage> Pages { get; set; } = new();

    public override string ToString() => Name;
}

public class Notebook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Notebook";
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
    // Apple-Notes-style accent colour for the notebook (used by the gallery view).
    public string Color { get; set; } = "#D97757";
    // SHA-256 hash of the lock password (null = not locked) (#23).
    public string? PasswordHash { get; set; }
    // Optional folder grouping for the gallery view (#16).
    public string? Folder { get; set; }
    // Emoji/icon for gallery card (#roadmap: cover picker).
    public string? CoverEmoji { get; set; }
    // Per-notebook default page settings (overrides library defaults).
    public string? DefaultBackground { get; set; }
    public GridType? DefaultGrid { get; set; }
    public double? DefaultGridSpacing { get; set; }
    // Per-notebook default paper texture (§7.3); null = follow the library default.
    public string? DefaultPaper { get; set; }
    // Per-notebook default text font/size (null = the library-wide default).
    public string? DefaultFont { get; set; }
    public double? DefaultFontSize { get; set; }
    public List<Section> Sections { get; set; } = new();

    public override string ToString() => Name;
}

public class Library
{
    public List<Notebook> Notebooks { get; set; } = new();
    public List<string> Folders { get; set; } = new();   // gallery folders (#16)
    public List<PenPreset> Pens { get; set; } = new();
    public string DefaultBackground { get; set; } = "#FAF9F5";
    public GridType DefaultGrid { get; set; } = GridType.None;
    public double DefaultGridSpacing { get; set; } = 32;
    // Library-wide default paper texture (§7.3); null = smooth (today's default).
    public string? DefaultPaper { get; set; }
    public string Theme { get; set; } = "Dark";
    // UI language tag ("en-US"/"tr"/"it"); "" follows the Windows UI language.
    public string Language { get; set; } = "";
    public string DefaultFont { get; set; } = "Lora";
    public double DefaultFontSize { get; set; } = 16;
    public string PenDock { get; set; } = "Bottom";
    public double NotebookPanelW { get; set; } = 300;
    public double NotebookPanelH { get; set; }
    // Last page the user worked on — restored (and offered as "Continue") at startup.
    public Guid? LastPageId { get; set; }
    // Startup behaviour: launch full screen with the notebook picker shown.
    public bool StartFullscreen { get; set; } = true;
    public bool StartOnGallery { get; set; } = true;
    // Accent colour for glows, highlights and buttons (#33).
    public string AccentColor { get; set; } = "#D97757";
    // Touch-screen mode: larger tap targets across the toolbars (#36).
    public bool TouchMode { get; set; }
    // Calculator history, kept across restarts (#47).
    public List<string> CalcHistory { get; set; } = new();
    // User-defined calculator constants, "name=value" (#18-batch3).
    public List<string> CalcConstants { get; set; } = new();
    // Calculator session variables ("name=value"), restored across restarts (#A7).
    public List<string> CalcVars { get; set; } = new();
    // Liquid-glass panel transparency, 0 (solid) … 1 (fully liquid) (#48).
    public double Liquidness { get; set; } = 0.35;
    // Recently used pen/highlight colours (newest first, max 16).
    public List<string> RecentColors { get; set; } = new();
    // User-curated custom accent colours shown as an extra swatch row in Settings.
    public List<string> CustomColors { get; set; } = new();
    // Last-selected eraser mode ("Point"/"Object"), restored on launch.
    public string LastEraserMode { get; set; } = "Object";
    // Last-selected eraser style, restored on launch. HardMask = today's eraser.
    public EraserStyle LastEraserStyle { get; set; } = EraserStyle.HardMask;
    // Eraser radius in canvas units. 0 = derive it from the active pen size,
    // which is exactly what the surface does today. Lives here rather than on a
    // pen preset because there is no eraser preset list: the eraser is one
    // global tool, and its size must survive switching pens and restarting.
    public double EraserSize { get; set; }
    // Glow animation on the glass rims: Off | Breathe | Circulate.
    public string GlowMode { get; set; } = "Breathe";
    // What drives the accent colour: Manual | Pen | Notebook.
    public string AccentFollow { get; set; } = "Manual";
    // Keyboard shortcut preset: Quill | OneNote | Photoshop. Unknown values fall
    // back to Quill so a hand-edited settings file cannot leave the app keyless.
    public string KeyPreset { get; set; } = "Quill";
    // Pure-black dark theme for OLED displays.
    public bool OledBlack { get; set; }
    // Autosave debounce in seconds.
    public double AutosaveSeconds { get; set; } = 1.5;
    // Pen repair, split into its two behaviours (#6-batch4). PenRepair is the
    // legacy combined switch, kept only so old settings migrate on first run.
    public bool PenRepair { get; set; }
    public bool PenRepairDots { get; set; }
    public bool PenRepairBridge { get; set; }
    // Motion blur (#A5): soften the page while it pans/zooms, sharpening as it stops.
    public bool MotionBlur { get; set; }
    // show comment pins even when the Comment tool is not active (#A3)
    public bool ShowCommentPins { get; set; }
    // toolbar buttons the user has switched off (#topbar)
    public List<string> HiddenTools { get; set; } = new();
    // Per-command rebinds layered OVER KeyPreset. Empty = pure preset, i.e.
    // exactly today's bindings. See KeyOverride for the resolution rule.
    public List<KeyOverride> KeyOverrides { get; set; } = new();
    // Recently opened pages for the gallery, newest first, capped at
    // RecentPage.MaxRecents. Empty on an existing library, which simply means
    // the Recents row starts empty until the user opens something.
    public List<RecentPage> Recents { get; set; } = new();
    // AI assistant (#25): provider + model + local endpoint. API keys are kept
    // in the Windows Credential Locker, never in this file.
    public string AiProvider { get; set; } = "None";
    public string AiModel { get; set; } = "";
    public string AiEndpoint { get; set; } = "";
    // Last non-maximised window placement, so leaving fullscreen/maximise
    // returns to the size the user actually had. 0 width = never saved.
    public double WinX { get; set; }
    public double WinY { get; set; }
    public double WinW { get; set; }
    public double WinH { get; set; }
    public bool WinMaximized { get; set; } = true;
}

// ===========================================================================
// PAGE SIZE (#pagesize)
// The preset TABLE lives in code, never in library.json: a page stores only
// its preset key (plus custom dimensions when the key is Custom), so adding or
// correcting a preset later fixes every page at once and costs no file size.
// ===========================================================================
public enum PageSizeUnit { Pixels, Millimeters, Inches }

// Appended-only, like GridType: these serialise as integers. Infinite stays 0
// (the default), Custom is the escape hatch; new presets are only ever appended.
public enum PageSizePreset
{
    Infinite,                                            // default: today's unbounded canvas
    A0, A1, A2, A3, A4, A5, A6, A7,                      // ISO 216 A series (§7.2)
    HalfLetter, Letter, Legal, Tabloid, Executive,       // US / office
    AnsiC, AnsiD, AnsiE, ArchC, ArchD, ArchE,            // large format (ANSI / ARCH)
    BusinessCardUS, BusinessCardEU,                      // cards …
    IndexCard3x5, IndexCard4x6, IndexCard5x8, PostcardA6, PlayingCard,
    InstagramSquare, InstagramPortrait, InstagramStory,  // social media (pixel-native)
    FacebookPost, FacebookCover, TwitterPost, LinkedInPost, YouTubeThumbnail, PinterestPin,
    Screen720p, Screen1080p, Screen1440p, Screen4K,      // screen resolutions (pixel-native)
    Custom                                               // use PageWidth/PageHeight/PageUnit
}

/// <summary>One row of the static preset table. Portrait orientation by
/// convention; NotePage.PageLandscape swaps the two at resolve time.</summary>
public sealed record PageSizeDef(PageSizePreset Preset, string Name, double Width, double Height, PageSizeUnit Unit);

public static class PageSizes
{
    // Order here is the order the picker shows. Infinite and Custom are in the
    // table so the picker can be built from one list, but both resolve specially.
    public static readonly PageSizeDef[] Table =
    {
        new(PageSizePreset.Infinite,          "Infinite",            0,     0,     PageSizeUnit.Pixels),
        // ISO 216 A series — stored portrait, millimetres.
        new(PageSizePreset.A0,                "A0",                  841,   1189,  PageSizeUnit.Millimeters),
        new(PageSizePreset.A1,                "A1",                  594,   841,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A2,                "A2",                  420,   594,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A3,                "A3",                  297,   420,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A4,                "A4",                  210,   297,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A5,                "A5",                  148,   210,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A6,                "A6",                  105,   148,   PageSizeUnit.Millimeters),
        new(PageSizePreset.A7,                "A7",                  74,    105,   PageSizeUnit.Millimeters),
        // US / office — inches.
        new(PageSizePreset.HalfLetter,        "Half Letter",         5.5,   8.5,   PageSizeUnit.Inches),
        new(PageSizePreset.Letter,            "US Letter",           8.5,   11,    PageSizeUnit.Inches),
        new(PageSizePreset.Legal,             "US Legal",            8.5,   14,    PageSizeUnit.Inches),
        new(PageSizePreset.Tabloid,           "Tabloid / Ledger",    11,    17,    PageSizeUnit.Inches),
        new(PageSizePreset.Executive,         "Executive",           7.25,  10.5,  PageSizeUnit.Inches),
        // Large format — inches.
        new(PageSizePreset.AnsiC,             "ANSI C",              17,    22,    PageSizeUnit.Inches),
        new(PageSizePreset.AnsiD,             "ANSI D",              22,    34,    PageSizeUnit.Inches),
        new(PageSizePreset.AnsiE,             "ANSI E",              34,    44,    PageSizeUnit.Inches),
        new(PageSizePreset.ArchC,             "Arch C",              18,    24,    PageSizeUnit.Inches),
        new(PageSizePreset.ArchD,             "Arch D",              24,    36,    PageSizeUnit.Inches),
        new(PageSizePreset.ArchE,             "Arch E",              36,    48,    PageSizeUnit.Inches),
        // Cards — as authored (orientation per §7.2, PageLandscape can still swap).
        new(PageSizePreset.BusinessCardUS,    "Business card (US)",  3.5,   2,     PageSizeUnit.Inches),
        new(PageSizePreset.BusinessCardEU,    "Business card (EU)",  85,    55,    PageSizeUnit.Millimeters),
        new(PageSizePreset.IndexCard3x5,      "Index card 3x5",      3,     5,     PageSizeUnit.Inches),
        new(PageSizePreset.IndexCard4x6,      "Index card 4x6",      4,     6,     PageSizeUnit.Inches),
        new(PageSizePreset.IndexCard5x8,      "Index card 5x8",      5,     8,     PageSizeUnit.Inches),
        new(PageSizePreset.PostcardA6,        "Postcard (A6)",       148,   105,   PageSizeUnit.Millimeters),
        new(PageSizePreset.PlayingCard,       "Playing card",        2.5,   3.5,   PageSizeUnit.Inches),
        // Social media — pixel-native (UnitsPerInch ignored).
        new(PageSizePreset.InstagramSquare,   "Instagram square",    1080,  1080,  PageSizeUnit.Pixels),
        new(PageSizePreset.InstagramPortrait, "Instagram portrait",  1080,  1350,  PageSizeUnit.Pixels),
        new(PageSizePreset.InstagramStory,    "Instagram story",     1080,  1920,  PageSizeUnit.Pixels),
        new(PageSizePreset.FacebookPost,      "Facebook post",       1200,  630,   PageSizeUnit.Pixels),
        new(PageSizePreset.FacebookCover,     "Facebook cover",      1640,  624,   PageSizeUnit.Pixels),
        new(PageSizePreset.TwitterPost,       "X (Twitter) post",    1600,  900,   PageSizeUnit.Pixels),
        new(PageSizePreset.LinkedInPost,      "LinkedIn post",       1200,  627,   PageSizeUnit.Pixels),
        new(PageSizePreset.YouTubeThumbnail,  "YouTube thumbnail",   1280,  720,   PageSizeUnit.Pixels),
        new(PageSizePreset.PinterestPin,      "Pinterest pin",       1000,  1500,  PageSizeUnit.Pixels),
        // Screen resolutions — pixel-native.
        new(PageSizePreset.Screen720p,        "Screen 720p",         1280,  720,   PageSizeUnit.Pixels),
        new(PageSizePreset.Screen1080p,       "Screen 1080p",        1920,  1080,  PageSizeUnit.Pixels),
        new(PageSizePreset.Screen1440p,       "Screen 1440p",        2560,  1440,  PageSizeUnit.Pixels),
        new(PageSizePreset.Screen4K,          "Screen 4K",           3840,  2160,  PageSizeUnit.Pixels),
        new(PageSizePreset.Custom,            "Custom",              0,     0,     PageSizeUnit.Pixels),
    };

    public static PageSizeDef? Find(PageSizePreset p)
    {
        foreach (var d in Table) if (d.Preset == p) return d;
        return null;
    }

    // Converts a unit-tagged value to WORLD units (1 world unit = 1 DIP = 1/96
    // inch at zoom 1). Pixels pass through; mm/inch scale by units-per-inch.
    public static double ToWorld(double value, PageSizeUnit unit, double upi) => unit switch
    {
        PageSizeUnit.Inches => value * upi,
        PageSizeUnit.Millimeters => value / 25.4 * upi,
        _ => value
    };

    /// <summary>Resolves a page's size to WORLD units. Returns false for
    /// Infinite (and for a Custom page with no dimensions yet), which is the
    /// signal to keep drawing the unbounded canvas exactly as today.</summary>
    public static bool TryResolve(NotePage page, out double w, out double h)
    {
        w = h = 0;
        double upi = page.UnitsPerInch > 0 ? page.UnitsPerInch : 96;
        if (page.PageSize == PageSizePreset.Infinite) return false;
        if (page.PageSize == PageSizePreset.Custom)
        {
            if (page.PageWidth <= 0 || page.PageHeight <= 0) return false;
            w = ToWorld(page.PageWidth, page.PageUnit, upi);
            h = ToWorld(page.PageHeight, page.PageUnit, upi);
        }
        else
        {
            var d = Find(page.PageSize);
            if (d == null || d.Width <= 0 || d.Height <= 0) return false;
            w = ToWorld(d.Width, d.Unit, upi);
            h = ToWorld(d.Height, d.Unit, upi);
        }
        if (page.PageLandscape) (w, h) = (h, w);
        return true;
    }

    /// <summary>The page's artboard (§7.1), or null for an Infinite page. This is
    /// the world-unit rectangle the preset resolves to; it is computed, never
    /// stored separately, so the persisted state stays "preset key + custom dims".</summary>
    public static Artboard? ResolveArtboard(NotePage page)
        => TryResolve(page, out double w, out double h) ? new Artboard(w, h) : null;
}

/// <summary>The optional drawn rectangle a non-Infinite page carries (§7.1): the
/// export clip and the "fit" anchor. Dimensions are WORLD units (1 unit = 1 DIP).
/// Resolved from the preset table by PageSizes.ResolveArtboard; a page persists
/// only its preset key + custom dimensions, never this resolved size.</summary>
public sealed record Artboard(double W, double H);

// ===========================================================================
// PERSPECTIVE GRIDS (§7.4)
// A per-page overlay, NOT a GridType, so it coexists with any grid kind.
// Persisted only once a perspective grid is actually placed. Coordinates are
// CANVAS coordinates (the space strokes live in), so the guides stay glued to
// the drawing when the view pans or zooms. The vanishing-point count encodes
// the kind: 1 VP = 1-point, 2 = 2-point (both on the horizon), 3 = 3-point
// (the third, VPZ, sits off the horizon).
// ===========================================================================

// A canvas-space point. A plain serializable {X,Y} pair rather than the WinRT
// Windows.Foundation.Point, so this model file stays free of UI types.
public struct CanvasPoint
{
    public double X { get; set; }
    public double Y { get; set; }
    public CanvasPoint() { }
    public CanvasPoint(double x, double y) { X = x; Y = y; }
}

public class PerspectiveDef
{
    // Canvas Y of the horizon line; the 1- and 2-point vanishing points sit on it.
    public double HorizonY { get; set; }
    // Vanishing points, 1–3. Order is [VP] / [VPL, VPR] / [VPL, VPR, VPZ].
    public List<CanvasPoint> Vps { get; set; } = new();
    // Rays cast per vanishing point for the guide fan. Higher = denser.
    public int RayCount { get; set; } = 24;
}

// ===========================================================================
// TRASH BIN (#trash)
// Deliberately NOT part of Library: a deleted notebook carries all of its
// strokes, and library.json is already 53 MB — folding deleted content back
// into it would grow the file that is rewritten on every 1.5 s autosave.
// The bin is its own document (trash.json, alongside library.json) so it is
// written only when something is actually deleted or restored.
// ===========================================================================
public enum TrashItemKind { Notebook, Section, Page }

public class TrashEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public TrashItemKind Kind { get; set; } = TrashItemKind.Page;
    // Cached display name, so the bin lists without deserialising the payload.
    public string Name { get; set; } = "";
    public long DeletedTicks { get; set; } = DateTime.UtcNow.Ticks;
    // Original location. Notebook: both null. Section: ParentNotebookId set.
    // Page: both set. Restore falls back to the end of the list if the parent
    // is gone or the index no longer fits.
    public Guid? ParentNotebookId { get; set; }
    public Guid? ParentSectionId { get; set; }
    public int OriginalIndex { get; set; }
    // Exactly one of these is non-null, matching Kind.
    public Notebook? Notebook { get; set; }
    public Section? Section { get; set; }
    public NotePage? Page { get; set; }
}

public class TrashBin
{
    // Newest first.
    public List<TrashEntry> Items { get; set; } = new();
    // Entries older than this are purged on load. 0 = keep until MaxItems evicts.
    public int RetentionDays { get; set; } = 30;
    // Hard cap so a bin left alone cannot grow without bound.
    public const int MaxItems = 200;
}

// ===========================================================================
// RECENTLY OPENED (#recents)
// Small enough to live in Library: at MaxRecents entries this is a few KB.
// Names are cached so the gallery can render the row without walking the tree.
// ===========================================================================
public class RecentPage
{
    public Guid PageId { get; set; }
    public Guid SectionId { get; set; }
    public Guid NotebookId { get; set; }
    public string PageName { get; set; } = "";
    public string NotebookName { get; set; } = "";
    public long OpenedTicks { get; set; } = DateTime.UtcNow.Ticks;

    public const int MaxRecents = 20;
}

// ===========================================================================
// CUSTOM SHORTCUTS (#keys)
// Library.KeyPreset stays the BASE layout; these are layered over it by
// command id. An empty override list therefore reproduces today's bindings
// exactly. Key and Mods are strings, not WinUI enums, both to keep this file
// free of UI types and so a hand-edited settings file with a typo degrades to
// "unrecognised, fall back to the preset" instead of failing to deserialise.
// ===========================================================================
public class KeyOverride
{
    // Command id from the preset table ("Undo", "ToolPen", ...).
    public string CommandId { get; set; } = "";
    // VirtualKey name, e.g. "F", "F11", "Delete". Ignored when Disabled.
    public string Key { get; set; } = "";
    // "+"-separated modifiers, e.g. "Control", "Control+Shift". "" = none.
    public string Mods { get; set; } = "";
    // true = the command is explicitly unbound (distinct from "no override").
    public bool Disabled { get; set; }
}
