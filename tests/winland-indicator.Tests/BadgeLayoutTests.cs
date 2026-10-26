using System.Drawing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Winland.Indicator.Tests;

[TestClass]
public sealed class BadgeLayoutTests
{
    private static readonly string[] Three = ["1", "•", "17"];

    [TestMethod]
    [DataRow((int)DockEdge.Top)]
    [DataRow((int)DockEdge.Bottom)]
    public void OnATopOrBottomEdge_ItemsRunLeftToRight_InAWideBadge(int edge)
    {
        BadgeLayout layout = BadgeLayout.For(Three, (DockEdge)edge, dpi: 96);

        Assert.AreEqual(BadgeLayout.BaseThickness, layout.Size.Height);
        Assert.IsGreaterThan(layout.Size.Height, layout.Size.Width);
        Assert.IsLessThanOrEqualTo(layout.Cells[1].Left, layout.Cells[0].Right);
        Assert.IsLessThanOrEqualTo(layout.Cells[2].Left, layout.Cells[1].Right);
        Assert.IsGreaterThan(layout.Cells[0].Width, layout.Cells[2].Width); // two digits need a wider cell
    }

    [TestMethod]
    [DataRow((int)DockEdge.Left)]
    [DataRow((int)DockEdge.Right)]
    public void OnALeftOrRightEdge_ItemsStackTopToBottom_FirstItemUp(int edge)
    {
        BadgeLayout layout = BadgeLayout.For(Three, (DockEdge)edge, dpi: 96);

        Assert.AreEqual(BadgeLayout.BaseThickness, layout.Size.Width);
        Assert.IsGreaterThan(layout.Size.Width, layout.Size.Height);
        Assert.IsLessThanOrEqualTo(layout.Cells[1].Top, layout.Cells[0].Bottom);
        Assert.IsLessThanOrEqualTo(layout.Cells[2].Top, layout.Cells[1].Bottom);
    }

    [TestMethod]
    public void CellsSitInTheVisibleBand_AwayFromTheTuckedSide()
    {
        RectangleF top = BadgeLayout.For(["1"], DockEdge.Top, 96).Cells[0];
        RectangleF bottom = BadgeLayout.For(["1"], DockEdge.Bottom, 96).Cells[0];

        Assert.AreEqual(BadgeLayout.BaseTuck, top.Top, 0.01);
        Assert.AreEqual(0, bottom.Top, 0.01);
        Assert.AreEqual(BadgeLayout.BaseThickness - BadgeLayout.BaseTuck, top.Height, 0.01);
    }

    [TestMethod]
    [DataRow((int)DockEdge.Top)]
    [DataRow((int)DockEdge.Left)]
    public void TheGripComesFirst_AtTheLeadingEnd_BeforeEveryItem(int edge)
    {
        BadgeLayout layout = BadgeLayout.For(Three, (DockEdge)edge, 96);
        bool vertical = BadgeLayout.IsVertical((DockEdge)edge);

        float gripEnd = vertical ? layout.Grip.Bottom : layout.Grip.Right;
        float firstItem = vertical ? layout.Cells[0].Top : layout.Cells[0].Left;
        Assert.IsLessThanOrEqualTo(firstItem, gripEnd);
        Assert.IsTrue(layout.GripArea.Contains(new PointF(1, 1)));                         // the very start
        Assert.IsFalse(layout.GripArea.Contains(PointF.Add(layout.Cells[0].Location, new SizeF(1, 1)))); // not an item
    }

    [TestMethod]
    [DataRow((int)DockEdge.Top)]
    [DataRow((int)DockEdge.Left)]
    public void TheGripToFirstItem_IsTheSameGapAsBetweenItems(int edge)
    {
        BadgeLayout layout = BadgeLayout.For(Three, (DockEdge)edge, 144);
        bool vertical = BadgeLayout.IsVertical((DockEdge)edge);
        float Start(RectangleF r) => vertical ? r.Top : r.Left;
        float End(RectangleF r) => vertical ? r.Bottom : r.Right;

        float gripGap = Start(layout.Cells[0]) - End(layout.Grip);
        Assert.IsGreaterThan(0f, gripGap);
        for (int i = 1; i < layout.Cells.Count; i++)
        {
            Assert.AreEqual(gripGap, Start(layout.Cells[i]) - End(layout.Cells[i - 1]), 0.01);
        }
    }

    [TestMethod]
    public void TheGripArea_SpansTheWholeThickness()
    {
        BadgeLayout layout = BadgeLayout.For(["1"], DockEdge.Top, 96);

        Assert.AreEqual(0, layout.GripArea.Top, 0.01);
        Assert.AreEqual(BadgeLayout.BaseThickness, layout.GripArea.Height, 0.01);
    }

    [TestMethod]
    public void EverythingScalesWithTheMonitorsDpi()
    {
        BadgeLayout at96 = BadgeLayout.For(Three, DockEdge.Top, 96);
        BadgeLayout at192 = BadgeLayout.For(Three, DockEdge.Top, 192);

        Assert.AreEqual(at96.Size.Height * 2, at192.Size.Height);
        Assert.AreEqual(at96.Size.Width * 2, at192.Size.Width, 1);
    }

    [TestMethod]
    [DataRow((int)DockEdge.Top, 96)]
    [DataRow((int)DockEdge.Bottom, 96)]
    [DataRow((int)DockEdge.Left, 144)]
    [DataRow((int)DockEdge.Right, 192)]
    public void EachUnderline_SitsCentredBelowItsItem_InTheVisiblePartOfTheBadge(int edge, int dpi)
    {
        BadgeLayout layout = BadgeLayout.For(Three, (DockEdge)edge, dpi);
        float tuck = BadgeLayout.BaseTuck * layout.Scale;
        var visible = (DockEdge)edge switch
        {
            DockEdge.Top => new RectangleF(0, tuck, layout.Size.Width, layout.Size.Height - tuck),
            DockEdge.Bottom => new RectangleF(0, 0, layout.Size.Width, layout.Size.Height - tuck),
            DockEdge.Left => new RectangleF(tuck, 0, layout.Size.Width - tuck, layout.Size.Height),
            _ => new RectangleF(0, 0, layout.Size.Width - tuck, layout.Size.Height)
        };

        for (int i = 0; i < layout.Cells.Count; i++)
        {
            RectangleF cell = layout.Cells[i];
            RectangleF line = layout.Underlines[i];

            Assert.IsGreaterThan(cell.Top + cell.Height / 2, line.Top);                                  // below the glyphs' centre
            Assert.AreEqual(cell.Left + cell.Width / 2, line.Left + line.Width / 2, 0.01);              // centred under them
            Assert.IsTrue(visible.Contains(line), $"underline {i} {line} leaves the visible {visible}"); // never tucked away
            if (i + 1 < layout.Cells.Count && BadgeLayout.IsVertical((DockEdge)edge))
            {
                Assert.IsLessThanOrEqualTo(layout.Cells[i + 1].Top, line.Bottom);                         // clear of the next row
            }
        }
    }

    [TestMethod]
    public void AnUnderline_IsAsWideAsItsItemsGlyphs()
    {
        BadgeLayout layout = BadgeLayout.For(Three, DockEdge.Top, 96);

        Assert.AreEqual(layout.Cells[0].Width, layout.Underlines[0].Width, 0.01);
        Assert.AreEqual(layout.Cells[2].Width, layout.Underlines[2].Width, 0.01); // two digits, a longer line
    }
}
