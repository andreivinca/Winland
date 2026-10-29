# Feature Deep Dive: Window Focus

## 1. Objective

Provide directional, spatial window navigation on top-level app windows, focus-by-process for the
launch-or-focus binds, and quick close of the foreground window.

The navigator lives in **`winland`** (`WindowNavigator`) and is reached over the control pipe:
`winland-keys` runs `winlandctl focus <dir>` / `winlandctl close` / …, and `winland`'s
`Dispatcher` calls `WindowNavigator`.

## 2. Hotkeys

Defined as `config.conf` binds (not hardcoded), each running a `winlandctl` verb:
- `Win+Left` → `winlandctl focus left` → focus nearest window to the left
- `Win+Up` → `winlandctl focus up` → focus nearest window above
- `Win+Right` → `winlandctl focus right` → focus nearest window to the right
- `Win+Down` → `winlandctl focus down` → focus nearest window below
- `Win+W` → `winlandctl close` → close foreground window (`WM_CLOSE`)
- `winlandctl focus-app <process>` has no direct bind: `launch-or-focus.ps1` calls it (see
  `app-shortcuts.md`).

The directional/close combos ship as plain Win combos (no Shift/Alt/Ctrl).

When the focused window is a **tile**, `focus left`/`right` first step to the neighbouring tile in its
strip, scrolling the strip if needed (`WorkspaceManager.FocusAdjacentTile`, see `tiling.md`). The
geometric search below is used for `up`/`down`, for windows that aren't tiles, and past either end of
a strip.

## 3. Directional Focus Algorithm

Entry point: `WindowNavigator.FocusNearest(Direction)`.

### 3.1 Preconditions
- Foreground window must exist.
- Foreground window rectangle must be retrievable.

### 3.2 Candidate Enumeration
- Enumerate all top-level windows using `EnumWindows`.
- Filter out non-candidates with `IsCandidateWindow`.

### 3.3 Candidate Filters
A candidate is rejected if any are true:
- Same window as current foreground.
- Not visible.
- Minimized/iconic.
- Desktop shell window class (`Progman`, `WorkerW`).
- Empty title text.
- DWM cloaked.
- Fully occluded by (non-cloaked) windows above it in Z-order.
- Show command indicates minimized state in placement data.

### 3.4 Distance Scoring
`TryGetDirectionalDistance(direction, currentRect, rect, out primary, out secondary)`:
- Candidate must lie in requested direction.
- `primary`: directional delta (how far forward in requested direction).
- `secondary`: orthogonal range distance (overlap gap).

Selection rule:
- Choose smallest `primary`.
- Tie-break with smallest `secondary`.

### 3.5 Focus Action
- On best candidate found:
  - Log process name best-effort.
  - Bring it to the foreground with `Foreground.Activate`, which `focus-app` and the workspace engine
    use too. `winland` never receives the hotkey's input (the keystroke goes to `winland-keys`, and the
    command arrives over the pipe via `winlandctl`), so Windows refuses a plain `SetForegroundWindow`
    from it whenever something else is in the foreground — the target just flashes on the taskbar.
    `Activate` first injects a no-op mouse input (`SendInput`, no motion, no buttons), which makes
    `winland` the process that received the last input and so grants it the foreground right. A
    refusal is still logged.
- The `Dispatcher` then moves the mouse to the newly focused window (§3.6).

### 3.6 The Mouse Follows Focus (`CursorWarp`)
`focus`, `focus-app`, `focus-web`, `move-window`, `link-here`, `toggle-tile`, `toggle-fullscreen` and `movetoworkspace` run through
`Dispatcher.FollowedByCursor`: it records the foreground window and its visible frame (DWM extended
frame bounds) before the command, and afterwards — if the foreground window or its frame changed and
the cursor isn't already over it — calls `SetCursorPos` on the centre of its frame. A command that
changes nothing leaves the mouse alone.

"Over it" means `WindowFromPoint` → root is the window **and** the cursor is inside its visible frame
(`CursorWarp.IsOverFrame`). The hit test alone also counts the invisible resize border around a window,
which spans the gap between two tiles: a cursor resting in that gap (e.g. the middle of the screen with
two tiles) used to count as over whichever tile was focused, so it never followed focus again.

A window that just became a tile — tiled with `link-here`/`toggle-tile` (Win+T), or opened from a
Winland shortcut and tiled by launch placement — gets `CursorWarp.CentreOn`: the mouse goes to the
middle of its new column even if it was already over it.

Showing a workspace — `workspace`, and `scratchpad` on or off — can't use that diff: while the outgoing
windows are put away Windows may activate any window. So `WorkspaceManager.BringCursorTo` takes the
window `FocusWorkspace` focused and calls `CursorWarp.ToWindow` (its frame's centre, unless the cursor is
over it); a workspace with nothing to focus calls `CursorWarp.ToArea` with its monitor's work area (the
centre, unless the cursor is on that monitor already), so a shortcut launched next opens there.
`workspace-release` doesn't move the mouse.

## 4. Focus by Process (`focus-app <process>`)

`WindowNavigator.FocusApp(processName)`:
- Collects the pids of processes with that name (a trailing `.exe` is ignored).
- Returns false ("`ERR` nothing to focus") when the foreground window already belongs to one of
  them — the caller (launch-or-focus) then starts a new instance.
- Otherwise picks the app's topmost non-minimized window (EnumWindows yields top-to-bottom), or the
  topmost minimized one as a fallback, and activates it with `Foreground.Activate` (§3.5), which
  restores it first if needed.
- Candidate windows must be visible, unowned, titled, non-cloaked, and not the shell.

## 5. Close Foreground Behavior

`WindowNavigator.CloseForeground()`:
- Gets foreground window.
- Posts `WM_CLOSE` with `PostMessage`.

Rationale: asks app window to close gracefully according to normal window contract.

## 6. Limitations and Edge Cases

- Occlusion detection is conservative (full-rectangle containment in higher z-order windows).
- Apps with unusual top-level windows, hidden titles, or cloaked states may be excluded.
- Focus-stealing restrictions by OS can occasionally still interfere.

## 7. Dependencies

- Heavy use of `user32.dll` and `dwmapi.dll` interop.
- Relies on desktop windowing model semantics (top-level visible windows).

## 8. Extension Guidance

If adding new focus strategies:
- Keep filter logic explicit and testable.
- Maintain deterministic tie-break behavior.
- Avoid expensive operations in per-window loop to preserve responsiveness.
