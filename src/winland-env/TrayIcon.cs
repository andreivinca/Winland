using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winland.Env;

/// <summary>
/// The system-tray presence: a NotifyIcon whose icon is a white circle with the active workspace
/// number, a right-click menu (Status / [Reload config] / [Open config] / Exit), balloon tips, and the
/// status dialog. The config items appear only when their callbacks are supplied — the environment has
/// no config, so it passes null. Everything WinForms/GDI lives here.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<string> _statusText;

    private Icon? _currentIcon;
    private IntPtr _currentIconHandle;
    private int _shownWorkspace = -1;

    public TrayIcon(Func<string> statusText, Action? onReloadConfig, Action? onOpenConfig, Action onExit)
    {
        _statusText = statusText;
        _menu = BuildMenu(onReloadConfig, onOpenConfig, onExit);

        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Winland",
            Visible = true,
            ContextMenuStrip = _menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowStatus();
    }

    public void ShowBalloon(string text, int timeoutMs = 2000)
    {
        _notifyIcon.BalloonTipTitle = "Winland";
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(timeoutMs);
    }

    public void SetWorkspace(int workspace)
    {
        if (workspace == _shownWorkspace)
        {
            return;
        }

        _shownWorkspace = workspace;

        var bmp = new Bitmap(32, 32);
        try
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(Color.Transparent);

                using var bg = new SolidBrush(Color.White);
                g.FillEllipse(bg, 1, 1, 30, 30);

                using var fg = new SolidBrush(Color.Black);
                // Shrink the glyph so multi-digit workspace numbers still fit the 32px circle.
                string text = workspace.ToString();
                float emSize = text.Length switch { <= 1 => 19f, 2 => 14f, _ => 10f };
                using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(text, font, fg, new RectangleF(0, 0, 32, 33), format);
            }

            IntPtr hIcon = bmp.GetHicon();
            var icon = Icon.FromHandle(hIcon);
            _notifyIcon.Icon = icon;
            _notifyIcon.Text = $"Winland — workspace {workspace}";

            // Release the previously shown icon (Icon.FromHandle does not own the HICON).
            _currentIcon?.Dispose();
            if (_currentIconHandle != IntPtr.Zero)
            {
                DestroyIcon(_currentIconHandle);
            }

            _currentIcon = icon;
            _currentIconHandle = hIcon;
        }
        finally
        {
            bmp.Dispose();
        }
    }

    private ContextMenuStrip BuildMenu(Action? onReloadConfig, Action? onOpenConfig, Action onExit)
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("Status");
        statusItem.Click += (_, _) => ShowStatus();

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        if (onReloadConfig != null)
        {
            var reloadItem = new ToolStripMenuItem("Reload config");
            reloadItem.Click += (_, _) => onReloadConfig();
            menu.Items.Add(reloadItem);
        }

        if (onOpenConfig != null)
        {
            var openConfigItem = new ToolStripMenuItem("Open config");
            openConfigItem.Click += (_, _) => onOpenConfig();
            menu.Items.Add(openConfigItem);
        }

        if (onReloadConfig != null || onOpenConfig != null)
        {
            menu.Items.Add(new ToolStripSeparator());
        }

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => onExit();
        menu.Items.Add(exitItem);

        return menu;
    }

    private void ShowStatus()
    {
        MessageBox.Show(_statusText(), "Winland", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
        if (_currentIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_currentIconHandle);
            _currentIconHandle = IntPtr.Zero;
        }
        _menu.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
