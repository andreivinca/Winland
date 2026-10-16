using System;
using System.Globalization;

namespace Winland.Indicator;

/// <summary>Which margin of a monitor's work area a circle is flush against.</summary>
internal enum DockEdge
{
    Left,
    Top,
    Right,
    Bottom
}

/// <summary>
/// Where one monitor's circle sits: the edge it is docked to, plus how far along that edge it is
/// (0 = the left/top end of the travel, 1 = the right/bottom end). The position is a fraction rather
/// than a pixel coordinate so it survives a resolution, DPI or work-area change — the circle keeps its
/// place on the edge instead of drifting or landing off-screen.
///
/// <see cref="Format"/> / <see cref="TryParse"/> are the persisted text form (the value half of a line
/// in the dock state file); they live here so the format is defined next to the type it describes.
/// </summary>
internal readonly record struct DockState(DockEdge Edge, double Offset)
{
    /// <summary>Where a monitor's circle appears before the user has ever dragged it: along the top
    /// edge, near the right (80% across) so it sits in the top-right area without touching the edge.</summary>
    public static readonly DockState Default = new(DockEdge.Top, 0.8);

    public string Format() =>
        $"{Edge} {Offset.ToString("0.###", CultureInfo.InvariantCulture)}";

    public static bool TryParse(string text, out DockState dock)
    {
        dock = Default;

        string[] parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !Enum.TryParse(parts[0], ignoreCase: true, out DockEdge edge)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double offset))
        {
            return false;
        }

        dock = new DockState(edge, Math.Clamp(offset, 0, 1));
        return true;
    }
}
