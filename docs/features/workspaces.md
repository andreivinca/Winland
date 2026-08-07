# Feature Deep Dive: Workspaces

## 1. Objective

Provide deterministic, per-monitor workspace management where each monitor displays one workspace at
a time and workspace windows are restored/minimized as users switch contexts.

Workspaces live in the **`winland-env`** service. They are driven over the control pipe: the
`winland-keys` daemon runs the bind `winlandctl workspace N`, `winlandctl` forwards `workspace N` to
`winland-env`, and the `Dispatcher` calls into `WorkspaceManager`. The keys daemon itself has no
workspace logic. (See `architecture-overview.md`.)

## 2. Membership Model (the core rule)

`_windowWorkspace` (window handle → workspace number) is the single source of truth, and it changes
**only on explicit user action**:

- **Move** (`Win+Shift+N` → `movetoworkspace N`) — unlink from the old workspace, link to N.
- **Link** (`Win+Space` → `link-here`) — link to the workspace currently shown on the window's
  monitor (a no-op when that monitor shows no workspace).
- **Close** — closed windows are pruned *lazily* in `WindowsOf` (when membership is next read).

Nothing else mutates membership. In particular:
- **No windows are assigned at startup** — apps that are open when Winland starts stay unlinked until
  claimed.
- **Interaction never re-links** — activating, restoring, maximizing, or dragging a window does not
  pull it into a workspace.
- **No destroy-event hook** — `EVENT_OBJECT_DESTROY` is delivered asynchronously and can arrive with
  a recycled HWND that now belongs to a different, still-open window; acting on it would silently
  unlink that window. Hence the lazy pruning above.

The only WinEvent hook is `EVENT_SYSTEM_FOREGROUND`, used solely to remember each workspace's
last-focused window (for focus restore on switch).

## 3. State Model

### 3.1 Core State Containers
- `_windowWorkspace`: window handle → workspace number (see §2).
- `_lastActive`: workspace → last active window (focus restore).
- `_zorder`: workspace → its windows top-to-bottom, captured when the workspace is put away, so the
  same stacking order (and frontmost window) comes back on return.
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
shell desktop window (`Progman`/`WorkerW`).

## 6. Switch Mechanics (`SwitchFocusedMonitorTo(k)`)

1. `RefreshMonitors()` reconciles cached monitor state with the live display configuration (device
   names keep identity across HMONITOR reissue; a sleeping monitor keeps its shown workspace).
2. `ResolveHome(k)`: use k's home monitor if present. If the home monitor is asleep/disconnected,
   show k on the mouse monitor *without* re-homing it. First-ever use pins k's home to the mouse
   monitor.
3. If k is **already shown** on its home monitor (a re-press), *re-assert*: restore minimized
   members and pull back windows dragged onto other monitors; windows already visible on the monitor
   keep their current place and stacking.
4. On a **real switch**: capture the outgoing workspace's z-order, minimize everything on the monitor
   that isn't k's (membership untouched), restore k's windows onto the monitor, re-apply k's captured
   z-order, record k as shown, and focus k's last-active window.
5. A ~1.5 s event guard brackets the operation so our own minimize/restore events don't feed back
   into `_lastActive` (see §9).

## 7. Release Semantics (`Win+Shift+W` → `workspace-release`)

Acts on the monitor under the cursor: minimize every window there, remove the workspace's home
pinning, set the monitor's `Current` to 0 (tray shows a dash). Members stay linked, so `Win+N` on any
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
  `Win+Space`.

## 9. Focus Restoration and Event Guarding

`FocusWorkspace` prefers `_lastActive[workspace]` (if still valid, non-minimized, and still a
member), else the first non-minimized member. Focus uses temporary foreground-lock-timeout
suppression (never `AttachThreadInput`).

Workspace operations generate window events asynchronously; a guard period (~1500 ms) around each
operation makes the foreground hook ignore them. Because a click made *during* the guard can be
missed, `CaptureZOrder` trusts the live frontmost window over `_lastActive` when a workspace is put
away.

## 10. Primary Monitor Integration

`PrimaryWorkspaceChanged` emits the workspace shown on the **primary** monitor; the tray icon
subscribes. The scratchpad shows as "S", a released (empty) monitor as a dash.

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
3. Key any new per-monitor state on device names, not HMONITORs.
4. Reassess guard timing if introducing async/longer operations.
5. Update tray integration if primary workspace signal semantics change.
