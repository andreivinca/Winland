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
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint WM_QUIT = 0x0012;
    private const uint LLKHF_EXTENDED = 0x01; // KBDLLHOOKSTRUCT.flags: extended key (e.g. arrow cluster)
    private const uint LLKHF_INJECTED = 0x10; // KBDLLHOOKSTRUCT.flags: event was injected (synthetic)

    // Scancodes of the synthetic Shift that Windows brackets numpad keys with when NumLock is on:
    // 0x22A pairs with a held Left Shift, 0x236 with a held Right Shift. Neither is flagged injected,
    // so they can only be told apart from a real Shift (0x2A left / 0x36 right) by these scancodes.
    // Ignoring them keeps our tracked Shift state matching the physical keys.
    private const uint NumpadFakeLeftShiftScanCode = 0x22A;
    private const uint NumpadFakeRightShiftScanCode = 0x236;

    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private const ulong InjectedMarker = 0x57494E44; // "WIND" — tags our own injected keystrokes

    // resolve(vk, shift, alt, ctrl) -> action id (0 = not a claimed combo; don't swallow).
    private readonly Func<int, bool, bool, bool, int> _resolve;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly MessageWindow _messageWindow;
    private readonly Thread _thread;
    private uint _threadId;
    private IntPtr _hookHandle;

    // Physical modifier state, tracked per side from NON-injected key events. Used for numpad keys,
    // where GetAsyncKeyState is unreliable: with NumLock on, Windows injects a synthetic Shift-up/down
    // around numpad keystrokes, so the OS-level Shift state reads "released" while Shift is really
    // held. Tracked per side because one flag per modifier is not enough — with both Shifts held,
    // releasing one must not read as "Shift is up".
    private volatile bool _leftShift, _rightShift;
    private volatile bool _leftCtrl, _rightCtrl;
    private volatile bool _leftAlt, _rightAlt;

    private bool ShiftPhys => _leftShift || _rightShift;
    private bool CtrlPhys => _leftCtrl || _rightCtrl;
    private bool AltPhys => _leftAlt || _rightAlt;

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
        if (!ready.Wait(2000))
        {
            // The hook may still install a moment later; Installed just reads false right now.
            Winland.Common.Log.Line("keyboard hook: worker thread was slow to start");
        }
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
        bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
        int vk = (int)data.vkCode;

        // Track real modifier presses/releases. Ignore injected events and Windows' synthetic numpad
        // Shifts so neither clobbers the true Shift state mid-combo.
        if ((isDown || isUp)
            && (data.flags & LLKHF_INJECTED) == 0
            && data.scanCode != NumpadFakeLeftShiftScanCode
            && data.scanCode != NumpadFakeRightShiftScanCode)
        {
            TrackModifier(vk, isDown);
        }

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
                // Identify numpad digit keys by scancode, not vk: with Shift+NumLock, Windows reports the
                // numpad key as its navigation vk (VK_LEFT/RIGHT/...) instead of VK_NUMPADn, but the
                // scancode is stable and non-extended (the real arrow cluster is extended). Normalizing
                // to VK_NUMPADn lets Super+Shift+NumpadN resolve regardless of which vk Windows sent.
                bool extended = (data.flags & LLKHF_EXTENDED) != 0;
                int numpadVk = extended ? 0 : NumpadVkFromScanCode(data.scanCode);
                bool numpad = numpadVk != 0;
                int effectiveVk = numpad ? numpadVk : vk;

                // For numpad keys GetAsyncKeyState's modifiers are unreliable (synthetic numpad Shift),
                // so use the physical state we track from real events. Other keys keep the stateless OS
                // query (no behavior change, self-healing).
                bool shift = numpad ? ShiftPhys : (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                bool alt = numpad ? AltPhys : (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
                bool ctrl = numpad ? CtrlPhys : (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

                int actionId = _resolve(effectiveVk, shift, alt, ctrl);
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

    // Map a numpad digit key's (non-extended) scancode to its VK_NUMPAD0..9 code, or 0 if it isn't a
    // numpad digit. Scancodes are the IBM set-1 layout; the extended arrow cluster is filtered out by
    // the caller via the extended flag, so these always mean the numeric keypad.
    private static int NumpadVkFromScanCode(uint scanCode)
    {
        switch (scanCode)
        {
            case 0x52: return 0x60; // Numpad0
            case 0x4F: return 0x61; // Numpad1
            case 0x50: return 0x62; // Numpad2
            case 0x51: return 0x63; // Numpad3
            case 0x4B: return 0x64; // Numpad4
            case 0x4C: return 0x65; // Numpad5
            case 0x4D: return 0x66; // Numpad6
            case 0x47: return 0x67; // Numpad7
            case 0x48: return 0x68; // Numpad8
            case 0x49: return 0x69; // Numpad9
            default: return 0;
        }
    }

    // Update tracked physical modifier state from a real (non-injected) key event. Low-level events
    // report the side-specific codes; the generic ones (VK_SHIFT etc.) are handled for safety and set
    // both sides.
    private void TrackModifier(int vk, bool isDown)
    {
        switch (vk)
        {
            case VK_LSHIFT: _leftShift = isDown; break;
            case VK_RSHIFT: _rightShift = isDown; break;
            case VK_SHIFT: _leftShift = _rightShift = isDown; break;
            case VK_LCONTROL: _leftCtrl = isDown; break;
            case VK_RCONTROL: _rightCtrl = isDown; break;
            case VK_CONTROL: _leftCtrl = _rightCtrl = isDown; break;
            case VK_LMENU: _leftAlt = isDown; break;
            case VK_RMENU: _rightAlt = isDown; break;
            case VK_MENU: _leftAlt = _rightAlt = isDown; break;
        }
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
