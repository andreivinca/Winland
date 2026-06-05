# Winland Architecture Overview

## 1. High-Level Summary

Winland is a tray-resident Windows desktop utility that intercepts Win-key combos globally and routes them to three feature groups:
- Workspaces
- Window focus/navigation
- Configurable app shortcuts

The system is organized around a thin root orchestrator (`WinlandApp`) and feature modules with clear ownership.

## 2. Runtime Composition

## 2.1 Process Startup
1. `Program.Main` initializes WinForms runtime.
2. `Application.Run(new WinlandApp())` starts tray application context.

## 2.2 Root Object (`WinlandApp`)
Construction sequence:
1. Disable native Windows Win hotkeys via policy (`WindowsHotkeyDisabler.EnsureDisabled`).
2. Create keyboard hook (`KeyboardHook`) with resolver + dispatcher callbacks.
3. Create workspace manager.
4. Load config and parse binds (`Config.Load` + `HotkeyConfig.Parse`).
5. Create tray icon with menu callbacks.
6. Wire primary workspace updates to tray icon state.

## 3. Module Responsibilities

### 3.1 `KeyboardHook`
- Global low-level keyboard hook on dedicated thread.
- Tracks Win key state.
- Calls resolver for Win+combo key-down events.
- Swallows claimed combos and dispatches action IDs to UI thread through hidden message window.

### 3.2 `WinlandApp`
- Resolves action IDs from key state.
- Enforces precedence (built-ins then config binds).
- Dispatches actions to feature modules.
- Owns config path/reload and app lifecycle.

### 3.3 `WorkspaceManager`
- Maintains monitor/workspace/window mappings.
- Executes workspace switch and release semantics.
- Tracks active workspace shown on primary monitor.
- Uses WinEvent hooks for dynamic workspace membership reassignment.

### 3.4 `WindowNavigator`
- Directional nearest-window focus.
- Foreground close behavior.

### 3.5 `AppShortcuts` (`HotkeyConfig`, `AppLauncher`, scripts)
- Parse user shortcut bindings from config.
- Execute commands or script verbs.

### 3.6 `TrayIcon`
- Visual presence and control plane for user operations.
- Shows status and workspace indicator.

## 4. Action ID Architecture

`WinlandApp` uses integer action IDs:
- Built-ins (small constants): directions, close, release.
- Workspace actions: `ACTION_WORKSPACE_BASE + workspaceNumber`.
- Config actions: `ACTION_CONFIG_BASE + bindIndex`.

Benefits:
- Fast lookup in hook path.
- Stable dispatch model decoupled from UI message transport.

## 5. End-to-End Input Flow

1. User presses key.
2. `KeyboardHook` receives low-level event.
3. If Win held and key-down:
   - Query resolver in `WinlandApp`.
4. If unclaimed (`0`): pass through to OS.
5. If claimed:
   - Inject dummy key events to suppress Start-menu side effect.
   - Post action id to UI message window.
6. `WinlandApp.HandleAction` runs on UI thread.
7. Feature module executes action.

## 6. Data and Config Flow

1. `Config.Load(path)` tokenizes `config.conf` into entries.
2. `HotkeyConfig.Parse` extracts `bind` entries into strongly-typed binds.
3. On hotkey match, bind action executes via `AppLauncher.Run`.
4. User can reload config from tray without restart.

## 7. Cross-Cutting Concerns

### 7.1 Error Tolerance
- Most operational failures are intentionally swallowed to avoid app termination.

### 7.2 Logging
- Best-effort append-only log file near executable (`winland-hooklog.txt`).

### 7.3 Threading
- Hook callback work is intentionally minimal.
- Real actions execute on UI thread.

### 7.4 Windows Integration
- Heavy Win32 interop for windows, monitors, focus, hooks, and DWM attributes.

## 8. Key Design Decisions

1. **Per-monitor workspace model** instead of global desktop workspace.
2. **Workspace home pinning** for deterministic monitor behavior.
3. **Dedicated hook thread** for callback responsiveness.
4. **Script-verb launcher** for extensible shortcut behaviors without recompiling app.
5. **Policy-based Win hotkey suppression** to avoid shell conflicts.

## 9. Known Architectural Constraints

- Windows-only implementation by design.
- Stateful behavior is in-memory; no persisted runtime state.
- Strong dependence on top-level window heuristics for both focus and workspace membership.

## 10. Suggested Reading Order for New Contributors / AI Agents

1. `Winland/WinlandApp.cs`
2. `Winland/KeyboardHook.cs`
3. `Winland/Workspaces/WorkspaceManager.cs`
4. `Winland/WindowFocus/WindowNavigator.cs`
5. `Winland/AppShortcuts/HotkeyConfig.cs`
6. `Winland/AppShortcuts/AppLauncher.cs`
7. `Winland/config.conf`
