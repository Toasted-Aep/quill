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
/// <para><b>§56 added the pruning, on edit only.</b> The product owner ruled
/// "trim on next edit only": <see cref="MayTrim"/> and
/// <see cref="EmptyParagraphMarksToDrop"/> let a box the user actually edited
/// drop its trailing empty paragraphs past the first when it is released. A
/// note nobody edits is still never written; the paragraph above remains true
/// of every such note.</para>
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

    // =======================================================================
    // CONCEPTS-REF 56: TRIM ON NEXT EDIT ONLY
    // =======================================================================

    /// <summary>CONCEPTS-REF 56: whether this write-back is one the trailing
    /// empty paragraphs may be dropped on.
    ///
    /// <para><b>The ruling.</b> The product owner's words were "trim on next edit
    /// only": when the user actually edits a box, the trailing empty paragraphs
    /// past the first are dropped as it saves; a note nobody edits is never
    /// rewritten, so there is no migration of the library. §50 stopped the growth
    /// and deliberately pruned nothing; this is the pruning, and it is confined
    /// to the one moment a box is being written for a reason the user
    /// gave.</para>
    ///
    /// <para>True only when ALL of these hold:</para>
    /// <list type="number">
    /// <item><description><b>The box is being released</b> — its live control is
    /// about to be torn down (a text-layer rebuild, a page switch, the window
    /// closing). The trim edits the live document, and doing that to a box that
    /// outlives the flush would put a deletion on the control's own undo stack
    /// (one Ctrl+Z inside the box would bring the paragraphs back) and would
    /// take blank lines out from under a caret that may be sitting on them.
    /// A released box has no caret to move and no undo history anyone can
    /// reach again, so the trim waits for that moment instead of restoring
    /// either.</description></item>
    /// <item><description><b>An edit is established</b> — Quill touched the box
    /// on the user's behalf (25.5's recolour), or the box was reached WITH a
    /// recorded baseline and the live document differs from it. A box that was
    /// only focused is not an edit (§50.4's 5d) and is not trimmed; a reach
    /// whose baseline could not be captured is written back by
    /// <see cref="ShouldWriteBack"/> as a fallback, but nothing established that
    /// it was edited, so it is not trimmed either. A box with nothing stored
    /// is trimmed only if one of the two edit facts also holds.</description></item>
    /// </list>
    /// <para>Deliberately NOT a second copy of <see cref="ShouldWriteBack"/>'s
    /// order: it asks a narrower question, and every case it answers true is a
    /// case <see cref="ShouldWriteBack"/> also answers true.</para></summary>
    public static bool MayTrim(
        string? stored, bool reached, bool touched, string? documentWhenReached, string live, bool releasing)
    {
        if (!releasing) return false;
        if (touched) return true;
        if (!reached || documentWhenReached is null) return false;
        return !string.Equals(documentWhenReached, live, System.StringComparison.Ordinal);
    }

    /// <summary>CONCEPTS-REF 56: which paragraph marks at the end of a document
    /// are the empty paragraphs to drop, given the document's plain text as the
    /// control reports it (<c>ITextRange.GetText(TextGetOptions.None)</c> over
    /// the whole story, where a paragraph mark is <c>'\r'</c>).
    ///
    /// <para>Returns a character range <c>(Start, Length)</c> in that string;
    /// <c>Length == 0</c> means drop nothing. The caller deletes exactly that
    /// range through the control's own document (<c>ITextRange</c>), so the
    /// bytes stored afterwards are still the control's own serialisation — no
    /// RTF is edited as a string anywhere, which is §50.2's objection to a
    /// normaliser met rather than argued with.</para>
    ///
    /// <para><b>What is kept, exactly.</b> The trailing run of <c>'\r'</c> is
    /// the only thing ever touched; the first character that is not a paragraph
    /// mark — a letter, a space, a line break (<c>'\v'</c>), an embedded object
    /// — ends the run, so every interior blank line survives. Of the run:</para>
    /// <list type="bullet">
    /// <item><description>When the box has content, the content's own paragraph
    /// mark (the first <c>'\r'</c> of the run) and the story's final mark (the
    /// last character) are kept: the content, then ONE empty paragraph — "past
    /// the first", as ruled. Everything between is dropped.</description></item>
    /// <item><description>When the box is nothing but empty paragraphs, only the
    /// final mark is kept: one empty paragraph, which is what an empty box is.
    /// The final mark is never inside the range, so the result is always a
    /// valid document.</description></item></list>
    ///
    /// <para><b>What <c>Start</c> also means.</b> The first character of the
    /// range is the mark of the FIRST trailing empty paragraph - the blank line
    /// the user typed, if one of them is theirs - while the mark that survives
    /// is the story's last. They are different paragraphs, so the survivor's
    /// formatting is not the first one's; <c>TextTrim</c> copies it across
    /// after the delete (section 56.8).</para>
    ///
    /// <para><b>What this function cannot see, and the caller must.</b> It
    /// does not know whether a <c>'\r'</c> belongs to a table. On the engine
    /// Quill ships a row reads U+FFF9 CR ... U+0007 ... U+FFFB CR (cell marks
    /// are U+0007, measured in tools/TrimEngineProof), but a story in which a
    /// cell mark or row end is a bare <c>'\r'</c> would be taken for empty
    /// paragraphs. The refusal for tables therefore does not live here; it
    /// lives in <see cref="ContainsTableStructure"/> (over the RTF) and
    /// <see cref="PlainTextShowsTable"/> (over this same plain text), and
    /// <c>TextTrim</c> asks both before it asks this.</para>
    ///
    /// <para><b>Refusal is the failure mode.</b> Text that does not end in a
    /// paragraph mark is a shape this function does not understand, and it
    /// answers "drop nothing". If the control's string ever left out the
    /// story's final mark, the last <c>'\r'</c> seen here would be an empty
    /// paragraph and the one after it would survive too: one paragraph kept too
    /// many, never the content's mark taken.</para></summary>
    public static (int Start, int Length) EmptyParagraphMarksToDrop(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain[^1] != '\r') return (plain?.Length ?? 0, 0);
        int run = 0;
        while (run < plain.Length && plain[plain.Length - 1 - run] == '\r') run++;
        bool hasContent = run < plain.Length;
        int keep = hasContent ? 2 : 1;
        int drop = run - keep;
        if (drop <= 0) return (plain.Length, 0);
        int start = plain.Length - 1 - drop;   // the final mark, at Length-1, stays
        return (start, drop);
    }

    /// <summary>CONCEPTS-REF 56 round 2: whether the control's own
    /// serialisation shows TABLE STRUCTURE - in which case nothing is trimmed
    /// at all.
    ///
    /// <para><b>Why a document Quill itself cannot build still has to be
    /// refused.</b> <see cref="EmptyParagraphMarksToDrop"/> is handed nothing
    /// but the story's plain text. If a table's CELL marks and ROW-END marks
    /// read there as carriage returns, like paragraph marks - the shape the
    /// round-2 finding was written against - then a story of <c>cell_one</c>
    /// followed by four carriage returns (the filled cell's mark, an empty
    /// cell's mark, the row end and the story's final mark) gets the answer
    /// <c>(9, 2)</c>, which is the empty cell AND the row end. Deleting those
    /// would not drop blank lines, it would destroy the table, which is exactly
    /// the kind of loss section 56 promises cannot happen. The engine Quill
    /// ships was measured to write cells as U+0007 instead (tools/TrimEngineProof,
    /// section 56.8), which that range never reaches in the shapes measured -
    /// but an unmeasured shape or engine is refused, not reasoned about. Quill models no RTF
    /// table of its own (no <c>\trowd</c>, <c>\cell</c>, <c>\row</c> or
    /// <c>\intbl</c> anywhere in src/Quill), but <c>MainWindow</c>'s two
    /// <c>Document.Selection.Paste(0)</c> calls have no sanitiser in front of
    /// them, so a table pasted from Word or a browser is reachable.</para>
    ///
    /// <para><b>A GATE, not a parser.</b> This is a refusal test over the
    /// string the control itself just produced. It never parses, never edits
    /// and never stores anything, so section 50.2's objection to a second RTF
    /// parser does not arise: a second parser is a thing that has to stay
    /// CORRECT about every document Windows can write, and this only has to
    /// stay SUSPICIOUS. No per-paragraph answer was available - the
    /// <c>Microsoft.UI.Text</c> winmd of the Windows App SDK 1.8 this project
    /// builds against carries no identifier matching table, cell, row or nest
    /// (the nearest is <c>TabLeader</c>), so neither <c>ITextRange</c> nor
    /// <c>ITextParagraphFormat</c> can be asked whether a paragraph sits in a
    /// table. What the range DOES expose, the story's plain text, is the second
    /// refusal: <see cref="PlainTextShowsTable"/>.</para>
    ///
    /// <para><b>Conservative on purpose: any doubt means do not trim.</b> Plain
    /// ordinal substring matching, so a literal <c>\cell</c> the user TYPED
    /// (RTF writes it <c>\\cell</c>, which contains it) trips the gate too. A
    /// false positive costs one un-trimmed edit - section 50's behaviour, which
    /// is the fallback this whole design already stands on - and a false
    /// negative would cost a table. <c>\itap</c> is deliberately NOT in the
    /// list: <c>\itap0</c> is a declaration of NOT being in a table and a
    /// writer may emit it unconditionally, which would silently disable the
    /// trim everywhere instead of only over tables.</para></summary>
    public static bool ContainsTableStructure(string? rtf)
    {
        if (string.IsNullOrEmpty(rtf)) return false;
        foreach (string word in TableWords)
            if (rtf.Contains(word, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The control words only table structure produces. <c>\cell</c>
    /// also catches <c>\cellx</c>, a cell boundary; the three <c>\nest</c>
    /// words are nested tables, which carry their own vocabulary.</summary>
    private static readonly string[] TableWords =
    {
        @"\intbl", @"\trowd", @"\cell", @"\row",
        @"\nestcell", @"\nestrow", @"\nesttableprops",
    };

    /// <summary>CONCEPTS-REF 56.8: the second, independent table refusal -
    /// whether the story's PLAIN TEXT (<c>ITextRange.GetText(None)</c> over the
    /// whole story, the same string <see cref="EmptyParagraphMarksToDrop"/> is
    /// given) carries the characters the RichEdit engine uses for table
    /// structure.
    ///
    /// <para>Measured, not assumed: tools/TrimEngineProof drives
    /// <c>WinUIEdit.dll</c> - the engine behind every <c>RichEditBox</c> in the
    /// app - and on it a table row reads U+FFF9, CR, the cells each ended by
    /// U+0007, then U+FFFB, CR; a nested table reads the same way inside its
    /// cell. U+FFFA, the third of the interlinear-annotation characters, is in
    /// the set because it belongs with the other two, not because it was seen.
    /// U+0007 is BEL, which nobody types into a note.</para>
    ///
    /// <para>Why two refusals and not one. <see cref="ContainsTableStructure"/>
    /// reads the control's serialisation; this reads the story the range is
    /// actually taken from. Either one alone refuses every table shape measured,
    /// and each would still refuse if the other were ever wrong about a writer
    /// or an engine not measured here. Both are gates - neither parses, edits
    /// or stores anything - and a false positive costs one un-trimmed edit,
    /// which is section 50's behaviour.</para></summary>
    public static bool PlainTextShowsTable(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return false;
        foreach (char c in plain)
            if (c == '\uFFF9' || c == '\uFFFA' || c == '\uFFFB' || c == '\u0007') return true;
        return false;
    }
}
