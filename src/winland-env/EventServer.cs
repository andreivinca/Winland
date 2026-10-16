using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// The event-channel server: a broadcast-only named pipe (<see cref="Ipc.EventsPipeName"/>) that
/// publishes what every monitor is showing. Subscribers — the on-screen indicator today — connect and
/// only read; nothing they could send would be listened to.
///
/// The workspace engine runs on the UI thread, so <see cref="Publish"/> must never block: it only
/// stores the newest snapshot and wakes the subscriber threads, one per connection, which do the
/// writing. Only the LATEST snapshot is ever sent — a subscriber that was slow simply skips the states
/// it missed, which is exactly right for "what is on screen right now". A subscriber that goes away or
/// stalls is dropped silently.
/// </summary>
internal sealed class EventServer : IDisposable
{
    // Enough concurrent subscribers for the indicator plus a few diagnostic readers; one of these
    // instances is always the idle listener waiting for the next connection.
    private const int MaxSubscribers = 8;

    // The pipe rejects a new instance while all of them are in use; pause before trying again so a
    // full pipe cannot spin this thread.
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromSeconds(1);

    // A subscriber that disconnects is only noticed when a write fails. Resending the current snapshot
    // after this much idle time frees its pipe instance even when no workspace changes.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _acceptThread;

    // _gate guards the pending snapshot and is the condition variable the subscriber threads wait on.
    // _version is bumped on every publish; each subscriber remembers the version it last wrote, so it
    // sends the current line once and then sleeps until something actually changes.
    private readonly object _gate = new();
    private string _snapshot = string.Empty;
    private long _version;

    private bool _disposed;

    public EventServer()
    {
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "WinlandEnvEventServer" };
        _acceptThread.Start();
    }

    /// <summary>
    /// Publish what each monitor is showing. Called on the UI thread after every workspace change —
    /// it does no I/O and takes only an uncontended lock, so it returns immediately.
    /// </summary>
    public void Publish(IReadOnlyList<(string Device, int Workspace)> snapshot)
    {
        string line = FormatSnapshot(snapshot);
        lock (_gate)
        {
            _snapshot = line;
            _version++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// The one place the wire format is written (its grammar is documented on
    /// <see cref="Ipc.EventsPipeName"/>): "&lt;device&gt;=&lt;workspace&gt;" pairs separated by
    /// spaces, which is unambiguous because GDI display names never contain one.
    /// </summary>
    private static string FormatSnapshot(IReadOnlyList<(string Device, int Workspace)> snapshot) =>
        string.Join(' ', snapshot.Select(m => $"{m.Device}={m.Workspace}"));

    private void AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(
                    Ipc.EventsPipeName, PipeDirection.Out, MaxSubscribers,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, PipeAcl.ForCurrentUser());

                try
                {
                    pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                }
                catch
                {
                    pipe.Dispose();
                    throw;
                }

                // Ownership passes to the subscriber's own thread: a stalled reader can then block
                // nobody but itself, and this loop is free to accept the next connection at once.
                var thread = new Thread(() => Serve(pipe))
                {
                    IsBackground = true,
                    Name = "WinlandEnvEventSubscriber"
                };
                thread.Start();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // No pipe instance available (or a transient failure): wait a moment and listen again.
                if (_cts.Token.WaitHandle.WaitOne(AcceptRetryDelay))
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// One subscriber's lifetime: write the current snapshot, then every later one, until the
    /// subscriber disconnects or the server shuts down. Any failure ends the connection quietly.
    /// </summary>
    private void Serve(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            {
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                long written = 0;

                while (true)
                {
                    string line;
                    lock (_gate)
                    {
                        while (_version == written && !_cts.IsCancellationRequested)
                        {
                            if (!Monitor.Wait(_gate, KeepAliveInterval) && written != 0)
                            {
                                break; // idle: resend the same line to probe the connection
                            }
                        }

                        if (_cts.IsCancellationRequested)
                        {
                            return;
                        }

                        line = _snapshot;
                        written = _version;
                    }

                    writer.WriteLine(line);
                }
            }
        }
        catch
        {
            // The subscriber closed its end (or wedged and was cut off on shutdown) — just let go.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();

        // Wake the subscriber threads so they see the cancellation and close their pipes. They are
        // background threads, so one blocked mid-write on a stalled reader cannot hold up shutdown.
        lock (_gate)
        {
            Monitor.PulseAll(_gate);
        }

        // A thread still running after the timeout may yet touch the token; leave the source alive then.
        if (_acceptThread.Join(1000))
        {
            _cts.Dispose();
        }
    }
}
