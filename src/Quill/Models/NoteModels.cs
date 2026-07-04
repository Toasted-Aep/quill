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
    RightTriangle, Diamond, Pentagon, Hexagon, Star, Parallelogram, Trapezoid
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
        Pen = Pen, Color = Color, Size = Size, Sens = Sens, Points = pts, CreatedTicks = CreatedTicks
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
    // Rotation in degrees about the shape's centre (#20).
    public double Rotation { get; set; }
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
}

public class TextElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 280;
    public string Rtf { get; set; } = "";
    public double Rotation { get; set; }
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
    public bool PenRowVisible { get; set; } = true;
    public double Width { get; set; } = 1500;
    public double Height { get; set; } = 2200;
    public List<PenStroke> Strokes { get; set; } = new();
    public List<TextElement> Texts { get; set; } = new();
    public List<ShapeElement> Shapes { get; set; } = new();
    // Cached handwriting recognition text for search indexing (#18).
    public string OcrText { get; set; } = "";

    public override string ToString() => Name;
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
}
