using System;
using System.IO;

namespace Winland.Common;

/// <summary>
/// Minimal append-only diagnostic log, written to "winland-hooklog.txt" next to the executable. Shared
/// by every Winland process, so each line is stamped with the time and the process it came from. All
/// failures are swallowed — logging must never affect the workflow.
/// </summary>
public static class Log
{
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "winland-hooklog.txt");
    private static readonly string ProcessName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "winland");

    // Keep the log from growing forever: past this size it is rotated to a single ".old" generation.
    private const long MaxBytes = 1024 * 1024;

    public static void Line(string message)
    {
        try
        {
            RotateIfFull();

            // Every Winland process (and several threads) append to this one file: share it, or
            // concurrent writers fail with a sharing violation and their lines are lost.
            using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream);
            writer.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{ProcessName}] {message}{Environment.NewLine}");
        }
        catch { /* ignored */ }
    }

    private static void RotateIfFull()
    {
        var info = new FileInfo(LogPath);
        if (info.Exists && info.Length > MaxBytes)
        {
            File.Move(LogPath, LogPath + ".old", overwrite: true);
        }
    }
}
