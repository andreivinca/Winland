# Winland Business Requirements

## 1. Purpose

Winland provides keyboard-first desktop productivity on Windows through three core capabilities:
1. **Per-monitor workspaces**
2. **Directional window focus/navigation**
3. **Configurable app shortcuts**

The product goal is to deliver Hyprland/Omarchy-style workflow ergonomics on Windows while remaining lightweight, tray-resident, and fast to use.

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
3. **Low friction**: Configuration is plain text and reloadable from tray.
4. **Continuity with Windows**: Standard Win combos can be reintroduced via config after disabling shell Win hotkeys.

## 5. In-Scope Features (Current)

### 5.1 Workspaces
- Nine numbered workspaces (`1..9`).
- Per-monitor current workspace state.
- Workspace “home monitor” concept:
  - First entry pins workspace to monitor under cursor.
  - Later switches target that monitor.
- Ability to release current workspace from monitor (`Win+Shift+W`).

### 5.2 Window Focus
- `Win+Left/Up/Right/Down`: focus nearest window by direction.
- `Win+W`: close foreground window (via `WM_CLOSE`).

### 5.3 App Shortcuts
- Config-driven binds in `config.conf`.
- Supports modifiers (`SHIFT`, `ALT`, `CTRL`) plus required `SUPER`.
- Supports script verbs (`<verb>.ps1`) and normal command execution.
- `launch-or-focus` behavior requirement: focus an existing instance when it is not currently focused; if already focused, open a new instance.
- Includes bundled scripts:
  - `launch-or-focus.ps1`
  - `launch-web.ps1`
  - `show-desktop.ps1`

### 5.4 Re-registered Windows Shell Shortcuts
- Because Winland disables native Win-key shell shortcuts (`NoWinKeys`), selected default Windows behaviors should be explicitly re-registered via config.
- Current default includes `Win+D` mapped to `show-desktop`, to mimic the original “Show Desktop” behavior.

## 6. User Experience Requirements

### 6.1 Startup & Presence
- App starts as a tray application.
- User receives startup balloon status (hook installed / failed).
- Tray icon displays active workspace number of primary monitor.

### 6.2 Discoverability
- Tray menu must expose:
  - Status
  - Reload config
  - Open config
  - Exit

### 6.3 Configurability
- Non-developer users can edit shortcuts by modifying `config.conf`.
- Reload operation should apply new binds without app restart.

### 6.4 Stability Expectations
- Failures in launching apps/scripts or logging should not crash app.
- Missing/unreadable config should degrade safely (empty binds).

## 7. Business Rules

1. **Win key is primary modifier** for all claimed combos.
2. **Built-in workspace and window-management shortcuts take precedence** over config binds when they overlap.
3. **Workspace switching affects only target monitor**; never alter unrelated monitors.
4. **Released workspace can be re-homed** to a new monitor when re-entered.
5. **No hard dependency on external services**; local desktop utility.

## 8. Out of Scope (Current)

- Cloud sync of config or workspace state.
- GUI settings editor for binds.
- Arbitrary workspace count beyond 9.
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
