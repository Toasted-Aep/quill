using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Quill.Controls;

/// <summary>
/// THE EDIT-HISTORY PANEL (CONCEPTS-REF §10.1 item 3, §11.5 item 34,
/// §11.20 item 14).
///
/// <para>History was <c>BtnHistory</c> on the top bar, carrying a 240 DIP
/// flyout. §5's top bar carries no tools at all, so it moved into the <b>Quill
/// dropdown</b> and became a <b>floating panel docked to the RIGHT of the
/// screen</b>, in the same Settings / Export / Objects family — the fourth
/// tenant of <see cref="FloatingWindow"/>.</para>
///
/// <para><b>Nothing here is new behaviour.</b> The list is
/// <c>UndoRedoManager.History</c>, exactly what the flyout showed, and the
/// replay toggle drives <c>InkSurface.StartReplay/StopReplay</c> through the
/// host — the same calls the old <c>BtnReplay</c> made. What changed is where it
/// lives and that it now follows the page instead of being a snapshot taken at
/// the moment the flyout opened.</para>
///
/// <para><b>Show BEFORE Refresh, never the other way round.</b>
/// <see cref="FloatingWindow.Show"/> builds a tab only when it has no tree at
/// all (<c>_scroller.Content == null</c>) and <c>RefreshContent</c> rebuilds
/// only while the window is open — so <c>Refresh(); Show();</c> on a window that
/// has been opened once before rebuilds NOTHING and puts a stale tree back on
/// screen. This panel is a live report of a stack that changes with every
/// stroke, so a stale tree is the one thing it must never show. Ordered the way
/// <see cref="BrushesWindow"/> orders it, and correct whether or not the shared
/// fix to that ordering has landed.</para>
/// </summary>
public sealed class HistoryWindow
{
    public sealed class Host
    {
        /// <summary>The undo stack's descriptions, newest last — the same list
        /// the top bar's flyout bound to.</summary>
        public required Func<IReadOnlyList<string>> Entries { get; init; }
        /// <summary>How many strokes the current page holds. Replay has nothing
        /// to play on an empty page and says so rather than starting.</summary>
        public required Func<int> StrokeCount { get; init; }
        public required Func<bool> Replaying { get; init; }
        public required Action<bool> SetReplaying { get; init; }
        public required Action Undo { get; init; }
        public required Action Redo { get; init; }
        public required Func<bool> CanUndo { get; init; }
        public required Func<bool> CanRedo { get; init; }
        public required Action<string> Status { get; init; }
    }

    private readonly Host _h;
    private readonly FloatingWindow _win;

    public static HistoryWindow Attach(Panel host, Host h) => new(host, h);

    private HistoryWindow(Panel host, Host h)
    {
        _h = h;
        _win = FloatingWindow.Attach(host, 330, 460);
        _win.Title = "History";
        // §10.1 item 3 / §11.5 item 34: "docked to the RIGHT of the screen".
        _win.OpenOn = FloatingWindow.Side.Right;
        // §11.6 item 40: this panel's type is uniform and has no reference scale
        // of its own to preserve, so the window scales the finished tree.
        _win.FontPage = "History";
        _win.InfoRequested = () => _h.Status(
            "Every edit on this page, newest first. Replay draws the page back stroke by stroke.");
        _win.SetTabs(new (string, Func<FrameworkElement>)[] { ("History", Build) });
        // The panel is a live report, so it follows the page's ground AND its
        // content. PageTheme.Changed is already answered by the window itself;
        // this is the one the window cannot know about.
        PageTheme.Changed += () => { if (IsOpen) Refresh(); };
    }

    public bool IsOpen => _win.IsOpen;
    public Rect? Bounds => _win.Bounds;
    public void Hide() => _win.Hide();

    /// <summary>Open it, THEN rebuild — see the class remark. Never the reverse.</summary>
    public void Show()
    {
        _win.Show();
        _win.RefreshContent(preserveScroll: false);
    }

    public void Toggle()
    {
        if (IsOpen) Hide(); else Show();
    }

    /// <summary>Re-read the stack. Called on every content change while the panel
    /// is up, so the reader's place is kept — a new stroke is not a navigation.</summary>
    public void Refresh()
    {
        if (_win.IsOpen) _win.RefreshContent(preserveScroll: true);
    }

