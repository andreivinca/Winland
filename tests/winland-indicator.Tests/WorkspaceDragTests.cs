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
    public void Badge_TheCarrierShowsTheCarriedWorkspace_WhateverTheSnapshotSays()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        Assert.AreEqual((3, true), drag.Badge(Right, shown: 5)); // the snapshot hasn't caught up yet
        Assert.AreEqual((3, true), drag.Badge(Right, shown: 3));
    }

    [TestMethod]
    public void Badge_ACircleStillCreditedWithTheCarriedWorkspace_IsHidden()
    {
        WorkspaceDrag drag = DragOf(3);
        drag.Follow(Right);

        Assert.IsFalse(drag.Badge(Left, shown: 3).Visible);       // before the snapshot: never two 3s
        Assert.AreEqual((1, true), drag.Badge(Left, shown: 1));    // after: its previous workspace
        Assert.AreEqual((0, true), drag.Badge(Left, shown: 0));    // or nothing
    }

    [TestMethod]
    public void Badge_WhenNothingIsCarried_ShowsTheSnapshot()
    {
        WorkspaceDrag drag = DragOf(0);

        Assert.AreEqual((0, true), drag.Badge(Left, shown: 0));
        Assert.AreEqual((5, true), drag.Badge(Right, shown: 5));
    }

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
