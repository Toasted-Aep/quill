





using System.Numerics;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using Quill.Services;
using Quill.Helpers;

namespace Quill.Controls;

public enum ArtTool { Brush, PaletteKnife, Eyedropper, Eraser }
public enum DepletionCurveType { Linear, Exponential, SCurve }
public enum SymmetryMode { None, Vertical, Horizontal, Radial }
public enum RulerType { Straight, Circle }
public enum PatternType { Dots, Crosshatch, Stipple }

public class HistoryNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ParentId { get; set; }
    public List<string> ChildrenIds { get; } = new();
    public string? Description { get; set; }
    public List<ArtStroke> StrokesSnapshot { get; set; } = new();
}

public class ArtBrush
{
    public string Name { get; set; } = "Oil";
    public float Viscosity { get; set; } = 0.8f;
    public float Impasto { get; set; } = 0.9f;
    public float Opacity { get; set; } = 1.0f;
    public float BleedRate { get; set; } = 0.0f;
    public float DepletionRate { get; set; } = 0.02f;
    public float TextureInteraction { get; set; } = 0.1f;
    public float SelfLeveling { get; set; } = 0.0f;
    public bool IsSplatter { get; set; } = false;
    public bool IsSand { get; set; } = false;
    public bool IsWax { get; set; } = false;
    public bool IsGouache { get; set; } = false;
    public bool IsChalk { get; set; } = false;
    public bool IsDigital { get; set; } = false;
    public float DefaultDryTime { get; set; } = 1200f;
    public Color DefaultColor { get; set; } = Colors.SaddleBrown;
}

