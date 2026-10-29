using System.Runtime.InteropServices;

namespace Winland.Common;

/// <summary>
/// The one name every Winland process gives a physical monitor: the monitor's device interface path, e.g.
/// <c>\\?\DISPLAY#DELA1C4#5&amp;340bf2b6&amp;0&amp;UID4357#{e6f07b5f-…}</c> — its model (from EDID) and the
/// connector it is plugged into. Winland keys workspace homes, what each monitor shows, the event pipe and
/// the indicator's dock positions on it.
///
/// Not the GDI display name (<c>\\.\DISPLAYn</c>): Windows reassigns those when monitors power off and on
/// again (or reconnect after the PC wakes), so keying on them swaps every workspace between monitors. Nor
/// the HMONITOR, which is reissued on every display change.
/// </summary>
public static class MonitorIdentity
{
    /// <summary>
    /// The stable id of the monitor on display source <paramref name="gdiDevice"/> (the szDevice of
    /// GetMonitorInfo, e.g. <c>\\.\DISPLAY1</c>). Something that is not a live display source — an id
    /// already, or a source Windows will not describe — is returned unchanged, so the call is idempotent:
    /// a command may name a monitor either way.
    /// </summary>
    public static string Of(string gdiDevice)
    {
        var monitors = new List<(string Id, uint Flags)>();
        var device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(gdiDevice, i, ref device, EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            monitors.Add((device.DeviceID ?? "", device.StateFlags));
            device = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        }

        return Choose(gdiDevice, monitors);
    }

    /// <summary>
    /// Pick the id among the monitors Windows lists on one display source: the first one that is attached
    /// and active (a mirrored source lists several; one is enough to tell sources apart). Falls back to
    /// <paramref name="gdiDevice"/> when none qualifies, or when an id would break the space- and
    /// '='-delimited lines it travels in (the event pipe, the dock file, move-workspace).
    /// </summary>
    public static string Choose(string gdiDevice, IEnumerable<(string Id, uint Flags)> monitors)
    {
        const uint live = DISPLAY_DEVICE_ATTACHED | DISPLAY_DEVICE_ACTIVE;
        string? id = monitors
            .Where(m => (m.Flags & live) == live)
            .Select(m => m.Id)
            .FirstOrDefault(IsUsableId);
        return id ?? gdiDevice;
    }

    private static bool IsUsableId(string id) =>
        id.Length > 0 && !id.Any(char.IsWhiteSpace) && !id.Contains('=');

    public const uint DISPLAY_DEVICE_ACTIVE = 0x1;
    public const uint DISPLAY_DEVICE_ATTACHED = 0x2;
    private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);
}
