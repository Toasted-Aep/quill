using Microsoft.UI.Text;

namespace Quill.Services;

/// <summary>CONCEPTS-REF 56: the trim itself, the half of "trim on next edit
/// only" that has to touch the live document.
///
/// <para>Lives in its own file, over <see cref="RichEditTextDocument"/> and
/// nothing else of WinUI, so that a harness can compile THIS file and drive it
/// against the RichEdit engine Quill ships (<c>WinUIEdit.dll</c>) instead of
/// against a restatement of it. <c>InkSurface.FlushTexts</c> calls it with
/// <c>box.Document</c>; before 56.8 it was a private method of
/// <c>InkSurface</c>, which no harness can link.</para></summary>
public static class TextTrim
{
    /// <summary>§56: deletes a released box's trailing empty paragraphs past
    /// the first THROUGH ITS OWN DOCUMENT MODEL and returns the control's own
    /// serialisation of the result, or null when nothing was dropped.
    ///
    /// <para>No RTF is edited as a string: the characters to delete are chosen
    /// by <see cref="TextFlushPolicy.EmptyParagraphMarksToDrop"/> over the
    /// story's plain text, deleted with <c>ITextRange</c>, and what is stored is
    /// whatever <c>GetText(FormatRtf)</c> then says — bytes the control emitted,
    /// which is §50.2's condition.</para>
    ///
    /// <para>Every step refuses rather than guesses: the story's text must map
    /// one character to one position (no hidden or object text that would
    /// shift the indices), the range must be nothing but paragraph marks
    /// immediately before it is deleted, and afterwards the story must read
    /// exactly as the old text with that range removed. If the last check
    /// fails the document is put back from the serialisation read at the start
    /// of this flush — the box is being released, so the only cost is §50's
    /// one extra paragraph, and nothing the user wrote is lost.</para>
    ///
    /// <para><b>56.8, TABLES.</b> The range function sees only plain text and
    /// cannot tell a table's marks from paragraph marks; were a cell mark or a
    /// row end a bare carriage return, it would delete an empty cell and a row
    /// end. Quill writes no table RTF, but MainWindow's two
    /// <c>Document.Selection.Paste(0)</c> calls have no sanitiser in front of
    /// them. So a box with table structure is not trimmed at all, and two
    /// independent refusals say so: the control's own serialisation is put to
    /// <see cref="TextFlushPolicy.ContainsTableStructure"/> before anything
    /// else, and the story's plain text to
    /// <see cref="TextFlushPolicy.PlainTextShowsTable"/> before any range is
    /// chosen. Both are tests over a string, never a parse and never an
    /// edit.</para>
    ///
    /// <para><b>56.8, THE SURVIVOR'S FORMATTING.</b> The mark that has to
    /// survive is the story's last, but the blank line the USER left is the
    /// FIRST of the trailing run. Its paragraph formatting, and its mark's
    /// character formatting, are captured before the delete and copied onto the
    /// survivor after it - and only when they differ, so the ordinary case puts
    /// no extra edit through the control. The copy is verified exactly as the
    /// delete is, with <c>IsEqual</c> and an unchanged plain text; if it does
    /// not take, the document goes back and the edit is stored
    /// untrimmed.</para>
    ///
    /// <para><b>56.9, LIST ITEMS.</b> An empty list item shows a bullet or a
    /// number; the section 50 growth never creates one. So the engine is asked,
    /// mark by mark, which paragraphs from the range's start onward are list
    /// items (<c>ParagraphFormat.ListType</c>), and the range is cut to the
    /// marks after the last of them
    /// (<see cref="TextFlushPolicy.EmptyParagraphMarksToDrop(string?, IEnumerable{int}?)"/>).
    /// Plain empty paragraphs after an empty list item may still go; the item
    /// never does.</para></summary>
    public static string? TrimTrailingEmptyParagraphs(RichEditTextDocument doc)
    {
        string before;
        try { doc.GetText(TextGetOptions.FormatRtf, out before); }
        catch { return null; }
        // 56.8: TABLES. EmptyParagraphMarksToDrop cannot tell a table's marks
        // from paragraph marks - driven with a story whose cell mark and row
        // end are '\r' it answers with the empty cell AND the row end, and
        // deleting those would destroy the table rather than drop blank lines.
        // This is a gate and nothing else:
        // a substring test over the string the control has just produced. Any
        // doubt stores the untrimmed edit, which is 50's own behaviour.
        if (TextFlushPolicy.ContainsTableStructure(before)) return null;
        try
        {
            var story = doc.GetRange(0, 0);
            story.Expand(TextRangeUnit.Story);
            story.GetText(TextGetOptions.None, out string plain);
            if (story.StartPosition != 0 || story.EndPosition - story.StartPosition != plain.Length) return null;
            // 56.8: the second table refusal, over the very string the range is
            // taken from - on this engine a row reads U+FFF9 CR ... U+0007 ...
            // U+FFFB CR. Independent of the RTF gate above; either refuses.
            if (TextFlushPolicy.PlainTextShowsTable(plain)) return null;
            var (start, length) = TextFlushPolicy.EmptyParagraphMarksToDrop(plain);
            if (length <= 0) return null;
            // 56.9: LIST ITEMS ARE THE USER'S. An empty list item shows a bullet
            // or a number, and the section 50 growth never makes one, so the
            // run that may go stops at the last list item. The engine is asked
            // about every mark from the range's start to the story's end; any
            // answer but None (Undefined included) counts as a list item.
            var listItemMarks = new List<int>();
            for (int i = start; i < plain.Length; i++)
                if (plain[i] == '\r' && doc.GetRange(i, i + 1).ParagraphFormat.ListType != MarkerType.None)
                    listItemMarks.Add(i);
            (start, length) = TextFlushPolicy.EmptyParagraphMarksToDrop(plain, listItemMarks);
            if (length <= 0) return null;

            var cut = doc.GetRange(start, start + length);
            cut.GetText(TextGetOptions.None, out string doomed);
            if (doomed != new string('\r', length)) return null;

            // The paragraph that survives at the end must still be the one that
            // was there: its paragraph formatting and its mark's character
            // formatting are captured now and compared after the delete. Which
            // mark RichEdit keeps when marks are deleted is the engine's
            // behaviour: tools/TrimEngineProof measured that it keeps the last,
            // but an engine not measured there is checked here, not assumed.
            int last = plain.Length - 1;
            var finalBefore = doc.GetRange(last, last + 1);
            var paraBefore = finalBefore.ParagraphFormat.GetClone();
            var charBefore = finalBefore.CharacterFormat.GetClone();

            // 56.8: and the formatting of the FIRST trailing empty paragraph -
            // the range's own Start, which is the blank line the user typed if
            // any of them is theirs. The survivor is forced (the story's final
            // mark cannot be deleted); its formatting is not.
            var firstEmpty = doc.GetRange(start, start + 1);
            var paraFirst = firstEmpty.ParagraphFormat.GetClone();
            var charFirst = firstEmpty.CharacterFormat.GetClone();

            cut.Text = string.Empty;

            var check = doc.GetRange(0, 0);
            check.Expand(TextRangeUnit.Story);
            check.GetText(TextGetOptions.None, out string after);
            int lastAfter = after.Length - 1;
            var finalAfter = lastAfter >= 0 ? doc.GetRange(lastAfter, lastAfter + 1) : null;
            if (after != plain.Remove(start, length) || finalAfter == null ||
                !finalAfter.ParagraphFormat.IsEqual(paraBefore) ||
                !finalAfter.CharacterFormat.IsEqual(charBefore))
            {
                try { doc.SetText(TextSetOptions.FormatRtf, before); } catch { }
                return null;
            }
            // 56.8: the survivor now carries the LAST trailing mark's
            // formatting (the check above proves the control kept it). What the
            // user set on their own blank line is the FIRST one's, so it is
            // copied across - only when the two differ, so the ordinary case
            // makes no further edit - and then verified the same way: IsEqual
            // on both formats, and a plain text that has not moved. If it does
            // not take, the document goes back and the edit is stored
            // untrimmed, which is 50's behaviour and loses nothing the user
            // wrote.
            if (!finalAfter.ParagraphFormat.IsEqual(paraFirst) ||
                !finalAfter.CharacterFormat.IsEqual(charFirst))
            {
                finalAfter.ParagraphFormat = paraFirst;
                finalAfter.CharacterFormat = charFirst;
                var recheck = doc.GetRange(0, 0);
                recheck.Expand(TextRangeUnit.Story);
                recheck.GetText(TextGetOptions.None, out string afterCopy);
                var copied = afterCopy.Length == after.Length && after.Length > 0
                    ? doc.GetRange(after.Length - 1, after.Length)
                    : null;
                if (afterCopy != after || copied == null ||
                    !copied.ParagraphFormat.IsEqual(paraFirst) ||
                    !copied.CharacterFormat.IsEqual(charFirst))
                {
                    try { doc.SetText(TextSetOptions.FormatRtf, before); } catch { }
                    return null;
                }
            }
            doc.GetText(TextGetOptions.FormatRtf, out string trimmed);
            return trimmed;
        }
        catch
        {
            return null;
        }
    }
}
