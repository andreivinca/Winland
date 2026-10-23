using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Winland.Indicator;

/// <summary>
/// One monitor's on-screen workspace badge: a translucent white rectangle docked to an edge of that
/// monitor, listing its workspaces (see <see cref="WorkspaceStack"/>), drawn as a layered window so its
/// antialiased corners blend with whatever is behind it. The side against the edge is square and tucks
/// past it; the far side has rounded corners. Items run along the edge (<see cref="BadgeLayout"/>).
///
/// It is deliberately invisible to window management. The window has an EMPTY title and the
/// WS_EX_TOOLWINDOW style, which is exactly what winland's "is this an app window" filter rejects — so
/// it is never minimized, moved or counted as a workspace member. It
/// also carries WS_EX_NOACTIVATE, so clicking and dragging it never takes focus away from the app the
/// user is working in, and is topmost so it stays visible above ordinary windows.
///
/// Dragging: only the grip at the badge's leading end starts a drag (<see cref="BadgeLayout.GripArea"/>); a
/// press anywhere else on the badge does nothing. The badge is always docked to one edge of its monitor's
/// work area and slides ALONG that
/// edge; motion perpendicular to the edge is ignored until the cursor comes close enough to another
/// edge, which re-docks it there (<see cref="DockFor"/>). It never leaves its monitor. The window only
/// reports the gesture (<see cref="DragStarted"/>, <see cref="DragMoved"/>, <see cref="DragEnded"/>) and
/// draws what it is told (<see cref="ShowBadge"/>): which circle a drag moves — the pressed one, or another
/// monitor's when a workspace is carried there — is the app's decision (see <see cref="WorkspaceDrag"/>).
///
/// Extension point: every message this window cares about is handled in the one
/// <see cref="WndProc"/> below. A right-click menu is the intended next addition — a WM_RBUTTONUP case
/// showing a ContextMenuStrip is all it takes; nothing else here needs to change.
/// </summary>
internal sealed class IndicatorWindow : Form
{
    // Geometry at 96 DPI; every one of these is scaled by the monitor's own DPI (see Scaled). The badge's
    // own measures are in BadgeLayout.
    private const int BaseDockThreshold = 80;   // how near an edge the cursor must come to dock there

    private const int BadgeAlpha = 64;          // ~25% opaque white — a hint, not a widget
    private const int GlyphAlpha = 170;         // the items stay stronger than their backing, still soft
    private const int ActiveGlyphAlpha = 230;   // the on-screen workspace: its number in white, not black

    private MonitorInfo _monitor;
    private DockState _dock;
    private IReadOnlyList<StackItem> _items = [new(WorkspaceStack.Nothing, false)]; // until told otherwise
    private bool _visible = true;               // false: draw nothing (the workspace is on another badge)
    private bool _dragging;

    /// <summary>
    /// The left button went down on the grip: a drag begins. The circle keeps the mouse until it ends. The
    /// argument is the grab point: how far along the edge from the badge's leading end it was pressed, at 96
    /// DPI — the drag keeps that point under the cursor (<see cref="DockFor"/>).
    /// </summary>
    public event Action<float>? DragStarted;

    /// <summary>The mouse moved during the drag; the cursor, in screen coordinates.</summary>
    public event Action<Point>? DragMoved;

    /// <summary>The drag is over: the button was released, or the mouse was taken away from the circle.</summary>
    public event Action? DragEnded;

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

    /// <summary>The monitor this circle belongs to.</summary>
    public MonitorInfo Monitor => _monitor;

    /// <summary>Where the badge currently sits, for persistence.</summary>
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

