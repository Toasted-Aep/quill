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

/// <summary>What happens to a deleted layer's content.
///
/// <para><b>DeleteContent is the default. That is the user's ruling</b> (18.12
/// item 3), and it replaced a reassign-to-base default this model originally
/// chose on safety grounds. Deleting a layer deletes its drawing, Photoshop's
/// way.</para>
///
/// <para><b>The ruling brings an obligation with it and the obligation is not
/// optional.</b> Reassign could not destroy work; this can, so a layer deletion
/// must go through the undo stack and its undo must bring back the CONTENT and
/// not merely the row. See <c>RemoveLayerAction</c> in Services/UndoRedo.cs,
/// which is the only thing that should be calling
/// <see cref="PageLayers.Remove"/> from a command path — a Layer coming back
/// empty would be worse than the old default, because the user would believe
/// their work was recoverable and find an empty shell.</para>
///
/// <para>ReassignToBase is kept and is still the right answer for a route that
/// is getting RID OF A LAYER rather than deleting a drawing — merging down, or
/// tidying an empty structure.</para></summary>
public enum LayerRemoval
{
    /// <summary>Delete the content with the layer. The user's ruling, and the
    /// default. Undoable only through <c>RemoveLayerAction</c>.</summary>
    DeleteContent,
    /// <summary>Move the content to the base layer. Nothing is lost.</summary>
    ReassignToBase,
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

    /// <summary>
    /// WHETHER ANYTHING ON THIS LAYER IS DRAWN AT ALL - and therefore whether
    /// anything on it can be picked, by any means (58.10, ruling A).
    ///
    /// <para><b>This is §49.1's one fact, and this overload is the one place it
    /// is computed.</b> 0 means hidden, so a layer is visible exactly when
    /// <see cref="EffectiveOpacity(Layer)"/> is above 0: <c>Hidden</c> and an
    /// opacity of 0% are the same answer. <see cref="DrawPlan"/> leaves a layer
    /// out on it, <see cref="IsEditable"/> (and so <see cref="CanSelect"/>, and
    /// so every selection gesture) refuses on it, and <see cref="LayerPick"/>'s
    /// single-answer picks refuse on it. Until 58.10 this was <c>!Hidden</c>,
    /// so a 0% layer was "visible" to the lasso and invisible to a click.</para>
    /// </summary>
    public static bool IsVisible(Layer layer) => EffectiveOpacity(layer) > 0f;

    /// <summary>The same fact for an element's key (unknown keys resolve to the
    /// base layer, 18.7).</summary>
    public static bool IsVisible(NotePage page, int layerKey) => IsVisible(Of(page, layerKey));

