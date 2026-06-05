using System;
using System.Threading;
using System.Windows.Forms;

namespace Winland.Keys;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // One keys daemon per session: a second hook would double-fire every shortcut.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\winland-keys", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new KeysApp());

        GC.KeepAlive(mutex);
    }
}
