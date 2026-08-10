using Quill.Helpers;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Quill.Controls;

/// <summary>
/// The top-bar commands that MOVE INTO THE RADIAL DIAL as assignable slots
/// (UI-SPEC-V3 I: "every remaining top-bar feature moves into the radial dial as
/// a selectable tool"). The dial already knew undo, redo and mouse-mode; these
/// are the rest of the bar's marking commands, declared once here so MainWindow
/// only has to hand over four delegates.
///
/// <para><b>What moved, and why:</b></para>
/// <list type="bullet">
/// <item><b>Comment</b> — a page-marking mode, exactly like the eraser: you
/// point at the page and it leaves something behind. Since V3 K.17 the slot
/// opens the Comments TOOL WINDOW rather than flipping the mode blind: the
/// window carries the mode switch, the pin-visibility switch and every comment
/// on the page, so the dial selects a surface instead of a hidden state.</item>
/// <item><b>Touch draw</b> — NO LONGER HERE. V3 K.14 moved it into
/// Settings ▸ Interaction as an on/off toggle: it decides what a finger does
/// for the whole session, which is a preference rather than a per-stroke
/// command, and it was burning one of only ten slots to say so.</item>
/// <item><b>Insert shape</b> — a marking command, and its whole menu comes with
/// it rather than being redeclared.</item>
/// <item><b>Edit history</b> — scoped to the ink on this page (and it carries
/// stroke-by-stroke replay), so it reads as part of the drawing surface. Since
/// 11.5 item 34 the slot OPENS THE RIGHT-DOCKED HISTORY PANEL rather than the
/// top bar's flyout, which no longer exists.</item>
/// <item><b>Mouse mode</b> — already a dial command; it only gained its
/// top-bar hand-back key here.</item>
/// </list>
///
/// <para><b>What did NOT move, and why.</b> The AI assistant, voice dictation,
/// audio recording and the calculator are not marking tools: none of them
/// changes what the pen does, and each would burn one of only ten slots that a
/// pen, an eraser or a colour wants. They stay on the hamburger and the command
/// palette. Zoom, export and page settings are not here either — the two
/// floating bars carry those now (<see cref="ChromeBars"/>), so putting them in
/// a slot as well would offer the same command three times.</para>
/// </summary>
public static class DialCommands
{
    public sealed class Host
    {
        /// <summary>Opens the Comments tool window (V3 K.17). It is no longer a
        /// bare mode toggle: the window carries the mode switch, the pin
        /// visibility switch and the page's own comments.</summary>
        public required Action OpenComments { get; init; }
        public required Func<bool> CommentsActive { get; init; }
        /// <summary>The existing top-bar menus, handed over whole — the dial
        /// opens them on itself rather than on the button they came from.</summary>
        public required Func<FlyoutBase?> ShapeMenu { get; init; }
        /// <summary>CONCEPTS-REF 11.5 item 34 / 11.20 item 14: history is no
        /// longer a flyout the dial can borrow from a top-bar button, because
        /// that button is gone. It is a floating panel docked to the right of the
        /// screen, so the dial RUNS a command instead of opening a menu - and
        /// reports whether the panel is up, so the cell reads as active while it
        /// is, exactly like Comments.</summary>
        public required Action OpenHistory { get; init; }
        public required Func<bool> HistoryActive { get; init; }
        /// <summary>Reference 9.7: dictation and audio recording stop being
        /// top-bar buttons and become SELECTABLE TOOLS, assignable to a dial
        /// sector or a pen-row cell like any other. Both are toggles, so each
        /// reports whether it is currently running.</summary>
        public required Action ToggleDictation { get; init; }
        public required Func<bool> DictationActive { get; init; }
        public required Action ToggleRecording { get; init; }
        public required Func<bool> RecordingActive { get; init; }
    }

    public static IReadOnlyList<ToolWheel.ExtraCommand> Build(Host h) => new[]
    {
        new ToolWheel.ExtraCommand
        {
            Id = "Comment",
            Label = Loc.T("Wheel.Cmd.Comment"),
            Icon = Icons.Comment,
            TopBarKey = "ToolComment",
            IsActive = h.CommentsActive,
            Run = h.OpenComments,
        },
        new ToolWheel.ExtraCommand
        {
            Id = "Shape",
            Label = Loc.T("Wheel.Cmd.Shape"),
            Icon = Icons.Shape,
            TopBarKey = "ShapeBtn",
            Flyout = h.ShapeMenu,
        },
        new ToolWheel.ExtraCommand
        {
            Id = "Dictate",
            Label = Loc.T("Wheel.Cmd.Dictate"),
            Icon = Icons.Microphone,
            // Deliberately NO TopBarKey. Both of these live under one top-bar
            // control - the Voice dropdown, "VoiceBtn" - so handing that key back
            // because ONE of them was placed would take the other one off screen
            // with it and leave it unreachable. The bar sheds the dropdown when
            // section 9.6 strips it, which is not this file's call to make.
            IsActive = h.DictationActive,
            Run = h.ToggleDictation,
        },
        new ToolWheel.ExtraCommand
        {
            Id = "Record",
            Label = Loc.T("Wheel.Cmd.Record"),
            Icon = Icons.Record,
            IsActive = h.RecordingActive,
            Run = h.ToggleRecording,
        },
        new ToolWheel.ExtraCommand
        {
            Id = "History",
            Label = Loc.T("Wheel.Cmd.History"),
            Icon = Icons.History,
            // Deliberately NO TopBarKey. The hand-back exists so a command placed
            // in a dial slot stops being offered twice; BtnHistory no longer
            // exists to hand anything back to, and naming a dead key would have
            // the visibility filter looking for a control that is not there.
            IsActive = h.HistoryActive,
            Run = h.OpenHistory,
        },
    };
}
