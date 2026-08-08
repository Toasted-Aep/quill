using Quill.Helpers;
using Quill.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Quill.Controls;

/// <summary>
/// THE OBJECTS LIBRARY (UI-SPEC-V3 L) — the third tenant of
/// <see cref="FloatingWindow"/>, after Settings (since retired to a dock) and
/// the export pane, and the one the user asked to keep the old resizable
/// "iPad-like" window: drag bar top-centre, close upper-left, info upper-right,
/// eight resize grips. It opens on the LEFT, which is where Concepts puts it.
///
/// <para><b>Layout</b>, top to bottom: the window's own tab row —
/// <i>Favorites · My Packs · Object Market · Pexels</i> — then a search field,
/// then one titled row per CATEGORY carrying a favourite star and an
/// installed/shown check on the right, and beneath each title the objects
/// themselves in a horizontally scrolling strip on a subtly tinted band.</para>
///
/// <para><b>Nothing here is fake.</b> Quill has no object-pack format, no store
/// and no network client, so:</para>
/// <list type="bullet">
/// <item><b>Basic Shapes</b> and <b>Axes &amp; Charts</b> are REAL. Their
/// objects are the shape kinds Quill's own shape engine already draws, the
/// preview IS the shape (generated from the same definition, not a picture of
/// it), and tapping one inserts it on the page in the selected state exactly as
/// the shape menu does.</item>
/// <item><b>Object Market</b> and <b>Pexels</b> are built, and say plainly that
/// there is nothing behind them. They do not show placeholder packs, they do not
/// pretend to load, and they make no network call.</item>
/// <item><b>My Packs</b> lists the built-ins and explains that user-made packs
/// need the object model Quill has not got yet.</item>
/// </list>
/// </summary>
public sealed class ObjectsWindow
{
    public sealed class Host
    {
        public required Func<Library> Library { get; init; }
        public required Action Save { get; init; }
        /// <summary>Insert a shape on the page, exactly as the shape menu does —
        /// same call, so the two can never place different geometry.</summary>
        public required Action<ShapeKind, bool> InsertShape { get; init; }
        public required Action<string> Status { get; init; }
    }

    // =====================================================================
    // The packs. Code, never data: an object is a shape kind plus a name.
    // =====================================================================
    private sealed record Obj(string Name, ShapeKind Kind, bool Regular);

    private sealed record Pack(string Id, string Name, string Desc, Obj[] Items, bool Builtin = true);

    private static readonly Pack[] Packs =
    {
        new("basic", "Basic Shapes",
            "The shapes Quill draws natively — tap one to place it, then drag a handle to size it.",
            new[]
            {
                new Obj("Line", ShapeKind.Line, false),
                new Obj("Arrow", ShapeKind.Arrow, false),
                new Obj("Rectangle", ShapeKind.Rect, false),
                new Obj("Square", ShapeKind.Rect, true),
                new Obj("Ellipse", ShapeKind.Ellipse, false),
                new Obj("Circle", ShapeKind.Ellipse, true),
                new Obj("Triangle", ShapeKind.Triangle, false),
                new Obj("Right triangle", ShapeKind.RightTriangle, false),
                new Obj("Diamond", ShapeKind.Diamond, false),
                new Obj("Parallelogram", ShapeKind.Parallelogram, false),
                new Obj("Trapezoid", ShapeKind.Trapezoid, false),
                new Obj("Pentagon", ShapeKind.Pentagon, false),
                new Obj("Hexagon", ShapeKind.Hexagon, false),
                new Obj("Star", ShapeKind.Star, false),
            }),
        new("axes", "Axes & Charts",
            "Ready-made reference frames for a sketch or a derivation.",
            new[]
            {
                new Obj("x-y plane", ShapeKind.AxesXY, false),
                new Obj("x-y-z axes", ShapeKind.AxesXYZ, false),
            }),
    };

    private const double Tile = 62;
    private const double Band = 86;

    private readonly Host _h;
    private readonly FloatingWindow _win;
    private string _query = "";

    public static ObjectsWindow Attach(Panel host, Host h) => new(host, h);

    private ObjectsWindow(Panel host, Host h)
    {
        _h = h;
        _win = FloatingWindow.Attach(host, 430, 620);
        _win.Title = "Objects";
        _win.OpenOn = FloatingWindow.Side.Left;
        _win.InfoRequested = () => _h.Status(
            "Tap an object to drop it on the page. Star a pack to float it to the top; the check hides or shows it.");
        _win.SetTabs(new (string, Func<FrameworkElement>)[]
        {
            ("Favorites", () => BuildLibrary(favouritesOnly: true)),
            ("My Packs", () => BuildLibrary(favouritesOnly: false)),
            ("Object Market", BuildMarket),
            ("Pexels", BuildPexels),
        });
    }