    /// <summary>SELECTION SCOPING (18.9 seam 3). False when the layer draws
    /// nothing (<see cref="IsVisible(Layer)"/>: hidden OR 0%) or is locked.
    /// Locked is a SEPARATE fact - a locked layer is drawn and cannot be
    /// selected; it is not "invisible". Composes with the element's own padlock
    /// rather than replacing it - a caller wants
    /// <c>IsEditable(page, e.LayerKey) &amp;&amp; !e.Locked</c>.</summary>
    public static bool IsEditable(NotePage page, int layerKey)
    {
        var l = Of(page, layerKey);
        return IsVisible(l) && !l.Locked;
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
    /// tool, AND on a layer that is drawn (not hidden, not 0%) and not locked.
    /// Still composes with the element's own padlock, which is the caller's to
    /// check - <see cref="LayerPick.Catchable"/> is that composition.</summary>
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

        // key -> bucket. ONE resolver, shared with DrawPlan (58.4), so the
        // canvas and this seam cannot disagree about where an orphan lands:
        // first occurrence wins, so a duplicated key (which NextKey makes
        // impossible but a hand-edited file does not) cannot make content
        // vanish into the second one, and an unknown key falls to the base
        // layer's bucket, or the bottom one when even that is missing (18.7).
        // The resolver alone (one int[]), not a whole DrawPlan: InOrder never
        // reads the steps (58.10, check 5).
        var resolver = new LayerBuckets(all);
        int Bucket(int key) => resolver.Of(key);

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

    /// <summary>Removes a layer, DELETING ITS CONTENT WITH IT by default — the
    /// user's ruling (18.12 item 3).
    ///
    /// <para><b>Call this from <c>RemoveLayerAction</c>, not from a command path
    /// directly.</b> The default destroys drawing, so the deletion has to be
    /// undoable and the undo has to restore the content; that action is what
    /// makes both true. This method is the mechanism, not the command.</para>
    ///
    /// <para>Refuses to remove the base layer, and refuses to empty the list:
    /// a page must always have somewhere for content to be.</para></summary>
    public static bool Remove(NotePage page, int key,
                              LayerRemoval mode = LayerRemoval.DeleteContent)
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

/// <summary>What one <see cref="DrawStep"/> paints.</summary>
public enum DrawStepKind
{
    /// <summary>The page's oil paint (OILPAINT, §49.10 item 3). Tiles, not
    /// elements, and outside the layer model - so it has one height and the
    /// layer list cannot address it. Always the FIRST step (58.2).</summary>
    Paint,
    /// <summary>One layer's shapes (and image attachments), in list order.</summary>
    Shapes,
    /// <summary>One layer's strokes, in list order.</summary>
    Strokes,
    /// <summary>One layer's text boxes. On the canvas these are live XAML
    /// controls in a layer above the Win2D surface, so text sits above ALL ink
    /// whatever the layer order (58.2, for now); the plan says so by putting
    /// every Texts step after every ink step.</summary>
    Texts,
    /// <summary>The LIVE oil gesture's scratch - the wet stroke still under the
    /// brush (58.10, ruling B). Exactly one, after every Shapes and Strokes
    /// step, so the user sees what they are painting over an opaque image or
    /// ink; on lift the scratch is committed into the settled tiles, which draw
    /// at <see cref="Paint"/>, below every layer (58.2). Whether it draws
    /// anything is <see cref="DrawPlan.PaintAt"/>'s answer, not the plan's.
    /// Declared LAST so no existing kind's value moves.</summary>
    WetPaint,
}

/// <summary>What a paint step composites (58.10, ruling B). Settled paint and
/// the wet scratch are separate sources drawn at separate steps, so a frame can
/// never draw the scratch twice.</summary>
[Flags]
public enum PaintSource
{
    None = 0,
    /// <summary>The page's committed paint tiles.</summary>
    Settled = 1,
    /// <summary>The live gesture's scratch tiles.</summary>
    Wet = 2,
}

/// <summary>
/// THE KEY -> BUCKET RESOLVER, and the only implementation of it (18.7 / 58.4).
/// <see cref="DrawPlan"/> holds one and <see cref="PageLayers.InOrder"/> builds
/// one, so the canvas and the panel/thumbnail/PSD seam cannot disagree about
/// where an element paints.
///
/// <para>First layer with a matching key wins; an unknown key falls to the base
/// layer's bucket, or the bottom one when the page has no base layer - visible,
/// never dropped. One layer: bucket 0 without looking at the key.</para>
///
/// <para><b>An empty layer list is REJECTED</b> (58.10, check 6).
/// <see cref="PageLayers.All"/> is never empty, so the app never builds one; a
/// direct caller that passes one gets an <see cref="ArgumentException"/> here,
/// at construction, instead of an IndexOutOfRange later from a lookup.</para>
/// </summary>
public readonly struct LayerBuckets
{
    private readonly int[] _keys;
    private readonly int _fallback;

    public LayerBuckets(IReadOnlyList<Layer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0)
            throw new ArgumentException(
                "A page always has at least one layer (PageLayers.All); an empty layer list has no bucket to resolve to.",
                nameof(layers));
        int n = layers.Count;
        _keys = new int[n];
        _fallback = -1;
        for (int i = 0; i < n; i++)
        {
            _keys[i] = layers[i].Key;
            if (_fallback < 0 && layers[i].Key == PageLayers.BaseKey) _fallback = i;
        }
        if (_fallback < 0) _fallback = 0;
    }

    /// <summary>How many buckets (layers).</summary>
    public int Count => _keys.Length;

    /// <summary>Which bucket an element with this key paints in.</summary>
    public int Of(int layerKey)
    {
        if (_keys.Length <= 1) return 0;
        for (int i = 0; i < _keys.Length; i++) if (_keys[i] == layerKey) return i;
        return _fallback;
    }
}

/// <summary>One step of the paint order. <see cref="Bucket"/> is the layer's
/// index in <see cref="PageLayers.All"/> - the same index
/// <see cref="PageLayers.InOrder"/> yields it at - and -1 for
/// <see cref="DrawStepKind.Paint"/> and <see cref="DrawStepKind.WetPaint"/>.
/// <see cref="Multiplier"/> is <see cref="PageLayers.EffectiveOpacity(Layer)"/>
/// for that layer (1 for paint), carried for callers that have no surface to
/// ask.</summary>
public readonly record struct DrawStep(DrawStepKind Kind, int Bucket, float Multiplier);

/// <summary>
/// THE PAINT ORDER ACROSS ELEMENT TYPES (CONCEPTS-REF 58.2 / 58.4 / 58.10) - the
/// one definition the canvas, the gallery thumbnail and hit-testing iterate.
///
/// <para><b>The ruling, in order:</b> settled oil paint first, just above the
/// paper and grid and below every layer; then each VISIBLE layer bottom first,
/// its shapes then its strokes (18.9's tuple order, so within a layer nothing
/// moves); then the live oil gesture's wet scratch, above all ink while the
/// gesture lasts (58.10 ruling B); then every visible layer's text, above all
/// ink. Reordering the stack therefore reorders shapes and strokes
/// together.</para>
///
/// <para><b>Visibility is not decided here.</b> A layer is left out exactly
/// when <see cref="PageLayers.IsVisible(Layer)"/> is false - hidden, or dropped
/// to 0% - which is §49.1's one fact, asked the one way. There is no second
/// idea of "is this layer showing"; <see cref="IsDrawn"/> hands the same answer
/// to the pick paths.</para>
///
/// <para><b>Buckets resolve the way <see cref="PageLayers.InOrder"/> does,
/// because both use <see cref="LayerBuckets"/>.</b> First layer with a
/// matching key wins; an unknown key falls to the base layer's bucket, or the
/// bottom one when the page has no base layer - visible, never dropped
/// (18.7). An empty layer list is rejected at construction.</para>
///
/// <para><b>Win2D-free and cheap.</b> The plan holds the layer list's keys and
/// at most 3 x layers + 2 steps; it does not copy the page's element lists,
/// so a draw loop filters the lists it already walks by
/// <see cref="BucketOf"/>. A page with no Layers array or one layer has ONE
/// bucket, and <see cref="BucketOf"/> answers 0 without looking at the
/// key.</para>
/// </summary>
public sealed class DrawPlan
{
    private readonly LayerBuckets _buckets;
    // per bucket: the index in Steps of its Shapes / Strokes / Texts step, or
    // -1 when the layer is not drawn; and the drawn fact itself.
    private readonly int[] _shapeStep, _strokeStep, _textStep;
    private readonly bool[] _drawn;
    private readonly DrawStep[] _steps;
    private readonly int _wetStep;

