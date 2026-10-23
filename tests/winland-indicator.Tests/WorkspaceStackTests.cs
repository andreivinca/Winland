using Microsoft.VisualStudio.TestTools.UnitTesting;
using Winland.Common;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class WorkspaceStackTests
{
    private static string[] ItemsOf(int shown, params int[] homed) =>
        WorkspaceStack.Items(new MonitorWorkspaces(@"\\.\DISPLAY1", shown, homed))
            .Select(i => i.Active ? $"[{i.Text}]" : i.Text) // [n] = drawn in white: the one on screen
            .ToArray();

    [TestMethod]
    public void AtStartup_EachMonitorListsItsOwnWorkspace_InWhite()
    {
        CollectionAssert.AreEqual(new[] { "[1]" }, ItemsOf(1, 1));
        CollectionAssert.AreEqual(new[] { "[2]" }, ItemsOf(2, 2));
    }

    [TestMethod]
    public void HomedWorkspaces_AreListedAscending_WithTheShownOneInWhite()
    {
        CollectionAssert.AreEqual(new[] { "1", "[4]" }, ItemsOf(4, 4, 1));
        CollectionAssert.AreEqual(new[] { "[1]", "4", "17" }, ItemsOf(1, 1, 4, 17));
    }

    [TestMethod]
    public void TheScratchpad_IsAppendedAsAWhiteS()
    {
        CollectionAssert.AreEqual(new[] { "1", "4", "[S]" }, ItemsOf(Ipc.ScratchpadWorkspace, 1, 4));
        CollectionAssert.AreEqual(new[] { "[S]" }, ItemsOf(Ipc.ScratchpadWorkspace));
    }

    [TestMethod]
    public void AWorkspaceShownHereButHomedElsewhere_IsListedToo()
    {
        CollectionAssert.AreEqual(new[] { "1", "[6]" }, ItemsOf(6, 1));
    }

    [TestMethod]
    public void NoWorkspaces_ShowsADash()
    {
        CollectionAssert.AreEqual(new[] { "–" }, ItemsOf(0));
    }

    [TestMethod]
    public void NothingShown_ListsEveryHomedWorkspaceAsANumber()
    {
        CollectionAssert.AreEqual(new[] { "2", "5" }, ItemsOf(0, 5, 2));
    }
}
