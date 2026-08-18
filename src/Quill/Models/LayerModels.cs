using System.Text.Json.Serialization;

namespace Quill.Models;

// ===========================================================================
// LAYERS (CONCEPTS-REF 18)
//
// The roadmap parks four features behind one data model - PSD export, per-layer
// visibility, selection scoping, and per-object rows in the Objects library.
// This file is that model, and NOT a layers panel: everything here is data plus
// pure functions over a NotePage.
//
// THE ONE IDEA. A page's Strokes, Shapes and Texts DO NOT MOVE. Membership is a
// small integer key ON the element, and key 0 - the value an absent field
// deserialises to - IS the base layer. So a page written before layers existed
// reads back as a page with one visible, unlocked, fully opaque layer holding
// everything, without a single byte on disk changing and without a load-time
// migration pass over a 53 MB library. The alternative, a Layer that OWNS its
// strokes, reads as an EMPTY PAGE to anything that predates layers, and that is
// the one way to turn a feature into data loss.
// ===========================================================================

/// <summary>One layer of a page. Every default is today's behaviour: visible,
/// unlocked, fully opaque.
///
/// <para><b>Key is identity; list position is order.</b> There is deliberately
/// no Order integer beside Key - <see cref="NotePage.Layers"/> is already an
/// ordered list, and a second source of truth for the same fact is a bug waiting
/// for the two to disagree. Keys are handed out by
/// <see cref="PageLayers.NextKey"/> and never reused within a page, so reordering
/// or deleting a layer cannot silently repoint content at a different one.</para>
///
/// <para><b>Keys are PAGE-SCOPED.</b> An element copied to another page carries a
/// key that means something else there; a cross-page paste has to re-key it
/// (18.10). That is the price of an int, and the int is worth it: this key rides
/// on every stroke, shape and text box in the library, where a Guid would cost
/// 36 characters apiece on a file that is rewritten whole every 1.5 seconds.</para></summary>
public class Layer
{
    /// <summary>Stable identity for the life of the page. 0 is the BASE layer -
    /// the one an absent <see cref="PenStroke.LayerKey"/> means, and therefore
    /// the one every element that predates layers is already on.</summary>
    public int Key { get; set; }

    /// <summary>"" derives a name from position (<see cref="PageLayers.DisplayName"/>).
    /// Empty rather than "Layer 1" because a page that has never been through a
    /// layers UI has no name the user chose, and inventing one and persisting it
    /// would make a derived label look like a decision.</summary>
    public string Name { get; set; } = "";

    /// <summary>HIDDEN rather than Visible so that false - today's behaviour - is
    /// the zero value and costs nothing to write, exactly as
    /// <see cref="PenStroke.Locked"/> does.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    /// <summary>0-1, a MULTIPLIER over each element's own opacity, applied at
    /// DRAW TIME and never written back to an element. See 18.8: this is the
    /// same promise §16.7 makes about the veil, and it is kept the same way.
    /// Not WhenWritingDefault, because the default that matters here is 1 and
    /// the zero value is 0 - a layer list is tens of entries, so writing it
    /// always costs nothing worth saving.</summary>
    public float Opacity { get; set; } = 1f;

    /// <summary>A locked layer's content cannot be selected or edited. Composes
    /// with the per-element padlock rather than replacing it: an element is
    /// editable when NEITHER it nor its layer is locked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Locked { get; set; }

    public long CreatedTicks { get; set; } = DateTime.UtcNow.Ticks;

    public override string ToString() => Name.Length > 0 ? Name : $"Layer#{Key}";
}

/// <summary>What happens to a deleted layer's content. Reassign is the default
/// and always will be: content that is intact but invisible is the failure this
/// model exists to refuse.</summary>
public enum LayerRemoval
{
    /// <summary>Move the content to the base layer. Nothing is lost.</summary>
    ReassignToBase,
    /// <summary>Delete the content with the layer, Photoshop-style.</summary>
    DeleteContent,
}

