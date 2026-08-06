using Quill.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// The Concepts-style widget vocabulary shared by the two floating bars
/// (<see cref="ChromeBars"/>) and the export pane (<see cref="ExportWindow"/>):
/// the liquid-glass bar shell, the icon button, the pill chip, the circular
/// format button with its sub-label, and the <c>#78a19c</c> toggle slider the
/// spec names by hex (UI-SPEC-V3 C / J).
///
/// <para>It exists so the two surfaces cannot drift: a chip in the export pane
/// and a chip in the precision panel are the same code, and every mark comes
/// from <see cref="Icons"/> — never a glyph font, never an emoji.</para>
///
/// <para>Colours are captured at BUILD time, exactly like the rest of Quill's
/// code-built surfaces, and every repaint replaces the brush rather than poking
/// a live one (WinUI caches GradientStop mutations).</para>
/// </summary>
internal static class ChromeUi
{
    // =====================================================================
    // PALETTE. Every colour the bars and the export pane use is named here so
    // the measured-reference pass (docs/CONCEPTS-UI-REFERENCE.md) has exactly
    // one place to true them up.
    // =====================================================================

    /// <summary>The toggle-slider "on" colour, named by the spec.</summary>
    public static readonly Color ToggleOn = Color.FromArgb(0xFF, 0x78, 0xA1, 0x9C);
    /// <summary>The primary action colour, named by the spec.</summary>
    public static readonly Color Primary = Color.FromArgb(0xFF, 0x32, 0x82, 0xAA);

    /// <summary>Alpha of a floating bar's plate. The bars float over the PAGE,
    /// not over the app's own surface, and the app's ink colour follows the APP
    /// theme - so a dark-themed Quill showing a light ivory page paints
    /// near-white marks onto near-white paper. The plate therefore has to be
    /// opaque enough to establish its own ground, exactly as the radial dial's
    /// plate does (the dial uses 0xF2 for the same reason). Anything much below
    /// this and the bar's contents stop being legible on a light page.</summary>
    private const byte PlateAlpha = 0xEE;

    /// <summary>The status bar's one rule: 1 x 16 DIP between the gallery icon
    /// and the page name, LEFT CLUSTER ONLY. Sampled #262829 from the reference
    /// capture; lightened on a light page so it does not vanish.</summary>
    public static Color BarDivider => IsDark
        ? Color.FromArgb(0xFF, 0x26, 0x28, 0x29)
        : Color.FromArgb(0x59, 0x26, 0x28, 0x29);

    // =====================================================================
    // Theme reads — the LIVE theme, i.e. whatever RootGrid.RequestedTheme
    // resolved to, never Application.Current.RequestedTheme.
    // =====================================================================
    /// <summary>The element these widgets actually live in. The bars float over
    /// the PAGE, and the page decides the app's light/dark when ThemeSource is
    /// "Page" - so the theme has to come from the live visual tree, exactly as
    /// ToolWheel reads _host.ActualTheme. Reading FloatingWindow.Theme instead
    /// was wrong the moment settings stopped being a FloatingWindow: nothing set
    /// ActiveRoot any more, the lookup fell back to Application.Current (always
    /// Dark), and the bars painted near-white glyphs onto near-white paper.</summary>
    public static FrameworkElement? ThemeSource { get; set; }

    public static bool IsDark
    {
        get
        {
            try
            {
                if (ThemeSource is { } fe && fe.ActualTheme != ElementTheme.Default)
                    return fe.ActualTheme == ElementTheme.Dark;
            }
            catch { }
            return FloatingWindow.Theme == ElementTheme.Dark;
        }
    }

    public static Color Ink => IsDark
        ? Color.FromArgb(0xFF, 0xF4, 0xF2, 0xEC)
        : Color.FromArgb(0xFF, 0x1B, 0x1A, 0x18);

    public static Color Dim => Color.FromArgb(0x8C, Ink.R, Ink.G, Ink.B);

