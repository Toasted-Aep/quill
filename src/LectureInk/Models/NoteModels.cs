using System.Text.Json.Serialization;

namespace LectureInk.Models;

public enum PenType { Standard, Brush, Fountain, Highlighter, Pencil, Marker, Calligraphy }
public enum ToolType { Pen, Eraser, Select, Text, FreeSpace }
// How the mouse behaves while the Pen tool is active. Auto = "normal mouse"
// (click to select/focus, drag empty space to rubber-band select).
public enum MouseMode { Auto, Grab, Select, Move }
public enum EraserMode { Point, Object }
public enum GridType { None, Dotted, Square, Lines }
public enum ShapeKind { Line, Rect, Ellipse, Triangle, AxesXY, AxesXYZ, Image }

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
    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;
}

public class TextElement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 280;
    public string Rtf { get; set; } = "";
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

    public override string ToString() => Name;
}

public class Section
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Section";
    public List<NotePage> Pages { get; set; } = new();

    public override string ToString() => Name;
}

public class Notebook
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Notebook";
    public List<Section> Sections { get; set; } = new();

    public override string ToString() => Name;
}

public class Library
{
    public List<Notebook> Notebooks { get; set; } = new();
    public List<PenPreset> Pens { get; set; } = new();
    public string DefaultBackground { get; set; } = "#FAF9F5";
    public GridType DefaultGrid { get; set; } = GridType.None;
    public double DefaultGridSpacing { get; set; } = 32;
    public string Theme { get; set; } = "Light";
    public string PenDock { get; set; } = "Bottom";
    public double NotebookPanelW { get; set; } = 300;
    public double NotebookPanelH { get; set; }
}