    // =====================================================================
    // Body
    // =====================================================================
    private FrameworkElement Build()
    {
        var root = new StackPanel { Spacing = 0 };

        // ---- the two commands the list is a record of -----------------
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 6),
        };
        row.Children.Add(Command(Icons.UndoRound, "Undo the last edit (Ctrl+Z)", _h.CanUndo(),
            () => { _h.Undo(); Refresh(); }));
        // Redo is Undo MIRRORED - the same geometry through Icons' own mirror
        // flag, which is how the top bar draws the pair, so the two marks can
        // never drift apart.
        row.Children.Add(Command(Icons.UndoRound, "Redo (Ctrl+Y)", _h.CanRedo(),
            () => { _h.Redo(); Refresh(); }, mirror: true));

        bool replaying = _h.Replaying();
        var replay = Command(replaying ? StopGeometry : PlayGeometry,
            replaying ? "Stop the replay" : "Replay this page stroke by stroke",
            true, () =>
            {
                if (!replaying && _h.StrokeCount() == 0)
                {
                    _h.Status("Nothing to replay on this page yet.");
                    return;
                }
                _h.SetReplaying(!replaying);
                Refresh();
            }, on: replaying);
        replay.HorizontalAlignment = HorizontalAlignment.Right;

        var bar = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        bar.Children.Add(row);
        bar.Children.Add(replay);
        root.Children.Add(bar);

        root.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(ChromeUi.Hairline),
        });

        // ---- the stack ------------------------------------------------
        var entries = _h.Entries();
        if (entries.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "No edits on this page yet",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 18, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ChromeUi.Ink),
            });
            root.Children.Add(ChromeUi.Caption(
                "Draw, type or move something and every step appears here, newest first."));
            return root;
        }

        // Newest first: the undo stack grows at its end, and the thing a reader
        // is looking for is almost always what they just did.
        for (int i = entries.Count - 1; i >= 0; i--)
            root.Children.Add(Entry(entries[i], entries.Count - i, i == entries.Count - 1));

        return root;
    }

    /// <summary>One line of the stack: its depth, then what it was. The newest
    /// entry — the one Ctrl+Z would take back — is marked, because that is the
    /// only row the reader can act on.</summary>
    private static FrameworkElement Entry(string text, int depth, bool newest)
    {
        var grid = new Grid { Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var n = new TextBlock
        {
            Text = depth.ToString(),
            FontSize = 11,
            Opacity = 0.45,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        };
        grid.Children.Add(n);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = newest ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (newest)
        {
            ToolTipService.SetToolTip(grid, "The next Ctrl+Z takes this one back.");
            grid.Background = new SolidColorBrush(ChromeUi.Wash(0x12));
            grid.CornerRadius = new CornerRadius(6);
            grid.Padding = new Thickness(0, 5, 6, 5);
        }
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(grid, text);
        return grid;
    }

    /// Replay: a play triangle with rounded corners, on the 24 grid. A filled
    /// outline, so it survives PathIcon as well as Icons.Filled.
    private const string PlayGeometry =
        "M8.4 5.1 L18.9 11.35 A0.75 0.75 0 0 1 18.9 12.65 L8.4 18.9 " +
        "A0.75 0.75 0 0 1 7.3 18.0 V6.0 A0.75 0.75 0 0 1 8.4 5.1 Z";

    /// Stop: the matching square, same optical weight as the triangle.
    private const string StopGeometry =
        "M7.6 6.6 H16.4 A1 1 0 0 1 17.4 7.6 V16.4 A1 1 0 0 1 16.4 17.4 " +
        "H7.6 A1 1 0 0 1 6.6 16.4 V7.6 A1 1 0 0 1 7.6 6.6 Z";

    /// <summary>A round command button carrying an authored vector mark. Never a
    /// glyph font, never an emoji.</summary>
    private static Button Command(string geometry, string tip, bool enabled, Action click,
                                  bool on = false, bool mirror = false)
    {
        var art = Icons.Filled(geometry, enabled ? ChromeUi.Ink : ChromeUi.Dim, 18, mirror);
        var b = new Button
        {
            Content = art,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            IsEnabled = enabled,
            BorderThickness = new Thickness(on ? 1.4 : 0),
            BorderBrush = new SolidColorBrush(on ? ChromeUi.Accent : Colors.Transparent),
            Background = new SolidColorBrush(on ? ChromeUi.Wash(0x24) : Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }
}
