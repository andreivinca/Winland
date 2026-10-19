# Feature Deep Dive: On-Screen Workspace Indicator

## 1. Objective

Tell the user, at a glance and on **every** monitor, which workspace that monitor is currently
showing — something the tray icon cannot do, because Windows 11 only gives tray apps the primary
monitor's taskbar.

The indicator is its own process, `winland-indicator`. It is mostly a **subscriber**: it reads
workspace state off `winland`'s broadcast event pipe and draws. Its one command back is a request
to **move a workspace to another monitor** when the user drops a circle there (§4). It holds no
window handles and needs no elevation. Winland works exactly as before without it.

## 2. What the User Sees

One small translucent white circle per monitor, floating above ordinary windows, carrying that
monitor's workspace glyph:

| Glyph | Meaning |
| --- | --- |
| `1`, `2`, `17`, … | the workspace shown on that monitor |
| `S` | the roaming scratchpad is up there |
| `–` (en dash) | the monitor shows no workspace — or no state has arrived yet |

Visually it is the tray icon's language — white disc, bold black Segoe UI glyph that shrinks for
multi-digit numbers — only faint (~25% opaque) with an antialiased rim, so it sits on top of a
window without hiding it.

## 3. The Docking Interaction (the core rule)

The circle is **always docked to one margin** of its monitor's work area: it hugs that edge and
tucks slightly past it (~6 px at 96 DPI, so it sits a little hidden by the margin) — at the ends of
the edge too.

Dragging it with the left mouse button obeys one rule:

> **A drag slides the circle along its docked edge. Perpendicular motion does nothing.**
> If it is docked to the top margin and you drag *down*, it does not move down. Only when the mouse
> comes near the bottom margin does it dock to the bottom margin — and then it slides along that one.
> The same for the left and right margins.

Concretely, on each mouse-move during a drag:

1. Find the edge of the work area **nearest the cursor** and how far the cursor is from it.
2. If that distance is within the **dock threshold** (~80 px at 96 DPI), that edge becomes the docked
   edge. Otherwise the docked edge stays exactly what it was.
