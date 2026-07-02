using System.Runtime.InteropServices.WindowsRuntime;
using LectureInk.Controls;
using LectureInk.Helpers;
using LectureInk.Models;
using LectureInk.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace LectureInk;

public sealed partial class MainWindow : Window
{
    private readonly Library _library;
    private Notebook? _curNb;
    private Section? _curSec;
    private NotePage? _curPage;
    private TreeViewNode? _selNode;
    private Guid? _activePresetId;
    // Notebooks unlocked for this session (#23).
    private readonly HashSet<Guid> _unlockedNotebooks = new();

    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _zoomTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private Button? _eraserChip;
    private string _lastZoomPct = "100%";
    private bool _calcReady;
    private long _progValue;
    private bool _progHasValue;
    private double _gxMin = -10, _gxMax = 10;
    private readonly Microsoft.Graphics.Canvas.Text.CanvasTextFormat _graphLabelFormat = new() { FontSize = 10 };

    private bool _syncingUi;
    private bool _syncingSize;
    private bool _uiHidden;
    private bool _floatPen;
    // true only when the hide-all button is what entered full screen, so restore
    // won't pull the user out of a full screen they set up themselves.
    private bool _hideEnteredFullscreen;
    // Gallery / start-screen state: which notebook is being browsed (null = the
    // notebook grid) and whether the gallery is acting as the startup picker (#31).
    private Notebook? _galleryNb;
    private bool _galleryLauncher;

    private static readonly string[] QuickColors =
        { "#141413", "#FAF9F5", "#D97757", "#D32F2F", "#FBC02D", "#788C5D", "#6A9BCC", "#7B1FA2" };

    private static readonly string[] Fonts =
    {
        "Lora", "Poppins", "Segoe UI", "Segoe Print", "Segoe Script", "Ink Free",
        "Calibri", "Cambria", "Cambria Math", "Georgia", "Times New Roman", "Garamond",
        "Arial", "Verdana", "Tahoma", "Trebuchet MS", "Comic Sans MS",
        "Consolas", "Cascadia Code", "Cascadia Mono", "Courier New", "JetBrains Mono",
        "Space Mono", "Google Sans Mono", "Maple Mono", "Maple Mono NF CN", "Amsterdam"
    };

    private static readonly string[] FontSizes =
        { "8", "9", "10", "11", "12", "14", "16", "18", "20", "24", "28", "32", "40", "48", "64", "72" };

    private static readonly string[] MathSymbols =
    {
        "×","÷","±","∓","≤","≥","≠","≈","≡","∝","∞","√","∛","∑","∏","∫","∬","∮","∂","∇",
        "Δ","π","θ","α","β","γ","δ","ε","ζ","η","λ","μ","ν","ξ","ρ","σ","τ","φ","χ","ψ",
        "ω","Γ","Θ","Λ","Ξ","Π","Σ","Φ","Ψ","Ω","∈","∉","⊂","⊆","⊄","∪","∩","∅","∧","∨",
        "¬","⊕","⊗","⇒","⇐","⇔","→","←","↔","↦","∀","∃","∄","∴","∵","ℝ","ℕ","ℤ","ℚ","ℂ",
        "°","′","″","‰","⋅","∘","ℏ","ℓ","Å","µ"
    };

    public MainWindow()
    {
        InitializeComponent();
        Title = "Fluent Ink";
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (System.IO.File.Exists(icon)) AppWindow.SetIcon(icon);
        }
        catch { /* icon is best-effort */ }

        _library = LibraryStore.Load();
        SeedPens();
        Surface.PendingFontFamily = _library.DefaultFont;
        Surface.PendingFontSize = (float)_library.DefaultFontSize;

        BuildFontItems();
        SizeCombo.ItemsSource = FontSizes;
        SymbolGrid.ItemsSource = MathSymbols;
        BuildStyleItems();
        CalcModeBox.ItemsSource = new[] { "Standard", "Scientific", "Graphing", "Programmer", "Converter" };
        ProgBase.ItemsSource = new[] { "HEX", "DEC", "OCT", "BIN" };
        ConvCat.ItemsSource = ConvCategories;
        CalcModeBox.SelectedIndex = 1;
        ProgBase.SelectedIndex = 1;
        ConvCat.SelectedIndex = 0;
        _calcReady = true;
        ApplyCalcMode();
        BuildCalcButtons();
        FillConvUnits();

        Surface.TitleClicked += async () => await RenamePageFromTitleAsync();
        Surface.DateClicked += async () => await EditPageDateAsync();

        // notebooks panel: restore size + resize grip
        NotebookPanel.Width = Math.Clamp(_library.NotebookPanelW, 220, 560);
        if (_library.NotebookPanelH > 0)
            NotebookPanel.Height = Math.Clamp(_library.NotebookPanelH, 240, 940);
        NbGrip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        NbGrip.ManipulationDelta += (_, e) =>
        {
            double w = double.IsNaN(NotebookPanel.Width) ? NotebookPanel.ActualWidth : NotebookPanel.Width;
            double h = double.IsNaN(NotebookPanel.Height) ? NotebookPanel.ActualHeight : NotebookPanel.Height;
            NotebookPanel.Width = Math.Clamp(w + e.Delta.Translation.X, 220, 560);
            NotebookPanel.Height = Math.Clamp(h + e.Delta.Translation.Y, 240, 940);
        };
        NbGrip.ManipulationCompleted += (_, _) =>
        {
            _library.NotebookPanelW = NotebookPanel.Width;
            _library.NotebookPanelH = NotebookPanel.Height;
            ScheduleSave();
        };

        StartGlowPulse();