    public bool IsOpen => _win.IsOpen;
    /// <summary>Its rectangle in canvas coordinates, for the panel solver.</summary>
    public Rect? Bounds => _win.Bounds;
    public void Toggle() => _win.Toggle();
    public void Show() => _win.Show();
    public void Hide() => _win.Hide();
    public void Refresh() { if (_win.IsOpen) _win.RefreshContent(); }

    // =====================================================================
    // The library view
    // =====================================================================
    private FrameworkElement BuildLibrary(bool favouritesOnly)
    {
        var lib = _h.Library();
        var root = new StackPanel { Spacing = 0 };

        root.Children.Add(SearchField());

        var shown = Packs
            .Where(p => !favouritesOnly || lib.FavoriteObjectPacks.Contains(p.Id))
            .OrderByDescending(p => lib.FavoriteObjectPacks.Contains(p.Id))
            .ToList();

        if (shown.Count == 0)
        {
            root.Children.Add(Empty(
                "No favourite packs yet",
                "Tap the star beside a pack in My Packs and it will appear here, at the top of the library."));
            return root;
        }

        bool anyHit = false;
        foreach (var pack in shown)
        {
            var items = Match(pack);
            if (items.Count == 0 && _query.Length > 0) continue;
            anyHit = true;
            root.Children.Add(PackRow(pack, items));
        }

        if (!anyHit)
            root.Children.Add(Empty("Nothing matched",
                $"No pack or object matches “{_query}”."));

        if (!favouritesOnly)
            root.Children.Add(ChromeUi.Caption(
                "These are Quill's built-in packs. Making your own packs — grouping strokes you have drawn and " +
                "saving them as reusable objects — needs the object model Quill does not have yet, so there is no " +
                "New Pack button here rather than one that cannot finish."));

        return root;
    }