    /// <summary>
    /// Show <paramref name="items"/> (see <see cref="WorkspaceStack"/>) docked at <paramref name="dock"/> —
    /// or nothing at all when <paramref name="visible"/> is false. One call is one frame, so a badge never
    /// shows its new contents in its old place or the other way round.
    /// </summary>
    public void ShowBadge(DockState dock, IReadOnlyList<StackItem> items, bool visible)
    {
        if (dock == _dock && items.SequenceEqual(_items) && visible == _visible)
        {
            return;
        }

        _dock = dock;
        _items = items;
        _visible = visible;
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
                if (!OnGrip(m.LParam))
                {
                    return; // only the grip drags; the rest of the badge ignores the press
                }

                _dragging = true;
                Capture = true; // keep receiving moves once the cursor leaves the badge — or its monitor
                Point pressed = ClientPoint(m.LParam);
                int grab = BadgeLayout.IsVertical(_dock.Edge) ? pressed.Y : pressed.X;
                DragStarted?.Invoke(grab * 96f / _monitor.Dpi);
                return;

            case WM_SETCURSOR when (m.LParam.ToInt64() & 0xFFFF) == HTCLIENT:
                // The move cursor over the grip says "drag here"; the arrow everywhere else.
                Cursor.Current = _dragging || OnGrip(PointToClient(Cursor.Position)) ? Cursors.SizeAll : Cursors.Default;
                m.Result = new IntPtr(1);
                return;

            case WM_MOUSEMOVE:
                if (_dragging)
                {
                    // Screen coordinates, not the message's client ones: the circle moves under the
                    // cursor as we drag, which would make client coordinates chase themselves.
                    DragMoved?.Invoke(Cursor.Position);
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

    /// <summary>Whether a mouse message's client point (packed in <paramref name="lParam"/>) is on the grip.</summary>
    private bool OnGrip(IntPtr lParam) => OnGrip(ClientPoint(lParam));

    /// <summary>A mouse message's client point, packed in its <paramref name="lParam"/>.</summary>
    private static Point ClientPoint(IntPtr lParam)
    {
        long packed = lParam.ToInt64();
        return new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    private bool OnGrip(Point client) => LayoutFor(_dock.Edge).GripArea.Contains(client);

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Capture = false; // re-enters WndProc via WM_CAPTURECHANGED, which the flag above already ended
        DragEnded?.Invoke();
    }

    /// <summary>
    /// Where the circle docks for a cursor at <paramref name="cursor"/> (screen coordinates), held at
    /// <paramref name="grab"/> (96-DPI distance from the badge's leading end, see <see cref="DragStarted"/>).
    /// The edge only changes when the cursor comes within the dock threshold of one — otherwise the drag
    /// slides the circle along the edge it already occupies, and motion away from that edge does nothing.
    /// </summary>
    public DockState DockFor(Point cursor, float grab)
    {
        (DockEdge edge, int distance) = NearestEdge(_monitor.Work, cursor);
        DockEdge docked = distance <= Scaled(BaseDockThreshold) ? edge : _dock.Edge;

        // The grab point follows the cursor's coordinate PARALLEL to the docked edge, so the badge doesn't
        // jump under the cursor at the press; clamped so it always stays fully inside the work area —
        // which is what keeps it on its own monitor. Its length along that edge depends on the edge (items
        // run along it). The grip is at the leading end on every edge, so the grab holds across a re-dock.
        Rectangle field = Field();
        int along = LayoutFor(docked).Along(docked);
        double held = grab * _monitor.Dpi / 96.0;
        double offset = BadgeLayout.IsVertical(docked)
            ? Fraction(cursor.Y - held - field.Top, field.Height - along)
            : Fraction(cursor.X - held - field.Left, field.Width - along);

        return new DockState(docked, offset);
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
    /// The rectangle the badge's box may occupy: the work area grown by the tuck, so a docked badge sits
    /// partly hidden beyond the margin — including at the ends of the edge it is docked to.
    /// </summary>
    private Rectangle Field() =>
        Rectangle.Inflate(_monitor.Work, Scaled(BadgeLayout.BaseTuck), Scaled(BadgeLayout.BaseTuck));

    private BadgeLayout LayoutFor(DockEdge edge) =>
        BadgeLayout.For(_items.Select(i => i.Text).ToList(), edge, _monitor.Dpi);

    private Rectangle BadgeBounds(BadgeLayout layout)
    {
        Rectangle field = Field();
        Size size = layout.Size;
        int along = (int)Math.Round(_dock.Offset * Math.Max(0, field.Width - size.Width));
        int down = (int)Math.Round(_dock.Offset * Math.Max(0, field.Height - size.Height));

        return _dock.Edge switch
        {
            DockEdge.Left => new Rectangle(new Point(field.Left, field.Top + down), size),
            DockEdge.Right => new Rectangle(new Point(field.Right - size.Width, field.Top + down), size),
            DockEdge.Top => new Rectangle(new Point(field.Left + along, field.Top), size),
            _ => new Rectangle(new Point(field.Left + along, field.Bottom - size.Height), size)
        };
    }

    private int Scaled(int basePixels) => (int)Math.Round(basePixels * _monitor.Dpi / 96.0);

    private void Redraw()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        BadgeLayout layout = LayoutFor(_dock.Edge);
        Rectangle bounds = BadgeBounds(layout);
        // PREMULTIPLIED alpha, because that is what ULW_ALPHA composites: a plain ARGB surface gives
        // every semi-transparent edge pixel a bright halo that reads as a border around the badge.
        using var surface = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using (Graphics g = Graphics.FromImage(surface))
        {
            DrawBadge(g, layout);
        }

        Present(surface, bounds);
    }

    /// <summary>
    /// Draw the badge: a translucent white rectangle, rounded on the side away from its edge, with each
    /// item in soft black bold Segoe UI in its own cell — the tray icon's visual language, only see-through.
    /// </summary>
    private void DrawBadge(Graphics g, BadgeLayout layout)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);
        if (!_visible)
        {
            return; // fully transparent: nothing to see, and the mouse passes through
        }

        using (GraphicsPath shape = Shape(layout.Size, _dock.Edge, BadgeLayout.BaseCornerRadius * layout.Scale))
        using (var backing = new SolidBrush(Color.FromArgb(BadgeAlpha, Color.White)))
        {
            g.FillPath(backing, shape);
        }

        using var ink = new SolidBrush(Color.FromArgb(GlyphAlpha, Color.Black));
        DrawGrip(g, ink, layout.Grip, BadgeLayout.IsVertical(_dock.Edge), layout.Scale);

        using var font = new Font("Segoe UI", BadgeLayout.BaseFontSize * layout.Scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };

        // Segoe UI's digits sit high: nudge each cell down a hair, as the tray icon does. The workspace on
        // screen is the one number drawn in white instead of black — nothing else marks it. Each item is
        // drawn centred on a POINT, not in its cell's rectangle: a cell is the glyphs' bare extent, and GDI+
        // would trim or wrap text that, with its side bearings, overflows a layout rectangle.
        float nudge = layout.Scale / 2;
        using var activeInk = new SolidBrush(Color.FromArgb(ActiveGlyphAlpha, Color.White));
        for (int i = 0; i < _items.Count; i++)
        {
            RectangleF cell = layout.Cells[i];
            var centre = new PointF(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2 + nudge);
            g.DrawString(_items[i].Text, font, _items[i].Active ? activeInk : ink, centre, format);
        }
    }

