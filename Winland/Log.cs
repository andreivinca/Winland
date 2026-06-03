using System;
using System.IO;

namespace Winland;

/// <summary>
/// Minimal append-only diagnostic log. Writes to "winland-hooklog.txt" next to the executable
/// (the same location convention as <see cref="Config.DefaultPath"/>). All failures are swallowed —
/// logging must never affect the hotkey workflow.
/// </summary>
internal static class Log
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "winland-hooklog.txt");

    public static void Line(string message)
    {
        try { File.AppendAllText(LogPath, message + Environment.NewLine); }
        catch { /* ignored */ }
    }
}