    private List<Obj> Match(Pack p)
    {
        if (_query.Length == 0) return p.Items.ToList();
        if (p.Name.Contains(_query, StringComparison.OrdinalIgnoreCase)) return p.Items.ToList();
        return p.Items
            .Where(o => o.Name.Contains(_query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private FrameworkElement SearchField()
    {
        var box = new TextBox
        {
            PlaceholderText = "Search packs and objects…",
            Text = _query,
            Margin = new Thickness(0, 2, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        box.TextChanged += (_, _) => { _query = box.Text.Trim(); Refresh(); };
        return box;
    }

    /// <summary>One CATEGORY: the title with its star and check on the right,
    /// then the objects on a tinted band beneath it.</summary>
    private FrameworkElement PackRow(Pack pack, List<Obj> items)
    {
        var lib = _h.Library();
        bool fav = lib.FavoriteObjectPacks.Contains(pack.Id);
        bool shown = !lib.HiddenObjectPacks.Contains(pack.Id);

        var head = new Grid { Padding = new Thickness(0, 4, 0, 4) };
        var title = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Left };
        title.Children.Add(new TextBlock
        {
            Text = pack.Name,
            FontSize = 14.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        });
        title.Children.Add(new TextBlock
        {
            Text = $"{pack.Items.Length} objects",
            FontSize = 11,
            Opacity = 0.55,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        });
        head.Children.Add(title);

        var marks = ChromeUi.Row(2);
        marks.HorizontalAlignment = HorizontalAlignment.Right;
        marks.VerticalAlignment = VerticalAlignment.Center;
        marks.Children.Add(MarkButton(StarGeometry, fav,
            fav ? "Remove from favourites" : "Add to favourites",
            () =>
            {
                if (fav) lib.FavoriteObjectPacks.Remove(pack.Id);
                else lib.FavoriteObjectPacks.Add(pack.Id);
                _h.Save();
                Refresh();
            }));
        marks.Children.Add(MarkButton(CheckGeometry, shown,
            shown ? "Installed and shown — tap to collapse this pack" : "Hidden — tap to show it again",
            () =>
            {
                if (shown) lib.HiddenObjectPacks.Add(pack.Id);
                else lib.HiddenObjectPacks.Remove(pack.Id);
                _h.Save();
                Refresh();
            }));
        head.Children.Add(marks);

        var box = new StackPanel { Spacing = 0, Margin = new Thickness(0, 2, 0, 10) };
        box.Children.Add(head);
        box.Children.Add(ChromeUi.Caption(pack.Desc));

        if (!shown) return box;

        // The tinted band the strip sits on: a hair of contrast against the
        // window's own plate, never a card.
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Padding = new Thickness(6, 6, 6, 6) };
        foreach (var o in items) strip.Children.Add(ObjectTile(o));

        // §10.5 item 24 — "the Objects library glitches when scrolled sideways".
        // Two causes, both in this one construction: a bare ScrollViewer nested
        // in the window's vertical scroller chains its horizontal fling into the
        // parent and lurches the whole panel, and the band's corner radius did
        // not clip the scroller inside it so tiles bled over the rounded corners.
        var scroller = StripScroll.Horizontal(strip);
        scroller.Height = Band;
        scroller.CornerRadius = new CornerRadius(10);

        box.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(10),
            // a wash of the ink rather than a fixed grey at two alphas: the last
            // two-state colour pick in this window
            Background = new SolidColorBrush(ChromeUi.Wash(0x18)),
            Child = scroller,
        });
        return box;
    }

    /// <summary>One object: its own geometry drawn at tile size, its name under
    /// it, and a tap that places it.</summary>
    private FrameworkElement ObjectTile(Obj o)
    {
        var art = Preview(o);
        var cell = new Grid
        {
            Width = Tile,
            Height = Tile - 12,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        if (art != null) cell.Children.Add(art);

        var stack = new StackPanel
        {
            Width = Tile,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        stack.Children.Add(cell);
        stack.Children.Add(new TextBlock
        {
            Text = o.Name,
            FontSize = 9.5,
            Opacity = 0.7,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        });
        ToolTipService.SetToolTip(stack, "Place a " + o.Name.ToLowerInvariant() + " on the page");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stack, o.Name);
        // Slop-guarded, not Tapped: a slow sideways drag over the band used to
        // end in a Tapped on whichever tile the pointer came to rest on, so
        // scrolling the row dropped a shape on the page (§10.5 item 24).
        StripScroll.Tap(stack, () =>
        {
            _h.InsertShape(o.Kind, o.Regular);
            _h.Status($"{o.Name} placed — drag it to move, drag a corner to resize.");
        });
        return stack;
    }

    // =====================================================================
    // The two surfaces with nothing behind them. Built, and honest.
    // =====================================================================
    private FrameworkElement BuildMarket()
    {
        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(SearchField());
        root.Children.Add(Empty(
            "The Object Market is not available",
            "Concepts sells object packs here. Quill has no pack format, no store account and no purchase path, " +
            "so there is nothing to list — and a grid of greyed-out packs you could never buy would be a " +
            "storefront that does not exist. The tab is here because the library's shape is fixed by the design."));
        return root;
    }

    private FrameworkElement BuildPexels()
    {
        var root = new StackPanel { Spacing = 0 };
        root.Children.Add(SearchField());
        root.Children.Add(Empty(
            "Pexels search is not available",
            "This tab searches Pexels' stock photography in Concepts. Quill makes no network calls at all — it has " +
            "no HTTP client, no API key and no image-licensing path — so this searches nothing. Use Import ▸ From " +
            "file to place a picture you already have."));
        return root;
    }

    private static FrameworkElement Empty(string title, string body)
    {
        var box = new StackPanel { Spacing = 4, Margin = new Thickness(0, 24, 0, 0) };
        box.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(ChromeUi.Ink),
        });
        box.Children.Add(ChromeUi.Caption(body));
        return box;
    }

    // =====================================================================
    // Marks. Authored vector geometry — never a glyph font, never an emoji.
    // =====================================================================
    private const string StarGeometry =
        "M12 2.4 L14.9 9.1 L22.1 9.7 L16.6 14.4 L18.3 21.5 L12 17.7 L5.7 21.5 L7.4 14.4 L1.9 9.7 L9.1 9.1 Z";
    private const string CheckGeometry =
        "M12 1.8 A10.2 10.2 0 1 1 11.99 1.8 Z M17.1 8.1 L15.6 6.7 L10.4 12.6 L8.2 10.4 L6.8 11.9 L10.5 15.6 Z";

