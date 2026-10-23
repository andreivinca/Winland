using Microsoft.VisualStudio.TestTools.UnitTesting;
using Winland.Common;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class WorkspaceDragTests
{
    private const string Left = @"\\.\DISPLAY2";
    private const string Right = @"\\.\DISPLAY1";

    private static readonly DockState LeftDock = new(DockEdge.Top, 0.8);
    private static readonly DockState RightDock = new(DockEdge.Bottom, 0.2);

    private static WorkspaceDrag DragOf(int workspace, string from = Left) =>
        new(from, workspace, new Dictionary<string, DockState> { [Left] = LeftDock, [Right] = RightDock });

    [TestMethod]
    [DataRow(0)]
    [DataRow(Ipc.ScratchpadWorkspace)]
    public void ADashOrTheScratchpad_CarriesNothing_AndStaysWithItsCircle(int shown)
    {
        WorkspaceDrag drag = DragOf(shown);

        bool moved = drag.Follow(Right);

        Assert.AreEqual(0, drag.Workspace);
        Assert.IsFalse(moved);
        Assert.AreEqual(Left, drag.Carrier);
    }

    [TestMethod]
    public void Follow_OnItsOwnMonitor_MovesNothing()
    {
        WorkspaceDrag drag = DragOf(3);

        Assert.IsFalse(drag.Follow(Left));
        Assert.IsFalse(drag.Follow(Left.ToLowerInvariant())); // device names compare case-insensitively
        Assert.IsFalse(drag.Follow(null));                    // over no monitor: keep the carrier
        Assert.AreEqual(Left, drag.Carrier);
    }

    [TestMethod]
    public void Follow_IntoAnotherMonitor_HandsTheDragToItsCircle_AndBackAgain()
    {
        WorkspaceDrag drag = DragOf(3);

        Assert.IsTrue(drag.Follow(Right));
        Assert.AreEqual(Right, drag.Carrier);
        Assert.IsFalse(drag.Follow(Right)); // still there: no second move

        Assert.IsTrue(drag.Follow(Left));
        Assert.AreEqual(Left, drag.Carrier);
    }

    [TestMethod]
    public void Badge_TheCarrierListsTheCarriedWorkspaceAsShown_AndKeepsWhatItShowedHomedThere()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        // The snapshot hasn't caught up yet: Right still shows 5, homed there with 2.
        (MonitorWorkspaces carrier, bool visible) = drag.Badge(Monitor(Right, shown: 5, 2, 5));

        Assert.IsTrue(visible);
        Assert.AreEqual(3, carrier.Shown);
        CollectionAssert.AreEqual(new[] { 2, 3, 5 }, carrier.Homed.ToArray());
    }

    [TestMethod]
    public void Badge_OnceTheSnapshotHasCaughtUp_TheCarrierIsUnchanged()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        (MonitorWorkspaces carrier, _) = drag.Badge(Monitor(Right, shown: 3, 3, 5));

        Assert.AreEqual(3, carrier.Shown);
        CollectionAssert.AreEqual(new[] { 3, 5 }, carrier.Homed.ToArray());
    }

    [TestMethod]
    public void Badge_ACircleStillCreditedWithTheCarriedWorkspace_IsHidden()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        Assert.IsFalse(drag.Badge(Monitor(Left, shown: 3, 1, 3)).Visible); // before the snapshot: never two 3s

        (MonitorWorkspaces after, bool visible) = drag.Badge(Monitor(Left, shown: 1, 1, 3));
        Assert.IsTrue(visible);                                             // after: its previous workspace
        Assert.AreEqual(1, after.Shown);
        CollectionAssert.AreEqual(new[] { 1 }, after.Homed.ToArray());      // and the carried one is dropped
    }

    [TestMethod]
    public void Badge_WhenNothingIsCarried_ShowsTheSnapshot()
    {
        WorkspaceDrag drag = DragOf(0);
        MonitorWorkspaces left = Monitor(Left, shown: 0);
        MonitorWorkspaces right = Monitor(Right, shown: 5, 5);

        Assert.AreEqual((left, true), drag.Badge(left));
        Assert.AreEqual((right, true), drag.Badge(right));
    }

    private static MonitorWorkspaces Monitor(string device, int shown, params int[] homed) => new(device, shown, homed);

    [TestMethod]
    public void DockAtPress_IsWhereACircleWasWhenTheDragBegan()
    {
        WorkspaceDrag drag = DragOf(3);
        var unknown = new DockState(DockEdge.Left, 0.5);

        Assert.AreEqual(RightDock, drag.DockAtPress(Right, current: unknown));
        Assert.AreEqual(unknown, drag.DockAtPress(@"\\.\DISPLAY9", current: unknown)); // a monitor added since
    }

    [TestMethod]
    public void IsSettled_OnlyOnceReleased_AndTheSnapshotShowsTheWorkspaceWhereItWasLeft()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        Assert.IsFalse(drag.IsSettled(shownOnCarrier: 3)); // still dragging

        drag.Release();
        Assert.IsFalse(drag.IsSettled(shownOnCarrier: 5)); // Winland hasn't caught up
        Assert.IsTrue(drag.IsSettled(shownOnCarrier: 3));
    }

    [TestMethod]
    public void IsSettled_ADragThatCarriedNothing_SettlesOnRelease()
    {
        WorkspaceDrag drag = DragOf(0);
        drag.Release();

        Assert.IsTrue(drag.IsSettled(shownOnCarrier: 0));
    }
}
