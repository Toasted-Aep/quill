using Quill.Helpers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// A BARE canvas panel — the Layers / Precision / Objects / Comments surfaces
/// (UI-SPEC-V3 K.19, K.20).
///
/// <para><b>Why it exists at all.</b> These three panels used to be
/// <see cref="Flyout"/>s hung off the status-bar buttons, and they never opened.
/// The <c>Opening</c> handler called the bar's own <c>Build()</c> to light the
/// active-menu underline, and <c>Build()</c> clears the bar's rows — which
/// unparents the very button the flyout was being positioned against. WinUI
/// cannot place a popup on an element that is no longer in the tree, so the
/// flyout aborted silently: the button highlighted, nothing appeared, and no
/// exception was raised. The fix is not to patch the handler but to stop using
/// a popup: the measured reference is explicit that these surfaces carry NO
/// chrome (docs/CONCEPTS-UI-REFERENCE.md §1.1 — sampling behind Layers and
/// Precision returns pure canvas), which a FlyoutPresenter can only ever
/// approximate. So they are ordinary children of the canvas layer.</para>
///
/// <para><b>What it looks like.</b> Nothing but ink: no background, no border,
/// no blur, no corner radius, no shadow. A title in the app's ink colour doubles
/// as the drag handle, a small authored cross closes it, and the content sits
/// directly beneath. Concepts' panels are drag-relocatable
/// (docs/CONCEPTS-UI-REFERENCE.md §35) and so is this one; dropping it re-homes
/// it with <see cref="PanelLayout.Pin"/>.</para>
///
/// <para>Placement is never hard-coded here: the pane registers itself with
/// <see cref="PanelLayout"/> under a home corner and the solver decides where it
/// actually lands, which is what makes K.21 apply to these panels for free.</para>
/// </summary>
public sealed class CanvasPane
{
    private readonly Grid _root;
    private readonly Border _slot = new();
    private readonly TextBlock _title = new();
    private readonly Func<FrameworkElement> _build;
    private readonly PanelLayout _layout;
    private readonly Panel _host;

    public string Id { get; }
    public bool IsOpen => _root.Visibility == Visibility.Visible;
    /// <summary>Raised whenever the pane opens or closes, so the bar can light
    /// (or drop) the 40 x 2 DIP underline under its toggle.</summary>
    public Action? StateChanged { get; set; }

    public CanvasPane(Panel host, PanelLayout layout, string id, string title,
                      Func<FrameworkElement> build, PanelLayout.Anchor home, int order, double width = 300)
    {
        _host = host;
        _layout = layout;
        _build = build;
        Id = id;

        _title.Text = title;
        _title.FontSize = 14;
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.VerticalAlignment = VerticalAlignment.Center;

        var close = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(close, "Close " + title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close, "Close " + title);
        close.Click += (_, _) => Hide();

        // The header IS the drag handle - there is no title bar to grab, by
        // design, so the title has to be the thing you take hold of.
        var header = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 0, 0, 4),
            ManipulationMode = ManipulationModes.TranslateX | ManipulationModes.TranslateY,
        };
        header.Children.Add(_title);
        header.Children.Add(close);
        ToolTipService.SetToolTip(header, title + " — drag to move");
        header.ManipulationDelta += (_, e) => Nudge(e.Delta.Translation.X, e.Delta.Translation.Y);
        header.ManipulationCompleted += (_, _) => Drop();

        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(header);
        stack.Children.Add(_slot);

        _root = new Grid
        {
            Width = width,
            // BARE. Every one of these is deliberate and measured: no
            // Background, no BorderBrush, no CornerRadius, no shadow.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Children = { stack },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_root, "Pane" + id);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_root, title + " panel");
        // Between the dial (60) and the status-bar clusters (70): a pane must
        // never swallow the bar button that toggles it.
        Canvas.SetZIndex(_root, 65);
        host.Children.Add(_root);

        _close = close;
        Repaint();
        _layout.Register(id, _root, home, movable: true, order: order);
    }

    private readonly Button _close;

    // =====================================================================
    // Show / hide
    // =====================================================================
    public void Toggle() { if (IsOpen) Hide(); else Show(); }

    public void Show()
    {
        Rebuild();
        _root.Visibility = Visibility.Visible;
        _layout.Invalidate();
        StateChanged?.Invoke();
    }

    public void Hide()
    {
        if (!IsOpen) return;
        _root.Visibility = Visibility.Collapsed;
        _layout.Invalidate();
        StateChanged?.Invoke();
    }

    /// <summary>Rebuilds the content from live state. These surfaces capture
    /// their colours at build time, exactly like the rest of Quill's code-built
    /// chrome, so a theme change rebuilds rather than repaints.</summary>
    public void Rebuild()
    {
        try { _slot.Child = _build(); }
        catch { _slot.Child = ChromeUi.Caption("This panel could not be built."); }
        Repaint();
    }

    /// <summary>Re-inks the chrome-free parts after a theme change.</summary>
    public void Repaint()
    {
        _title.Foreground = new SolidColorBrush(ChromeUi.Ink);
        var mark = Icons.Stroked(Icons.Close, ChromeUi.Ink, 12, 1.6);
        if (mark != null) { mark.Opacity = 0.55; _close.Content = mark; }
    }

    /// <summary>Only rebuilds if it is actually on screen — a closed pane costs
    /// nothing until the next time it opens.</summary>
    public void RefreshIfOpen() { if (IsOpen) Rebuild(); }

    // =====================================================================
    // Dragging
    // =====================================================================
    private double _dragX, _dragY;
    private bool _dragging;

    private void Nudge(double dx, double dy)
    {
        if (!_dragging)
        {
            _dragging = true;
            _dragX = _root.Margin.Left;
            _dragY = _root.Margin.Top;
        }
        _dragX += dx;
        _dragY += dy;
        _root.Margin = new Thickness(_dragX, _dragY, 0, 0);
    }

    private void Drop()
    {
        if (!_dragging) return;
        _dragging = false;
        // The drop point becomes the pane's new home, so the solver defends it
        // instead of yanking the pane back to its corner.
        _layout.Pin(Id, new Point(_dragX, _dragY));
    }
}
