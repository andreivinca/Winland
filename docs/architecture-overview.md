# Winland Architecture Overview

## 1. High-Level Summary

Winland is **one app, `winland`, which runs two companion processes** and ends them when it exits,
plus a control CLI. Together they provide four feature groups:
- Workspaces
- Window focus/navigation
- Configurable app shortcuts
- On-screen workspace indication

| Process | Responsibility | Elevation |
| --- | --- | --- |
| **`winland`** | The main app, the one the user starts: the "window manager" (workspaces, tiling, directional focus) and the tray presence. No keyboard hook — driven over a named pipe. Starts and ends the two companions (`Companions`). | elevated |
| **`winland-keys`** | Companion. Global Super (Win) keyboard hook. Maps each combo to a command line and runs it. No workspace/focus logic. | Winland's (elevated) |
| **`winland-indicator`** | Companion. One translucent workspace circle per monitor. Subscribes to `winland`'s broadcast event pipe; sends nothing back. | the user's (unelevated) |
| **`winlandctl`** | One-shot CLI: forwards a single command to `winland` over the pipe and reports the result. | inherits its caller |

They share **`Winland.Common`** (the pipe protocol constants in `Ipc.cs`, the append-only logger in
`Log.cs`, and `UnelevatedLauncher`, which starts a process with the user's normal token).

### 1.1 Why the split

- **Crash isolation** — the hook lives in a tiny, stable process.
- **Scriptable control surface** — anything that can run `winlandctl <verb>` can drive Winland; the
  keys daemon is just the most common caller.
- **A dumb hotkey daemon** — `winland-keys` only maps combos to commands; all behavior is behind the
  `winlandctl` verbs.
- **Least privilege for the UI** — the indicator draws and reads only, so it runs without the
  administrator rights it has no use for.

### 1.2 One app to start: companions

The split stays an implementation detail: the user starts `winland.exe` only. `Companions`
(`src/winland/Companions.cs`) is the one place that knows the companion processes — which exist, with
which rights each runs, how to start them and how they end:

- **Start** (once, when `WinlandApp` has its pipes listening): each companion is started from
  Winland's own folder — `winland-keys` with Winland's elevated token, `winland-indicator` with the
  user's normal token borrowed from the shell (`UnelevatedLauncher`; Winland's own token as a
  fallback). A copy left from an earlier run (an older version, or one started by hand) is stopped
  first: it wouldn't end with this Winland, and a second keyboard hook fires every shortcut twice.