    public static Color Hairline => IsDark
        ? Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF)
        : Color.FromArgb(0x3D, 0x00, 0x00, 0x00);

    public static Color Accent
    {
        get
        {
            try
            {
                if (Application.Current.Resources.TryGetValue("BrandOrangeBrush", out var b) &&
                    b is SolidColorBrush sb) return sb.Color;
            }
            catch { }
            return Color.FromArgb(0xFF, 0xD9, 0x77, 0x57);
        }
    }

    /// <summary>Fetches a theme brush by key, or null. Same lookup the settings
    /// window does, kept here so both surfaces read one implementation.</summary>
    public static Brush? Res(string key, bool themed = true)
    {
        try
        {
            if (themed &&
                Application.Current.Resources.ThemeDictionaries.TryGetValue(FloatingWindow.ThemeDictionaryKey, out var d) &&
                d is ResourceDictionary rd && rd.TryGetValue(key, out var v) && v is Brush tb) return tb;
            if (Application.Current.Resources.TryGetValue(key, out var g) && g is Brush gb) return gb;
        }
        catch { }
        return null;
    }

    // =====================================================================
    // Shells
    // =====================================================================

    /// <summary>One liquid-glass panel of a floating bar. The bars are built
    /// from SEVERAL of these with real gaps between them, because the spec's
    /// "transparent divider" is exactly that: the glass stops, the page shows
    /// through, and the glass starts again.</summary>
    public static Border GlassPanel(UIElement content, double radius = 17)
    {
        // A brush-level assignment, never a mutation of a shared one: WinUI
        // caches GradientStop changes, and CardBrushFloat is a SHARED acrylic
        // that ApplyLiquidness re-tints for the whole app.
        var plate = IsDark
            ? Color.FromArgb(PlateAlpha, 0x1C, 0x1B, 0x1F)
            : Color.FromArgb(PlateAlpha, 0xF7, 0xF5, 0xF0);
        return new Border
        {
            Child = content,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(plate),
            BorderBrush = Res("GlassEdgeBrush", themed: false) ?? new SolidColorBrush(Hairline),
        };
    }

    /// <summary>A horizontal row for a glass panel.</summary>
    public static StackPanel Row(double spacing = 2) =>
        new() { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };

    // =====================================================================
    // Buttons
    // =====================================================================

    /// <summary>A bar button: an authored vector mark in a transparent square.
    /// <paramref name="stroked"/> picks the stroked factory for the marks that
    /// genuinely ARE a line (the tilt protractor, the chevrons).</summary>
    public static Button IconButton(string geometry, string tip, Action click,
                                    bool stroked = false, double size = 19, double box = 34)
    {
        var art = stroked ? Icons.Stroked(geometry, Ink, size, 1.7) : Icons.Filled(geometry, Ink, size);
        var b = new Button
        {
            Content = art,
            Width = box,
            Height = box,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(box / 2),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>An icon button that carries a flyout instead of a click.</summary>
    public static Button IconMenuButton(string geometry, string tip, Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout,
                                        bool stroked = false, double size = 19, double box = 34)
    {
        var b = IconButton(geometry, tip, () => { }, stroked, size, box);
        b.Flyout = flyout;
        return b;
    }

    /// <summary>A pill chip — the Region row, the Scale row, the precision
    /// options. Disabled chips keep their place and carry a tooltip saying
    /// why, rather than vanishing or lying.</summary>
    public static Button Chip(string label, bool selected, Action onTap,
                              bool enabled = true, string? tip = null)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(enabled ? Ink : Dim),
        };
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(selected ? 1.4 : 1),
            IsEnabled = enabled,
            Background = new SolidColorBrush(selected
                ? Color.FromArgb(0x2E, 0x9A, 0x9A, 0x9A)
                : Colors.Transparent),
            BorderBrush = new SolidColorBrush(selected ? Accent : Hairline),
        };
        if (tip != null) ToolTipService.SetToolTip(b, tip);
        ToolTipService.SetToolTip(b, tip ?? label);
        b.Click += (_, _) => onTap();
        return b;
    }

    /// <summary>One circular format button with its sub-label, as the export
    /// pane's Format row draws them. The selected one is BOLD inside a ring.</summary>
    public static FrameworkElement CircleOption(string label, string sub, bool selected, bool enabled,
                                                string tip, Action onTap, double diameter = 46)
    {
        var ring = new Ellipse
        {
            Width = diameter,
            Height = diameter,
            Fill = new SolidColorBrush(enabled
                ? Color.FromArgb(IsDark ? (byte)0x2A : (byte)0x16, 0x9A, 0x9A, 0x9A)
                : Color.FromArgb(0x10, 0x9A, 0x9A, 0x9A)),
            Stroke = new SolidColorBrush(selected ? Accent : Hairline),
            StrokeThickness = selected ? 2.4 : 1,
        };

        var caption = new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(enabled ? Ink : Dim),
        };

        var disc = new Grid { Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Center };
        disc.Children.Add(ring);
        disc.Children.Add(caption);

        var subText = new TextBlock
        {
            Text = sub,
            FontSize = 9.5,
            Opacity = enabled ? 0.66 : 0.34,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(Ink),
        };

        var stack = new StackPanel
        {
            Width = diameter + 18,
            Padding = new Thickness(2, 4, 2, 4),
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = enabled ? 1 : 0.55,
        };
        stack.Children.Add(disc);
        stack.Children.Add(subText);
        ToolTipService.SetToolTip(stack, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack, label + " " + sub);
        if (enabled) stack.Tapped += (_, _) => onTap();
        return stack;
    }

    // =====================================================================
    // Toggle slider — #78a19c when on (UI-SPEC-V3 C)
    // =====================================================================

    /// <summary>A labelled toggle row: caption on the left, slider on the right.
    /// The slider is authored rather than a ToggleSwitch because the spec pins
    /// its colour, and restyling ToggleSwitch's template for one hex is a far
    /// bigger surface than drawing the pill.</summary>
    public static FrameworkElement ToggleRow(string label, bool on, Action<bool> changed,
                                             bool enabled = true, string? tip = null)
    {
        var grid = new Grid { Padding = new Thickness(0, 5, 0, 5), Background = new SolidColorBrush(Colors.Transparent) };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(enabled ? Ink : Dim),
        });

        var slider = Toggle(on, changed, enabled);
        slider.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(slider);
        if (tip != null) ToolTipService.SetToolTip(grid, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, label);
        return grid;
    }

    /// <summary>The bare pill.</summary>
    public static FrameworkElement Toggle(bool on, Action<bool> changed, bool enabled = true)
    {
        const double w = 44, h = 24, knob = 18;
        bool state = on;

        var track = new Border
        {
            Width = w,
            Height = h,
            CornerRadius = new CornerRadius(h / 2),
            BorderThickness = new Thickness(1),
        };
        var dot = new Border
        {
            Width = knob,
            Height = knob,
            CornerRadius = new CornerRadius(knob / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            Background = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(1),
        };
        var slide = new TranslateTransform();
        dot.RenderTransform = slide;

        void Paint()
        {
            // brush-level writes only
            track.Background = new SolidColorBrush(state
                ? (enabled ? ToggleOn : Color.FromArgb(0x66, ToggleOn.R, ToggleOn.G, ToggleOn.B))
                : Color.FromArgb(IsDark ? (byte)0x3A : (byte)0x28, 0x9A, 0x9A, 0x9A));
            track.BorderBrush = new SolidColorBrush(state ? ToggleOn : Hairline);
            slide.X = state ? w - knob - 6 : 0;
        }
        Paint();

        var host = new Grid
        {
            Width = w,
            Height = h,
            Background = new SolidColorBrush(Colors.Transparent),
            Opacity = enabled ? 1 : 0.5,
        };
        host.Children.Add(track);
        host.Children.Add(dot);
        if (enabled)
            host.Tapped += (_, _) =>
            {
                state = !state;
                Paint();
                changed(state);
            };
        return host;
    }

    // =====================================================================
    // Type + rules
    // =====================================================================
    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Ink),
        Margin = new Thickness(0, 10, 0, 0),
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 11.5,
        Opacity = 0.62,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Ink),
        Margin = new Thickness(0, 1, 0, 5),
    };

    public static TextBlock Label(string text, bool strong = false) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = strong ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Ink),
    };

    public static Border Rule() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 8, 0, 4),
        Background = new SolidColorBrush(Hairline),
    };

    public static ScrollViewer HScroll(UIElement content) => new()
    {
        Content = content,
        HorizontalScrollMode = ScrollMode.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
    };

    /// <summary>The blue primary action button the spec pins to #3282aa.</summary>
    public static Button PrimaryButton(string label, Action click)
    {
        var b = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
            },
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Primary),
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A FlyoutPresenter style with NO background, border, shadow or
    /// padding. The measured reference is unambiguous: Concepts' Layers,
    /// Precision and Objects surfaces are bare text and controls sitting
    /// directly on the canvas - only the title bar and Settings carry chrome.
    /// So the panels these bars open must not be wrapped in a card.</summary>
    public static Style BarePresenter()
    {
        var s = new Style { TargetType = typeof(FlyoutPresenter) };
        s.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        s.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FlyoutPresenter.BorderBrushProperty, new SolidColorBrush(Colors.Transparent)));
        s.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(0)));
        s.Setters.Add(new Setter(Control.IsTabStopProperty, false));
        s.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        return s;
    }

    /// <summary>A small vector mark for use inside menus and rows.</summary>
    public static FrameworkElement? Mark(string geometry, double size = 16, bool stroked = false) =>
        stroked ? Icons.Stroked(geometry, Ink, size, 1.7) : Icons.Filled(geometry, Ink, size);

    /// <summary>A menu item that carries an authored mark instead of a glyph.</summary>
    public static MenuFlyoutItem MenuItem(string text, string geometry, Action click, bool enabled = true, string? tip = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        try
        {
            var p = (Path?)Icons.Filled(geometry, Ink, 16);
            if (p != null) item.Icon = new IconSourceElement { IconSource = new PathIconSource { Data = p.Data } };
        }
        catch { }
        if (tip != null) ToolTipService.SetToolTip(item, tip);
        item.Click += (_, _) => click();
        return item;
    }
}
