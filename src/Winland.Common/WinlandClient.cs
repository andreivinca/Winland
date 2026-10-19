using System;
using System.IO;
using System.IO.Pipes;

namespace Winland.Common;

/// <summary>
/// Best-effort client for winland's control pipe (<see cref="Ipc.PipeName"/>), for the requests a
/// Winland process makes itself rather than through a winlandctl process (which would add a process start
/// each time): the keys daemon's "place-next-window" before a launch, and the indicator's
/// "move-workspace" when a circle is dropped on another monitor. Each caller picks timeouts to suit how
/// long it can afford to wait. It never throws.
/// </summary>
public sealed class WinlandClient
{
    private readonly int _connectTimeoutMs;
    private readonly TimeSpan _replyTimeout;

    public WinlandClient(TimeSpan connectTimeout, TimeSpan replyTimeout)
    {
        _connectTimeoutMs = (int)connectTimeout.TotalMilliseconds;
        _replyTimeout = replyTimeout;
    }

    /// <summary>Send one command line; true when winland replied OK.</summary>
    public bool TrySend(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(_connectTimeoutMs);

            var writer = new StreamWriter(client) { AutoFlush = true };
            var reader = new StreamReader(client);
            writer.WriteLine(command);

            string? reply = reader.ReadLineAsync().WaitAsync(_replyTimeout).GetAwaiter().GetResult();
            return reply == Ipc.Ok;
        }
        catch (TimeoutException)
        {
            return false; // Winland not running or busy — the caller decides whether that is worth a log line
        }
        catch (Exception ex)
        {
            Log.Line($"winland request '{command}' failed: {ex.Message}");
            return false;
        }
    }
}