/// <summary>Which of a page's three content lists a row came from.</summary>
public enum LayerObjectKind { Stroke, Shape, Text }

/// <summary>THE SCOPE SEAM (CONCEPTS-REF 18.1) — what the bottom mode bar's
/// third control switches between.
///
/// <para><b>AllLayers is the zero value on purpose.</b> A default-constructed
/// scope, an unset field, and a surface that has never heard of layers all mean
/// today's behaviour: everything is in scope. A stub cannot accidentally scope
/// a user out of their own drawing.</para>
///
/// <para>Where the current scope is STORED is the mode bar's business. This
/// model neither persists it on <see cref="NotePage"/> nor mirrors it into
/// Library: passing it in is the whole interface, and that is what stops a
/// second, competing layer concept from growing beside this one.</para></summary>
public enum LayerScope
{
    /// <summary>Every layer is in scope — today's behaviour.</summary>
    AllLayers,
    /// <summary>Only the page's active layer is in scope.</summary>
    ActiveLayer,
}

/// <summary>One layer's content, in the order it paints (18.5): shapes, then
/// strokes, then texts - the page's existing type order, preserved exactly
/// inside the layer so that a page with one layer paints bit-identically to
/// the way it painted before layers existed.</summary>
public sealed record LayerContent(
    Layer Layer,
    IReadOnlyList<ShapeElement> Shapes,
    IReadOnlyList<PenStroke> Strokes,
    IReadOnlyList<TextElement> Texts)
{
    public int Count => Shapes.Count + Strokes.Count + Texts.Count;
}

/// <summary>One object on the page, for the Objects library's per-object rows
/// (18.9 seam 4). Carries IDENTITY, not a copy of the element: the caller
/// already holds the page.
///
/// <para><see cref="Label"/> is a stable TYPE label ("Brush", "Ellipse", the
/// file name of an image) and is not localised - a richer or translated label
/// is the view's job, and putting it here would drag Helpers/Loc into the
/// model.</para></summary>
public sealed record LayerRow(
    Layer Layer,
    LayerObjectKind Kind,
    Guid Id,
    string Label,
    bool Locked);

/// <summary>
/// EVERY QUESTION ANYTHING ASKS ABOUT LAYERS (CONCEPTS-REF 18).
///
/// <para>Static and pure over a <see cref="NotePage"/>, so the renderer, the
/// exporters, the selection path and the Objects library all reach the same
/// answers without any of them holding a reference to the others.</para>
///
/// <para><b>Every resolution path falls towards visibility.</b> A key naming no
/// layer resolves to the base layer and draws; it is NOT rewritten, because a
/// build that later restores the missing layer should be able to reunite the
/// content with it. An unreadable key becomes 0 through
/// <see cref="TolerantIntConverter"/>. The base layer cannot be deleted, and a
/// page always has at least one layer, so there is always somewhere for content
/// to be.</para>
/// </summary>
public static class PageLayers
{
    /// <summary>The base layer's key. Also the value an absent LayerKey
    /// deserialises to, which is what makes migration a no-op.</summary>
    public const int BaseKey = 0;

    // ---- reading ---------------------------------------------------------

    /// <summary>Every layer, BOTTOM FIRST. Never null and never empty.
    ///
    /// <para>When the page has no stored list this synthesises a FRESH implicit
    /// base layer each call. That is deliberate: you cannot rename or hide a
    /// layer that has not been materialised, and a shared mutable singleton
    /// would let a caller think it had. Call <see cref="Materialise"/> before
    /// editing.</para></summary>
    public static IReadOnlyList<Layer> All(NotePage page)
        => page.Layers is { Count: > 0 } ls ? ls : new[] { ImplicitBase(page) };

    /// <summary>True when this page has never had a real layer list - i.e. it is
    /// still costing exactly the bytes it did before layers existed.</summary>
    public static bool IsImplicit(NotePage page) => page.Layers is not { Count: > 0 };

