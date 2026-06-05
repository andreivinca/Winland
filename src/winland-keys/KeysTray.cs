using System;
using System.Drawing;
using System.Windows.Forms;

namespace Winland.Keys;

/// <summary>
/// Minimal tray presence for the keys daemon: a static icon with a right-click menu
/// (Status / Reload config / Open config / Exit) and balloon tips. There is no workspace indicator —
/// that belongs to winland-env.
/// </summary>
internal sealed class KeysTray : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<string> _statusText;

    public KeysTray(Func<string> statusText, Action onReloadConfig, Action onOpenConfig, Action onExit)
    {
        _statusText = statusText;
        _menu = BuildMenu(onReloadConfig, onOpenConfig, onExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Winland keys",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowStatus();
    }

    public void ShowBalloon(string text, int timeoutMs = 2000)
    {
        _notifyIcon.BalloonTipTitle = "Winland keys";
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

    private ContextMenuStrip BuildMenu(Action onReloadConfig, Action onOpenConfig, Action onExit)
    {
        var menu = new ContextMenuStrip();

        var status = new ToolStripMenuItem("Status");
        status.Click += (_, _) => ShowStatus();

        var reload = new ToolStripMenuItem("Reload config");
        reload.Click += (_, _) => onReloadConfig();

        var open = new ToolStripMenuItem("Open config");
        open.Click += (_, _) => onOpenConfig();

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => onExit();

        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(reload);
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        return menu;
    }

    private void ShowStatus() =>
        MessageBox.Show(_statusText(), "Winland keys", MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
