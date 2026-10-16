# Feature Deep Dive: Workspaces

## 1. Objective

Provide deterministic, per-monitor workspace management where each monitor displays one workspace at
a time: switching puts away everything on the monitor and lays out the incoming workspace's tiles.
(How tiles are arranged: `tiling.md`.)

Workspaces live in the **`winland-env`** service. They are driven over the control pipe: the
`winland-keys` daemon runs the bind `winlandctl workspace N`, `winlandctl` forwards `workspace N` to
`winland-env`, and the `Dispatcher` calls into `WorkspaceManager`. The keys daemon itself has no
workspace logic. (See `architecture-overview.md`.)

## 2. Membership Model (the core rule)

`_windowWorkspace` (window handle → workspace number) is the single source of truth, and it changes
**only on explicit user action**. A member is a tile of its workspace, or floating (dragged out of the
strip by the user but still in the workspace; see `tiling.md`).

- **Move** (`Win+Shift+N` → `movetoworkspace N`) — unlink from the old workspace, link to N as a tile.
- **Tile** (`Win+Space` → `link-here`) — link to the workspace currently shown on the window's
  monitor, as a tile (a no-op when that monitor shows no workspace).
- **Open from a Winland shortcut** (`place-next-window`) — link to the workspace shown on the monitor
  under the mouse, as a tile (resizable windows only).
- **Unlink** (`Win+Shift+Space` → `unlink`) — remove from its workspace; a normal window again.
- **A tile moved past the end of its strip** (`move-window`) — relink to the workspace shown on the
  monitor it crosses to.
- **Close** — closed windows are pruned *lazily* in `WindowsOf` (when membership is next read).

Nothing else mutates membership. In particular:
- **No windows are assigned at startup** — apps that are open when Winland starts stay unlinked until
  claimed.
- **Interaction never re-links** — activating, restoring, maximizing, or dragging a window never pulls it
  into a workspace. Dragging a tile, even onto another monitor, only makes it float in its own
  workspace.
- **Window events never unlink** — `EVENT_OBJECT_DESTROY` is delivered asynchronously and can arrive
  with a recycled HWND that now belongs to a different, still-open window; acting on that handle would
  silently unlink it. Tiling hooks destroy/show/hide only to re-lay out a strip, which re-checks every
  member; membership itself is still pruned lazily.

The foreground hook remembers each workspace's last-focused tile (for focus restore on switch, and to
keep it in view); the tiling hooks are described in `tiling.md` §4.

## 3. State Model

### 3.1 Core State Containers
- `_windowWorkspace`: window handle → workspace number (see §2).
- `_lastActive`: workspace → last active window (focus restore, and the tile kept in view).
- `_tileOrder`, `_floating`, `_scrollFirst`, `_arranged`: each workspace's strip order, floating
  members, scroll position, and last
  layout (see `tiling.md`).
- `_workspaceHome`: workspace → home monitor, stored as the stable GDI **device name**
  (`\\.\DISPLAYn`), *not* an HMONITOR — handles are reissued on sleep/wake/display changes.
- `_shownByDevice`: device name → the workspace shown on that monitor; survives a monitor dropping
  out of enumeration (sleep), so a wake restores what it was showing.
- `_monitors`: HMONITOR → live monitor state; rebuilt from enumeration before every operation.

### 3.2 Invariants
1. Workspace ids are any whole number `>= 1` — **not capped**. The shipped config binds 1..9 and
   11..19; any other number is reachable via `winlandctl workspace <n>`.
2. `int.MaxValue` is reserved for the scratchpad and is rejected by the `workspace` /
   `movetoworkspace` verbs.
3. A monitor's `Current` is either `0` (released/none) or a workspace id `>= 1`.
4. `_windowWorkspace` is authoritative for membership.
5. Switching a workspace on one monitor does not mutate `Current` of other monitors.
6. A workspace is shown on at most one monitor at a time (`SetShown` clears any other claim).

## 4. Startup Behavior

`Rebuild()` initializes the runtime model:
- Enumerates monitors and sorts left-to-right.
- Seeds each monitor with a workspace (leftmost = 1, next = 2, …) and homes those workspaces to
  their monitors.
- **Assigns no windows** (see §2).

## 5. Managed Window Eligibility

A window is considered managed if it exists and is visible or minimized, is top-level (no owner),
has non-empty title text, is not a tool window (`WS_EX_TOOLWINDOW`), is not DWM-cloaked, and is not a
shell window: the desktop (`Progman`/`WorkerW`) or a shell panel (`Windows.UI.Core.CoreWindow` — Start,
Search, notifications, the touch keyboard). Shell panels look like app windows while open, but the
shell cloaks rather than hides them when they close, so a linked one would keep an invisible tile.

