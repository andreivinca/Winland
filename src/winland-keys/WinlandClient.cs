using System;
using System.IO;
using System.IO.Pipes;
using Winland.Common;

namespace Winland.Keys;

/// <summary>
/// Best-effort client for winland's control pipe, for requests the keys daemon makes itself rather
/// than through a winlandctl process (which would add a process start to every app launch).
/// </summary>
internal sealed class WinlandClient
{
    // Short: a launch must never wait long on a Winland that is down or busy.
    private const int ConnectTimeoutMs = 250;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Send one command line; true when winland replied OK.</summary>
    public bool TrySend(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Ipc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(ConnectTimeoutMs);

            var writer = new StreamWriter(client) { AutoFlush = true };
            var reader = new StreamReader(client);
            writer.WriteLine(command);

            string? reply = reader.ReadLineAsync().WaitAsync(ReplyTimeout).GetAwaiter().GetResult();
            return reply == Ipc.Ok;
        }
        catch (TimeoutException)
        {
            return false; // Winland not running or busy — not worth a log line per launch
        }
        catch (Exception ex)
        {
            Log.Line($"env request '{command}' failed: {ex.Message}");
            return false;
        }
    }
}
