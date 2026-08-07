# Winland Business Requirements

## 1. Purpose

Winland provides keyboard-first desktop productivity on Windows through three core capabilities:
1. **Per-monitor workspaces**
2. **Directional window focus/navigation**
3. **Configurable app shortcuts**

The product goal is to deliver Hyprland/Omarchy-style workflow ergonomics on Windows while remaining lightweight, tray-resident, and fast to use.

Implementation note: Winland is delivered as three cooperating processes — `winland-keys` (the
hotkey daemon), `winland-env` (the window-manager service), and `winlandctl` (a control CLI). This is
an architectural detail, not a product feature; the requirements below describe user-facing behavior
regardless of process boundaries. See `architecture-overview.md`.

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
- A window joins a workspace only on explicit move (`Win+Shift+N`) or link (`Win+Space`) — never as a
  side effect of interacting with it.
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
  - `show-desktop.ps1`

### 5.4 Re-registered Windows Shell Shortcuts
- Because Winland disables native Win-key shell shortcuts (`NoWinKeys`), selected default Windows behaviors should be explicitly re-registered via config.
- Current defaults include `Win+D` → `show-desktop`, `Win+E` → `explorer.exe`, `Win+R` → the Run
  dialog (via its shell CLSID), and `Win+Shift+S` → `ms-screenclip:`.

## 6. User Experience Requirements

### 6.1 Startup & Presence
- `winland-env` starts as a tray application (icon on the primary taskbar); `winland-keys` runs
  headless (no tray icon or balloons). The start script (`start-winland`) launches both with a
  single elevation prompt.
- `winland-keys` logs its startup status (hook installed / failed); `winland-env` shows a control-
  channel balloon.
- The `winland-env` tray icon displays the active workspace number of the primary monitor.

### 6.2 Discoverability
- The `winland-env` tray menu must expose:
  - Status
  - Exit

### 6.3 Configurability
- Non-developer users can edit shortcuts by modifying `config.conf`.
- Config is read at startup; restarting the keys daemon (via the start script) applies new binds.

### 6.4 Stability Expectations
- Failures in launching apps/scripts or logging should not crash app.
- Missing/unreadable config should degrade safely (empty binds).

## 7. Business Rules

1. **Win key is primary modifier** for all claimed combos.
2. **Bindings are resolved in file order** — the first matching `bind` wins. Workspace and
   window-management combos are themselves binds (commands of the form `winlandctl <verb>`), not
   reserved built-ins, so they can be rebound or removed.
3. **Workspace switching affects only target monitor**; never alter unrelated monitors.
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
