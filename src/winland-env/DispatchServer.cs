using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
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
    /// Create the pipe with an explicit ACL granting the logged-on user's processes access — elevated
    /// or not. The default DACL of a pipe created by this elevated server rejects the user's own
    /// unelevated processes, which would force every winlandctl caller (helper scripts, terminals) to
    /// run elevated. The trade: any same-user process may drive the window-management verbs; the
    /// verbs are deliberately limited to that.
    /// </summary>
    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(Ipc.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

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
        _thread.Join(1000);
        _cts.Dispose();
    }
}
