using Microsoft.VisualStudio.TestTools.UnitTesting;
using Winland.Common;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class MonitorWorkspacesTests
{
    [TestMethod]
    public void Format_ThenParse_RoundTripsEveryMonitor()
    {
        MonitorWorkspaces[] sent =
        [
            new(@"\\.\DISPLAY1", 4, [1, 4]),
            new(@"\\.\DISPLAY2", 0, []),
            new(@"\\.\DISPLAY3", Ipc.ScratchpadWorkspace, [2])
        ];

        string line = MonitorWorkspaces.Format(sent);
        Dictionary<string, MonitorWorkspaces> received = MonitorWorkspaces.Parse(line);

        Assert.AreEqual(@"\\.\DISPLAY1=4:1,4 \\.\DISPLAY2=0: \\.\DISPLAY3=2147483647:2", line);
        foreach (MonitorWorkspaces monitor in sent)
        {
            MonitorWorkspaces back = received[monitor.Device.ToLowerInvariant()]; // keys compare case-insensitively
            Assert.AreEqual(monitor.Shown, back.Shown);
            CollectionAssert.AreEqual(monitor.Homed.ToArray(), back.Homed.ToArray());
        }
    }

    [TestMethod]
    public void Parse_SkipsUnreadablePairs_AndToleratesAMissingHomedList()
    {
        Dictionary<string, MonitorWorkspaces> received =
            MonitorWorkspaces.Parse(@"\\.\DISPLAY1=3 garbage \\.\DISPLAY2=x:1 \\.\DISPLAY3=5:5,y,7");

        Assert.HasCount(2, received);
        Assert.IsEmpty(received[@"\\.\DISPLAY1"].Homed);
        CollectionAssert.AreEqual(new[] { 5, 7 }, received[@"\\.\DISPLAY3"].Homed.ToArray());
    }
}