- **End**: every started companion is put in a `KillOnCloseJob` of its own — a Windows job object
  with `KILL_ON_JOB_CLOSE`, held only by Winland. Exit (tray) disposes them; a crash or Task Manager
  closes them just the same; either way the system ends the companions. One job each, because a
  process already in a job can only join an empty one, and a companion may start in one (the
  shell-token launch puts the indicator in the logon service's job; a Winland started by Task
  Scheduler passes the scheduler's job on to `winland-keys`). A companion that can't be put in its
  job is ended at once, so none is ever left running without one. The jobs have
  `SILENT_BREAKAWAY_OK`, so the companions' own children — the apps a shortcut launches — are not in
  them and never end with Winland (`tests/winland.Tests/KillOnCloseJobTests.cs` checks both).

The build supports this layout: building `src/winland` builds both companions (`ProjectReference`,
`ReferenceOutputAssembly=false`) and its `CopyCompanions` target copies their whole output — with
`winlandctl.exe`, `config.conf` and the helper scripts — next to `winland.exe`.

## 2. End-to-End Input Flow

```
Win+1
  → winland-keys: KeyboardHook callback (dedicated thread)
  → resolver maps (vk, shift, alt, ctrl) to a bind index
  → swallow the combo, inject a dummy 0xFF keystroke (suppress Start menu), post the action to the UI thread
  → KeysApp.HandleAction → AppLauncher.Run("winlandctl workspace 1")
  → AppLauncher finds sibling winlandctl.exe → starts it with args "workspace 1"
  → winlandctl connects to \\.\pipe\winland, writes "workspace 1", reads one reply line
  → winland: DispatchServer accepts the connection (background thread)
  → UiInvoker marshals the command to the UI thread → Dispatcher.Execute
  → WorkspaceManager.SwitchFocusedMonitorTo(1)
  → reply "OK" written back to winlandctl (exit code 0)
```

For unclaimed combos (`actionId == 0`) the hook passes the key through to the OS untouched.

## 3. `winland-keys` — the hotkey daemon (a companion, started by `winland`)

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
- On each key-down it queries the OS (`GetAsyncKeyState`) for the live Win key state and, while Win
  is held, calls the resolver.
- Swallows claimed combos and dispatches the action id to the UI thread via a hidden message window.
  Auto-repeat key-downs of a held combo are swallowed without dispatching again (one action per press).

### 3.4 `AppLauncher` and `UnelevatedLauncher`
- Splits a bind's command into `verb` + `args` and resolves the verb, in order:
  1. `<verb>.ps1` next to the exe → run via `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '<script>' <args>"`.
  2. `<verb>.exe` sibling next to the exe → run directly (**how `winlandctl` is found**).
  3. Otherwise → run the whole line as a shell command (App Paths names, URIs, documents).
- Scripts and shell commands are launched with the user's **normal (unelevated) token** via
  `UnelevatedLauncher` (`Winland.Common`, shared with `Companions`), which duplicates the desktop
  shell's token (`CreateProcessWithTokenW`) —
  otherwise every app a bind starts would inherit the daemon's elevation. Shell semantics come from
  an unelevated `cmd /c start "" <command>`. If the unelevated route is unavailable (no shell, UAC
  off), the launch falls back to a plain elevated start.
- Launch failures are logged and otherwise ignored.

### 3.5 `Config` / `HotkeyConfig`
- `Config` tokenizes `config.conf` into `(keyword, value)` entries (ignores blanks/`#`).
- `HotkeyConfig.Parse` turns `bind` values into `KeyBind(Modifiers, Vk, Action)`; the action is the
  raw command text, resolved at run time.

## 4. `winland` — the main app

### 4.1 `WinlandApp`
Construction wires:
1. `WorkspaceManager` (workspace engine + WinEvent hooks).
2. `Dispatcher` (command → operation).
3. `UiInvoker` (marshal a pipe command onto the UI thread).
4. `DispatchServer` (named-pipe listener).
5. `TrayIcon` (static badge + Status/Exit menu).
6. `EventServer` (broadcast pipe), subscribed to `WorkspacesChanged` and given an initial snapshot.
7. `Companions` — last, so both pipes already listen when the companions connect: starts
   `winland-keys` and `winland-indicator` (§1.2). Disposed first on Exit, which ends them.

It has **no keyboard hook**: every action originates from the pipe.

### 4.2 `DispatchServer`
- A `NamedPipeServerStream` (`Ipc.PipeName` = `winland`) loop on a background thread.
- Reads one command line per connection, hands it to `UiInvoker.Invoke`, writes back the reply.
- A broken/malformed connection never kills the server; it accepts the next one.
- Its pipe is created with the ACL from `PipeAcl.ForCurrentUser()`, shared with `EventServer`.

### 4.2a `EventServer`
- The push half of Winland: a broadcast-only pipe (`Ipc.EventsPipeName` =
  `winland-events`, `PipeDirection.Out`, up to 8 instances) that carries workspace state out and
  accepts nothing in.
- An accept thread hands each connection to its own subscriber thread, so a stalled reader blocks
  only itself and the listener stays free.
- `Publish(snapshot)` is called on the UI thread and must never block: it formats the line, stores it
  as the newest state, bumps a version, and pulses the subscriber threads. Only the **latest**
  snapshot is ever sent — a slow subscriber skips states it missed, which is right for "what is on
  screen now".
- Each subscriber thread writes the current snapshot on connect, then every later one; any failure
  ends that connection silently.
- `FormatSnapshot` is the single place the wire format is produced.

### 4.3 `UiInvoker`
- Marshals a command string from the pipe thread onto the UI thread via a hidden window +
  synchronous `SendMessage`, so the caller blocks until the handler returns its reply. A lock
  serialises callers.

### 4.4 `Dispatcher`
- The single source of truth for what Winland can do. Maps a verb to an operation and returns
  the protocol response:
  - `workspace <n>` (n >= 1, scratchpad id rejected) → `WorkspaceManager.SwitchFocusedMonitorTo`
  - `movetoworkspace <n>` → `WorkspaceManager.MoveFocusedWindowToWorkspace`
  - `link-here` → `WorkspaceManager.LinkFocusedWindowToCurrentWorkspace` (tile it)
  - `unlink` → `WorkspaceManager.UnlinkFocusedWindow` (a normal window again)
  - `toggle-float` → `WorkspaceManager.ToggleFloating` (pop a tile out of its strip, or put a floating
    window back into its own workspace's strip)
  - `scratchpad` → `WorkspaceManager.ToggleScratchpad`
  - `workspace-release` → `WorkspaceManager.ReleaseCurrentWorkspace`
  - `focus <left|right|up|down>` → `WorkspaceManager.FocusAdjacentTile` (left/right within a tile
    strip), falling back to `WindowNavigator.FocusNearest`
  - `move-window <left|right>` → `WorkspaceManager.MoveFocusedWindow`
  - `focus-app <process>` → `WindowNavigator.FocusApp` (`ERR` when there is nothing to focus)
  - `focus-web <url>` → `WindowNavigator.FocusWebApp` (matches the browser `--app` window by its
    AppUserModelID `<browser>.<host>_<path>`; `ERR` when there is nothing to focus)
  - `close` → `WindowNavigator.CloseForeground`
  - `place-next-window` → `WorkspaceManager.PlaceNextWindowOnActiveMonitor` (records the monitor
    under the mouse; the first new app window within 10 s is moved there and, if resizable, tiled into
    the workspace shown there — see `WorkspaceManager.LaunchPlacement.cs`). `AppLauncher` sends it over
    the pipe (`WinlandClient`) before a shell-command bind; `launch-or-focus.ps1` / `launch-web.ps1` send it
    before launching.
  - unknown → `ERR unknown verb '<verb>'`

### 4.5 `WorkspaceManager` / `WindowNavigator` / `TrayIcon`
- `WorkspaceManager` — monitor/workspace/window mappings, switch/move/scratchpad/release semantics,
  tiling (`WorkspaceManager.Tiling.cs`: members are tiles in their workspace's scrolling strip, or
  floating once the user drags them out of it,
  geometry in `StripLayout`, order in `TileOrder`), and the change event raised by
  `NotifyWorkspaceChanges`: `WorkspacesChanged` (the whole
  `WorkspaceSnapshot`: every live monitor's device name + shown workspace, left-to-right, for the
  event pipe). Membership changes **only** on explicit move, tile/unlink, or close — never from window
  interaction — and windows that aren't members are never tiled, only put away on a switch (see
  `features/workspaces.md`,
  `features/tiling.md`).
- `WindowNavigator` — directional nearest-window focus, focus-by-process (`focus-app`), and
  foreground close.
- `TrayIcon` — a static Winland badge with the Status/Exit menu; workspace numbers live in the
  on-screen indicator.

## 5. `winlandctl` — the control CLI

- `winlandctl <verb> [args…]` joins its args into one command line, connects to the pipe (2s
  timeout), writes the line, reads one reply.
- Exit codes: `0` OK · `1` env replied `ERR`/no reply · `2` bad usage · `3` couldn't reach env.
- Deliberately one-shot and dependency-free, so it works from a bind, a script, or a terminal.

## 5a. `winland-indicator` — the on-screen indicator

- `Program` — single-instance mutex (`Local\winland-indicator`), `ApplicationConfiguration.Initialize()`
  (per-monitor-v2 DPI), runs `IndicatorApp`.
- `IndicatorApp` — enumerates monitors with Win32 (so device names match `winland`'s
  `\\.\DISPLAYn`) and keeps one `IndicatorWindow` per monitor in step with
  `SystemEvents.DisplaySettingsChanged`; loads/saves dock positions; applies each snapshot.
- `IndicatorWindow` — one monitor's circle: a layered window (`WS_EX_LAYERED`) painted with
  `UpdateLayeredWindow` from a 32bpp ARGB bitmap, plus the drag/dock behavior. Empty title +
  `WS_EX_TOOLWINDOW` keep it out of window management and Alt-Tab; `WS_EX_NOACTIVATE` keeps it from
  taking focus; `WS_EX_TOPMOST` keeps it visible.
- `EventClient` — background thread that stays subscribed to the events pipe, parses snapshots, and
  retries forever (~2 s) when Winland is absent or restarts.
- `UiPoster` — hidden message window that runs background-thread work (snapshots, display changes) on
  the UI thread; the one-way sibling of `winland`'s `UiInvoker`.

See `features/workspace-indicator.md` for the interaction rules and persistence format.

## 6. Control Protocol (`Winland.Common/Ipc.cs`)

### 6.1 Command channel (`Ipc.PipeName`)
- Transport: a local named pipe, `\\.\pipe\winland` (`Ipc.PipeName`).
- Line-based: the client writes one command line; the server replies with a single line — `OK`
  (`Ipc.Ok`) on success, or `ERR <message>` (`Ipc.ErrPrefix`) on failure.
- The pipe carries an explicit ACL granting the logged-on user's processes access, elevated or not —
  so unelevated scripts and terminals can run `winlandctl` (see §7.4).
- A connected client that never sends its line is dropped after a read timeout, so it cannot wedge
  the single-connection server.

### 6.2 Event channel (`Ipc.EventsPipeName`)
- Transport: a second local named pipe, `\\.\pipe\winland-events`, outbound only and never read
  from. Same ACL, so unelevated subscribers can attach.
- One message is one line: space-separated `<device>=<workspace>` pairs, ordered left-to-right, e.g.
  `\\.\DISPLAY1=3 \\.\DISPLAY2=5`. Device names are the stable GDI names and contain no spaces; the
  workspace is the raw int — `0` = the monitor shows nothing, `int.MaxValue` = the scratchpad. The
  client decides how to render those (the indicator draws `–` and `S`).
- A subscriber receives the current snapshot immediately on connect and a fresh one after each
  workspace change, so connecting before or after `winland` starts converges to the same state.
  After ~30 s without a change the current line is re-sent, which is how a subscriber that
  disconnected while idle is noticed and its pipe instance freed.
- Broadcast, latest-wins, best-effort: subscribers are never waited on, missed intermediate states
  are never resent, and a dead or stalled subscriber is dropped silently.

## 7. Cross-Cutting Concerns

### 7.1 Error Tolerance
- Most operational failures (launches, I/O, broken pipe connections) are intentionally swallowed to
  avoid terminating a daemon.

### 7.2 Logging
- Best-effort append-only log file near each executable (`winland-hooklog.txt`), via
  `Winland.Common.Log`. `winland` logs workspace transitions; `HotkeyConfig` logs ignored binds;
  `winland-indicator` logs its monitor count at startup and each successful (re)subscription — not
  its retries.

### 7.3 Threading
- Hook callback work is minimal; real actions run on the keys UI thread.
- Pipe-server work is marshalled to Winland's UI thread before touching window/WinEvent state.

### 7.4 Elevation
- `winland` requests administrator rights (app manifest), and `winland-keys` (also manifested) runs with
  them, started by Winland. Elevation is needed for the hook to see input bound for elevated windows,
  and for `winland` to minimize/restore/focus elevated windows (UIPI).
- That elevation stops there: apps launched by binds run with the user's normal token
  (`UnelevatedLauncher`), and both pipes explicitly admit unelevated clients. The trade of the
  pipe ACL: any same-user process can drive the window-management verbs and read workspace state;
  the verbs are deliberately limited to window management.
- `winland-indicator` ships **without** an app manifest (asInvoker), and Winland starts it with the
  user's normal token. Only when that token isn't available (no shell running) does it fall back to
  Winland's own — harmless, but never required.

### 7.5 Windows Integration
- Heavy Win32/DWM interop for windows, monitors, focus, hooks, and DWM attributes.

## 8. Key Design Decisions

1. **Process split, one app** — hotkey hook, window manager, control CLI, and on-screen indicator are
   separate processes for crash isolation, scriptability, and least privilege; but the user starts
   only `winland`, which owns the companions' lifetime through a kill-on-close job (§1.2).
2. **Named-pipe control channel** — one front door (`Dispatcher`) for every Winland action.
3. **A separate broadcast channel for state** — commands and notifications never share a pipe, so a
   subscriber can neither issue verbs nor be mistaken for a caller awaiting a reply, and publishing
   can never block the UI thread.
4. **Per-monitor workspace model** with **home pinning** for deterministic monitor behavior.
5. **Dedicated hook thread** for callback responsiveness.
6. **Command-resolution launcher** (script / sibling exe / shell) for extensible binds without
   recompiling — and the mechanism by which the keys daemon reaches `winlandctl`.
7. **Policy-based Win hotkey suppression** (`NoWinKeys`) to avoid shell conflicts.
8. **Elevation stops at Winland and its hook** — launched apps get the user's normal token
   (`UnelevatedLauncher`), the pipe ACL admits unelevated `winlandctl` callers, and the indicator is
   started unelevated.

## 9. Known Architectural Constraints

- Windows-only by design.
- Workspace state is in-memory; nothing is persisted across restarts except the indicator's dock
  positions (a text file next to its exe).
- Winland is unreachable while `winland` isn't running (`winlandctl` exits `3`) — and then neither
  companion runs either.
- A companion that crashes is not restarted; restarting Winland restarts it.
- Strong dependence on top-level window heuristics for both focus and workspace membership.
- The indicator's circles are plain topmost windows: they are not hidden over fullscreen exclusive
  applications.

## 10. Suggested Reading Order for New Contributors / AI Agents

1. `src/winland/WinlandApp.cs` → `Companions.cs` → `KillOnCloseJob.cs`
2. `src/winland-keys/KeysApp.cs`
3. `src/winland-keys/KeyboardHook.cs`
4. `src/winland-keys/AppLauncher.cs` and `HotkeyConfig.cs`
5. `src/Winland.Common/Ipc.cs`
6. `src/winlandctl/Program.cs`
7. `src/winland/DispatchServer.cs` → `UiInvoker.cs` → `Dispatcher.cs`
8. `src/winland/WorkspaceManager.cs` → `WorkspaceManager.Tiling.cs`
9. `src/winland/WindowNavigator.cs`
10. `src/winland/EventServer.cs` → `src/winland-indicator/IndicatorApp.cs` → `IndicatorWindow.cs`
11. `src/winland-keys/config.conf`
