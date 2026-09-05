using Quill.Helpers;
using Quill.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Quill.Controls;

/// <summary>Which bottom menu. <b>The order is the stack order</b>: a higher
/// value COVERS a lower one, and a page that covers another is the one that
/// carries a back button (17.9).</summary>
public enum BottomPage
{
    /// <summary>17.9's three-part mode bar - Rotate, Scale, Filter. Published by
    /// <see cref="SelectionChrome"/> while something is selected, because it
    /// describes the selection and nothing else.</summary>
    Modes = 0,
    /// <summary>The active tool's own menu. Today that is the mouse tool's
    /// (17.10); a tool with nothing to configure publishes nothing.</summary>
    Tool = 1,
    /// <summary>The colour picker's own bottom menu, reached from Filter or as a
    /// tool in its own right.</summary>
    Picker = 2,
}

/// <summary>
/// THE SCREEN-BOTTOM MENU (CONCEPTS-REF 17.9, 17.10, 17.12).
///
/// <para><b>ONE SURFACE, NOT TWO.</b> The mode bar and the mouse tool's menu are
/// the same strip of screen showing different contents, and that is a decision
/// with consequences rather than an implementation detail. Two surfaces would
/// have to answer, every frame, which of them is allowed to be on screen - and
/// the moment the answer is "both", the user has two bars at the bottom of the
/// page and no way to tell which one the back button belongs to. One surface
/// makes that state unrepresentable, which is the same argument
/// <see cref="SelectionChrome"/> makes for 11.9 being a MODE on the selection
/// bar rather than a fourth floating strip.</para>
///
/// <para><b>HOW A TOOL SELECTION AND A *SELECTION* SELECTION COEXIST: the tool
/// menu COVERS the mode bar; it does not replace it.</b> They are answers to
/// different questions - "what is selected" and "what am I selecting with" - and
/// both can be true at once, so neither may destroy the other. The pages are
/// ranked, the highest-ranked published page draws, and the ones underneath stay
/// published. That single rule produces every behaviour 17.9 and 17.10 name:</para>
///
/// <list type="bullet">
/// <item>Something selected, pen in hand: <c>Modes</c> alone. Rotate, Scale,
/// Filter, no back button - it is the bottom of the stack.</item>
/// <item>Mouse tool, nothing selected: <c>Tool</c> alone. Item picker / Lasso
/// and the rest, no back button - there is nothing underneath to go back
/// to.</item>
/// <item>Mouse tool WITH a selection: <c>Tool</c> over <c>Modes</c>, so the
/// mouse menu carries a back button that returns to the three-part bar. That is
/// 17.10's capture exactly - <c>&lt; | Lasso | Complete | Include | All</c> - and
/// the leading <c>&lt;</c> is the whole reason to notice that the mouse menu is
/// not a root.</item>
/// <item>Filter pressed: <c>Picker</c> over <c>Modes</c>, so the colour picker's
/// menu carries a back button returning to the three-part bar.</item>
/// <item>The colour picker opened as a tool with nothing selected: <c>Picker</c>
/// alone, and <b>no back button</b> - 17.9's last clause, and the detail it says
/// is most likely to be missed.</item>
/// </list>
///
/// <para>So the back button is not a property of the colour picker, and it is
/// not a flag anyone sets. It is <see cref="ShowsBack"/>: <i>is there a page
/// underneath this one right now</i>. There is no way to reach the conditional
/// wrong without reaching the ordinary case wrong at the same time.</para>
///
/// <para><b>IT RESERVES NO PAGE HEIGHT.</b> 17.15 reclaimed 46 DIP by taking the
/// fullscreen strip's reservation out of the scarce axis and putting it in the
/// abundant one. A bar docked along the bottom would spend that back, so this
/// one is an OVERLAY: a child of the root grid spanning every row, aligned to
/// the bottom, consuming no layout height at all. The page is exactly as tall
/// with the menu up as without it.</para>
///
/// <para><b>WHY THE ROOT GRID AND NOT THE CANVAS AREA.</b>
/// <see cref="SelectionChrome"/> lives on the canvas area because it maps world
/// coordinates and must share that origin. This surface maps nothing - it is
/// pinned to the window - and it has to be able to sit ABOVE the colour picker,
/// whose overlay is a child of the root grid at z-index 150. A child of the
/// canvas area cannot: z-index only orders siblings.</para>
///
/// <para><b>The owner keeps its own plate.</b> Publish hands this surface a
/// <see cref="Border"/> the caller built and still owns; the surface decides
/// only whether it is in the tree and where. That is what lets the mode bar stay
/// part of the selection presentation - it is that presentation's row, moved to
/// the bottom of the screen by 17.9, not a new thing that happens to look like
/// it - while the mouse tool's menu, which belongs to no selection, is published
/// by the window on the same terms.</para>
///
/// <para><b>The two hit-testing traps this file is built around</b>, both of
/// which have silently killed overlays in this codebase:
/// <c>IsHitTestVisible = false</c> PROPAGATES to the whole subtree, so it is set
/// on the divider and nowhere else; and a <c>null</c> Background is TRANSPARENT
/// to hit-testing, which is what the layer wants and what the plate and every
/// cell must never have.</para>
/// </summary>
public sealed class BottomMenu
{
    /// <summary>Every number this surface is laid out with, in one block, like
    /// <see cref="SelectionChrome.Metrics"/> and <see cref="ChromeBars.Metrics"/>.</summary>
    public static class Metrics
    {
        /// <summary>17.16: <b>the bottom bar is 30% smaller</b> - 2.0 x 0.70,
        /// applied to the SAME one constant 17.12 set, so every mark, pitch and
        /// target moves together rather than the glyph shrinking inside an
        /// unchanged button.
        ///
        /// <para>It still multiplies the row 17.9 moved down here - a 15 DIP
        /// mark beside a 12.5 DIP word in a 30 DIP cell - so the whole button
        /// resizes and not the mark alone. 17.12's +100% was seen on screen and
        /// was too big; this is that decision revised, not a second knob.</para>
        /// </summary>
        public const double Scale = 1.40;
        public const double MarkSize = 15 * Scale;
        public const double FontSize = 12.5 * Scale;