    /// <summary>The plan for a page, from <see cref="PageLayers.All"/>.</summary>
    public static DrawPlan For(NotePage page) => new(PageLayers.All(page));

    /// <summary>Throws <see cref="ArgumentException"/> for an empty list
    /// (<see cref="LayerBuckets"/>); every other list, orphans and duplicate
    /// keys included, yields a plan.</summary>
    public DrawPlan(IReadOnlyList<Layer> layers)
    {
        _buckets = new LayerBuckets(layers);   // rejects null and empty first
        int n = layers.Count;

        _shapeStep = new int[n];
        _strokeStep = new int[n];
        _textStep = new int[n];
        _drawn = new bool[n];
        _strokesIn = new bool[n];
        _shapesIn = new bool[n];
        var steps = new List<DrawStep>(3 * n + 2);

        // 58.2 item 2: settled paint below every layer, drawn once, before any ink.
        steps.Add(new DrawStep(DrawStepKind.Paint, -1, 1f));

        // 58.2 item 1: each layer's shapes and strokes together, bottom first.
        for (int i = 0; i < n; i++)
        {
            _shapeStep[i] = _strokeStep[i] = _textStep[i] = -1;
            _drawn[i] = PageLayers.IsVisible(layers[i]);   // §49.1 / 58.10: the one fact
            if (!_drawn[i]) continue;                       // draws nothing, picks nothing
            float m = PageLayers.EffectiveOpacity(layers[i]);
            _shapeStep[i] = steps.Count;
            steps.Add(new DrawStep(DrawStepKind.Shapes, i, m));
            _strokeStep[i] = steps.Count;
            steps.Add(new DrawStep(DrawStepKind.Strokes, i, m));
        }

        // 58.10 ruling B: the wet scratch above every layer's ink, while the
        // gesture lasts. After the LAST ink step, whatever the layer order.
        _wetStep = steps.Count;
        steps.Add(new DrawStep(DrawStepKind.WetPaint, -1, 1f));

        // 58.2 item 3: text above all ink, whatever the layer order.
        for (int i = 0; i < n; i++)
        {
            if (!_drawn[i]) continue;
            _textStep[i] = steps.Count;
            steps.Add(new DrawStep(DrawStepKind.Texts, i, PageLayers.EffectiveOpacity(layers[i])));
        }

        _steps = steps.ToArray();
    }