    /// <summary>The layer a key names, resolving an unknown key to the base
    /// layer (and, if even that is missing, to the bottom-most layer). Never
    /// null.</summary>
    public static Layer Of(NotePage page, int layerKey)
    {
        var all = All(page);
        for (int i = 0; i < all.Count; i++) if (all[i].Key == layerKey) return all[i];
        for (int i = 0; i < all.Count; i++) if (all[i].Key == BaseKey) return all[i];
        return all[0];
    }

    /// <summary>The layer new ink lands on. An <see cref="NotePage.ActiveLayer"/>
    /// naming no layer resolves to the base layer rather than leaving the page
    /// with nowhere to draw.</summary>
    public static Layer Active(NotePage page) => Of(page, page.ActiveLayer);

    /// <summary>Name for display; "" derives "Layer N" from position, counting
    /// from the bottom. English by design - see 18.12 item 5.</summary>
    public static string DisplayName(NotePage page, Layer layer)
    {
        if (!string.IsNullOrWhiteSpace(layer.Name)) return layer.Name;
        var all = All(page);
        for (int i = 0; i < all.Count; i++)
            if (ReferenceEquals(all[i], layer) || all[i].Key == layer.Key)
                return "Layer " + (i + 1);
        return "Layer 1";
    }

    // ---- the render-time answers (18.8) ----------------------------------

    /// <summary>0 when hidden, otherwise the clamped opacity. A MULTIPLIER for
    /// the draw call. Nothing may write this into an element.</summary>
    public static float EffectiveOpacity(Layer layer)
        => layer.Hidden ? 0f : Math.Clamp(layer.Opacity, 0f, 1f);

    public static float EffectiveOpacity(NotePage page, int layerKey)
        => EffectiveOpacity(Of(page, layerKey));

    /// <summary>Whether anything on this layer is drawn at all.</summary>
    public static bool IsVisible(NotePage page, int layerKey) => !Of(page, layerKey).Hidden;

    /// <summary>SELECTION SCOPING (18.9 seam 3). False when the layer is hidden
    /// or locked. Composes with the element's own padlock rather than replacing
    /// it - a caller wants <c>IsEditable(page, e.LayerKey) &amp;&amp; !e.Locked</c>.</summary>
    public static bool IsEditable(NotePage page, int layerKey)
    {
        var l = Of(page, layerKey);
        return !l.Hidden && !l.Locked;
    }

    /// <summary>"Is this in the active layer?" - asked with the element's key,
    /// and true for an orphaned key when the active layer is the base one,
    /// because that is where an orphan resolves to.</summary>
    public static bool InActive(NotePage page, int layerKey)
        => Of(page, layerKey).Key == Active(page).Key;

    /// <summary>THE SCOPE SEAM (18.1). "Is this element in scope for the tool?"
    ///
    /// <para>On a page with one implicit layer BOTH scopes answer true for
    /// everything, which is the compatibility guarantee a mode-bar stub can be
    /// built against: it cannot diverge from a real layer list until the user
    /// actually makes a second layer.</para></summary>
    public static bool InScope(NotePage page, int layerKey, LayerScope scope)
        => scope == LayerScope.AllLayers || InActive(page, layerKey);

    /// <summary>Both halves of the selection question at once: in scope for the
    /// tool, AND on a layer that is neither hidden nor locked. Still composes
    /// with the element's own padlock, which is the caller's to check.</summary>
    public static bool CanSelect(NotePage page, int layerKey, LayerScope scope)
        => IsEditable(page, layerKey) && InScope(page, layerKey, scope);

    // ---- z-order (18.5) --------------------------------------------------