        /// <summary>17.16 change 1, the half of it that is arithmetic. The 5 the
        /// plate used to hold above and below a cell is not deleted, it MOVES
        /// into the cell: 30 + 5 + 5. So the plate stands exactly as tall as it
        /// would have with the old inset - it is 30% smaller because
        /// <see cref="Scale"/> says so and for no other reason - and the cell now
        /// reaches both of its edges. Fold it the other way and change 1 would
        /// quietly take a further 25% off a bar the user asked to shrink by
        /// exactly 30.</summary>
        public const double CellRise = 5;
        public const double CellHeight = (30 + 2 * CellRise) * Scale;

        /// <summary>Mark to word, and cell to cell - the row's own 6 and 10, on
        /// the one factor.</summary>
        public const double MarkToWord = 6 * Scale, CellGap = 10 * Scale;

        /// <summary>The plate's own inset, off the row's 12/5 - and the vertical
        /// half is now ZERO.
        ///
        /// <para><b>17.16: "the fill runs to the panel's own bounds, top and
        /// bottom."</b> A selected cell takes an accent wash, and while the plate
        /// held 5 above and below it that wash stopped short, leaving a margin of
        /// panel showing all round - a button floating inside a bar. With the
        /// inset gone the cell is exactly as tall as the plate's content box and
        /// the wash reaches both edges, so it reads as a filled SEGMENT of the
        /// bar. The plate's own <see cref="CornerRadius"/> is untouched: it is
        /// the INNER inset being removed, not the bar's shape.</para></summary>
        public static readonly Thickness Padding =
            new(12 * Scale, 0, 12 * Scale, 0);
        /// <summary>Inside a cell, around the mark and its word.</summary>
        public static readonly Thickness CellPadding =
            new(4 * Scale, 4 * Scale, 4 * Scale, 4 * Scale);

        public const double CornerRadius = 10 * Scale;

        /// <summary>The selected cell's corners, and they are SQUARE.
        ///
        /// <para>This was 7 * <see cref="Scale"/> — 9.8 DIP. 17.16 had already
        /// taken the plate's vertical inset to zero so the accent wash reaches
        /// the bar's top and bottom edges, but a rounded cell inside a squared
        /// opening leaves four small wedges of PANEL colour showing at the
        /// corners of the fill. The user was shown that the wedges are the
        /// chip's own rounding rather than a margin left around it — and chose
        /// hard edges anyway. So the fill is a true rectangle and reads as a
        /// segment of the bar rather than as a pill dropped into it.</para>
        ///
        /// <para>The PLATE's <see cref="CornerRadius"/> is untouched. The bar
        /// still has its own rounded shape; it is the cell inside it that
        /// squares off.</para></summary>
        public const double CellCornerRadius = 0;

