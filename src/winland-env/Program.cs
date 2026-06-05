using System;
using System.Threading;
using System.Windows.Forms;

namespace Winland.Env;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // One environment per session: a second instance would double the WinEvent hooks, tray icon,
        // and pipe server. Hold a named mutex; if it already exists, another instance owns the role.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\winland-env", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new EnvApp());

        GC.KeepAlive(mutex);
    }
}
