using System.Collections.Generic;
using System.Linq;

namespace Winland;

/// <summary>
/// Which workspace a monitor goes back to when the workspace it shows is moved to another monitor: the one
/// it showed last before it. Pure, so the rule is unit-tested apart from any window.
///
/// "Its" workspaces are the ones homed on it; a workspace that some monitor shows right now — the one being
/// moved included — is not available to go back to.
/// </summary>
internal static class PreviousWorkspace
{
    /// <param name="device">The monitor that is losing its workspace (stable GDI device name).</param>
    /// <param name="homes">workspace → the device it is homed on.</param>
    /// <param name="lastShown">workspace → when it was last shown: a higher number is more recent. A workspace
    /// that is missing counts as the oldest.</param>
    /// <param name="shown">The workspaces some monitor shows right now.</param>
    /// <returns>The workspace to show, or 0 — show nothing — when the monitor has no other workspace.</returns>
    public static int For(
        string device,
        IReadOnlyDictionary<int, string> homes,
        IReadOnlyDictionary<int, long> lastShown,
        IReadOnlySet<int> shown) =>
        homes
            .Where(home => home.Value == device && !shown.Contains(home.Key))
            .Select(home => home.Key)
            .OrderByDescending(workspace => lastShown.GetValueOrDefault(workspace))
            .ThenBy(workspace => workspace)
            .FirstOrDefault();
}
