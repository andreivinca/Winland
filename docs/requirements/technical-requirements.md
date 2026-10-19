# Winland Technical Requirements

## 1. Platform and Runtime

- Language/runtime: C# on `.NET 10`.
- Target frameworks:
  - `winland-keys`, `winland-env`, `winland-indicator`: `net10.0-windows` (WinForms
    `ApplicationContext`; `NotifyIcon` for the tray, layered `Form`s for the indicator).
  - `winlandctl`: `net10.0` (console).
  - `Winland.Common`: `net10.0` shared library, referenced by all of them.
- OS dependency: Windows (Win32 APIs via P/Invoke).
- Process model: **four executables** — a hotkey daemon (`winland-keys`), an environment service
  (`winland-env`), a control CLI (`winlandctl`), and an on-screen indicator (`winland-indicator`).
  The first two run elevated; the indicator must run unelevated. See `architecture-overview.md`.

## 2. Build and Packaging Requirements

- Output types: `WinExe` for the two daemons and the indicator, `Exe` for `winlandctl`.
- Each daemon has an `app.manifest` requesting administrator elevation. `winland-indicator`
  deliberately has **none** (asInvoker), and sets `ApplicationHighDpiMode = PerMonitorV2`.
- `winland-keys` copies to its output (`PreserveNewest`): `config.conf`, `launch-or-focus.ps1`,
  `launch-web.ps1`, `launch-or-focus-web.ps1`, `show-desktop.ps1`.
- **`winland-keys` must place `winlandctl.exe` (plus its `.dll`, `.runtimeconfig.json`,
  `.deps.json`) next to `winland-keys.exe`** — the keys daemon resolves `winlandctl` as a sibling
  exe. This is done by a build target (`CopyWinlandctl`) in `winland-keys.csproj`; without it, every
  `winlandctl …` bind (workspaces, focus, close, release) silently no-ops.
- Start/packaging scripts (tracked in `packaging/`, copied into the assembled `dist/`):
  - `start-winland.ps1` / `start-winland.cmd` — start both daemons elevated (one UAC prompt), plus
    `winland-indicator.exe` **if present** (so folders packaged before it existed still work).
  - `install-autostart.ps1` — register logon Scheduled Tasks: the daemons at `-RunLevel Highest`,
    `Winland Indicator` at `-RunLevel Limited`.
  - `run.ps1` (repo root) — developer launcher: stop → build → start from source (all three).
  - All three scripts stop `winland-env`, `winland-keys` and `winland-indicator`.

## 3. Core Technical Architecture

- **`winland-keys`** — `Program.Main` (single-instance mutex) runs `KeysApp`, which composes
  `KeyboardHook`, `Config` + `HotkeyConfig`, `AppLauncher` + `UnelevatedLauncher`, and
  `WindowsHotkeyDisabler`. It owns the only keyboard hook and has no workspace/focus logic. Binds
  are parsed before the hook is installed (the hook thread resolves combos from the moment it is live).
- **`winland-env`** — `Program.Main` runs `EnvApp`, which composes `WorkspaceManager`,
  `WindowNavigator` (static), `Dispatcher`, `UiInvoker`, `DispatchServer`, `EventServer`, and
  `TrayIcon`. It has no keyboard hook; all actions arrive on the control pipe. Both pipes are created
  with the shared ACL from `PipeAcl.ForCurrentUser()`.
- **`winlandctl`** — a one-shot console client that sends one command over the pipe.
- **`winland-indicator`** — `Program.Main` (single-instance mutex `Local\winland-indicator`) runs
  `IndicatorApp`, which composes `EventClient`, `UiPoster`, and one `IndicatorWindow` per monitor. It
  is a pure subscriber: it never writes to either pipe.
- **`Winland.Common`** — `Ipc` (both pipe names + `OK`/`ERR` constants) and `Log` (append-only
  logger).

## 4. Input and Hotkey Pipeline Requirements (`winland-keys`)

