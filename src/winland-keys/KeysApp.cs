using System;
using System.Collections.Generic;
using Winland.Common;

namespace Winland.Keys;

/// <summary>
/// Root of the keys daemon: a headless (no tray icon) hotkey mapper. It owns the keyboard hook and the
/// config, and does nothing more than run each configured Super (Win) combo's command line (via
/// AppLauncher). It has no workspace or focus logic — those live in winland, driven through
/// winlandctl. It shows no UI; Winland starts it and ends it when Winland exits.
/// </summary>
internal sealed class KeysApp : System.Windows.Forms.ApplicationContext
{
    private readonly KeyboardHook _hook;
    // Short timeouts: a launch must never wait long on a Winland that is down or busy.
    private readonly AppLauncher _launcher =
        new(new WinlandClient(connectTimeout: TimeSpan.FromMilliseconds(250), replyTimeout: TimeSpan.FromSeconds(1)));
    private readonly IReadOnlyList<KeyBind> _binds;
    private bool _disposed;

    public KeysApp()
    {
        // The hook alone keeps the shell off the combos we bind; undo the NoWinKeys policy an earlier
        // version set, which switched off every other Windows Win-key shortcut too.
        WindowsHotkeyRestorer.RestoreIfDisabled();

        // Parse the binds BEFORE the hook goes live: the hook thread calls ResolveActionId from the
        // moment it is installed, so the binds must already exist by then.
        _binds = HotkeyConfig.Parse(Config.Load(Config.DefaultPath));

        // The hook asks us to resolve each Win+combo to an action id (0 = not ours), then dispatches it
        // back on the UI thread.
        _hook = new KeyboardHook(ResolveActionId, HandleAction);

        Log.Line(_hook.Installed
            ? $"keys daemon running (headless) — {_binds.Count} shortcut(s); Super (Win) hotkeys active."
            : "keys daemon running (headless), but the keyboard hook failed to install.");
    }

    // Adapter for KeyboardHook: returns (bind index + 1) for a matching Win+combo, or 0 if unclaimed.
    private int ResolveActionId(int vk, bool shiftDown, bool altDown, bool ctrlDown)
    {
        BindModifiers mods = (shiftDown ? BindModifiers.Shift : 0)
            | (altDown ? BindModifiers.Alt : 0)
            | (ctrlDown ? BindModifiers.Ctrl : 0);

        for (int i = 0; i < _binds.Count; i++)
        {
            if (_binds[i].Vk == vk && _binds[i].Modifiers == mods)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void HandleAction(int actionId)
    {
        // Runs on the UI thread, immediately when the combo is pressed.
        int index = actionId - 1;
        if (index >= 0 && index < _binds.Count)
        {
            _launcher.Run(_binds[index].Action);
        }
    }

    protected override void ExitThreadCore()
    {
        Dispose(true);
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _hook.Dispose();
        }

        base.Dispose(disposing);
    }
}
