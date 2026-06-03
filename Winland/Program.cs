using System;
using System.Windows.Forms;

namespace Winland;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WinlandApp());
    }
}
