# Winland Technical Requirements

## 1. Platform and Runtime

- Language/runtime: C# on `.NET 10`.
- Target frameworks:
  - `winland-keys`, `winland`, `winland-indicator`: `net10.0-windows` (WinForms
    `ApplicationContext`; `NotifyIcon` for the tray, layered `Form`s for the indicator).
  - `winlandctl`: `net10.0` (console).
  - `Winland.Common`: `net10.0` shared library, referenced by all of them.
- OS dependency: Windows (Win32 APIs via P/Invoke).
- Process model: **one app the user starts, `winland`**, which starts and ends two companion
  executables — a hotkey daemon (`winland-keys`) and an on-screen indicator (`winland-indicator`) —
  plus a control CLI (`winlandctl`). `winland` and `winland-keys` run elevated; the indicator runs
  unelevated. See `architecture-overview.md` §1.2.

## 2. Build and Packaging Requirements

- Output types: `WinExe` for `winland` and both companions, `Exe` for `winlandctl`.
- `winland` and `winland-keys` have an `app.manifest` requesting administrator elevation.
  `winland-indicator` deliberately has **none** (asInvoker), and sets
  `ApplicationHighDpiMode = PerMonitorV2` (so does `winland`).
- `winland-keys` copies to its output (`PreserveNewest`): `config.conf`, `launch-or-focus.ps1`,
  `launch-web.ps1`, `launch-or-focus-web.ps1`.
- **`winland-keys` must place `winlandctl.exe` (plus its `.dll`, `.runtimeconfig.json`,
  `.deps.json`) next to `winland-keys.exe`** — the keys daemon resolves `winlandctl` as a sibling
  exe. This is done by a build target (`CopyWinlandctl`) in `winland-keys.csproj`; without it, every
  `winlandctl …` bind (workspaces, focus, close, release) silently no-ops.
- **`winland`'s build must place both companions next to `winland.exe`** — `Companions` starts them
  from its own folder. The companions are `ProjectReference`s with `ReferenceOutputAssembly=false`
  (built first, no code dependency); the `CopyCompanions` target copies each one's whole output there
  (so `winlandctl`, `config.conf` and the helper scripts come along), leaving out files a run left
  behind (`winland-hooklog.txt*`, `winland-indicator-dock.txt`). Skipped for RID builds:
  `publish-dist.ps1` publishes each project into `dist/` itself.
