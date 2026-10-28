# Feature Deep Dive: On-Screen Workspace Indicator

## 1. Objective

Tell the user, at a glance and on **every** monitor, which workspace that monitor is currently
showing — something the tray icon cannot do, because Windows 11 only gives tray apps the primary
monitor's taskbar.

The indicator is its own process, `winland-indicator`. It is mostly a **subscriber**: it reads
workspace state off `winland`'s broadcast event pipe and draws. Its one command back is a request
to **move a workspace to another monitor** when the user drags a circle onto it (§4). It holds no
window handles and needs no elevation. Winland works exactly as before without it.

## 2. What the User Sees

One small translucent badge per monitor, floating above ordinary windows, listing the workspaces
that live on that monitor (`WorkspaceStack`). Every item is drawn in the same ink (§2a); the one on
screen is **underlined**:

| Item | Meaning |
| --- | --- |
| `1`, `4`, `17`, … | a workspace homed on that monitor, not on screen right now |
| underlined number | the workspace on screen, in its place among the numbers |
| `S` (underlined) | appended while the roaming scratchpad is up there |
| `–` (en dash) | nothing to list — or no state has arrived yet |

At startup monitor 1 reads `1` and monitor 2 reads `2`, each underlined. A monitor that homes workspaces
1 and 4 and shows 4 reads `1 4` with the 4 underlined; with the scratchpad brought up there it reads
`1 4 S` with the `S` underlined, and the `S` goes away when the scratchpad does. A workspace on screen
there but homed on a monitor that is asleep is listed too, underlined. The underline is as wide as the
item's glyphs and sits just below them (`BadgeLayout.Underlines`), on every edge.

The badge is a rectangle that hugs its docked edge (`BadgeLayout`): the side against the edge is square
and tucks past it, the far side has rounded corners. The items run **along** the edge — left to right
on the top or bottom edge, stacked top to bottom (first item up) on the left or right edge — so the
badge grows with the number of workspaces. Ahead of the first item sits a **grip** — a small block of
dots at the left end (top or bottom edge) or the top end (left or right edge). Visually it is the tray icon's language — bold Segoe
UI — only faint (~25% opaque), so it sits on top of a window without hiding it.

## 2a. Light and Dark Mode