    /// <summary>
    /// THE PAINT ORDER, and the seam PSD export iterates.
    ///
    /// <para>Bottom layer first; within each layer the page's existing type order
    /// - shapes, then strokes, then texts - preserved exactly. With zero or one
    /// layer this is bit-identical to what the draw loop does today, which is
    /// what makes it safe to land this model before the renderer adopts it.</para>
    ///
    /// <para>Empty layers are yielded: the user made them, and PSD export should
    /// write them.</para>
    /// </summary>
    public static IEnumerable<LayerContent> InOrder(NotePage page)
    {
        var all = All(page);
        int n = all.Count;

        // key -> bucket. First occurrence wins, so a duplicated key (which
        // NextKey makes impossible but a hand-edited file does not) cannot make
        // content vanish into the second one.
        var bucketOf = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) bucketOf.TryAdd(all[i].Key, i);

        int fallback = bucketOf.TryGetValue(BaseKey, out int b) ? b : 0;
        int Bucket(int key) => bucketOf.TryGetValue(key, out int i) ? i : fallback;

        var shapes = new List<ShapeElement>[n];
        var strokes = new List<PenStroke>[n];
        var texts = new List<TextElement>[n];
        for (int i = 0; i < n; i++)
        {
            shapes[i] = new List<ShapeElement>();
            strokes[i] = new List<PenStroke>();
            texts[i] = new List<TextElement>();
        }

        foreach (var s in page.Shapes) shapes[Bucket(s.LayerKey)].Add(s);
        foreach (var s in page.Strokes) strokes[Bucket(s.LayerKey)].Add(s);
        foreach (var t in page.Texts) texts[Bucket(t.LayerKey)].Add(t);

