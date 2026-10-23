using System;
using System.Collections.Generic;
using System.Drawing;

namespace Winland.Indicator;

/// <summary>
/// The shape of one badge: a rectangle that hugs its docked edge, laid out ALONG that edge — left to right
/// on the top or bottom edge, top to bottom on the left or right edge. First comes the drag
/// <see cref="Grip"/>, then the items in order.
///
/// Across the edge the badge is always <see cref="BaseThickness"/> thick, and its first
/// <see cref="BaseTuck"/> pixels sit past the work-area margin. The grip and cells are centred in the part
/// that stays visible. Along the edge it is as long as its contents need, never shorter than it is thick.
/// Every measure here is at 96 DPI and scaled by the monitor's own.
/// </summary>
internal sealed class BadgeLayout
{
    public const int BaseThickness = 24;   // across the docked edge
    public const int BaseTuck = 6;         // how much of that hides past the work-area margin
    public const int BaseCornerRadius = 7; // the corners away from the edge
    public const float BaseFontSize = 11f; // glyph em size, in pixels

    public const float BaseGripDot = 2f;   // grip dot diameter
    public const float BaseGripPitch = 4f; // grip dots, centre to centre

    private const int BaseEndPadding = 6;  // before the grip and after the last item
    private const int BaseGap = 8;         // between the grip and the first item, and between items
    private const int BaseRowHeight = 11;  // one item's glyphs, stacked on a left/right edge
    private const int BaseGlyphWidth = 7;  // one character, on a top/bottom edge
    private const float BaseGripLength = BaseGripPitch + BaseGripDot; // two dots along the edge

    private BadgeLayout(Size size, RectangleF grip, RectangleF gripArea, IReadOnlyList<RectangleF> cells, float scale)
    {
        Size = size;
        Grip = grip;
        GripArea = gripArea;
        Cells = cells;
        Scale = scale;
    }

    /// <summary>The whole badge, tucked part included.</summary>
    public Size Size { get; }

    /// <summary>Where the grip's dots are centred, in badge coordinates.</summary>
    public RectangleF Grip { get; }

    /// <summary>
    /// Where a press starts a drag: the badge's leading end, from its start through the grip, across its
    /// whole thickness. A press anywhere else does nothing.
    /// </summary>
    public RectangleF GripArea { get; }

    /// <summary>Where each item's glyph is centred, in badge coordinates, in item order.</summary>
    public IReadOnlyList<RectangleF> Cells { get; }

    /// <summary>The monitor's DPI over 96.</summary>
    public float Scale { get; }

    /// <summary>The badge's extent along <paramref name="edge"/>.</summary>
    public int Along(DockEdge edge) => IsVertical(edge) ? Size.Height : Size.Width;

    public static bool IsVertical(DockEdge edge) => edge is DockEdge.Left or DockEdge.Right;

    public static BadgeLayout For(IReadOnlyList<string> items, DockEdge edge, int dpi)
    {
        float scale = dpi / 96f;
        float thickness = BaseThickness * scale;
        float tuck = BaseTuck * scale;
        bool vertical = IsVertical(edge);

        // The visible band across the edge: the tucked strip is on the docked side.
        float bandStart = edge is DockEdge.Left or DockEdge.Top ? tuck : 0;
        float band = thickness - tuck;

        RectangleF Cell(float at, float length) => vertical
            ? new RectangleF(bandStart, at, band, length)
            : new RectangleF(at, bandStart, length, band);

        float cursor = BaseEndPadding * scale;
        float gripLength = BaseGripLength * scale;
        RectangleF grip = Cell(cursor, gripLength);
        cursor += gripLength;
        float gripEnd = cursor + BaseGap * scale / 2; // the press area reaches halfway to the first item

        // Every cell is exactly its glyphs' extent, so the one gap is the whole visible spacing.
        var cells = new List<RectangleF>(items.Count);
        foreach (string item in items)
        {
            cursor += BaseGap * scale;
            float length = (vertical ? BaseRowHeight : BaseGlyphWidth * Math.Max(1, item.Length)) * scale;
            cells.Add(Cell(cursor, length));
            cursor += length;
        }

        float content = cursor + BaseEndPadding * scale;
        int along = (int)Math.Ceiling(Math.Max(thickness, content));
        int across = (int)Math.Round(thickness);
        RectangleF gripArea = vertical
            ? new RectangleF(0, 0, across, gripEnd)
            : new RectangleF(0, 0, gripEnd, across);

        return new BadgeLayout(vertical ? new Size(across, along) : new Size(along, across), grip, gripArea, cells, scale);
    }
}
