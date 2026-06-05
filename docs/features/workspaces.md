# Feature Deep Dive: Workspaces

## 1. Objective

Provide deterministic, per-monitor workspace management where each monitor displays one workspace at a time and workspace windows are restored/minimized as users switch contexts.

Workspaces live in the **`winland-env`** service. They are driven over the control pipe: the
`winland-keys` daemon runs the bind `winlandctl workspace N`, `winlandctl` forwards `workspace N` to
`winland-env`, and the `Dispatcher` calls into `WorkspaceManager`. The keys daemon itself has no
workspace logic. (See `architecture-overview.md`.)

## 2. Primary User Flows

### 2.1 Switch to Workspace N (`Win+1..9`)
1. User presses Win+N.
2. `winland-keys` runs the matching bind's command, `winlandctl workspace N`.
3. `winlandctl` sends `workspace N` over the pipe; `winland-env`'s `Dispatcher` calls
   `WorkspaceManager.SwitchFocusedMonitorTo(N)`.
4. Workspace home monitor is resolved:
   - Existing home: use it.
   - No home: pin to monitor under cursor.
5. If workspace already active on home monitor:
   - Focus workspace window.
6. Else:
   - Minimize outgoing windows on that monitor.
   - Move/restore workspace N windows onto that monitor.
   - Set monitor current workspace to N.
   - Focus workspace N target window.

### 2.2 Release Current Workspace (`Win+Shift+W`)
0. `winland-keys` runs `winlandctl workspace-release`; `winland-env` calls `ReleaseCurrentWorkspace()`.
1. Active monitor determined from cursor position.
2. Current workspace on that monitor is minimized away.
3. Workspace home binding removed.
4. Monitor current set to `0` (no workspace displayed).
5. Next Win+N from any monitor can re-home that workspace.

## 3. State Model

### 3.1 Core State Containers
- `_windowWorkspace`: window handle -> workspace number.
- `_lastActive`: workspace -> last active window.
- `_workspaceHome`: workspace -> home monitor handle.
- `_monitors`: monitor handle -> monitor state (`Current`, work area, primary flag).

### 3.2 Invariants
1. Workspace ids are in `[1..9]`.
2. A monitor’s `Current` is either `0` (released/none) or `[1..9]`.
3. `_windowWorkspace` is authoritative for membership.
4. Switching workspace on one monitor does not mutate `Current` of other monitors.

## 4. Startup Behavior

`Rebuild()` initializes runtime model:
- Enumerates monitors and sorts left-to-right.
- Seeds each monitor with initial workspace number (`1..N`, max 9).
- Seeds `_workspaceHome` for those initial assignments.
- Enumerates non-minimized managed windows and assigns each to its monitor’s current workspace.

## 5. Window Assignment Rules

### 5.1 Managed Window Eligibility
A window is considered managed if:
- It exists and is visible or minimized.
- It is top-level (no owner window).
- It has non-empty title text.
- It is not a tool window (`WS_EX_TOOLWINDOW`).
- It is not cloaked.
- It is not shell desktop window class (`Progman`/`WorkerW`).

### 5.2 Runtime Re-assignment by User Activity
Via WinEvent hooks:
- Foreground, restore (minimize-end), or move-size-end causes window to join the currently shown workspace on its monitor (if current workspace >= 1).
- Destroy event removes window from membership map.

## 6. Switch Mechanics Details

### 6.1 Minimize Outgoing Monitor Windows
`MinimizeMonitorWindows(monitor, assignTo, keep)`:
- Iterates visible managed windows physically on target monitor.
- Skips windows belonging to `keep` workspace.
- Assigns others to `assignTo` workspace and minimizes them.

### 6.2 Forget User-Minimized Stale Members
`ForgetMinimizedMembers(assignTo)` removes minimized/non-existent windows from outgoing workspace before minimizing visible members.

Intent: if user manually minimized a window earlier, it should not reappear automatically when returning to workspace.

### 6.3 Restore Workspace Windows
`ShowWorkspaceOnMonitor(workspace, monitor)`:
- Enumerates windows assigned to workspace.
- Moves each to monitor while preserving relative placement/maximized state.
- Restores visibility through placement/show operations.

## 7. Focus Restoration Rules

`FocusWorkspace(workspace)` target priority:
1. `_lastActive[workspace]` if still valid, non-minimized, and still assigned.
2. First non-minimized window in workspace.

Focus uses temporary foreground-lock timeout suppression to improve reliability.

## 8. Event Guarding and Race Prevention

Workspace operations generate window events asynchronously. To avoid self-induced reassignment corruption:
- Manager sets guard period (`~1500ms`) around switch/release operations.
- During guard, event callback ignores assignment events.

## 9. Primary Monitor Integration

- `PrimaryWorkspaceChanged` event emits current workspace displayed on primary monitor.
- Tray icon subscribes and updates indicator accordingly.
- This avoids displaying workspace from non-primary monitor when user triggers actions elsewhere.

## 10. Known Constraints

- Fixed workspace count (9).
- Workspace state is in-memory only; no persistence across app restarts.
- Behavior depends on Win32 window semantics; some app window types can behave unusually.

## 11. Extension Guidance

When changing workspace logic:
1. Preserve monitor isolation semantics.
2. Preserve `_windowWorkspace` as single membership authority.
3. Reassess guard timing if introducing async/longer operations.
4. Update tray integration if primary workspace signal semantics change.
