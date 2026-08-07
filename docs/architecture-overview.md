# Winland Architecture Overview

## 1. High-Level Summary

Winland is split into **three processes** that together provide three feature groups:
- Workspaces
- Window focus/navigation
- Configurable app shortcuts

| Process | Responsibility |
| --- | --- |
| **`winland-keys`** | Global Super (Win) keyboard hook. Maps each combo to a command line and runs it. No workspace/focus logic. |
| **`winland-env`** | The "window manager": workspaces, directional focus, numbered tray icon. No keyboard hook — driven over a named pipe. |
| **`winlandctl`** | One-shot CLI: forwards a single command to `winland-env` over the pipe and reports the result. |

They share **`Winland.Common`** (the pipe protocol constants in `Ipc.cs` and the append-only logger
in `Log.cs`).

### 1.1 Why the split

- **Crash isolation** — the hook lives in a tiny, stable process; restarting the window-manager
  service doesn't drop your hotkeys, and vice-versa.
- **Scriptable control surface** — anything that can run `winlandctl <verb>` can drive the
  environment; the keys daemon is just the most common caller.
- **A dumb hotkey daemon** — `winland-keys` only maps combos to commands; all behavior is behind the
  `winlandctl` verbs.

### 1.2 The cost

- Both daemons must be running.
- `winlandctl.exe` must sit next to `winland-keys.exe` (the keys daemon resolves it as a sibling
  exe). The `winland-keys` build copies it there; the start scripts assume the packaged layout.

## 2. End-to-End Input Flow

```
Win+1
  → winland-keys: KeyboardHook callback (dedicated thread)
  → resolver maps (vk, shift, alt, ctrl) to a bind index
  → swallow the combo, inject a dummy 0xFF keystroke (suppress Start menu), post the action to the UI thread
  → KeysApp.HandleAction → AppLauncher.Run("winlandctl workspace 1")
  → AppLauncher finds sibling winlandctl.exe → starts it with args "workspace 1"
  → winlandctl connects to \\.\pipe\winland-env, writes "workspace 1", reads one reply line
  → winland-env: DispatchServer accepts the connection (background thread)
  → UiInvoker marshals the command to the UI thread → Dispatcher.Execute
  → WorkspaceManager.SwitchFocusedMonitorTo(1)
  → reply "OK" written back to winlandctl (exit code 0)
```

For unclaimed combos (`actionId == 0`) the hook passes the key through to the OS untouched.

## 3. `winland-keys` — the hotkey daemon

### 3.1 `Program`
- Single-instance mutex (`Local\winland-keys`) — a second hook would double-fire every shortcut.
- Runs `KeysApp` as a WinForms `ApplicationContext`.

### 3.2 `KeysApp`
- On startup sets the `NoWinKeys` policy (`WindowsHotkeyDisabler.EnsureDisabled`).
- Loads `config.conf` (`Config.Load`) and parses binds (`HotkeyConfig.Parse`).
- Owns the `KeyboardHook` (resolver + dispatcher callbacks). Headless — no tray icon; startup
  status goes to the log.
- `ResolveActionId` returns *(bind index + 1)* for a matching Super combo, or `0`.
- `HandleAction` runs the matched bind via `AppLauncher.Run`.

### 3.3 `KeyboardHook`
- Global low-level hook (`WH_KEYBOARD_LL`) on a dedicated STA thread with its own message loop.
- Stateless: on each key-down it queries the OS (`GetAsyncKeyState`) for the live Win key state and,
  while Win is held, calls the resolver.
- Swallows claimed combos and dispatches the action id to the UI thread via a hidden message window.

### 3.4 `AppLauncher` and `UnelevatedLauncher`
- Splits a bind's command into `verb` + `args` and resolves the verb, in order:
  1. `<verb>.ps1` next to the exe → run via `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '<script>' <args>"`.
  2. `<verb>.exe` sibling next to the exe → run directly (**how `winlandctl` is found**).
  3. Otherwise → run the whole line as a shell command (App Paths names, URIs, documents).
- Scripts and shell commands are launched with the user's **normal (unelevated) token** via
  `UnelevatedLauncher`, which duplicates the desktop shell's token (`CreateProcessWithTokenW`) —
  otherwise every app a bind starts would inherit the daemon's elevation. Shell semantics come from
  an unelevated `cmd /c start "" <command>`. If the unelevated route is unavailable (no shell, UAC
  off), the launch falls back to a plain elevated start.
- All launch failures are swallowed.

### 3.5 `Config` / `HotkeyConfig`
- `Config` tokenizes `config.conf` into `(keyword, value)` entries (ignores blanks/`#`).
- `HotkeyConfig.Parse` turns `bind` values into `KeyBind(Modifiers, Vk, Action)`; the action is the
  raw command text, resolved at run time.

## 4. `winland-env` — the environment service

### 4.1 `EnvApp`
Construction wires:
1. `WorkspaceManager` (workspace engine + WinEvent hooks).
2. `Dispatcher` (command → operation).
3. `UiInvoker` (marshal a pipe command onto the UI thread).
4. `DispatchServer` (named-pipe listener).
5. `TrayIcon` (numbered workspace icon), subscribed to `PrimaryWorkspaceChanged`.

It has **no keyboard hook**: every action originates from the pipe.

### 4.2 `DispatchServer`
- A `NamedPipeServerStream` (`Ipc.PipeName` = `winland-env`) loop on a background thread.
- Reads one command line per connection, hands it to `UiInvoker.Invoke`, writes back the reply.
- A broken/malformed connection never kills the server; it accepts the next one.

### 4.3 `UiInvoker`
- Marshals a command string from the pipe thread onto the UI thread via a hidden window +
  synchronous `SendMessage`, so the caller blocks until the handler returns its reply. A lock
  serialises callers.

