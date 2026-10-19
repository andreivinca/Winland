using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Tests;

[TestClass]
public sealed class StripLayoutTests
{
    // A 1920x1080 monitor with a 40 px taskbar at the bottom, at 100% scaling (96 DPI).
    private static readonly TileRect Work = new(0, 0, 1920, 1040);
    private const int Dpi = 96;
    private const int Gap = StripLayout.BaseGap;
    private const int NoReveal = -1;

    [TestMethod]
    public void Arrange_WithNoTiles_ReturnsEmptyArrangement()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 0, first: 3, reveal: NoReveal);

        Assert.IsEmpty(result.Slots);
        Assert.AreEqual(0, result.First);
        Assert.AreEqual(0, result.VisibleCount);
    }

    [TestMethod]
    public void Arrange_WithOneTile_FillsWorkAreaInsideTheGaps()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 1, first: 0, reveal: NoReveal);

        Assert.AreEqual(new TileSlot(new TileRect(Gap, Gap, 1920 - Gap, 1040 - Gap), Visible: true), result.Slots[0]);
    }

    [TestMethod]
    public void Arrange_TilesThatFit_ShareTheWidthEquallyAndAllShow()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 2, first: 0, reveal: NoReveal);

        TileRect left = result.Slots[0].Rect;
        TileRect right = result.Slots[1].Rect;
        Assert.IsTrue(result.Slots.All(s => s.Visible));
        Assert.AreEqual(Gap, right.Left - left.Right, "one gap between neighbours");
        Assert.IsLessThanOrEqualTo(1, Math.Abs(left.Width - right.Width), "equal widths, give or take a rounding pixel");
        Assert.AreEqual(1920 - Gap, right.Right, "the last tile reaches the far gap");
    }

    [TestMethod]
    public void Arrange_MoreTilesThanFit_ShowsOnlyTheCapacityAndHidesTheRest()
    {
        // 1920 px fits three tiles of at least the minimum width.
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 5, first: 0, reveal: NoReveal);

        Assert.AreEqual(3, result.VisibleCount);
        CollectionAssert.AreEqual(
            new[] { true, true, true, false, false },
            result.Slots.Select(s => s.Visible).ToArray());
    }

    [TestMethod]
    public void Arrange_ScrolledTiles_KeepTheirWidthInsteadOfShrinking()
    {
        StripArrangement three = StripLayout.Arrange(Work, Dpi, tileCount: 3, first: 0, reveal: NoReveal);
        StripArrangement six = StripLayout.Arrange(Work, Dpi, tileCount: 6, first: 0, reveal: NoReveal);

        Assert.AreEqual(three.Slots[0].Rect.Width, six.Slots[0].Rect.Width);
        Assert.IsGreaterThanOrEqualTo(StripLayout.BaseMinTileWidth, six.Slots[0].Rect.Width);
    }

    [TestMethod]
    public void Arrange_RevealingATileRightOfTheViewport_ScrollsJustEnoughToShowIt()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 5, first: 0, reveal: 4);

        Assert.AreEqual(2, result.First);
        Assert.IsTrue(result.Slots[4].Visible);
        Assert.AreEqual(1920 - Gap, result.Slots[4].Rect.Right, "it enters at the right edge");
    }

    [TestMethod]
    public void Arrange_RevealingATileLeftOfTheViewport_ScrollsBackToIt()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 5, first: 2, reveal: 0);

        Assert.AreEqual(0, result.First);
        Assert.IsTrue(result.Slots[0].Visible);
        Assert.IsFalse(result.Slots[4].Visible);
    }

    [TestMethod]
    public void Arrange_RevealingAVisibleTile_KeepsTheScrollPosition()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 6, first: 1, reveal: 2);

        Assert.AreEqual(1, result.First);
    }

    [TestMethod]
    public void Arrange_ScrollPositionPastTheEnd_ClampsToTheLastFullView()
    {
        // e.g. tiles were closed while the strip was scrolled to its far end.
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 5, first: 4, reveal: NoReveal);

        Assert.AreEqual(2, result.First);
        Assert.AreEqual(3, result.Slots.Count(s => s.Visible));
    }

    [TestMethod]
    public void Arrange_VisibleTiles_NeverOverlapAndKeepTheirOrder()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 7, first: 2, reveal: NoReveal);

        TileRect[] shown = result.Slots.Where(s => s.Visible).Select(s => s.Rect).ToArray();
        for (int i = 1; i < shown.Length; i++)
        {
            Assert.AreEqual(Gap, shown[i].Left - shown[i - 1].Right);
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(9)]
    public void Arrange_EveryTileHiddenOrNot_StaysInsideItsOwnMonitor(int tileCount)
    {
        // A monitor LEFT of the primary, at negative coordinates: nothing may land on a neighbour.
        var work = new TileRect(-2560, 0, 0, 1400);

        for (int first = 0; first < tileCount; first++)
        {
            StripArrangement result = StripLayout.Arrange(work, Dpi, tileCount, first, reveal: NoReveal);

            foreach (TileSlot slot in result.Slots)
            {
                Assert.IsTrue(slot.Rect.Left >= work.Left && slot.Rect.Right <= work.Right, $"x out of monitor: {slot}");
                Assert.IsTrue(slot.Rect.Top >= work.Top && slot.Rect.Bottom <= work.Bottom, $"y out of monitor: {slot}");
            }
        }
    }

    [TestMethod]
    public void Arrange_HiddenTiles_AreParkedOnTheEdgeTheyWouldScrollInFrom()
    {
        StripArrangement result = StripLayout.Arrange(Work, Dpi, tileCount: 6, first: 2, reveal: NoReveal);

        Assert.AreEqual(result.Slots[2].Rect, result.Slots[0].Rect, "tiles left of the view park in the left column");
        Assert.AreEqual(result.Slots[4].Rect, result.Slots[5].Rect, "tiles right of the view park in the right column");
    }

    [TestMethod]
    public void Arrange_OnAHighDpiMonitor_ScalesTheGapAndMinimumWidth()
    {
        // At 200% the minimum tile is twice as wide, so only one fits across 1920 physical pixels.
        StripArrangement result = StripLayout.Arrange(Work, dpi: 192, tileCount: 2, first: 0, reveal: NoReveal);

        Assert.AreEqual(1, result.VisibleCount);
        Assert.AreEqual(new TileRect(2 * Gap, 2 * Gap, 1920 - 2 * Gap, 1040 - 2 * Gap), result.Slots[0].Rect);
    }

    [TestMethod]
    public void IsNear_WithinTheTolerance_MatchesAndBeyondItDoesNot()
    {
        var column = new TileRect(8, 8, 637, 1024);

        Assert.IsTrue(column.IsNear(new TileRect(9, 7, 639, 1024), tolerance: 2), "a pixel or two of jitter is still the column");
        Assert.IsFalse(column.IsNear(new TileRect(8, 8, 637, 1000), tolerance: 2), "a resized edge is not");
        Assert.IsFalse(column.IsNear(new TileRect(300, 8, 929, 1024), tolerance: 2), "a moved window is not");
    }

    [TestMethod]
    public void Arrange_OnAMonitorNarrowerThanTheMinimumTile_StillShowsOneTile()
    {
        var portraitSliver = new TileRect(0, 0, 400, 1000);

        StripArrangement result = StripLayout.Arrange(portraitSliver, Dpi, tileCount: 2, first: 0, reveal: 1);

        Assert.AreEqual(1, result.VisibleCount);
        Assert.AreEqual(1, result.First);
        Assert.IsTrue(result.Slots[1].Visible);
    }
}