- Scripts:
  - `install-autostart.ps1` (tracked in `packaging/`, copied into the assembled `dist/`) — register
    one logon Scheduled Task, `Winland`, running `winland.exe` at `-RunLevel Highest`; removes the
    per-process tasks earlier versions registered.
  - `publish-dist.ps1` (repo root) — assemble a self-contained `dist/`. It first stops `winland`,
    `winland-keys`, `winland-indicator` and `winland-env` (Winland's former name).
- There is no launcher script: from source, build `src/winland` and start the `winland.exe` in its
  output folder — it elevates through its manifest and starts its companions itself.

## 3. Core Technical Architecture

- **`winland-keys`** — `Program.Main` (single-instance mutex) runs `KeysApp`, which composes
  `KeyboardHook`, `Config` + `HotkeyConfig`, `AppLauncher` + `UnelevatedLauncher` (from
  `Winland.Common`), and
  `WindowsHotkeyRestorer`. It owns the only keyboard hook and has no workspace/focus logic. Binds
  are parsed before the hook is installed (the hook thread resolves combos from the moment it is live).
- **`winland`** — `Program.Main` runs `WinlandApp`, which composes `WorkspaceManager`,
  `WindowNavigator` (static), `Dispatcher`, `UiInvoker`, `DispatchServer`, `EventServer`, and
  `TrayIcon`. It has no keyboard hook; all actions arrive on the control pipe. Both pipes are created
  with the shared ACL from `PipeAcl.ForCurrentUser()`.
- **`winlandctl`** — a one-shot console client that sends one command over the pipe.
- **`winland-indicator`** — `Program.Main` (single-instance mutex `Local\winland-indicator`) runs
  `IndicatorApp`, which composes `EventClient`, `WinlandClient`, `UiPoster`, and one `IndicatorWindow`
  per monitor. It subscribes to the event pipe and writes exactly one verb to the control pipe:
  `move-workspace`, the moment a dragged circle enters another monitor.
- **`Winland.Common`** — `Ipc` (both pipe names + `OK`/`ERR` constants), `WinlandClient` (best-effort
  in-process control-pipe client, caller-chosen timeouts; used by `winland-keys` and
  `winland-indicator`) and `Log` (append-only logger).

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
  - `Win+0..9` / `Win+NumPad0..9` → `winlandctl workspace <n>`; `Win+Alt+0..9` → 10..19
  - `Win+Shift+W` → `winlandctl workspace-release`
  - `Win+Left/Up/Right/Down` → `winlandctl focus <dir>`
  - `Win+W` → `winlandctl close`
- The window-management behavior therefore lives in `winland`, reached via `winlandctl`.

### 4.5 Bind Matching
- At key-down with Win held, build the modifier bitmask from physical Shift/Alt/Ctrl state and match
  the first bind (in file order) with the same `Vk` and `Modifiers`. Config is read once at startup;
  restarting the daemon applies changes (there is no reload).

## 5. IPC Requirements

`winland` serves two independent local named pipes: a request/response **command** channel and a
broadcast-only **event** channel. Commands never travel on the event pipe and state is never returned
on the command pipe.

### 5.1 Transport
- A local named pipe, `\\.\pipe\winland` (`Ipc.PipeName`). `winland` is the server; `winlandctl` is
  the general client, and `winland-keys` / `winland-indicator` send their own requests through
  `WinlandClient`.
- Line-based protocol: the client writes one command line; the server replies with a single line —
  `OK` (`Ipc.Ok`) on success, or `ERR <message>` (`Ipc.ErrPrefix`) on failure.

### 5.2 Server (`winland`)
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
- `workspace <n>` / `movetoworkspace <n>` (n >= 0; the scratchpad id is rejected),
  `move-workspace <n> <device>` (same rule for n; `ERR` when no monitor with that device name is
  attached), `link-here`,
  `unlink`, `toggle-tile`, `toggle-fullscreen`, `scratchpad`, `workspace-release`, `focus <left|right|up|down>`,
  `move-window <left|right>`, `focus-app <process>`,
  `focus-web <url>`, `close`,
  `place-next-window` (the next new app window opens on the monitor under the mouse);
  unknown verbs and invalid arguments return `ERR …`.

### 5.4 Client (`winlandctl`)
- Joins args into one command line, connects with a 2s timeout, writes the line, reads one reply
  (10s timeout, so a wedged Winland cannot strand client processes).
- Exit codes: `0` OK · `1` env replied `ERR`/no reply · `2` bad usage · `3` could not reach env.

### 5.5 Event Channel (`Ipc.EventsPipeName`)
- Transport: a local named pipe, `\\.\pipe\winland-events`, `PipeDirection.Out`, byte mode,
  asynchronous, several server instances (8), created with the same user ACL so unelevated
  subscribers can attach. `winland` is the server; `winland-indicator` is the client.
- Message grammar: one line per snapshot — space-separated `<device>=<shown>:<homed>` entries, ordered
  left-to-right, e.g. `\\?\DISPLAY#DELA1C4#…#{…}=4:1,4 \\?\DISPLAY#DELA1C5#…#{…}=-1:`. The device is
  the stable monitor id from `MonitorIdentity` (contains no space or `=`); `shown` is the raw int: `-1` (`Ipc.NoWorkspace`) = the monitor shows no workspace,
  `int.MaxValue` = the scratchpad; `homed` is the ascending, comma-separated list of workspaces homed
  on that monitor (possibly empty). Written and read only by `Winland.Common.MonitorWorkspaces`.
  Rendering is the client's job.
- Delivery contract: the current snapshot is written immediately on connect, then a new snapshot line
  after every workspace change. Only the **latest** snapshot is ever sent — intermediate states a slow
  subscriber missed are not replayed. After ~30 s without a change the current line is re-sent, so a
  subscriber that disconnected while idle is detected and its pipe instance freed.
- `EventServer.Publish` is called on the UI thread and must never block: it stores the newest line and
  wakes per-subscriber writer threads. Dead or stalled subscribers are dropped silently and can never
  hold up Winland.
- Client contract (`EventClient`): a background thread reconnects forever (~2 s between attempts) when
  Winland is not running or restarts; the last known state stays on screen throughout, and
  only successful subscriptions are logged.

## 6. Workspace System Requirements (`winland`)

### 6.1 Workspace Domain Model
- No fixed workspace count — any whole number >= 0. The shipped config binds 0..19, but a
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
  monitor shows, survives monitor sleep), `_lastShown` (when each workspace was last shown, for the
  fallback of a monitor whose workspace is moved away), `_monitors` (live monitor state).
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
  workspace's home binding, set the monitor's current workspace to `NoWorkspace` (-1, none shown).

