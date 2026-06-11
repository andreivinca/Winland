using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Winland.Keys;

/// <summary>
/// Shared "hotkey register": a global low-level keyboard hook (WH_KEYBOARD_LL) running on its own
/// dedicated thread. It is stateless: on each key-down it asks the OS whether the Win key is
/// physically held right now, then asks a resolver whether the combo is claimed; if so it swallows the key,
/// injects a dummy 0xFF keystroke (so the Start menu doesn't pop when Win is used as a modifier), and
/// dispatches the matched action id on the UI thread via a hidden message window.
///
/// Construct it on the UI thread (its message window must pump there).
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint WM_QUIT = 0x0012;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt

    private const ulong InjectedMarker = 0x57494E44; // "WIND" — tags our own injected keystrokes

    // resolve(vk, shift, alt, ctrl) -> action id (0 = not a claimed combo; don't swallow).
    private readonly Func<int, bool, bool, bool, int> _resolve;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly MessageWindow _messageWindow;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hookHandle;

    public KeyboardHook(Func<int, bool, bool, bool, int> resolve, Action<int> dispatch)
    {
        _resolve = resolve;
        _messageWindow = new MessageWindow(dispatch);
        _hookProc = HookCallback; // keep a strong ref for the hook's lifetime

        // The hook lives on a dedicated thread with its own message loop so its callback always
        // responds within the low-level-hook timeout, even while the UI thread is busy.
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => ThreadProc(ready)) { IsBackground = true, Name = "WinlandKeyboardHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(2000);
    }

    public bool Installed => _hookHandle != IntPtr.Zero;

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(1000);
            _threadId = 0;
        }

        _messageWindow.Dispose();
    }

    private void ThreadProc(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _hookHandle = InstallHook(_hookProc);
        ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private static IntPtr InstallHook(LowLevelKeyboardProc proc)
    {
        using Process process = Process.GetCurrentProcess();
        using ProcessModule? module = process.MainModule;
        IntPtr moduleHandle = module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, moduleHandle, 0);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

        // Ignore the dummy keystrokes we inject ourselves.
        if ((ulong)data.dwExtraInfo == InjectedMarker)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        int msg = wParam.ToInt32();
        bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
        int vk = (int)data.vkCode;

        if (vk == VK_LWIN || vk == VK_RWIN)
        {
            // Let the Win key itself flow through so Start menu / other combos still work.
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (isDown)
        {
            // No remembered state: ask the OS whether Win is physically held right now.
            bool winHeld = (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (winHeld)
            {
                bool shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                bool alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                int actionId = _resolve(vk, shift, alt, ctrl);
                if (actionId != 0)
                {
                    // Break the "lone Win press" sequence so Start menu doesn't open on Win-up.
                    keybd_event(0xFF, 0, 0, (UIntPtr)InjectedMarker);
                    keybd_event(0xFF, 0, KEYEVENTF_KEYUP, (UIntPtr)InjectedMarker);

                    // Run the action on the UI thread; keeps this callback fast.
                    PostMessage(_messageWindow.Handle, MessageWindow.WM_WINLAND_ACTION, (IntPtr)actionId, IntPtr.Zero);

                    return (IntPtr)1; // swallow
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private sealed class MessageWindow : NativeWindow, IDisposable
    {
        public const int WM_WINLAND_ACTION = 0x8000 + 1; // WM_APP + 1

        private readonly Action<int> _onAction;

        public MessageWindow(Action<int> onAction)
        {
            _onAction = onAction;

            CreateHandle(new CreateParams
            {
                Caption = "WinlandHotkeyMessageWindow",
                X = 0,
                Y = 0,
                Width = 0,
                Height = 0,
                Style = 0
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_WINLAND_ACTION)
            {
                _onAction(m.WParam.ToInt32());
                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            DestroyHandle();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
