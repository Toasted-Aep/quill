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
/// stroke-by-stroke replay), so it reads as part of the drawing surface.</item>
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
        public required Func<FlyoutBase?> HistoryMenu { get; init; }
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
            Id = "History",
            Label = Loc.T("Wheel.Cmd.History"),
            Icon = Icons.History,
            TopBarKey = "BtnHistory",
            Flyout = h.HistoryMenu,
        },
    };
}
