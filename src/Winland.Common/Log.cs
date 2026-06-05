using System;
using System.IO;

namespace Winland.Common;

/// <summary>
/// Minimal append-only diagnostic log, written to "winland-hooklog.txt" next to the executable. Shared
/// by every Winland process. All failures are swallowed — logging must never affect the workflow.
/// </summary>
public static class Log
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "winland-hooklog.txt");

    public static void Line(string message)
    {
        try { File.AppendAllText(LogPath, message + Environment.NewLine); }
        catch { /* ignored */ }
    }
}
