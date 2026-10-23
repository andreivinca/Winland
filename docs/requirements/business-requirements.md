# Winland Business Requirements

## 1. Purpose

Winland provides keyboard-first desktop productivity on Windows through three core capabilities:
1. **Per-monitor workspaces**
2. **Directional window focus/navigation**
3. **Configurable app shortcuts**

The product goal is to deliver Hyprland/Omarchy-style workflow ergonomics on Windows while remaining lightweight, tray-resident, and fast to use.

Implementation note: Winland is one app, `winland` (the window manager), which runs two companion
processes — `winland-keys` (the hotkey daemon) and `winland-indicator` (the on-screen workspace
circles) — and ends them when it exits; `winlandctl` is a control CLI. This is an architectural detail,
not a product feature; the requirements below describe user-facing behavior regardless of process
boundaries. See `architecture-overview.md`.

## 2. Product Vision

A user should be able to:
- Keep hands on keyboard.
- Manage window/task context by workspace number.
- Move focus across windows spatially without Alt+Tab cycling.
- Launch/focus common apps with mnemonic Win-key shortcuts.
- Operate across multi-monitor setups without cross-monitor side effects.

## 3. Target User Persona

Primary users:
- Power users and developers on Windows.
- Users with multi-monitor setups.
- Users who prefer tiling-WM-like key workflows over mouse-centric interaction.

## 4. Core Value Propositions

1. **Speed**: Global Win-key combos trigger immediately.
2. **Predictability**: Workspace actions are monitor-scoped and deterministic.
3. **Low friction**: Configuration is plain text; a daemon restart applies it.
4. **Continuity with Windows**: Standard Win combos can be reintroduced via config after disabling
   shell Win hotkeys, and launched apps run with the user's normal (unelevated) rights.

## 5. In-Scope Features (Current)

### 5.1 Workspaces
- Numbered workspaces, unbounded (`>= 1`); the shipped config binds `1..9` and `11..19`.
- Per-monitor current workspace state.
- Workspace “home monitor” concept:
  - First entry pins workspace to monitor under cursor.
  - Later switches target that monitor.
  - Dragging the workspace's indicator circle onto another monitor moves the whole workspace there
    (re-homing it); the monitor it left goes back to the workspace it showed before.
- A window joins a workspace only on explicit move (`Win+Shift+N`) or tile (`Win+T`) — never as a
  side effect of interacting with it — and leaves it only on unlink (`Win+Shift+Space`, or `Win+T` on a
  tile), a move, or
  closing.
- A workspace's windows are tiled in a row that shares the monitor and scrolls once they no longer
  fit; tiles never appear on another monitor. Windows that belong to no workspace keep normal
  Windows behavior and are never tiled or resized by Winland; a workspace switch still puts them away
  with everything else on that monitor, so an empty workspace shows an empty monitor.
- A roaming **scratchpad** (`Win+S`) that appears on the monitor under the mouse and hides again on
  the next press.
- Ability to release current workspace from monitor (`Win+Shift+W`).

### 5.2 Window Focus
- `Win+Left/Up/Right/Down`: focus nearest window by direction.
- `Win+W`: close foreground window (via `WM_CLOSE`).

### 5.3 App Shortcuts
- Config-driven binds in `config.conf`.
- Supports modifiers (`SHIFT`, `ALT`, `CTRL`) plus required `SUPER`.
- Supports script verbs (`<verb>.ps1`) and normal command execution.
- `launch-or-focus` behavior requirement: focus an existing instance when it is not currently focused; if already focused, open a new instance.
- Apps launched from binds run with the user's normal (unelevated) rights.
- Includes bundled scripts:
  - `launch-or-focus.ps1`
  - `launch-web.ps1`
  - `launch-or-focus-web.ps1`

### 5.4 Windows Shell Shortcuts
- Winland only takes over the Win combos bound in `config.conf`. Every other Win combo (`Win+E`,
  `Win+R`, `Win+Shift+S`, `Win+V`, ...) keeps its normal Windows behavior, and all of them
  work as usual whenever Winland is not running.

## 6. User Experience Requirements

### 6.1 Startup & Presence
- The user starts one program, `winland` — a tray application (icon on the primary taskbar) — with a
  single elevation prompt (none at logon with the auto-start task). It starts `winland-keys`, which
  runs headless (no tray icon or balloons), and `winland-indicator`.
- Exiting Winland from its tray menu ends its companion processes too — as does Winland ending any
  other way.
- `winland-keys` logs its startup status (hook installed / failed); `winland` shows a "running"
  balloon.
- The `winland` tray icon is a static Winland badge; the active workspace of every monitor is
  shown by the on-screen indicator circles (`winland-indicator`).

### 6.2 Discoverability
- The `winland` tray menu must expose:
  - Status
  - Exit

### 6.3 Configurability
- Non-developer users can edit shortcuts by modifying `config.conf`.
- Config is read at startup; restarting Winland applies new binds.

### 6.4 Stability Expectations
- Failures in launching apps/scripts or logging should not crash app.
- Missing/unreadable config should degrade safely (empty binds).

## 7. Business Rules

1. **Win key is primary modifier** for all claimed combos.
2. **Bindings are resolved in file order** — the first matching `bind` wins. Workspace and
   window-management combos are themselves binds (commands of the form `winlandctl <verb>`), not
   reserved built-ins, so they can be rebound or removed.
3. **Workspace switching affects only target monitor**; never alter unrelated monitors. Moving a
   workspace affects exactly the two monitors involved: the one it leaves and the one it goes to.
4. **Released workspace can be re-homed** to a new monitor when re-entered.
5. **No hard dependency on external services**; local desktop utility.

## 8. Out of Scope (Current)

- Cloud sync of config or workspace state.
- GUI settings editor for binds.
- Linux/macOS support.
- Complex window tiling/layout algorithms.

## 9. Success Criteria (Product-Level)

- User can reliably switch/focus workspaces with Win+1..9.
- User can focus nearest window directionally with Win+Arrows.
- User can define and execute app shortcuts from config.
- App remains running in tray with minimal interruption.

## 10. Future Business Opportunities

- Settings UI for keybind management.
- Per-workspace naming and visual indicators.
- Import/export preset profiles.
- Per-app rules for workspace auto-assignment.
