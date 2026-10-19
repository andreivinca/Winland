using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Tests;

[TestClass]
public sealed class TileOrderTests
{
    private static readonly string[] Abcd = { "a", "b", "c", "d" };

    [TestMethod]
    public void Reconcile_KeepsTheKnownOrderAndAppendsNewMembers()
    {
        List<string> result = TileOrder.Reconcile(new[] { "c", "a" }, members: new[] { "a", "b", "c" });

        CollectionAssert.AreEqual(new[] { "c", "a", "b" }, result);
    }

    [TestMethod]
    public void Reconcile_DropsWindowsThatAreNoLongerMembers()
    {
        List<string> result = TileOrder.Reconcile(Abcd, members: new[] { "d", "b" });

        CollectionAssert.AreEqual(new[] { "b", "d" }, result);
    }

    [TestMethod]
    public void InsertAfter_PlacesTheTileRightAfterTheAnchor()
    {
        List<string> result = TileOrder.InsertAfter(Abcd, "x", anchor: "b");

        CollectionAssert.AreEqual(new[] { "a", "b", "x", "c", "d" }, result);
    }

    [TestMethod]
    public void InsertAfter_WithoutTheAnchor_AppendsAtTheEnd()
    {
        List<string> result = TileOrder.InsertAfter(Abcd, "x", anchor: "missing");

        CollectionAssert.AreEqual(new[] { "a", "b", "c", "d", "x" }, result);
    }

    [TestMethod]
    public void InsertAfter_ATileAlreadyInTheStrip_MovesItWithoutDuplicating()
    {
        List<string> result = TileOrder.InsertAfter(Abcd, "a", anchor: "c");

        CollectionAssert.AreEqual(new[] { "b", "c", "a", "d" }, result);
    }

    [TestMethod]
    [DataRow(-5, new[] { "x", "a", "b" })]
    [DataRow(1, new[] { "a", "x", "b" })]
    [DataRow(99, new[] { "a", "b", "x" })]
    public void InsertAt_ClampsTheIndexToTheStrip(int index, string[] expected)
    {
        List<string> result = TileOrder.InsertAt(new[] { "a", "b" }, "x", index);

        CollectionAssert.AreEqual(expected, result);
    }

    [TestMethod]
    public void MoveTo_ANeighbour_SwapsTheTwo()
    {
        CollectionAssert.AreEqual(new[] { "a", "c", "b", "d" }, TileOrder.MoveTo(Abcd, "b", target: "c"));
        CollectionAssert.AreEqual(new[] { "a", "c", "b", "d" }, TileOrder.MoveTo(Abcd, "c", target: "b"));
    }

    [TestMethod]
    public void MoveTo_AFarTile_TakesItsPlaceAndShiftsTheOthers()
    {
        CollectionAssert.AreEqual(new[] { "b", "c", "a", "d" }, TileOrder.MoveTo(Abcd, "a", target: "c"));
        CollectionAssert.AreEqual(new[] { "a", "d", "b", "c" }, TileOrder.MoveTo(Abcd, "d", target: "b"));
    }

    [TestMethod]
    public void MoveTo_AnUnknownTile_LeavesTheOrderAlone()
    {
        CollectionAssert.AreEqual(Abcd, TileOrder.MoveTo(Abcd, "x", target: "b"));
        CollectionAssert.AreEqual(Abcd, TileOrder.MoveTo(Abcd, "a", target: "x"));
    }

    [TestMethod]
    public void Remove_TakesTheTileOut()
    {
        CollectionAssert.AreEqual(new[] { "a", "c", "d" }, TileOrder.Remove(Abcd, "b"));
    }

    [TestMethod]
    public void Operations_NeverMutateTheirInput()
    {
        string[] original = (string[])Abcd.Clone();

        _ = TileOrder.Reconcile(original, new[] { "a" });
        _ = TileOrder.InsertAfter(original, "x", "a");
        _ = TileOrder.InsertAt(original, "x", 0);
        _ = TileOrder.MoveTo(original, "a", "d");
        _ = TileOrder.Remove(original, "a");

        CollectionAssert.AreEqual(Abcd, original);
    }
}