The badge follows Windows' **default app mode** (Settings → Personalization → Colors; the
`AppsUseLightTheme` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`).
`BadgeTheme` holds the colours:

| Mode | Badge | Items and underline |
| --- | --- | --- |
| Light (default, or value missing) | faint white | black |
| Dark | faint black | white |

Dark is the exact inverse of light at the same transparency, so only the hue changes. Switching modes
recolours every circle live: `IndicatorApp` listens for `SystemEvents.UserPreferenceChanged`
(category `General`), re-reads the value on the UI thread and hands the new theme to each window.

## 3. The Docking Interaction (the core rule)

The circle is **always docked to one margin** of its monitor's work area: it hugs that edge and
tucks slightly past it (~6 px at 96 DPI, so it sits a little hidden by the margin) — at the ends of
the edge too.

It is dragged **by its grip only**: press the left mouse button on the grip and drag. A press anywhere
else on the badge does nothing. The drag obeys one rule:

> **A drag slides the circle along its docked edge. Perpendicular motion does nothing.**
> If it is docked to the top margin and you drag *down*, it does not move down. Only when the mouse
> comes near the bottom margin does it dock to the bottom margin — and then it slides along that one.
> The same for the left and right margins.

Concretely, on each mouse-move during a drag:

1. Find the edge of the work area **nearest the cursor** and how far the cursor is from it.
2. If that distance is within the **dock threshold** (~80 px at 96 DPI), that edge becomes the docked
   edge. Otherwise the docked edge stays exactly what it was.
3. Place the circle on the docked edge so the point of the grip that was pressed stays under the
   cursor's coordinate **parallel** to that edge (the cursor's X for a top/bottom dock, its Y for a
   left/right dock) — the badge never jumps at the press — clamped so the circle stays
   fully inside the work area.

Because the docked position is always derived from the monitor's own work area and clamped to it,
**a circle never leaves its own monitor** — and because the window carries `WS_EX_NOACTIVATE`, dragging
it never takes focus from the app the user is working in.

## 4. Moving a Workspace to Another Monitor

Dragging a circle that shows a number onto another monitor moves that **whole workspace** there —
**live, the moment the cursor enters that monitor**, not on release. The rules are in `WorkspaceDrag`
(pure, unit-tested in `tests/winland-indicator.Tests`); `IndicatorApp` applies them.

**Which circle a drag moves.** The circle that was pressed keeps the mouse (capture) for the whole
drag, but the circle the drag *moves* — the **carrier** — is the circle of the monitor under the
cursor, docked there by the rule of §3. So the dragged badge seems to jump monitors while every window
stays on its own monitor.

**Entering another monitor** (with a workspace in hand) does, in one step:

1. That monitor's circle becomes the carrier: it docks at the cursor and shows the dragged workspace.
   Its old glyph is gone in that same frame.
2. `move-workspace <n> <device>` is sent at once.
3. The circle the drag came from goes back to where it was when the drag began and **shows nothing**
   until the snapshot that follows the move arrives (§6). Then it shows that monitor's previous
   workspace, or `–`.

The drag goes on: keep sliding along the new monitor's edges, or carry the workspace back. Re-entering
the first monitor moves the workspace back the same way, live.

**Never on two circles.** While a drag carrying workspace *n* is in effect, the carrier lists *n*
underlined (and what it showed before as a plain number, since that stays homed there), every other
badge drops
*n*, and any badge that the latest snapshot still credits with *showing* *n* is hidden (fully
transparent, so also click-through). Circles that hide are redrawn before those that appear, so *n* never
shows twice, not even for a frame.

**Release** only ends the drag: the carrier stays docked where it was let go, and every circle's dock
is saved (§5). The rule above stays in effect until a snapshot shows *n* on the carrier's monitor, or
3 s have passed (`SettleTimeoutMs`: Winland isn't running, or refused the move). Then the circles show
plain snapshots again. A lost capture ends the drag the same way, because every move it asked for has
already happened.

The `–` circle has nothing to carry and the `S` circle's scratchpad already roams with `Win+S`. A drag
of either stays with its own circle and moves nothing.

**Requests** leave in order on a worker thread (`LatestRequestSender`, through `WinlandClient`: 2 s to
connect, 10 s for the reply), so the circles never wait on Winland. A request still waiting when a
newer one is made is dropped in its favour: dragged back and forth quickly, only where the workspace
ends up matters, and an older move arriving late would undo a newer one. A failed request is logged.

What `winland` does for each move (`WorkspaceManager.MoveWorkspaceToMonitor`; details in
`workspaces.md` §7a):

1. **Re-home** the workspace to the target monitor — `Win+N` shows it there from now on.
2. Its floating windows on the old monitor are **carried** to the target, at the same place relative
   to the monitor. Floating windows the user put on any other monitor stay put.
3. The target **puts away** what it showed (that workspace stays homed there) and **lays out** the
   moved workspace's tiles.
4. The old monitor **goes back to the workspace it showed before**: of the workspaces homed on it that
   no monitor is showing, the most recently shown (`PreviousWorkspace`) — or nothing (`–`) when there
   is none.
5. Focus lands on the moved workspace. The **mouse is left alone** — it is in the user's hand, mid-drag.

Example: monitor 1 shows `3`, monitor 2 shows `5`. Drag monitor 1's circle across → as the cursor
crosses into monitor 2, monitor 2's circle shows `3` under the cursor, monitor 1's circle vanishes,
monitor 2 switches to workspace 3 with all its windows (5 is put away, still homed on monitor 2), and
monitor 1 switches to the workspace it showed before 3 — its circle reappearing with that number.

## 5. Persistence

Where each circle sits is remembered in a plain-text file next to the exe (the same convention as the
log), `winland-indicator-dock.txt`:

```
# <device> <edge> <offset along the edge, 0..1>
\\.\DISPLAY1 Top 0.8
\\.\DISPLAY2 Right 0.12
```

- Keyed by the **stable GDI device name**, so a monitor keeps its position across handle reissue.
- The position along the edge is a **fraction**, not a pixel coordinate, so a resolution, DPI or
  taskbar change keeps the circle where the user put it instead of drifting or landing off-screen.
- Written when a drag is released — every circle's dock, so a drag that ended on another monitor
  keeps its circle there. Read at startup. The default is `Top 0.8` (top edge, toward the
  right).
- Monitors that are not attached right now keep their remembered entry, so unplugging a screen never
  forgets where its circle lived. A missing or unreadable file is not an error — everything falls back
  to the default.

## 6. The Event Protocol

`winland` publishes on a second named pipe, `\\.\pipe\winland-events`
(`Ipc.EventsPipeName`), which is **outbound only** — nothing a subscriber could write is ever read.

- **Message**: one line per snapshot, space-separated `<device>=<shown>:<homed>` entries ordered
  left-to-right, e.g. `\\.\DISPLAY1=4:1,4 \\.\DISPLAY2=-1:`. Device names contain no spaces, so the format
  needs no escaping. `shown` is the **raw** number on screen: `-1` (`Ipc.NoWorkspace`) = nothing shown, `int.MaxValue` = the
  scratchpad. `homed` is the comma-separated, ascending list of workspaces whose home is that monitor
  (possibly empty). `MonitorWorkspaces` (in `Winland.Common`) is the one place it is written and read;
  turning it into the badge's items is the client's business.
- **On connect**: the current snapshot is sent immediately, so a subscriber that starts after
  Winland is instantly correct.
- **On change**: `WorkspaceManager.NotifyWorkspaceChanges` hands `WorkspaceSnapshot` to
  `EventServer.Publish`.
- **Latest wins**: only the newest snapshot is kept pending. A subscriber that was slow skips the
  states it missed, which is exactly right for "what is on screen right now".
- **Never blocking**: `Publish` runs on Winland's UI thread and only stores a line and pulses
  the writer threads. Each subscriber has its own thread, so a stalled reader can block nobody but
  itself, and is dropped silently.
- **Client side** (`EventClient`): a background thread that connects, reads lines, parses them into
  `device → workspace`, and hands each snapshot to the UI thread via `UiPoster`. If the pipe is
  unavailable or drops — Winland isn't running yet, or was restarted — it retries forever
  (~2 s apart) while the circles keep showing the last state they knew. Only successful subscriptions
  are logged, never the retries.

## 7. DPI and Display Changes

- The process is **per-monitor-v2 DPI aware** (`ApplicationHighDpiMode`), set before any window
  exists. Circle size, margin and dock threshold are all scaled by **each monitor's own** DPI
  (`GetDpiForMonitor`), so a 4K screen next to a 1080p one gets circles of the same physical size.
- Monitors are enumerated with `EnumDisplayMonitors` + `GetMonitorInfo` (MONITORINFOEX) rather than
  WinForms' `Screen`, so the device names are exactly the ones `winland` keys its state on, and
  the **work area** (`rcWork`) is used so a circle never lands under the taskbar. The full monitor
  rectangle (`rcMonitor`) decides which monitor a drag is over, and so which circle carries it.
- `SystemEvents.DisplaySettingsChanged` re-syncs the window set on the UI thread: windows for
  vanished monitors are disposed, surviving ones adopt the new work area/DPI (keeping their edge and
  offset), and new monitors get a window at their remembered dock position. A monitor coming or going
  mid-drag drops the drag's rules (the circles go back to plain snapshots).

## 8. Why It Is Invisible to Window Management

Each circle is a `Form` with an **empty title** and the `WS_EX_TOOLWINDOW` extended style — precisely
the thing `WorkspaceManager.IsManaged` rejects. So the circles are never
minimized by a workspace switch, never counted as workspace members, never listed in the
taskbar or Alt-Tab. `WS_EX_TOPMOST` keeps them above ordinary windows, and `WS_EX_LAYERED` +
`UpdateLayeredWindow` give them per-pixel alpha — which also means the mouse only hits the circle
itself, not the transparent square around it.

## 9. Staying On Top

`WS_EX_TOPMOST` is granted at creation but not guaranteed for life: a display reset, DWM restart, or
sleep/wake can silently demote an overlay window. The circle therefore re-asserts its place in the
topmost band (`SetWindowPos(HWND_TOPMOST, …, SWP_NOACTIVATE)`) on every presented frame, and a 5 s
UI-thread heartbeat covers the stretches where nothing is redrawn.

## 10. Known Constraints

- **Fullscreen exclusive apps**: the circles are plain topmost windows and are *not* hidden over
  fullscreen games or video. This is not implemented.
- **State follows Winland**: with `winland` stopped, the circles keep the last snapshot
  they received (they do not blank out and do not claim to be current).
- **A sleeping monitor is absent** from the snapshot; its circle shows `–` until the monitor comes
  back and a workspace change republishes.
- **The badge lists homed workspaces, not just occupied ones.** A workspace stays homed on a monitor from
  its first `Win+N` until it is released or moved, so it is listed even with no windows in it.
- **A drag carries the workspace on screen** (the underlined one) and starts only from the grip; the other
  numbers can't be dragged out individually.
- **No keyboard interaction**; the circle is drag-only by design (it must never take focus). There is
  no cancel: a move happens as the cursor enters a monitor, so carry the workspace back to undo it.
- **Every crossing is a full move.** Each one puts windows away and lays them out again; brushing
  across a monitor edge moves the workspace there and back. Waiting requests are coalesced (§4), the
  one already being carried out is not.
- **The drag relies on keeping the mouse while Winland switches focus.** Each move activates a window
  of the moved workspace mid-drag. A circle is never the foreground window, and with the button held its
  capture is expected to survive that — if Windows ever takes it, the drag simply ends where it is.
- **A carry acts on what the circle shows.** If Winland changes that monitor's workspace while the
  button is down (e.g. a shortcut pressed mid-drag), the workspace the circle showed at the press is the
  one moved.
- **The scratchpad can't be carried**; `Win+S` on the other monitor brings it there.

## 11. Extension Guidance

- A **right-click context menu** is the intended next addition and the class is shaped for it: every
  message `IndicatorWindow` cares about is handled in one `WndProc`, so a `WM_RBUTTONUP` case that
  shows a `ContextMenuStrip` is the whole change.
- Keep `IndicatorWindow` free of any knowledge of the pipes, and `EventClient` / `WinlandClient` free of
  any knowledge of windows — `IndicatorApp` is the only place they meet. A circle reports the gesture
  (`DragStarted` / `DragMoved` / `DragEnded`) and draws what it is told (`ShowBadge`); the rules of a
  drag live in `WorkspaceDrag`, free of windows, so they stay unit-testable.
- Any further request belongs on the control pipe as a `Dispatcher` verb, never on the event pipe.
- If the protocol grows a field, extend the line grammar in `Ipc`'s remarks and in
  `EventServer.FormatSnapshot`; `EventClient.Parse` already skips pairs it cannot read rather than
  failing a whole line.
- Anything that would need elevation does **not** belong in this process.
