using Microsoft.VisualStudio.TestTools.UnitTesting;
using Winland.Common;

namespace Winland.Tests;

[TestClass]
public sealed class PreviousWorkspaceTests
{
    private const string Left = @"\\.\DISPLAY1";
    private const string Right = @"\\.\DISPLAY2";

    [TestMethod]
    public void For_PicksTheWorkspaceTheMonitorShowedLast()
    {
        var homes = new Dictionary<int, string> { [1] = Left, [3] = Left, [4] = Left };
        var lastShown = new Dictionary<int, long> { [1] = 1, [4] = 7, [3] = 9 };

        int previous = PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int> { 3 });

        Assert.AreEqual(4, previous);
    }

    [TestMethod]
    public void For_SkipsWorkspacesHomedOnAnotherMonitor()
    {
        var homes = new Dictionary<int, string> { [1] = Left, [2] = Right, [3] = Left };
        var lastShown = new Dictionary<int, long> { [1] = 1, [2] = 5, [3] = 9 };

        int previous = PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int> { 3 });

        Assert.AreEqual(1, previous);
    }

    [TestMethod]
    public void For_SkipsWorkspacesAMonitorIsShowing()
    {
        // 5 is homed here but on screen elsewhere (shown there while this monitor slept): it stays put.
        var homes = new Dictionary<int, string> { [1] = Left, [3] = Left, [5] = Left };
        var lastShown = new Dictionary<int, long> { [1] = 1, [5] = 8, [3] = 9 };

        int previous = PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int> { 3, 5 });

        Assert.AreEqual(1, previous);
    }

    [TestMethod]
    public void For_WithNoOtherWorkspaceHomedThere_ReturnsNoWorkspace()
    {
        var homes = new Dictionary<int, string> { [2] = Right, [3] = Left };
        var lastShown = new Dictionary<int, long> { [2] = 5, [3] = 9 };

        int previous = PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int> { 3 });

        Assert.AreEqual(Ipc.NoWorkspace, previous);
    }

    [TestMethod]
    public void For_WorkspaceZero_IsAWorkspaceToGoBackTo()
    {
        var homes = new Dictionary<int, string> { [0] = Left, [3] = Left };
        var lastShown = new Dictionary<int, long> { [0] = 5, [3] = 9 };

        int previous = PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int> { 3 });

        Assert.AreEqual(0, previous);
    }

    [TestMethod]
    public void For_WorkspacesNeverShown_ComeLastInAscendingOrder()
    {
        var homes = new Dictionary<int, string> { [8] = Left, [6] = Left, [7] = Left };
        var lastShown = new Dictionary<int, long> { [7] = 4 };

        Assert.AreEqual(7, PreviousWorkspace.For(Left, homes, lastShown, shown: new HashSet<int>()));
        Assert.AreEqual(6, PreviousWorkspace.For(Left, homes, new Dictionary<int, long>(), shown: new HashSet<int>()));
    }
}
