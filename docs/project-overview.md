# Winland Project Overview

## 1. What Winland Is

Winland is a Windows tray application that provides keyboard-centric desktop workflow features inspired by tiling window manager ergonomics.

Current core capabilities:
- Per-monitor numbered workspaces (`1..9`)
- Directional nearest-window focus (`Win+Arrows`)
- Configurable app shortcuts from text config (`config.conf`)

## 2. Primary Goals

- Reduce context-switch friction for keyboard-first users.
- Make multi-monitor workspace behavior deterministic.
- Keep customization simple through text configuration and script verbs.

## 3. Runtime Experience

- App starts into the system tray.
- Global Win-key combos are intercepted and resolved to actions.
- Tray menu offers status, config reload/open, and exit.
- Tray icon displays workspace currently shown on primary monitor.

## 4. Feature Summary

### 4.1 Workspaces
- `Win+1..9` switches/focuses workspace.
- Workspaces are pinned to a home monitor after first use.
- `Win+Shift+W` releases current workspace from active monitor.

### 4.2 Window Focus
- `Win+Left/Up/Right/Down` focuses nearest candidate window by direction.
- `Win+W` sends close request to foreground window.

### 4.3 App Shortcuts
- User-defined `bind = ...` lines in `config.conf`.
- Supports modifiers and key names.
- Actions can launch commands or invoke script verbs (`<verb>.ps1`).
- Default config re-registers selected Windows shell shortcuts (for example `Win+D` via `show-desktop.ps1`).

## 5. Tech Stack

- C# / .NET 10 (`net10.0-windows`)
- WinForms for app context + tray UI
- Win32/DWM interop for hooks, monitor/window operations, and focus behavior
- PowerShell scripts for extensible launcher actions

## 6. Configuration and Assets

Files packaged next to executable:
- `config.conf`
- `launch-or-focus.ps1`
- `launch-web.ps1`
- `show-desktop.ps1`

Config model:
- Global key-value lines (`keyword = value`)
- Shortcut bindings consumed from `bind` entries

## 7. Repository/Code Orientation

Main code areas:
- `Winland/WinlandApp.cs` – app orchestration and action routing
- `Winland/KeyboardHook.cs` – global Win-key hook and dispatch
- `Winland/Workspaces/WorkspaceManager.cs` – workspace engine
- `Winland/WindowFocus/WindowNavigator.cs` – directional focus/close
- `Winland/AppShortcuts/` – bind parser, launcher, scripts
- `Winland/UI/TrayIcon.cs` – tray UX

## 8. Documentation Index

For detailed requirements and internals:
- `docs/requirements/business-requirements.md`
- `docs/requirements/technical-requirements.md`
- `docs/architecture-overview.md`
- `docs/features/workspaces.md`
- `docs/features/window-focus.md`
- `docs/features/app-shortcuts.md`
