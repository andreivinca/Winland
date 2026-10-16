using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winland.Env;

/// <summary>
/// The system-tray presence: a NotifyIcon drawn once as a white circle with a "W" (per-monitor
/// workspace numbers live in the on-screen indicator, not here), a right-click menu (Status / Exit),
/// balloon tips, and the status dialog. Everything WinForms/GDI lives here.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<string> _statusText;

    private readonly Icon _icon;
    private readonly IntPtr _iconHandle;

    public TrayIcon(Func<string> statusText, Action onExit)
    {
        _statusText = statusText;
        _menu = BuildMenu(onExit);
        (_icon, _iconHandle) = DrawIcon();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
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

    /// <summary>The static badge: a white circle with a "W", drawn once for the icon's lifetime.</summary>
    private static (Icon Icon, IntPtr Handle) DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.White);
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var fg = new SolidBrush(Color.Black);
            using var font = new Font("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            g.DrawString("W", font, fg, new RectangleF(0, 0, 32, 33), format);
        }

        // Icon.FromHandle does not own the HICON — keep the handle so Dispose can free it.
        IntPtr handle = bmp.GetHicon();
        return (Icon.FromHandle(handle), handle);
    }

    private ContextMenuStrip BuildMenu(Action onExit)
    {
        var menu = new ContextMenuStrip();

        var statusItem = new ToolStripMenuItem("Status");
        statusItem.Click += (_, _) => ShowStatus();

        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

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
        _icon.Dispose();
        DestroyIcon(_iconHandle);
        _menu.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
