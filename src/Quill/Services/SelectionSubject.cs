using System;
using Windows.UI;

namespace Quill.Services;

/// <summary>What KIND of thing the selection is. CONCEPTS-REF 16.9: one
/// selection presentation, three kinds of subject.</summary>
public enum SubjectKind
{
    None,
    /// <summary>An image placed on the page - 16.2's original subject.</summary>
    Attachment,
    /// <summary>A typed text box.</summary>
    Text,
    /// <summary>One or more drawn strokes - 16.9's addition.</summary>
    Ink,
    /// <summary>More than one kind at once. Supports only what they all support.</summary>
    Mixed,
}

/// <summary>
/// THE SELECTION, DESCRIBED BY WHAT IT SUPPORTS (CONCEPTS-REF 16.3 / 16.9).
///
/// <para><b>This type exists to stop one specific mistake.</b> 16.3 is easy to
/// read as "something is selected, so grey the dial", and that reading is wrong
/// in a way 16.9 then has to correct: a selected STROKE leaves the dial live and
/// populated with its own size, stability, opacity and colour, because a stroke
/// genuinely has all four. The rule is <b>a subject that LACKS a property greys
/// that property's control</b> - never "a selection exists". So the flags below
/// are per-property, the dial and the pen row ask this object rather than asking
/// whether anything is selected, and adding a fourth kind of subject means
/// filling in its flags rather than editing every control.</para>
///
/// <para>An attachment answers: no pen size, no stability, cannot be recoloured;
/// opacity yes. Undo and redo are not here at all, deliberately - 16.3 keeps
/// them live for every subject because they are page-level commands rather than
/// properties of the thing selected, so there is nothing for a subject to
/// report about them.</para>
///
/// <para><b>The per-pen colour arcs are also not here</b>, and that is the point.
/// 16.3: "Do NOT grey the per-pen colour arcs on the ring... those arcs report
/// which colour each pen carries; that fact is still true while an attachment is
/// selected, and greying it would destroy information rather than disable a
/// control." A control that reports rather than sets has nothing to ask this
/// object, so it never does.</para>
/// </summary>
public sealed class SelectionSubject
{
    public static readonly SelectionSubject None = new();

    public SubjectKind Kind { get; init; } = SubjectKind.None;
    /// <summary>How many things are selected, across all kinds.</summary>
    public int Count { get; init; }

    public bool Any => Kind != SubjectKind.None;

    // ---- what the subject HAS -------------------------------------------
    // False greys that control. Nothing else may.

    public bool HasPenSize { get; init; }
    public bool HasStability { get; init; }
    public bool HasOpacity { get; init; }
    /// <summary>False sends the colour circle WHITE and inert, in the dial and
    /// in the pen row both (16.3). You cannot recolour a photograph, and a
    /// live-looking colour control that silently does nothing is worse than one
    /// that says so.</summary>
    public bool CanRecolour { get; init; }

    // ---- what the subject READS -----------------------------------------
    // 16.9: the dial shows "that stroke's own values", not the active pen's.
    // Null where the selection disagrees with itself - a mixed bag of sizes has
    // no single size to show, which is a different state from having none.

    public float? Size { get; init; }
    public float? Stability { get; init; }
    public float? Opacity { get; init; }
    public Color? Ink { get; init; }

    // ---- writing back ----------------------------------------------------
    // 16.9: "the controls stay usable and EDITING THEM EDITS THE SELECTION."
    // Null means the dial may show the value but must not offer to change it.

    public Action<float>? SetSize { get; init; }
    public Action<float>? SetStability { get; init; }
    public Action<float>? SetOpacity { get; init; }
    public Action<Color>? SetInk { get; init; }

    // ---- 16.7 ------------------------------------------------------------

    /// <summary>Whether the PAGE de-emphasises behind this subject (16.7). True
    /// for an attachment, which is the case the reference actually shows and the
    /// only one the user asked for; a selected stroke does not fade the page it
    /// is part of. See <c>InkSurface.Veil</c> for why this is asked at draw time
    /// and can never reach stored colour.</summary>
    public bool FadesPage { get; init; }

    /// <summary>Value equality on everything the surfaces render from, so
    /// <see cref="SelectionState.Set"/> can drop a no-op rather than repaint the
    /// dial on every pointer move. The delegates are deliberately excluded: they
    /// are freshly allocated closures on each publish and would make every
    /// comparison false, which is exactly the repaint storm this avoids.</summary>
    public bool SameAs(SelectionSubject o) =>
        Kind == o.Kind && Count == o.Count &&
        HasPenSize == o.HasPenSize && HasStability == o.HasStability &&
        HasOpacity == o.HasOpacity && CanRecolour == o.CanRecolour &&
        FadesPage == o.FadesPage &&
        Nullable.Equals(Size, o.Size) && Nullable.Equals(Stability, o.Stability) &&
        Nullable.Equals(Opacity, o.Opacity) && Nullable.Equals(Ink, o.Ink);
}

/// <summary>The one place the app asks what is selected. Static and event-based
/// like <see cref="ColorPickerService.Obstructing"/> and <c>PageTheme</c>, which
/// is how every other piece of cross-surface state in this app is published -
/// the alternative is threading a reference to InkSurface through the dial, the
/// pen row and the selection chrome, and then through whatever gets a fourth
/// opinion about the selection next.</summary>
public static class SelectionState
{
    public static SelectionSubject Current { get; private set; } = SelectionSubject.None;

    /// <summary>Raised only when something a surface renders from has actually
    /// changed.</summary>
    public static event Action? Changed;

    public static void Set(SelectionSubject s)
    {
        s ??= SelectionSubject.None;
        if (Current.SameAs(s))
        {
            // Same picture, fresh closures: keep the newer delegates (they close
            // over the current page and element list) without waking anyone.
            Current = s;
            return;
        }
        Current = s;
        try { Changed?.Invoke(); } catch { }
    }

    public static void Clear() => Set(SelectionSubject.None);
}
