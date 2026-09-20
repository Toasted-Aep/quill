namespace Quill.Services;

/// <summary>CONCEPTS-REF 50: WHETHER A TEXT BOX'S LIVE DOCUMENT MAY BE WRITTEN
/// OVER THE STORED ONE.
///
/// <para><b>The defect this exists to close.</b> <c>InkSurface.FlushTexts</c>
/// stored <c>Document.GetText(FormatRtf)</c> into <c>TextElement.Rtf</c> for
/// every box on the page, unconditionally, at some thirty call sites — every
/// save, every undo, every page change, every export, and on close. Windows'
/// RTF writer is <b>not</b> idempotent: <c>SetText(FormatRtf, x)</c> followed by
/// <c>GetText(FormatRtf)</c> returns <c>x</c> with ONE more empty paragraph —
/// the literal six characters <c>\par\r\n</c>. Storing that back made the extra
/// paragraph part of <c>x</c>, so the next open added another, forever. 49.7
/// measured the ladder on one box: 264 → 294 → 306 → 312 characters, every step
/// a multiple of six, two of them with nothing in the app touched at all.</para>
///
/// <para><b>Measured across the real library, not inferred.</b> All 106 stored
/// documents end in a run of empty paragraphs — 23 of them carry 47, 21 carry 7,
/// 13 carry 43, 9 carry 39. Boxes that share a page carry identical counts,
/// which is the signature of a per-session increment and not of anybody pressing
/// Return. 17,346 of the 83,195 stored RTF characters (20.8%) are empty
/// paragraphs past the first.</para>
///
/// <para><b>Why this is a rule about WRITING and not a rule about RTF.</b> The
/// growth could also be removed by normalising the document on the way out —
/// trimming the paragraph the control added before storing it. That was
/// rejected. Normalising means a second RTF parser to keep correct against every
/// document Windows can produce, it is a new way to lose formatting nobody
/// modelled, and it puts bytes on disk that no control ever emitted. The
/// question the defect actually asks is narrower and has an honest answer:
/// <i>why is a document nobody edited being written back at all?</i> So Quill
/// either stores exactly what the control produced, or it stores nothing.</para>
///
/// <para><b>The two facts a caller has to supply.</b>
/// <list type="bullet">
/// <item><description><c>reached</c> — the box has been focused, or Quill has
/// edited it on the user's behalf, since it was built. Focus is the gate every
/// edit path in the app goes through: typing, paste, dictation, the AI rewrite,
/// the symbol picker and every one of the format bar's controls reach a box only
/// as <c>InkSurface.ActiveTextBox</c> or <c>LastTextBox</c>, and both of those
/// are assigned in <c>GotFocus</c> and nowhere else. A box that was only
/// displayed cannot have changed.</description></item>
/// <item><description><c>documentWhenReached</c> — the control's own
/// serialisation, captured at the moment it was reached. Captured THERE and not
/// at build time on purpose: 40.5/43.1 established that the template pushes
/// <c>RichEditBox.Foreground</c> into the document after <c>Loaded</c>, so a
/// baseline taken during <c>BuildTextUi</c> would differ from the settled
/// document and report a change nobody made. A person cannot focus a box that
/// has not finished loading, so the moment of reach is after the
/// flatten.</description></item></list></para>
///
/// <para><b>What it costs, said plainly.</b> A box that is focused and then left
/// alone is compared byte for byte and still not written. A box that IS edited
/// is written, and carries the one paragraph the control added along with the
/// edit — that paragraph is the control's, not Quill's, and removing it is the
/// normalising this rule declines to do. So growth becomes one paragraph per
/// session in which the note was actually changed, instead of one per session in
/// which it was merely opened.</para>
///
/// <para><b>What it does NOT do.</b> Nothing here prunes. The 17,346 characters
/// the 106 stored notes have already accumulated stay exactly where they are;
/// the fix stops the ladder for existing notes on their very next open, and
/// removes nothing that is already on disk. Rewriting 106 stored documents to
/// tidy them would be the same unasked-for write this rule exists to
/// stop.</para>
///
/// <para>Kept as plain static functions over strings, with no reference to
/// <c>RichEditBox</c> or to WinUI at all, so <c>tools/TextColourRoundTrip</c>
/// can link the shipping decision instead of restating it. None of the ten
/// harnesses can link <c>InkSurface.cs</c>; this is the half of the fix that can
/// be measured, and it is the half that holds the
/// judgement.</para></summary>
public static class TextFlushPolicy
{
    /// <summary>Whether the caller has to ask the control for its document at
    /// all. False is the common path — a page of boxes nobody has been near
    /// costs no <c>GetText</c> and no allocation, where before this rule it cost
    /// one full RTF serialisation per box per flush.</summary>
    public static bool NeedsTheDocument(string? stored, bool reached, bool touched) =>
        string.IsNullOrEmpty(stored) || touched || reached;

    /// <summary>The decision itself.
    ///
    /// <para>Order matters and each refusal is separate:</para>
    /// <list type="number">
    /// <item><description>A box with <b>nothing stored</b> is always written. A
    /// brand-new bubble has an empty <c>Rtf</c> and its first words must reach
    /// disk even if every latch below missed it. This is the safety net, and it
    /// is first so that no other clause can shadow it.</description></item>
    /// <item><description>A box Quill has <b>touched</b> on the user's behalf is
    /// always written — 25.5's whole-box recolour stamps the live document after
    /// a rebuild, when no focus is involved, and the stored copy must keep up
    /// with the field.</description></item>
    /// <item><description>A box <b>nothing has reached</b> is never written. This
    /// is the fixed point: the stored document is returned unchanged across any
    /// number of loads and saves.</description></item>
    /// <item><description>A box that was reached is written only if its document
    /// is <b>not the one it was reached with</b>, compared ordinally and in
    /// full. Any difference at all counts — a character, a bold run, an
    /// alignment, a link <c>LinkifyBox</c> added on the way out — because the
    /// comparison is on the bytes and not on anything's reading of
    /// them.</description></item></list></summary>
    public static bool ShouldWriteBack(
        string? stored, bool reached, bool touched, string? documentWhenReached, string live)
    {
        if (string.IsNullOrEmpty(stored)) return true;
        if (touched) return true;
        if (!reached) return false;
        // Reached but with no baseline recorded: the capture failed or was
        // skipped, so fall back to the behaviour that predates this rule rather
        // than to silence. Losing an edit is worse than writing a paragraph.
        if (documentWhenReached is null) return true;
        return !string.Equals(documentWhenReached, live, System.StringComparison.Ordinal);
    }
}