### 4.1 Keyboard Capture
- Global low-level keyboard hook (`WH_KEYBOARD_LL`) on a dedicated STA thread.
- The hook thread runs a message loop to keep the callback within the low-level-hook timeout.
- The hook asks the OS on each key-down whether Win is physically held. The only key state it keeps
  is the physical key of the combo that last fired, so auto-repeat fires a bind once per press.
- Shift/Ctrl/Alt are additionally tracked per side from real (non-injected) events, because with
  NumLock on Windows brackets numpad keys with a synthetic Shift (scancodes `0x22A`/`0x236` for
  left/right) that makes `GetAsyncKeyState` misreport Shift during numpad combos.

### 4.2 Action Resolution Contract
- Resolver signature: `(vk, shiftDown, altDown, ctrlDown) -> actionId`.
- `actionId = 0` means unclaimed combo; the event passes through to the OS.
- Non-zero ids are swallowed and dispatched to the UI thread; `KeysApp` maps id → bind index and runs
  the bind via `AppLauncher`.

### 4.3 Start Menu Suppression for Claimed Win Combos
- For claimed combos, inject dummy `0xFF` key events with a marker (`dwExtraInfo`) to break the
  "lone Win press" sequence and avoid the Start menu opening. Injected events are ignored by the hook.

### 4.4 Combo → Command Mapping (no hardcoded actions)
- There are **no hardcoded built-in actions**. Every Super combo — including workspaces, arrows,
  close, and release — is an ordinary `bind` in `config.conf` whose command is `winlandctl <verb>`:
  - `Win+1..9` / `Win+NumPad1..9` → `winlandctl workspace <n>`
  - `Win+Shift+W` → `winlandctl workspace-release`
  - `Win+Left/Up/Right/Down` → `winlandctl focus <dir>`
  - `Win+W` → `winlandctl close`
- The window-management behavior therefore lives in `winland-env`, reached via `winlandctl`.

### 4.5 Bind Matching
- At key-down with Win held, build the modifier bitmask from physical Shift/Alt/Ctrl state and match
  the first bind (in file order) with the same `Vk` and `Modifiers`. Config is read once at startup;
  restarting the daemon applies changes (there is no reload).

## 5. IPC Requirements

`winland-env` serves two independent local named pipes: a request/response **command** channel and a
broadcast-only **event** channel. Commands never travel on the event pipe and state is never returned
on the command pipe.

### 5.1 Transport
- A local named pipe, `\\.\pipe\winland-env` (`Ipc.PipeName`). `winland-env` is the server,
  `winlandctl` the client.
- Line-based protocol: the client writes one command line; the server replies with a single line —
  `OK` (`Ipc.Ok`) on success, or `ERR <message>` (`Ipc.ErrPrefix`) on failure.

### 5.2 Server (`winland-env`)
- `DispatchServer` listens on a background thread; each connection carries exactly one command.
- The pipe carries an explicit ACL granting the logged-on user's processes access (elevated or not),
  so unelevated scripts/terminals can use `winlandctl`. Trade-off: any same-user process can drive
  the verbs — which is why the verbs are limited to window management.
- The command is marshalled to the UI thread by `UiInvoker` (hidden window + synchronous
  `SendMessage`) before `Dispatcher.Execute` runs it, because the operations touch window/WinEvent
  and foreground state.
- A malformed/broken connection must not kill the server, and a client that connects but never sends
  its line is dropped after a read timeout (it must not wedge the single-connection server).

### 5.3 Dispatcher verbs
- `workspace <n>` / `movetoworkspace <n>` (n >= 1; the scratchpad id is rejected), `link-here`,
  `unlink`, `toggle-float`, `scratchpad`, `workspace-release`, `focus <left|right|up|down>`,
  `move-window <left|right>`, `focus-app <process>`,
  `focus-web <url>`, `close`,
  `place-next-window` (the next new app window opens on the monitor under the mouse);
  unknown verbs and invalid arguments return `ERR …`.

