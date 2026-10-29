using Microsoft.VisualStudio.TestTools.UnitTesting;
using Winland.Common;

namespace Winland.Tests;

[TestClass]
public sealed class MonitorIdentityTests
{
    private const string Source = @"\\.\DISPLAY1";
    private const string Dell = @"\\?\DISPLAY#DELA1C4#5&340bf2b6&0&UID4357#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string Other = @"\\?\DISPLAY#DELA1C5#5&340bf2b6&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const uint Live = MonitorIdentity.DISPLAY_DEVICE_ATTACHED | MonitorIdentity.DISPLAY_DEVICE_ACTIVE;

    [TestMethod]
    public void Choose_NamesTheMonitorByItsInterfacePath_NotItsDisplayNumber()
    {
        string id = MonitorIdentity.Choose(Source, [(Dell, Live)]);

        Assert.AreEqual(Dell, id);
    }

    [TestMethod]
    public void Choose_SkipsMonitorsThatAreNotAttachedAndActive()
    {
        string id = MonitorIdentity.Choose(Source,
            [(Other, MonitorIdentity.DISPLAY_DEVICE_ATTACHED), (Other, MonitorIdentity.DISPLAY_DEVICE_ACTIVE), (Dell, Live)]);

        Assert.AreEqual(Dell, id);
    }

    [TestMethod]
    public void Choose_TakesTheFirstLiveMonitorOfAMirroredSource()
    {
        string id = MonitorIdentity.Choose(Source, [(Dell, Live), (Other, Live)]);

        Assert.AreEqual(Dell, id);
    }

    [TestMethod]
    public void Choose_FallsBackToTheDisplayName_WhenWindowsDescribesNoMonitor()
    {
        Assert.AreEqual(Source, MonitorIdentity.Choose(Source, []));
        Assert.AreEqual(Source, MonitorIdentity.Choose(Source, [("", Live)]));
    }

    [TestMethod]
    public void Choose_RejectsIdsThatWouldBreakTheEventLine()
    {
        Assert.AreEqual(Source, MonitorIdentity.Choose(Source, [("has space", Live)]));
        Assert.AreEqual(Source, MonitorIdentity.Choose(Source, [("a=b", Live)]));
    }

    [TestMethod]
    public void Of_LeavesAnIdUnchanged_SoCommandsMayNameAMonitorEitherWay()
    {
        Assert.AreEqual(Dell, MonitorIdentity.Of(Dell));
    }
}
