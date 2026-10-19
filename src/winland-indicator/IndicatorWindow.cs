using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winland.Indicator;

/// <summary>
/// One monitor's on-screen workspace badge: a translucent white circle carrying that monitor's
/// workspace glyph, drawn as a layered window so its antialiased rim blends with whatever is behind it.
///
/// It is deliberately invisible to window management. The window has an EMPTY title and the
/// WS_EX_TOOLWINDOW style, which is exactly what winland's "is this an app window" filter (and the
/// show-desktop script) reject — so it is never minimized, moved or counted as a workspace member. It
/// also carries WS_EX_NOACTIVATE, so clicking and dragging it never takes focus away from the app the
/// user is working in, and is topmost so it stays visible above ordinary windows.
///
/// Dragging: the circle is always docked to one edge of its monitor's work area and slides ALONG that
/// edge; motion perpendicular to the edge is ignored until the cursor comes close enough to another
/// edge, which re-docks it there. It can therefore never be dragged off its monitor.
///
/// Extension point: every message this window cares about is handled in the one
/// <see cref="WndProc"/> below. A right-click menu is the intended next addition — a WM_RBUTTONUP case
/// showing a ContextMenuStrip is all it takes; nothing else here needs to change.
/// </summary>
internal sealed class IndicatorWindow : Form
{
    // Geometry at 96 DPI; every one of these is scaled by the monitor's own DPI (see Scaled).
    private const int BaseCircleSize = 24;      // diameter of the circle
    private const int BaseEdgeMargin = -6;      // negative: the circle tucks partly past the work-area edge
    private const int BaseDockThreshold = 80;   // how near an edge the cursor must come to dock there

    private const int CircleAlpha = 64;         // ~25% opaque white — a hint, not a widget
    private const int GlyphAlpha = 170;         // the number stays stronger than its disc, still soft
    private const float GlyphReferenceCircle = 32f; // the tray icon's circle, which the em sizes below fit

    private MonitorInfo _monitor;
    private DockState _dock;
    private int _workspace;                     // raw id from the event pipe; 0 = none/not known yet
    private bool _dragging;

    /// <summary>Raised when a drag ends, so the new <see cref="DockPosition"/> can be persisted.</summary>
    public event Action? DockPositionChanged;

    public IndicatorWindow(MonitorInfo monitor, DockState dock)
    {
        _monitor = monitor;
        _dock = dock;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; // UpdateLayeredWindow does all the placing
        ShowInTaskbar = false;
        TopMost = true;
        Text = string.Empty;    // untitled + tool window = ignored by every managed-window filter
    }

    /// <summary>The monitor this circle belongs to, by stable GDI device name (e.g. \\.\DISPLAY1).</summary>
    public string Device => _monitor.Device;

    /// <summary>Where the circle currently sits, for persistence.</summary>
    public DockState DockPosition => _dock;

    /// <summary>Never take focus: the user's app must stay active while they click or drag the circle.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            // TopMost is already in base.CreateParams (the Form property); these three are not.
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>
    /// Put the circle back in the topmost band. WS_EX_TOPMOST is granted at creation but not
    /// guaranteed for life: a display reset, DWM restart, or sleep/wake can silently demote an
    /// overlay window, after which it sits under ordinary windows until someone re-asserts it —
    /// which is why every presented frame does, plus a slow heartbeat for the idle stretches.
    /// </summary>
    public void EnsureOnTop()
    {
        if (IsHandleCreated)
        {
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }

    /// <summary>Show a new workspace id (raw, as it arrives over the event pipe).</summary>
    public void SetWorkspace(int workspace)
    {
        if (workspace == _workspace)
        {
            return;
        }

        _workspace = workspace;
        Redraw();
    }

    /// <summary>
    /// Adopt a changed work area or DPI (resolution change, docking, taskbar move). The dock edge and
    /// fractional offset are kept, so the circle stays where the user put it.
    /// </summary>
    public void ApplyMonitor(MonitorInfo monitor)
    {
        _monitor = monitor;
        Redraw();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Redraw(); // a layered window shows nothing at all until its surface is first supplied
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_LBUTTONDOWN:
                _dragging = true;
                Capture = true; // keep receiving moves once the cursor leaves the circle
                return;

            case WM_MOUSEMOVE:
                if (_dragging)
                {
                    // Screen coordinates, not the message's client ones: the window moves under the
                    // cursor as we drag, which would make client coordinates chase themselves.
                    DragTo(Cursor.Position);
                }

                return;

            case WM_LBUTTONUP:
                EndDrag();
                return;

            case WM_CAPTURECHANGED:
                EndDrag(); // capture can be taken from us; finish the drag rather than get stuck in it
                break;
        }

        base.WndProc(ref m);
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false; // re-enters WndProc via WM_CAPTURECHANGED, which the flag above already ended
        DockPositionChanged?.Invoke();
    }

    /// <summary>
    /// Place the circle for a cursor at <paramref name="cursor"/> (screen coordinates). The edge only
    /// changes when the cursor comes within the dock threshold of one — otherwise the drag slides the
    /// circle along the edge it already occupies, and motion away from that edge does nothing.
    /// </summary>
    private void DragTo(Point cursor)
    {
        (DockEdge edge, int distance) = NearestEdge(_monitor.Work, cursor);
        DockEdge docked = distance <= Scaled(BaseDockThreshold) ? edge : _dock.Edge;

        // The circle centres on the cursor's coordinate PARALLEL to the docked edge, clamped so it
        // always stays fully inside the work area — which is what keeps it on its own monitor.
        Rectangle field = Field();
        int size = Scaled(BaseCircleSize);
        double offset = docked is DockEdge.Left or DockEdge.Right
            ? Fraction(cursor.Y - size / 2.0 - field.Top, field.Height - size)
            : Fraction(cursor.X - size / 2.0 - field.Left, field.Width - size);

        _dock = new DockState(docked, offset);
        Redraw();
    }

