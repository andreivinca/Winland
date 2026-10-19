using System;
using System.Collections.Generic;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// One drag of a circle, from the press until Winland has caught up with it: the rules, kept apart from
/// the windows so they can be unit-tested. <see cref="IndicatorApp"/> applies them to the circles.
///
/// Every circle stays on its own monitor. The circle that was pressed keeps the mouse for the whole drag,
/// but the circle the drag MOVES — the carrier — is the one of the monitor under the cursor. A drag that
/// carries a numbered workspace hands over to another monitor's circle the moment the cursor enters that
/// monitor, and that is when the workspace moves there (<see cref="Follow"/>); back over the first
/// monitor, it moves back. A drag of the dash or of the scratchpad's "S" carries nothing and stays with its
/// own circle.
///
/// While the drag is in effect, the carrier shows the carried workspace and any other circle the latest
/// snapshot still credits with it shows nothing (<see cref="Badge"/>): the workspace is never on two
/// circles at once, even before Winland's snapshot catches up. After the release it stays in effect until
/// the snapshot shows the workspace where the drag left it (<see cref="IsSettled"/>).
/// </summary>
internal sealed class WorkspaceDrag
{
    private readonly Dictionary<string, DockState> _docksAtPress;

    /// <param name="device">The monitor whose circle was pressed.</param>
    /// <param name="workspace">The workspace that circle showed.</param>
    /// <param name="docksAtPress">Where every circle was docked when the drag began.</param>
    public WorkspaceDrag(string device, int workspace, IReadOnlyDictionary<string, DockState> docksAtPress)
    {
        Carrier = device;
        Workspace = workspace is >= 1 and not Ipc.ScratchpadWorkspace ? workspace : 0;
        _docksAtPress = new Dictionary<string, DockState>(docksAtPress, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The numbered workspace this drag carries, or 0 when it carries nothing.</summary>
    public int Workspace { get; }

    /// <summary>The monitor whose circle the drag moves.</summary>
    public string Carrier { get; private set; }

    /// <summary>The button is up: the drag only waits for Winland to show where it left the workspace.</summary>
    public bool Released { get; private set; }

    public bool IsCarrier(string device) => Same(device, Carrier);

    /// <summary>
    /// The cursor is over <paramref name="device"/>'s monitor (null: over none). Returns true when the drag
    /// handed over to that monitor's circle — the carried workspace must now move there.
    /// </summary>
    public bool Follow(string? device)
    {
        if (Workspace == 0 || device == null || Same(device, Carrier))
        {
            return false;
        }

        Carrier = device;
        return true;
    }

    public void Release() => Released = true;

    /// <summary>
    /// What <paramref name="device"/>'s circle shows during the drag, given the workspace
    /// (<paramref name="shown"/>) the latest snapshot says its monitor shows.
    /// </summary>
    public (int Workspace, bool Visible) Badge(string device, int shown)
    {
        if (Workspace == 0)
        {
            return (shown, true);
        }

        return IsCarrier(device) ? (Workspace, true) : (shown, shown != Workspace);
    }

    /// <summary>
    /// Where a circle that isn't the carrier sits during the drag: where it was when the drag began — so a
    /// circle the drag handed over from goes back there. <paramref name="current"/> for a monitor added since.
    /// </summary>
    public DockState DockAtPress(string device, DockState current) =>
        _docksAtPress.TryGetValue(device, out DockState dock) ? dock : current;

    /// <summary>
    /// Whether the drag is over: released, and — if it carried a workspace — the snapshot shows it on the
    /// carrier's monitor (<paramref name="shownOnCarrier"/>).
    /// </summary>
    public bool IsSettled(int shownOnCarrier) => Released && (Workspace == 0 || shownOnCarrier == Workspace);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