    /// <summary>Every step, in paint order.</summary>
    public IReadOnlyList<DrawStep> Steps => _steps;

    /// <summary>How many layers (buckets) the page has. 1 for a page with no
    /// Layers array.</summary>
    public int LayerCount => _buckets.Count;

    /// <summary>Which bucket an element with this key paints in. See
    /// <see cref="LayerBuckets"/>; it is InOrder's rule, by the same code.</summary>
    public int BucketOf(int layerKey) => _buckets.Of(layerKey);

    /// <summary>58.10 ruling A: whether an element with this key is drawn at
    /// all - <see cref="PageLayers.IsVisible(Layer)"/> for its layer, taken
    /// when the plan was built. False means it cannot be picked by any
    /// means.</summary>
    public bool IsDrawn(int layerKey) => _drawn[_buckets.Of(layerKey)];

    /// <summary>The index in <see cref="Steps"/> of the one
    /// <see cref="DrawStepKind.WetPaint"/> step.</summary>
    public int WetPaintStep => _wetStep;

    /// <summary>
    /// 58.10 RULING B, the flag that decides it: what a paint step composites.
    /// <see cref="DrawStepKind.Paint"/> draws the SETTLED tiles only - never the
    /// scratch, so the scratch cannot be drawn twice.
    /// <see cref="DrawStepKind.WetPaint"/> draws the scratch only while an oil
    /// gesture is live; once the gesture has ended (lift, pointer lost, page
    /// switch, device loss, window close - see InkSurface.SettleOilGesture)
    /// it draws nothing, so the scratch is never left on top. Every other step
    /// composites no paint.
    /// </summary>
    public static PaintSource PaintAt(DrawStepKind kind, bool wetGestureActive) => kind switch
    {
        DrawStepKind.Paint => PaintSource.Settled,
        DrawStepKind.WetPaint => wetGestureActive ? PaintSource.Wet : PaintSource.None,
        _ => PaintSource.None,
    };

