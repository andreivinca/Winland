using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Winland.Common;

namespace Winland.Env;

/// <summary>
/// Marshals a string command from a background thread (the IPC server) onto the UI thread, where the
/// workspace/focus operations must run (they touch WinEvent hooks and foreground state). Uses a hidden
/// message window + synchronous SendMessage, so the caller blocks until the handler returns its reply.
/// A single lock serialises callers, keeping the shared input/output fields safe.
/// </summary>
internal sealed class UiInvoker : NativeWindow, IDisposable
{
    private const int WM_INVOKE = 0x0400 + 100; // WM_USER + 100

    private readonly Func<string, string> _handler;
    private readonly object _gate = new();
    private string _input = string.Empty;
    private string _output = string.Empty;

    /// <summary>Construct on the UI thread; its handle is pumped by the app's message loop.</summary>
    public UiInvoker(Func<string, string> handler)
    {
        _handler = handler;
        CreateHandle(new CreateParams { Caption = "WinlandEnvInvoker" });
    }

    /// <summary>Called from any thread. Blocks until the handler runs on the UI thread and returns.</summary>
    public string Invoke(string command)
    {
        lock (_gate)
        {
            _input = command;
            // Kept only if the message is never handled (window already destroyed during shutdown).
            _output = $"{Ipc.ErrPrefix} environment is shutting down";
            SendMessage(Handle, WM_INVOKE, IntPtr.Zero, IntPtr.Zero); // synchronous: WndProc runs on the UI thread
            return _output;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_INVOKE)
        {
            try
            {
                _output = _handler(_input);
            }
            catch (Exception ex)
            {
                Log.Line($"command '{_input}' failed: {ex}");
                _output = $"{Ipc.ErrPrefix} {ex.Message}";
            }

            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
