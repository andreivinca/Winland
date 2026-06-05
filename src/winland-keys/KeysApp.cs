using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace Winland.Keys;

/// <summary>
/// Root of the keys daemon: a tray-resident hotkey mapper. It owns the keyboard hook and the config,
/// and does nothing more than run each configured Super (Win) combo's command line (via AppLauncher).
/// It has no workspace or focus logic — those live in winland-env and are driven through winlandctl.
/// </summary>
internal sealed class KeysApp : ApplicationContext
{
    private readonly KeysTray _tray;
    private readonly KeyboardHook _hook;
    private readonly string _configPath;
    private List<KeyBind> _binds;
    private bool _disposed;

    public KeysApp()
    {
        // Turn off Windows' own Win+<key> hotkeys so the shell can't act on combos we own.
        WindowsHotkeyDisabler.EnsureDisabled();

        // The hook asks us to resolve each Win+combo to an action id (0 = not ours), then dispatches it
        // back on the UI thread.
        _hook = new KeyboardHook(ResolveActionId, HandleAction);

        _configPath = Config.DefaultPath;
        _binds = HotkeyConfig.Parse(Config.Load(_configPath));

        _tray = new KeysTray(BuildStatusText, ReloadConfig, OpenConfig, ExitApplication);

        _tray.ShowBalloon(_hook.Installed
            ? $"Keys daemon running — {_binds.Count} shortcut(s). Super (Win) hotkeys are active."
            : "Keys daemon running, but the keyboard hook failed to install.");
    }

    private void ReloadConfig()
    {
        _binds = HotkeyConfig.Parse(Config.Load(_configPath));
        _tray.ShowBalloon($"Config reloaded — {_binds.Count} shortcut(s).", 1500);
    }

    private void OpenConfig()
    {
        try { Process.Start(new ProcessStartInfo(_configPath) { UseShellExecute = true }); }
        catch { /* ignored */ }
    }

    private string BuildStatusText() =>
        _hook.Installed
            ? $"Winland keys daemon is running.\n\n{_binds.Count} shortcut(s) loaded from config.\n" +
              "Each Super (Win) combo runs its configured command\n" +
              "(e.g. winlandctl workspace 1, or launch-or-focus ...).\n\n" +
              "Use the tray menu to open/reload the config."
            : "Winland keys daemon is running, but the keyboard hook could not be installed.";

    private void ExitApplication() => ExitThread();

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
            AppLauncher.Run(_binds[index].Action);
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
            _tray.Dispose();
        }

        base.Dispose(disposing);
    }
}
