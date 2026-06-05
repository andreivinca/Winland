# Winland Technical Requirements

## 1. Platform and Runtime

- Language/runtime: C# on `.NET 10`.
- Target frameworks:
  - `winland-keys`, `winland-env`: `net10.0-windows` (WinForms `ApplicationContext` + `NotifyIcon`).
  - `winlandctl`: `net10.0` (console).
  - `Winland.Common`: `net10.0` shared library, referenced by all three.
- OS dependency: Windows (Win32 APIs via P/Invoke).
- Process model: **three executables** — a hotkey daemon (`winland-keys`), an environment service
  (`winland-env`), and a control CLI (`winlandctl`). See `architecture-overview.md`.

## 2. Build and Packaging Requirements

- Output types: `WinExe` for the two daemons, `Exe` for `winlandctl`.
- Each daemon has an `app.manifest` requesting administrator elevation.
- `winland-keys` copies to its output (`PreserveNewest`): `config.conf`, `launch-or-focus.ps1`,
  `launch-web.ps1`, `show-desktop.ps1`.
- **`winland-keys` must place `winlandctl.exe` (plus its `.dll`, `.runtimeconfig.json`,
  `.deps.json`) next to `winland-keys.exe`** — the keys daemon resolves `winlandctl` as a sibling
  exe. This is done by a build target (`CopyWinlandctl`) in `winland-keys.csproj`; without it, every
  `winlandctl …` bind (workspaces, focus, close, release) silently no-ops.
- Start/packaging scripts (tracked in `packaging/`, copied into the assembled `dist/`):
  - `start-winland.ps1` / `start-winland.cmd` — start both daemons elevated (one UAC prompt).
  - `install-autostart.ps1` — register both daemons as logon Scheduled Tasks (elevated).
  - `run.ps1` (repo root) — developer launcher: stop → build → start from source.

## 3. Core Technical Architecture

- **`winland-keys`** — `Program.Main` (single-instance mutex) runs `KeysApp`, which composes
  `KeyboardHook`, `Config` + `HotkeyConfig`, `AppLauncher`, `WindowsHotkeyDisabler`, and `KeysTray`.
  It owns the only keyboard hook and has no workspace/focus logic.
- **`winland-env`** — `Program.Main` runs `EnvApp`, which composes `WorkspaceManager`,
  `WindowNavigator` (static), `Dispatcher`, `UiInvoker`, `DispatchServer`, and `TrayIcon`. It has no
  keyboard hook; all actions arrive on the control pipe.
- **`winlandctl`** — a one-shot console client that sends one command over the pipe.
- **`Winland.Common`** — `Ipc` (pipe name + `OK`/`ERR` constants) and `Log` (append-only logger).

## 4. Input and Hotkey Pipeline Requirements (`winland-keys`)

### 4.1 Keyboard Capture
- Global low-level keyboard hook (`WH_KEYBOARD_LL`) on a dedicated STA thread.
- The hook thread runs a message loop to keep the callback within the low-level-hook timeout.
- Win key down/up state tracked for combo detection.

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
  the first bind (in file order) with the same `Vk` and `Modifiers`. Reloading config reparses binds.

## 5. Control Channel Requirements (IPC)

### 5.1 Transport
- A local named pipe, `\\.\pipe\winland-env` (`Ipc.PipeName`). `winland-env` is the server,
  `winlandctl` the client.
- Line-based protocol: the client writes one command line; the server replies with a single line —
  `OK` (`Ipc.Ok`) on success, or `ERR <message>` (`Ipc.ErrPrefix`) on failure.

### 5.2 Server (`winland-env`)
- `DispatchServer` listens on a background thread; each connection carries exactly one command.
- The command is marshalled to the UI thread by `UiInvoker` (hidden window + synchronous
  `SendMessage`) before `Dispatcher.Execute` runs it, because the operations touch window/WinEvent
  and foreground state.
- A malformed/broken connection must not kill the server.

### 5.3 Dispatcher verbs
- `workspace <1..9>`, `workspace-release`, `focus <left|right|up|down>`, `close`; unknown verbs and
  out-of-range arguments return `ERR …`.

### 5.4 Client (`winlandctl`)
- Joins args into one command line, connects with a 2s timeout, writes the line, reads one reply.
- Exit codes: `0` OK · `1` env replied `ERR`/no reply · `2` bad usage · `3` could not reach env.

## 6. Workspace System Requirements (`winland-env`)

### 6.1 Workspace Domain Model
- Workspace count fixed at 9.
- Membership source of truth: `Dictionary<IntPtr,int> _windowWorkspace`.
- Additional state: `_lastActive` (focus restoration), `_workspaceHome` (monitor pinning),
  `_monitors` (monitor state: `Current`, work area, primary flag).