3. Place the circle on the docked edge, centred on the cursor's coordinate **parallel** to that edge
   (the cursor's X for a top/bottom dock, its Y for a left/right dock), clamped so the circle stays
   fully inside the work area.

Because the docked position is always derived from the monitor's own work area and clamped to it,
**docking never puts a circle on another monitor** — and because the window carries
`WS_EX_NOACTIVATE`, dragging it never takes focus from the app the user is working in.

The one way off the monitor is carrying a workspace (§4): while the cursor is **outside the circle's
own monitor** (its full bounds, not just the work area — the taskbar still counts as home), a circle
that shows a numbered workspace leaves its edge and follows the cursor, drawn more opaque (~63%) so it
reads as picked up. Back over its own monitor it docks again by the rule above.

## 4. Moving a Workspace to Another Monitor

Dropping a circle on another monitor moves that **whole workspace** there.

| Drag ends… | Result |
| --- | --- |
| on the circle's own monitor | an ordinary re-dock; the new position is persisted (§5) |
| on another monitor, circle shows a number | `move-workspace <n> <device>` is sent; the circle returns to where it was picked up |
| because capture was lost (not a button release) | nothing is sent; the circle returns to where it was picked up |

The `–` circle has nothing to carry and the `S` circle's scratchpad already roams with `Win+S`, so
neither ever leaves its monitor.

What `winland` does (`WorkspaceManager.MoveWorkspaceToMonitor`; details in `workspaces.md` §7a):

1. **Re-home** the workspace to the target monitor — `Win+N` shows it there from now on.
2. Its floating windows on the old monitor are **carried** to the target, at the same place relative
   to the monitor. Floating windows the user put on any other monitor stay put.
3. The target **puts away** what it showed (that workspace stays homed there) and **lays out** the
   moved workspace's tiles.
4. The old monitor **goes back to the workspace it showed before**: of the workspaces homed on it that
   no monitor is showing, the most recently shown (`PreviousWorkspace`) — or nothing (`–`) when there
   is none.
5. Focus, and the mouse, land on the moved workspace.

Example: monitor 1 shows `3`, monitor 2 shows `5`. Drop monitor 1's circle on monitor 2 → monitor 2
shows `3` with all its windows (5 is put away, still homed on monitor 2), and monitor 1 shows the
workspace it showed before 3.

The circles never update themselves optimistically. Both relabel when the snapshot that follows the
move arrives (§6), so they can never disagree with what Winland did. The request runs on a
worker thread (`WinlandClient`, 2 s to connect, 10 s for the reply): the move minimizes and lays out
windows, and the circles must stay responsive meanwhile. A request that fails — Winland is
not running, or the monitor vanished — is logged and changes nothing.

**DPI:** a carried circle keeps its own monitor's size. Moving it onto a monitor with a different DPI
makes Windows send `WM_DPICHANGED` with a suggested new window rectangle; `IndicatorWindow` declines it
(all of its geometry comes from `Redraw`), so the rescale can't fight the layered-window surface.

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
- Written when a drag ends **on the circle's own monitor**; a carry (§4) leaves it untouched. Read at
  startup. The default is `Top 0.8` (top edge, toward the
  right).
- Monitors that are not attached right now keep their remembered entry, so unplugging a screen never
  forgets where its circle lived. A missing or unreadable file is not an error — everything falls back
  to the default.

## 6. The Event Protocol

`winland` publishes on a second named pipe, `\\.\pipe\winland-events`
(`Ipc.EventsPipeName`), which is **outbound only** — nothing a subscriber could write is ever read.

- **Message**: one line per snapshot, space-separated `<device>=<workspace>` pairs ordered
  left-to-right, e.g. `\\.\DISPLAY1=3 \\.\DISPLAY2=5`. Device names contain no spaces, so the format
  needs no escaping. The workspace is the **raw** number: `0` = nothing shown, `int.MaxValue` = the
  scratchpad. Mapping those to `–` and `S` is the client's business.
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
  rectangle (`rcMonitor`) decides whether a drag is still over the circle's own monitor, and which
  monitor a carried circle was dropped on.
- `SystemEvents.DisplaySettingsChanged` re-syncs the window set on the UI thread: windows for
  vanished monitors are disposed, surviving ones adopt the new work area/DPI (keeping their edge and
  offset), and new monitors get a window at their remembered dock position.

## 8. Why It Is Invisible to Window Management

Each circle is a `Form` with an **empty title** and the `WS_EX_TOOLWINDOW` extended style — precisely
the two things `WorkspaceManager.IsManaged` and `show-desktop.ps1` reject. So the circles are never
minimized by a workspace switch or by `Win+D`, never counted as workspace members, never listed in the
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
- **The workspace count is not tracked** — the indicator shows what each monitor displays, nothing
  about workspaces on other monitors or how many exist.
- **No keyboard interaction**; the circle is drag-only by design (it must never take focus) — so a
  carry cannot be cancelled with `Esc`; drop it back on its own monitor instead.
- **A carry acts on what the circle shows.** If Winland changes that monitor's workspace in the
  instant between the drop and the request (e.g. a shortcut pressed mid-drag), the workspace the circle
  showed is the one moved.
- **The scratchpad can't be carried**; `Win+S` on the other monitor brings it there.

## 11. Extension Guidance

- A **right-click context menu** is the intended next addition and the class is shaped for it: every
  message `IndicatorWindow` cares about is handled in one `WndProc`, so a `WM_RBUTTONUP` case that
  shows a `ContextMenuStrip` is the whole change.
- Keep `IndicatorWindow` free of any knowledge of the pipes, and `EventClient` / `WinlandClient` free of
  any knowledge of windows — `IndicatorApp` is the only place they meet. A circle reports a drop
  (`WorkspaceDropped`); the app decides what to ask Winland.
- Any further request belongs on the control pipe as a `Dispatcher` verb, never on the event pipe.
- If the protocol grows a field, extend the line grammar in `Ipc`'s remarks and in
  `EventServer.FormatSnapshot`; `EventClient.Parse` already skips pairs it cannot read rather than
  failing a whole line.
- Anything that would need elevation does **not** belong in this process.