## 6. Switch Mechanics (`SwitchFocusedMonitorTo(k)`)

1. `RefreshMonitors()` reconciles cached monitor state with the live display configuration (device
   names keep identity across HMONITOR reissue; a sleeping monitor keeps its shown workspace).
2. `ResolveHome(k)`: use k's home monitor if present. If the home monitor is asleep/disconnected,
   show k on the mouse monitor *without* re-homing it. First-ever use pins k's home to the mouse
   monitor.
3. On a **real switch**, remember the outgoing workspace's frontmost tile as its focus target
   (`RememberFrontmost`).
4. Minimize everything on the monitor that isn't k's — other workspaces' windows and unlinked windows
   alike (`MinimizeMonitorWindows`; membership untouched), so an empty workspace means an empty
   monitor. On a real switch, also put away the outgoing workspace's windows floating on other
   monitors (`PutAwayWorkspace`).
5. Record k as shown and bring k's windows back (`ShowWorkspace`): its strip laid out on the home
   monitor scrolled to its focused tile, its floating windows restored where the user left them — on
   any monitor. Then focus k's last-active window. On a re-press of an already-shown workspace this
   *re-asserts* the workspace: minimized or maximized tiles snap back into their columns, and minimized
   floating windows come back in place.
6. A ~1.5 s event guard brackets the operation so our own minimize/restore events don't feed back
   into `_lastActive` (see §9).

## 7. Release Semantics (`Win+Shift+W` → `workspace-release`)

Acts on the monitor under the cursor: remember the workspace's frontmost tile, minimize every window
there and the workspace's windows floating elsewhere, remove the workspace's home pinning, set the
monitor's `Current` to 0 (the indicator shows a dash). Members stay linked, so `Win+N` on any
monitor later re-homes the workspace there and brings them back. Releasing while the **scratchpad**
is shown puts the scratchpad's windows away and clears its return workspace instead (the scratchpad
never has a home to unpin).

## 8. The Scratchpad (`Win+S` → `scratchpad`)

A roaming workspace reserved at `int.MaxValue` with **no home monitor**:
- Toggle on: appears on the monitor under the mouse, remembering what that monitor showed
  (`_scratchpadReturn`); toggle off restores it.
- Toggling while it is up on *another* monitor first sends that monitor back to its previous
  workspace, then brings the scratchpad (and its windows) to the mouse monitor.
- Windows attach to it like any workspace: focus one while the scratchpad is shown and press
  `Win+Space`. Its tiles are laid out on whichever monitor it appears on.

## 9. Focus Restoration and Event Guarding

`FocusWorkspace` prefers `_lastActive[workspace]` (if still valid, non-minimized, and still a
member), else the first non-minimized member. Focus uses temporary foreground-lock-timeout
suppression (never `AttachThreadInput`).

Workspace operations generate window events asynchronously; a guard period (~1500 ms) around each
operation makes the foreground hook ignore them. Because a click made *during* the guard can be
missed, `RememberFrontmost` trusts the live frontmost tile over `_lastActive` when a workspace is put
away.

## 10. Change Notification

`NotifyWorkspaceChanges` runs after every operation that can change what a monitor shows, and raises
`WorkspacesChanged` — `WorkspaceSnapshot`: every live monitor's device name and shown workspace,
ordered left-to-right. `EventServer` subscribes and broadcasts it to the on-screen indicator (see
`workspace-indicator.md`), which renders the scratchpad as "S" and a released (empty) monitor as a
dash.

## 11. Known Constraints

- Workspace state is in-memory only; no persistence across restarts.
- Membership is keyed by HWND; Windows recycles handles, so a stale entry could in principle be
  inherited by an unrelated new window until pruning runs. Accepted trade for the simplicity of the
  lazy-prune model.
- Behavior depends on Win32 window semantics; some app window types can behave unusually.

## 12. Extension Guidance

When changing workspace logic:
1. Preserve monitor isolation semantics.
2. Preserve `_windowWorkspace` as the single membership authority — and keep membership mutations
   explicit-only (§2).
3. Never tile, move or resize a window that isn't a workspace member; only put it away with its
   monitor's workspace.
4. Key any new per-monitor state on device names, not HMONITORs.
5. Reassess guard timing if introducing async/longer operations.
6. Keep every change on the one `NotifyWorkspaceChanges` path, so the on-screen indicator can never
   disagree about what a monitor is showing.