    /// <summary>The edge of <paramref name="work"/> nearest the cursor, and its distance from it.</summary>
    private static (DockEdge Edge, int Distance) NearestEdge(Rectangle work, Point cursor)
    {
        (DockEdge Edge, int Distance)[] edges =
        {
            (DockEdge.Left, cursor.X - work.Left),
            (DockEdge.Right, work.Right - cursor.X),
            (DockEdge.Top, cursor.Y - work.Top),
            (DockEdge.Bottom, work.Bottom - cursor.Y)
        };

        return edges.MinBy(e => e.Distance);
    }

    private static double Fraction(double travelled, int travel) =>
        travel <= 0 ? 0 : Math.Clamp(travelled / travel, 0, 1);

    /// <summary>
    /// The rectangle the circle's box may occupy: the work area inset by the margin. The margin is
    /// negative, so the field reaches PAST every work-area edge and a docked circle sits partly
    /// hidden beyond the margin — including at the ends of the edge it is docked to.
    /// </summary>
    private Rectangle Field() => Rectangle.Inflate(_monitor.Work, -Scaled(BaseEdgeMargin), -Scaled(BaseEdgeMargin));

    private Rectangle CircleBounds()
    {
        Rectangle field = Field();
        int size = Scaled(BaseCircleSize);
        int along = (int)Math.Round(_dock.Offset * Math.Max(0, field.Width - size));
        int down = (int)Math.Round(_dock.Offset * Math.Max(0, field.Height - size));

        return _dock.Edge switch
        {
            DockEdge.Left => new Rectangle(field.Left, field.Top + down, size, size),
            DockEdge.Right => new Rectangle(field.Right - size, field.Top + down, size, size),
            DockEdge.Top => new Rectangle(field.Left + along, field.Top, size, size),
            _ => new Rectangle(field.Left + along, field.Bottom - size, size, size)
        };
    }

    private int Scaled(int basePixels) => (int)Math.Round(basePixels * _monitor.Dpi / 96.0);

    private void Redraw()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        Rectangle bounds = CircleBounds();
        // PREMULTIPLIED alpha, because that is what ULW_ALPHA composites: a plain ARGB surface gives
        // every semi-transparent edge pixel a bright halo that reads as a border around the circle.
        using var surface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(surface))
        {
            DrawBadge(g, bounds.Width);
        }

        Present(surface, bounds);
    }

    /// <summary>
    /// Draw the badge: a translucent white disc with an opaque black glyph — the tray icon's visual
    /// language, only see-through. The glyph is the workspace number, "S" for the roaming scratchpad,
    /// or an en dash when the monitor shows no workspace (or we have no news about it yet).
    /// </summary>
    private void DrawBadge(Graphics g, int size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        using var disc = new SolidBrush(Color.FromArgb(CircleAlpha, Color.White));
        g.FillEllipse(disc, 0.5f, 0.5f, size - 1f, size - 1f); // inset so the antialiased rim isn't clipped

        // Raw ids straight off the event pipe: int.MaxValue is winland's reserved scratchpad, and
        // anything below 1 means the monitor is showing nothing.
        string text = _workspace == int.MaxValue ? "S"
            : _workspace >= 1 ? _workspace.ToString()
            : "–";

        // Shrink the glyph so multi-digit numbers still fit — the tray icon's curve, scaled down a
        // step so the number sits small and quiet inside the disc.
        float emSize = size * (text.Length switch { <= 1 => 15f, 2 => 11f, _ => 8f }) / GlyphReferenceCircle;
        using var ink = new SolidBrush(Color.FromArgb(GlyphAlpha, Color.Black));
        using var font = new Font("Segoe UI", emSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        // Lay the text out in a box a hair taller than the circle: Segoe UI's digits sit high, and the
        // tray icon nudges them down the same way.
        g.DrawString(text, font, ink, new RectangleF(0, 0, size, size + size / GlyphReferenceCircle), format);
    }

    /// <summary>
    /// Hand the drawn surface to the compositor. UpdateLayeredWindow both moves/resizes the window and
    /// replaces its pixels in one call, so a drag never shows a torn or half-moved frame — and because
    /// the window keeps per-pixel alpha, the mouse only hits it where the circle is actually drawn.
    /// </summary>
    private void Present(Bitmap surface, Rectangle bounds)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            // A fully transparent background keeps the alpha channel intact; the surface is already
            // premultiplied (Format32bppPArgb), which is the layout ULW_ALPHA expects.
            hBitmap = surface.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, hBitmap);

            var position = new POINT { X = bounds.Left, Y = bounds.Top };
            var size = new SIZE { Cx = bounds.Width, Cy = bounds.Height };
            var origin = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255, // per-pixel alpha only — no extra global fade on top
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memoryDc, ref origin, 0,
                ref blend, ULW_ALPHA);
            EnsureOnTop(); // every fresh frame also reclaims the topmost band (see EnsureOnTop)
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (hBitmap != IntPtr.Zero)
            {
                DeleteObject(hBitmap);
            }

            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ----- Win32 -----

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_CAPTURECHANGED = 0x0215;

    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
