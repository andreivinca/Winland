using System;
using System.Threading;
using System.Windows.Forms;

namespace Winland;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // One Winland per session: a second instance would double the WinEvent hooks, tray icon and pipe
        // servers, and start a second set of companions. Hold a named mutex; if it already exists, another
        // instance owns the role.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\winland", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new WinlandApp());

        GC.KeepAlive(mutex);
    }
}