### 4.4 `Dispatcher`
- The single source of truth for what the environment can do. Maps a verb to an operation and returns
  the protocol response:
  - `workspace <n>` (n >= 1, scratchpad id rejected) → `WorkspaceManager.SwitchFocusedMonitorTo`
  - `movetoworkspace <n>` → `WorkspaceManager.MoveFocusedWindowToWorkspace`
  - `link-here` → `WorkspaceManager.LinkFocusedWindowToCurrentWorkspace`
  - `scratchpad` → `WorkspaceManager.ToggleScratchpad`
  - `workspace-release` → `WorkspaceManager.ReleaseCurrentWorkspace`
  - `focus <left|right|up|down>` → `WindowNavigator.FocusNearest`
  - `focus-app <process>` → `WindowNavigator.FocusApp` (`ERR` when there is nothing to focus)
  - `close` → `WindowNavigator.CloseForeground`
  - unknown → `ERR unknown verb '<verb>'`

### 4.5 `WorkspaceManager` / `WindowNavigator` / `TrayIcon`
- `WorkspaceManager` — monitor/workspace/window mappings, switch/move/link/scratchpad/release
  semantics, and the `PrimaryWorkspaceChanged` event. Membership changes **only** on explicit move,
  link, or close — never from window interaction (see `features/workspaces.md`).
- `WindowNavigator` — directional nearest-window focus, focus-by-process (`focus-app`), and
  foreground close.
- `TrayIcon` — runtime-drawn numbered icon for the primary monitor's workspace ("S" for the
  scratchpad, a dash when the monitor shows no workspace).

## 5. `winlandctl` — the control CLI

- `winlandctl <verb> [args…]` joins its args into one command line, connects to the pipe (2s
  timeout), writes the line, reads one reply.
- Exit codes: `0` OK · `1` env replied `ERR`/no reply · `2` bad usage · `3` couldn't reach env.
- Deliberately one-shot and dependency-free, so it works from a bind, a script, or a terminal.

## 6. Control Protocol (`Winland.Common/Ipc.cs`)

- Transport: a local named pipe, `\\.\pipe\winland-env` (`Ipc.PipeName`).
- Line-based: the client writes one command line; the server replies with a single line — `OK`
  (`Ipc.Ok`) on success, or `ERR <message>` (`Ipc.ErrPrefix`) on failure.
- The pipe carries an explicit ACL granting the logged-on user's processes access, elevated or not —
  so unelevated scripts and terminals can run `winlandctl` (see §7.4).
- A connected client that never sends its line is dropped after a read timeout, so it cannot wedge
  the single-connection server.

## 7. Cross-Cutting Concerns

### 7.1 Error Tolerance
- Most operational failures (launches, I/O, broken pipe connections) are intentionally swallowed to
  avoid terminating a daemon.

### 7.2 Logging
- Best-effort append-only log file near each executable (`winland-hooklog.txt`), via
  `Winland.Common.Log`. `winland-env` logs workspace transitions; `HotkeyConfig` logs ignored binds.

### 7.3 Threading
- Hook callback work is minimal; real actions run on the keys UI thread.
- Pipe-server work is marshalled to the env UI thread before touching window/WinEvent state.

### 7.4 Elevation
- Both daemons request administrator rights (app manifests). Elevation is needed for the hook to see
  input bound for elevated windows, and for `winland-env` to minimize/restore/focus elevated windows
  (UIPI).
- That elevation stops at the daemons: apps launched by binds run with the user's normal token
  (`UnelevatedLauncher`), and the control pipe explicitly admits unelevated clients. The trade of the
  pipe ACL: any same-user process can drive the window-management verbs; the verbs are deliberately
  limited to window management.

### 7.5 Windows Integration
- Heavy Win32/DWM interop for windows, monitors, focus, hooks, and DWM attributes.

## 8. Key Design Decisions

1. **Process split** — hotkey hook, window manager, and control CLI are separate for crash isolation
   and scriptability.
2. **Named-pipe control channel** — one front door (`Dispatcher`) for every environment action.
3. **Per-monitor workspace model** with **home pinning** for deterministic monitor behavior.
4. **Dedicated hook thread** for callback responsiveness.
5. **Command-resolution launcher** (script / sibling exe / shell) for extensible binds without
   recompiling — and the mechanism by which the keys daemon reaches `winlandctl`.
6. **Policy-based Win hotkey suppression** (`NoWinKeys`) to avoid shell conflicts.
7. **Elevation stops at the daemons** — launched apps get the user's normal token
   (`UnelevatedLauncher`), and the pipe ACL admits unelevated `winlandctl` callers.

## 9. Known Architectural Constraints

- Windows-only by design.
- Runtime state is in-memory; nothing is persisted across restarts.
- The environment is unreachable if `winland-env` isn't running (`winlandctl` exits `3`); shortcuts
  that aren't `winlandctl` verbs still work as long as `winland-keys` runs.
- Strong dependence on top-level window heuristics for both focus and workspace membership.

## 10. Suggested Reading Order for New Contributors / AI Agents

1. `src/winland-keys/KeysApp.cs`
2. `src/winland-keys/KeyboardHook.cs`
3. `src/winland-keys/AppLauncher.cs` and `HotkeyConfig.cs`
4. `src/Winland.Common/Ipc.cs`
5. `src/winlandctl/Program.cs`
6. `src/winland-env/EnvApp.cs` → `DispatchServer.cs` → `UiInvoker.cs` → `Dispatcher.cs`
7. `src/winland-env/WorkspaceManager.cs`
8. `src/winland-env/WindowNavigator.cs`
9. `src/winland-keys/config.conf`
