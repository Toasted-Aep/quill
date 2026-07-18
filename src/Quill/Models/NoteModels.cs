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
public enum GridType { None, Dotted, Square, Lines }
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
    // null = linear (default). 3 floats = cubic Bézier mid-points.
    public List<float>? PressureCurve { get; set; }
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
    public GridType Grid { get; set; } = GridType.None;
    public double GridSpacing { get; set; } = 32;
    // Custom gridline colour ("#RRGGBB"); null = automatic (contrast with background).
    public string? GridColor { get; set; }
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
    public string Theme { get; set; } = "Dark";
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
    // Glow animation on the glass rims: Off | Breathe | Circulate.
    public string GlowMode { get; set; } = "Breathe";
    // What drives the accent colour: Manual | Pen | Notebook.
    public string AccentFollow { get; set; } = "Manual";
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