        Surface.ContentChanged += ScheduleSave;
        Surface.ActiveTextChanged += box => { UpdateFormatBarVisibility(); SyncSizeComboFromSelection(box); };
        Surface.ContextMenuRequested += ShowCanvasContextMenu;
        Surface.ReplayEnded += () => BtnReplay.IsChecked = false;
        Surface.UndoManager.Changed += UpdateUndoButtons;
        Surface.ViewChanged += OnViewChanged;
        Surface.RulerAngleChanged += OnRulerAngleChanged;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };
        _zoomTimer.Tick += (_, _) => { _zoomTimer.Stop(); ZoomBorder.Visibility = Visibility.Collapsed; };
        Closed += (_, _) => SaveNow();

        // pen panel dragging -> dock to an edge
        PenGrip.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        PenGrip.ManipulationDelta += (_, e) =>
        {
            if (PenRow.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                PenRow.RenderTransform = tt;
            }
            tt.X += e.Delta.Translation.X;
            tt.Y += e.Delta.Translation.Y;
        };
        PenGrip.ManipulationCompleted += (_, _) => DockPenRowFromPosition();

        // minimal-UI floating buttons: drag the cluster itself; snap to nearest corner (#30)
        MinimalButtons.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        MinimalButtons.ManipulationDelta += (_, e) =>
        {
            if (MinimalButtons.RenderTransform is not TranslateTransform mt)
            {
                mt = new TranslateTransform();
                MinimalButtons.RenderTransform = mt;
            }
            mt.X += e.Delta.Translation.X;
            mt.Y += e.Delta.Translation.Y;
        };
        MinimalButtons.ManipulationCompleted += (_, _) => SnapMinimalButtons();

        // calculator window dragging
        CalcHeader.ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY;
        CalcHeader.ManipulationDelta += (_, e) =>
        {
            if (CalcPanel.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                CalcPanel.RenderTransform = tt;
            }
            tt.X += e.Delta.Translation.X;
            tt.Y += e.Delta.Translation.Y;
        };

        ApplyTheme();
        ApplyPenDock();
        BuildTree();
        BuildPenStrip();
        OpenStartupPage();
        SelectTool("Pen");
        if (_library.Pens.Count > 0) ApplyPreset(_library.Pens[0]);
        UpdateUndoButtons();

        // Startup experience: full screen + the notebook/section/page picker,
        // with the last-used page already loaded behind it (#31).
        if (_library.StartFullscreen)
            try { AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
        UpdateFullscreenIcon();
        if (_library.StartOnGallery) ShowGallery(launcher: true);

        ShowStatus("Right-click a pen to edit its type, colour, size and pressure response. F11 toggles full screen.");
    }

    private void StartGlowPulse()
    {
        try
        {
            if (Application.Current.Resources["GlowBrush"] is not Brush glow) return;
            var anim = new DoubleAnimation
            {
                From = 0.35,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromSeconds(2.4)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EnableDependentAnimation = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(anim, glow);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
        catch
        {
            // glow stays static if the animation can't run
        }
    }

    private void BarScroll_Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        int delta = e.GetCurrentPoint(sv).Properties.MouseWheelDelta;
        sv.ChangeView(sv.HorizontalOffset - delta, null, null);
        e.Handled = true;
    }

    // =======================================================================
    // Persistence / status
    // =======================================================================
    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow()
    {
        Surface.FlushTexts();
        LibraryStore.Save(_library);
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // =======================================================================
    // Theme
    // =======================================================================
    private void ApplyTheme()
    {
        bool dark = _library.Theme == "Dark";
        RootGrid.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        BtnThemeIcon.Glyph = dark ? "\uE706" : "\uE708";
        ApplyTitleBarColors(dark);
    }

    private void ApplyTitleBarColors(bool dark)
    {
        try
        {
            if (!Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;
            var tb = AppWindow.TitleBar;
            var bg = dark ? Color.FromArgb(255, 0x0F, 0x0E, 0x10) : Color.FromArgb(255, 0xF7, 0xF6, 0xF1);
            var fg = dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18);
            var hover = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0);
            var press = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(48, 0, 0, 0);
            tb.BackgroundColor = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.InactiveForegroundColor = fg;
            tb.ButtonBackgroundColor = bg;
            tb.ButtonInactiveBackgroundColor = bg;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = fg;
            tb.ButtonHoverBackgroundColor = hover;
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonPressedBackgroundColor = press;
            tb.ButtonPressedForegroundColor = fg;
        }
        catch { /* title-bar theming is best-effort */ }
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        _library.Theme = _library.Theme == "Dark" ? "Light" : "Dark";
        ApplyTheme();
        ScheduleSave();
        ShowStatus(_library.Theme == "Dark" ? "Dark mode on." : "Light mode on.");
    }

    // =======================================================================
    // Pen panel docking
    // =======================================================================
    private void ApplyPenDock()
    {
        string dock = _library.PenDock;
        bool vertical = dock is "Left" or "Right";

        PenStack.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        PresetPanel.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;

        PenScroll.HorizontalScrollMode = vertical ? ScrollMode.Disabled : ScrollMode.Enabled;
        PenScroll.VerticalScrollMode = vertical ? ScrollMode.Enabled : ScrollMode.Disabled;
        PenScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        PenScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        PenScroll.MaxWidth = vertical ? 200 : 1280;
        PenScroll.MaxHeight = vertical ? 760 : 96;

        switch (dock)
        {
            case "Top":
                PenRow.HorizontalAlignment = HorizontalAlignment.Center;
                PenRow.VerticalAlignment = VerticalAlignment.Top;
                PenRow.Margin = new Thickness(24, 12, 24, 0);
                SetShowBtn(HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(0, 12, 0, 0));
                break;
            case "Left":
                PenRow.HorizontalAlignment = HorizontalAlignment.Left;
                PenRow.VerticalAlignment = VerticalAlignment.Center;
                PenRow.Margin = new Thickness(12, 24, 0, 24);
                SetShowBtn(HorizontalAlignment.Left, VerticalAlignment.Center, new Thickness(12, 0, 0, 0));
                break;
            case "Right":
                PenRow.HorizontalAlignment = HorizontalAlignment.Right;
                PenRow.VerticalAlignment = VerticalAlignment.Center;
                PenRow.Margin = new Thickness(0, 24, 12, 24);
                SetShowBtn(HorizontalAlignment.Right, VerticalAlignment.Center, new Thickness(0, 0, 12, 0));
                break;
            default:
                PenRow.HorizontalAlignment = HorizontalAlignment.Center;
                PenRow.VerticalAlignment = VerticalAlignment.Bottom;
                PenRow.Margin = new Thickness(24, 0, 24, 16);
                SetShowBtn(HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(0, 0, 0, 16));
                break;
        }
        PenRow.RenderTransform = null;
    }

    private void SetShowBtn(HorizontalAlignment h, VerticalAlignment v, Thickness m)
    {
        PenRowShowBtn.HorizontalAlignment = h;
        PenRowShowBtn.VerticalAlignment = v;
        PenRowShowBtn.Margin = m;
    }

    private void DockPenRowFromPosition()
    {
        try
        {
            var t = PenRow.TransformToVisual(CanvasArea);
            var tl = t.TransformPoint(new Point(0, 0));
            double cx = tl.X + PenRow.ActualWidth / 2.0;
            double cy = tl.Y + PenRow.ActualHeight / 2.0;
            double w = CanvasArea.ActualWidth, h = CanvasArea.ActualHeight;
            double dl = cx, dr = w - cx, dt = cy, db = h - cy;
            double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            _library.PenDock = min == dl ? "Left" : min == dr ? "Right" : min == dt ? "Top" : "Bottom";
        }
        catch { }
        ApplyPenDock();
        ScheduleSave();
    }

    // =======================================================================
    // Pen presets
    // =======================================================================
    private void SeedPens()
    {
        if (_library.Pens.Count > 0) return;
        _library.Pens.AddRange(new[]
        {
            new PenPreset { Name = "Ink",          Pen = PenType.Standard,    Color = "#141413", Size = 3.5f, Sens = 1f },
            new PenPreset { Name = "Sky note",     Pen = PenType.Standard,    Color = "#6A9BCC", Size = 3.5f, Sens = 1f },
            new PenPreset { Name = "Red fountain", Pen = PenType.Fountain,    Color = "#D32F2F", Size = 5f,   Sens = 1.4f },
            new PenPreset { Name = "Marker",       Pen = PenType.Highlighter, Color = "#FBC02D", Size = 6f,   Sens = 1f }
        });
    }

    private static string GlyphFor(PenType t) => t switch
    {
        PenType.Brush => "\U0001F58C",
        PenType.Fountain => "✒",
        PenType.Highlighter => "\U0001F58D",
        PenType.Pencil => "✏",
        PenType.Marker => "❚",
        PenType.Calligraphy => "\U0001FAB6",
        PenType.Crayon => "\U0001F58D",
        PenType.Watercolor => "\U0001F58C",
        _ => "\U0001F58A"
    };

    private PenPreset? ActivePreset() => _library?.Pens.FirstOrDefault(x => x.Id == _activePresetId);

    private void BuildPenStrip()
    {
        PresetPanel.Children.Clear();
        BuildEraserChip();
        foreach (var preset in _library.Pens)
        {
            var p = preset;
            var color = ColorUtil.Parse(p.Color);

            var ell = new Ellipse
            {
                Width = 24, Height = 24,
                Fill = new SolidColorBrush(color),
                Stroke = new SolidColorBrush(Color.FromArgb(255, 176, 174, 165)),
                StrokeThickness = 1
            };
            var glyph = new TextBlock
            {
                Text = GlyphFor(p.Pen),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var icon = new Grid { Width = 26, Height = 26 };
            icon.Children.Add(ell);
            icon.Children.Add(glyph);

            var btn = new Button
            {
                Content = icon,
                Padding = new Thickness(2),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(
                    p.Id == _activePresetId ? Color.FromArgb(255, 217, 119, 87) : Colors.Transparent)
            };
            ToolTipService.SetToolTip(btn, $"{p.Name} (right-click to edit)");
            btn.Click += (_, _) => ApplyPreset(p);
            btn.ContextFlyout = CreatePresetFlyout(p, ell, glyph);

            PresetPanel.Children.Add(btn);
        }
    }

    private void BuildEraserChip()
    {
        var icon = new Grid { Width = 26, Height = 26 };
        icon.Children.Add(new Ellipse
        {
            Width = 24, Height = 24,
            Fill = new SolidColorBrush(Color.FromArgb(255, 232, 230, 220)),
            Stroke = new SolidColorBrush(Color.FromArgb(255, 176, 174, 165)),
            StrokeThickness = 1
        });
        icon.Children.Add(new FontIcon { Glyph = "\uE75C", FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 20, 20, 19)) });

        var chip = new Button
        {
            Content = icon,
            Padding = new Thickness(2),
            CornerRadius = new CornerRadius(15),
            BorderThickness = new Thickness(2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(
                Surface.Tool == ToolType.Eraser ? Color.FromArgb(255, 217, 119, 87) : Colors.Transparent)
        };
        ToolTipService.SetToolTip(chip, "Eraser (right-click to pick point or stroke mode)");
        chip.Click += (_, _) => SelectTool("Eraser");

        var fly = new Flyout();
        var panel = new StackPanel { Spacing = 6, Width = 220 };
        var rbPoint = new RadioButton { Content = "Point eraser — erases only what you touch", FontSize = 12, IsChecked = Surface.EraserMode == EraserMode.Point };
        var rbObject = new RadioButton { Content = "Stroke eraser — removes whole strokes", FontSize = 12, IsChecked = Surface.EraserMode == EraserMode.Object };
        rbPoint.Checked += (_, _) => { Surface.EraserMode = EraserMode.Point; SelectTool("Eraser"); };
        rbObject.Checked += (_, _) => { Surface.EraserMode = EraserMode.Object; SelectTool("Eraser"); };
        panel.Children.Add(rbPoint);
        panel.Children.Add(rbObject);
        fly.Content = panel;
        chip.ContextFlyout = fly;

        _eraserChip = chip;
        PresetPanel.Children.Add(chip);
    }

    private void RefreshEraserChip()
    {
        if (_eraserChip != null)
        {
            _eraserChip.BorderBrush = new SolidColorBrush(
                Surface.Tool == ToolType.Eraser ? Color.FromArgb(255, 217, 119, 87) : Colors.Transparent);
        }
    }

    private Flyout CreatePresetFlyout(PenPreset p, Ellipse ell, TextBlock glyph)
    {
        var fly = new Flyout();
        bool built = false;
        fly.Opening += (_, _) =>
        {
            if (built) return;
            built = true;

            var panel = new StackPanel { Spacing = 8, Width = 272 };

            var nameBox = new TextBox { Header = "Name", Text = p.Name };
            nameBox.TextChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(nameBox.Text)) p.Name = nameBox.Text.Trim();
                ScheduleSave();
            };
            panel.Children.Add(nameBox);

            var typeCombo = new ComboBox
            {
                Header = "Pen type",
                ItemsSource = new[] { "Standard pen", "Brush pen", "Fountain pen", "Highlighter", "Pencil", "Marker (chisel)", "Calligraphy",
                                      "Crayon", "Watercolour", "Monoline", "Rollerball", "Gel pen", "Ballpoint", "Felt-tip" },
                SelectedIndex = (int)p.Pen,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            typeCombo.SelectionChanged += (_, _) =>
            {
                if (typeCombo.SelectedIndex < 0) return;
                p.Pen = (PenType)typeCombo.SelectedIndex;
                glyph.Text = GlyphFor(p.Pen);
                if (_activePresetId == p.Id) Surface.Pen = p.Pen;
                ScheduleSave();
            };
            panel.Children.Add(typeCombo);

            ColorPicker? pickerRef = null;
            void SetColor(Color c)
            {
                p.Color = ColorUtil.ToHex(c);
                ell.Fill = new SolidColorBrush(c);
                if (_activePresetId == p.Id) Surface.PenColor = c;
                ScheduleSave();
            }

            var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var hex in QuickColors)
            {
                var c = ColorUtil.Parse(hex);
                var sb = new Button
                {
                    Padding = new Thickness(3),
                    Content = new Ellipse
                    {
                        Width = 16, Height = 16,
                        Fill = new SolidColorBrush(c),
                        Stroke = new SolidColorBrush(Color.FromArgb(120, 128, 128, 128)),
                        StrokeThickness = 1
                    }
                };
                sb.Click += (_, _) =>
                {
                    SetColor(c);
                    if (pickerRef != null)
                    {
                        _syncingUi = true;
                        pickerRef.Color = c;
                        _syncingUi = false;
                    }
                };
                swatches.Children.Add(sb);
            }
            panel.Children.Add(swatches);

            var picker = new ColorPicker
            {
                IsAlphaEnabled = false,
                IsMoreButtonVisible = false,
                ColorSpectrumShape = ColorSpectrumShape.Box,
                Color = ColorUtil.Parse(p.Color)
            };
            pickerRef = picker;
            picker.ColorChanged += (_, args) =>
            {
                if (_syncingUi) return;
                SetColor(args.NewColor);
            };
            panel.Children.Add(new Expander
            {
                Header = "Custom colour",
                Content = picker,
                HorizontalAlignment = HorizontalAlignment.Stretch
            });

            var sizeLabel = new TextBlock { Text = $"Size: {p.Size:0.#}", FontSize = 12 };
            var size = new Slider { Minimum = 1, Maximum = 24, StepFrequency = 0.5, Value = p.Size };
            size.ValueChanged += (_, args) =>
            {
                p.Size = (float)args.NewValue;
                sizeLabel.Text = $"Size: {p.Size:0.#}";
                if (_activePresetId == p.Id) Surface.PenSize = p.Size;
                ScheduleSave();
            };
            panel.Children.Add(sizeLabel);
            panel.Children.Add(size);

            var sensLabel = new TextBlock { Text = $"Pressure response: {p.Sens:0.0}×", FontSize = 12 };
            var sens = new Slider { Minimum = 0.2, Maximum = 2.5, StepFrequency = 0.1, Value = p.Sens };
            sens.ValueChanged += (_, args) =>
            {
                p.Sens = (float)args.NewValue;
                sensLabel.Text = $"Pressure response: {p.Sens:0.0}×";
                if (_activePresetId == p.Id) Surface.PenSensitivity = p.Sens;
                ScheduleSave();
            };
            panel.Children.Add(sensLabel);
            panel.Children.Add(sens);
            panel.Children.Add(new TextBlock
            {
                Text = "Brush and fountain pens react most: press lightly for a hairline, hard for a broad stroke.",
                FontSize = 11,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });

            var del = new Button { Content = "Delete this pen", HorizontalAlignment = HorizontalAlignment.Stretch };
            del.Click += (_, _) =>
            {
                if (_library.Pens.Count <= 1)
                {
                    ShowStatus("Keep at least one pen — add another first.");
                    return;
                }
                fly.Hide();
                _library.Pens.Remove(p);
                if (_activePresetId == p.Id) ApplyPreset(_library.Pens[0]);
                else BuildPenStrip();
                ScheduleSave();
            };
            panel.Children.Add(del);

            fly.Content = panel;
        };
        fly.Closed += (_, _) => BuildPenStrip(); // refresh tooltips/icons after edits
        return fly;
    }

    private void ApplyPreset(PenPreset p)
    {
        Surface.Pen = p.Pen;
        Surface.PenColor = ColorUtil.Parse(p.Color);
        Surface.PenSize = p.Size;
        Surface.PenSensitivity = p.Sens;
        _activePresetId = p.Id;
        SelectTool("Pen");
        BuildPenStrip();
    }

    private async void AddPreset_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("Name this pen", $"My {Surface.Pen}");
        if (name == null) return;
        var p = new PenPreset
        {
            Name = name,
            Pen = Surface.Pen,
            Color = ColorUtil.ToHex(Surface.PenColor),
            Size = Surface.PenSize,
            Sens = Surface.PenSensitivity
        };
        _library.Pens.Add(p);
        _activePresetId = p.Id;
        BuildPenStrip();
        ScheduleSave();
        ShowStatus($"Saved pen “{name}”. Right-click it to change its type, colour or feel.");
    }

    // =======================================================================
    // Notebook tree
    // =======================================================================
    private NotePage NewPage(string name) => new()
    {
        Name = name,
        Background = _library.DefaultBackground,
        Grid = _library.DefaultGrid,
        GridSpacing = _library.DefaultGridSpacing
    };

    private void BuildTree()
    {
        // Remember which notebooks/sections the user had collapsed so a rebuild
        // (after add / rename / reorder / move) doesn't re-expand everything (#29).
        var collapsed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Scan(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.IsExpanded && n.Content != null) collapsed.Add(n.Content);
                Scan(n.Children);
            }
        }
        Scan(NotebookTree.RootNodes);

        NotebookTree.RootNodes.Clear();
        foreach (var nb in _library.Notebooks)
        {
            var nbNode = new TreeViewNode { Content = nb, IsExpanded = !collapsed.Contains(nb) };
            foreach (var sec in nb.Sections)
            {
                var secNode = new TreeViewNode { Content = sec, IsExpanded = !collapsed.Contains(sec) };
                foreach (var pg in sec.Pages)
                    secNode.Children.Add(new TreeViewNode { Content = pg });
                nbNode.Children.Add(secNode);
            }
            NotebookTree.RootNodes.Add(nbNode);
        }
    }

    private TreeViewNode? FindNode(object content)
    {
        TreeViewNode? Walk(IList<TreeViewNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Content, content)) return n;
                var hit = Walk(n.Children);
                if (hit != null) return hit;
            }
            return null;
        }
        return Walk(NotebookTree.RootNodes);
    }

    private async void NotebookTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        object? item = args.InvokedItem;
        if (item is TreeViewNode node) item = node.Content;
        if (item == null) return;
        _selNode = FindNode(item);

        if (item is NotePage page)
        {
            var (nb, sec) = FindContext(page);
            if (nb != null && sec != null)
            {
                if (!await EnsureUnlockedAsync(nb)) return; // password gate (#23)
                SwitchToPage(nb, sec, page);
            }
        }
    }

    // ---- password locking (#23) ----
    private static string HashPw(string pw)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(pw)));
    }

    private async Task<string?> PromptPasswordAsync(string title)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary && box.Password.Length > 0 ? box.Password : null;
    }

    private async Task<bool> EnsureUnlockedAsync(Notebook nb)
    {
        if (nb.PasswordHash == null || _unlockedNotebooks.Contains(nb.Id)) return true;
        var pw = await PromptPasswordAsync($"“{nb.Name}” is locked");
        if (pw == null) return false;
        if (HashPw(pw) == nb.PasswordHash) { _unlockedNotebooks.Add(nb.Id); return true; }
        ShowStatus("Incorrect password.");
        return false;
    }

    private async void LockToggle_Click(object sender, RoutedEventArgs e)
    {
        var (nb, _, _) = ResolveSelection();
        if (nb == null) { ShowStatus("Select a notebook to lock or unlock."); return; }
        if (nb.PasswordHash == null)
        {
            var pw = await PromptPasswordAsync($"Set a password to lock “{nb.Name}”");
            if (pw == null) return;
            nb.PasswordHash = HashPw(pw);
            _unlockedNotebooks.Add(nb.Id);
            ScheduleSave();
            ShowStatus($"“{nb.Name}” is locked — it'll ask for the password when you open it.");
        }
        else
        {
            var pw = await PromptPasswordAsync($"Enter the password to remove the lock on “{nb.Name}”");
            if (pw == null) return;
            if (HashPw(pw) == nb.PasswordHash)
            {
                nb.PasswordHash = null;
                ScheduleSave();
                ShowStatus($"“{nb.Name}” is unlocked.");
            }
            else ShowStatus("Incorrect password.");
        }
    }

    private (Notebook?, Section?) FindContext(NotePage page)
    {
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                if (sec.Pages.Contains(page))
                    return (nb, sec);
        return (null, null);
    }

    private (Notebook? nb, Section? sec, NotePage? pg) FindPageById(Guid? id)
    {
        if (id == null) return (null, null, null);
        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                foreach (var pg in sec.Pages)
                    if (pg.Id == id) return (nb, sec, pg);
        return (null, null, null);
    }

    // Loads the page the user worked on last (so the startup picker sits over it),
    // skipping locked notebooks; falls back to the first available page.
    private void OpenStartupPage()
    {
        var (nb, sec, pg) = FindPageById(_library.LastPageId);
        if (pg != null && nb!.PasswordHash == null)
        {
            SwitchToPage(nb, sec!, pg);
            return;
        }
        foreach (var n in _library.Notebooks)
        {
            if (n.PasswordHash != null) continue;
            if (n.Sections.Count == 0) n.Sections.Add(new Section { Name = "Section 1" });
            var s = n.Sections[0];
            if (s.Pages.Count == 0) s.Pages.Add(NewPage("Page 1"));
            BuildTree();
            SwitchToPage(n, s, s.Pages[0]);
            return;
        }
        OpenFirstPage();
    }

    private void OpenFirstPage()
    {
        if (_library.Notebooks.Count == 0)
            _library.Notebooks.Add(new Notebook { Name = "My Notebook" });
        var nb = _library.Notebooks[0];
        if (nb.Sections.Count == 0)
            nb.Sections.Add(new Section { Name = "Section 1" });
        var sec = nb.Sections[0];
        if (sec.Pages.Count == 0)
            sec.Pages.Add(NewPage("Page 1"));
        SwitchToPage(nb, sec, sec.Pages[0]);
    }

    private void SwitchToPage(Notebook nb, Section sec, NotePage page)
    {
        if (_curPage != null) SaveNow();
        _curNb = nb;
        _curSec = sec;
        _curPage = page;
        if (_library.LastPageId != page.Id)
        {
            _library.LastPageId = page.Id;   // remembered for "Continue" at startup
            ScheduleSave();
        }

        Surface.LoadPage(page);
        CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {page.Name}";

        _syncingUi = true;
        GridRadios.SelectedIndex = (int)page.Grid;
        SpacingSlider.Value = page.GridSpacing;
        BgPicker.Color = ColorUtil.Parse(page.Background);
        _syncingUi = false;

        ApplyPenRowVisibility();
        UpdateUndoButtons();
        UpdateFormatBarVisibility();
    }

    private async Task<string?> PromptAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, SelectionStart = initial.Length };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text)
            ? box.Text.Trim()
            : null;
    }

    private async Task<bool> ConfirmAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "Are you sure?",
            Content = message,
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void AddNotebook_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New notebook", $"Notebook {_library.Notebooks.Count + 1}");
        if (name == null) return;
        var nb = new Notebook { Name = name };
        var sec = new Section { Name = "Section 1" };
        var pg = NewPage("Page 1");
        sec.Pages.Add(pg);
        nb.Sections.Add(sec);
        _library.Notebooks.Add(nb);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private async void AddSection_Click(object sender, RoutedEventArgs e)
    {
        var (nb, _, _) = ResolveSelection();
        if (nb == null) { ShowStatus("Select a notebook first."); return; }
        var name = await PromptAsync("New section", $"Section {nb.Sections.Count + 1}");
        if (name == null) return;
        var sec = new Section { Name = name };
        var pg = NewPage("Page 1");
        sec.Pages.Add(pg);
        nb.Sections.Add(sec);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private async void AddPage_Click(object sender, RoutedEventArgs e)
    {
        var (nb, sec, _) = ResolveSelection();
        if (nb == null || sec == null) { ShowStatus("Select a section (or a page in it) first."); return; }
        var name = await PromptAsync("New page", $"Page {sec.Pages.Count + 1}");
        if (name == null) return;
        var pg = NewPage(name);
        sec.Pages.Add(pg);
        BuildTree();
        SwitchToPage(nb, sec, pg);
        ScheduleSave();
    }

    private (Notebook? nb, Section? sec, NotePage? pg) ResolveSelection()
    {
        var content = _selNode?.Content;
        switch (content)
        {
            case NotePage pg:
            {
                var (nb, sec) = FindContext(pg);
                return (nb, sec, pg);
            }
            case Section sec:
            {
                var nb = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                return (nb, sec, null);
            }
            case Notebook nb:
                return (nb, null, null);
            default:
                return (_curNb, _curSec, _curPage);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }
        string current = content switch
        {
            Notebook nb => nb.Name,
            Section sec => sec.Name,
            NotePage pg => pg.Name,
            _ => ""
        };
        var name = await PromptAsync("Rename", current);
        if (name == null) return;
        switch (content)
        {
            case Notebook nb: nb.Name = name; break;
            case Section sec: sec.Name = name; break;
            case NotePage pg: pg.Name = name; break;
        }
        BuildTree();
        if (_curNb != null && _curSec != null && _curPage != null)
            CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
        Surface.Refresh();
        ScheduleSave();
    }

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }

        switch (content)
        {
            case Notebook nb:
                if (!await ConfirmAsync($"Delete notebook “{nb.Name}” and everything inside it?")) return;
                _library.Notebooks.Remove(nb);
                break;
            case Section sec:
            {
                if (!await ConfirmAsync($"Delete section “{sec.Name}” and all its pages?")) return;
                var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                owner?.Sections.Remove(sec);
                break;
            }
            case NotePage pg:
            {
                if (!await ConfirmAsync($"Delete page “{pg.Name}”?")) return;
                var (_, sec2) = FindContext(pg);
                sec2?.Pages.Remove(pg);
                break;
            }
        }

        _selNode = null;
        BuildTree();
        if (_curPage == null || FindContext(_curPage).Item1 == null)
            OpenFirstPage();
        ScheduleSave();
    }

    // Order notebooks / sections / pages by creation date. Tag = "scope:dir",
    // e.g. "pg:desc", "sec:asc", "nb:desc". Manual order is via Move up/down.
    private void Order_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;
        var parts = tag.Split(':');
        if (parts.Length != 2) return;
        string scope = parts[0];
        bool asc = parts[1] == "asc";
        var (selNb, selSec, _) = ResolveSelection();

        switch (scope)
        {
            case "nb":
                ReorderBy(_library.Notebooks, asc, n => n.CreatedTicks);
                ShowStatus(asc ? "Notebooks: oldest first." : "Notebooks: newest first.");
                break;
            case "sec":
            {
                var nbs = selNb != null ? new List<Notebook> { selNb } : _library.Notebooks;
                foreach (var nb in nbs) ReorderBy(nb.Sections, asc, s => s.CreatedTicks);
                ShowStatus(asc ? "Sections: oldest first." : "Sections: newest first.");
                break;
            }
            default: // pages
            {
                List<Section> targets = selSec != null ? new() { selSec }
                    : selNb != null ? selNb.Sections.ToList()
                    : _library.Notebooks.SelectMany(n => n.Sections).ToList();
                foreach (var sec in targets) ReorderBy(sec.Pages, asc, p => p.CreatedTicks);
                ShowStatus(asc ? "Pages: oldest first." : "Pages: newest first.");
                break;
            }
        }
        BuildTree();
        ScheduleSave();
    }

    private static void ReorderBy<T>(List<T> list, bool asc, Func<T, long> key)
    {
        var sorted = asc ? list.OrderBy(key).ToList() : list.OrderByDescending(key).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // ---- manual reordering (replaces the old drag-reorder that lost notes) ----
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        var content = _selNode?.Content;
        if (content == null) { ShowStatus("Select an item in the tree first."); return; }
        bool moved = false;
        switch (content)
        {
            case Notebook nb:
                moved = MoveInList(_library.Notebooks, nb, delta);
                break;
            case Section sec:
            {
                var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
                if (owner != null) moved = MoveInList(owner.Sections, sec, delta);
                break;
            }
            case NotePage pg:
            {
                var (_, sec) = FindContext(pg);
                if (sec != null) moved = MoveInList(sec.Pages, pg, delta);
                break;
            }
        }
        if (!moved) return;
        BuildTree();
        var node = FindNode(content);
        if (node != null)
        {
            _selNode = node;
            try { NotebookTree.SelectedNode = node; } catch { }
        }
        ScheduleSave();
    }

    private static bool MoveInList<T>(IList<T> list, T item, int delta)
    {
        int i = list.IndexOf(item);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return false;
        list.RemoveAt(i);
        list.Insert(j, item);
        return true;
    }

    // Move a section to another notebook, or a page to another section, with no
    // data loss (this is the safe replacement for cross-notebook drag).
    private async void MoveTo_Click(object sender, RoutedEventArgs e)
    {
        var content = _selNode?.Content;
        if (content is Section sec)
        {
            var owner = _library.Notebooks.FirstOrDefault(n => n.Sections.Contains(sec));
            var targets = _library.Notebooks.Where(n => !ReferenceEquals(n, owner)).ToList();
            if (targets.Count == 0) { ShowStatus("There's no other notebook to move this section into."); return; }
            int pick = await PickFromAsync("Move section to notebook", targets.Select(n => n.Name).ToList());
            if (pick < 0) return;
            owner?.Sections.Remove(sec);
            targets[pick].Sections.Add(sec);
            BuildTree();
            RefreshCrumb();
            ScheduleSave();
            ShowStatus($"Moved section “{sec.Name}” to “{targets[pick].Name}”.");
        }
        else if (content is NotePage pg)
        {
            var (_, curSec) = FindContext(pg);
            var dests = _library.Notebooks
                .SelectMany(n => n.Sections.Select(s => (Nb: n, Sec: s)))
                .Where(t => !ReferenceEquals(t.Sec, curSec))
                .ToList();
            if (dests.Count == 0) { ShowStatus("There's no other section to move this page into."); return; }
            int pick = await PickFromAsync("Move page to section",
                dests.Select(t => $"{t.Nb.Name} ▸ {t.Sec.Name}").ToList());
            if (pick < 0) return;
            curSec?.Pages.Remove(pg);
            dests[pick].Sec.Pages.Add(pg);
            BuildTree();
            RefreshCrumb();
            ScheduleSave();
            ShowStatus($"Moved page “{pg.Name}” to “{dests[pick].Sec.Name}”.");
        }
        else
        {
            ShowStatus("Select a section or a page to move it elsewhere.");
        }
    }

    private async Task<int> PickFromAsync(string title, List<string> options)
    {
        var combo = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var dlg = new ContentDialog
        {
            Title = title,
            Content = combo,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary ? combo.SelectedIndex : -1;
    }

    private void RefreshCrumb()
    {
        if (_curPage == null) return;
        var (nb, sec) = FindContext(_curPage);
        if (nb != null && sec != null)
        {
            _curNb = nb;
            _curSec = sec;
            CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {_curPage.Name}";
        }
    }

    // =======================================================================
    // Search across all notes, incl. handwriting OCR (#18)
    // =======================================================================
    private sealed class SearchHit
    {
        public Notebook Nb = null!;
        public Section Sec = null!;
        public NotePage Page = null!;
        public string Display = "";
        public override string ToString() => Display;
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; _ = RunSearchAsync(SearchBox.Text); }
    }

    private async Task RunSearchAsync(string q)
    {
        q = (q ?? "").Trim();
        SearchResults.ItemsSource = null;
        if (q.Length == 0) { SearchStatus.Text = ""; return; }
        SearchStatus.Text = "Searching…";
        string ql = q.ToLowerInvariant();
        var hits = new List<SearchHit>();
        bool indexedSomething = false;

        foreach (var nb in _library.Notebooks)
            foreach (var sec in nb.Sections)
                foreach (var pg in sec.Pages)
                {
                    // lazily OCR handwriting that hasn't been indexed yet
                    if (string.IsNullOrEmpty(pg.OcrText) && pg.Strokes.Count > 0)
                    {
                        await IndexHandwritingAsync(pg);
                        indexedSomething = true;
                    }
                    string texts = string.Join(" ", pg.Texts.Select(t => RtfToText(t.Rtf)));
                    string hay = ($"{nb.Name} {sec.Name} {pg.Name} {texts} {pg.OcrText}").ToLowerInvariant();
                    if (hay.Contains(ql))
                        hits.Add(new SearchHit { Nb = nb, Sec = sec, Page = pg, Display = $"{nb.Name} ▸ {sec.Name} ▸ {pg.Name}" });
                }

        SearchResults.ItemsSource = hits;
        SearchStatus.Text = hits.Count == 0 ? "No matches." : $"{hits.Count} match{(hits.Count == 1 ? "" : "es")}.";
        if (indexedSomething) ScheduleSave(); // persist the new OCR index
    }

    private async void SearchResult_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchHit hit) return;
        if (!await EnsureUnlockedAsync(hit.Nb)) return;
        BuildTree();
        SwitchToPage(hit.Nb, hit.Sec, hit.Page);
    }

    // Recognise handwriting on a page with the Windows ink analyzer and cache it.
    private static async Task IndexHandwritingAsync(NotePage page)
    {
        try
        {
            if (page.Strokes.Count == 0) { page.OcrText = " "; return; }
            var analyzer = new Windows.UI.Input.Inking.Analysis.InkAnalyzer();
            var builder = new Windows.UI.Input.Inking.InkStrokeBuilder();
            foreach (var s in page.Strokes)
            {
                if (s.Points.Count < 2) continue;
                var pts = s.Points.Select(p =>
                    new Windows.UI.Input.Inking.InkPoint(new Windows.Foundation.Point(p.X, p.Y), p.Pressure));
                var stroke = builder.CreateStrokeFromInkPoints(pts, System.Numerics.Matrix3x2.Identity);
                analyzer.AddDataForStroke(stroke);
            }
            await analyzer.AnalyzeAsync();
            var words = analyzer.AnalysisRoot.FindNodes(Windows.UI.Input.Inking.Analysis.InkAnalysisNodeKind.InkWord);
            var sb = new System.Text.StringBuilder();
            foreach (var w in words)
                sb.Append(((Windows.UI.Input.Inking.Analysis.InkAnalysisInkWord)w).RecognizedText).Append(' ');
            page.OcrText = sb.Length > 0 ? sb.ToString() : " ";
        }
        catch
        {
            page.OcrText = " "; // mark as attempted so we don't retry endlessly
        }
    }

    private static string RtfToText(string rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return "";
        var sb = new System.Text.StringBuilder();
        int depth = 0;
        for (int i = 0; i < rtf.Length; i++)
        {
            char c = rtf[i];
            if (c == '{' || c == '}') continue;
            if (c == '\\')
            {
                // skip a control word and an optional trailing space
                i++;
                while (i < rtf.Length && (char.IsLetter(rtf[i]))) i++;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    while (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i]))) i++;
                if (i < rtf.Length && rtf[i] != ' ') i--; // step back if not the trailing space
                sb.Append(' ');
                continue;
            }
            if (c is '\r' or '\n') { sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // =======================================================================
    // Notebook gallery (#16)
    // =======================================================================
    private static readonly (string Hex, string Name)[] NotebookColours =
    {
        ("#D97757", "Clay"), ("#6A9BCC", "Sky"), ("#788C5D", "Sage"), ("#D32F2F", "Red"),
        ("#FBC02D", "Amber"), ("#7B1FA2", "Violet"), ("#3A3A38", "Graphite"), ("#2E7D6B", "Teal")
    };

    private void OpenGallery_Click(object sender, RoutedEventArgs e) => ShowGallery(launcher: false);

    // Shows the gallery as an overlay. launcher = the startup picker variant
    // (welcome title + "Continue where I left off").
    private void ShowGallery(bool launcher, Notebook? nb = null)
    {
        _galleryLauncher = launcher;
        _galleryNb = nb;
        BuildGallery();
        GalleryPanel.Visibility = Visibility.Visible;
        try
        {
            var anim = new DoubleAnimation
            {
                From = 0, To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(anim, GalleryPanel);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
        catch { GalleryPanel.Opacity = 1; }
    }

    private void CloseGallery()
    {
        GalleryPanel.Visibility = Visibility.Collapsed;
        _galleryLauncher = false;
        _galleryNb = null;
    }

    private void CloseGallery_Click(object sender, RoutedEventArgs e) => CloseGallery();

    private void GalleryBack_Click(object sender, RoutedEventArgs e)
    {
        _galleryNb = null;
        BuildGallery();
    }

    private void BuildGallery()
    {
        GalleryHost.Children.Clear();

        bool detail = _galleryNb != null;
        GalleryBackBtn.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        GalleryNbBtn.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        GalleryFolderBtn.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        GallerySecBtn.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;

        if (detail)
        {
            GalleryTitle.Text = _galleryNb!.Name;
            GallerySubtitle.Text = "Pick a page to work on — or create a new section or page.";
            GalleryContinueBtn.Visibility = Visibility.Collapsed;
            BuildNotebookDetail(_galleryNb!);
            return;
        }

        GalleryTitle.Text = _galleryLauncher ? "Welcome back" : "Notebooks";
        GallerySubtitle.Text = _galleryLauncher
            ? "Choose a notebook to browse its sections and pages — or continue where you left off."
            : "Click a notebook to browse its sections and pages · right-click for colour, folder, rename, lock.";

        var (lnb, lsec, lpg) = FindPageById(_library.LastPageId);
        if (_galleryLauncher && lpg != null)
        {
            GalleryContinueBtn.Content = $"Continue: {lpg.Name}";
            ToolTipService.SetToolTip(GalleryContinueBtn, $"{lnb!.Name} ▸ {lsec!.Name} ▸ {lpg.Name}");
            GalleryContinueBtn.Visibility = Visibility.Visible;
        }
        else GalleryContinueBtn.Visibility = Visibility.Collapsed;

        // ungrouped notebooks first
        var ungrouped = _library.Notebooks.Where(n => string.IsNullOrEmpty(n.Folder)).ToList();
        if (ungrouped.Count > 0) GalleryHost.Children.Add(BuildCardRow(ungrouped));

        // then each folder (union of declared folders and folders in use)
        var folders = _library.Folders
            .Concat(_library.Notebooks.Select(n => n.Folder ?? "").Where(f => f != ""))
            .Distinct().OrderBy(f => f).ToList();
        foreach (var folder in folders)
        {
            GalleryHost.Children.Add(new TextBlock
            {
                Text = "\U0001F4C1  " + folder,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Poppins"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                FontSize = 15,
                Opacity = 0.85,
                Margin = new Thickness(2, 6, 0, 0)
            });
            var inFolder = _library.Notebooks.Where(n => n.Folder == folder).ToList();
            GalleryHost.Children.Add(inFolder.Count > 0
                ? BuildCardRow(inFolder)
                : new TextBlock { Text = "(empty — move a notebook here)", Opacity = 0.5, FontSize = 12, Margin = new Thickness(4, 0, 0, 0) });
        }
    }

    private GridView BuildCardRow(List<Notebook> notebooks)
    {
        var gv = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
        foreach (var nb in notebooks) gv.Items.Add(MakeNotebookCard(nb));
        return gv;
    }

    private Border MakeNotebookCard(Notebook nb)
    {
        bool dark = _library.Theme == "Dark";
        var cardBg = new SolidColorBrush(dark ? Color.FromArgb(255, 0x22, 0x21, 0x1F) : Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        var inkBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18));
        var col = ColorUtil.Parse(nb.Color);
        var card = new Border
        {
            Width = 188,
            Height = 134,
            Margin = new Thickness(0, 0, 14, 14),
            CornerRadius = new CornerRadius(12),
            Background = cardBg,
            BorderBrush = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1)
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(60) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var strip = new Border { Background = new SolidColorBrush(col), CornerRadius = new CornerRadius(12, 12, 0, 0) };
        if (nb.PasswordHash != null)
            strip.Child = new FontIcon { Glyph = "", FontSize = 16, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 8, 0) };
        Grid.SetRow(strip, 0);

        var name = new TextBlock { Text = nb.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Margin = new Thickness(12, 8, 12, 0), Foreground = inkBrush };
        Grid.SetRow(name, 1);

        int pages = nb.Sections.Sum(s => s.Pages.Count);
        var counts = new TextBlock { Text = $"{nb.Sections.Count} sections · {pages} pages", FontSize = 11, Opacity = 0.65, Margin = new Thickness(12, 2, 12, 10) };
        Grid.SetRow(counts, 2);

        grid.Children.Add(strip);
        grid.Children.Add(name);
        grid.Children.Add(counts);
        card.Child = grid;

        card.Tapped += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            _galleryNb = nb;             // drill into sections & pages
            BuildGallery();
        };

        // hover polish: brand glow + a gentle lift
        var restBrush = card.BorderBrush;
        card.PointerEntered += (_, _) =>
        {
            if (Application.Current.Resources["GlowBrush"] is Brush glow) card.BorderBrush = glow;
            card.BorderThickness = new Thickness(1.5);
            card.Translation = new System.Numerics.Vector3(0, -2, 0);
        };
        card.PointerExited += (_, _) =>
        {
            card.BorderBrush = restBrush;
            card.BorderThickness = new Thickness(1);
            card.Translation = new System.Numerics.Vector3(0, 0, 0);
        };

        card.ContextFlyout = BuildCardFlyout(nb);
        ToolTipService.SetToolTip(card, "Click to browse sections & pages · right-click for more");
        return card;
    }

    private MenuFlyout BuildCardFlyout(Notebook nb)
    {
        var fly = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "Open first page" };
        openItem.Click += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            OpenNotebook(nb);
            CloseGallery();
        };
        fly.Items.Add(openItem);
        fly.Items.Add(new MenuFlyoutSeparator());

        var colourSub = new MenuFlyoutSubItem { Text = "Colour" };
        foreach (var (hex, cname) in NotebookColours)
        {
            var h = hex;
            var it = new MenuFlyoutItem { Text = cname };
            it.Click += (_, _) => { nb.Color = h; ScheduleSave(); BuildGallery(); };
            colourSub.Items.Add(it);
        }
        fly.Items.Add(colourSub);

        var folderSub = new MenuFlyoutSubItem { Text = "Move to folder" };
        var none = new MenuFlyoutItem { Text = "(none)" };
        none.Click += (_, _) => { nb.Folder = null; ScheduleSave(); BuildGallery(); };
        folderSub.Items.Add(none);
        foreach (var f in _library.Folders.OrderBy(x => x))
        {
            var ff = f;
            var it = new MenuFlyoutItem { Text = f };
            it.Click += (_, _) => { nb.Folder = ff; ScheduleSave(); BuildGallery(); };
            folderSub.Items.Add(it);
        }
        var newF = new MenuFlyoutItem { Text = "New folder…" };
        newF.Click += async (_, _) =>
        {
            var f = await PromptAsync("New folder", "Folder");
            if (f == null) return;
            if (!_library.Folders.Contains(f)) _library.Folders.Add(f);
            nb.Folder = f;
            ScheduleSave();
            BuildGallery();
        };
        folderSub.Items.Add(newF);
        fly.Items.Add(folderSub);

        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) =>
        {
            var n = await PromptAsync("Rename notebook", nb.Name);
            if (n == null) return;
            nb.Name = n; ScheduleSave(); BuildGallery(); BuildTree();
        };
        fly.Items.Add(rename);

        var lockItem = new MenuFlyoutItem { Text = nb.PasswordHash == null ? "Lock…" : "Remove lock…" };
        lockItem.Click += (_, _) => { _selNode = FindNode(nb); LockToggle_Click(this, new RoutedEventArgs()); };
        fly.Items.Add(lockItem);

        fly.Items.Add(new MenuFlyoutSeparator());
        var delItem = new MenuFlyoutItem { Text = "Delete notebook…" };
        delItem.Click += async (_, _) =>
        {
            if (!await EnsureUnlockedAsync(nb)) return;
            if (!await ConfirmAsync($"Delete notebook “{nb.Name}” and everything inside it?")) return;
            _library.Notebooks.Remove(nb);
            _selNode = null;
            BuildTree();
            if (_curPage == null || FindContext(_curPage).Item1 == null) OpenFirstPage();
            ScheduleSave();
            BuildGallery();
        };
        fly.Items.Add(delItem);

        return fly;
    }

    // ---- notebook detail: sections with page chips (start-screen drill-down) ----
    private void BuildNotebookDetail(Notebook nb)
    {
        bool dark = _library.Theme == "Dark";
        var inkBrush = new SolidColorBrush(dark ? Color.FromArgb(255, 0xF4, 0xF2, 0xEC) : Color.FromArgb(255, 0x1B, 0x1A, 0x18));
        var cardBg = new SolidColorBrush(dark ? Color.FromArgb(255, 0x1C, 0x1B, 0x20) : Color.FromArgb(255, 0xFF, 0xFF, 0xFF));
        var chipBg = new SolidColorBrush(dark ? Color.FromArgb(255, 0x27, 0x26, 0x2C) : Color.FromArgb(255, 0xF3, 0xF1, 0xEA));
        var hairline = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));
        var accent = new SolidColorBrush(ColorUtil.Parse(nb.Color));

        if (nb.Sections.Count == 0)
            GalleryHost.Children.Add(new TextBlock
            {
                Text = "This notebook is empty — click “New section” above to get started.",
                Opacity = 0.6, FontSize = 13, Margin = new Thickness(4, 8, 0, 0)
            });

        foreach (var sec in nb.Sections)
        {
            var s0 = sec;
            var secCard = new Border
            {
                Background = cardBg,
                BorderBrush = hairline,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16, 12, 16, 12)
            };
            var stack = new StackPanel { Spacing = 8 };

            var header = new Grid();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5),
                Background = accent, VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = sec.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 16,
                Foreground = inkBrush, VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = $"{sec.Pages.Count} page{(sec.Pages.Count == 1 ? "" : "s")}",
                FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(titleRow);

            var addPg = new Button
            {
                Content = "+ New page", FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Padding = new Thickness(10, 4, 10, 4)
            };
            addPg.Click += async (_, _) => await GalleryNewPageAsync(nb, s0);
            header.Children.Add(addPg);
            stack.Children.Add(header);

            var secFly = new MenuFlyout();
            var renSec = new MenuFlyoutItem { Text = "Rename section" };
            renSec.Click += async (_, _) =>
            {
                var n2 = await PromptAsync("Rename section", s0.Name);
                if (n2 == null) return;
                s0.Name = n2;
                ScheduleSave(); BuildTree(); BuildGallery();
                if (_curSec == s0 && _curNb != null && _curPage != null)
                    CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
            };
            secFly.Items.Add(renSec);
            var delSec = new MenuFlyoutItem { Text = "Delete section…" };
            delSec.Click += async (_, _) =>
            {
                if (!await ConfirmAsync($"Delete section “{s0.Name}” and all its pages?")) return;
                nb.Sections.Remove(s0);
                ScheduleSave(); BuildTree();
                if (_curPage != null && FindContext(_curPage).Item1 == null) OpenFirstPage();
                BuildGallery();
            };
            secFly.Items.Add(delSec);
            secCard.ContextFlyout = secFly;
            ToolTipService.SetToolTip(secCard, "Right-click to rename or delete this section");

            if (sec.Pages.Count == 0)
                stack.Children.Add(new TextBlock { Text = "No pages yet — add one above.", FontSize = 12, Opacity = 0.55 });
            else
            {
                var wrap = new GridView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
                foreach (var pg in sec.Pages) wrap.Items.Add(MakePageChip(nb, sec, pg, chipBg, inkBrush));
                stack.Children.Add(wrap);
            }

            secCard.Child = stack;
            GalleryHost.Children.Add(secCard);
        }
    }

    private Button MakePageChip(Notebook nb, Section sec, NotePage pg, Brush bg, Brush ink)
    {
        bool current = ReferenceEquals(pg, _curPage);
        var inner = new StackPanel { Spacing = 2 };
        inner.Children.Add(new TextBlock
        {
            Text = pg.Name, Foreground = ink, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160
        });
        inner.Children.Add(new TextBlock
        {
            Text = new DateTime(pg.CreatedTicks, DateTimeKind.Utc).ToLocalTime().ToString("d MMM yyyy"),
            FontSize = 10, Opacity = 0.55
        });

        var chip = new Button
        {
            Content = inner,
            Background = bg,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(current ? 1.5 : 1),
            BorderBrush = current
                ? (Brush)Application.Current.Resources["BrandOrangeBrush"]
                : new SolidColorBrush(Color.FromArgb(50, 128, 128, 128))
        };
        ToolTipService.SetToolTip(chip, current
            ? "This page is open right now"
            : "Open this page · right-click to rename or delete");

        chip.Click += (_, _) =>
        {
            BuildTree();
            SwitchToPage(nb, sec, pg);
            CloseGallery();
        };

        var fly = new MenuFlyout();
        var ren = new MenuFlyoutItem { Text = "Rename page" };
        ren.Click += async (_, _) =>
        {
            var n2 = await PromptAsync("Rename page", pg.Name);
            if (n2 == null) return;
            pg.Name = n2;
            ScheduleSave(); BuildTree(); BuildGallery();
            if (ReferenceEquals(pg, _curPage))
            {
                CrumbText.Text = $"{nb.Name} ▸ {sec.Name} ▸ {pg.Name}";
                Surface.Refresh();
            }
        };
        fly.Items.Add(ren);
        var del = new MenuFlyoutItem { Text = "Delete page…" };
        del.Click += async (_, _) =>
        {
            if (!await ConfirmAsync($"Delete page “{pg.Name}”?")) return;
            sec.Pages.Remove(pg);
            ScheduleSave(); BuildTree();
            if (ReferenceEquals(pg, _curPage)) OpenFirstPage();
            BuildGallery();
        };
        fly.Items.Add(del);
        chip.ContextFlyout = fly;
        return chip;
    }

    private async Task GalleryNewPageAsync(Notebook nb, Section sec)
    {
        var name = await PromptAsync("New page", $"Page {sec.Pages.Count + 1}");
        if (name == null) return;
        var pg = NewPage(name);
        sec.Pages.Add(pg);
        BuildTree();
        ScheduleSave();
        SwitchToPage(nb, sec, pg);   // creating a page means "work on it now"
        CloseGallery();
    }

    private async void GalleryNewSection_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryNb == null) return;
        var name = await PromptAsync("New section", $"Section {_galleryNb.Sections.Count + 1}");
        if (name == null) return;
        _galleryNb.Sections.Add(new Section { Name = name });
        BuildTree();
        ScheduleSave();
        BuildGallery();   // stay in the picker so a page can be added next
    }

    private async void GalleryContinue_Click(object sender, RoutedEventArgs e)
    {
        var (nb, sec, pg) = FindPageById(_library.LastPageId);
        if (pg == null) { CloseGallery(); return; }
        if (!await EnsureUnlockedAsync(nb!)) return;
        BuildTree();
        SwitchToPage(nb!, sec!, pg);
        CloseGallery();
    }

    private void OpenNotebook(Notebook nb)
    {
        if (nb.Sections.Count == 0) nb.Sections.Add(new Section { Name = "Section 1" });
        var sec = nb.Sections[0];
        if (sec.Pages.Count == 0) sec.Pages.Add(NewPage("Page 1"));
        BuildTree();
        SwitchToPage(nb, sec, sec.Pages[0]);
        ScheduleSave();
    }

    private async void GalleryNewNotebook_Click(object sender, RoutedEventArgs e)
    {
        var name = await PromptAsync("New notebook", $"Notebook {_library.Notebooks.Count + 1}");
        if (name == null) return;
        var nb = new Notebook { Name = name };
        var sec = new Section { Name = "Section 1" };
        sec.Pages.Add(NewPage("Page 1"));
        nb.Sections.Add(sec);
        _library.Notebooks.Add(nb);
        BuildTree();
        _galleryNb = nb;      // jump straight into the new notebook's pages
        BuildGallery();
        ScheduleSave();
    }

    private async void GalleryNewFolder_Click(object sender, RoutedEventArgs e)
    {
        var f = await PromptAsync("New folder", "Folder");
        if (f == null) return;
        if (!_library.Folders.Contains(f)) _library.Folders.Add(f);
        ScheduleSave();
        BuildGallery();
    }

    // =======================================================================
    // Settings page — central storage folder + recover/import notebooks
    // =======================================================================
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10, Width = 480 };

        panel.Children.Add(new TextBlock { Text = "Notebook storage (universal sync)", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15 });
        panel.Children.Add(new TextBlock { Text = "Every version of the app reads and writes notebooks from this one folder, so they always stay in sync.", FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        var folderText = new TextBlock { Text = LibraryStore.Dir, FontSize = 12, Opacity = 0.9, TextWrapping = TextWrapping.Wrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        panel.Children.Add(folderText);
        var changeBtn = new Button { Content = "Change folder…", HorizontalAlignment = HorizontalAlignment.Left };
        changeBtn.Click += async (_, _) =>
        {
            var f = await PickFolderAsync();
            if (f == null) return;
            Surface.FlushTexts();
            LibraryStore.SetDataFolder(f, _library);
            folderText.Text = LibraryStore.Dir;
            ShowStatus("Storage folder updated — notebooks now sync from here.");
        };
        panel.Children.Add(changeBtn);

        // ---- default text font & size ----
        panel.Children.Add(new TextBlock { Text = "Default text font & size", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var defFont = new ComboBox { Width = 200 };
        foreach (var f in Fonts)
            defFont.Items.Add(new ComboBoxItem { Content = new TextBlock { Text = f, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(f) }, Tag = f });
        foreach (ComboBoxItem it in defFont.Items)
            if ((string)it.Tag == _library.DefaultFont) { defFont.SelectedItem = it; break; }
        defFont.SelectionChanged += (_, _) =>
        {
            if (defFont.SelectedItem is ComboBoxItem ci && ci.Tag is string fn)
            { _library.DefaultFont = fn; Surface.PendingFontFamily = fn; ScheduleSave(); }
        };
        var defSize = new ComboBox { Width = 92, IsEditable = true };
        foreach (var sz in FontSizes) defSize.Items.Add(sz);
        defSize.Text = ((int)_library.DefaultFontSize).ToString();
        void ApplyDefaultSize(string? txt)
        {
            if (ParseSize(txt) is float v) { _library.DefaultFontSize = v; Surface.PendingFontSize = v; ScheduleSave(); }
        }
        defSize.SelectionChanged += (_, _) => { if (defSize.SelectedItem is string s2) ApplyDefaultSize(s2); };
        defSize.TextSubmitted += (cb, args) => { ApplyDefaultSize(args.Text); };
        row.Children.Add(new TextBlock { Text = "Font", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        row.Children.Add(defFont);
        row.Children.Add(new TextBlock { Text = "Size", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        row.Children.Add(defSize);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "New text boxes start with this font and size.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        // ---- startup behaviour ----
        panel.Children.Add(new TextBlock { Text = "Startup", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var fsToggle = new ToggleSwitch { Header = "Start in full screen", IsOn = _library.StartFullscreen };
        fsToggle.Toggled += (_, _) => { _library.StartFullscreen = fsToggle.IsOn; ScheduleSave(); };
        panel.Children.Add(fsToggle);
        var pickerToggle = new ToggleSwitch { Header = "Show the notebook picker at startup", IsOn = _library.StartOnGallery };
        pickerToggle.Toggled += (_, _) => { _library.StartOnGallery = pickerToggle.IsOn; ScheduleSave(); };
        panel.Children.Add(pickerToggle);
        panel.Children.Add(new TextBlock { Text = "The picker opens over your last page — press Esc to skip it.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        panel.Children.Add(new TextBlock { Text = "Recover / import notebooks", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 10, 0, 0) });
        var recoverBtn = new Button { Content = "Recover my old notebooks (previous location)", HorizontalAlignment = HorizontalAlignment.Left };
        recoverBtn.Click += (_, _) => ImportFromLegacy();
        panel.Children.Add(recoverBtn);
        var importBtn = new Button { Content = "Import notebooks from a file…", HorizontalAlignment = HorizontalAlignment.Left };
        importBtn.Click += async (_, _) => { var p = await PickJsonFileAsync(); if (p != null) ImportFromFile(p); };
        panel.Children.Add(importBtn);
        panel.Children.Add(new TextBlock { Text = "Importing only adds notebooks you don't already have — it never overwrites or deletes your current notes.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });

        var dlg = new ContentDialog { Title = "Settings", Content = panel, CloseButtonText = "Done", XamlRoot = RootGrid.XamlRoot };
        await dlg.ShowAsync();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private async Task<string?> PickJsonFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private void ImportFromLegacy()
    {
        var legacyPath = System.IO.Path.Combine(LibraryStore.LegacyDir, "library.json");
        var src = LibraryStore.LoadFrom(legacyPath) ?? LibraryStore.LoadFrom(legacyPath + ".bak");
        if (src == null) { ShowStatus("No notebooks found in the previous location."); return; }
        int n = LibraryStore.Merge(_library, src);
        BuildTree();
        SaveNow();
        ShowStatus(n > 0 ? $"Recovered {n} notebook(s)." : "Those notebooks are already here.");
    }

    private void ImportFromFile(string path)
    {
        var src = LibraryStore.LoadFrom(path);
        if (src == null) { ShowStatus("That file didn't contain any notebooks."); return; }
        int n = LibraryStore.Merge(_library, src);
        BuildTree();
        SaveNow();
        ShowStatus(n > 0 ? $"Imported {n} notebook(s)." : "Those notebooks are already here.");
    }

    private void Sidebar_Toggle(object sender, RoutedEventArgs e)
    {
        NotebookPanel.Visibility = BtnSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Sidebar_Close(object sender, RoutedEventArgs e)
    {
        BtnSidebar.IsChecked = false;
        NotebookPanel.Visibility = Visibility.Collapsed;
    }

    // =======================================================================
    // Tools
    // =======================================================================
    private void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton tb && tb.Tag is string tag) SelectTool(tag);
    }

    private void SelectTool(string tag)
    {
        ToolPen.IsChecked = tag == "Pen";
        ToolText.IsChecked = tag == "Text";
        ToolSelect.IsChecked = tag == "Select";
        ToolSpace.IsChecked = tag == "FreeSpace";

        var tool = Enum.Parse<ToolType>(tag);
        // Leaving writing mode reverts the chosen size/font to the saved defaults (#8).
        if (tool != ToolType.Text)
        {
            Surface.PendingFontSize = (float)_library.DefaultFontSize;
            Surface.PendingFontFamily = _library.DefaultFont;
        }
        Surface.SetTool(tool);
        UpdateFormatBarVisibility();
        RefreshEraserChip();

        switch (tool)
        {
            case ToolType.Text:
                ShowStatus("Tap anywhere on the page to add a text box.");
                break;
            case ToolType.Select:
                ShowStatus("Draw a lasso around strokes, then drag inside the box to move them. Del removes them.");
                break;
            case ToolType.FreeSpace:
                ShowStatus("Drag downwards to push everything below apart; drag up to pull together.");
                break;
        }
    }

    private void TouchDraw_Click(object sender, RoutedEventArgs e)
    {
        Surface.HandDrawMode = TouchDrawToggle.IsChecked == true;
        ShowStatus(Surface.HandDrawMode
            ? "Finger and mouse drawing enabled."
            : "Finger and mouse drawing disabled; touch pans the page.");
    }

    private void MouseMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
            SetMouseMode(Enum.Parse<MouseMode>(tag));
    }

    private void SetMouseMode(MouseMode mode)
    {
        Surface.MouseMode = mode;
        MouseAuto.IsChecked = mode == MouseMode.Auto;
        MouseGrab.IsChecked = mode == MouseMode.Grab;
        MouseSelect.IsChecked = mode == MouseMode.Select;
        MouseMove.IsChecked = mode == MouseMode.Move;
        MouseModeGlyph.Text = mode switch
        {
            MouseMode.Grab => "✋",
            MouseMode.Select => "⬚",
            MouseMode.Move => "✥",
            _ => "↖"
        };
        ToolTipService.SetToolTip(MouseModeBtn, "Mouse mode: " + mode);
        ShowStatus(mode switch
        {
            MouseMode.Grab => "Mouse: grab — drag to pan the page.",
            MouseMode.Select => "Mouse: select — drag a box to lasso strokes.",
            MouseMode.Move => "Mouse: move — drag images and shapes; drag a handle to resize.",
            _ => "Mouse: auto — click to select or edit, drag empty space to select."
        });
    }

    private void RulerToggle_Toggled(object sender, RoutedEventArgs e)
    {
        Surface.RulerMode = RulerToggle.IsOn;
        if (RulerToggle.IsOn) SelectTool("Pen");
        Surface.Refresh();
    }

    private void RulerSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RulerDegLabel == null) return;
        Surface.RulerAngle = e.NewValue;
        RulerDegLabel.Text = $"Angle: {(int)e.NewValue}°";
        Surface.Refresh();
    }

    // Keep the Settings flyout slider in sync when the ruler is tilted by gesture.
    private void OnRulerAngleChanged(double deg)
    {
        if (RulerSlider == null) return;
        _syncingUi = true;
        try { RulerSlider.Value = ((deg % 180) + 180) % 180; } catch { }
        _syncingUi = false;
        if (RulerDegLabel != null) RulerDegLabel.Text = $"Angle: {(int)deg}°";
    }

    private void InsertShape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem item || item.Tag is not string tag) return;
        SelectTool("Select");
        switch (tag)
        {
            case "Line": Surface.InsertShape(ShapeKind.Line, false); break;
            case "Arrow": Surface.InsertShape(ShapeKind.Arrow, false); break;
            case "Rect": Surface.InsertShape(ShapeKind.Rect, false); break;
            case "Square": Surface.InsertShape(ShapeKind.Rect, true); break;
            case "Ellipse": Surface.InsertShape(ShapeKind.Ellipse, false); break;
            case "Circle": Surface.InsertShape(ShapeKind.Ellipse, true); break;
            case "Triangle": Surface.InsertShape(ShapeKind.Triangle, false); break;
            case "RightTriangle": Surface.InsertShape(ShapeKind.RightTriangle, false); break;
            case "Diamond": Surface.InsertShape(ShapeKind.Diamond, false); break;
            case "Parallelogram": Surface.InsertShape(ShapeKind.Parallelogram, false); break;
            case "Trapezoid": Surface.InsertShape(ShapeKind.Trapezoid, false); break;
            case "Pentagon": Surface.InsertShape(ShapeKind.Pentagon, false); break;
            case "Hexagon": Surface.InsertShape(ShapeKind.Hexagon, false); break;
            case "Star": Surface.InsertShape(ShapeKind.Star, false); break;
            case "AxesXY": Surface.InsertShape(ShapeKind.AxesXY, false); break;
            case "AxesXYZ": Surface.InsertShape(ShapeKind.AxesXYZ, false); break;
        }
        ShowStatus("Drag the shape to move it; drag a corner handle to resize. Del removes it.");
    }

    // =======================================================================
    // Undo / redo / history / replay
    // =======================================================================
    private void Undo_Click(object sender, RoutedEventArgs e) => Surface.Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Surface.Redo();

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = Surface.UndoManager.CanUndo;
        BtnRedo.IsEnabled = Surface.UndoManager.CanRedo;
    }

    private void HistoryFlyout_Opening(object? sender, object e)
    {
        var items = Surface.UndoManager.History;
        HistoryList.ItemsSource = items.Count > 0 ? items : new[] { "(no edits on this page yet)" };
    }

    private void Replay_Click(object sender, RoutedEventArgs e)
    {
        if (BtnReplay.IsChecked == true)
        {
            if (_curPage == null || _curPage.Strokes.Count == 0)
            {
                BtnReplay.IsChecked = false;
                ShowStatus("Nothing to replay on this page yet.");
                return;
            }
            Surface.StartReplay();
        }
        else
        {
            Surface.StopReplay();
        }
    }

    private void UndoAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        Surface.Undo();
        args.Handled = true;
    }

    private void RedoAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        Surface.Redo();
        args.Handled = true;
    }

    private void DeleteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused)
        {
            args.Handled = false;
            return;
        }
        if (Surface.HasDeletable)
        {
            Surface.DeleteSelection();
            args.Handled = true;
        }
        else
        {
            args.Handled = false;
        }
    }

    // =======================================================================
    // Zoom / view
    // =======================================================================
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Surface.ZoomBy(1f / 1.25f);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Surface.SetViewZoom(1f);
    private void HomeView_Click(object sender, RoutedEventArgs e) => Surface.ResetView();

    private void OnViewChanged()
    {
        var pct = $"{Math.Round(Surface.ViewZoom * 100)}%";
        ZoomText.Text = pct;
        ZoomBtnText.Text = pct;
        if (pct != _lastZoomPct)
        {
            _lastZoomPct = pct;
            ZoomBorder.Visibility = Visibility.Visible;
            _zoomTimer.Stop();
            _zoomTimer.Start();
        }
        ScheduleSave();
    }

    // =======================================================================
    // Page settings
    // =======================================================================
    private void BgPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null || sender is not Button b || b.Tag is not string hex) return;
        _curPage.Background = hex;
        Surface.Refresh();
        ScheduleSave();
    }

    private void BgPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncingUi || _curPage == null) return;
        _curPage.Background = ColorUtil.ToHex(args.NewColor);
        Surface.Refresh();
        ScheduleSave();
    }

    private void SetDefaultBg_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;
        _library.DefaultBackground = _curPage.Background;
        ScheduleSave();
        ShowStatus($"New pages will now start with {_library.DefaultBackground}.");
    }

    private void SetDefaultGrid_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage == null) return;
        _library.DefaultGrid = _curPage.Grid;
        _library.DefaultGridSpacing = _curPage.GridSpacing;
        ScheduleSave();
        ShowStatus("New pages will now start with this grid.");
    }

    private void GridRadios_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi || _curPage == null || GridRadios.SelectedIndex < 0) return;
        _curPage.Grid = (GridType)GridRadios.SelectedIndex;
        Surface.Refresh();
        ScheduleSave();
    }

    private void SpacingSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingUi || _curPage == null) return;
        _curPage.GridSpacing = e.NewValue;
        Surface.Refresh();
        ScheduleSave();
    }

    // =======================================================================
    // Pen panel visibility
    // =======================================================================
    private void ApplyPenRowVisibility()
    {
        bool rowOn = _curPage?.PenRowVisible ?? true;
        if (_uiHidden)
        {
            PenRow.Visibility = _floatPen ? Visibility.Visible : Visibility.Collapsed;
            PenRowShowBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            PenRow.Visibility = rowOn ? Visibility.Visible : Visibility.Collapsed;
            PenRowShowBtn.Visibility = rowOn ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void PenRowCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (_uiHidden)
        {
            _floatPen = false;
        }
        else if (_curPage != null)
        {
            _curPage.PenRowVisible = false;
            ScheduleSave();
        }
        ApplyPenRowVisibility();
    }

    private void PenRowShow_Click(object sender, RoutedEventArgs e)
    {
        if (_curPage != null)
        {
            _curPage.PenRowVisible = true;
            ScheduleSave();
        }
        ApplyPenRowVisibility();
    }

    // =======================================================================
    // Minimal UI / fullscreen
    // =======================================================================
    private void HideUi_Click(object sender, RoutedEventArgs e)
    {
        _uiHidden = true;
        _floatPen = false;
        TopBar.Visibility = Visibility.Collapsed;
        FormatBar.Visibility = Visibility.Collapsed;
        NotebookPanel.Visibility = Visibility.Collapsed;
        CalcPanel.Visibility = Visibility.Collapsed;
        BtnCalc.IsChecked = false;
        MinimalButtons.Visibility = Visibility.Visible;
        ApplyPenRowVisibility();
        // hiding everything also goes full screen — but only take credit for it
        // if we weren't already full screen, so restore won't yank the user out.
        try
        {
            if (AppWindow.Presenter.Kind != AppWindowPresenterKind.FullScreen)
            {
                _hideEnteredFullscreen = true;
                AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            }
            else
            {
                _hideEnteredFullscreen = false;
            }
        }
        catch { }
        UpdateFullscreenIcon();
    }

    private void RestoreUi_Click(object sender, RoutedEventArgs e)
    {
        _uiHidden = false;
        TopBar.Visibility = Visibility.Visible;
        MinimalButtons.Visibility = Visibility.Collapsed;
        NotebookPanel.Visibility = BtnSidebar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateFormatBarVisibility();
        ApplyPenRowVisibility();
        // only leave full screen if hiding the UI is what entered it; if the user
        // was already full screen beforehand, stay full screen.
        try
        {
            if (_hideEnteredFullscreen && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        }
        catch { }
        _hideEnteredFullscreen = false;
        UpdateFullscreenIcon();
    }

    private void SnapMinimalButtons()
    {
        try
        {
            var t = MinimalButtons.TransformToVisual(CanvasArea);
            var tl = t.TransformPoint(new Point(0, 0));
            double cx = tl.X + MinimalButtons.ActualWidth / 2.0;
            double cy = tl.Y + MinimalButtons.ActualHeight / 2.0;
            double w = CanvasArea.ActualWidth, h = CanvasArea.ActualHeight;
            bool left = cx < w / 2, top = cy < h / 2;
            MinimalButtons.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            MinimalButtons.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            MinimalButtons.Margin = new Thickness(left ? 14 : 0, top ? 14 : 0, left ? 0 : 14, top ? 0 : 14);
            MinimalButtons.RenderTransform = null;
        }
        catch { }
    }

    private void FloatPen_Click(object sender, RoutedEventArgs e)
    {
        _floatPen = !_floatPen;
        ApplyPenRowVisibility();
    }

    private void FloatNotebook_Click(object sender, RoutedEventArgs e)
    {
        NotebookPanel.Visibility = NotebookPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var presenter = AppWindow.Presenter;
        if (presenter.Kind == AppWindowPresenterKind.FullScreen)
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
        else
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        UpdateFullscreenIcon();
    }

    private void UpdateFullscreenIcon()
    {
        try
        {
            bool fs = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;
            BtnFullscreenIcon.Glyph = fs ? "\uE73F" : "\uE740";   // BackToWindow / FullScreen
            ToolTipService.SetToolTip(BtnFullscreen, fs ? "Exit full screen (F11)" : "Full screen (F11)");
        }
        catch { }
    }

    private void FullscreenAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Fullscreen_Click(this, new RoutedEventArgs());
        args.Handled = true;
    }

    // Esc: close the start screen / gallery if it's open; otherwise leave full
    // screen (unless a text box has focus — then let the control handle it).
    private void EscAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (GalleryPanel.Visibility == Visibility.Visible)
        {
            if (_galleryNb != null) { _galleryNb = null; BuildGallery(); }   // step back first
            else CloseGallery();
            args.Handled = true;
            return;
        }
        if (Surface.ActiveTextBox == null && AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            UpdateFullscreenIcon();
            args.Handled = true;
            return;
        }
        args.Handled = false;
    }

    private void SearchAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        try
        {
            if (_uiHidden || SearchBtn.Flyout == null) { args.Handled = false; return; }
            SearchBtn.Flyout.ShowAt(SearchBtn);
            SearchBox.Focus(FocusState.Programmatic);
            args.Handled = true;
        }
        catch { args.Handled = false; }
    }

    // =======================================================================
    // Text formatting
    // =======================================================================
    private void UpdateFormatBarVisibility()
    {
        if (_uiHidden) { FormatBar.Visibility = Visibility.Collapsed; return; }
        bool show = Surface.Tool == ToolType.Text || Surface.ActiveTextBox != null;
        FormatBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private ITextSelection? Sel()
    {
        var box = Surface.ActiveTextBox;
        if (box == null)
        {
            ShowStatus("Pick the Text tool and tap a text box first.");
            return null;
        }
        return box.Document.Selection;
    }

    private void FormatBold_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Bold = FormatEffect.Toggle;
    }

    private void FormatItalic_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Italic = FormatEffect.Toggle;
    }

    private void FormatUnderline_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        s.CharacterFormat.Underline = s.CharacterFormat.Underline == UnderlineType.Single
            ? UnderlineType.None
            : UnderlineType.Single;
    }

    private void FormatStrike_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Strikethrough = FormatEffect.Toggle;
    }

    private void FormatSuper_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Superscript = FormatEffect.Toggle;
    }

    private void FormatSub_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.Subscript = FormatEffect.Toggle;
    }

    private void TextColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) s.CharacterFormat.ForegroundColor = args.NewColor;
    }

    private void HighlightPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) s.CharacterFormat.BackgroundColor = args.NewColor;
    }

    private void ClearHighlight_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s != null) s.CharacterFormat.BackgroundColor = Colors.Transparent;
    }

    private void FormatBullets_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        var pf = s.ParagraphFormat;
        pf.ListType = pf.ListType == MarkerType.Bullet ? MarkerType.None : MarkerType.Bullet;
    }

    private void FormatIndent_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        try
        {
            var pf = s.ParagraphFormat;
            pf.SetIndents(pf.FirstLineIndent, pf.LeftIndent + 24, pf.RightIndent);
        }
        catch { }
    }

    private void FormatOutdent_Click(object sender, RoutedEventArgs e)
    {
        var s = Sel();
        if (s == null) return;
        try
        {
            var pf = s.ParagraphFormat;
            pf.SetIndents(pf.FirstLineIndent, Math.Max(0, pf.LeftIndent - 24), pf.RightIndent);
        }
        catch { }
    }

    // Checklist: cycle the current paragraph through none -> ☐ -> ☑ -> none (#22).
    private void FormatChecklist_Click(object sender, RoutedEventArgs e)
    {
        var box = Surface.ActiveTextBox;
        if (box == null) { ShowStatus("Tap a text box first, then add a checklist."); return; }
        try
        {
            var sel = box.Document.Selection;
            var para = box.Document.GetRange(sel.StartPosition, sel.EndPosition);
            para.Expand(TextRangeUnit.Paragraph);
            para.GetText(TextGetOptions.None, out string txt);
            string body = txt.TrimEnd('\r');
            string tail = txt.Substring(body.Length);
            string next;
            if (body.StartsWith("☐ ")) next = "☑ " + body.Substring(2);
            else if (body.StartsWith("☑ ")) next = body.Substring(2);
            else next = "☐ " + body;
            para.SetText(TextSetOptions.None, next + tail);
        }
        catch { ShowStatus("Couldn't update the checklist here."); }
        box.Focus(FocusState.Programmatic);
    }

    // Add a hyperlink to the selected text (#24).
    private async void FormatLink_Click(object sender, RoutedEventArgs e)
    {
        var box = Surface.ActiveTextBox;
        if (box == null) { ShowStatus("Select some text in a text box first."); return; }
        var sel = box.Document.Selection;
        if (sel.StartPosition == sel.EndPosition)
        {
            ShowStatus("Select the text you want to turn into a link first.");
            return;
        }
        var url = await PromptAsync("Add link", "https://");
        if (url == null) return;
        if (!url.Contains("://")) url = "https://" + url;
        try { sel.Link = "\"" + url + "\""; }
        catch { ShowStatus("Couldn't add that link."); return; }
        box.Focus(FocusState.Programmatic);
        ShowStatus("Link added. Ctrl+click it to open.");
    }

    private void RotateText_Click(object sender, RoutedEventArgs e) => Surface.RotateActiveText(15);

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Left);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Center);
    private void AlignRight_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Right);
    private void AlignJustify_Click(object sender, RoutedEventArgs e) => SetAlignment(ParagraphAlignment.Justify);

    private void SetAlignment(ParagraphAlignment alignment)
    {
        var s = Sel();
        if (s != null) s.ParagraphFormat.Alignment = alignment;
    }

    private void FontCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FontCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string font) return;
        Surface.PendingFontFamily = font; // also becomes the default for new boxes this session
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null)
        {
            s.CharacterFormat.Name = font;
            Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
        }
    }

    private static float? ParseSize(string? s) =>
        float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= 400
            ? v : (float?)null;

    private void SizeCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSize) return; // ignore programmatic sync (prevents cross-box bleed)
        if (SizeCombo.SelectedItem is string s && ParseSize(s) is float v) ApplyFontSize(v);
    }

    // Typed-in size (e.g. "37" + Enter) — makes the size fully typeable (#1).
    private void SizeCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (ParseSize(args.Text) is float v)
        {
            ApplyFontSize(v);
            args.Handled = true;
        }
    }

    // Applies a size to the active text box's selection if there is one, and
    // always records it as the session default for new boxes / new typing (#2, #8).
    private void ApplyFontSize(float size)
    {
        Surface.PendingFontSize = size;
        var box = Surface.ActiveTextBox;
        if (box != null)
        {
            try { box.Document.Selection.CharacterFormat.Size = size; } catch { }
            box.Focus(FocusState.Programmatic);
        }
    }

    // Reflect the focused box's actual size in the combo so a later change is
    // intentional and never carries a stale value onto another box (#14).
    private void SyncSizeComboFromSelection(RichEditBox? box)
    {
        if (box == null) return;
        try
        {
            float sz = box.Document.Selection.CharacterFormat.Size;
            if (sz > 0)
            {
                _syncingSize = true;
                SizeCombo.Text = sz % 1 == 0 ? ((int)sz).ToString() : sz.ToString("0.#");
            }
        }
        catch { }
        finally { _syncingSize = false; }
    }

    private void Symbol_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string symbol) return;
        var box = Surface.ActiveTextBox;
        if (box == null)
        {
            ShowStatus("Pick the Text tool, tap a text box, then insert symbols.");
            return;
        }
        box.Document.Selection.TypeText(symbol);
        box.Focus(FocusState.Programmatic);
    }

    // =======================================================================
    // Text styles (brand typography)
    // =======================================================================
    private record StyleDef(string Font, float Size, int Weight, bool Italic, Color? Color);

    // Shared definitions used both to render the dropdown (each name shown in its
    // own style, #26) and to apply the style to selected text.
    private static readonly (string Name, StyleDef Def)[] StyleDefs =
    {
        ("Normal",     new StyleDef("Lora", 12, 400, false, null)),
        ("Heading 1",  new StyleDef("Poppins", 22, 700, false, null)),
        ("Heading 2",  new StyleDef("Poppins", 18, 700, false, null)),
        ("Heading 3",  new StyleDef("Poppins", 16, 600, false, null)),
        ("Heading 4",  new StyleDef("Poppins", 14, 600, true, null)),
        ("Heading 5",  new StyleDef("Poppins", 13, 600, false, null)),
        ("Heading 6",  new StyleDef("Poppins", 12, 600, false, Color.FromArgb(255, 138, 136, 127))),
        ("Page Title", new StyleDef("Poppins", 28, 700, false, null)),
        ("Citation",   new StyleDef("Lora", 10.5f, 400, true, Color.FromArgb(255, 138, 136, 127))),
        ("Quote",      new StyleDef("Lora", 13, 400, true, Color.FromArgb(255, 106, 104, 98))),
        ("Code",       new StyleDef("Consolas", 11.5f, 400, false, null)),
    };

    private void BuildStyleItems()
    {
        StyleCombo.Items.Clear();
        foreach (var (name, d) in StyleDefs)
        {
            float display = Math.Clamp(d.Size, 11f, 19f); // keep the list compact
            var tb = new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily(d.Font),
                FontSize = display,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)d.Weight },
                FontStyle = d.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal
            };
            if (d.Color is Color c) tb.Foreground = new SolidColorBrush(c);
            StyleCombo.Items.Add(new ComboBoxItem { Content = tb, Tag = name, MinHeight = 0 });
        }
    }

    private void BuildFontItems()
    {
        FontCombo.Items.Clear();
        foreach (var f in Fonts)
        {
            FontCombo.Items.Add(new ComboBoxItem
            {
                Content = new TextBlock { Text = f, FontFamily = new FontFamily(f) },
                Tag = f,
                MinHeight = 0
            });
        }
    }

    private void StyleCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (StyleCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string style) return;
        var def = StyleDefs.FirstOrDefault(x => x.Name == style).Def;
        if (def == null) return;
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s == null) return;
        var cf = s.CharacterFormat;
        cf.Name = def.Font;
        cf.Size = def.Size;
        cf.Weight = def.Weight;
        cf.Italic = def.Italic ? FormatEffect.On : FormatEffect.Off;
        if (def.Color is Color c) cf.ForegroundColor = c;
        Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
    }

    // =======================================================================
    // Canvas context menu (right-click / pen barrel-tap / touch long-press)
    // =======================================================================
    private void ShowCanvasContextMenu(Point pos)
    {
        var menu = new MenuFlyout();
        var box = Surface.ActiveTextBox;
        bool textSel = box != null && box.FocusState != FocusState.Unfocused &&
                       HasTextSelection(box);
        var world = Surface.ScreenToWorld(pos);

        var copy = new MenuFlyoutItem { Text = "Copy", Icon = new FontIcon { Glyph = "" } };
        copy.IsEnabled = textSel || Surface.HasCanvasSelection;
        copy.Click += (_, _) => ContextCopy();
        menu.Items.Add(copy);

        var cut = new MenuFlyoutItem { Text = "Cut", Icon = new FontIcon { Glyph = "" } };
        cut.IsEnabled = textSel || Surface.HasCanvasSelection;
        cut.Click += (_, _) => ContextCut();
        menu.Items.Add(cut);

        var paste = new MenuFlyoutItem { Text = "Paste", Icon = new FontIcon { Glyph = "" } };
        paste.Click += (_, _) => ContextPaste(world);
        menu.Items.Add(paste);

        if (Surface.HasCanvasSelection)
        {
            var del = new MenuFlyoutItem { Text = "Delete", Icon = new FontIcon { Glyph = "" } };
            del.Click += (_, _) => Surface.DeleteSelection();
            menu.Items.Add(del);
        }

        // Word-style formatting when text is selected: style, font, size (punto).
        if (textSel)
        {
            menu.Items.Add(new MenuFlyoutSeparator());

            var style = new MenuFlyoutSubItem { Text = "Style" };
            AddClick(style, "Bold", FormatBold_Click);
            AddClick(style, "Italic", FormatItalic_Click);
            AddClick(style, "Underline", FormatUnderline_Click);
            AddClick(style, "Strikethrough", FormatStrike_Click);
            AddClick(style, "Superscript", FormatSuper_Click);
            AddClick(style, "Subscript", FormatSub_Click);
            menu.Items.Add(style);

            var font = new MenuFlyoutSubItem { Text = "Font" };
            foreach (var f in Fonts)
            {
                var name = f;
                var it = new MenuFlyoutItem { Text = f };
                it.Click += (_, _) => SetSelectionFont(name);
                font.Items.Add(it);
            }
            menu.Items.Add(font);

            var size = new MenuFlyoutSubItem { Text = "Size (punto)" };
            foreach (var sz in FontSizes)
            {
                var s = sz;
                var it = new MenuFlyoutItem { Text = sz };
                it.Click += (_, _) => SetSelectionSize(s);
                size.Items.Add(it);
            }
            menu.Items.Add(size);
        }

        menu.ShowAt(Surface, new FlyoutShowOptions { Position = pos });
    }

    private static void AddClick(MenuFlyoutSubItem parent, string text, RoutedEventHandler handler)
    {
        var it = new MenuFlyoutItem { Text = text };
        it.Click += handler;
        parent.Items.Add(it);
    }

    private static bool HasTextSelection(RichEditBox box)
    {
        var sel = box.Document.Selection;
        return sel.StartPosition != sel.EndPosition;
    }

    private void ContextCopy()
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused && HasTextSelection(box))
            box.Document.Selection.Copy();
        else if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            ShowStatus("Copied — right-click where you want to paste.");
        }
    }

    private void ContextCut()
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused && HasTextSelection(box))
        {
            box.Document.Selection.Cut();
        }
        else if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            Surface.DeleteSelection();
            ShowStatus("Cut — right-click where you want to paste.");
        }
    }

    private async void ContextPaste(System.Numerics.Vector2 world)
    {
        var box = Surface.ActiveTextBox;
        if (box != null && box.FocusState != FocusState.Unfocused)
        {
            try { box.Document.Selection.Paste(0); } catch { }
            return;
        }
        bool hasBitmap = false;
        try
        {
            hasBitmap = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
        }
        catch { }
        if (hasBitmap) { await PasteImageAsync(world); return; }
        if (InkSurface.HasCanvasClipboard) { Surface.PasteCanvasAt(world); return; }
        ShowStatus("Nothing to paste.");
    }

    private void SetSelectionFont(string font)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null) { s.CharacterFormat.Name = font; Surface.ActiveTextBox?.Focus(FocusState.Programmatic); }
    }

    private void SetSelectionSize(string sizeStr)
    {
        var s = Surface.ActiveTextBox?.Document.Selection;
        if (s != null && float.TryParse(sizeStr, out float v))
        {
            s.CharacterFormat.Size = v;
            Surface.ActiveTextBox?.Focus(FocusState.Programmatic);
        }
    }

    // =======================================================================
    // Calculator
    // =======================================================================
    private void Calc_Toggle(object sender, RoutedEventArgs e)
    {
        CalcPanel.Visibility = BtnCalc.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (CalcPanel.Visibility == Visibility.Visible) CalcInput.Focus(FocusState.Programmatic);
    }

    private void Calc_Close(object sender, RoutedEventArgs e)
    {
        BtnCalc.IsChecked = false;
        CalcPanel.Visibility = Visibility.Collapsed;
    }

    private void CalcMode_Click(object sender, RoutedEventArgs e) => BuildCalcButtons();

    private string CalcModeName => CalcModeBox?.SelectedItem as string ?? "Scientific";

    private void CalcModeBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        ApplyCalcMode();
        BuildCalcButtons();
    }

    private void ApplyCalcMode()
    {
        string m = CalcModeName;
        bool buttons = m is "Standard" or "Scientific" or "Programmer";
        CalcHistory.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcInput.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcButtons.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        CalcToggles.Visibility = buttons ? Visibility.Visible : Visibility.Collapsed;
        bool sci = m == "Scientific";
        CalcDeg.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        Calc2nd.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        CalcHyp.Visibility = sci ? Visibility.Visible : Visibility.Collapsed;
        ProgHost.Visibility = m == "Programmer" ? Visibility.Visible : Visibility.Collapsed;
        GraphHost.Visibility = m == "Graphing" ? Visibility.Visible : Visibility.Collapsed;
        ConvHost.Visibility = m == "Converter" ? Visibility.Visible : Visibility.Collapsed;
        CalcError.Text = "";
        if (m == "Graphing") GraphCanvas.Invalidate();
    }

    private int ProgBaseNum => (ProgBase?.SelectedItem as string) switch
    {
        "HEX" => 16,
        "OCT" => 8,
        "BIN" => 2,
        _ => 10
    };

    private void BuildCalcButtons()
    {
        if (CalcButtons == null) return;
        CalcButtons.Children.Clear();
        if (CalcModeName == "Programmer")
        {
            BuildProgButtons();
            return;
        }
        if (CalcModeName == "Standard")
        {
            BuildButtonRows(new List<(string, string)[]>
            {
                new[] { ("(", "("), (")", ")"), ("C", "C"), ("⌫", "⌫"), ("÷", "÷") },
                new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("√", "√(") },
                new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), ("x²", "^2") },
                new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("1/x", "1/(") },
                new[] { ("+/−", "±"), ("0", "0"), (".", "."), ("=", "=") }
            }, null);
            return;
        }
        bool inv = Calc2nd?.IsChecked == true;
        bool hyp = CalcHyp?.IsChecked == true;

        string Trig(string baseName)
        {
            var n = baseName;
            if (hyp) n += "h";
            if (inv) n = "a" + n;
            return n;
        }
        string TrigLabel(string baseName)
        {
            var n = baseName + (hyp ? "h" : "");
            return inv ? n + "⁻¹" : n;
        }

        var rows = new List<(string Label, string Token)[]>
        {
            new[] { (TrigLabel("sin"), Trig("sin") + "("), (TrigLabel("cos"), Trig("cos") + "("),
                    (TrigLabel("tan"), Trig("tan") + "("), (TrigLabel("sec"), Trig("sec") + "("),
                    (TrigLabel("csc"), Trig("csc") + "("), (TrigLabel("cot"), Trig("cot") + "(") },
            new[] { ("π", "π"), ("e", "e"), ("x²", "^2"), ("xʸ", "^"), ("10ˣ", "10^"), ("√", "√(") },
            new[] { ("log", "log("), ("ln", "ln("), ("eˣ", "exp("), ("|x|", "abs("), ("n!", "!"), ("mod", " mod ") },
            new[] { ("(", "("), (")", ")"), ("C", "C"), ("⌫", "⌫"), ("÷", "÷") },
            new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("1/x", "1/(") },
            new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), ("ʸ√x", "^(1/") },
            new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("=", "=") },
            new[] { ("+/−", "±"), ("0", "0"), (".", ".") }
        };

        BuildButtonRows(rows, null);
    }

    private void BuildButtonRows(List<(string Label, string Token)[]> rows, Func<string, bool>? enabled)
    {
        foreach (var row in rows)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            foreach (var (label, token) in row)
            {
                var b = new Button
                {
                    Content = label,
                    Tag = token,
                    Width = row.Length >= 6 ? 48 : 58,
                    Height = 32,
                    FontSize = 13,
                    Padding = new Thickness(0)
                };
                if (token == "=") b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                if (enabled != null) b.IsEnabled = enabled(label);
                b.Click += Calc_Click;
                sp.Children.Add(b);
            }
            CalcButtons.Children.Add(sp);
        }
    }

    private void BuildProgButtons()
    {
        int nb = ProgBaseNum;
        bool DigitOk(string d)
        {
            if (d.Length == 1 && d[0] >= 'A' && d[0] <= 'F') return nb == 16;
            if (d.Length == 1 && char.IsDigit(d[0]))
            {
                int v = d[0] - '0';
                return v < nb;
            }
            return true;
        }
        BuildButtonRows(new List<(string, string)[]>
        {
            new[] { ("A", "A"), ("B", "B"), ("C", "C₁"), ("D", "D"), ("E", "E"), ("F", "F") },
            new[] { ("AND", " and "), ("OR", " or "), ("XOR", " xor "), ("NOT", "not("), ("mod", " mod ") },
            new[] { ("(", "("), (")", ")"), ("CE", "C"), ("⌫", "⌫"), ("÷", "÷") },
            new[] { ("7", "7"), ("8", "8"), ("9", "9"), ("×", "×"), ("<<", "<<") },
            new[] { ("4", "4"), ("5", "5"), ("6", "6"), ("−", "−"), (">>", ">>") },
            new[] { ("1", "1"), ("2", "2"), ("3", "3"), ("+", "+"), ("=", "=") },
            new[] { ("+/−", "±"), ("0", "0") }
        }, label => label switch
        {
            "AND" or "OR" or "XOR" or "NOT" or "mod" or "(" or ")" or "CE" or "⌫" or "÷" or "×"
                or "<<" or ">>" or "−" or "+" or "=" or "+/−" => true,
            "C" => true,
            _ => DigitOk(label)
        });
    }

    private static string FormatBase(long v, int b)
    {
        if (b == 10) return v.ToString();
        string raw = Convert.ToString(v, b).ToUpperInvariant();
        return raw;
    }

    private void ProgBase_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        if (_progHasValue) CalcInput.Text = FormatBase(_progValue, ProgBaseNum);
        BuildCalcButtons();
    }

    private void ProgEvaluate()
    {
        var expr = CalcInput.Text.Trim();
        if (expr.Length == 0) return;
        if (ProgCalc.TryEvaluate(expr, ProgBaseNum, out long v, out string error))
        {
            _progValue = v;
            _progHasValue = true;
            ProgHex.Text = "HEX  " + FormatBase(v, 16);
            ProgDec.Text = "DEC  " + v;
            ProgOct.Text = "OCT  " + FormatBase(v, 8);
            ProgBin.Text = "BIN  " + FormatBase(v, 2);
            var items = CalcHistory.ItemsSource as List<string> ?? new List<string>();
            items.Insert(0, $"{expr} = {FormatBase(v, ProgBaseNum)}");
            if (items.Count > 60) items.RemoveAt(items.Count - 1);
            CalcHistory.ItemsSource = null;
            CalcHistory.ItemsSource = items;
            CalcInput.Text = FormatBase(v, ProgBaseNum);
        }
        else
        {
            CalcError.Text = error;
        }
    }

    // ---- graphing ----
    private void GraphInput_Changed(object sender, TextChangedEventArgs e) => GraphCanvas.Invalidate();

    private void GraphZoomIn_Click(object sender, RoutedEventArgs e) => GraphSetRange(0.5);
    private void GraphZoomOut_Click(object sender, RoutedEventArgs e) => GraphSetRange(2.0);

    private void GraphReset_Click(object sender, RoutedEventArgs e)
    {
        _gxMin = -10; _gxMax = 10;
        GraphRangeText.Text = "x: −10 … 10";
        GraphCanvas.Invalidate();
    }

    private void GraphSetRange(double factor)
    {
        double c = (_gxMin + _gxMax) / 2, half = (_gxMax - _gxMin) / 2 * factor;
        half = Math.Clamp(half, 0.5, 1e6);
        _gxMin = c - half;
        _gxMax = c + half;
        GraphRangeText.Text = $"x: {_gxMin:0.##} … {_gxMax:0.##}";
        GraphCanvas.Invalidate();
    }

    private void GraphCanvas_Draw(Microsoft.Graphics.Canvas.UI.Xaml.CanvasControl sender,
        Microsoft.Graphics.Canvas.UI.Xaml.CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float W = (float)sender.ActualWidth, H = (float)sender.ActualHeight;
        if (W < 10 || H < 10) return;

        var axis = Color.FromArgb(140, 128, 128, 128);
        var exprs = (GraphInput.Text ?? "")
            .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).Take(4).ToArray();

        int n = Math.Max(64, (int)W);
        var curves = new List<double?[]>();
        double yMin = double.MaxValue, yMax = double.MinValue;
        foreach (var ex in exprs)
        {
            var ys = new double?[n];
            for (int i = 0; i < n; i++)
            {
                double x = _gxMin + (_gxMax - _gxMin) * i / (n - 1);
                if (CalcEngine.TryEvaluate(ex, false, x, out double y, out _) &&
                    !double.IsNaN(y) && !double.IsInfinity(y) && Math.Abs(y) < 1e9)
                {
                    ys[i] = y;
                    if (y < yMin) yMin = y;
                    if (y > yMax) yMax = y;
                }
            }
            curves.Add(ys);
        }
        if (yMin > yMax) { yMin = -10; yMax = 10; }
        if (Math.Abs(yMax - yMin) < 1e-12) { yMin -= 1; yMax += 1; }
        double pad = (yMax - yMin) * 0.08;
        yMin -= pad; yMax += pad;

        float Sx(double x) => (float)((x - _gxMin) / (_gxMax - _gxMin) * W);
        float Sy(double y) => (float)(H - (y - yMin) / (yMax - yMin) * H);

        if (yMin < 0 && yMax > 0)
            ds.DrawLine(0, Sy(0), W, Sy(0), axis, 1f);
        if (_gxMin < 0 && _gxMax > 0)
            ds.DrawLine(Sx(0), 0, Sx(0), H, axis, 1f);

        var palette = new[]
        {
            Color.FromArgb(255, 217, 119, 87),
            Color.FromArgb(255, 106, 155, 204),
            Color.FromArgb(255, 120, 140, 93),
            Color.FromArgb(255, 176, 174, 165)
        };
        for (int c = 0; c < curves.Count; c++)
        {
            var ys = curves[c];
            var col = palette[c % palette.Length];
            for (int i = 1; i < n; i++)
            {
                if (ys[i - 1] is double y0 && ys[i] is double y1)
                {
                    double x0 = _gxMin + (_gxMax - _gxMin) * (i - 1) / (n - 1);
                    double x1 = _gxMin + (_gxMax - _gxMin) * i / (n - 1);
                    ds.DrawLine(Sx(x0), Sy(y0), Sx(x1), Sy(y1), col, 1.8f);
                }
            }
        }
        ds.DrawText($"y: {yMin:0.##} … {yMax:0.##}  (radians)",
            new System.Numerics.Vector2(6, 4), axis, _graphLabelFormat);
    }

    // ---- converter ----
    private static readonly string[] ConvCategories =
        { "Length", "Mass", "Temperature", "Area", "Volume", "Speed", "Time", "Data", "Energy", "Power", "Pressure", "Angle" };

    private static readonly Dictionary<string, (string Unit, double Factor)[]> ConvData = new()
    {
        ["Length"] = new[] { ("millimetre", 0.001), ("centimetre", 0.01), ("metre", 1.0), ("kilometre", 1000.0), ("inch", 0.0254), ("foot", 0.3048), ("yard", 0.9144), ("mile", 1609.344) },
        ["Mass"] = new[] { ("milligram", 1e-6), ("gram", 0.001), ("kilogram", 1.0), ("tonne", 1000.0), ("ounce", 0.0283495), ("pound", 0.453592), ("stone", 6.35029) },
        ["Area"] = new[] { ("cm²", 1e-4), ("m²", 1.0), ("km²", 1e6), ("in²", 0.00064516), ("ft²", 0.092903), ("acre", 4046.8564), ("hectare", 10000.0) },
        ["Volume"] = new[] { ("millilitre", 0.001), ("litre", 1.0), ("m³", 1000.0), ("teaspoon", 0.00492892), ("tablespoon", 0.0147868), ("cup", 0.24), ("pint (US)", 0.473176), ("gallon (US)", 3.78541) },
        ["Speed"] = new[] { ("m/s", 1.0), ("km/h", 0.2777778), ("mph", 0.44704), ("knot", 0.514444) },
        ["Time"] = new[] { ("millisecond", 0.001), ("second", 1.0), ("minute", 60.0), ("hour", 3600.0), ("day", 86400.0), ("week", 604800.0) },
        ["Data"] = new[] { ("bit", 0.125), ("byte", 1.0), ("kB", 1e3), ("MB", 1e6), ("GB", 1e9), ("TB", 1e12), ("KiB", 1024.0), ("MiB", 1048576.0), ("GiB", 1073741824.0) },
        ["Energy"] = new[] { ("joule", 1.0), ("kilojoule", 1000.0), ("calorie", 4.184), ("kcal", 4184.0), ("watt-hour", 3600.0), ("kWh", 3.6e6) },
        ["Power"] = new[] { ("watt", 1.0), ("kilowatt", 1000.0), ("megawatt", 1e6), ("horsepower", 745.7) },
        ["Pressure"] = new[] { ("pascal", 1.0), ("kPa", 1000.0), ("bar", 1e5), ("atm", 101325.0), ("psi", 6894.757), ("mmHg", 133.322) },
        ["Angle"] = new[] { ("degree", 1.0), ("radian", 57.29577951), ("gradian", 0.9), ("turn", 360.0) }
    };

    private static readonly string[] TempUnits = { "Celsius", "Fahrenheit", "Kelvin" };

    private void FillConvUnits()
    {
        if (ConvCat.SelectedItem is not string cat) return;
        string[] units = cat == "Temperature" ? TempUnits : ConvData[cat].Select(u => u.Unit).ToArray();
        ConvFrom.ItemsSource = units;
        ConvTo.ItemsSource = units;
        ConvFrom.SelectedIndex = 0;
        ConvTo.SelectedIndex = Math.Min(1, units.Length - 1);
    }

    private void ConvCat_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_calcReady) return;
        FillConvUnits();
        DoConvert();
    }

    private void Conv_Changed(object sender, object e)
    {
        if (!_calcReady) return;
        DoConvert();
    }

    private void ConvSwap_Click(object sender, RoutedEventArgs e)
    {
        (ConvFrom.SelectedIndex, ConvTo.SelectedIndex) = (ConvTo.SelectedIndex, ConvFrom.SelectedIndex);
        DoConvert();
    }

    private void DoConvert()
    {
        if (ConvCat.SelectedItem is not string cat ||
            ConvFrom.SelectedItem is not string fu ||
            ConvTo.SelectedItem is not string tu)
        {
            return;
        }
        var raw = ConvInput.Text.Trim();
        if (raw.Length == 0) { ConvResult.Text = ""; return; }
        if (!CalcEngine.TryEvaluate(raw, true, out double v, out _))
        {
            ConvResult.Text = "…";
            return;
        }

        double result;
        if (cat == "Temperature")
        {
            double c = fu switch
            {
                "Fahrenheit" => (v - 32) * 5 / 9,
                "Kelvin" => v - 273.15,
                _ => v
            };
            result = tu switch
            {
                "Fahrenheit" => c * 9 / 5 + 32,
                "Kelvin" => c + 273.15,
                _ => c
            };
        }
        else
        {
            var units = ConvData[cat];
            double f1 = units.First(u => u.Unit == fu).Factor;
            double f2 = units.First(u => u.Unit == tu).Factor;
            result = v * f1 / f2;
        }
        ConvResult.Text = $"= {result:G10} {tu}";
    }

    private async Task RenamePageFromTitleAsync()
    {
        if (_curPage == null) return;
        var name = await PromptAsync("Rename page", _curPage.Name);
        if (name == null) return;
        _curPage.Name = name;
        BuildTree();
        if (_curNb != null && _curSec != null)
            CrumbText.Text = $"{_curNb.Name} ▸ {_curSec.Name} ▸ {_curPage.Name}";
        Surface.Refresh();
        ScheduleSave();
    }

    private async Task EditPageDateAsync()
    {
        if (_curPage == null) return;
        var local = new DateTime(_curPage.CreatedTicks, DateTimeKind.Utc).ToLocalTime();
        var datePick = new DatePicker { Date = new DateTimeOffset(local.Date) };
        var timePick = new TimePicker { Time = local.TimeOfDay, ClockIdentifier = "24HourClock" };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(datePick);
        panel.Children.Add(timePick);
        var dlg = new ContentDialog
        {
            Title = "Page date and time",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var dt = datePick.Date.Date + timePick.Time;
        _curPage.CreatedTicks = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime().Ticks;
        Surface.Refresh();
        ScheduleSave();
    }

    private void Calc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string token) return;
        CalcError.Text = "";
        switch (token)
        {
            case "C":
                CalcInput.Text = "";
                break;
            case "⌫":
                if (CalcInput.Text.Length > 0)
                    CalcInput.Text = CalcInput.Text[..^1];
                break;
            case "=":
                if (CalcModeName == "Programmer") ProgEvaluate();
                else CalcEvaluate();
                break;
            case "C₁": // programmer hex digit C
                CalcInput.Text += "C";
                break;
            case "±":
                CalcInput.Text = CalcInput.Text.StartsWith("-(") && CalcInput.Text.EndsWith(")")
                    ? CalcInput.Text[2..^1]
                    : (CalcInput.Text.Length > 0 ? $"-({CalcInput.Text})" : "-");
                break;
            default:
                CalcInput.Text += token;
                break;
        }
        if (token != "=") CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    private void CalcEvaluate()
    {
        var expr = CalcInput.Text.Trim();
        if (expr.Length == 0) return;
        if (CalcEngine.TryEvaluate(expr, CalcDeg.IsChecked == true, out double result, out string error))
        {
            string res = result.ToString("G12");
            var items = CalcHistory.ItemsSource as List<string> ?? new List<string>();
            items.Insert(0, $"{expr} = {res}");
            if (items.Count > 60) items.RemoveAt(items.Count - 1);
            CalcHistory.ItemsSource = null;
            CalcHistory.ItemsSource = items;
            CalcInput.Text = res;
            CalcInput.SelectionStart = CalcInput.Text.Length;
        }
        else
        {
            CalcError.Text = error;
        }
    }

    private void CalcInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CalcEvaluate();
            e.Handled = true;
        }
    }

    private bool TextBoxFocused =>
        Surface.ActiveTextBox != null && Surface.ActiveTextBox.FocusState != FocusState.Unfocused;

    private void CopyAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextBoxFocused) { args.Handled = false; return; } // text box keeps its own Ctrl+C
        if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            ShowStatus("Copied.");
            args.Handled = true;
        }
        else args.Handled = false;
    }

    private void CutAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (TextBoxFocused) { args.Handled = false; return; }
        if (Surface.HasCanvasSelection)
        {
            Surface.CopySelection();
            Surface.DeleteSelection();
            ShowStatus("Cut.");
            args.Handled = true;
        }
        else args.Handled = false;
    }

    private void PasteAccel_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        bool hasBitmap = false;
        try
        {
            hasBitmap = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent()
                .Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap);
        }
        catch { }

        if (hasBitmap)
        {
            // images always become resizable canvas objects
            args.Handled = true;
            _ = PasteImageAsync();
            return;
        }
        if (TextBoxFocused)
        {
            args.Handled = false; // plain text: let the focused text box paste
            return;
        }
        if (InkSurface.HasCanvasClipboard)
        {
            // paste copied / cut writings or shapes near the centre of the view
            Surface.PasteCanvasAtViewCenter();
            args.Handled = true;
            return;
        }
        args.Handled = false;
    }

    private async Task PasteImageAsync(System.Numerics.Vector2? worldTopLeft = null)
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
            {
                ShowStatus("Clipboard has no image to paste.");
                return;
            }
            var streamRef = await content.GetBitmapAsync();
            using var stream = await streamRef.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var software = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var dir = System.IO.Path.Combine(LibraryStore.Dir, "assets");
            Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"{Guid.NewGuid():N}.png");
            using (var outStream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, outStream.AsRandomAccessStream());
                encoder.SetSoftwareBitmap(software);
                await encoder.FlushAsync();
            }

            if (worldTopLeft is { } tl)
            {
                // pasted via the context menu at a specific point
                Surface.InsertImageAt(path, decoder.PixelWidth, decoder.PixelHeight, tl);
            }
            else
            {
                // Insert first so the image can consume a pending text caret as its
                // position, then switch to Pen so pen and touch behave as normal,
                // but keep the mouse in Auto/Move mode to allow mouse dragging.
                Surface.InsertImage(path, decoder.PixelWidth, decoder.PixelHeight);
                SelectTool("Pen");
                if (Surface.MouseMode == MouseMode.Grab)
                {
                    SetMouseMode(MouseMode.Auto);
                }
            }
            ShowStatus("Image pasted — drag it with the mouse to move, drag a corner to resize.");
        }
        catch
        {
            ShowStatus("Could not paste that image.");
        }
    }

    private void CalcHistory_Click(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not string entry) return;
        int idx = entry.LastIndexOf("= ", StringComparison.Ordinal);
        if (idx >= 0) CalcInput.Text += entry[(idx + 2)..];
        CalcInput.SelectionStart = CalcInput.Text.Length;
    }

    // =======================================================================
    // Export
    // =======================================================================
    private async Task<(byte[] Pixels, int Width, int Height)?> CaptureViewportAsync()
    {
        try
        {
            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(Surface);
            var buffer = await rtb.GetPixelsAsync();
            return (buffer.ToArray(), rtb.PixelWidth, rtb.PixelHeight);
        }
        catch
        {
            ShowStatus("Could not capture the page. Try again.");
            return null;
        }
    }

    // Export the WHOLE page (#13): fit all content into the viewport, render, then
    // restore the user's original view.
    private async Task<(byte[] Pixels, int Width, int Height)?> CapturePageAsync()
    {
        var saved = Surface.GetView();
        try
        {
            // drop text focus so a focused box's grip/handles aren't captured
            if (Surface.ActiveTextBox != null) ExportBtn.Focus(FocusState.Programmatic);
            Surface.FitToContent(28);
            await Task.Delay(110); // let the Win2D canvas + text layer re-render
            return await CaptureViewportAsync();
        }
        finally
        {
            Surface.SetView(saved.Offset, saved.Zoom);
        }
    }

    private async Task<StorageFile?> PickSaveFileAsync(string extension, string typeName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _curPage?.Name ?? "page"
        };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        return await picker.PickSaveFileAsync();
    }

    private async void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        Surface.FlushTexts();
        var capture = await CapturePageAsync();
        if (capture == null) return;
        var file = await PickSaveFileAsync(".png", "PNG image");
        if (file == null) return;

        try
        {
            using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)capture.Value.Width, (uint)capture.Value.Height,
                96, 96, capture.Value.Pixels);
            await encoder.FlushAsync();
            ShowStatus($"Exported {file.Name}");
        }
        catch
        {
            ShowStatus("Could not save the PNG. Check the location and try again.");
        }
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        Surface.FlushTexts();
        var capture = await CapturePageAsync();
        if (capture == null) return;
        var file = await PickSaveFileAsync(".pdf", "PDF document");
        if (file == null) return;

        try
        {
            var pdf = PdfExporter.Create(new[]
            {
                new PdfPageImage(capture.Value.Width, capture.Value.Height, capture.Value.Pixels)
            });
            await FileIO.WriteBytesAsync(file, pdf);
            ShowStatus($"Exported {file.Name}");
        }
        catch
        {
            ShowStatus("Could not save the PDF. Check the location and try again.");
        }
    }
}