        for (int i = 0; i < n; i++)
            yield return new LayerContent(all[i], shapes[i], strokes[i], texts[i]);
    }

    /// <summary>THE OBJECTS LIBRARY's rows (18.9 seam 4): one row per object,
    /// grouped by layer, TOP layer first and topmost object first within it,
    /// because that is the order a layers list reads in.</summary>
    public static IEnumerable<LayerRow> Rows(NotePage page)
    {
        var buckets = InOrder(page).ToList();
        for (int b = buckets.Count - 1; b >= 0; b--)
        {
            var c = buckets[b];
            for (int i = c.Texts.Count - 1; i >= 0; i--)
                yield return new LayerRow(c.Layer, LayerObjectKind.Text, c.Texts[i].Id,
                                          "Text", c.Texts[i].Locked);
            for (int i = c.Strokes.Count - 1; i >= 0; i--)
                yield return new LayerRow(c.Layer, LayerObjectKind.Stroke, c.Strokes[i].Id,
                                          c.Strokes[i].Pen.ToString(), c.Strokes[i].Locked);
            for (int i = c.Shapes.Count - 1; i >= 0; i--)
                yield return new LayerRow(c.Layer, LayerObjectKind.Shape, c.Shapes[i].Id,
                                          ShapeLabel(c.Shapes[i]), c.Shapes[i].Locked);
        }
    }

    // ---- writing ---------------------------------------------------------

    /// <summary>Turns the implicit base layer into a real, editable one. This is
    /// the ONLY thing that ever makes a page start carrying a Layers array, and
    /// it is called by the operations that genuinely need one - never on load.
    /// Idempotent.</summary>
    public static List<Layer> Materialise(NotePage page)
    {
        if (page.Layers is { Count: > 0 } ls) return ls;
        var made = new List<Layer> { ImplicitBase(page) };
        page.Layers = made;
        return made;
    }

    /// <summary>The next unused key for this page.
    ///
    /// <para>Scans the ELEMENTS as well as the layer list, so a key that is
    /// currently orphaned - content pointing at a layer some other build removed
    /// - can never be handed to a brand-new layer, which would make that layer
    /// silently adopt somebody else's drawing.</para></summary>
    public static int NextKey(NotePage page)
    {
        int max = BaseKey;
        if (page.Layers != null)
            foreach (var l in page.Layers) if (l.Key > max) max = l.Key;
        foreach (var s in page.Strokes) if (s.LayerKey > max) max = s.LayerKey;
        foreach (var s in page.Shapes) if (s.LayerKey > max) max = s.LayerKey;
        foreach (var t in page.Texts) if (t.LayerKey > max) max = t.LayerKey;
        return max + 1;
    }

    /// <summary>Adds a layer directly above <paramref name="aboveKey"/>, or on
    /// top when it is null or names nothing. Returns the new layer.</summary>
    public static Layer Add(NotePage page, string name = "", int? aboveKey = null)
    {
        var ls = Materialise(page);
        var layer = new Layer { Key = NextKey(page), Name = name ?? "" };
        int at = ls.Count;
        if (aboveKey is int ak)
        {
            int i = IndexOf(ls, ak);
            if (i >= 0) at = i + 1;
        }
        ls.Insert(at, layer);
        return layer;
    }

    /// <summary>Removes a layer. Its content is REASSIGNED TO THE BASE LAYER by
    /// default; deleting it is opt-in and never implied.
    ///
    /// <para>Refuses to remove the base layer, and refuses to empty the list:
    /// a page must always have somewhere for content to be.</para></summary>
    public static bool Remove(NotePage page, int key,
                              LayerRemoval mode = LayerRemoval.ReassignToBase)
    {
        if (key == BaseKey) return false;
        var ls = page.Layers;
        if (ls == null || ls.Count <= 1) return false;
        int i = IndexOf(ls, key);
        if (i < 0) return false;
        ls.RemoveAt(i);

        int fallback = IndexOf(ls, BaseKey) >= 0 ? BaseKey : ls[0].Key;
        if (mode == LayerRemoval.DeleteContent)
        {
            page.Strokes.RemoveAll(s => s.LayerKey == key);
            page.Shapes.RemoveAll(s => s.LayerKey == key);
            page.Texts.RemoveAll(t => t.LayerKey == key);
        }
        else
        {
            foreach (var s in page.Strokes) if (s.LayerKey == key) s.LayerKey = fallback;
            foreach (var s in page.Shapes) if (s.LayerKey == key) s.LayerKey = fallback;
            foreach (var t in page.Texts) if (t.LayerKey == key) t.LayerKey = fallback;
        }
        if (page.ActiveLayer == key) page.ActiveLayer = fallback;
        return true;
    }

    /// <summary>Moves a layer to a new position, 0 = bottom. Order is the list;
    /// no key changes, so no element is repointed.</summary>
    public static bool Move(NotePage page, int key, int newIndex)
    {
        var ls = page.Layers;
        if (ls == null || ls.Count == 0) return false;
        int i = IndexOf(ls, key);
        if (i < 0) return false;
        newIndex = Math.Clamp(newIndex, 0, ls.Count - 1);
        if (newIndex == i) return true;
        var layer = ls[i];
        ls.RemoveAt(i);
        ls.Insert(newIndex, layer);
        return true;
    }

    /// <summary>Sets the active layer, resolving an unknown key rather than
    /// storing it.</summary>
    public static void SetActive(NotePage page, int key) => page.ActiveLayer = Of(page, key).Key;

    // ---- internals -------------------------------------------------------

    /// <summary>A fresh stand-in for the layer every page has always had. Its
    /// CreatedTicks is the PAGE's, because that is when this layer really came
    /// into being.</summary>
    private static Layer ImplicitBase(NotePage page)
        => new() { Key = BaseKey, Name = "", CreatedTicks = page.CreatedTicks };

    private static int IndexOf(List<Layer> ls, int key)
    {
        for (int i = 0; i < ls.Count; i++) if (ls[i].Key == key) return i;
        return -1;
    }

    private static string ShapeLabel(ShapeElement s)
        => s.Kind == ShapeKind.Image && !string.IsNullOrEmpty(s.ImagePath)
            ? System.IO.Path.GetFileName(s.ImagePath!)
            : s.Kind.ToString();
}
