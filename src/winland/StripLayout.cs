using System;
using System.Collections.Generic;
using System.Linq;

namespace Winland;

/// <summary>A screen rectangle in physical pixels; <see cref="Right"/> and <see cref="Bottom"/> are exclusive.</summary>
internal readonly record struct TileRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Whether the point lies inside (right and bottom edges excluded).</summary>
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    /// <summary>Whether every edge is within <paramref name="tolerance"/> pixels of <paramref name="other"/>'s.</summary>
    public bool IsNear(TileRect other, int tolerance) =>
        Math.Abs(Left - other.Left) <= tolerance
        && Math.Abs(Top - other.Top) <= tolerance
        && Math.Abs(Right - other.Right) <= tolerance
        && Math.Abs(Bottom - other.Bottom) <= tolerance;
}

/// <summary>
/// Where one tile goes. A <see cref="Visible"/> tile is shown at <see cref="Rect"/>; a hidden one is put
/// away, and <see cref="Rect"/> is the column it will scroll in through — so even a hidden tile that the
/// user restores by hand (e.g. from the taskbar) reappears on its own monitor.
/// </summary>
internal readonly record struct TileSlot(TileRect Rect, bool Visible);

/// <summary>A strip's arrangement: its scroll position, how many columns fit, and one slot per tile.</summary>
internal sealed record StripArrangement(int First, int VisibleCount, IReadOnlyList<TileSlot> Slots)
{
    public static readonly StripArrangement Empty = new(0, 0, Array.Empty<TileSlot>());
}

/// <summary>
/// The scrolling-strip layout, as pure geometry. A workspace's tiles stand side by side in one row of
/// full-height columns across its monitor's work area:
///
///  * While every tile can be at least <see cref="BaseMinTileWidth"/> wide, they all show and share the
///    width equally — one tile fills the monitor, two take half each, and so on.
///  * Past that, columns keep the width of a full view and the row overflows: only the tiles inside the
///    viewport show, and the viewport scrolls by whole tiles. Nothing is ever drawn outside the monitor,
///    so tiles can't spill onto a neighbouring screen.
///
/// Sizes are at 96 DPI and scale with the monitor's DPI, so tiles have the same physical size everywhere.
/// </summary>
internal static class StripLayout
{
    /// <summary>Space around and between tiles, at 96 DPI.</summary>
    public const int BaseGap = 8;

    /// <summary>Tiles share a monitor only while each stays at least this wide (96 DPI); then the strip scrolls.</summary>
    public const int BaseMinTileWidth = 560;

    private const int BaseDpi = 96;

    /// <summary>
    /// Arrange <paramref name="tileCount"/> tiles on a monitor's <paramref name="work"/> area. The viewport
    /// starts at tile <paramref name="first"/> (clamped to a full view) and scrolls the minimum needed to
    /// show tile <paramref name="reveal"/>; pass -1 to keep the current scroll position.
    /// </summary>
    public static StripArrangement Arrange(TileRect work, int dpi, int tileCount, int first, int reveal)
    {
        if (tileCount <= 0 || work.Width <= 0 || work.Height <= 0)
        {
            return StripArrangement.Empty;
        }

        int gap = Scale(BaseGap, dpi);
        int visible = Math.Min(tileCount, Capacity(work.Width, gap, Scale(BaseMinTileWidth, dpi)));
        int scrolled = ScrollTo(first, reveal, tileCount, visible);

        var slots = new TileSlot[tileCount];
        for (int i = 0; i < tileCount; i++)
        {
            int column = i - scrolled;
            bool shown = column >= 0 && column < visible;
            slots[i] = new TileSlot(Column(work, gap, visible, Math.Clamp(column, 0, visible - 1)), shown);
        }

        return new StripArrangement(scrolled, visible, slots);
    }

    /// <summary>
    /// <paramref name="strip"/> with tile <paramref name="fullscreen"/> filling the whole <paramref name="work"/>
    /// area, inside the usual gap — where a lone tile would stand — instead of its column. The taskbar stays
    /// visible. Nothing else moves: the strip keeps its scroll position and the tile keeps its column,
    /// reserved behind it, so leaving fullscreen puts it straight back. A tile out of view stays hidden in
    /// its parking column; -1 (or any index outside the strip) means no fullscreen tile.
    /// </summary>
    public static StripArrangement WithFullscreen(StripArrangement strip, int fullscreen, TileRect work, int dpi)
    {
        if (fullscreen < 0 || fullscreen >= strip.Slots.Count || !strip.Slots[fullscreen].Visible)
        {
            return strip;
        }

        TileSlot[] slots = strip.Slots.ToArray();
        slots[fullscreen] = new TileSlot(Column(work, Scale(BaseGap, dpi), columns: 1, column: 0), Visible: true);
        return strip with { Slots = slots };
    }

    /// <summary>How many columns of at least <paramref name="minTileWidth"/> fit; never fewer than one.</summary>
    private static int Capacity(int workWidth, int gap, int minTileWidth) =>
        Math.Max(1, (workWidth - gap) / (minTileWidth + gap));

    private static int ScrollTo(int first, int reveal, int tileCount, int visible)
    {
        int scrolled = Math.Clamp(first, 0, tileCount - visible);
        if (reveal < 0 || reveal >= tileCount)
        {
            return scrolled;
        }

        if (reveal < scrolled)
        {
            return reveal;
        }

        return reveal >= scrolled + visible ? reveal - visible + 1 : scrolled;
    }

    /// <summary>The rectangle of viewport column <paramref name="column"/> out of <paramref name="columns"/>.</summary>
    private static TileRect Column(TileRect work, int gap, int columns, int column)
    {
        // Spread the width left after the gaps proportionally, so rounding never leaves a ragged edge:
        // the last column always ends exactly one gap before the work area's right edge.
        long usable = Math.Max(columns, work.Width - (columns + 1) * gap);
        int origin = work.Left + gap + column * gap;
        int left = origin + (int)(usable * column / columns);
        int right = origin + (int)(usable * (column + 1) / columns);
        int top = work.Top + gap;
        int bottom = Math.Max(top + 1, work.Bottom - gap);

        return new TileRect(left, top, right, bottom);
    }

    private static int Scale(int value, int dpi) =>
        (int)Math.Round(value * (double)(dpi > 0 ? dpi : BaseDpi) / BaseDpi);
}