        /// <summary>Clear of the window's bottom edge. Not doubled - it is a
        /// distance to an edge, not part of a button.</summary>
        public const double BottomInset = 14;

        /// <summary>The divider between the back button and the menu it returns
        /// from, and between a control and the control that qualifies it.</summary>
        public const double DividerHeight = 18 * Scale;

        /// <summary>A mode that is ON takes an accent wash rather than a
        /// different mark. 17.9's Rotate and Scale are on/off, and a control
        /// whose GLYPH changes with its state reads as a different control
        /// rather than as the same one switched.</summary>
        public const byte OnWash = 46;
    }

    /// <summary>The one surface. Static because the mode bar, the mouse tool's
    /// menu and the colour picker's menu are built by three unrelated files, and
    /// the alternative is threading a reference through all three - which is the
    /// same argument <see cref="ToolSurfaceService"/> makes for being a service
    /// rather than a field.</summary>
    public static BottomMenu? Current { get; private set; }

    /// <summary>Raised when the visible page changes - a page published,
    /// retracted or dismissed. Owners rebuild their cells on it, because whether
    /// a page shows a back button depends on what is underneath it and a page
    /// cannot know that by itself.</summary>
    public static event Action? Changed;

    private readonly Grid _host;
    private readonly Grid _layer = new();
    private readonly Dictionary<BottomPage, Border> _plates = new();
    // Pages the user has backed out of. Cleared by Retract, so re-entering a
    // page - re-choosing the tool, making a fresh selection - reopens it.
    private readonly HashSet<BottomPage> _dismissed = new();
    private BottomPage? _showing;

    public static BottomMenu Attach(Grid host) => Current ??= new BottomMenu(host);

    private BottomMenu(Grid host)
    {
        _host = host;
        // A null Background: the layer spans the bottom of the window and must be
        // invisible to hit-testing, or every press near the bottom edge would
        // land on it instead of on the page. Its CHILDREN still hit-test, which
        // is what the plate needs.
        _layer.Background = null;
        _layer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _layer.VerticalAlignment = VerticalAlignment.Bottom;
        _layer.Margin = new Thickness(0, 0, 0, Metrics.BottomInset);
        _layer.Visibility = Visibility.Collapsed;
        Grid.SetRow(_layer, 0);
        Grid.SetRowSpan(_layer, Math.Max(1, host.RowDefinitions.Count));
        // Above the colour picker's overlay, which is a sibling at 150. The
        // picker's own bottom menu has to be reachable while the picker is up -
        // that is what "descends into the colour picker's own bottom menu" means
        // - so this cannot be underneath it.
        Canvas.SetZIndex(_layer, 160);
        host.Children.Add(_layer);

        PageTheme.Changed += Repaint;
    }

    // =====================================================================
    // Publishing
    // =====================================================================

    /// <summary>Put a page on the stack, or replace the plate of one already
    /// there. The caller keeps ownership of <paramref name="plate"/>; this
    /// surface only decides whether it is in the tree.</summary>
    public void Publish(BottomPage id, Border plate)
    {
        if (_plates.TryGetValue(id, out var had) && ReferenceEquals(had, plate))
        {
            Sync();
            return;
        }
        if (had != null) Detach(had);
        _plates[id] = plate;
        Sync();
    }

    /// <summary>Take a page off the stack. Also clears its dismissal, so the
    /// next time it is published it opens rather than staying backed out.</summary>
    public void Retract(BottomPage id)
    {
        if (_plates.TryGetValue(id, out var plate))
        {
            Detach(plate);
            _plates.Remove(id);
        }
        _dismissed.Remove(id);
        Sync();
    }

    /// <summary>Undo a back press without re-publishing - for when the user
    /// re-chooses the tool whose menu they backed out of.</summary>
    public void Reopen(BottomPage id)
    {
        if (_dismissed.Remove(id)) Sync();
    }

    public bool IsPublished(BottomPage id) => _plates.ContainsKey(id);

    /// <summary><b>17.9's conditional back button, and the whole of it.</b> True
    /// when a page is underneath this one - which is what "reached from the mode
    /// bar" means, and what being a tool in its own right does not.</summary>
    public bool ShowsBack(BottomPage id)
    {
        foreach (var (page, _) in _plates)
            if (page < id && !_dismissed.Contains(page)) return true;
        return false;
    }