    /// <summary>A star or a check, filled when the state is on and a thin
    /// outline when it is off, so the row reads at a glance.</summary>
    private static Button MarkButton(string geometry, bool on, string tip, Action click)
    {
        var art = on
            ? Icons.Filled(geometry, ChromeUi.Accent, 17)
            : Icons.Stroked(geometry, ChromeUi.Ink, 17, 1.3);
        if (art != null && !on) art.Opacity = 0.45;
        var b = new Button
        {
            Content = art,
            Width = 30,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(15),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>The object's own outline, GENERATED FROM ITS DEFINITION rather
    /// than drawn by hand — so a preview can never show something the insert
    /// does not place.</summary>
    private static FrameworkElement? Preview(Obj o)
    {
        var ink = new SolidColorBrush(ChromeUi.Ink);
        const double s = 40, c = s / 2, r = 17;

        Shape shape;
        switch (o.Kind)
        {
            case ShapeKind.Line:
                shape = new Line { X1 = 3, Y1 = s - 5, X2 = s - 3, Y2 = 5 };
                break;
            case ShapeKind.Arrow:
                return Vector("M3 34 L33 8 M33 8 L24 9.5 M33 8 L31.5 17", s, stroked: true);
            case ShapeKind.Rect:
                shape = o.Regular
                    ? new Rectangle { Width = 30, Height = 30 }
                    : new Rectangle { Width = 34, Height = 23 };
                break;
            case ShapeKind.Ellipse:
                shape = o.Regular
                    ? new Ellipse { Width = 32, Height = 32 }
                    : new Ellipse { Width = 36, Height = 24 };
                break;
            case ShapeKind.Triangle:
                shape = Poly(Regular(3, c, r, -90));
                break;
            case ShapeKind.RightTriangle:
                shape = Poly(new[] { new Point(4, s - 4), new Point(s - 4, s - 4), new Point(4, 5) });
                break;
            case ShapeKind.Diamond:
                shape = Poly(new[] { new Point(c, 3), new Point(s - 4, c), new Point(c, s - 3), new Point(4, c) });
                break;
            case ShapeKind.Parallelogram:
                shape = Poly(new[] { new Point(10, 7), new Point(s - 2, 7), new Point(s - 10, s - 7), new Point(2, s - 7) });
                break;
            case ShapeKind.Trapezoid:
                shape = Poly(new[] { new Point(11, 8), new Point(s - 11, 8), new Point(s - 3, s - 8), new Point(3, s - 8) });
                break;
            case ShapeKind.Pentagon:
                shape = Poly(Regular(5, c, r, -90));
                break;
            case ShapeKind.Hexagon:
                shape = Poly(Regular(6, c, r, -90));
                break;
            case ShapeKind.Star:
                shape = Poly(Star(c, r, r * 0.45));
                break;
            case ShapeKind.AxesXY:
                return Vector("M6 34 L6 5 M6 34 L35 34 M6 5 L4 9 M6 5 L8 9 M35 34 L31 32 M35 34 L31 36", s, stroked: true);
            case ShapeKind.AxesXYZ:
                return Vector("M8 32 L8 5 M8 32 L35 32 M8 32 L2 38", s, stroked: true);
            default:
                shape = new Rectangle { Width = 30, Height = 22 };
                break;
        }

        shape.Stroke = ink;
        shape.StrokeThickness = 1.7;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.HorizontalAlignment = HorizontalAlignment.Center;
        shape.VerticalAlignment = VerticalAlignment.Center;
        return shape;

        static Point[] Regular(int n, double cx, double rr, double startDeg)
        {
            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double a = (startDeg + i * 360.0 / n) * Math.PI / 180;
                pts[i] = new Point(cx + rr * Math.Cos(a), cx + rr * Math.Sin(a));
            }
            return pts;
        }

        static Point[] Star(double cx, double outer, double inner)
        {
            var pts = new Point[10];
            for (int i = 0; i < 10; i++)
            {
                double a = (-90 + i * 36.0) * Math.PI / 180;
                double rr = (i & 1) == 0 ? outer : inner;
                pts[i] = new Point(cx + rr * Math.Cos(a), cx + rr * Math.Sin(a));
            }
            return pts;
        }

        static Polygon Poly(Point[] pts)
        {
            var p = new Polygon();
            foreach (var pt in pts) p.Points.Add(pt);
            return p;
        }
    }

    private static FrameworkElement? Vector(string data, double size, bool stroked) =>
        stroked ? Icons.Stroked(data, ChromeUi.Ink, size, 1.7) : Icons.Filled(data, ChromeUi.Ink, size);
}
