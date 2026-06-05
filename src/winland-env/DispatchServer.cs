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

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Ipc.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();

                using var reader = new StreamReader(server);
                using var writer = new StreamWriter(server) { AutoFlush = true };

                string? line = reader.ReadLine();
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
                // A malformed/broken connection must not kill the server; just accept the next one.
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