    /// <summary>The cells a page must put in FRONT of its own: a back button and
    /// a divider, or nothing at all. Called by the page's own build, because the
    /// back button belongs inside the plate - 17.10's capture reads
    /// <c>&lt; | Lasso | ...</c>, one bar, not a button beside a bar.</summary>
    public IEnumerable<UIElement> Lead(BottomPage id)
    {
        if (!ShowsBack(id)) yield break;
        yield return Cell(Icons.ChevronLeft, null, false, () => Back(id),
                          tip: "Back", stroked: true);
        yield return Divider();
    }

    /// <summary>Back out of a page. It stays published - the tool is still the
    /// tool, the picker is still open - but the page underneath draws until
    /// something reopens this one.</summary>
    public void Back(BottomPage id)
    {
        if (_dismissed.Add(id)) Sync();
    }

    private void Detach(Border plate)
    {
        if (_layer.Children.Contains(plate)) _layer.Children.Remove(plate);
    }

    private BottomPage? Top()
    {
        BottomPage? top = null;
        foreach (var (page, _) in _plates)
            if (!_dismissed.Contains(page) && (top == null || page > top.Value)) top = page;
        return top;
    }

    private void Sync()
    {
        var top = Top();
        // Everything that is not the top page comes out of the tree. Collapsing
        // it instead would leave a hit-testable plate under the visible one at
        // the same place, which is the overlay bug this codebase keeps writing.
        foreach (var (page, plate) in _plates)
            if (top == null || page != top.Value) Detach(plate);

        if (top != null && _plates.TryGetValue(top.Value, out var show))
        {
            if (!_layer.Children.Contains(show)) _layer.Children.Add(show);
            show.Visibility = Visibility.Visible;
        }
        _layer.Visibility = top == null ? Visibility.Collapsed : Visibility.Visible;

        bool moved = _showing != top;
        _showing = top;
        // Raised whether or not the top page changed: a page appearing UNDER the
        // visible one changes whether the visible one shows a back button, and
        // that is exactly the case the conditional exists for.
        try { Changed?.Invoke(); } catch { }
        _ = moved;
    }

    private void Repaint()
    {
        // The plates are the owners' - they repaint their own contents on the
        // same PageTheme.Changed - so this only has to re-run the stack, which
        // costs nothing and keeps the two in step if an owner rebuilds late.
        //
        // Run 14: that was true of the CELLS, which rebuild for other reasons
        // and so happened to pick up fresh ink, but not of the Border a plate
        // itself is - Plate() now carries its own PageTheme.Changed subscription
        // for exactly that reason, so this comment's claim no longer depends on
        // an owner remembering anything.
        Sync();
    }

    // =====================================================================
    // The cells (17.16's sizes and its edge-to-edge fill, and 17.9: an icon
    // and only essential words)
    // =====================================================================

