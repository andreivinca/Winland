using System;
using System.Threading;
using System.Windows.Forms;

namespace Winland.Indicator;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // One indicator per session: a second instance would stack a duplicate circle on every monitor.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\winland-indicator", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        // Sets per-monitor-v2 DPI awareness (see ApplicationHighDpiMode in the .csproj) before any
        // window exists, so each circle can be sized from its own monitor's DPI.
        ApplicationConfiguration.Initialize();
        Application.Run(new IndicatorApp());

        GC.KeepAlive(mutex);
    }
}