### 5.4 Client (`winlandctl`)
- Joins args into one command line, connects with a 2s timeout, writes the line, reads one reply
  (10s timeout, so a wedged environment cannot strand client processes).
- Exit codes: `0` OK · `1` env replied `ERR`/no reply · `2` bad usage · `3` could not reach env.

### 5.5 Event Channel (`Ipc.EventsPipeName`)
- Transport: a local named pipe, `\\.\pipe\winland-env-events`, `PipeDirection.Out`, byte mode,
  asynchronous, several server instances (8), created with the same user ACL so unelevated
  subscribers can attach. `winland-env` is the server; `winland-indicator` is the client.
- Message grammar: one line per snapshot — space-separated `<device>=<workspace>` pairs, ordered
  left-to-right, e.g. `\\.\DISPLAY1=3 \\.\DISPLAY2=5`. The device is the stable GDI display name
  (contains no space); the workspace is the raw int: `0` = the monitor shows no workspace,
  `int.MaxValue` = the scratchpad. Rendering those as "–"/"S" is the client's job.
- Delivery contract: the current snapshot is written immediately on connect, then a new snapshot line
  after every workspace change. Only the **latest** snapshot is ever sent — intermediate states a slow
  subscriber missed are not replayed. After ~30 s without a change the current line is re-sent, so a
  subscriber that disconnected while idle is detected and its pipe instance freed.
- `EventServer.Publish` is called on the UI thread and must never block: it stores the newest line and
  wakes per-subscriber writer threads. Dead or stalled subscribers are dropped silently and can never
  hold up the environment.
- Client contract (`EventClient`): a background thread reconnects forever (~2 s between attempts) when
  the environment is not running or restarts; the last known state stays on screen throughout, and
  only successful subscriptions are logged.

## 6. Workspace System Requirements (`winland-env`)

### 6.1 Workspace Domain Model
- No fixed workspace count — any whole number >= 1. The shipped config binds 1..9 and 11..19, but a
  user can bind any key to any number (e.g. `bind = SUPER F1, winlandctl workspace 20`).
- `int.MaxValue` is reserved for the scratchpad (a roaming workspace with no home monitor, toggled
  by the `scratchpad` verb).
- Membership source of truth: `Dictionary<IntPtr,int> _windowWorkspace`. Membership changes only on
  explicit move (`movetoworkspace`), tile (`link-here`), unlink (`unlink`), opening a window from a
  Winland shortcut (`place-next-window`), a tile moved past the end of its strip onto another monitor's
  workspace (`move-window`), or window close (pruned lazily). A member is a tile, or floating
  (`_floating`) after the user dragged or resized it with the mouse.
- Additional state: `_lastActive` (focus restoration; the tile kept in view), `_tileOrder` /
  `_floating` / `_scrollFirst` / `_arranged` (each strip's order, floating members, scroll position and
  last layout),
  `_workspaceHome` (monitor pinning, keyed by stable GDI device name), `_shownByDevice` (what each
  monitor shows, survives monitor sleep), `_monitors` (live monitor state).
- Windows that are not members are never tiled or resized; workspace switches and release minimize
  them like everything else on the monitor.

### 6.2 Monitor Discovery and Initial Assignment
- Enumerate monitors and order left-to-right.
- At startup, initialize monitor current workspaces sequentially (leftmost = 1, next = 2, …).
- **No windows are assigned at startup** — apps open when Winland starts stay unlinked until claimed
  by a move or link.

### 6.3 Workspace Switching Semantics
- `SwitchFocusedMonitorTo(k)`:
  - Resolve workspace home monitor (`ResolveHome`; first entry pins to the monitor under the cursor).
  - Minimize every window on that monitor that isn't `k`'s (unlinked windows included) and, on a real
    switch, the outgoing workspace's windows floating on other monitors; set the monitor's current
    workspace; lay out `k`'s strip there scrolled to its focused tile and restore `k`'s floating windows
    in place (on any monitor); focus. On a re-press this re-asserts the workspace.
