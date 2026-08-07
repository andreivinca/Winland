# Feature Deep Dive: Window Focus

## 1. Objective

Provide directional, spatial window navigation on top-level app windows, focus-by-process for the
launch-or-focus binds, and quick close of the foreground window.

The navigator lives in **`winland-env`** (`WindowNavigator`) and is reached over the control pipe:
`winland-keys` runs `winlandctl focus <dir>` / `winlandctl close` / …, and `winland-env`'s
`Dispatcher` calls `WindowNavigator`.

## 2. Hotkeys

Defined as `config.conf` binds (not hardcoded), each running a `winlandctl` verb:
- `Win+Left` → `winlandctl focus left` → focus nearest window to the left
- `Win+Up` → `winlandctl focus up` → focus nearest window above
- `Win+Right` → `winlandctl focus right` → focus nearest window to the right
- `Win+Down` → `winlandctl focus down` → focus nearest window below
- `Win+W` → `winlandctl close` → close foreground window (`WM_CLOSE`)
- `winlandctl focus-app <process>` has no direct bind: `launch-or-focus.ps1` calls it (see
  `app-shortcuts.md`, which also covers the `show-desktop.ps1` script behind `Win+D`).

The directional/close combos ship as plain Win combos (no Shift/Alt/Ctrl).

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
- Fully occluded by windows above it in Z-order.
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
  - Call `SetForegroundWindow(bestHandle)`.

## 4. Focus by Process (`focus-app <process>`)

`WindowNavigator.FocusApp(processName)`:
- Collects the pids of processes with that name (a trailing `.exe` is ignored).
- Returns false ("`ERR` nothing to focus") when the foreground window already belongs to one of
  them — the caller (launch-or-focus) then starts a new instance.
- Otherwise picks the app's topmost non-minimized window (EnumWindows yields top-to-bottom), or the
  topmost minimized one as a fallback, restores it if needed, and forces it to the foreground with
  the same foreground-lock-timeout suppression the workspace engine uses.
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
