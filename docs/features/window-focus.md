# Feature Deep Dive: Window Focus

## 1. Objective

Provide directional, spatial window navigation on top-level app windows and allow quick close of foreground window using Win-key combos.

## 2. Hotkeys

Built-in mappings in `WinlandApp`:
- `Win+Left` -> focus nearest window to the left
- `Win+Up` -> focus nearest window above
- `Win+Right` -> focus nearest window to the right
- `Win+Down` -> focus nearest window below
- `Win+W` -> close foreground window (`WM_CLOSE`)

These are plain Win combos (no Shift/Alt/Ctrl).

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

## 4. Close Foreground Behavior

`WindowNavigator.CloseForeground()`:
- Gets foreground window.
- Posts `WM_CLOSE` with `PostMessage`.

Rationale: asks app window to close gracefully according to normal window contract.

## 5. Limitations and Edge Cases

- Occlusion detection is conservative (full-rectangle containment in higher z-order windows).
- Apps with unusual top-level windows, hidden titles, or cloaked states may be excluded.
- Focus-stealing restrictions by OS can occasionally still interfere.

## 6. Dependencies

- Heavy use of `user32.dll` and `dwmapi.dll` interop.
- Relies on desktop windowing model semantics (top-level visible windows).

## 7. Extension Guidance

If adding new focus strategies:
- Keep filter logic explicit and testable.
- Maintain deterministic tie-break behavior.
- Avoid expensive operations in per-window loop to preserve responsiveness.