- No other side effects on other monitors.

### 6.4 Release Semantics
- `ReleaseCurrentWorkspace()` acts on the active monitor (under the cursor): minimize its visible
  windows and the released workspace's windows floating elsewhere (members stay assigned), remove the
  workspace's home binding, set the monitor's current workspace to `0` (none shown).

### 6.5 Window Events
- `EVENT_SYSTEM_FOREGROUND` remembers each workspace's last-focused tile and scrolls a strip to a tile
  focused by hand. `EVENT_OBJECT_DESTROY..HIDE`, `EVENT_OBJECT_CLOAKED..UNCLOAKED` and
  `EVENT_SYSTEM_MOVESIZEEND` on tiles re-lay out or float them (`features/tiling.md` §4), via a 50 ms
  UI-thread timer — never inside the hook callback. A hidden or cloaked tile takes no column.
- Shell panels (`Windows.UI.Core.CoreWindow`: Start, Search, notifications, touch keyboard) are never
  managed, so they can't be linked.
- **Membership is never mutated by window events**: interaction doesn't re-link, and a destroy event
  only triggers a layout (an asynchronous destroy can arrive with a recycled HWND, so its handle is
  never acted on) — closed windows are pruned lazily instead.

### 6.5a Tiling Layout
- Per workspace, one row of full-height columns across the work area with a gap of 8 px (96 DPI).
  Tiles share the width while each stays >= 560 px (96 DPI); beyond that columns keep the width of a
  full view and the row scrolls by whole tiles. Both values scale with the monitor's effective DPI;
  `winland-env` is per-monitor-v2 DPI aware.
- Tiles outside the view are minimized, never positioned outside their monitor's work area.
- Hung windows (`IsHungAppWindow`) are skipped by layout.

### 6.6 Guarding Against Self-Induced Events
- During switch/release, a temporary event guard (~1500ms) ignores the async events our own
  minimize/restore produce, to prevent state corruption.

### 6.7 Change Notification
- One private notify path (`NotifyWorkspaceChanges`) runs after every operation that can change what
  a monitor shows and raises `WorkspacesChanged` — `WorkspaceSnapshot` (each live monitor's device
  name + shown workspace, ordered left-to-right); `EventServer` subscribes. Monitors that are not
  enumerating (asleep) are absent from the snapshot.

## 7. Window Focus Requirements (`winland-env`)

### 7.1 Candidate Filtering
A directional-focus candidate must be: visible; not the current window; not minimized; not the
desktop shell (`Progman`/`WorkerW`); have a non-empty title; not DWM-cloaked; not fully occluded by
Z-order predecessors; not minimized per its window placement.

### 7.2 Selection Algorithm
- Primary metric: forward distance in the chosen direction.
- Secondary metric: orthogonal overlap/range distance.
- Choose minimal `(primary, secondary)`.

### 7.3 Focus Execution
- Force the chosen candidate to the foreground (foreground-lock-timeout suppression, as `focus-app`
  does); log the focused process name best-effort.

### 7.4 Close Foreground
- `close` posts `WM_CLOSE` to the foreground window.

### 7.5 The Mouse Follows Focus
- After `focus`, `focus-app`, `focus-web`, `move-window`, `link-here`, `toggle-float` and `movetoworkspace`, if the
  foreground window or its visible frame changed and the cursor isn't over it, move the cursor to the
  centre of its frame (`CursorWarp`).
- After showing a workspace (`workspace`, `scratchpad` on/off), move the cursor to the centre of the
  window focused there unless it is over it already; with nothing focused, to the centre of that
  workspace's monitor unless it is on that monitor already. `workspace-release` never moves the cursor.

## 8. Config and Shortcut Requirements (`winland-keys`)

