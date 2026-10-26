using System.Collections.Generic;
using System.Linq;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>One entry on a badge: its text, and whether it is what the monitor shows right now.</summary>
internal readonly record struct StackItem(string Text, bool Active);

/// <summary>
/// What one monitor's badge lists, in order: the workspaces homed on that monitor, ascending, by number —
/// the one on screen marked <see cref="StackItem.Active"/> (underlined); then
/// <see cref="Scratchpad"/>, active, while the roaming scratchpad is up there. A monitor with none of these
/// lists <see cref="Nothing"/>.
///
/// A workspace on screen here but homed elsewhere (its home monitor is asleep) is listed here too: the
/// badge always marks what the monitor shows.
/// </summary>
internal static class WorkspaceStack
{
    public const string Scratchpad = "S";
    public const string Nothing = "–";

    public static IReadOnlyList<StackItem> Items(MonitorWorkspaces monitor)
    {
        IEnumerable<int> numbered = monitor.Shown is >= 1 and not Ipc.ScratchpadWorkspace
            ? monitor.Homed.Append(monitor.Shown)
            : monitor.Homed;

        var items = numbered
            .Where(w => w is >= 1 and not Ipc.ScratchpadWorkspace)
            .Distinct()
            .Order()
            .Select(w => new StackItem(w.ToString(), w == monitor.Shown))
            .ToList();

        if (monitor.Shown == Ipc.ScratchpadWorkspace)
        {
            items.Add(new StackItem(Scratchpad, true));
        }

        return items.Count > 0 ? items : [new StackItem(Nothing, false)];
    }
}