### 6.2 Monitor Discovery and Initial Assignment
- Enumerate monitors and order left-to-right.
- At startup, initialize monitor current workspaces sequentially (`1..N`, bounded by 9).
- Enumerate windows and assign non-minimized managed windows to their monitor's current workspace.

### 6.3 Workspace Switching Semantics
- `SwitchFocusedMonitorTo(k)`:
  - Resolve workspace home monitor (`ResolveHome`; first entry pins to the monitor under the cursor).
  - If already current on the home monitor: focus the workspace.
  - Else: minimize outgoing windows on that monitor, show workspace `k`'s windows there, set the
    monitor's current workspace, focus `k`.
- No side effects on other monitors.

### 6.4 Release Semantics
- `ReleaseCurrentWorkspace()` acts on the active monitor (under the cursor): minimize its visible
  windows (still assigned to the released workspace), remove the workspace's home binding, set the
  monitor's current workspace to `0` (none shown).

### 6.5 Event-Driven Membership Updates
- WinEvent hooks for foreground, minimize-end, move-size-end, destroy.
- If a monitor's current workspace ≥ 1 and a window is activated/restored/moved, assign it to that
  workspace and update `_lastActive`. Destroy events remove stale handles.

### 6.6 Guarding Against Self-Induced Events
- During switch/release, a temporary event guard (~1500ms) ignores the async events our own
  minimize/restore produce, to prevent state corruption.

### 6.7 Primary Monitor Integration
- `PrimaryWorkspaceChanged` emits the workspace shown on the primary monitor; `TrayIcon` subscribes.

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
- Set foreground to the chosen candidate; log the focused process name best-effort.

### 7.4 Close Foreground
- `close` posts `WM_CLOSE` to the foreground window.

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
  1. `<verb>.ps1` next to the exe → `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '<script>' <args>"` (so a `--` token can pass dash-prefixed args verbatim).
  2. `<verb>.exe` sibling next to the exe → run directly (how `winlandctl` is found).
  3. Otherwise → run the whole line as a shell command.
- Failures are swallowed (non-fatal).

### 8.5 Script Behavior Contracts (Current Defaults)
- `launch-or-focus.ps1` — focus an existing window of the target process when it isn't already
  foreground; otherwise launch a new instance.
- `launch-web.ps1` — resolve Chrome/Chromium and launch `--app=<url>`.
- `show-desktop.ps1` — minimize all eligible top-level windows (mimics `Win+D`).

## 9. Tray and UX Integration Requirements

- Each daemon keeps a tray icon for its lifetime (both on the primary monitor's taskbar).
- `winland-env` icon displays the primary monitor's workspace number and updates on relevant changes;
  menu: Status, Exit.
- `winland-keys` menu: Status, Reload config, Open config, Exit.
- Startup and config-reload events present balloon notifications.

## 10. Elevation and Windows Shell Hotkey Conflict Requirements

- Both daemons run elevated (UIPI: a medium-integrity hook can't see input bound for elevated
  windows; and the elevated `winland-env` pipe must be reachable by the `winlandctl` the elevated
  keys daemon spawns).
- `winland-keys` disables native Win-key hotkeys via the `NoWinKeys` policy (notably Win+number
  taskbar launch). On a policy change it restarts Explorer to apply immediately; if blocked, it
  continues without crashing.
- Selected shell behaviors are reintroduced through config binds (e.g. `Win+D` → `show-desktop`,
  `Win+E` → `explorer.exe`).

## 11. Reliability and Error Handling Requirements

- Keep heavy work out of the hook callback; dispatch actions to the keys UI thread via a message
  window.
- Marshal pipe commands to the env UI thread before touching window state.
- I/O, process-launch, and broken-pipe failures are caught and ignored where designed.
- Logging is best-effort only and must never block feature behavior.

## 12. Observability Requirements

- Diagnostic log file: `winland-hooklog.txt` next to each executable (`Winland.Common.Log`).
- `winland-env` logs workspace transitions; `winland-keys`/`HotkeyConfig` logs ignored binds.

## 13. Security and Operational Considerations

- PowerShell scripts run with `ExecutionPolicy Bypass`; the trust boundary is local config + local
  script files.
- Registry modification for `NoWinKeys` may require permissions and can be constrained by org policy.
- The control pipe is local-machine only; commands are accepted from any client that can open it
  (both daemons run as the same elevated user). Future hardening could add a script allowlist/signing
  or an optional strict mode.