    /// <summary>The index in <see cref="Steps"/> at which an element of this
    /// kind and key is painted, or -1 when its layer is not drawn. HIT-TESTING
    /// reads this in reverse: of two elements under the pointer, the one with
    /// the higher step is on top, and within one step the later list index is.
    /// Paint has no key and is not asked for here.</summary>
    public int StepOf(DrawStepKind kind, int layerKey)
    {
        int b = BucketOf(layerKey);
        return kind switch
        {
            DrawStepKind.Shapes => _shapeStep[b],
            DrawStepKind.Strokes => _strokeStep[b],
            DrawStepKind.Texts => _textStep[b],
            _ => -1,
        };
    }

    /// <summary>"Is (stepA, indexA) painted above (stepB, indexB)?" - the one
    /// comparison every topmost pick makes. A step of -1 (not drawn) is never
    /// above anything.</summary>
    public static bool IsAbove(int stepA, int indexA, int stepB, int indexB)
        => stepA >= 0 && (stepA > stepB || (stepA == stepB && indexA > indexB));

    // ---- the big-page ink cache's height (#43, 58.4, 58.10 check 1) --------

    // Scratch for the walk, allocated once per plan rather than per call.
    private readonly bool[] _strokesIn, _shapesIn;
    // The per-PASS memo: the answer for (page, pass), so a draw pass of ~120
    // invalidated regions walks the page once, not once per region.
    private NotePage? _memoPage;
    private long _memoPass = long.MinValue;
    private int _memoStep;

    /// <summary>Elements (strokes + shapes) the ink-cache walk has visited over
    /// this plan's life. A COUNTER, not a clock: it is how the harness proves
    /// the per-region cost (58.10 check 1) and costs one add per walk.</summary>
    public long InkCacheElementVisits { get; private set; }

    /// <summary>Where a single image holding EVERY stroke (the canvas's big-page
    /// ink cache, #43) may be drawn without changing the picture: the index of
    /// the first Strokes step that has strokes in it, or -1 when no such place
    /// exists because a layer's SHAPES sit between two layers' strokes. With one
    /// layer this is simply that layer's Strokes step, which is where the cache
    /// has always been drawn relative to shapes.
    ///
    /// <para><b>This overload WALKS THE PAGE</b> (every stroke and shape) on a
    /// multi-layer page. The draw loop must call
    /// <see cref="InkCacheStep(NotePage, long)"/>, which walks it once per
    /// pass.</para></summary>
    public int InkCacheStep(NotePage page)
    {
        if (_buckets.Count <= 1) return _strokeStep[0];
        Array.Clear(_strokesIn);
        Array.Clear(_shapesIn);
        foreach (var s in page.Strokes) _strokesIn[BucketOf(s.LayerKey)] = true;
        foreach (var s in page.Shapes) _shapesIn[BucketOf(s.LayerKey)] = true;
        InkCacheElementVisits += page.Strokes.Count + page.Shapes.Count;
        int first = -1, last = -1;
        for (int i = 0; i < _steps.Length; i++)
            if (_steps[i].Kind == DrawStepKind.Strokes && _strokesIn[_steps[i].Bucket])
            {
                if (first < 0) first = i;
                last = i;
            }
        if (first < 0) return -1;
        for (int i = first + 1; i < last; i++)
            if (_steps[i].Kind == DrawStepKind.Shapes && _shapesIn[_steps[i].Bucket]) return -1;
        return first;
    }