    /// <summary>The standard plate. Every page uses it, so a page cannot end up
    /// a different height from the one it covers.
    ///
    /// <para>§27: the plate is <see cref="PageTheme.Panel"/>, so its rule is
    /// <see cref="PageTheme.PanelOutline"/> and not <c>Outline</c>. Outline is
    /// OnSurface at alpha 36 - keyed to the SHELL - and this plate stopped
    /// standing on the shell's ground when Panel became page-derived.</para>
    ///
    /// <para>Run 14: <see cref="PageTheme.Panel"/> used to be read only HERE, at
    /// construction. <c>MainWindow</c> builds all three plates once, at window
    /// setup, while still on the gallery - so a plate built before any page is
    /// open kept the gallery's ground forever. The CELLS inside rebuild for
    /// other reasons (a tool change, a press) and pick up fresh ink each time,
    /// which is what let the plate's own stale Background hide: on a default
    /// install under Theme = Light the resting pill measured 1.50:1 and the
    /// hovered cell 1.17:1, both against a ground the open page had already left
    /// behind. Same split-by-time shape as the §0 trap §27 closed by theme,
    /// reopened here by construction order - so the plate now repaints its own
    /// Background/BorderBrush rather than trusting whoever holds it to do
    /// that.</para></summary>
    public static Border Plate(StackPanel items)
    {
        var b = new Border
        {
            Child = items,
            CornerRadius = new CornerRadius(Metrics.CornerRadius),
            BorderThickness = new Thickness(1),
            Padding = Metrics.Padding,
            // NEVER null: a null Background is transparent to hit-testing and every
            // press on this plate would land on the page behind it.
            Background = new SolidColorBrush(PageTheme.Panel),
            BorderBrush = new SolidColorBrush(PageTheme.PanelOutline),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        // Never unsubscribed - a plate returned from here lives as long as its
        // owner, which today means the app, exactly like the instance
        // subscription a few lines below in the constructor.
        PageTheme.Changed += () =>
        {
            b.Background = new SolidColorBrush(PageTheme.Panel);
            b.BorderBrush = new SolidColorBrush(PageTheme.PanelOutline);
        };
        return b;
    }

    public static StackPanel Items() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = Metrics.CellGap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>One button.
    ///
    /// <para><b>17.9: "bottom menus name intent with an icon and carry only
    /// essential words."</b> So <paramref name="word"/> is the control's own
    /// name or its current value - "Lasso", "Complete", "All" - and never a
    /// sentence about what it does. A null word leaves the mark alone, which is
    /// what the back button wants.</para>
    ///
    /// <para>The explanation is not deleted, it MOVES: it becomes the tooltip,
    /// where it is available to a user who wants it and out of a bar that has
    /// four other controls to fit.</para></summary>
    public static Button Cell(string mark, string? word, bool on, Action click,
                              string? tip = null, bool stroked = false, bool live = true)
    {
        // §27: OnPanel, not OnSurface. A cell is only ever added to Items(),
        // and Items() is only ever handed to Plate() - so this mark stands on
        // PageTheme.Panel without exception, and Panel is derived from the PAGE
        // while OnSurface is picked by the SHELL's luminance. Under the default
        // ThemeSource = "Manual" those two are different colours, and the pair
        // that fails hardest is the one the user reported: a pinned dark shell
        // over white paper put #F2F2F2 on a #CACACA plate, 1.46:1.
        var ink = live ? PageTheme.OnPanel : PageTheme.WithAlpha(PageTheme.OnPanel, 70);
        var art = Icons.Mark(mark, ink, Metrics.MarkSize, stroked: stroked, thickness: 2);
        art.VerticalAlignment = VerticalAlignment.Center;

        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = word == null ? 0 : Metrics.MarkToWord,
            Padding = Metrics.CellPadding,
            VerticalAlignment = VerticalAlignment.Center,
            // Same reason as the plate's own: this IS the target.
            Background = new SolidColorBrush(Colors.Transparent),
        };
        stack.Children.Add(art);
        if (word != null)
            stack.Children.Add(new TextBlock
            {
                Text = word,
                FontSize = Metrics.FontSize,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ink),
            });

        var b = new Button
        {
            Content = stack,
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = Metrics.CellHeight,
            CornerRadius = new CornerRadius(Metrics.CellCornerRadius),
            BorderThickness = new Thickness(0),
            IsEnabled = live,
            // A mode that is ON takes an accent wash. The MARK does not change -
            // a control whose glyph changes with its state reads as a different
            // control rather than as the same one switched.
            Background = new SolidColorBrush(
                on ? PageTheme.WithAlpha(PageTheme.Accent, Metrics.OnWash) : Colors.Transparent),
        };
        ToolTipService.SetToolTip(b, tip ?? word ?? "");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip ?? word ?? "");
        // The same focus trap 11.9 hit on the quick actions: pressing a cell must
        // not steal focus from whatever the menu is acting on.
        b.AllowFocusOnInteraction = false;
        if (live) b.Click += (_, _) => click();
        return b;
    }

    /// <summary>A two-state control drawn as ONE cell showing its CURRENT value,
    /// which is how 17.10's capture reads - <c>Lasso</c>, not <c>Item picker |
    /// Lasso</c>. Pressing it switches.</summary>
    public static Button Toggle(bool second, string markA, string wordA, string markB, string wordB,
                                Action<bool> set, string? tip = null,
                                bool strokedA = false, bool strokedB = false) =>
        Cell(second ? markB : markA, second ? wordB : wordA, false, () => set(!second),
             tip: tip, stroked: second ? strokedB : strokedA);

    public static FrameworkElement Divider() => new Rectangle
    {
        Width = 1,
        Height = Metrics.DividerHeight,
        VerticalAlignment = VerticalAlignment.Center,
        // §27: on the plate, so the plate's hairline. Same reason as Cell's ink.
        Fill = new SolidColorBrush(PageTheme.PanelOutline),
        // Decoration. This is the ONE place in this file the flag belongs - it
        // propagates to the whole subtree, and a divider has no subtree and no
        // business intercepting a press meant for the cell beside it.
        IsHitTestVisible = false,
    };
}
