# Winland Documentation

This folder contains the baseline project documentation for future contributors (human or AI) to understand current product behavior and implementation constraints.

## Documentation Map

### 1) Project Orientation
- [Project Overview](project-overview.md)

### 2) Product and Requirement Specs
- [Business Requirements](requirements/business-requirements.md)
- [Technical Requirements](requirements/technical-requirements.md)

### 3) Architecture and System Design
- [Architecture Overview](architecture-overview.md)

### 4) Feature Deep Dives
- [Workspaces](features/workspaces.md)
- [Window Focus](features/window-focus.md)
- [App Shortcuts](features/app-shortcuts.md)

## Scope

These documents describe the current implementation: **three cooperating processes** under `src/`
(`winland-keys`, `winland-env`, `winlandctl`) plus the shared `Winland.Common` library. The two
daemons target `net10.0-windows` (WinForms + Win32 interop); `winlandctl` targets `net10.0`.

## Source of Truth

When docs and code conflict, current code is authoritative unless an explicit requirement in this folder states intended future behavior.

Primary implementation references:
- `src/winland-keys/KeysApp.cs` — hotkey daemon root
- `src/winland-keys/KeyboardHook.cs` — the global Win-key hook
- `src/winland-keys/AppLauncher.cs`, `UnelevatedLauncher.cs`, `HotkeyConfig.cs`, `Config.cs` — binds + command execution
- `src/winland-keys/config.conf` — shipped shortcut bindings
- `src/Winland.Common/Ipc.cs` — control-pipe protocol (shared)
- `src/winlandctl/Program.cs` — the control CLI
- `src/winland-env/EnvApp.cs`, `DispatchServer.cs`, `UiInvoker.cs`, `Dispatcher.cs` — pipe-driven service
- `src/winland-env/WorkspaceManager.cs` — per-monitor workspaces
- `src/winland-env/WindowNavigator.cs` — directional focus + close