    /// <summary>
    /// <see cref="InkCacheStep(NotePage)"/> for one DRAW PASS (58.10 check 1).
    /// <paramref name="pass"/> identifies the pass - InkSurface bumps it once
    /// per <c>RegionsInvalidated</c> - and the answer is memoised against
    /// (page, pass): the first region of a pass walks the page, every later
    /// region of the same pass is O(1). One layer is O(1) always and walks
    /// nothing. The page cannot change between two regions of one pass (the
    /// handler runs on the UI thread start to finish), and any change lands in
    /// a LATER pass, which gets a fresh walk.
    /// </summary>
    public int InkCacheStep(NotePage page, long pass)
    {
        if (_buckets.Count <= 1) return _strokeStep[0];
        if (pass == _memoPass && ReferenceEquals(page, _memoPage)) return _memoStep;
        _memoStep = InkCacheStep(page);
        _memoPage = page;
        _memoPass = pass;
        return _memoStep;
    }

    /// <summary>The plan expanded over a page: every step, and for each element
    /// step every element of that type in that bucket, in list order - exactly
    /// the walk the canvas makes. Paint and WetPaint each yield one entry with a
    /// null element. Allocates an iterator; for a one-off render (the
    /// thumbnail) or a proof, not a per-frame loop.</summary>
    public IEnumerable<(DrawStep Step, object? Element)> Sequence(NotePage page)
    {
        foreach (var step in _steps)
        {
            switch (step.Kind)
            {
                case DrawStepKind.Paint:
                case DrawStepKind.WetPaint:
                    yield return (step, null);
                    break;
                case DrawStepKind.Shapes:
                    foreach (var s in page.Shapes)
                        if (BucketOf(s.LayerKey) == step.Bucket) yield return (step, s);
                    break;
                case DrawStepKind.Strokes:
                    foreach (var s in page.Strokes)
                        if (BucketOf(s.LayerKey) == step.Bucket) yield return (step, s);
                    break;
                case DrawStepKind.Texts:
                    foreach (var t in page.Texts)
                        if (BucketOf(t.LayerKey) == step.Bucket) yield return (step, t);
                    break;
            }
        }
    }
}

/// <summary>
/// THE PICK RULES (CONCEPTS-REF 58.5 / 58.10) - Win2D-free, so
/// <c>tools/LayerRoundTrip</c> runs the same code the canvas does.
///
/// <para><b>Ruling A (58.10): a layer that draws nothing cannot be picked by
/// any means.</b> "Draws nothing" is <see cref="PageLayers.IsVisible(Layer)"/>
/// and nothing else - §49.1 / 49.2's one fact. Two gates read it:</para>
/// <list type="bullet">
/// <item><see cref="Catchable"/> - the SELECTION gestures (click-select, lasso,
/// rectangle select). Visibility, plus the separate facts a selection also
/// respects: the layer lock and the All/Active scope
/// (<see cref="PageLayers.CanSelect"/>) and the element's own padlock.</item>
/// <item><see cref="DrawPlan.IsDrawn"/> - read unconditionally by
/// <see cref="Topmost{T}"/>, so every single-answer pick (the press grab, the
/// eraser preview, the eyedropper, the equation and axes editors) refuses an
/// undrawn layer whatever else it checks. It is the same fact, taken by the
/// plan from <see cref="PageLayers.IsVisible(Layer)"/>.</item>
/// </list>
/// <para>Locked is NOT folded into visibility: a locked layer is drawn, so the
/// eyedropper may sample it and the eraser preview may name it.</para>
/// </summary>
public static class LayerPick
{
    /// <summary>The selection gestures' gate: may this element be caught by a
    /// click, a lasso or a rectangle right now? The element padlock binds only
    /// when the padlock control says IGNORE (<paramref name="ignoreLocked"/>);
    /// the layer half is <see cref="PageLayers.CanSelect"/> - drawn, not
    /// locked, in scope.</summary>
    public static bool Catchable(NotePage page, int layerKey, bool elementLocked,
                                 bool ignoreLocked, LayerScope scope)
    {
        if (ignoreLocked && elementLocked) return false;
        return PageLayers.CanSelect(page, layerKey, scope);
    }

