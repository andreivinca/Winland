using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Tests;

[TestClass]
public sealed class CursorWarpTests
{
    private static readonly IntPtr Left = new(1);
    private static readonly IntPtr Right = new(2);
    private static readonly TileRect LeftColumn = new(8, 8, 637, 1024);
    private static readonly TileRect RightColumn = new(645, 8, 1274, 1024);

    [TestMethod]
    public void WarpTarget_WhenFocusMovesToAnotherWindow_IsThatWindowsCentre()
    {
        var before = new CursorWarp.FocusState(Left, LeftColumn);
        var after = new CursorWarp.FocusState(Right, RightColumn);

        Assert.AreEqual((959, 516), CursorWarp.WarpTarget(before, after, cursorOverWindow: false));
    }

    [TestMethod]
    public void WarpTarget_WhenTheFocusedWindowMoves_IsItsNewCentre()
    {
        var before = new CursorWarp.FocusState(Left, LeftColumn);
        var after = new CursorWarp.FocusState(Left, RightColumn);

        Assert.AreEqual((959, 516), CursorWarp.WarpTarget(before, after, cursorOverWindow: false));
    }

    [TestMethod]
    public void WarpTarget_WhenNothingChanged_LeavesTheMouseAlone()
    {
        var state = new CursorWarp.FocusState(Left, LeftColumn);

        Assert.IsNull(CursorWarp.WarpTarget(state, state, cursorOverWindow: false));
    }

    [TestMethod]
    public void WarpTarget_WhenTheMouseIsAlreadyOverTheWindow_LeavesItThere()
    {
        var before = new CursorWarp.FocusState(Left, LeftColumn);
        var after = new CursorWarp.FocusState(Right, RightColumn);

        Assert.IsNull(CursorWarp.WarpTarget(before, after, cursorOverWindow: true));
    }

    [TestMethod]
    public void WarpTarget_WithNoFocusedWindowOrNoFrame_LeavesTheMouseAlone()
    {
        var before = new CursorWarp.FocusState(Left, LeftColumn);

        Assert.IsNull(CursorWarp.WarpTarget(before, new CursorWarp.FocusState(IntPtr.Zero, RightColumn), cursorOverWindow: false));
        Assert.IsNull(CursorWarp.WarpTarget(before, new CursorWarp.FocusState(Right, default), cursorOverWindow: false));
    }

    [TestMethod]
    public void WindowTarget_IsTheWindowsCentreUnlessTheMouseIsAlreadyOverIt()
    {
        Assert.AreEqual((959, 516), CursorWarp.WindowTarget(RightColumn, cursorOverWindow: false));
        Assert.IsNull(CursorWarp.WindowTarget(RightColumn, cursorOverWindow: true));
        Assert.IsNull(CursorWarp.WindowTarget(default, cursorOverWindow: false), "no frame (e.g. minimized): leave it");
    }

    [TestMethod]
    public void IsOverFrame_InTheGapBetweenTiles_IsNotOverTheWindow_EvenThoughTheHitTestFindsIt()
    {
        // The 8 px gap between the columns is covered by the focused tile's invisible resize border, so
        // Windows' hit test reports that tile there. Counting it as "over" left a mouse parked in the gap
        // (e.g. the middle of the screen) behind on every focus change.
        Assert.IsFalse(CursorWarp.IsOverFrame(RightColumn, cursorX: 641, cursorY: 500, hitTestFindsWindow: true));
        Assert.AreEqual((959, 516), CursorWarp.WindowTarget(RightColumn,
            CursorWarp.IsOverFrame(RightColumn, 641, 500, hitTestFindsWindow: true)));
    }

    [TestMethod]
    public void IsOverFrame_InsideTheVisibleFrame_FollowsTheHitTest()
    {
        Assert.IsTrue(CursorWarp.IsOverFrame(RightColumn, cursorX: 700, cursorY: 500, hitTestFindsWindow: true));
        Assert.IsFalse(CursorWarp.IsOverFrame(RightColumn, cursorX: 700, cursorY: 500, hitTestFindsWindow: false),
            "another window covers it there");
    }

    [TestMethod]
    public void AreaTarget_FromAnotherMonitor_IsThatMonitorsCentre()
    {
        var leftMonitor = new TileRect(-1920, 0, 0, 1032);

        Assert.AreEqual((-960, 516), CursorWarp.AreaTarget(leftMonitor, cursorX: 700, cursorY: 400));
    }

    [TestMethod]
    public void AreaTarget_WhenTheMouseIsAlreadyOnTheMonitor_LeavesItThere()
    {
        var leftMonitor = new TileRect(-1920, 0, 0, 1032);

        Assert.IsNull(CursorWarp.AreaTarget(leftMonitor, cursorX: -1500, cursorY: 900));
        Assert.IsNull(CursorWarp.AreaTarget(default, cursorX: 0, cursorY: 0), "an empty area: leave it");
    }
}
