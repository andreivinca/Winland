using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// The control-channel server: a named-pipe listener on a background thread. Each connection carries one
/// command line (e.g. "workspace 1"); it is marshalled to the UI thread via <see cref="UiInvoker"/>,
/// executed by the <see cref="Dispatcher"/>, and the reply is written back. winlandctl is the client.
/// </summary>
internal sealed class DispatchServer : IDisposable
{
    // A connected client that never sends its line must not wedge the (single-connection) server —
    // drop it after this long and accept the next caller.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly UiInvoker _invoker;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private bool _disposed;

    public DispatchServer(UiInvoker invoker)
    {
        _invoker = invoker;
        _thread = new Thread(Loop) { IsBackground = true, Name = "WinlandEnvDispatchServer" };
        _thread.Start();
    }

    /// <summary>
    /// Create the pipe with the shared ACL (see <see cref="PipeAcl"/>), so unelevated callers of
    /// winlandctl reach this elevated server. One instance at a time: commands are serialised anyway.
    /// </summary>
    private static NamedPipeServerStream CreateServer() =>
        NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, PipeAcl.ForCurrentUser());

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream server = CreateServer();

                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();

                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };

                string? line = reader.ReadLineAsync(_cts.Token).AsTask()
                    .WaitAsync(ReadTimeout, _cts.Token).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    string response = _invoker.Invoke(line.Trim());
                    writer.WriteLine(response);
                    server.WaitForPipeDrain(); // let the client read the reply before we close the pipe
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A malformed/broken/stalled connection must not kill the server; just accept the next one.
            }
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

        // A thread still running after the timeout may yet touch the token; leave the source alive then.
        if (_thread.Join(1000))
        {
            _cts.Dispose();
        }
    }
}