### 6.4a Moving a Workspace
- `MoveWorkspaceToMonitor(n, device)` re-homes `n` to the target monitor and shows it there: the
  target puts away what it showed (that workspace keeps its home) and lays out `n`'s strip; `n`'s
  visible floating windows on the source monitor are moved along, others stay; the source monitor
  then shows `PreviousWorkspace.For(...)` — the most recently shown workspace homed on it that no
  monitor shows, or `0`. When the monitors' DPIs differ, `n` is laid out a second time. Focus ends on
  `n`; the cursor is never moved. Membership is unchanged. Only the source and target monitors are touched.
  See `features/workspaces.md` §7a.

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
  `winland` is per-monitor-v2 DPI aware.
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

## 7. Window Focus Requirements (`winland`)

### 7.1 Candidate Filtering
A directional-focus candidate must be: visible; not the current window; not minimized; not the
desktop shell (`Progman`/`WorkerW`); have a non-empty title; not DWM-cloaked; not fully occluded by
Z-order predecessors; not minimized per its window placement.

### 7.2 Selection Algorithm
- Primary metric: forward distance in the chosen direction.
- Secondary metric: orthogonal overlap/range distance.
- Choose minimal `(primary, secondary)`.

### 7.3 Focus Execution
- Force the chosen candidate to the foreground (`Foreground.Activate`, shared with `focus-app` and the
  workspace engine); log the focused process name best-effort.

### 7.4 Close Foreground
- `close` posts `WM_CLOSE` to the foreground window.

### 7.5 The Mouse Follows Focus
- After `focus`, `focus-app`, `focus-web`, `move-window`, `link-here`, `toggle-tile`, `toggle-fullscreen` and `movetoworkspace`, if the
  foreground window or its visible frame changed and the cursor isn't over it, move the cursor to the
  centre of its frame (`CursorWarp`). "Over it" means inside its visible frame, not just in its
  invisible resize border (which covers the gap between tiles).
- When a window becomes a tile through Win+T (`link-here`/`toggle-tile`) or by being opened from a
  Winland shortcut, move the cursor to the centre of its frame, even if it was already over it.
- After showing a workspace (`workspace`, `scratchpad` on/off), move the cursor to the centre of the
  window focused there unless it is over it already; with nothing focused, to the centre of that
  workspace's monitor unless it is on that monitor already. `workspace-release` and `move-workspace`
  (a mouse gesture, usually mid-drag) never move the cursor.

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

## 9. Tray and UX Integration Requirements