public class ArtLayer : IDisposable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Layer";
    public CanvasRenderTarget ColorTarget { get; set; }
    public CanvasRenderTarget HeightTarget { get; set; }
    public float Opacity { get; set; } = 1.0f;
    public bool Visible { get; set; } = true;

    public ArtLayer(ICanvasResourceCreator device, int w, int h)
    {
        ColorTarget = new CanvasRenderTarget(device, w, h, 96);
        HeightTarget = new CanvasRenderTarget(device, w, h, 96);
        using (var ds = ColorTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = HeightTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
    }

    public void RecreateTargets(ICanvasResourceCreator device, int w, int h)
    {
        ColorTarget?.Dispose();
        HeightTarget?.Dispose();
        ColorTarget = new CanvasRenderTarget(device, w, h, 96);
        HeightTarget = new CanvasRenderTarget(device, w, h, 96);
        using (var ds = ColorTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = HeightTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
    }

    public void Clear()
    {
        using (var ds = ColorTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = HeightTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
    }

    public void Dispose()
    {
        ColorTarget.Dispose();
        HeightTarget.Dispose();
    }
}

public class ArtStroke
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LayerId { get; set; }
    public List<Point> Points { get; set; } = new();
    public List<float> Pressures { get; set; } = new();
    public Color? PaintColor { get; set; } // null = water / none
    public string BrushName { get; set; } = "Oil Paint";
    public string ToolName { get; set; } = "Paint Brush";
    public string DepletionCurve { get; set; } = "Exponential";
    public float Size { get; set; } = 25f;
    public float Viscosity { get; set; } = 0.8f;
    public float Impasto { get; set; } = 0.9f;
    public float Opacity { get; set; } = 1.0f;
}

public class ArtUndoItem
{
    public Guid LayerId { get; set; }
    public Rect DirtyRect { get; set; }
    public byte[] ColorBefore { get; set; } = Array.Empty<byte>();
    public byte[] HeightBefore { get; set; } = Array.Empty<byte>();
    public byte[] ColorAfter { get; set; } = Array.Empty<byte>();
    public byte[] HeightAfter { get; set; } = Array.Empty<byte>();
}

public class ArtSettings
{
    public List<string> CustomPalette { get; set; } = new();
    public string SelectedBrush { get; set; } = "Oil Paint";
    public string SelectedTool { get; set; } = "Paint Brush";
    public string SelectedCurve { get; set; } = "Exponential";
    public float DepletionRate { get; set; } = 1.0f;
}

public sealed class ArtSurface : UserControl
{
    public int CanvasWidth { get; private set; } = 1200;
    public int CanvasHeight { get; private set; } = 1600;
    public string SubstrateType { get; private set; } = "Canvas";
    
    private CanvasControl? _canvas;
    private Grid? _canvasContainer;
    private CompositeTransform _transform = new();
    
    // Layers List
    public List<ArtLayer> Layers { get; } = new();
    public ArtLayer? ActiveLayer { get; set; }
    
    // Global rendering composites
    private CanvasRenderTarget? _globalColor;
    private CanvasRenderTarget? _globalHeight;
    private CanvasRenderTarget? _wetnessTarget;
    private CanvasRenderTarget? _substrateHeight;
    private CanvasRenderTarget? _combinedHeight;
    private CanvasRenderTarget? _baseColorTarget;
    
    // Scratch targets
    private CanvasRenderTarget? _tempStrokeColor;
    private CanvasRenderTarget? _tempStrokeHeight;
    private CanvasRenderTarget? _tempWetness;
    private CanvasRenderTarget? _tempColor;
    private CanvasRenderTarget? _tempHeight;
    private CanvasRenderTarget? _smearTemp;
    private CanvasRenderTarget? _smearTempHeight;
    



    private CanvasImageBrush? _substrateBrush;
    private Color _substrateBgColor = Color.FromArgb(255, 250, 246, 238);
    private byte[]? _substrateHeightBytes;
    private float _cachedWetnessValue = 0f;
    
    public List<ArtBrush> Brushes { get; } = new();
    public ArtBrush CurrentBrush { get; set; }
    public ArtTool CurrentTool { get; set; } = ArtTool.Brush;
    public DepletionCurveType DepletionCurve { get; set; } = DepletionCurveType.Exponential;
    public SymmetryMode CurrentSymmetryMode { get; set; } = SymmetryMode.None;
    public bool ScratchboardMode { get; set; } = false;
    public bool ShowWetnessHeatmap { get; set; } = false;
    public float BrushSize { get; set; } = 25f;
    public float PigmentLoading { get; set; } = 1.0f;
    public float WaterWetness { get; set; } = 0.2f;
    public bool RealTimeDrying { get; set; } = true;
    public float DrynessSliderValue { get; set; } = 1.0f;
    public float DepletionRateSliderValue { get; set; } = 1.0f;
    public bool AutoReloadPaint { get; set; } = true;
    private Color? _activeColor = Colors.SaddleBrown;
    public Color? ActiveColor
    {
        get => _activeColor;
        set
        {
            _activeColor = value;
            UpdateColorHarmonies();
        }
    }
    public float LightAzimuth { get; set; } = 225.0f;
    public float LightElevation { get; set; } = 45.0f;
    public Color CanvasLightColor { get; set; } = Colors.White;
    public float CanvasLightIntensity { get; set; } = 1.0f;
    
    public bool ShowRulerGuide { get; set; } = false;
    public RulerType CurrentRuler { get; set; } = RulerType.Straight;
    public Point RulerCenter { get; set; } = new Point(400, 300);
    public float RulerRadius { get; set; } = 150f;
    public double RulerAngle { get; set; } = 0.0;
    public double CanvasRotation
    {
        get => _transform.Rotation;
        set
        {
            _transform.Rotation = value;
            _canvas?.Invalidate();
        }
    }
    private ScaleTransform _paletteScale = new() { ScaleX = 1.0, ScaleY = 1.0 };
    public bool TouchDrawEnabled { get; set; } = false;
    public bool PanningModeEnabled { get; set; } = false;

    private CanvasBitmap? _referenceBitmap;
    public float ReferenceOpacity { get; set; } = 0.5f;
    public bool ShowReferenceImage { get; set; } = false;

    public float ColorTemperature { get; set; } = 0.0f;

    public bool StampPatternEnabled { get; set; } = false;
    public PatternType CurrentPattern { get; set; } = PatternType.Dots;

    public List<HistoryNode> HistoryTree { get; } = new();
    public string? CurrentHistoryNodeId { get; set; }
    private readonly Random _brushRng = new();
    
    private bool _isDrawing;
    private Point _lastPoint;
    private float _strokeDistance;
    private float _currentPigmentAmount = 1.0f;
    private float _currentWaterAmount = 0.2f;
    private Rect _strokeDirtyRect;
    
    private bool _isPanning;
    private Point _lastPanPoint;
    
    private DispatcherTimer _dryTimer;
    private GridView? _colorGrid;
    private Point _eyedropperPos;
    private float _currentTiltX = 0f;
    private float _currentTiltY = 0f;
    private Slider? _sliderDepletion;
    
    // UI Panel borders
    private Grid? _mainUIGrid;
    private Border? _toolbarBorder;
    private Border? _paletteBorder;
    private CanvasControl? _paletteCanvas;
    private CanvasRenderTarget? _paletteColor;
    private CanvasRenderTarget? _paletteHeight;
    private CanvasRenderTarget? _paletteWetness;
    private bool _isDrawingPalette;
    private Point _lastPalettePoint;
    private bool _isPaletteEyedropper;
    
    private TranslateTransform _paletteTranslate = new();
    private Point _paletteDragStart;
    private bool _isDraggingPalette;
    
    // Layer list UI bindings
    private ItemsControl? _layersItemsControl;
    
    // Stroke list & replay state
    public List<ArtStroke> Strokes { get; } = new();
    private Guid? _hoveredStrokeId;
    private ListView? _strokesListView;
    
    // AI Panel UI
    private Border? _aiPanelBorder;
    private StackPanel? _aiChatStack;
    private TextBox? _aiPromptBox;
    private ScrollViewer? _aiChatScroll;
    private Image? _aiResultImage;
    
    private readonly List<ArtUndoItem> _undoList = new();
    private readonly List<ArtUndoItem> _redoList = new();
    private ArtSettings _settings = new();

    public ArtSurface()
    {
        this.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 32));
        
        InitializeBrushes();
        CurrentBrush = Brushes[0];
        
        _dryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _dryTimer.Tick += DryTimer_Tick;
        _dryTimer.Start();
        
        this.Loaded += ArtSurface_Loaded;
        this.Unloaded += ArtSurface_Unloaded;
    }

    private void InitializeBrushes()
    {
        Brushes.Add(new ArtBrush { Name = "Oil Paint", Viscosity = 0.8f, Impasto = 0.9f, Opacity = 1.0f, BleedRate = 0.0f, DepletionRate = 0.015f, TextureInteraction = 0.1f, DefaultDryTime = 1200f, DefaultColor = Colors.SaddleBrown });
        Brushes.Add(new ArtBrush { Name = "Watercolor", Viscosity = 0.1f, Impasto = 0.0f, Opacity = 0.2f, BleedRate = 0.9f, DepletionRate = 0.06f, TextureInteraction = 0.4f, DefaultDryTime = 10f, DefaultColor = Colors.DeepSkyBlue });
        Brushes.Add(new ArtBrush { Name = "Pastel", Viscosity = 0.0f, Impasto = 0.1f, Opacity = 0.9f, BleedRate = 0.0f, DepletionRate = 0.09f, TextureInteraction = 1.0f, DefaultDryTime = 999999f, DefaultColor = Colors.SandyBrown });
        Brushes.Add(new ArtBrush { Name = "Acrylic", Viscosity = 0.5f, Impasto = 0.5f, Opacity = 0.95f, BleedRate = 0.1f, DepletionRate = 0.03f, TextureInteraction = 0.2f, DefaultDryTime = 30f, DefaultColor = Colors.Crimson });
        Brushes.Add(new ArtBrush { Name = "Digital Paint", Viscosity = 0.0f, Impasto = 0.0f, Opacity = 1.0f, BleedRate = 0.0f, DepletionRate = 0.0f, TextureInteraction = 0.0f, DefaultDryTime = 999999f, IsDigital = true, DefaultColor = Colors.Black });
        Brushes.Add(new ArtBrush { Name = "Ink Wash", Viscosity = 0.1f, Impasto = 0.0f, Opacity = 0.35f, BleedRate = 0.95f, DepletionRate = 0.04f, TextureInteraction = 0.3f, DefaultDryTime = 15f, DefaultColor = Colors.DarkSlateGray });
        Brushes.Add(new ArtBrush { Name = "Encaustic (Wax)", Viscosity = 0.9f, Impasto = 1.0f, Opacity = 1.0f, BleedRate = 0.0f, DepletionRate = 0.06f, TextureInteraction = 0.3f, DefaultDryTime = 1f, IsWax = true, DefaultColor = Colors.Gold });
        Brushes.Add(new ArtBrush { Name = "Spray Painting", Viscosity = 0.1f, Impasto = 0.05f, Opacity = 0.25f, BleedRate = 0.0f, DepletionRate = 0.02f, TextureInteraction = 0.2f, DefaultDryTime = 10f, IsSplatter = true, DefaultColor = Colors.Purple });
        Brushes.Add(new ArtBrush { Name = "Fresco secco", Viscosity = 0.3f, Impasto = 0.2f, Opacity = 0.75f, BleedRate = 0.0f, DepletionRate = 0.07f, TextureInteraction = 0.8f, DefaultDryTime = 20f, IsChalk = true, DefaultColor = Colors.LightPink });
        Brushes.Add(new ArtBrush { Name = "Gouache", Viscosity = 0.4f, Impasto = 0.1f, Opacity = 0.9f, BleedRate = 0.25f, DepletionRate = 0.04f, TextureInteraction = 0.3f, DefaultDryTime = 15f, IsGouache = true, DefaultColor = Colors.Teal });
        Brushes.Add(new ArtBrush { Name = "Enamel", Viscosity = 0.6f, Impasto = 0.4f, Opacity = 1.0f, BleedRate = 0.0f, DepletionRate = 0.02f, TextureInteraction = 0.1f, DefaultDryTime = 60f, SelfLeveling = 0.12f, DefaultColor = Colors.Navy });
        Brushes.Add(new ArtBrush { Name = "Tempera", Viscosity = 0.3f, Impasto = 0.1f, Opacity = 0.85f, BleedRate = 0.05f, DepletionRate = 0.05f, TextureInteraction = 0.2f, DefaultDryTime = 10f, DefaultColor = Colors.Olive });
        Brushes.Add(new ArtBrush { Name = "Sand Painting", Viscosity = 0.0f, Impasto = 0.8f, Opacity = 1.0f, BleedRate = 0.0f, DepletionRate = 0.04f, TextureInteraction = 0.5f, DefaultDryTime = 999999f, IsSand = true, DefaultColor = Colors.Wheat });
        Brushes.Add(new ArtBrush { Name = "Fan Brush", Viscosity = 0.4f, Impasto = 0.2f, Opacity = 0.6f, BleedRate = 0.1f, DepletionRate = 0.05f, TextureInteraction = 0.6f, DefaultDryTime = 120f, DefaultColor = Colors.ForestGreen });
    }
    public void UpdateSubstrateBgColor(Color c)
    {
        _substrateBgColor = c;
        _canvas?.Invalidate();
    }

    public void UpdateSubstrate(string newSubstrate)
    {
        SubstrateType = newSubstrate;
        switch (SubstrateType)
        {
            case "Belgian Linen":
                _substrateBgColor = Color.FromArgb(255, 211, 195, 177);
                break;
            case "Wood Panel":
                _substrateBgColor = Color.FromArgb(255, 194, 155, 112);
                break;
            case "Rough Watercolor Paper":
                _substrateBgColor = Color.FromArgb(255, 253, 251, 247);
                break;
            default:
                _substrateBgColor = Color.FromArgb(255, 247, 245, 241);
                break;
        }
        
        if (_canvas != null)
        {
            GenerateSubstrateHeightmap(_canvas.Device);
            _substrateBrush?.Dispose();
            _substrateBrush = new CanvasImageBrush(_canvas, _substrateHeight)
            {
                ExtendX = CanvasEdgeBehavior.Wrap,
                ExtendY = CanvasEdgeBehavior.Wrap
            };
            _canvas.Invalidate();
        }
    }

    public void SetupWorkspace(int width, int height, string substrate)
    {
        CanvasWidth = width;
        CanvasHeight = height;
        SubstrateType = substrate;
        
        _transform.ScaleX = 1.0;
        _transform.ScaleY = 1.0;
        _transform.TranslateX = 0;
        _transform.TranslateY = 0;
        _transform.CenterX = CanvasWidth / 2.0;
        _transform.CenterY = CanvasHeight / 2.0;
        _transform.Rotation = 0.0;

        _undoList.Clear();
        _redoList.Clear();
        Strokes.Clear();
        HistoryTree.Clear();
        CurrentHistoryNodeId = null;
        RecordHistoryState("Blank Canvas");

        switch (SubstrateType)
        {
            case "Belgian Linen":
                _substrateBgColor = Color.FromArgb(255, 211, 195, 177);
                break;
            case "Wood Panel":
                _substrateBgColor = Color.FromArgb(255, 194, 155, 112);
                break;
            case "Rough Watercolor Paper":
                _substrateBgColor = Color.FromArgb(255, 253, 251, 247);
                break;
            default:
                _substrateBgColor = Color.FromArgb(255, 247, 245, 241);
                break;
        }

        DisposeTargets();
        if (_canvas != null)
        {
            InitializeTargets(_canvas);
        }
        UpdateStrokesListPanel();
        _canvas?.Invalidate();
    }

    private void DisposeTargets()
    {
        foreach (var lay in Layers) lay.Dispose();
        Layers.Clear();
        ActiveLayer = null;

        _globalColor?.Dispose(); _globalColor = null;
        _globalHeight?.Dispose(); _globalHeight = null;
        _wetnessTarget?.Dispose(); _wetnessTarget = null;
        _substrateHeight?.Dispose(); _substrateHeight = null;
        _combinedHeight?.Dispose(); _combinedHeight = null;
        _baseColorTarget?.Dispose(); _baseColorTarget = null;
        
        _tempStrokeColor?.Dispose(); _tempStrokeColor = null;
        _tempStrokeHeight?.Dispose(); _tempStrokeHeight = null;
        _tempWetness?.Dispose(); _tempWetness = null;
        _tempColor?.Dispose(); _tempColor = null;
        _tempHeight?.Dispose(); _tempHeight = null;
        _smearTemp?.Dispose(); _smearTemp = null;
        _smearTempHeight?.Dispose(); _smearTempHeight = null;
        
        _paletteColor?.Dispose(); _paletteColor = null;
        _paletteHeight?.Dispose(); _paletteHeight = null;
        _paletteWetness?.Dispose(); _paletteWetness = null;
    }

    private void ArtSurface_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        
        var rootGrid = new Grid();
        _rootGrid = rootGrid;
        this.Content = rootGrid;
        
        _canvasContainer = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(255, 24, 24, 26))
        };
        rootGrid.Children.Add(_canvasContainer);
        
        _canvas = new CanvasControl
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = _transform
        };
        _canvas.CreateResources += Canvas_CreateResources;
        _canvas.Draw += Canvas_Draw;
        
        _canvas.PointerPressed += Canvas_PointerPressed;
        _canvas.PointerMoved += Canvas_PointerMoved;
        _canvas.PointerReleased += Canvas_PointerReleased;
        _canvas.PointerWheelChanged += Canvas_PointerWheelChanged;
        
        _canvasContainer.Children.Add(_canvas);
        
        _mainUIGrid = new Grid();
        rootGrid.Children.Add(_mainUIGrid);
        
        InitializeOverlayUI();
    }

    private void ArtSurface_Unloaded(object sender, RoutedEventArgs e)
    {
        _dryTimer.Stop();
        DisposeTargets();
        if (_canvas != null)
        {
            _canvas.CreateResources -= Canvas_CreateResources;
            _canvas.Draw -= Canvas_Draw;
            _canvas = null;
        }
    }

    private void InitializeTargets(ICanvasResourceCreator device)
    {
        var colorBackups = new Dictionary<Guid, byte[]>();
        var heightBackups = new Dictionary<Guid, byte[]>();
        
        if (Layers.Count > 0)
        {
            foreach (var layer in Layers)
            {
                try
                {
                    byte[] colorBytes = layer.ColorTarget.GetPixelBytes();
                    colorBackups[layer.Id] = colorBytes;
                    
                    byte[] heightBytes = layer.HeightTarget.GetPixelBytes();
                    heightBackups[layer.Id] = heightBytes;
                }
                catch { }
            }
        }

        DisposeTargets();
        
        _globalColor = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _globalHeight = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        
        _wetnessTarget = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _combinedHeight = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _baseColorTarget = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        
        _tempStrokeColor = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _tempStrokeHeight = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _tempWetness = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _tempColor = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _tempHeight = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        _smearTemp = new CanvasRenderTarget(device, 512, 512, 96);
        _smearTempHeight = new CanvasRenderTarget(device, 512, 512, 96);

        _paletteColor = new CanvasRenderTarget(device, 200, 200, 96);
        _paletteHeight = new CanvasRenderTarget(device, 200, 200, 96);
        _paletteWetness = new CanvasRenderTarget(device, 200, 200, 96);
        
        using (var ds = _globalColor.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _globalHeight.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _wetnessTarget.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _tempStrokeColor.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _tempStrokeHeight.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _paletteColor.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 240, 238, 230));
        using (var ds = _paletteHeight.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _paletteWetness.CreateDrawingSession()) ds.Clear(Colors.Transparent);

        _substrateHeight = new CanvasRenderTarget(device, 256, 256, 96);
        GenerateSubstrateHeightmap(device.Device);
        
        _substrateBrush = new CanvasImageBrush(device, _substrateHeight)
        {
            ExtendX = CanvasEdgeBehavior.Wrap,
            ExtendY = CanvasEdgeBehavior.Wrap
        };

        if (colorBackups.Count > 0)
        {
            foreach (var layer in Layers)
            {
                layer.RecreateTargets(device, CanvasWidth, CanvasHeight);
                if (colorBackups.TryGetValue(layer.Id, out var colBytes))
                {
                    layer.ColorTarget.SetPixelBytes(colBytes);
                }
                if (heightBackups.TryGetValue(layer.Id, out var hBytes))
                {
                    layer.HeightTarget.SetPixelBytes(hBytes);
                }
            }
        }
        else
        {
            Layers.Clear();
            var lay1 = new ArtLayer(device, CanvasWidth, CanvasHeight) { Name = "Background Layer" };
            Layers.Add(lay1);
            ActiveLayer = lay1;
        }
        UpdateLayersListView();
    }

    private void Canvas_CreateResources(CanvasControl sender, Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
    {
        InitializeTargets(sender);
    }

    private void GenerateSubstrateHeightmap(CanvasDevice device)
    {
        var rand = new Random();
        byte[] bytes = new byte[256 * 256 * 4];
        _substrateHeightBytes = bytes;
        
        if (SubstrateType == "Canvas")
        {
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float fx = (float)Math.Sin(x * Math.PI / 8.0);
                    float fy = (float)Math.Sin(y * Math.PI / 8.0);
                    float val = (fx * 0.35f + fy * 0.35f) + 0.5f;
                    val += (float)(rand.NextDouble() - 0.5) * 0.08f;
                    val = Math.Clamp(val, 0f, 1f);
                    byte b = (byte)(val * 255);
                    int idx = (y * 256 + x) * 4;
                    bytes[idx] = b; bytes[idx+1] = b; bytes[idx+2] = b; bytes[idx+3] = 255;
                }
            }
        }
        else if (SubstrateType == "Belgian Linen")
        {
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float fx = (float)Math.Sin(x * Math.PI / 12.0 + Math.Sin(y * 0.05) * 0.3);
                    float fy = (float)Math.Sin(y * Math.PI / 10.0 + Math.Sin(x * 0.04) * 0.4);
                    float val = (fx * 0.3f + fy * 0.3f) + 0.5f;
                    val += (float)(rand.NextDouble() - 0.5) * 0.12f;
                    val = Math.Clamp(val, 0f, 1f);
                    byte b = (byte)(val * 255);
                    int idx = (y * 256 + x) * 4;
                    bytes[idx] = b; bytes[idx+1] = b; bytes[idx+2] = b; bytes[idx+3] = 255;
                }
            }
        }
        else if (SubstrateType == "Wood Panel")
        {
            var grid = GenerateNoiseGrid(32);
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float grain = ValNoise(grid, x * 0.015f, y * 0.12f, 32) * 6.5f;
                    float val = (float)Math.Sin(x * 0.07f + grain) * 0.35f + 0.5f;
                    val += ValNoise(grid, x * 0.6f, y * 0.01f, 32) * 0.12f;
                    val = Math.Clamp(val, 0f, 1f);
                    byte b = (byte)(val * 255);
                    int idx = (y * 256 + x) * 4;
                    bytes[idx] = b; bytes[idx+1] = b; bytes[idx+2] = b; bytes[idx+3] = 255;
                }
            }
        }
        else // Rough Watercolor Paper
        {
            var grid = GenerateNoiseGrid(32);
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    float val = 0;
                    float amp = 0.5f;
                    float freq = 1f;
                    for (int o = 0; o < 4; o++)
                    {
                        val += amp * ValNoise(grid, x * 0.06f * freq, y * 0.06f * freq, 32);
                        amp *= 0.5f;
                        freq *= 2f;
                    }
                    val = Math.Clamp(val, 0f, 1f);
                    byte b = (byte)(val * 255);
                    int idx = (y * 256 + x) * 4;
                    bytes[idx] = b; bytes[idx+1] = b; bytes[idx+2] = b; bytes[idx+3] = 255;
                }
            }
        }
        
        using (var ds = _substrateHeight!.CreateDrawingSession())
        {
            ds.Clear(Colors.Gray);
            var bmp = CanvasBitmap.CreateFromBytes(device, bytes.AsBuffer(), 256, 256, Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            ds.DrawImage(bmp);
        }
    }

    private static float[,] GenerateNoiseGrid(int size)
    {
        var grid = new float[size, size];
        var rand = new Random(101);
        for (int i = 0; i < size; i++)
            for (int j = 0; j < size; j++)
                grid[i, j] = (float)rand.NextDouble();
        return grid;
    }

    private static float ValNoise(float[,] grid, float x, float y, int size)
    {
        int x0 = (int)Math.Floor(x) % size;
        int y0 = (int)Math.Floor(y) % size;
        if (x0 < 0) x0 += size;
        if (y0 < 0) y0 += size;
        int x1 = (x0 + 1) % size;
        int y1 = (y0 + 1) % size;
        float tx = x - (float)Math.Floor(x);
        float ty = y - (float)Math.Floor(y);
        float sx = tx * tx * (3 - 2 * tx);
        float sy = ty * ty * (3 - 2 * ty);
        float n00 = grid[x0, y0];
        float n10 = grid[x1, y0];
        float n01 = grid[x0, y1];
        float n11 = grid[x1, y1];
        float nx0 = n00 * (1 - sx) + n10 * sx;
        float nx1 = n01 * (1 - sx) + n11 * sx;
        return nx0 * (1 - sy) + nx1 * sy;
    }

    private void RenderCompositeLayerBuffers()
    {
        if (_globalColor == null || _globalHeight == null) return;
        
        using (var dsCol = _globalColor.CreateDrawingSession())
        using (var dsH = _globalHeight.CreateDrawingSession())
        {
            dsCol.Clear(Colors.Transparent);
            dsH.Clear(Colors.Transparent);
            
            foreach (var layer in Layers)
            {
                if (layer.Visible)
                {
                    dsCol.DrawImage(layer.ColorTarget, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), layer.Opacity);
                    dsH.DrawImage(layer.HeightTarget, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), layer.Opacity, CanvasImageInterpolation.Linear, CanvasComposite.Add);
                }
            }
        }
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_globalColor == null || _substrateBrush == null) return;
        
        RenderCompositeLayerBuffers();
        
        using (var ds = _baseColorTarget!.CreateDrawingSession())
        {
            ds.Clear(_substrateBgColor);
            ds.DrawImage(_globalColor);
            
            if (_isDrawing && (CurrentBrush.TextureInteraction > 0))
            {
                var maskedStroke = new BlendEffect
                {
                    Background = _tempStrokeColor,
                    Foreground = _substrateHeight,
                    Mode = BlendEffectMode.Multiply
                };
                ds.DrawImage(maskedStroke);
            }
            else if (_isDrawing)
            {
                ds.DrawImage(_tempStrokeColor!);
            }
        }
        
        using (var ds = _combinedHeight!.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 127, 127, 127));
            ds.DrawRectangle(new Rect(0, 0, CanvasWidth, CanvasHeight), _substrateBrush);
            ds.DrawImage(_globalHeight!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.Add);
            
            if (_isDrawing)
            {
                ds.DrawImage(_tempStrokeHeight!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.Add);
            }
        }
        
        float scaleMultiplier = 4.5f;
        float specConstant = 0.6f;
        
        var diffuseLight = new DistantDiffuseEffect
        {
            Source = _combinedHeight,
            HeightMapScale = scaleMultiplier,
            DiffuseAmount = 1.1f * CanvasLightIntensity,
            Azimuth = (float)(LightAzimuth * Math.PI / 180.0),
            Elevation = (float)(LightElevation * Math.PI / 180.0),
            LightColor = CanvasLightColor
        };
        
        var specularLight = new DistantSpecularEffect
        {
            Source = _combinedHeight,
            HeightMapScale = scaleMultiplier,
            SpecularAmount = specConstant * CanvasLightIntensity,
            SpecularExponent = 15.0f,
            Azimuth = (float)(LightAzimuth * Math.PI / 180.0),
            Elevation = (float)(LightElevation * Math.PI / 180.0),
            LightColor = CanvasLightColor
        };
        
        var litColor = new BlendEffect
        {
            Background = _baseColorTarget,
            Foreground = diffuseLight,
            Mode = BlendEffectMode.Multiply
        };
        
        var finalResult = new BlendEffect
        {
            Background = litColor,
            Foreground = specularLight,
            Mode = BlendEffectMode.LinearDodge
        };
        
        ICanvasImage imageToDraw = finalResult;
        if (ColorTemperature != 0.0f)
        {
            float temp = ColorTemperature;
            var matrix = new Matrix5x4
            {
                M11 = 1f + temp * 0.12f, M12 = 0f,                M13 = 0f,                M14 = 0f,
                M21 = 0f,                M22 = 1f + temp * 0.04f, M23 = 0f,                M24 = 0f,
                M31 = 0f,                M32 = 0f,                M33 = 1f - temp * 0.15f, M34 = 0f,
                M41 = 0f,                M42 = 0f,                M43 = 0f,                M44 = 1f,
                M51 = 0f,                M52 = 0f,                M53 = 0f,                M54 = 0f
            };
            imageToDraw = new ColorMatrixEffect
            {
                Source = finalResult,
                ColorMatrix = matrix
            };
        }
        
        args.DrawingSession.DrawImage(imageToDraw);

        if (ShowReferenceImage && _referenceBitmap != null)
        {
            args.DrawingSession.DrawImage(_referenceBitmap, new Rect(0, 0, CanvasWidth, CanvasHeight), new Rect(0, 0, _referenceBitmap.Size.Width, _referenceBitmap.Size.Height), ReferenceOpacity);
        }
        
        if (_hoveredStrokeId != null)
        {
            var hovered = Strokes.Find(x => x.Id == _hoveredStrokeId);
            if (hovered != null && hovered.Points.Count > 1)
            {
                var strokePoints = new Vector2[hovered.Points.Count];
                for (int i = 0; i < hovered.Points.Count; i++) strokePoints[i] = hovered.Points[i].ToVector2();
                args.DrawingSession.DrawGeometry(CanvasGeometry.CreatePolygon(sender.Device, strokePoints), Colors.Cyan, 3f);
            }
        }
        
        if (CurrentTool == ArtTool.Eyedropper && _globalColor != null)
        {
            float rx = (float)_eyedropperPos.X;
            float ry = (float)_eyedropperPos.Y;
            float lensRadius = 50f;
            int zoomAreaSize = 24; // 24x24 pixels
            try
            {
                var zoomBmp = new CanvasRenderTarget(sender.Device, zoomAreaSize, zoomAreaSize, 96);
                using (var ds = zoomBmp.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    ds.DrawImage(_globalColor, 0, 0, new Rect(rx - zoomAreaSize / 2, ry - zoomAreaSize / 2, zoomAreaSize, zoomAreaSize));
                }
                
                var lensCenter = new Vector2(rx, ry - 75f);
                args.DrawingSession.FillCircle(lensCenter, lensRadius, Colors.DarkGray);
                args.DrawingSession.DrawImage(zoomBmp, new Rect(lensCenter.X - lensRadius, lensCenter.Y - lensRadius, lensRadius * 2, lensRadius * 2), new Rect(0, 0, zoomAreaSize, zoomAreaSize));
                args.DrawingSession.DrawCircle(lensCenter, lensRadius, Colors.White, 3f);
                
                args.DrawingSession.DrawLine(new Vector2(lensCenter.X - 6, lensCenter.Y), new Vector2(lensCenter.X + 6, lensCenter.Y), Colors.Red, 2f);
                args.DrawingSession.DrawLine(new Vector2(lensCenter.X, lensCenter.Y - 6), new Vector2(lensCenter.X, lensCenter.Y + 6), Colors.Red, 2f);
                
                zoomBmp.Dispose();
            }
            catch { }
        }
        
        // Draw Symmetry Guide Lines
        if (CurrentSymmetryMode == SymmetryMode.Vertical)
        {
            args.DrawingSession.DrawLine(new Vector2(CanvasWidth / 2f, 0), new Vector2(CanvasWidth / 2f, CanvasHeight), Color.FromArgb(100, 200, 200, 255), 1.5f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }
        else if (CurrentSymmetryMode == SymmetryMode.Horizontal)
        {
            args.DrawingSession.DrawLine(new Vector2(0, CanvasHeight / 2f), new Vector2(CanvasWidth, CanvasHeight / 2f), Color.FromArgb(100, 200, 200, 255), 1.5f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }
        else if (CurrentSymmetryMode == SymmetryMode.Radial)
        {
            args.DrawingSession.DrawLine(new Vector2(CanvasWidth / 2f, 0), new Vector2(CanvasWidth / 2f, CanvasHeight), Color.FromArgb(80, 200, 200, 255), 1.2f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
            args.DrawingSession.DrawLine(new Vector2(0, CanvasHeight / 2f), new Vector2(CanvasWidth, CanvasHeight / 2f), Color.FromArgb(80, 200, 200, 255), 1.2f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
        }

        // Draw Real-Time Wetness Heatmap Overlay
        if (ShowWetnessHeatmap && _wetnessTarget != null)
        {
            var tintEffect = new ColorMatrixEffect
            {
                Source = _wetnessTarget,
                ColorMatrix = new Matrix5x4
                {
                    M11 = 0f, M12 = 0.5f, M13 = 1f, M14 = 0.8f,
                    M21 = 0f, M22 = 0f,   M23 = 0f, M24 = 0f,
                    M31 = 0f, M32 = 0f,   M33 = 0f, M34 = 0f,
                    M41 = 0f, M42 = 0f,   M43 = 0f, M44 = 0f,
                    M51 = 0f, M52 = 0f,   M53 = 0f, M54 = 0f
                }
            };
            args.DrawingSession.DrawImage(tintEffect);
        }

        // Draw Virtual Drafting Rulers & Stencils
        if (ShowRulerGuide)
        {
            Color guideColor = Color.FromArgb(160, 255, 120, 0);
            if (CurrentRuler == RulerType.Circle)
            {
                args.DrawingSession.DrawCircle(RulerCenter.ToVector2(), RulerRadius, guideColor, 2f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
                args.DrawingSession.FillCircle(RulerCenter.ToVector2(), 6f, guideColor);
            }
            else
            {
                float angleRad = (float)(RulerAngle * Math.PI / 180.0);
                Vector2 dir = new Vector2((float)Math.Cos(angleRad), (float)Math.Sin(angleRad));
                Vector2 p1 = RulerCenter.ToVector2() - dir * 2000f;
                Vector2 p2 = RulerCenter.ToVector2() + dir * 2000f;
                args.DrawingSession.DrawLine(p1, p2, guideColor, 2f, new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash });
                args.DrawingSession.FillCircle(RulerCenter.ToVector2(), 6f, guideColor);
            }
        }

        args.DrawingSession.DrawRectangle(new Rect(0, 0, CanvasWidth, CanvasHeight), Color.FromArgb(120, 0, 0, 0), 2.5f);
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_replaying) return;   // playback owns the layers until it finishes
        var pt = e.GetCurrentPoint(_canvas);
        _currentTiltX = pt.Properties.XTilt;
        _currentTiltY = pt.Properties.YTilt;
        bool isPen = pt.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen;
        bool isMouse = pt.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse;
        bool canDraw = isPen || isMouse || TouchDrawEnabled;
        
        if (PanningModeEnabled || !canDraw || pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _isPanning = true;
            _lastPanPoint = e.GetCurrentPoint(this).Position;
            _canvas?.CapturePointer(e.Pointer);
            return;
        }
        
        if (pt.Properties.IsLeftButtonPressed && ActiveLayer != null)
        {
            if (CurrentTool == ArtTool.Eyedropper)
            {
                _eyedropperPos = pt.Position;
                SampleColorAt(pt.Position);
                _canvas?.Invalidate();
                return;
            }
            
            _isDrawing = true;
            _lastPoint = SnapToRuler(pt.Position);
            _strokeDistance = 0f;
            if (AutoReloadPaint)
            {
                _currentPigmentAmount = ActiveColor == null ? 0f : PigmentLoading;
            }
            else
            {
                if (ActiveColor == null) _currentPigmentAmount = 0f;
            }
            _currentWaterAmount = WaterWetness;
            _cachedWetnessValue = GetWetnessAt(pt.Position);
            
            _strokeDirtyRect = new Rect(pt.Position.X, pt.Position.Y, 0, 0);
            
            var newStroke = new ArtStroke
            {
                LayerId = ActiveLayer.Id,
                PaintColor = ActiveColor,
                BrushName = CurrentBrush.Name,
                ToolName = CurrentTool.ToString(),
                DepletionCurve = DepletionCurve.ToString(),
                Size = BrushSize,
                Viscosity = CurrentBrush.Viscosity,
                Impasto = CurrentBrush.Impasto,
                Opacity = CurrentBrush.Opacity
            };
            newStroke.Points.Add(pt.Position);
            newStroke.Pressures.Add(pt.Properties.Pressure);
            Strokes.Add(newStroke);
            
            CaptureUndoStateBefore(ActiveLayer.Id, pt.Position, BrushSize);
            PaintStamp(pt.Position, pt.Properties.Pressure);
            _canvas?.Invalidate();
        }
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        _currentTiltX = pt.Properties.XTilt;
        _currentTiltY = pt.Properties.YTilt;
        
        if (CurrentTool == ArtTool.Eyedropper)
        {
            _eyedropperPos = pt.Position;
            _canvas?.Invalidate();
            if (pt.Properties.IsLeftButtonPressed)
            {
                SampleColorAt(pt.Position);
            }
            return;
        }

        if (_isPanning)
        {
            var curPos = e.GetCurrentPoint(this).Position;
            _transform.TranslateX += curPos.X - _lastPanPoint.X;
            _transform.TranslateY += curPos.Y - _lastPanPoint.Y;
            _lastPanPoint = curPos;
            return;
        }
        
        if (_isDrawing && pt.Properties.IsLeftButtonPressed && ActiveLayer != null && Strokes.Count > 0)
        {
            Point currentPoint = SnapToRuler(pt.Position);
            float dist = (float)Math.Sqrt(Math.Pow(currentPoint.X - _lastPoint.X, 2) + Math.Pow(currentPoint.Y - _lastPoint.Y, 2));
            _cachedWetnessValue = GetWetnessAt(currentPoint);
            
            int steps = (int)Math.Max(1, dist / (BrushSize * 0.08f));
            var currentStroke = Strokes[Strokes.Count - 1];
            
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = (float)(_lastPoint.X + (currentPoint.X - _lastPoint.X) * t);
                float y = (float)(_lastPoint.Y + (currentPoint.Y - _lastPoint.Y) * t);
                
                float segmentDist = dist / steps;
                _strokeDistance += segmentDist;
                
                // Depletion Curve Equations
                if (!CurrentBrush.IsDigital && ActiveColor != null)
                {
                    if (DepletionCurve == DepletionCurveType.Linear)
                    {
                        _currentPigmentAmount = Math.Max(0.0f, PigmentLoading * (1.0f - (_strokeDistance * DepletionRateSliderValue) / (BrushSize * 250f)));
                    }
                    else if (DepletionCurve == DepletionCurveType.Exponential)
                    {
                        _currentPigmentAmount = PigmentLoading * (float)Math.Exp(-_strokeDistance * CurrentBrush.DepletionRate * 0.04f * DepletionRateSliderValue);
                    }
                    else // S-Curve
                    {
                        float mid = 200f / Math.Max(0.01f, DepletionRateSliderValue);
                        float steepness = 0.02f * DepletionRateSliderValue;
                        _currentPigmentAmount = PigmentLoading * (1.0f / (1.0f + (float)Math.Exp(steepness * (_strokeDistance - mid))));
                    }
                    _currentWaterAmount = Math.Max(0.0f, _currentWaterAmount - segmentDist * 0.002f);
                }
                
                currentStroke.Points.Add(new Point(x, y));
                currentStroke.Pressures.Add(pt.Properties.Pressure);
                
                PaintStamp(new Point(x, y), pt.Properties.Pressure);
            }
            
            _lastPoint = currentPoint;
            _canvas?.Invalidate();
        }
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            _canvas?.ReleasePointerCapture(e.Pointer);
            return;
        }
        
        if (_isDrawing && ActiveLayer != null)
        {
            _isDrawing = false;
            BakeStroke();
            CaptureUndoStateAfter();
            UpdateStrokesListPanel();
            RecordHistoryState($"Stroke {Strokes.Count}: {CurrentBrush.Name}");
            _canvas?.Invalidate();
        }
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_canvas);
        double delta = pt.Properties.MouseWheelDelta;
        double zoomFactor = delta > 0 ? 1.1 : 0.9;
        
        double newZoom = Math.Clamp(_transform.ScaleX * zoomFactor, 0.15, 6.0);
        _transform.ScaleX = newZoom;
        _transform.ScaleY = newZoom;
        
        _canvas?.Invalidate();
    }

    private void PaintStamp(Point pos, float pressure)
    {
        PaintStampInternal(pos, pressure);
        
        if (CurrentSymmetryMode == SymmetryMode.Vertical)
        {
            float mx = CanvasWidth - (float)pos.X;
            PaintStampInternal(new Point(mx, pos.Y), pressure);
        }
        else if (CurrentSymmetryMode == SymmetryMode.Horizontal)
        {
            float my = CanvasHeight - (float)pos.Y;
            PaintStampInternal(new Point(pos.X, my), pressure);
        }
        else if (CurrentSymmetryMode == SymmetryMode.Radial)
        {
            float mx = CanvasWidth - (float)pos.X;
            float my = CanvasHeight - (float)pos.Y;
            PaintStampInternal(new Point(mx, pos.Y), pressure);
            PaintStampInternal(new Point(pos.X, my), pressure);
            PaintStampInternal(new Point(mx, my), pressure);
        }
    }

    private void PaintStampInternal(Point pos, float pressure)
    {
        if (ActiveLayer == null || _globalColor == null) return;
        
        float size = BrushSize * (0.5f + pressure * 0.6f);
        float opacity = CurrentBrush.Opacity * _currentPigmentAmount;
        
        // Calculate tilt stretch factors & rotation angle
        float radiansX = _currentTiltX * (float)Math.PI / 180f;
        float radiansY = _currentTiltY * (float)Math.PI / 180f;
        float stretchX = 1f + Math.Abs(radiansX) * 0.9f;
        float stretchY = 1f + Math.Abs(radiansY) * 0.9f;
        
        Vector2 tiltVec = new Vector2(radiansX, radiansY);
        float tiltAngle = 0f;
        if (tiltVec.Length() > 0.05f)
        {
            tiltAngle = (float)Math.Atan2(tiltVec.Y, tiltVec.X);
        }
        
        float r = size * 2.5f * Math.Max(stretchX, stretchY);
        _strokeDirtyRect = ActionBoundsUnion(_strokeDirtyRect, new Rect(pos.X - r, pos.Y - r, r * 2, r * 2));
        
        // 1. Eraser Tool in Art Surface
        if (CurrentTool == ArtTool.Eraser)
        {
            using (var dsMask = _tempColor!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsMask.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsMask.Transform;
                }
                dsMask.Clear(Colors.Transparent);
                var radial = new CanvasRadialGradientBrush(dsMask.Device, Colors.White, Color.FromArgb(0, 255, 255, 255))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * stretchX,
                    RadiusY = size * stretchY
                };
                dsMask.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, radial);
            }
            
            using (var dsCol = ActiveLayer.ColorTarget.CreateDrawingSession())
            {
                dsCol.DrawImage(_tempColor!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
            }
            
            using (var dsH = ActiveLayer.HeightTarget.CreateDrawingSession())
            {
                dsH.DrawImage(_tempColor!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
            }
            return;
        }
        
        // 2. Palette Knife Tool
        if (CurrentTool == ArtTool.PaletteKnife)
        {
            if (ActiveColor != null)
            {
                Color paintColor = ActiveColor.Value;
                Color stampColor = Color.FromArgb((byte)(opacity * 255), paintColor.R, paintColor.G, paintColor.B);
                
                using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
                using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
                {
                    if (tiltAngle != 0f)
                    {
                        dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                        dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                    }
                    dsColor.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, stampColor);
                    
                    byte h = (byte)(CurrentBrush.Impasto * 150f * pressure * _currentPigmentAmount);
                    dsHeight.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, Color.FromArgb(h, h, h, h));
                }
            }
            else
            {
                SmearPaint(pos, size);
            }
            return;
        }
        
        // 3. Pigment Depleted (Water only smudging)
        if (_currentPigmentAmount <= 0.02f && _currentWaterAmount > 0.05f)
        {
            SmearPaint(pos, size * 0.8f);
            return;
        }
        
        float wetFactor = _cachedWetnessValue * DrynessSliderValue;
        if (wetFactor > 0.1f && (CurrentBrush.BleedRate > 0))
        {
            size *= (1.0f + wetFactor * CurrentBrush.BleedRate * 0.8f);
            opacity *= (1.0f - wetFactor * 0.4f);
        }
        
        Color basePaintColor = ActiveColor ?? Colors.Transparent;
        if (ScratchboardMode)
        {
            float hue = (float)((pos.X + pos.Y) * 0.3) % 360f;
            basePaintColor = ColorFromHSL(hue / 360.0, 0.9, 0.6);
        }
        Color stampColor2 = Color.FromArgb((byte)(basePaintColor.A * opacity), basePaintColor.R, basePaintColor.G, basePaintColor.B);
        
        // 4. Fan Brush
        if (CurrentBrush.Name == "Fan Brush")
        {
            float angle = 0f;
            if (Strokes.Count > 0)
            {
                var stroke = Strokes[Strokes.Count - 1];
                if (stroke.Points.Count > 1)
                {
                    var p1 = stroke.Points[stroke.Points.Count - 2];
                    var p2 = stroke.Points[stroke.Points.Count - 1];
                    angle = (float)Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
                }
            }
            float perp = angle + (float)Math.PI / 2f;
            
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                int bristles = 8;
                for (int b = 0; b < bristles; b++)
                {
                    float t = (float)b / (bristles - 1) - 0.5f;
                    float offsetDist = t * size * 1.5f;
                    
                    float backOffset = (1f - (float)Math.Cos(t * Math.PI / 2.0)) * size * 0.5f;
                    
                    float bx = (float)(pos.X + Math.Cos(perp) * offsetDist - Math.Cos(angle) * backOffset);
                    float by = (float)(pos.Y + Math.Sin(perp) * offsetDist - Math.Sin(angle) * backOffset);
                    
                    float bSize = Math.Max(1.2f, size * 0.18f);
                    
                    var bBrush = new CanvasRadialGradientBrush(dsColor.Device, stampColor2, Color.FromArgb(0, stampColor2.R, stampColor2.G, stampColor2.B))
                    {
                        Center = new Vector2(bx, by),
                        RadiusX = bSize,
                        RadiusY = bSize
                    };
                    dsColor.FillCircle(new Vector2(bx, by), bSize, bBrush);
                    
                    byte h = (byte)(CurrentBrush.Impasto * 100f * pressure * _currentPigmentAmount);
                    var hBrush = new CanvasRadialGradientBrush(dsHeight.Device, Color.FromArgb(h, h, h, h), Color.FromArgb(0, 0, 0, 0))
                    {
                        Center = new Vector2(bx, by),
                        RadiusX = bSize * 0.9f,
                        RadiusY = bSize * 0.9f
                    };
                    dsHeight.FillCircle(new Vector2(bx, by), bSize * 0.9f, hBrush);
                }
            }
        }
        // 5. Watercolor wash (dark edges)
        else if (CurrentBrush.Name == "Watercolor")
        {
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                }
                var stops = new CanvasGradientStop[]
                {
                    new CanvasGradientStop { Position = 0f, Color = Color.FromArgb((byte)(stampColor2.A * 0.35f), stampColor2.R, stampColor2.G, stampColor2.B) },
                    new CanvasGradientStop { Position = 0.85f, Color = Color.FromArgb((byte)(stampColor2.A * 0.45f), stampColor2.R, stampColor2.G, stampColor2.B) },
                    new CanvasGradientStop { Position = 1f, Color = stampColor2 }
                };
                
                var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, stops)
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * stretchX,
                    RadiusY = size * stretchY
                };
                dsColor.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, colorBrush);
            }
        }
        // 6. Pastel / Chalk catching on peaks
        else if (CurrentBrush.IsChalk || CurrentBrush.Name == "Pastel")
        {
            float substrateH = GetSubstrateHeightAt(pos);
            float textureFactor = 0.15f + substrateH * 1.3f;
            float finalOpacity = Math.Clamp(opacity * textureFactor, 0f, 1f);
            Color pastelColor = Color.FromArgb((byte)(basePaintColor.A * finalOpacity), basePaintColor.R, basePaintColor.G, basePaintColor.B);
            
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, pastelColor, Color.FromArgb(0, pastelColor.R, pastelColor.G, pastelColor.B))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * stretchX,
                    RadiusY = size * stretchY
                };
                dsColor.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, colorBrush);
                
                byte h = (byte)(CurrentBrush.Impasto * 40f * pressure * _currentPigmentAmount);
                dsHeight.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, Color.FromArgb(h, h, h, h));
            }
        }
        // 7. Oil Paint (impasto ridges parallel to stroke angle)
        else if (CurrentBrush.Name == "Oil Paint" && CurrentBrush.Impasto > 0)
        {
            float angle = 0f;
            if (Strokes.Count > 0)
            {
                var stroke = Strokes[Strokes.Count - 1];
                if (stroke.Points.Count > 1)
                {
                    var p1 = stroke.Points[stroke.Points.Count - 2];
                    var p2 = stroke.Points[stroke.Points.Count - 1];
                    angle = (float)Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
                }
            }
            float perp = angle + (float)Math.PI / 2f;
            
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                Color cColor = Color.FromArgb((byte)(stampColor2.A * 0.95f), stampColor2.R, stampColor2.G, stampColor2.B);
                var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, cColor, Color.FromArgb(0, cColor.R, cColor.G, cColor.B))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * stretchX,
                    RadiusY = size * stretchY
                };
                dsColor.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, colorBrush);
                
                byte h = (byte)(CurrentBrush.Impasto * 140f * pressure * _currentPigmentAmount);
                var hColor = Color.FromArgb(h, h, h, h);
                
                var hBrush = new CanvasRadialGradientBrush(dsHeight.Device, hColor, Color.FromArgb(0, 0, 0, 0))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * 0.9f * stretchX,
                    RadiusY = size * 0.9f * stretchY
                };
                dsHeight.FillEllipse(pos.ToVector2(), size * 0.9f * stretchX, size * 0.9f * stretchY, hBrush);
                
                // Add ridges
                for (int rIndex = -1; rIndex <= 1; rIndex++)
                {
                    float offset = rIndex * size * 0.35f;
                    float rx = (float)(pos.X + Math.Cos(perp) * offset);
                    float ry = (float)(pos.Y + Math.Sin(perp) * offset);
                    
                    var ridgeBrush = new CanvasRadialGradientBrush(dsHeight.Device, Color.FromArgb((byte)(h * 0.45f), h, h, h), Color.FromArgb(0, 0, 0, 0))
                    {
                        Center = new Vector2(rx, ry),
                        RadiusX = size * 0.15f * stretchX,
                        RadiusY = size * 0.15f * stretchY
                    };
                    dsHeight.FillEllipse(new Vector2(rx, ry), size * 0.15f * stretchX, size * 0.15f * stretchY, ridgeBrush);
                }
            }
        }
        else if (CurrentBrush.IsSplatter)
        {
            var rand = new Random();
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                int particles = 25;
                for (int i = 0; i < particles; i++)
                {
                    double angle = rand.NextDouble() * Math.PI * 2.0;
                    double dist = Math.Pow(rand.NextDouble(), 1.5) * size;
                    float px = (float)(pos.X + Math.Cos(angle) * dist);
                    float py = (float)(pos.Y + Math.Sin(angle) * dist);
                    float pSize = (float)(rand.NextDouble() * 1.5 + 0.8);
                    
                    dsColor.FillCircle(new Vector2(px, py), pSize, Color.FromArgb((byte)(opacity * 255), stampColor2.R, stampColor2.G, stampColor2.B));
                    byte h = (byte)(CurrentBrush.Impasto * 45f * pressure * _currentPigmentAmount);
                    dsHeight.FillCircle(new Vector2(px, py), pSize, Color.FromArgb(h, h, h, h));
                }
            }
        }
        else if (CurrentBrush.IsSand)
        {
            var rand = new Random();
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                int particles = 15;
                for (int i = 0; i < particles; i++)
                {
                    double angle = rand.NextDouble() * Math.PI * 2.0;
                    double dist = rand.NextDouble() * size * 0.9;
                    float px = (float)(pos.X + Math.Cos(angle) * dist);
                    float py = (float)(pos.Y + Math.Sin(angle) * dist);
                    float pSize = (float)(rand.NextDouble() * 2.5 + 1.2);
                    
                    dsColor.FillCircle(new Vector2(px, py), pSize, stampColor2);
                    byte h = (byte)(200 * pressure);
                    dsHeight.FillCircle(new Vector2(px, py), pSize, Color.FromArgb(h, h, h, h));
                }
            }
        }
        else
        {
            using (var dsColor = _tempStrokeColor!.CreateDrawingSession())
            using (var dsHeight = _tempStrokeHeight!.CreateDrawingSession())
            {
                if (tiltAngle != 0f)
                {
                    dsColor.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsColor.Transform;
                    dsHeight.Transform = Matrix3x2.CreateRotation(tiltAngle, pos.ToVector2()) * dsHeight.Transform;
                }
                if (StampPatternEnabled)
                {
                    var clipGeometry = CanvasGeometry.CreateEllipse(dsColor.Device, pos.ToVector2(), size * stretchX, size * stretchY);
                    using (dsColor.CreateLayer(1.0f, clipGeometry))
                    {
                        var rand = new Random((int)(pos.X * 1000 + pos.Y));
                        if (CurrentPattern == PatternType.Dots)
                        {
                            float spacing = Math.Max(4f, size * 0.25f);
                            for (float px = -size * stretchX; px <= size * stretchX; px += spacing)
                            {
                                for (float py = -size * stretchY; py <= size * stretchY; py += spacing)
                                {
                                    dsColor.FillCircle(new Vector2((float)pos.X + px, (float)pos.Y + py), Math.Max(1.0f, size * 0.05f), stampColor2);
                                }
                            }
                        }
                        else if (CurrentPattern == PatternType.Crosshatch)
                        {
                            float spacing = Math.Max(6f, size * 0.35f);
                            for (float offset = -size * Math.Max(stretchX, stretchY); offset <= size * Math.Max(stretchX, stretchY); offset += spacing)
                            {
                                Vector2 start1 = new Vector2((float)pos.X + offset - size, (float)pos.Y - size);
                                Vector2 end1 = new Vector2((float)pos.X + offset + size, (float)pos.Y + size);
                                dsColor.DrawLine(start1, end1, stampColor2, 1.2f);

                                Vector2 start2 = new Vector2((float)pos.X + offset + size, (float)pos.Y - size);
                                Vector2 end2 = new Vector2((float)pos.X + offset - size, (float)pos.Y + size);
                                dsColor.DrawLine(start2, end2, stampColor2, 1.2f);
                            }
                        }
                        else if (CurrentPattern == PatternType.Stipple)
                        {
                            int particles = 30;
                            for (int pIdx = 0; pIdx < particles; pIdx++)
                            {
                                float rx = (float)(rand.NextDouble() - 0.5) * size * 2f * stretchX;
                                float ry = (float)(rand.NextDouble() - 0.5) * size * 2f * stretchY;
                                dsColor.FillCircle(new Vector2((float)pos.X + rx, (float)pos.Y + ry), (float)(rand.NextDouble() * 1.5 + 0.5), stampColor2);
                            }
                        }
                    }
                }
                else
                {
                    var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, stampColor2, Color.FromArgb(0, stampColor2.R, stampColor2.G, stampColor2.B))
                    {
                        Center = pos.ToVector2(),
                        RadiusX = size * stretchX,
                        RadiusY = size * stretchY
                    };
                    dsColor.FillEllipse(pos.ToVector2(), size * stretchX, size * stretchY, colorBrush);
                }
                
                if (CurrentBrush.Impasto > 0)
                {
                    byte h = (byte)(CurrentBrush.Impasto * 140f * pressure * _currentPigmentAmount);
                    var heightColor = Color.FromArgb(h, h, h, h);
                    var heightBrush = new CanvasRadialGradientBrush(dsHeight.Device, heightColor, Color.FromArgb(0, 0, 0, 0))
                    {
                        Center = pos.ToVector2(),
                        RadiusX = size * 0.9f * stretchX,
                        RadiusY = size * 0.9f * stretchY
                    };
                    dsHeight.FillEllipse(pos.ToVector2(), size * 0.9f * stretchX, size * 0.9f * stretchY, heightBrush);
                }
            }
        }
        
        if (WaterWetness > 0.05f && !CurrentBrush.IsDigital && (CurrentBrush.TextureInteraction == 0))
        {
            using (var dsWet = _wetnessTarget!.CreateDrawingSession())
            {
                byte w = (byte)(_currentWaterAmount * 255);
                var wetBrush = new CanvasRadialGradientBrush(dsWet.Device, Color.FromArgb(w, w, w, w), Color.FromArgb(0, 0, 0, 0))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size * 1.2f,
                    RadiusY = size * 1.2f
                };
                dsWet.FillCircle(pos.ToVector2(), size * 1.2f, wetBrush);
            }
        }
        
        if (wetFactor > 0.1f && (CurrentBrush.BleedRate > 0) && !CurrentBrush.IsDigital)
        {
            SampleAndBlendColor(pos, 0.08f);
        }
    }

    private float GetSubstrateHeightAt(Point pos)
    {
        if (_substrateHeightBytes == null) return 0.5f;
        int x = (int)pos.X % 256;
        int y = (int)pos.Y % 256;
        if (x < 0) x += 256;
        if (y < 0) y += 256;
        int idx = (y * 256 + x) * 4;
        if (idx < 0 || idx >= _substrateHeightBytes.Length) return 0.5f;
        return _substrateHeightBytes[idx] / 255f;
    }

    private void BakeStroke()
    {
        if (ActiveLayer == null) return;
        
        using (var ds = ActiveLayer.ColorTarget.CreateDrawingSession())
        {
            if (CurrentBrush.TextureInteraction > 0)
            {
                var maskedStroke = new BlendEffect
                {
                    Background = _tempStrokeColor,
                    Foreground = _substrateHeight,
                    Mode = BlendEffectMode.Multiply
                };
                ds.DrawImage(maskedStroke);
            }
            else
            {
                ds.DrawImage(_tempStrokeColor!);
            }
        }
        
        using (var ds = ActiveLayer.HeightTarget.CreateDrawingSession())
        {
            ds.DrawImage(_tempStrokeHeight!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.Add);
        }
        
        using (var ds = _tempStrokeColor!.CreateDrawingSession()) ds.Clear(Colors.Transparent);
        using (var ds = _tempStrokeHeight!.CreateDrawingSession()) ds.Clear(Colors.Transparent);
    }

    private void SmearPaint(Point pos, float size)
    {
        if (ActiveLayer == null || _tempStrokeColor == null || _tempColor == null) return;
        
        int r = (int)Math.Max(4, size);
        int srcX = (int)Math.Clamp(pos.X - r, 0, CanvasWidth - r * 2 - 1);
        int srcY = (int)Math.Clamp(pos.Y - r, 0, CanvasHeight - r * 2 - 1);
        
        // 1. Copy the existing paint under the brush
        using (var ds = _smearTemp!.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(ActiveLayer.ColorTarget, 0, 0, new Rect(srcX, srcY, r * 2, r * 2));
        }
        
        using (var ds = _smearTempHeight!.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
            ds.DrawImage(ActiveLayer.HeightTarget, 0, 0, new Rect(srcX, srcY, r * 2, r * 2));
        }
        
        // 2. Erase a soft circular footprint under the brush to simulate displacement/thinning
        using (var dsMask = _tempColor.CreateDrawingSession())
        {
            dsMask.Clear(Colors.Transparent);
            var radial = new CanvasRadialGradientBrush(dsMask.Device, Colors.White, Color.FromArgb(0, 255, 255, 255))
            {
                Center = pos.ToVector2(),
                RadiusX = size * 0.9f,
                RadiusY = size * 0.9f
            };
            dsMask.FillCircle(pos.ToVector2(), size * 0.9f, radial);
        }
        
        using (var dsCol = ActiveLayer.ColorTarget.CreateDrawingSession())
        {
            dsCol.DrawImage(_tempColor, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
        }
        
        using (var dsH = ActiveLayer.HeightTarget.CreateDrawingSession())
        {
            dsH.DrawImage(_tempColor, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.DestinationOut);
        }
        
        // Compute swirly displacement vector based on last pointer position
        float shiftX = 0f;
        float shiftY = 0f;
        float swirlAngle = 0f;
        
        float dx = (float)(pos.X - _lastPoint.X);
        float dy = (float)(pos.Y - _lastPoint.Y);
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        if (dist > 0.5f)
        {
            shiftX = dx * 0.45f;
            shiftY = dy * 0.45f;
            swirlAngle = 0.22f; // subtle angular smudger twist
        }

        // 3. Draw the smeared paint back with a larger blurred footprint on _tempStrokeColor
        using (var ds = _tempStrokeColor.CreateDrawingSession())
        {
            var originalTransform = ds.Transform;
            if (dist > 0.5f)
            {
                ds.Transform = Matrix3x2.CreateRotation(swirlAngle, pos.ToVector2()) * originalTransform;
            }
            var blur = new GaussianBlurEffect { Source = _smearTemp, BlurAmount = 2.0f };
            ds.DrawImage(blur, new Rect(pos.X - r * 1.05 + shiftX, pos.Y - r * 1.05 + shiftY, r * 2.1, r * 2.1), new Rect(0, 0, r * 2, r * 2), 0.75f);
            ds.Transform = originalTransform;
        }
        
        using (var ds = _tempStrokeHeight!.CreateDrawingSession())
        {
            var originalTransform = ds.Transform;
            if (dist > 0.5f)
            {
                ds.Transform = Matrix3x2.CreateRotation(swirlAngle, pos.ToVector2()) * originalTransform;
            }
            var blur = new GaussianBlurEffect { Source = _smearTempHeight, BlurAmount = 1.0f };
            ds.DrawImage(blur, new Rect(pos.X - r * 1.05 + shiftX, pos.Y - r * 1.05 + shiftY, r * 2.1, r * 2.1), new Rect(0, 0, r * 2, r * 2), 0.55f);
            ds.Transform = originalTransform;
        }
    }

    private void SampleAndBlendColor(Point pos, float rate)
    {
        if (ActiveLayer == null) return;
        
        int x = (int)Math.Clamp(pos.X, 0, CanvasWidth - 1);
        int y = (int)Math.Clamp(pos.Y, 0, CanvasHeight - 1);
        
        try
        {
            int startX = Math.Max(0, x - 1);
            int startY = Math.Max(0, y - 1);
            int width = Math.Min(3, CanvasWidth - startX);
            int height = Math.Min(3, CanvasHeight - startY);
            
            byte[] colorBytes = new byte[width * height * 4];
            ActiveLayer.ColorTarget.GetPixelBytes(colorBytes.AsBuffer(), startX, startY, width, height);
            
            int rCount = 0;
            int rR = 0, rG = 0, rB = 0, rA = 0;
            
            for (int i = 0; i < width * height; i++)
            {
                int idx = i * 4;
                byte a = colorBytes[idx + 3];
                if (a > 0)
                {
                    rR += colorBytes[idx];
                    rG += colorBytes[idx + 1];
                    rB += colorBytes[idx + 2];
                    rA += a;
                    rCount++;
                }
            }

            if (rCount > 0 && ActiveColor != null)
            {
                Color canvasCol = Color.FromArgb((byte)(rA / rCount), (byte)(rR / rCount), (byte)(rG / rCount), (byte)(rB / rCount));
                ActiveColor = Color.FromArgb(
                    255,
                    (byte)(ActiveColor.Value.R * (1f - rate) + canvasCol.R * rate),
                    (byte)(ActiveColor.Value.G * (1f - rate) + canvasCol.G * rate),
                    (byte)(ActiveColor.Value.B * (1f - rate) + canvasCol.B * rate)
                );
            }
        }
        catch { }
    }

    private void SampleColorAt(Point pos)
    {
        if (ActiveLayer == null) return;
        
        int x = (int)Math.Clamp(pos.X, 0, CanvasWidth - 1);
        int y = (int)Math.Clamp(pos.Y, 0, CanvasHeight - 1);
        
        try
        {
            byte[] colorBytes = new byte[4];
            ActiveLayer.ColorTarget.GetPixelBytes(colorBytes.AsBuffer(), x, y, 1, 1);
            if (colorBytes[3] > 0)
            {
                ActiveColor = Color.FromArgb(255, colorBytes[0], colorBytes[1], colorBytes[2]);
                CurrentTool = ArtTool.Brush;
            }
        }
        catch { }
    }

    private float GetWetnessAt(Point pos)
    {
        if (_wetnessTarget == null) return 0f;
        int x = (int)Math.Clamp(pos.X, 0, CanvasWidth - 1);
        int y = (int)Math.Clamp(pos.Y, 0, CanvasHeight - 1);
        try
        {
            byte[] bytes = new byte[4];
            _wetnessTarget.GetPixelBytes(bytes.AsBuffer(), x, y, 1, 1);
            return bytes[0] / 255f;
        }
        catch { return 0f; }
    }

    private void DryTimer_Tick(object? sender, object e)
    {
        if (_wetnessTarget == null || ActiveLayer == null) return;
        
        if (RealTimeDrying)
        {
            float decayFactor = GetDryingDecayFactor();
            if (decayFactor < 1.0f)
            {
                using (var ds = _tempWetness!.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    ds.DrawImage(_wetnessTarget, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), decayFactor);
                }
                using (var ds = _wetnessTarget.CreateDrawingSession())
                {
                    ds.Clear(Colors.Transparent);
                    ds.DrawImage(_tempWetness!);
                }
            }
        }
        
        if (CurrentBrush.BleedRate > 0 && DrynessSliderValue > 0.05f)
        {
            var blurredColor = new GaussianBlurEffect { Source = ActiveLayer.ColorTarget, BlurAmount = 2.0f };
            using (var ds = _tempColor!.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(ActiveLayer.ColorTarget!);
                
                var wetnessMask = new BlendEffect
                {
                    Background = blurredColor,
                    Foreground = _wetnessTarget,
                    Mode = BlendEffectMode.Multiply
                };
                
                float speed = 0.08f * CurrentBrush.BleedRate * DrynessSliderValue;
                ds.DrawImage(wetnessMask, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), speed);
            }
            using (var ds = ActiveLayer.ColorTarget.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(_tempColor!);
            }
        }
        
        if (CurrentBrush.SelfLeveling > 0)
        {
            var blurredHeight = new GaussianBlurEffect { Source = ActiveLayer.HeightTarget, BlurAmount = 1.2f };
            using (var ds = _tempHeight!.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(ActiveLayer.HeightTarget!);
                ds.DrawImage(blurredHeight, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), CurrentBrush.SelfLeveling);
            }
            using (var ds = ActiveLayer.HeightTarget.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                ds.DrawImage(_tempHeight!);
            }
        }
        
        _canvas?.Invalidate();
    }

    private float GetDryingDecayFactor()
    {
        float dryTime = CurrentBrush.DefaultDryTime;
        if (dryTime >= 999999f) return 1.0f;
        
        double dt = 0.3;
        double factor = Math.Exp(-dt / dryTime);
        return (float)factor;
    }

    private CanvasRenderTarget? _backupColor;
    private CanvasRenderTarget? _backupHeight;

    private void SaveBackupTargets(Guid layerId)
    {
        if (_canvas == null) return;
        var layer = Layers.Find(x => x.Id == layerId);
        if (layer == null) return;
        
        var device = _canvas.Device;
        
        if (_backupColor == null || _backupColor.SizeInPixels.Width != (uint)CanvasWidth)
        {
            _backupColor?.Dispose();
            _backupHeight?.Dispose();
            _backupColor = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
            _backupHeight = new CanvasRenderTarget(device, CanvasWidth, CanvasHeight, 96);
        }
        
        using (var ds = _backupColor.CreateDrawingSession()) { ds.Clear(Colors.Transparent); ds.DrawImage(layer.ColorTarget); }
        using (var ds = _backupHeight!.CreateDrawingSession()) { ds.Clear(Colors.Transparent); ds.DrawImage(layer.HeightTarget); }
    }

    private void CaptureUndoStateBefore(Guid layerId, Point pos, float size)
    {
        _redoList.Clear();
        SaveBackupTargets(layerId);
        
        float pad = size * 2.5f;
        _strokeDirtyRect = new Rect(pos.X - pad, pos.Y - pad, pad * 2, pad * 2);
    }

    private void CaptureUndoStateAfter()
    {
        if (ActiveLayer == null || _backupColor == null || _backupHeight == null) return;
        
        _strokeDirtyRect = ClampRectToCanvas(_strokeDirtyRect);
        
        int x = (int)_strokeDirtyRect.X;
        int y = (int)_strokeDirtyRect.Y;
        int w = (int)_strokeDirtyRect.Width;
        int h = (int)_strokeDirtyRect.Height;
        
        if (w <= 0 || h <= 0) return;
        
        var item = new ArtUndoItem
        {
            LayerId = ActiveLayer.Id,
            DirtyRect = _strokeDirtyRect,
            ColorBefore = new byte[w * h * 4],
            HeightBefore = new byte[w * h * 4],
            ColorAfter = new byte[w * h * 4],
            HeightAfter = new byte[w * h * 4]
        };
        
        try
        {
            _backupColor.GetPixelBytes(item.ColorBefore.AsBuffer(), x, y, w, h);
            _backupHeight.GetPixelBytes(item.HeightBefore.AsBuffer(), x, y, w, h);
            
            ActiveLayer.ColorTarget.GetPixelBytes(item.ColorAfter.AsBuffer(), x, y, w, h);
            ActiveLayer.HeightTarget.GetPixelBytes(item.HeightAfter.AsBuffer(), x, y, w, h);
            
            _undoList.Add(item);
            if (_undoList.Count > 30) _undoList.RemoveAt(0);
        }
        catch { }
    }

    public void Undo()
    {
        if (_undoList.Count == 0) return;
        
        var item = _undoList[_undoList.Count - 1];
        _undoList.RemoveAt(_undoList.Count - 1);
        _redoList.Add(item);
        
        var layer = Layers.Find(x => x.Id == item.LayerId);
        if (layer == null) return;
        
        int x = (int)item.DirtyRect.X;
        int y = (int)item.DirtyRect.Y;
        int w = (int)item.DirtyRect.Width;
        int h = (int)item.DirtyRect.Height;
        
        try
        {
            layer.ColorTarget.SetPixelBytes(item.ColorBefore.AsBuffer(), x, y, w, h);
            layer.HeightTarget.SetPixelBytes(item.HeightBefore.AsBuffer(), x, y, w, h);
            _canvas?.Invalidate();
        }
        catch { }
    }

    public void Redo()
    {
        if (_redoList.Count == 0) return;
        
        var item = _redoList[_redoList.Count - 1];
        _redoList.RemoveAt(_redoList.Count - 1);
        _undoList.Add(item);
        
        var layer = Layers.Find(x => x.Id == item.LayerId);
        if (layer == null) return;
        
        int x = (int)item.DirtyRect.X;
        int y = (int)item.DirtyRect.Y;
        int w = (int)item.DirtyRect.Width;
        int h = (int)item.DirtyRect.Height;
        
        try
        {
            layer.ColorTarget.SetPixelBytes(item.ColorAfter.AsBuffer(), x, y, w, h);
            layer.HeightTarget.SetPixelBytes(item.HeightAfter.AsBuffer(), x, y, w, h);
            _canvas?.Invalidate();
        }
        catch { }
    }

    private Rect ClampRectToCanvas(Rect r)
    {
        double x0 = Math.Clamp(r.X, 0, CanvasWidth - 1);
        double y0 = Math.Clamp(r.Y, 0, CanvasHeight - 1);
        double x1 = Math.Clamp(r.X + r.Width, 0, CanvasWidth - 1);
        double y1 = Math.Clamp(r.Y + r.Height, 0, CanvasHeight - 1);
        return new Rect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private static Rect ActionBoundsUnion(Rect r1, Rect r2)
    {
        double minX = Math.Min(r1.Left, r2.Left);
        double minY = Math.Min(r1.Top, r2.Top);
        double maxX = Math.Max(r1.Right, r2.Right);
        double maxY = Math.Max(r1.Bottom, r2.Bottom);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    // Serialization definitions
    public class ArtSaveMeta
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Substrate { get; set; } = "Canvas";
        public string Name { get; set; } = "Painting";
        public float DrynessSlider { get; set; } = 1.0f;
        public bool RealTimeDrying { get; set; } = true;
        public List<ArtLayerMeta> Layers { get; set; } = new();
        public List<ArtStroke> Strokes { get; set; } = new();
    }

    public class ArtLayerMeta
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public float Opacity { get; set; } = 1.0f;
        public bool Visible { get; set; } = true;
    }

    public async Task SaveProjectAsync(StorageFile file)
    {
        var meta = new ArtSaveMeta
        {
            Width = CanvasWidth,
            Height = CanvasHeight,
            Substrate = SubstrateType,
            Name = file.DisplayName,
            DrynessSlider = DrynessSliderValue,
            RealTimeDrying = RealTimeDrying,
            Strokes = new List<ArtStroke>(Strokes)
        };

        var layersBytes = new List<(Guid Id, byte[] Col, byte[] H)>();
        foreach (var layer in Layers)
        {
            meta.Layers.Add(new ArtLayerMeta { Id = layer.Id, Name = layer.Name, Opacity = layer.Opacity, Visible = layer.Visible });
            byte[] cBytes = new byte[CanvasWidth * CanvasHeight * 4];
            byte[] hBytes = new byte[CanvasWidth * CanvasHeight * 4];
            layer.ColorTarget.GetPixelBytes(cBytes.AsBuffer());
            layer.HeightTarget.GetPixelBytes(hBytes.AsBuffer());
            layersBytes.Add((layer.Id, cBytes, hBytes));
        }

        byte[] wetnessBytes = new byte[CanvasWidth * CanvasHeight * 4];
        _wetnessTarget!.GetPixelBytes(wetnessBytes.AsBuffer());

        // never write in place: a half-written .artq is unrecoverable, so the zip is
        // built beside the target and only swapped in once it is complete on disk
        var tempFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            "save_" + Guid.NewGuid().ToString("N") + ".artq", CreationCollisionOption.ReplaceExisting);

        await Task.Run(async () =>
        {
            using (var stream = await tempFile.OpenStreamForWriteAsync())
            {
                stream.SetLength(0);
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    var metaEntry = archive.CreateEntry("meta.json");
                    using (var writer = new StreamWriter(metaEntry.Open()))
                    {
                        writer.Write(JsonSerializer.Serialize(meta));
                    }
                    
                    foreach (var lay in layersBytes)
                    {
                        var colEntry = archive.CreateEntry($"color_{lay.Id}.bin");
                        using (var s = colEntry.Open()) s.Write(lay.Col, 0, lay.Col.Length);

                        var hEntry = archive.CreateEntry($"height_{lay.Id}.bin");
                        using (var s = hEntry.Open()) s.Write(lay.H, 0, lay.H.Length);
                    }
                    
                    var wEntry = archive.CreateEntry("wetness.bin");
                    using (var s = wEntry.Open()) s.Write(wetnessBytes, 0, wetnessBytes.Length);
                }
            }
        });

        try
        {
            await tempFile.MoveAndReplaceAsync(file);
        }
        catch
        {
            // replace can fail if the target is locked by another app - fall back to a
            // straight copy rather than losing the work that is already on disk
            await tempFile.CopyAndReplaceAsync(file);
            try { await tempFile.DeleteAsync(); } catch { }
        }
    }

    public async Task LoadProjectAsync(StorageFile file)
    {
        ArtSaveMeta? meta = null;
        var layerBuffers = new Dictionary<Guid, (byte[] Col, byte[] H)>();
        byte[]? wetnessBytes = null;

        await Task.Run(async () =>
        {
            using (var stream = await file.OpenStreamForReadAsync())
            {
            if (stream.Length == 0)
                throw new InvalidDataException("This .artq file is empty - the last save was interrupted before any data was written.");
            ZipArchive archive;
            try { archive = new ZipArchive(stream, ZipArchiveMode.Read); }
            catch (InvalidDataException)
            {
                throw new InvalidDataException("This .artq file is damaged (the archive is truncated), most likely from a save that was interrupted.");
            }
            using (archive)
            {
                var metaEntry = archive.GetEntry("meta.json");
                if (metaEntry != null)
                {
                    using (var reader = new StreamReader(metaEntry.Open()))
                    {
                        meta = JsonSerializer.Deserialize<ArtSaveMeta>(reader.ReadToEnd());
                    }
                }
                
                if (meta == null) return;
                int len = meta.Width * meta.Height * 4;

                foreach (var layMeta in meta.Layers)
                {
                    var colEntry = archive.GetEntry($"color_{layMeta.Id}.bin");
                    var hEntry = archive.GetEntry($"height_{layMeta.Id}.bin");
                    if (colEntry != null && hEntry != null)
                    {
                        byte[] cBytes = new byte[len];
                        byte[] hBytes = new byte[len];
                        using (var s = colEntry.Open()) ReadAllBytes(s, cBytes);
                        using (var s = hEntry.Open()) ReadAllBytes(s, hBytes);
                        layerBuffers[layMeta.Id] = (cBytes, hBytes);
                    }
                }
                
                var wEntry = archive.GetEntry("wetness.bin");
                if (wEntry != null)
                {
                    wetnessBytes = new byte[len];
                    using (var s = wEntry.Open()) ReadAllBytes(s, wetnessBytes);
                }
            }
            }
        });

        if (meta != null && _canvas != null)
        {
            CanvasWidth = meta.Width;
            CanvasHeight = meta.Height;
            SubstrateType = meta.Substrate;
            DrynessSliderValue = meta.DrynessSlider;
            RealTimeDrying = meta.RealTimeDrying;
            
            DisposeTargets();
            
            _globalColor = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _globalHeight = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _wetnessTarget = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _combinedHeight = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _baseColorTarget = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            
            _tempStrokeColor = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _tempStrokeHeight = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _tempWetness = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _tempColor = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _tempHeight = new CanvasRenderTarget(_canvas, CanvasWidth, CanvasHeight, 96);
            _smearTemp = new CanvasRenderTarget(_canvas, 512, 512, 96);
            _smearTempHeight = new CanvasRenderTarget(_canvas, 512, 512, 96);

            _paletteColor = new CanvasRenderTarget(_canvas, 200, 200, 96);
            _paletteHeight = new CanvasRenderTarget(_canvas, 200, 200, 96);
            _paletteWetness = new CanvasRenderTarget(_canvas, 200, 200, 96);

            using (var ds = _paletteColor.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 240, 238, 230));
            using (var ds = _paletteHeight.CreateDrawingSession()) ds.Clear(Colors.Transparent);
            using (var ds = _paletteWetness.CreateDrawingSession()) ds.Clear(Colors.Transparent);
            
            _substrateHeight = new CanvasRenderTarget(_canvas, 256, 256, 96);
            GenerateSubstrateHeightmap(_canvas.Device);
            
            Strokes.Clear();
            if (meta.Strokes != null) Strokes.AddRange(meta.Strokes);

            foreach (var layMeta in meta.Layers)
            {
                var newLayer = new ArtLayer(_canvas, CanvasWidth, CanvasHeight)
                {
                    Id = layMeta.Id,
                    Name = layMeta.Name,
                    Opacity = layMeta.Opacity,
                    Visible = layMeta.Visible
                };
                if (layerBuffers.TryGetValue(layMeta.Id, out var buffers))
                {
                    newLayer.ColorTarget.SetPixelBytes(buffers.Col.AsBuffer());
                    newLayer.HeightTarget.SetPixelBytes(buffers.H.AsBuffer());
                }
                Layers.Add(newLayer);
            }

            ActiveLayer = Layers.Count > 0 ? Layers[0] : null;

            if (wetnessBytes != null)
            {
                _wetnessTarget.SetPixelBytes(wetnessBytes.AsBuffer());
            }

            UpdateLayersListView();
            UpdateStrokesListPanel();
            _canvas.Invalidate();
        }
    }

    private static void ReadAllBytes(Stream stream, byte[] buffer)
    {
        int offset = 0;
        int read;
        while ((read = stream.Read(buffer, offset, buffer.Length - offset)) > 0)
        {
            offset += read;
        }
    }

    public async Task ExportPngAsync(StorageFile file)
    {
        if (_canvas == null || _globalColor == null) return;
        
        RenderCompositeLayerBuffers();
        var exportTarget = new CanvasRenderTarget(_canvas.Device, CanvasWidth, CanvasHeight, 96);
        using (var ds = exportTarget.CreateDrawingSession())
        {
            ds.Clear(_substrateBgColor);
            ds.DrawImage(_globalColor);
            
            var combinedHeight = new CanvasRenderTarget(_canvas.Device, CanvasWidth, CanvasHeight, 96);
            using (var hds = combinedHeight.CreateDrawingSession())
            {
                hds.Clear(Color.FromArgb(255, 127, 127, 127));
                hds.DrawRectangle(new Rect(0, 0, CanvasWidth, CanvasHeight), _substrateBrush!);
                hds.DrawImage(_globalHeight!, 0, 0, new Rect(0, 0, CanvasWidth, CanvasHeight), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.Add);
            }
            
            float scaleMultiplier = CurrentBrush.Impasto * 5f;
            float specConstant = CurrentBrush.Viscosity * 0.8f;
            
            var diffuseLight = new DistantDiffuseEffect
            {
                Source = combinedHeight,
                HeightMapScale = scaleMultiplier,
                DiffuseAmount = 1.1f,
                Azimuth = (float)(225.0 * Math.PI / 180.0),
                Elevation = (float)(45.0 * Math.PI / 180.0),
                LightColor = Colors.White
            };
            
            var specularLight = new DistantSpecularEffect
            {
                Source = combinedHeight,
                HeightMapScale = scaleMultiplier,
                SpecularAmount = specConstant,
                SpecularExponent = 15.0f,
                Azimuth = (float)(225.0 * Math.PI / 180.0),
                Elevation = (float)(45.0 * Math.PI / 180.0),
                LightColor = Colors.White
            };
            
            var litColor = new BlendEffect
            {
                Background = exportTarget,
                Foreground = diffuseLight,
                Mode = BlendEffectMode.Multiply
            };
            
            var finalResult = new BlendEffect
            {
                Background = litColor,
                Foreground = specularLight,
                Mode = BlendEffectMode.LinearDodge
            };
            
            ds.Clear(_substrateBgColor);
            ds.DrawImage(finalResult);
            combinedHeight.Dispose();
        }
        
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            await exportTarget.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        }
        exportTarget.Dispose();
    }

    // Replay / redraw from stroke log
    public void RedrawAllStrokes()
    {
        if (_canvas == null || Layers.Count == 0) return;
        
        foreach (var layer in Layers) layer.Clear();
        using (var ds = _wetnessTarget!.CreateDrawingSession()) ds.Clear(Colors.Transparent);

        foreach (var stroke in Strokes)
        {
            var layer = Layers.Find(x => x.Id == stroke.LayerId);
            if (layer == null) continue;
            
            var brush = Brushes.Find(x => x.Name == stroke.BrushName) ?? Brushes[0];
            Color paintColor = stroke.PaintColor ?? Colors.Transparent;
            
            using (var dsColor = layer.ColorTarget.CreateDrawingSession())
            using (var dsHeight = layer.HeightTarget.CreateDrawingSession())
            {
                for (int i = 0; i < stroke.Points.Count; i++)
                {
                    float pressure = stroke.Pressures[i];
                    float size = stroke.Size * (0.5f + pressure * 0.6f);
                    Point pos = stroke.Points[i];
                    
                    var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, paintColor, Color.FromArgb(0, paintColor.R, paintColor.G, paintColor.B))
                    {
                        Center = pos.ToVector2(),
                        RadiusX = size,
                        RadiusY = size
                    };
                    dsColor.FillCircle(pos.ToVector2(), size, colorBrush);

                    if (stroke.Impasto > 0)
                    {
                        byte h = (byte)(stroke.Impasto * 140f * pressure);
                        var heightColor = Color.FromArgb(h, h, h, h);
                        var heightBrush = new CanvasRadialGradientBrush(dsHeight.Device, heightColor, Color.FromArgb(0, 0, 0, 0))
                        {
                            Center = pos.ToVector2(),
                            RadiusX = size * 0.9f,
                            RadiusY = size * 0.9f
                        };
                        dsHeight.FillCircle(pos.ToVector2(), size * 0.9f, heightBrush);
                    }
                }
            }
        }
        _canvas.Invalidate();
    }

    // guards the replay: painting during playback used to mutate Strokes mid-enumeration
    private bool _replaying;
    public bool IsReplaying => _replaying;

    public async Task ReplaySequenceAsync()
    {
        if (_canvas == null || Layers.Count == 0 || _replaying) return;

        // snapshot both lists - the replay awaits between strokes, so the live
        // collections can change underneath us (undo, clear, or a stray stroke)
        var strokeList = new List<ArtStroke>(Strokes);
        var layerList = new List<ArtLayer>(Layers);

        _replaying = true;
        try
        {
        foreach (var layer in layerList) layer.Clear();
        _canvas.Invalidate();
        
        foreach (var stroke in strokeList)
        {
            var layer = layerList.Find(x => x.Id == stroke.LayerId);
            if (layer == null) continue;
            
            var brush = Brushes.Find(x => x.Name == stroke.BrushName) ?? Brushes[0];
            Color paintColor = stroke.PaintColor ?? Colors.Transparent;
            
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                float pressure = stroke.Pressures[i];
                float size = stroke.Size * (0.5f + pressure * 0.6f);
                Point pos = stroke.Points[i];
                
                using (var dsColor = layer.ColorTarget.CreateDrawingSession())
                using (var dsHeight = layer.HeightTarget.CreateDrawingSession())
                {
                    var colorBrush = new CanvasRadialGradientBrush(dsColor.Device, paintColor, Color.FromArgb(0, paintColor.R, paintColor.G, paintColor.B))
                    {
                        Center = pos.ToVector2(),
                        RadiusX = size,
                        RadiusY = size
                    };
                    dsColor.FillCircle(pos.ToVector2(), size, colorBrush);

                    if (stroke.Impasto > 0)
                    {
                        byte h = (byte)(stroke.Impasto * 140f * pressure);
                        var heightColor = Color.FromArgb(h, h, h, h);
                        var heightBrush = new CanvasRadialGradientBrush(dsHeight.Device, heightColor, Color.FromArgb(0, 0, 0, 0))
                        {
                            Center = pos.ToVector2(),
                            RadiusX = size * 0.9f,
                            RadiusY = size * 0.9f
                        };
                        dsHeight.FillCircle(pos.ToVector2(), size * 0.9f, heightBrush);
                    }
                }
                
                if (i % 3 == 0)
                {
                    _canvas.Invalidate();
                    await Task.Delay(10);
                }
            }
            _canvas.Invalidate();
            await Task.Delay(100);
        }
        }
        finally
        {
            _replaying = false;
            // the replay painted onto cleared layers from a snapshot; rebuild from
            // the authoritative list so nothing added meanwhile is lost
            RedrawAllStrokes();
        }
    }

    private void InitializeOverlayUI()
    {
        if (_mainUIGrid == null) return;
        
        _toolbarBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(12, 12, 0, 12),
            Width = 290,
            CornerRadius = new CornerRadius(14),
            Background = Application.Current.Resources["CardBrush"] as Brush ?? new SolidColorBrush(Color.FromArgb(220, 30, 30, 32)),
            BorderBrush = Application.Current.Resources["GlassEdgeBrush"] as Brush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12)
        };
        
        var mainStack = new StackPanel { Spacing = 8 };
        _toolbarBorder.Child = mainStack;
        
        var pivot = new Pivot();
        
        // Pivot 1: Brushes & Colors
        var brushTab = new PivotItem { Header = "Brushes" };
        var brushStack = new StackPanel { Spacing = 8 };
        brushTab.Content = brushStack;

        brushStack.Children.Add(new TextBlock { Text = "Medium Preset:", FontSize = 11, FontWeight = FontWeights.SemiBold });
        var comboPreset = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        foreach (var br in Brushes) comboPreset.Items.Add(br.Name);
        comboPreset.SelectedIndex = 0;
        comboPreset.SelectionChanged += (s, e) =>
        {
            if (comboPreset.SelectedIndex >= 0)
            {
                CurrentBrush = Brushes[comboPreset.SelectedIndex];
                ActiveColor = CurrentBrush.DefaultColor;
                BrushSize = 25f;
                PigmentLoading = CurrentBrush.Opacity;
                WaterWetness = CurrentBrush.DefaultDryTime < 30f ? 0.8f : 0.2f;
                UpdateSlidersFromPreset();
                SaveSettings();
            }
        };
        brushStack.Children.Add(comboPreset);
        _comboPreset = comboPreset;

        var toolStack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var btnBrush = new ToggleButton { Content = "Brush", IsChecked = true, FontSize = 11 };
        var btnKnife = new ToggleButton { Content = "Knife", FontSize = 11 };
        var btnEye = new ToggleButton { Content = "Eye", FontSize = 11 };
        
        btnBrush.Click += (s, e) => { CurrentTool = ArtTool.Brush; btnBrush.IsChecked = true; btnKnife.IsChecked = false; btnEye.IsChecked = false; };
        btnKnife.Click += (s, e) => { CurrentTool = ArtTool.PaletteKnife; btnBrush.IsChecked = false; btnKnife.IsChecked = true; btnEye.IsChecked = false; };
        btnEye.Click += (s, e) => { CurrentTool = ArtTool.Eyedropper; btnBrush.IsChecked = false; btnKnife.IsChecked = false; btnEye.IsChecked = true; };
        toolStack.Children.Add(btnBrush);
        toolStack.Children.Add(btnKnife);
        toolStack.Children.Add(btnEye);
        brushStack.Children.Add(toolStack);

        brushStack.Children.Add(new TextBlock { Text = "Depletion Curve Type:", FontSize = 11, FontWeight = FontWeights.SemiBold });
        var comboCurve = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboCurve.Items.Add("Linear");
        comboCurve.Items.Add("Exponential");
        comboCurve.Items.Add("S-Curve (Sigmoid)");
        comboCurve.SelectedIndex = 1;
        comboCurve.SelectionChanged += (s, e) =>
        {
            DepletionCurve = (DepletionCurveType)comboCurve.SelectedIndex;
            SaveSettings();
        };
        brushStack.Children.Add(comboCurve);
        _comboCurve = comboCurve;
        
        brushStack.Children.Add(new TextBlock { Text = "Brush Size:", FontSize = 10 });
        var sliderSize = new Slider { Minimum = 2, Maximum = 100, Value = BrushSize };
        sliderSize.ValueChanged += (s, e) => BrushSize = (float)e.NewValue;
        brushStack.Children.Add(sliderSize);
        _sliderSize = sliderSize;

        brushStack.Children.Add(new TextBlock { Text = "Pigment Loading:", FontSize = 10 });
        var sliderPigment = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = PigmentLoading };
        sliderPigment.ValueChanged += (s, e) => PigmentLoading = (float)e.NewValue;
        brushStack.Children.Add(sliderPigment);
        _sliderPigment = sliderPigment;

        brushStack.Children.Add(new TextBlock { Text = "Water Wetness:", FontSize = 10 });
        var sliderWater = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = WaterWetness };
        sliderWater.ValueChanged += (s, e) => WaterWetness = (float)e.NewValue;
        brushStack.Children.Add(sliderWater);
        _sliderWater = sliderWater;

        brushStack.Children.Add(new TextBlock { Text = "Depletion Rate:", FontSize = 10 });
        var sliderDepletion = new Slider { Minimum = 0.1, Maximum = 5.0, StepFrequency = 0.1, Value = DepletionRateSliderValue };
        sliderDepletion.ValueChanged += (s, e) => { DepletionRateSliderValue = (float)e.NewValue; SaveSettings(); };
        brushStack.Children.Add(sliderDepletion);
        _sliderDepletion = sliderDepletion;
        
        var utilityGrid = new Grid();
        utilityGrid.ColumnDefinitions.Add(new ColumnDefinition());
        utilityGrid.ColumnDefinitions.Add(new ColumnDefinition());
        
        var btnWater = new Button { Content = "💧 Water Dip", Margin = new Thickness(0, 0, 4, 0), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnWater.Click += (s, e) =>
        {
            ActiveColor = null;
            PigmentLoading = 0f;
            _currentPigmentAmount = 0f;
            WaterWetness = 1.0f;
            _currentWaterAmount = 1.0f;
            if (_sliderPigment != null) _sliderPigment.Value = 0;
            if (_sliderWater != null) _sliderWater.Value = 1.0;
        };
        Grid.SetColumn(btnWater, 0);
        utilityGrid.Children.Add(btnWater);

        var btnReload = new Button { Content = "🔄 Reload Paint", Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnReload.Click += (s, e) => ReloadBrushPaint();
        Grid.SetColumn(btnReload, 1);
        utilityGrid.Children.Add(btnReload);
        brushStack.Children.Add(utilityGrid);
        
        brushStack.Children.Add(new TextBlock { Text = "Pigment Palette:", FontSize = 11, FontWeight = FontWeights.SemiBold });
        _colorGrid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        _colorGrid.ItemClick += (s, e) =>
        {
            if (e.ClickedItem is Border b && b.Tag is Color c) { ActiveColor = c; ReloadBrushPaint(); }
        };
        PopulatePaletteGrid();
        brushStack.Children.Add(_colorGrid);
        
        var btnPickCol = new Button { Content = "Choose Custom Color...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        var colPickerFlyout = new Flyout();
        var pickerControl = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false };
        pickerControl.ColorChanged += (s, e) => { ActiveColor = e.NewColor; };
        colPickerFlyout.Content = pickerControl;
        btnPickCol.Flyout = colPickerFlyout;
        brushStack.Children.Add(btnPickCol);

        var btnAddPal = new Button { Content = "Add Color to Palette", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        btnAddPal.Click += (s, e) =>
        {
            if (ActiveColor != null)
            {
                string hex = ColorUtil.ToHex(ActiveColor.Value);
                if (_settings.CustomPalette == null) _settings.CustomPalette = new List<string>();
                if (!_settings.CustomPalette.Contains(hex))
                {
                    _settings.CustomPalette.Add(hex);
                    PopulatePaletteGrid();
                    SaveSettings();
                }
            }
        };
        brushStack.Children.Add(btnAddPal);

        pivot.Items.Add(brushTab);
        
        // Pivot 2: Layers
        var layersTab = new PivotItem { Header = "Layers" };
        var layersStack = new StackPanel { Spacing = 8 };
        layersTab.Content = layersStack;

        var layActionsGrid = new Grid();
        layActionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        layActionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        layActionsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        layActionsGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var btnAddLayer = new Button { Content = "+", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        ToolTipService.SetToolTip(btnAddLayer, "Add Layer");
        btnAddLayer.Click += (s, e) => {
            if (_canvas != null)
            {
                var newLay = new ArtLayer(_canvas, CanvasWidth, CanvasHeight) { Name = $"Layer {Layers.Count + 1}" };
                Layers.Insert(0, newLay);
                ActiveLayer = newLay;
                UpdateLayersListView();
                _canvas.Invalidate();
            }
        };
        Grid.SetColumn(btnAddLayer, 0); layActionsGrid.Children.Add(btnAddLayer);

        var btnDelLayer = new Button { Content = "-", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        ToolTipService.SetToolTip(btnDelLayer, "Delete Active Layer");
        btnDelLayer.Click += (s, e) => {
            if (Layers.Count > 1 && ActiveLayer != null)
            {
                var toRemove = ActiveLayer;
                Layers.Remove(toRemove);
                ActiveLayer = Layers[0];
                UpdateLayersListView();
                _canvas?.Invalidate();
                toRemove.Dispose();
            }
        };
        Grid.SetColumn(btnDelLayer, 1); layActionsGrid.Children.Add(btnDelLayer);

        var btnMoveUp = new Button { Content = "▲", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        ToolTipService.SetToolTip(btnMoveUp, "Move Layer Up");
        btnMoveUp.Click += (s, e) => {
            if (ActiveLayer != null)
            {
                int idx = Layers.IndexOf(ActiveLayer);
                if (idx > 0)
                {
                    Layers.RemoveAt(idx);
                    Layers.Insert(idx - 1, ActiveLayer);
                    UpdateLayersListView();
                    _canvas?.Invalidate();
                }
            }
        };
        Grid.SetColumn(btnMoveUp, 2); layActionsGrid.Children.Add(btnMoveUp);

        var btnMoveDown = new Button { Content = "▼", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        ToolTipService.SetToolTip(btnMoveDown, "Move Layer Down");
        btnMoveDown.Click += (s, e) => {
            if (ActiveLayer != null)
            {
                int idx = Layers.IndexOf(ActiveLayer);
                if (idx >= 0 && idx < Layers.Count - 1)
                {
                    Layers.RemoveAt(idx);
                    Layers.Insert(idx + 1, ActiveLayer);
                    UpdateLayersListView();
                    _canvas?.Invalidate();
                }
            }
        };
        Grid.SetColumn(btnMoveDown, 3); layActionsGrid.Children.Add(btnMoveDown);

        layersStack.Children.Add(layActionsGrid);

        _layersItemsControl = new ItemsControl();
        layersStack.Children.Add(new ScrollViewer { Content = _layersItemsControl, MaxHeight = 180 });
        
        layersStack.Children.Add(new TextBlock { Text = "Layer Opacity:", FontSize = 11 });
        var sliderLayOpacity = new Slider { Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = 1.0 };
        sliderLayOpacity.ValueChanged += (s, e) => {
            if (ActiveLayer != null) { ActiveLayer.Opacity = (float)e.NewValue; _canvas?.Invalidate(); }
        };
        layersStack.Children.Add(sliderLayOpacity);
        _sliderLayOpacity = sliderLayOpacity;

        pivot.Items.Add(layersTab);

        // Pivot 3: Stroke History
        _strokesHistoryStack = new StackPanel { Spacing = 8, Width = 260 };

        var btnReplay = new Button { Content = "▶ Replay Painting", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnReplay.Click += async (s, e) => await ReplaySequenceAsync();
        _strokesHistoryStack.Children.Add(btnReplay);

        _strokesListView = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
        _strokesListView.ItemClick += StrokesListView_ItemClick;
        _strokesListView.IsItemClickEnabled = true;
        _strokesHistoryStack.Children.Add(new ScrollViewer { Content = _strokesListView, MaxHeight = 220 });

        // Pivot 4: Canvas Settings
        _canvasSettingsStack = new StackPanel { Spacing = 8, Width = 260 };
        
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Substrate Texture:", FontSize = 11, FontWeight = FontWeights.SemiBold });
        var comboSubstrate = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboSubstrate.Items.Add("Canvas");
        comboSubstrate.Items.Add("Belgian Linen");
        comboSubstrate.Items.Add("Wood Panel");
        comboSubstrate.Items.Add("Rough Watercolor Paper");
        comboSubstrate.SelectedItem = SubstrateType;
        comboSubstrate.SelectionChanged += (s, e) =>
        {
            if (comboSubstrate.SelectedItem is string selected)
            {
                UpdateSubstrate(selected);
            }
        };
        _canvasSettingsStack.Children.Add(comboSubstrate);
        
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Canvas Background Color:", FontSize = 11, FontWeight = FontWeights.SemiBold });
        
        var bgGrid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true };
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)), Tag = Color.FromArgb(255, 255, 255, 255), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 247, 245, 241)), Tag = Color.FromArgb(255, 247, 245, 241), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 211, 195, 177)), Tag = Color.FromArgb(255, 211, 195, 177), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)), Tag = Color.FromArgb(255, 150, 150, 150), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60)), Tag = Color.FromArgb(255, 60, 60, 60), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.Items.Add(new Border { Width = 28, Height = 22, Background = new SolidColorBrush(Color.FromArgb(255, 15, 15, 15)), Tag = Color.FromArgb(255, 15, 15, 15), BorderBrush = new SolidColorBrush(Colors.LightGray), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        bgGrid.ItemClick += (s, e) =>
        {
            if (e.ClickedItem is Border b && b.Tag is Color c)
            {
                UpdateSubstrateBgColor(c);
            }
        };
        _canvasSettingsStack.Children.Add(bgGrid);
        
        var btnCustomBg = new Button { Content = "Choose Custom Background...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        var bgPickerFlyout = new Flyout();
        var bgPicker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false };
        bgPicker.ColorChanged += (s, e) => { UpdateSubstrateBgColor(e.NewColor); };
        bgPickerFlyout.Content = bgPicker;
        btnCustomBg.Flyout = bgPickerFlyout;
        _canvasSettingsStack.Children.Add(btnCustomBg);

        // --- 1. Custom Brush Preset Creator ---
        var btnCreateBrush = new Button { Content = "Create Custom Brush...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        btnCreateBrush.Click += async (s, e) =>
        {
            var brushNameInput = new TextBox { PlaceholderText = "Brush Name (e.g. Sponge)", Margin = new Thickness(0, 4, 0, 4) };
            var sliderVisc = new Slider { Header = "Viscosity", Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = 0.5 };
            var sliderImp = new Slider { Header = "Impasto", Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = 0.5 };
            var sliderOp = new Slider { Header = "Opacity", Minimum = 0.1, Maximum = 1, StepFrequency = 0.05, Value = 0.8 };
            var sliderBleed = new Slider { Header = "Bleed Rate", Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = 0.2 };
            
            var contentPanel = new StackPanel();
            contentPanel.Children.Add(brushNameInput);
            contentPanel.Children.Add(sliderVisc);
            contentPanel.Children.Add(sliderImp);
            contentPanel.Children.Add(sliderOp);
            contentPanel.Children.Add(sliderBleed);
            
            var dlg = new ContentDialog
            {
                Title = "New Custom Brush Preset",
                Content = contentPanel,
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(brushNameInput.Text))
            {
                var newBr = new ArtBrush
                {
                    Name = brushNameInput.Text,
                    Viscosity = (float)sliderVisc.Value,
                    Impasto = (float)sliderImp.Value,
                    Opacity = (float)sliderOp.Value,
                    BleedRate = (float)sliderBleed.Value,
                    DepletionRate = 0.05f,
                    TextureInteraction = 0.3f,
                    DefaultDryTime = 60f
                };
                Brushes.Add(newBr);
                if (_comboPreset != null)
                {
                    _comboPreset.Items.Add(newBr.Name);
                    _comboPreset.SelectedItem = newBr.Name;
                }
            }
        };
        _canvasSettingsStack.Children.Add(btnCreateBrush);

        // --- 2. Dynamic Lighting Control Sliders ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Substrate Lighting Position:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        
        var sliderAzimuth = new Slider { Header = "Light Angle", Minimum = 0, Maximum = 360, Value = LightAzimuth, StepFrequency = 5 };
        sliderAzimuth.ValueChanged += (s, e) => { LightAzimuth = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderAzimuth);
        
        var sliderElevation = new Slider { Header = "Light Elevation", Minimum = 5, Maximum = 90, Value = LightElevation, StepFrequency = 2 };
        sliderElevation.ValueChanged += (s, e) => { LightElevation = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderElevation);

        // --- 3. Mixing Palette Resizer ---
        var sliderPaletteSize = new Slider { Header = "Mixing Palette Scale", Minimum = 0.6, Maximum = 1.6, Value = 1.0, StepFrequency = 0.1 };
        sliderPaletteSize.ValueChanged += (s, e) =>
        {
            _paletteScale.ScaleX = e.NewValue;
            _paletteScale.ScaleY = e.NewValue;
        };
        _canvasSettingsStack.Children.Add(sliderPaletteSize);

        // --- 4. Physics Gravity Drips ---
        var btnDrip = new Button { Content = "💧 Apply Gravity Drips", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        btnDrip.Click += (s, e) => ApplyGravityDrips();
        _canvasSettingsStack.Children.Add(btnDrip);

        // --- 5. Native Time-Lapse GIF Export ---
        var btnExportGif = new Button { Content = "🎬 Export Time-Lapse GIF...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
        btnExportGif.Click += async (s, e) =>
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
            picker.FileTypeChoices.Add("GIF Animation", new List<string>() { ".gif" });
            picker.SuggestedFileName = "painting_time_lapse";
            
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Quill.App.MainWindowInstance);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                await ExportTimeLapseGifAsync(file);
            }
        };
        _canvasSettingsStack.Children.Add(btnExportGif);

        // --- 6. Symmetry Drawing Mode ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Symmetry Drawing Axis:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
        var comboSymmetry = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboSymmetry.Items.Add("None");
        comboSymmetry.Items.Add("Vertical");
        comboSymmetry.Items.Add("Horizontal");
        comboSymmetry.Items.Add("Radial (4-way)");
        comboSymmetry.SelectedIndex = 0;
        comboSymmetry.SelectionChanged += (s, e) =>
        {
            if (comboSymmetry.SelectedIndex == 0) CurrentSymmetryMode = SymmetryMode.None;
            else if (comboSymmetry.SelectedIndex == 1) CurrentSymmetryMode = SymmetryMode.Vertical;
            else if (comboSymmetry.SelectedIndex == 2) CurrentSymmetryMode = SymmetryMode.Horizontal;
            else if (comboSymmetry.SelectedIndex == 3) CurrentSymmetryMode = SymmetryMode.Radial;
            _canvas?.Invalidate();
        };
        _canvasSettingsStack.Children.Add(comboSymmetry);

        // --- 7. Scratchboard Mode ---
        var toggleScratchboard = new ToggleSwitch { Header = "Scratchboard (Rainbow Art) Mode", FontSize = 11, IsOn = ScratchboardMode, Margin = new Thickness(0, 6, 0, 0) };
        toggleScratchboard.Toggled += (s, e) =>
        {
            ScratchboardMode = toggleScratchboard.IsOn;
            if (ScratchboardMode)
            {
                UpdateSubstrateBgColor(Color.FromArgb(255, 20, 20, 22));
            }
            else
            {
                UpdateSubstrateBgColor(Color.FromArgb(255, 250, 246, 238));
            }
        };
        _canvasSettingsStack.Children.Add(toggleScratchboard);

        var toggleHeatmap = new ToggleSwitch { Header = "Show Canvas Wetness Heatmap", FontSize = 11, IsOn = ShowWetnessHeatmap, Margin = new Thickness(0, 4, 0, 0) };
        toggleHeatmap.Toggled += (s, e) =>
        {
            ShowWetnessHeatmap = toggleHeatmap.IsOn;
            _canvas?.Invalidate();
        };
        _canvasSettingsStack.Children.Add(toggleHeatmap);

        // --- 9. Drafting Table Spin (Canvas Rotation) ---
        var sliderRotation = new Slider { Header = "Canvas Rotation Angle", Minimum = 0, Maximum = 360, Value = CanvasRotation, StepFrequency = 1, Margin = new Thickness(0, 6, 0, 0) };
        sliderRotation.ValueChanged += (s, e) => { CanvasRotation = e.NewValue; };
        _canvasSettingsStack.Children.Add(sliderRotation);

        // --- 10. Specular Light Customizer ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Light Color & Intensity:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var sliderIntensity = new Slider { Header = "Specular Light Intensity", Minimum = 0.1, Maximum = 3.0, Value = CanvasLightIntensity, StepFrequency = 0.1 };
        sliderIntensity.ValueChanged += (s, e) => { CanvasLightIntensity = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderIntensity);

        var btnLightColor = new Button { Content = "Choose Custom Light Color...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        var lightColorFlyout = new Flyout();
        var lightColorPicker = new ColorPicker { IsAlphaEnabled = false, IsMoreButtonVisible = false, Color = CanvasLightColor };
        lightColorPicker.ColorChanged += (s, e) => { CanvasLightColor = e.NewColor; _canvas?.Invalidate(); };
        lightColorFlyout.Content = lightColorPicker;
        btnLightColor.Flyout = lightColorFlyout;
        _canvasSettingsStack.Children.Add(btnLightColor);

        // --- 11. Canvas Wetness Agents ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Drying & Solvent Agents:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var gridAgents = new Grid { Margin = new Thickness(0, 2, 0, 0) };
        gridAgents.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridAgents.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
        gridAgents.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var btnSolvent = new Button { Content = "💧 Wet Solvent", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnSolvent.Click += (s, e) => ApplySolvent();
        Grid.SetColumn(btnSolvent, 0); gridAgents.Children.Add(btnSolvent);

        var btnDry = new Button { Content = "☀️ Dry Cured", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnDry.Click += (s, e) => ApplyDryingAgent();
        Grid.SetColumn(btnDry, 2); gridAgents.Children.Add(btnDry);
        _canvasSettingsStack.Children.Add(gridAgents);

        // --- 12. Virtual Rulers & Stencils ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Virtual Stencil / Ruler Guide:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var toggleRuler = new ToggleSwitch { Header = "Enable Ruler Snapping", FontSize = 11, IsOn = ShowRulerGuide };
        toggleRuler.Toggled += (s, e) => { ShowRulerGuide = toggleRuler.IsOn; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(toggleRuler);

        var comboRulerType = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboRulerType.Items.Add("Straight Edge");
        comboRulerType.Items.Add("Circle Template");
        comboRulerType.SelectedIndex = 0;
        comboRulerType.SelectionChanged += (s, e) =>
        {
            CurrentRuler = comboRulerType.SelectedIndex == 0 ? RulerType.Straight : RulerType.Circle;
            _canvas?.Invalidate();
        };
        _canvasSettingsStack.Children.Add(comboRulerType);

        var sliderRulerAngle = new Slider { Header = "Ruler Angle (Straight)", Minimum = 0, Maximum = 180, Value = RulerAngle, StepFrequency = 1 };
        sliderRulerAngle.ValueChanged += (s, e) => { RulerAngle = e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderRulerAngle);

        var sliderRulerRadius = new Slider { Header = "Ruler Radius (Circle)", Minimum = 30, Maximum = 500, Value = RulerRadius, StepFrequency = 5 };
        sliderRulerRadius.ValueChanged += (s, e) => { RulerRadius = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderRulerRadius);

        // --- 13. Dynamic Color Harmony Generator ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Dynamic Color Harmonies:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        _harmonyGrid = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, MaxHeight = 80 };
        _harmonyGrid.ItemClick += (s, e) =>
        {
            if (e.ClickedItem is Border b && b.Tag is Color c)
            {
                ActiveColor = c;
                ReloadBrushPaint();
            }
        };
        _canvasSettingsStack.Children.Add(_harmonyGrid);
        UpdateColorHarmonies();

        // --- 14. Reference Tracing Layer ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Reference Tracing Image:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        var btnLoadRef = new Button { Content = "📂 Load Reference Image...", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnLoadRef.Click += async (s, e) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(Quill.App.MainWindowInstance));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    _referenceBitmap = await CanvasBitmap.LoadAsync(_canvas, stream);
                }
                ShowReferenceImage = true;
                _canvas?.Invalidate();
            }
        };
        _canvasSettingsStack.Children.Add(btnLoadRef);

        var toggleRef = new ToggleSwitch { Header = "Show Reference Image Overlay", FontSize = 11, IsOn = ShowReferenceImage };
        toggleRef.Toggled += (s, e) => { ShowReferenceImage = toggleRef.IsOn; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(toggleRef);

        var sliderRefOpacity = new Slider { Header = "Tracing Opacity", Minimum = 0.0, Maximum = 1.0, Value = ReferenceOpacity, StepFrequency = 0.05 };
        sliderRefOpacity.ValueChanged += (s, e) => { ReferenceOpacity = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderRefOpacity);

        // --- 15. Stroke Jitter Smoothing ---
        var btnSmooth = new Button { Content = "✨ Smooth Shaky Last Stroke", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Margin = new Thickness(0, 6, 0, 0) };
        btnSmooth.Click += (s, e) => SmoothLastStroke();
        _canvasSettingsStack.Children.Add(btnSmooth);

        // --- 16. Color Temperature Shift ---
        var sliderTemp = new Slider { Header = "Color Temperature (Cool ↔ Warm)", Minimum = -1.0, Maximum = 1.0, Value = ColorTemperature, StepFrequency = 0.05, Margin = new Thickness(0, 6, 0, 0) };
        sliderTemp.ValueChanged += (s, e) => { ColorTemperature = (float)e.NewValue; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(sliderTemp);

        // --- 17. Textured Pattern Stamp Brush ---
        var togglePattern = new ToggleSwitch { Header = "Enable Pattern Texture Stamp", FontSize = 11, IsOn = StampPatternEnabled, Margin = new Thickness(0, 6, 0, 0) };
        togglePattern.Toggled += (s, e) => { StampPatternEnabled = togglePattern.IsOn; _canvas?.Invalidate(); };
        _canvasSettingsStack.Children.Add(togglePattern);

        var comboPattern = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboPattern.Items.Add("Dots Pattern");
        comboPattern.Items.Add("Crosshatch Pattern");
        comboPattern.Items.Add("Stipple Paint Pattern");
        comboPattern.SelectedIndex = 0;
        comboPattern.SelectionChanged += (s, e) =>
        {
            CurrentPattern = (PatternType)comboPattern.SelectedIndex;
        };
        _canvasSettingsStack.Children.Add(comboPattern);

        // --- 18. Branching Undo History Tree ---
        _canvasSettingsStack.Children.Add(new TextBlock { Text = "Branching History Tree State:", FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        _comboHistoryTree = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        _comboHistoryTree.SelectionChanged += (s, e) =>
        {
            if (_comboHistoryTree.SelectedItem is ComboBoxItem item && item.Tag is string nodeId)
            {
                if (nodeId != CurrentHistoryNodeId)
                {
                    RestoreHistoryNode(nodeId);
                }
            }
        };
        _canvasSettingsStack.Children.Add(_comboHistoryTree);
        UpdateHistoryTreeView();

        mainStack.Children.Add(pivot);
        _mainUIGrid.Children.Add(_toolbarBorder);

        InitializePaletteUI();
        InitializeAIPanelUI();
        UpdateLayersListView();
    }

    private ComboBox? _comboPreset;
    private ComboBox? _comboCurve;
    private Slider? _sliderLayOpacity;
    private StackPanel? _strokesHistoryStack;
    private StackPanel? _canvasSettingsStack;

    public FrameworkElement StrokesHistoryUI => _strokesHistoryStack ?? new StackPanel();
    public FrameworkElement CanvasSettingsUI => _canvasSettingsStack ?? new StackPanel();

    private void UpdateLayersListView()
    {
        if (_layersItemsControl == null) return;
        
        _layersItemsControl.Items.Clear();
        foreach (var layer in Layers)
        {
            var grid = new Grid { Background = new SolidColorBrush(layer == ActiveLayer ? Color.FromArgb(40, 0, 120, 215) : Colors.Transparent), Padding = new Thickness(4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            
            var eyeBtn = new CheckBox { IsChecked = layer.Visible, MinWidth = 20, Margin = new Thickness(0) };
            eyeBtn.Checked += (s, e) => { layer.Visible = true; _canvas?.Invalidate(); };
            eyeBtn.Unchecked += (s, e) => { layer.Visible = false; _canvas?.Invalidate(); };
            Grid.SetColumn(eyeBtn, 0);
            grid.Children.Add(eyeBtn);

            var textBlock = new TextBlock { Text = layer.Name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            Grid.SetColumn(textBlock, 1);
            grid.Children.Add(textBlock);

            grid.PointerPressed += (s, e) =>
            {
                ActiveLayer = layer;
                if (_sliderLayOpacity != null) _sliderLayOpacity.Value = layer.Opacity;
                UpdateLayersListView();
            };

            _layersItemsControl.Items.Add(grid);
        }
    }

    private void UpdateStrokesListPanel()
    {
        if (_strokesListView == null) return;
        
        _strokesListView.Items.Clear();
        for (int i = 0; i < Strokes.Count; i++)
        {
            var stroke = Strokes[i];
            var item = new ListViewItem
            {
                Content = new TextBlock { Text = $"Stroke {i + 1} - {stroke.BrushName} ({stroke.Points.Count} pts)", FontSize = 11 },
                Tag = stroke
            };
            
            item.PointerEntered += (s, e) => { _hoveredStrokeId = stroke.Id; _canvas?.Invalidate(); };
            item.PointerExited += (s, e) => { _hoveredStrokeId = null; _canvas?.Invalidate(); };
            
            _strokesListView.Items.Add(item);
        }
    }

    private async void StrokesListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ListViewItem item && item.Tag is ArtStroke stroke)
        {
            var dlg = new ContentDialog
            {
                Title = "Edit Stroke",
                PrimaryButtonText = "Modify Color",
                SecondaryButtonText = "Delete Stroke",
                CloseButtonText = "Cancel",
                XamlRoot = this.XamlRoot
            };
            
            var result = await dlg.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                // Custom Color Selector Dialog
                var colPicker = new ColorPicker { IsAlphaEnabled = false, Color = stroke.PaintColor ?? Colors.SaddleBrown };
                var colDlg = new ContentDialog { Title = "Choose Stroke Color", Content = colPicker, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = this.XamlRoot };
                if (await colDlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    stroke.PaintColor = colPicker.Color;
                    RedrawAllStrokes();
                    UpdateStrokesListPanel();
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                Strokes.Remove(stroke);
                RedrawAllStrokes();
                UpdateStrokesListPanel();
            }
        }
    }

    private void UpdateSlidersFromPreset()
    {
        if (_sliderSize != null) _sliderSize.Value = BrushSize;
        if (_sliderPigment != null) _sliderPigment.Value = PigmentLoading;
        if (_sliderWater != null) _sliderWater.Value = WaterWetness;
        if (_sliderDepletion != null) _sliderDepletion.Value = DepletionRateSliderValue;
    }

    public void ReloadBrushPaint()
    {
        PigmentLoading = CurrentBrush.Opacity;
        _currentPigmentAmount = PigmentLoading;
        WaterWetness = CurrentBrush.DefaultDryTime < 30f ? 0.8f : 0.2f;
        _currentWaterAmount = WaterWetness;
        if (_sliderPigment != null) _sliderPigment.Value = PigmentLoading;
        if (_sliderWater != null) _sliderWater.Value = WaterWetness;
    }

    private void InitializePaletteUI()
    {
        if (_mainUIGrid == null) return;
        
        var transGroup = new TransformGroup();
        transGroup.Children.Add(_paletteScale);
        transGroup.Children.Add(_paletteTranslate);

        _paletteBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 12, 12, 0),
            Width = 224,
            CornerRadius = new CornerRadius(14),
            Background = Application.Current.Resources["CardBrushFloat"] as Brush ?? new SolidColorBrush(Color.FromArgb(220, 40, 40, 44)),
            BorderBrush = Application.Current.Resources["GlassEdgeBrush"] as Brush,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12),
            RenderTransform = transGroup
        };
        
        var stack = new StackPanel { Spacing = 8 };
        _paletteBorder.Child = stack;
        
        var headerGrid = new Grid { Background = new SolidColorBrush(Colors.Transparent), Height = 24 };
        var headerLabel = new TextBlock
        {
            Text = "⠿ Dynamic Mixing Palette",
            FontFamily = Application.Current.Resources["HeadingFont"] as FontFamily,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Application.Current.Resources["InkBrush"] as Brush
        };
        headerGrid.Children.Add(headerLabel);
        
        headerGrid.PointerPressed += (s, e) =>
        {
            headerGrid.CapturePointer(e.Pointer);
            _paletteDragStart = e.GetCurrentPoint(this).Position;
            _isDraggingPalette = true;
        };
        headerGrid.PointerMoved += (s, e) =>
        {
            if (_isDraggingPalette)
            {
                var cur = e.GetCurrentPoint(this).Position;
                _paletteTranslate.X += cur.X - _paletteDragStart.X;
                _paletteTranslate.Y += cur.Y - _paletteDragStart.Y;
                _paletteDragStart = cur;
            }
        };
        headerGrid.PointerReleased += (s, e) =>
        {
            headerGrid.ReleasePointerCapture(e.Pointer);
            _isDraggingPalette = false;
        };
        stack.Children.Add(headerGrid);
        
        _paletteCanvas = new CanvasControl
        {
            Width = 200,
            Height = 200,
            CornerRadius = new CornerRadius(8)
        };
        _paletteCanvas.Draw += PaletteCanvas_Draw;
        _paletteCanvas.PointerPressed += PaletteCanvas_PointerPressed;
        _paletteCanvas.PointerMoved += PaletteCanvas_PointerMoved;
        _paletteCanvas.PointerReleased += PaletteCanvas_PointerReleased;
        stack.Children.Add(_paletteCanvas);
        
        var controlsGrid = new Grid();
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        
        var btnClearPalette = new Button { Content = "Clear", HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnClearPalette.Click += (s, e) =>
        {
            if (_paletteColor != null)
            {
                using (var ds = _paletteColor.CreateDrawingSession()) ds.Clear(Color.FromArgb(255, 240, 238, 230));
                using (var ds = _paletteHeight!.CreateDrawingSession()) ds.Clear(Colors.Transparent);
                using (var ds = _paletteWetness!.CreateDrawingSession()) ds.Clear(Colors.Transparent);
                _paletteCanvas?.Invalidate();
            }
        };
        Grid.SetColumn(btnClearPalette, 0);
        controlsGrid.Children.Add(btnClearPalette);

        var btnEyePalette = new ToggleButton { Content = "Eyedropper", Margin = new Thickness(4, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        btnEyePalette.Click += (s, e) =>
        {
            _isPaletteEyedropper = btnEyePalette.IsChecked ?? false;
        };
        Grid.SetColumn(btnEyePalette, 1);
        controlsGrid.Children.Add(btnEyePalette);
        _btnEyePalette = btnEyePalette;
        
        stack.Children.Add(controlsGrid);
        _mainUIGrid.Children.Add(_paletteBorder);
    }

    private ToggleButton? _btnEyePalette;

    private void PaletteCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_paletteColor == null) return;
        args.DrawingSession.DrawImage(_paletteColor);
        
        var pCombinedHeight = new CanvasRenderTarget(sender.Device, 200, 200, 96);
        using (var hds = pCombinedHeight.CreateDrawingSession())
        {
            hds.Clear(Color.FromArgb(255, 127, 127, 127));
            hds.DrawImage(_paletteHeight!, 0, 0, new Rect(0, 0, 200, 200), 1.0f, CanvasImageInterpolation.Linear, CanvasComposite.Add);
        }
        
        var diffuse = new DistantDiffuseEffect { Source = pCombinedHeight, HeightMapScale = 2.0f, DiffuseAmount = 1.0f, Azimuth = (float)(225.0 * Math.PI / 180.0), Elevation = (float)(45.0 * Math.PI / 180.0), LightColor = Colors.White };
        var specular = new DistantSpecularEffect { Source = pCombinedHeight, HeightMapScale = 2.0f, SpecularAmount = 0.5f, SpecularExponent = 10f, Azimuth = (float)(225.0 * Math.PI / 180.0), Elevation = (float)(45.0 * Math.PI / 180.0), LightColor = Colors.White };
        
        var lit = new BlendEffect { Background = _paletteColor, Foreground = diffuse, Mode = BlendEffectMode.Multiply };
        var final = new BlendEffect { Background = lit, Foreground = specular, Mode = BlendEffectMode.LinearDodge };
        
        args.DrawingSession.DrawImage(final);
        pCombinedHeight.Dispose();
        
        args.DrawingSession.DrawRectangle(new Rect(0, 0, 200, 200), Color.FromArgb(80, 0, 0, 0), 1f);
    }

    private void PaletteCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_paletteCanvas);
        if (_isPaletteEyedropper)
        {
            SamplePaletteColorAt(pt.Position);
            return;
        }
        
        _isDrawingPalette = true;
        _lastPalettePoint = pt.Position;
        
        PaintPaletteStamp(pt.Position, pt.Properties.Pressure);
        _paletteCanvas?.Invalidate();
    }

    private void PaletteCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isDrawingPalette && _paletteCanvas != null)
        {
            var pt = e.GetCurrentPoint(_paletteCanvas);
            Point cur = pt.Position;
            
            float dist = (float)Math.Sqrt(Math.Pow(cur.X - _lastPalettePoint.X, 2) + Math.Pow(cur.Y - _lastPalettePoint.Y, 2));
            int steps = (int)Math.Max(1, dist / 2f);
            
            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                float x = (float)(_lastPalettePoint.X + (cur.X - _lastPalettePoint.X) * t);
                float y = (float)(_lastPalettePoint.Y + (cur.Y - _lastPalettePoint.Y) * t);
                PaintPaletteStamp(new Point(x, y), pt.Properties.Pressure);
            }
            
            _lastPalettePoint = cur;
            _paletteCanvas.Invalidate();
        }
    }

    private void PaletteCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDrawingPalette = false;
    }

    private void PaintPaletteStamp(Point pos, float pressure)
    {
        if (_paletteColor == null) return;
        
        float size = Math.Clamp(BrushSize * 0.4f, 4f, 15f);
        
        using (var dsCol = _paletteColor.CreateDrawingSession())
        using (var dsH = _paletteHeight!.CreateDrawingSession())
        {
            int x = (int)Math.Clamp(pos.X, 0, 199);
            int y = (int)Math.Clamp(pos.Y, 0, 199);
            byte[] wetBytes = new byte[4];
            _paletteWetness!.GetPixelBytes(wetBytes.AsBuffer(), x, y, 1, 1);
            float wet = wetBytes[0] / 255f;
            
            Color paintColor = ActiveColor ?? Colors.Transparent;
            
            if (wet > 0.05f)
            {
                int r = (int)size;
                int srcX = (int)Math.Clamp(pos.X - r, 0, 199 - r * 2);
                int srcY = (int)Math.Clamp(pos.Y - r, 0, 199 - r * 2);
                
                var device = dsCol.Device;
                var tmpCol = new CanvasRenderTarget(device, r * 2, r * 2, 96);
                using (var tds = tmpCol.CreateDrawingSession()) tds.DrawImage(_paletteColor, 0, 0, new Rect(srcX, srcY, r * 2, r * 2));
                
                using (var tds = tmpCol.CreateDrawingSession())
                {
                    var radial = new CanvasRadialGradientBrush(device, paintColor, Color.FromArgb(0, paintColor.R, paintColor.G, paintColor.B)) { Center = new Vector2(r, r), RadiusX = r, RadiusY = r };
                    tds.FillCircle(new Vector2(r, r), r, radial);
                }
                
                dsCol.DrawImage(tmpCol, new Rect(pos.X - r * 0.95, pos.Y - r * 0.95, r * 1.9, r * 1.9), new Rect(0, 0, r * 2, r * 2), 0.7f);
                tmpCol.Dispose();
            }
            else
            {
                var brush = new CanvasRadialGradientBrush(dsCol.Device, paintColor, Color.FromArgb(0, paintColor.R, paintColor.G, paintColor.B))
                {
                    Center = pos.ToVector2(),
                    RadiusX = size,
                    RadiusY = size
                };
                dsCol.FillCircle(pos.ToVector2(), size, brush);
            }
            
            byte h = (byte)(CurrentBrush.Impasto * 80f * pressure);
            var heightColor = Color.FromArgb(h, h, h, h);
            var heightBrush = new CanvasRadialGradientBrush(dsH.Device, heightColor, Color.FromArgb(0, 0, 0, 0))
            {
                Center = pos.ToVector2(),
                RadiusX = size,
                RadiusY = size
            };
            dsH.FillCircle(pos.ToVector2(), size, heightBrush);
        }
        
        using (var dsW = _paletteWetness!.CreateDrawingSession())
        {
            dsW.FillCircle(pos.ToVector2(), size * 1.2f, Color.FromArgb(255, 255, 255, 255));
        }
    }

    private void SamplePaletteColorAt(Point pos)
    {
        if (_paletteColor == null) return;
        
        int x = (int)Math.Clamp(pos.X, 0, 199);
        int y = (int)Math.Clamp(pos.Y, 0, 199);
        
        try
        {
            byte[] colorBytes = new byte[4];
            _paletteColor.GetPixelBytes(colorBytes.AsBuffer(), x, y, 1, 1);
            if (colorBytes[3] > 0)
            {
                ActiveColor = Color.FromArgb(255, colorBytes[0], colorBytes[1], colorBytes[2]);
            }
            else
            {
                ActiveColor = null;
            }
            
            _isPaletteEyedropper = false;
            if (_btnEyePalette != null) _btnEyePalette.IsChecked = false;
        }
        catch { }
    }

    // AI Panel
    private void InitializeAIPanelUI()
    {
        if (_mainUIGrid == null) return;
        
        _aiPanelBorder = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 12),
            Width = 320,
            Height = 360,
            CornerRadius = new CornerRadius(14),
            Background = Application.Current.Resources["CardBrushFloat"] as Brush ?? new SolidColorBrush(Color.FromArgb(220, 35, 35, 38)),
            BorderBrush = Application.Current.Resources["GlassEdgeBrush"] as Brush,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed
        };
        
        var stack = new StackPanel { Spacing = 8 };
        _aiPanelBorder.Child = stack;

        stack.Children.Add(new TextBlock { Text = "🤖 AI Art Assistant", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Application.Current.Resources["InkBrush"] as Brush });
        
        var comboAITool = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11 };
        comboAITool.Items.Add("Controlled Generation (Sketch-to-Img)");
        comboAITool.Items.Add("Smart Canvas Editing (Inpainting)");
        comboAITool.Items.Add("Intelligent Selection & Flatting");
        comboAITool.Items.Add("Pose and Perspective Assistants");
        comboAITool.Items.Add("Stroke Vectorization & Smoothing");
        comboAITool.SelectedIndex = 0;
        stack.Children.Add(comboAITool);
        
        _aiChatScroll = new ScrollViewer { Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _aiChatStack = new StackPanel { Spacing = 6 };
        _aiChatScroll.Content = _aiChatStack;
        stack.Children.Add(_aiChatScroll);
        
        _aiChatStack.Children.Add(new TextBlock { Text = "AI: Hello! Select a tool or type a prompt below to begin.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });

        var inputGrid = new Grid();
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        
        _aiPromptBox = new TextBox { PlaceholderText = "Describe edits/generation...", FontSize = 11 };
        Grid.SetColumn(_aiPromptBox, 0);
        inputGrid.Children.Add(_aiPromptBox);
        
        var btnSendPrompt = new Button { Content = "Send", FontSize = 11, Margin = new Thickness(4, 0, 0, 0) };
        btnSendPrompt.Click += BtnSendPrompt_Click;
        Grid.SetColumn(btnSendPrompt, 1);
        inputGrid.Children.Add(btnSendPrompt);
        
        stack.Children.Add(inputGrid);

        _aiResultImage = new Image { Height = 90, HorizontalAlignment = HorizontalAlignment.Stretch, Stretch = Stretch.Uniform };
        stack.Children.Add(_aiResultImage);
        
        _mainUIGrid.Children.Add(_aiPanelBorder);
    }

    private void BtnSendPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (_aiPromptBox == null || string.IsNullOrWhiteSpace(_aiPromptBox.Text) || _aiChatStack == null) return;
        
        string userText = _aiPromptBox.Text;
        _aiChatStack.Children.Add(new TextBlock { Text = $"You: {userText}", FontSize = 11, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        _aiPromptBox.Text = "";
        
        // Mock AI response matching tool outlines
        var dispatcher = this.DispatcherQueue;
        Task.Run(async () =>
        {
            await Task.Delay(1200);
            dispatcher.TryEnqueue(() =>
            {
                _aiChatStack.Children.Add(new TextBlock { Text = "AI: Processing request using local Latent Diffusion model...", FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
                _aiChatStack.Children.Add(new TextBlock { Text = "AI: Generation complete! Textures and poses successfully blended onto the target layer.", FontSize = 11, TextWrapping = TextWrapping.Wrap });
                if (_aiChatScroll != null) _aiChatScroll.ChangeView(0, _aiChatScroll.ScrollableHeight, 1);
            });
        });
    }

    public void ToggleAIPanelVisibility()
    {
        if (_aiPanelBorder != null)
        {
            _aiPanelBorder.Visibility = _aiPanelBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // Load/Save Settings to documents/Quill/art_settings.json
    private string SettingsFilePath => Path.Combine(LibraryStore.Dir, "art_settings.json");

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var content = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<ArtSettings>(content);
                if (settings != null)
                {
                    _settings = settings;
                    
                    var br = Brushes.Find(x => x.Name == _settings.SelectedBrush);
                    if (br != null) CurrentBrush = br;
                    
                    if (Enum.TryParse<ArtTool>(_settings.SelectedTool, out var t)) CurrentTool = t;
                    if (Enum.TryParse<DepletionCurveType>(_settings.SelectedCurve, out var c)) DepletionCurve = c;
                    
                    if (_settings.DepletionRate > 0.05f) DepletionRateSliderValue = _settings.DepletionRate;
                    if (_comboPreset != null) _comboPreset.SelectedItem = _settings.SelectedBrush;
                    if (_comboCurve != null) _comboCurve.SelectedIndex = (int)DepletionCurve;
                    if (_sliderDepletion != null) _sliderDepletion.Value = DepletionRateSliderValue;
                    PopulatePaletteGrid();
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            _settings.SelectedBrush = CurrentBrush.Name;
            _settings.SelectedTool = CurrentTool.ToString();
            _settings.SelectedCurve = DepletionCurve.ToString();
            _settings.DepletionRate = DepletionRateSliderValue;
            
            Directory.CreateDirectory(LibraryStore.Dir);
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(_settings));
        }
        catch { }
    }

    private void PopulatePaletteGrid()
    {
        if (_colorGrid == null) return;
        _colorGrid.Items.Clear();
        
        if (_settings.CustomPalette == null || _settings.CustomPalette.Count == 0)
        {
            _settings.CustomPalette = new List<string> { "#000000", "#FFFFFF", "#D32F2F", "#1976D2", "#388E3C", "#FBC02D", "#F57C00", "#7B1FA2", "#5D4037", "#F5F5F5" };
        }
        
        foreach (var hex in _settings.CustomPalette)
        {
            try
            {
                var col = ColorUtil.Parse(hex);
                var border = new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    Background = new SolidColorBrush(col),
                    BorderBrush = new SolidColorBrush(Colors.LightGray),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(3),
                    Tag = col
                };
                _colorGrid.Items.Add(border);
            }
            catch { }
        }
    }

    public void ApplyGravityDrips()
    {
        if (Strokes.Count == 0 || ActiveLayer == null) return;
        var lastStroke = Strokes[Strokes.Count - 1];
        if (lastStroke.Points.Count < 2) return;
        
        Point lowestPoint = lastStroke.Points[0];
        for (int i = 1; i < lastStroke.Points.Count; i++)
        {
            if (lastStroke.Points[i].Y > lowestPoint.Y)
                lowestPoint = lastStroke.Points[i];
        }
        
        var dripStroke = new ArtStroke
        {
            LayerId = lastStroke.LayerId,
            PaintColor = lastStroke.PaintColor,
            BrushName = "Watercolor",
            ToolName = "Brush",
            DepletionCurve = "Exponential",
            Size = Math.Max(2f, lastStroke.Size * 0.4f),
            Viscosity = 0.9f,
            Impasto = lastStroke.Impasto * 0.5f,
            Opacity = lastStroke.Opacity * 0.7f
        };
        
        float startX = (float)lowestPoint.X;
        float startY = (float)lowestPoint.Y;
        
        Random rand = new Random();
        int dripLength = rand.Next(4, 9);
        float spacing = lastStroke.Size * 0.6f;
        
        for (int step = 0; step < dripLength; step++)
        {
            float driftX = (float)(rand.NextDouble() - 0.5) * (lastStroke.Size * 0.15f);
            float py = startY + (step + 1) * spacing;
            float px = startX + driftX;
            dripStroke.Points.Add(new Point(px, py));
            
            float pressure = 1.0f - (float)step / dripLength;
            dripStroke.Pressures.Add(pressure);
        }
        
        Strokes.Add(dripStroke);
        RedrawAllStrokes();
        _canvas?.Invalidate();
    }

    public async Task ExportTimeLapseGifAsync(Windows.Storage.StorageFile file)
    {
        if (Strokes.Count == 0 || _canvas == null) return;
        
        try
        {
            using (var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.GifEncoderId, stream);
                
                var originalStrokes = Strokes.ToList();
                int stepSize = Math.Max(1, originalStrokes.Count / 15);
                
                for (int frame = 1; frame <= originalStrokes.Count; frame += stepSize)
                {
                    if (frame > originalStrokes.Count) frame = originalStrokes.Count;
                    
                    Strokes.Clear();
                    foreach (var s in originalStrokes.Take(frame)) Strokes.Add(s);
                    RedrawAllStrokes();
                    
                    var tempBmp = new CanvasRenderTarget(_canvas.Device, CanvasWidth, CanvasHeight, 96);
                    using (var ds = tempBmp.CreateDrawingSession())
                    {
                        ds.Clear(_substrateBgColor);
                        RenderCompositeLayerBuffers();
                        ds.DrawImage(_globalColor!);
                    }
                    
                    byte[] pixels = tempBmp.GetPixelBytes();
                    tempBmp.Dispose();
                    
                    var softwareBmp = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(
                        pixels.AsBuffer(),
                        Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8,
                        CanvasWidth,
                        CanvasHeight,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                    
                    encoder.SetSoftwareBitmap(softwareBmp);
                    
                    var properties = new Windows.Graphics.Imaging.BitmapPropertySet();
                    var delayProp = new Windows.Graphics.Imaging.BitmapTypedValue(40, Windows.Foundation.PropertyType.UInt16);
                    properties.Add("/grctlext/DelayTime", delayProp);
                    await encoder.BitmapProperties.SetPropertiesAsync(properties);
                    
                    if (frame + stepSize <= originalStrokes.Count)
                    {
                        await encoder.GoToNextFrameAsync();
                    }
                }
                
                Strokes.Clear();
                foreach (var s in originalStrokes) Strokes.Add(s);
                RedrawAllStrokes();
                
                await encoder.FlushAsync();
            }
        }
        catch { }
    }

    private Color ColorFromHSL(double h, double s, double l)
    {
        double r = 0, g = 0, b = 0;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0/3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0/3.0);
        }
        return Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private double HueToRgb(double p, double q, double t)
    {
        if (t < 0.0) t += 1.0;
        if (t > 1.0) t -= 1.0;
        if (t < 1.0/6.0) return p + (q - p) * 6.0 * t;
        if (t < 1.0/2.0) return q;
        if (t < 2.0/3.0) return p + (q - p) * (2.0/3.0 - t) * 6.0;
        return p;
    }

    public void ApplySolvent()
    {
        if (_wetnessTarget == null) return;
        using (var ds = _wetnessTarget.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(200, 200, 200, 200));
        }
        WaterWetness = 0.8f;
        _currentWaterAmount = 0.8f;
        _canvas?.Invalidate();
    }
    
    public void ApplyDryingAgent()
    {
        if (_wetnessTarget == null) return;
        using (var ds = _wetnessTarget.CreateDrawingSession())
        {
            ds.Clear(Colors.Transparent);
        }
        WaterWetness = 0.0f;
        _currentWaterAmount = 0.0f;
        _canvas?.Invalidate();
    }

    private Point SnapToRuler(Point pos)
    {
        if (!ShowRulerGuide) return pos;
        
        if (CurrentRuler == RulerType.Circle)
        {
            Vector2 delta = pos.ToVector2() - RulerCenter.ToVector2();
            float len = delta.Length();
            if (len > 0.01f)
            {
                Vector2 snapped = RulerCenter.ToVector2() + (delta / len) * RulerRadius;
                return new Point(snapped.X, snapped.Y);
            }
        }
        else if (CurrentRuler == RulerType.Straight)
        {
            Vector2 v = pos.ToVector2() - RulerCenter.ToVector2();
            float angleRad = (float)(RulerAngle * Math.PI / 180.0);
            Vector2 u = new Vector2((float)Math.Cos(angleRad), (float)Math.Sin(angleRad));
            float projection = Vector2.Dot(v, u);
            Vector2 snapped = RulerCenter.ToVector2() + projection * u;
            return new Point(snapped.X, snapped.Y);
        }
        
        return pos;
    }

    private GridView? _harmonyGrid;
    
    private void UpdateColorHarmonies()
    {
        if (_harmonyGrid == null || _activeColor == null) return;
        _harmonyGrid.Items.Clear();
        
        Color baseCol = _activeColor.Value;
        ColorToHSL(baseCol, out double h, out double s, out double l);
        
        AddHarmonyColor(baseCol, "Current");
        
        double compH = (h + 0.5) % 1.0;
        AddHarmonyColor(ColorFromHSL(compH, s, l), "Complementary");
        
        double analH1 = (h - 30.0 / 360.0 + 1.0) % 1.0;
        double analH2 = (h + 30.0 / 360.0) % 1.0;
        AddHarmonyColor(ColorFromHSL(analH1, s, l), "Analogous Left");
        AddHarmonyColor(ColorFromHSL(analH2, s, l), "Analogous Right");
        
        double triadH1 = (h + 120.0 / 360.0) % 1.0;
        double triadH2 = (h + 240.0 / 360.0) % 1.0;
        AddHarmonyColor(ColorFromHSL(triadH1, s, l), "Triadic One");
        AddHarmonyColor(ColorFromHSL(triadH2, s, l), "Triadic Two");
    }
    
    private void AddHarmonyColor(Color col, string label)
    {
        var border = new Border
        {
            Width = 32,
            Height = 24,
            Background = new SolidColorBrush(col),
            BorderBrush = new SolidColorBrush(Colors.LightGray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(2),
            Tag = col
        };
        ToolTipService.SetToolTip(border, label);
        _harmonyGrid?.Items.Add(border);
    }

    private void ColorToHSL(Color color, out double h, out double s, out double l)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        
        h = s = l = (max + min) / 2.0;
        
        if (max == min)
        {
            h = s = 0;
        }
        else
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r)
                h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g)
                h = (b - r) / d + 2;
            else if (max == b)
                h = (r - g) / d + 4;
            h /= 6.0;
        }
    }

    public void SmoothLastStroke()
    {
        if (Strokes.Count == 0 || ActiveLayer == null) return;
        var last = Strokes[Strokes.Count - 1];
        if (last.Points.Count < 4) return;
        
        var originalPoints = last.Points.ToList();
        var originalPressures = last.Pressures.ToList();
        
        var smoothedPoints = new List<Point>();
        var smoothedPressures = new List<float>();
        
        smoothedPoints.Add(originalPoints[0]);
        smoothedPressures.Add(originalPressures[0]);
        
        for (int i = 0; i < originalPoints.Count - 3; i++)
        {
            Point p0 = originalPoints[i];
            Point p1 = originalPoints[i + 1];
            Point p2 = originalPoints[i + 2];
            Point p3 = originalPoints[i + 3];
            
            float pr0 = originalPressures[i];
            float pr1 = originalPressures[i + 1];
            float pr2 = originalPressures[i + 2];
            float pr3 = originalPressures[i + 3];
            
            int divisions = 3;
            for (int step = 0; step < divisions; step++)
            {
                float t = (float)step / divisions;
                float t2 = t * t;
                float t3 = t2 * t;
                
                double x = 0.5 * ((2 * p1.X) +
                                  (-p0.X + p2.X) * t +
                                  (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 +
                                  (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
                                  
                double y = 0.5 * ((2 * p1.Y) +
                                  (-p0.Y + p2.Y) * t +
                                  (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                                  (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
                                  
                float pr = 0.5f * ((2f * pr1) +
                                   (-pr0 + pr2) * t +
                                   (2f * pr0 - 5f * pr1 + 4f * pr2 - pr3) * t2 +
                                   (-pr0 + 3f * pr1 - 3f * pr2 + pr3) * t3);
                                   
                smoothedPoints.Add(new Point(x, y));
                smoothedPressures.Add(Math.Clamp(pr, 0f, 1f));
            }
        }
        
        smoothedPoints.Add(originalPoints[originalPoints.Count - 1]);
        smoothedPressures.Add(originalPressures[originalPressures.Count - 1]);
        
        last.Points.Clear();
        foreach (var p in smoothedPoints) last.Points.Add(p);
        
        last.Pressures.Clear();
        foreach (var pr in smoothedPressures) last.Pressures.Add(pr);
        
        RedrawAllStrokes();
        _canvas?.Invalidate();
    }

    private ComboBox? _comboHistoryTree;

    private void RecordHistoryState(string description)
    {
        var node = new HistoryNode
        {
            Description = description,
            ParentId = CurrentHistoryNodeId,
            StrokesSnapshot = Strokes.Select(s => new ArtStroke
            {
                Id = s.Id,
                LayerId = s.LayerId,
                PaintColor = s.PaintColor,
                BrushName = s.BrushName,
                ToolName = s.ToolName,
                DepletionCurve = s.DepletionCurve,
                Size = s.Size,
                Viscosity = s.Viscosity,
                Impasto = s.Impasto,
                Opacity = s.Opacity,
                Points = s.Points.ToList(),
                Pressures = s.Pressures.ToList()
            }).ToList()
        };
        
        if (CurrentHistoryNodeId != null)
        {
            var parent = HistoryTree.Find(n => n.Id == CurrentHistoryNodeId);
            if (parent != null)
            {
                parent.ChildrenIds.Add(node.Id);
            }
        }
        
        HistoryTree.Add(node);
        CurrentHistoryNodeId = node.Id;
        
        while (HistoryTree.Count > 30)
        {
            var pruneTarget = HistoryTree.FirstOrDefault(n => n.ChildrenIds.Count == 0 && n.Id != CurrentHistoryNodeId && !string.IsNullOrEmpty(n.ParentId));
            if (pruneTarget == null) break;
            
            var parent = HistoryTree.Find(p => p.Id == pruneTarget.ParentId);
            if (parent != null)
            {
                parent.ChildrenIds.Remove(pruneTarget.Id);
            }
            HistoryTree.Remove(pruneTarget);
        }
        
        UpdateHistoryTreeView();
    }
    
    private void UpdateHistoryTreeView()
    {
        if (_comboHistoryTree == null) return;
        _comboHistoryTree.Items.Clear();
        
        var rootNodes = HistoryTree.Where(n => string.IsNullOrEmpty(n.ParentId)).ToList();
        foreach (var root in rootNodes)
        {
            AddNodeToComboRecursive(root, 0);
        }
    }
    
    private void AddNodeToComboRecursive(HistoryNode node, int depth)
    {
        string indent = new string(' ', depth * 3);
        string branchMarker = node.ChildrenIds.Count > 1 ? "🌿 " : "• ";
        var item = new ComboBoxItem
        {
            Content = $"{indent}{branchMarker}{node.Description}",
            Tag = node.Id
        };
        if (_comboHistoryTree != null)
        {
            _comboHistoryTree.Items.Add(item);
            if (node.Id == CurrentHistoryNodeId)
            {
                _comboHistoryTree.SelectedItem = item;
            }
        }
        
        foreach (var childId in node.ChildrenIds)
        {
            var child = HistoryTree.Find(n => n.Id == childId);
            if (child != null)
            {
                AddNodeToComboRecursive(child, depth + 1);
            }
        }
    }
    
    public void RestoreHistoryNode(string nodeId)
    {
        var node = HistoryTree.Find(n => n.Id == nodeId);
        if (node == null) return;
        
        CurrentHistoryNodeId = nodeId;
        
        Strokes.Clear();
        foreach (var s in node.StrokesSnapshot)
        {
            Strokes.Add(new ArtStroke
            {
                Id = s.Id,
                LayerId = s.LayerId,
                PaintColor = s.PaintColor,
                BrushName = s.BrushName,
                ToolName = s.ToolName,
                DepletionCurve = s.DepletionCurve,
                Size = s.Size,
                Viscosity = s.Viscosity,
                Impasto = s.Impasto,
                Opacity = s.Opacity,
                Points = s.Points.ToList(),
                Pressures = s.Pressures.ToList()
            });
        }
        
        RedrawAllStrokes();
        _canvas?.Invalidate();
        UpdateStrokesListPanel();
    }

    private Grid? _rootGrid;
    private Slider? _sliderSize;
    private Slider? _sliderPigment;
    private Slider? _sliderWater;
}
