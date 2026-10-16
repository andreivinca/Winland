using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Winland.Common;

namespace Winland.Indicator;

/// <summary>
/// The UI thread's doorbell. Workspace snapshots arrive on the event client's thread and display
/// changes on the system-events thread, but every window operation must happen on the UI thread — so
/// callers hand their work here and a hidden message window runs it from the message loop. This is
/// winland-env's <c>UiInvoker</c> trick, one-way: the caller never waits for a result.
///
/// Construct it on the UI thread; its handle is pumped by the app's message loop.
/// </summary>
internal sealed class UiPoster : NativeWindow, IDisposable
{
    private const int WM_RUN = 0x0400 + 101; // WM_USER + 101

    private readonly ConcurrentQueue<Action> _queue = new();

    public UiPoster() => CreateHandle(new CreateParams { Caption = "WinlandIndicatorPoster" });

    /// <summary>Called from any thread. Returns immediately; the action runs on the UI thread.</summary>
    public void Post(Action action)
    {
        _queue.Enqueue(action);
        PostMessage(Handle, WM_RUN, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_RUN)
        {
            // Drain everything: if a PostMessage ever failed, one-per-message would leave the queue
            // permanently a step behind.
            while (_queue.TryDequeue(out Action? action))
            {
                // A failure while applying one update must not take the whole indicator down; log it
                // and keep pumping.
                try { action(); }
                catch (Exception ex) { Log.Line($"indicator: {ex.Message}"); }
            }

            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    // ----- Win32 -----

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