    /// <summary>
    /// The topmost element of one kind under a pointer, by the draw plan
    /// (58.5): of the elements that pass, the one in the highest step wins,
    /// and within a step the one latest in the list. Walks the list from the
    /// back, so with one layer it stops at the first hit - the backwards walk
    /// these picks always made.
    ///
    /// <para><b>Ruling A is tested here, first and unconditionally</b>
    /// (<see cref="DrawPlan.IsDrawn"/>): an element whose layer draws nothing
    /// is skipped whether the page has one layer or five. 58.4's
    /// <c>HitStrokeForClick</c> tested it only inside <c>if (multi)</c>, so on
    /// a one-layer page at 0% a click still found ink the plan did not
    /// draw.</para>
    ///
    /// <para><paramref name="admit"/> is the caller's other gates (the
    /// selection gate <see cref="Catchable"/>, a spatial-index candidate test,
    /// a colour that must parse); <paramref name="hit"/> is the geometry.
    /// Both are asked only for an element that could still win.</para>
    /// </summary>
    public static T? Topmost<T>(DrawPlan plan, IReadOnlyList<T> list, DrawStepKind kind,
                                Func<T, int> keyOf, Func<T, bool>? admit, Func<T, bool> hit)
        where T : class
    {
        bool multi = plan.LayerCount > 1;
        T? best = null;
        int bestStep = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var e = list[i];
            int key = keyOf(e);
            if (!plan.IsDrawn(key)) continue;                 // ruling A: draws nothing, picks nothing
            int step = plan.StepOf(kind, key);
            if (best != null && step <= bestStep) continue;   // lower index, not higher step: underneath
            if (admit != null && !admit(e)) continue;
            if (!hit(e)) continue;
            if (!multi) return e;
            best = e;
            bestStep = step;
        }
        return best;
    }

    /// <summary>
    /// THE LASSO AND THE RECTANGLE (one routine: a rectangle is a four-point
    /// lasso). Takes every element the geometry encloses that the SELECTION
    /// gate passes - <see cref="Catchable"/>, the same gate a click uses, so the
    /// two gestures of the one tool cannot disagree about what is selectable
    /// (16.10 / 17.10), and a 0% layer is refused by both (58.10 ruling A).
    ///
    /// <para><paramref name="strokeCand"/> / <paramref name="shapeCand"/> are the
    /// spatial index's cheap rejections, asked first; null means "no index,
    /// consider everything". The gate is asked before the geometry.</para>
    /// </summary>
    public static void Lasso(NotePage page, bool ignoreLocked, LayerScope scope,
                             Func<PenStroke, bool>? strokeCand, Func<PenStroke, bool> strokeIn,
                             Func<ShapeElement, bool>? shapeCand, Func<ShapeElement, bool> shapeIn,
                             Func<TextElement, bool> textIn,
                             ICollection<PenStroke> strokesOut, ICollection<ShapeElement> shapesOut,
                             ICollection<TextElement> textsOut)
    {
        foreach (var s in page.Strokes)
        {
            if (s.Points.Count == 0) continue;
            if (strokeCand != null && !strokeCand(s)) continue;
            if (!Catchable(page, s.LayerKey, s.Locked, ignoreLocked, scope)) continue;
            if (strokeIn(s)) strokesOut.Add(s);
        }
        foreach (var sh in page.Shapes)
        {
            if (shapeCand != null && !shapeCand(sh)) continue;
            if (!Catchable(page, sh.LayerKey, sh.Locked, ignoreLocked, scope)) continue;
            if (shapeIn(sh)) shapesOut.Add(sh);
        }
        foreach (var t in page.Texts)
        {
            if (!Catchable(page, t.LayerKey, t.Locked, ignoreLocked, scope)) continue;
            if (textIn(t)) textsOut.Add(t);
        }
    }
}