### 8.1 Config Reader
- Line-based `keyword = value`; ignore blanks and `#` comments; split on the first `=`; preserve
  duplicate keywords in order; a missing/unreadable file yields an empty config.

### 8.2 Bind Grammar
- `bind = <MODS... KEY>, <command>`; modifiers `SUPER|WIN|META|MOD` (required), `SHIFT`, `ALT`,
  `CTRL|CONTROL`; last combo token is the key; the command is the raw string after the comma.

### 8.3 Key Name Resolution
- A–Z, 0–9, `F1`–`F24`, `NUMPAD0`–`NUMPAD9`, and named keys (RETURN/ENTER, arrows, SPACE, TAB, ESC,
  etc.) per the parser map.

### 8.4 Command Execution Model (`AppLauncher`)
- Split the command into `verb` + `args`. Resolve, in order:
  1. `<verb>.ps1` next to the exe → `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '<script>' <args>"` (so a `--` token can pass dash-prefixed args verbatim; double quotes in args are escaped).
  2. `<verb>.exe` sibling next to the exe → run directly (how `winlandctl` is found).
  3. Otherwise → run the whole line with shell semantics (App Paths, URIs).
- Scripts and shell commands run with the user's normal token via `UnelevatedLauncher`
  (`CreateProcessWithTokenW` on the desktop shell's token; shell semantics via an unelevated
  `cmd /c start`), falling back to an elevated start when that route is unavailable.
- Failures are logged and otherwise ignored (non-fatal).

### 8.5 Script Behavior Contracts (Current Defaults)
- `launch-or-focus.ps1` — `winlandctl focus-app <match>`; on any failure (no window, already
  foreground, env not running) launch a new instance.
- `launch-web.ps1` — resolve Chrome/Chromium and launch `--app=<url>` (always a new window).
- `launch-or-focus-web.ps1` — `winlandctl focus-web <url>`; on any failure (no window, already
  foreground, env not running) run `launch-web.ps1 <url>`.
- `show-desktop.ps1` — minimize all eligible top-level windows (mimics `Win+D`). Runs unelevated,
  so elevated apps' windows are left alone (UIPI).

## 9. Tray and UX Integration Requirements