    /// <summary>
    /// Draw the drag grip: two rows of three dots running across the edge — so the grip reads as a
    /// separate handle beside the items, whichever way they run.
    /// </summary>
    private static void DrawGrip(Graphics g, Brush ink, RectangleF cell, bool vertical, float scale)
    {
        float dot = BadgeLayout.BaseGripDot * scale;
        float pitch = BadgeLayout.BaseGripPitch * scale;
        int alongCount = 2;         // along the edge
        int acrossCount = 3;        // across it
        PointF centre = new(cell.Left + cell.Width / 2, cell.Top + cell.Height / 2);

        for (int a = 0; a < alongCount; a++)
        {
            for (int c = 0; c < acrossCount; c++)
            {
                float alongOffset = (a - (alongCount - 1) / 2f) * pitch;
                float acrossOffset = (c - (acrossCount - 1) / 2f) * pitch;
                float x = centre.X + (vertical ? acrossOffset : alongOffset);
                float y = centre.Y + (vertical ? alongOffset : acrossOffset);
                g.FillEllipse(ink, x - dot / 2, y - dot / 2, dot, dot);
            }
        }
    }

    /// <summary>
    /// The badge's outline: square on the side against <paramref name="edge"/> (that side tucks past the
    /// margin), the two corners of the opposite side rounded by <paramref name="radius"/>.
    /// </summary>
    private static GraphicsPath Shape(Size size, DockEdge edge, float radius)
    {
        // Inset half a pixel so the antialiased outline isn't clipped by the surface.
        var box = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
        float d = Math.Min(2 * radius, Math.Min(box.Width, box.Height));

        bool roundTopLeft = edge is DockEdge.Bottom or DockEdge.Right;
        bool roundTopRight = edge is DockEdge.Bottom or DockEdge.Left;
        bool roundBottomRight = edge is DockEdge.Top or DockEdge.Left;
        bool roundBottomLeft = edge is DockEdge.Top or DockEdge.Right;

        var path = new GraphicsPath();
        AddCorner(path, roundTopLeft, new RectangleF(box.Left, box.Top, d, d), 180, new PointF(box.Left, box.Top));
        AddCorner(path, roundTopRight, new RectangleF(box.Right - d, box.Top, d, d), 270, new PointF(box.Right, box.Top));
        AddCorner(path, roundBottomRight, new RectangleF(box.Right - d, box.Bottom - d, d, d), 0, new PointF(box.Right, box.Bottom));
        AddCorner(path, roundBottomLeft, new RectangleF(box.Left, box.Bottom - d, d, d), 90, new PointF(box.Left, box.Bottom));
        path.CloseFigure();
        return path;
    }

    private static void AddCorner(GraphicsPath path, bool round, RectangleF arc, float startAngle, PointF square)
    {
        if (round && arc.Width > 0)
        {
            path.AddArc(arc, startAngle, 90);
        }
        else
        {
            path.AddLine(square, square);
        }
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

    private const int WM_SETCURSOR = 0x0020;
    private const int HTCLIENT = 1;
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