- `winland` keeps a tray icon for its lifetime (primary monitor's taskbar): a static Winland
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
- The circle keeps the grip point that was pressed under the cursor's coordinate parallel to the docked
  edge, clamped so it stays fully inside the work area — a circle never leaves its monitor.

**Moving a workspace by dragging its circle** (rules in `WorkspaceDrag`, unit-tested)
- The pressed circle keeps the mouse (capture) for the whole drag; the circle the drag moves (the
  carrier) is the one of the monitor under the cursor (`rcMonitor`), docked by the rule above.
- When the cursor enters another monitor during a drag of a numbered workspace, that monitor's circle
  becomes the carrier and shows the workspace, and `move-workspace <n> <device>` is sent immediately.
  Re-entering a monitor moves it there again. The `–` and `S` circles carry nothing.
- While the drag is in effect, any non-carrier circle the latest snapshot credits with `n` is drawn
  fully transparent; circles that hide are redrawn before those that show, so `n` is never on two
  circles. Non-carrier circles sit at the dock they had when the drag began.
- Requests go out in order on one worker (`LatestRequestSender`, via `WinlandClient`: 2 s connect, 10 s
  reply); a waiting request is replaced by a newer one; failures are logged.
- Release (or lost capture) ends the gesture and saves every dock; the rules above stay in effect until
  a snapshot shows `n` on the carrier's monitor, or for at most 3 s.

**Persistence**
- Dock edge + fractional offset (0..1 along the edge) per monitor device name, in a plain-text file
  `winland-indicator-dock.txt` next to the exe (`# comment`, then `<device> <edge> <offset>` lines).
- Written when a drag is released (every circle's dock), read at startup;
  default `Top 0.8`. Monitors that are not currently
  attached keep their remembered entry. An unreadable or missing file is not an error.

**Windowing and elevation**
- Single instance (`Local\winland-indicator`), unelevated, no taskbar button and no Alt-Tab entry.
- Windows carry an **empty title** and `WS_EX_TOOLWINDOW`, which is exactly what `winland`'s
  managed-window filter rejects — so the circles are never minimized, moved, or
  treated as workspace members. `WS_EX_NOACTIVATE` keeps clicks and drags from stealing focus;
  `WS_EX_TOPMOST` keeps them visible.

**Displays and DPI**
- Per-monitor-v2 DPI awareness; circle size, margin and dock threshold are scaled by each monitor's
  own DPI (`GetDpiForMonitor`).
- Monitors are enumerated with `EnumDisplayMonitors` + `GetMonitorInfo` (MONITORINFOEX) so device
  names match `winland`'s, and `SystemEvents.DisplaySettingsChanged` re-syncs the window set
  (create / dispose / re-place) on the UI thread.
- A future right-click context menu is an explicit extension point: one more case in
  `IndicatorWindow.WndProc`.

## 10. Elevation and Windows Shell Hotkey Conflict Requirements

- `winland` and `winland-keys` run elevated (UIPI: a medium-integrity hook can't see input bound for
  elevated windows, and `winland` couldn't minimize/restore/focus elevated windows otherwise).
- Elevation stops there: apps launched from binds get the user's normal token
  (`UnelevatedLauncher`), the pipe ACL admits unelevated `winlandctl` callers, and
  `winland-indicator` runs unelevated (no manifest; `Companions` starts it with the shell's token).
- Windows' own Win-key shortcuts stay on. The hook swallows only the combos bound in `config.conf`
  (so the shell never sees those, e.g. Win+number taskbar launch); every other Win combo keeps its
  Windows meaning, and with Winland closed all of them do. Nothing is changed in the system, so there
  is nothing to restore on exit or after a crash.
- Migration: earlier versions set the `NoWinKeys` policy. On startup `winland-keys` removes it if set
  (`WindowsHotkeyRestorer`) and restarts Explorer once to apply — asking it to exit cleanly first,
  killing only as fallback, and relying on Windows' `AutoRestartShell` to bring it back unelevated.
  If the policy key is locked, it logs and continues.

## 11. Reliability and Error Handling Requirements

- Keep heavy work out of the hook callback; dispatch actions to the keys UI thread via a message
  window.
- Marshal pipe commands to Winland's UI thread before touching window state.
- Companions end with Winland, however it ends: each lives in its own kill-on-close job object held
  only by `winland` (`Companions`, `KillOnCloseJob`); one that can't join its job is ended at once.
  Their own children (apps launched by binds) break away from the jobs silently and never end with
  Winland.
- I/O, process-launch, and broken-pipe failures are caught and ignored where designed.
- Logging is best-effort only and must never block feature behavior.

## 12. Observability Requirements

- Diagnostic log file: `winland-hooklog.txt` next to each executable (`Winland.Common.Log`), each
  line stamped with time and process name (Winland and its companions share the one file next to
  `winland.exe`); rotated to a
  single `.old` generation past ~1 MB.
- `winland` logs workspace transitions and focus actions; `winland-keys`/`HotkeyConfig` logs
  startup status and ignored binds; `winland-indicator` logs its monitor count at startup, each
  successful (re)subscription to the event pipe — never its retries — and a `move-workspace` request
  that was not carried out.

## 13. Security and Operational Considerations

- PowerShell scripts run with `ExecutionPolicy Bypass`; the trust boundary is local config + local
  script files.
- Removing a leftover `NoWinKeys` policy may require permissions and can be constrained by org policy.
- Both pipes are local-machine only, ACL'd to the logged-on user's processes (elevated or not).
  Accepted trade: any same-user process can drive the window-management verbs — including against
  elevated windows, via the elevated service — and any same-user process can read which workspace
  each monitor is showing. The verbs are deliberately limited to window management and the event pipe
  is read-only; future hardening could add an optional strict (elevated-callers-only) mode.
- Apps launched from binds run unelevated (desktop-shell token); the fallback to an elevated launch
  only engages when no unelevated token is obtainable (no shell, UAC off).
