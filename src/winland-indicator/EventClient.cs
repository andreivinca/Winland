using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// The subscriber half of winland's event channel: a background thread that stays connected to
/// <see cref="Ipc.EventsPipeName"/>, parses each snapshot line into "device → workspace", and hands it
/// to the UI through the callback it was given (the caller is responsible for marshalling).
///
/// Winland is treated as optional, not required: it may not be running yet, and it may be
/// restarted at any time. Either way this reconnects forever, and the indicator simply keeps showing
/// the last state it knew until fresh news arrives — never a blank or an error.
/// </summary>
internal sealed class EventClient : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly Action<IReadOnlyDictionary<string, int>> _onSnapshot;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private bool _disposed;

    public EventClient(Action<IReadOnlyDictionary<string, int>> onSnapshot)
    {
        _onSnapshot = onSnapshot;
        _thread = new Thread(Loop) { IsBackground = true, Name = "WinlandIndicatorEventClient" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", Ipc.EventsPipeName, PipeDirection.In, PipeOptions.Asynchronous);

                // Waits for as long as it takes: Winland may well start after us.
                pipe.ConnectAsync(_cts.Token).GetAwaiter().GetResult();
                Log.Line("indicator: subscribed to winland workspace events");

                using var reader = new StreamReader(pipe);
                while (reader.ReadLineAsync(_cts.Token).AsTask().GetAwaiter().GetResult() is string line)
                {
                    _onSnapshot(Parse(line));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Winland went away mid-stream (or refused us). Nothing to report each time —
                // the successful reconnect above is the line worth logging.
            }

            // Either the stream ended or it failed; pause so a stopped Winland costs us nothing.
            if (_cts.Token.WaitHandle.WaitOne(RetryDelay))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Read one snapshot line — space-separated "&lt;device&gt;=&lt;workspace&gt;" pairs (the grammar
    /// is documented on <see cref="Ipc.EventsPipeName"/>). Unparsable pairs are skipped rather than
    /// failing the whole line, so a future field can be added to the protocol without breaking us.
    /// </summary>
    private static Dictionary<string, int> Parse(string line)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.LastIndexOf('=');
            if (separator > 0 && int.TryParse(pair.AsSpan(separator + 1), out int workspace))
            {
                snapshot[pair[..separator]] = workspace;
            }
        }

        return snapshot;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        // A thread still running after the timeout may yet touch the token; leave the source alive then.
        if (_thread.Join(1000))
        {
            _cts.Dispose();
        }
    }
}
