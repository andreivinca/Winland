using System.Globalization;

namespace Winland.Common;

/// <summary>
/// One monitor's line item on the event pipe: what it shows right now, and which workspaces live there.
/// <paramref name="Shown"/> is the raw id — <see cref="Ipc.NoWorkspace"/> for nothing, <see cref="Ipc.ScratchpadWorkspace"/> for the
/// scratchpad. <paramref name="Homed"/> are the numbered workspaces whose home is this monitor, ascending.
/// </summary>
public sealed record MonitorWorkspaces(string Device, int Shown, IReadOnlyList<int> Homed)
{
    /// <summary>
    /// The one place the snapshot line is written (grammar on <see cref="Ipc.EventsPipeName"/>):
    /// "&lt;device&gt;=&lt;shown&gt;:&lt;homed,…&gt;" per monitor, separated by spaces.
    /// </summary>
    public static string Format(IEnumerable<MonitorWorkspaces> snapshot) =>
        string.Join(' ', snapshot.Select(m =>
            $"{m.Device}={m.Shown.ToString(CultureInfo.InvariantCulture)}:" +
            string.Join(',', m.Homed.Select(h => h.ToString(CultureInfo.InvariantCulture)))));

    /// <summary>
    /// Read one snapshot line, keyed by device (case-insensitively). A pair whose device or shown workspace
    /// is unreadable is skipped rather than failing the whole line; unreadable homed entries are dropped, and
    /// a pair without the ":homed" part has none.
    /// </summary>
    public static Dictionary<string, MonitorWorkspaces> Parse(string line)
    {
        var snapshot = new Dictionary<string, MonitorWorkspaces>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.LastIndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string[] value = pair[(separator + 1)..].Split(':', 2);
            if (!int.TryParse(value[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shown))
            {
                continue;
            }

            int[] homed = value.Length < 2 ? [] : value[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(h => int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w) ? w : Ipc.NoWorkspace)
                .Where(Ipc.IsNumberedWorkspace)
                .ToArray();

            string device = pair[..separator];
            snapshot[device] = new MonitorWorkspaces(device, shown, homed);
        }

        return snapshot;
    }
}
