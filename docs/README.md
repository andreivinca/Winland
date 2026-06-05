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

These documents describe the current implementation in the `Winland` project (Windows tray app targeting `net10.0-windows` with WinForms and Win32 interop) and define requirements for maintaining/extending current behavior.

## Source of Truth

When docs and code conflict, current code is authoritative unless an explicit requirement in this folder states intended future behavior.

Primary implementation references:
- `Winland/WinlandApp.cs`
- `Winland/KeyboardHook.cs`
- `Winland/Workspaces/WorkspaceManager.cs`
- `Winland/WindowFocus/WindowNavigator.cs`
- `Winland/AppShortcuts/HotkeyConfig.cs`
- `Winland/AppShortcuts/AppLauncher.cs`
- `Winland/config.conf`