- `winland-env` keeps a tray icon for its lifetime (primary monitor's taskbar): a static Winland
  badge; menu: Status, Exit. Workspace numbers are shown by the indicator circles, not the tray.
- `winland-keys` is headless: no tray icon, menu, or balloons; startup status is written to the log.
  Config changes are applied by restarting the daemon.

### 9.1 On-Screen Indicator (`winland-indicator`)

**Appearance**
- One window per monitor: a translucent white circle (~24 px at 96 DPI, ~25% alpha, antialiased rim)
  with a soft black Segoe UI Bold glyph — the workspace number, `S` for the scratchpad, `–` when
  the monitor shows no workspace or no snapshot has arrived yet. The glyph shrinks for 2- and 3+
  character text on the same curve as the tray icon.
- Rendered as a layered window (`WS_EX_LAYERED`) via `UpdateLayeredWindow` from a 32bpp ARGB bitmap,
  which also gives per-pixel hit-testing: only the circle itself takes the mouse.

**Docking and dragging**
- The circle is always docked to one edge of its monitor's **work area**, tucked slightly past it
  (~6 px at 96 DPI) on every side.
- Left-drag moves it **along the docked edge only**; motion perpendicular to that edge is ignored.
- During a drag, the edge of the work area nearest the cursor becomes the docked edge **only** if the
  cursor is within the dock threshold (~80 px at 96 DPI) of it; otherwise the current edge stands.
- The circle centres on the cursor's coordinate parallel to the docked edge, clamped so it stays fully
  inside the work area — so it can never be dragged off its monitor.

**Persistence**
- Dock edge + fractional offset (0..1 along the edge) per monitor device name, in a plain-text file
  `winland-indicator-dock.txt` next to the exe (`# comment`, then `<device> <edge> <offset>` lines).
- Written when a drag ends, read at startup; default `Top 0.8`. Monitors that are not currently
  attached keep their remembered entry. An unreadable or missing file is not an error.

**Windowing and elevation**
- Single instance (`Local\winland-indicator`), unelevated, no taskbar button and no Alt-Tab entry.
- Windows carry an **empty title** and `WS_EX_TOOLWINDOW`, which is exactly what `winland-env`'s
  managed-window filter and `show-desktop.ps1` reject — so the circles are never minimized, moved, or
  treated as workspace members. `WS_EX_NOACTIVATE` keeps clicks and drags from stealing focus;
  `WS_EX_TOPMOST` keeps them visible.

**Displays and DPI**
- Per-monitor-v2 DPI awareness; circle size, margin and dock threshold are scaled by each monitor's
  own DPI (`GetDpiForMonitor`).
- Monitors are enumerated with `EnumDisplayMonitors` + `GetMonitorInfo` (MONITORINFOEX) so device
  names match `winland-env`'s, and `SystemEvents.DisplaySettingsChanged` re-syncs the window set
  (create / dispose / re-place) on the UI thread.
- A future right-click context menu is an explicit extension point: one more case in
  `IndicatorWindow.WndProc`.

## 10. Elevation and Windows Shell Hotkey Conflict Requirements

- Both daemons run elevated (UIPI: a medium-integrity hook can't see input bound for elevated
  windows, and `winland-env` couldn't minimize/restore/focus elevated windows otherwise).
- Elevation stops at the daemons: apps launched from binds get the user's normal token
  (`UnelevatedLauncher`), the pipe ACL admits unelevated `winlandctl` callers, and
  `winland-indicator` runs unelevated by construction (no manifest, `-RunLevel Limited` at logon).
- `winland-keys` disables native Win-key hotkeys via the `NoWinKeys` policy (notably Win+number
  taskbar launch). On a policy change it restarts Explorer to apply immediately — asking it to exit
  cleanly first, killing only as fallback, and relying on Windows' `AutoRestartShell` to bring it
  back unelevated. If the policy key is locked, it continues without crashing.
- Selected shell behaviors are reintroduced through config binds (e.g. `Win+D` → `show-desktop`,
  `Win+E` → `explorer.exe`).

## 11. Reliability and Error Handling Requirements

- Keep heavy work out of the hook callback; dispatch actions to the keys UI thread via a message
  window.
- Marshal pipe commands to the env UI thread before touching window state.
- I/O, process-launch, and broken-pipe failures are caught and ignored where designed.
- Logging is best-effort only and must never block feature behavior.

## 12. Observability Requirements

- Diagnostic log file: `winland-hooklog.txt` next to each executable (`Winland.Common.Log`), each
  line stamped with time and process name (the daemons share the file when co-located); rotated to a
  single `.old` generation past ~1 MB.
- `winland-env` logs workspace transitions and focus actions; `winland-keys`/`HotkeyConfig` logs
  startup status and ignored binds; `winland-indicator` logs its monitor count at startup and each
  successful (re)subscription to the event pipe — never its retries.

## 13. Security and Operational Considerations

- PowerShell scripts run with `ExecutionPolicy Bypass`; the trust boundary is local config + local
  script files.
- Registry modification for `NoWinKeys` may require permissions and can be constrained by org policy.
- Both pipes are local-machine only, ACL'd to the logged-on user's processes (elevated or not).
  Accepted trade: any same-user process can drive the window-management verbs — including against
  elevated windows, via the elevated service — and any same-user process can read which workspace
  each monitor is showing. The verbs are deliberately limited to window management and the event pipe
  is read-only; future hardening could add an optional strict (elevated-callers-only) mode.
- Apps launched from binds run unelevated (desktop-shell token); the fallback to an elevated launch
  only engages when no unelevated token is obtainable (no shell, UAC off).
