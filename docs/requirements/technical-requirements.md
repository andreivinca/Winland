# Winland Technical Requirements

## 1. Platform and Runtime

- Language/runtime: C# on `.NET 10`.
- Target framework: `net10.0-windows`.
- UI model: WinForms (`ApplicationContext` + `NotifyIcon`).
- OS dependency: Windows (Win32 APIs via P/Invoke).

## 2. Build and Packaging Requirements

From current project configuration:
- Output type: `WinExe`.
- Manifest file: `app.manifest`.
- `config.conf` must be copied to output directory (`PreserveNewest`).
- `AppShortcuts/launch-or-focus.ps1` and `AppShortcuts/launch-web.ps1` must be copied beside executable as:
- `AppShortcuts/launch-or-focus.ps1`, `AppShortcuts/launch-web.ps1`, and `AppShortcuts/show-desktop.ps1` must be copied beside executable as:
  - `launch-or-focus.ps1`
  - `launch-web.ps1`
  - `show-desktop.ps1`

## 3. Core Technical Architecture

- Entry point: `Program.Main()` initializes WinForms and runs `WinlandApp` context.
- Root coordinator: `WinlandApp` composes and orchestrates:
  - `KeyboardHook`
  - `WorkspaceManager`
  - `TrayIcon`
  - Config parsing (`Config` + `HotkeyConfig`)
  - Shortcut execution (`AppLauncher`)

## 4. Input and Hotkey Pipeline Requirements

### 4.1 Keyboard Capture
- Use global low-level keyboard hook (`WH_KEYBOARD_LL`) on dedicated STA thread.
- Hook thread must run message loop to maintain callback responsiveness.
- Win key down/up state tracked for combo detection.

### 4.2 Action Resolution Contract
- Resolver signature: `(vk, shiftDown, altDown, ctrlDown) -> actionId`.
- `actionId = 0` means unclaimed combo; event should pass through.
- Non-zero action IDs are swallowed and dispatched to UI thread.

### 4.3 Start Menu Suppression for Claimed Win Combos
- For claimed combos, inject dummy key events with marker (`dwExtraInfo`) to break “lone Win press” sequence and avoid Start menu opening.
- Injected events must be ignored by hook callback.

### 4.4 Built-in Action Mapping
- Built-ins in `WinlandApp`:
  - `Win+Left/Up/Right/Down`: directional focus
  - `Win+W`: close foreground window
  - `Win+1..9` / `Win+NumPad1..9`: workspace switch
  - `Win+Shift+W`: release current workspace

### 4.5 Precedence Rules
- Built-ins (window management/workspace) resolve before config binds.
- Config binds are resolved by exact `(vk + modifiers)` match from parsed config order.

## 5. Workspace System Requirements

### 5.1 Workspace Domain Model
- Workspace count fixed at 9.
- Membership source of truth: `Dictionary<IntPtr,int> _windowWorkspace`.
- Additional state:
  - `_lastActive` for focus restoration
  - `_workspaceHome` for monitor pinning
  - `_monitors` for monitor state (`Current`, work area, primary flag)

### 5.2 Monitor Discovery and Initial Assignment
- Enumerate monitors and order left-to-right.
- At startup, initialize monitor current workspaces sequentially (`1..N`, bounded by 9).
- Enumerate windows and assign non-minimized managed windows to their monitor’s current workspace.

### 5.3 Workspace Switching Semantics
- `SwitchFocusedMonitorTo(k)` must:
  - Resolve workspace home monitor (`ResolveHome`).
  - If already current on home monitor: focus workspace.
  - Else: minimize outgoing windows on that monitor, show workspace windows on that monitor, set monitor current workspace, focus workspace.
- No side effects on other monitors.

### 5.4 Release Semantics
- `ReleaseCurrentWorkspace()` acts on active monitor (under cursor).
- Minimize visible windows on that monitor assigned to released workspace.
- Remove workspace home binding.
- Set monitor current workspace to `0` (none shown).

### 5.5 Event-Driven Membership Updates
- Install WinEvent hooks for foreground, minimize-end, move-size-end, destroy.
- If monitor current workspace >= 1 and event indicates active/restored/moved window, assign window to monitor current workspace and update `_lastActive`.
- Destroy events remove stale window handles.

### 5.6 Guarding Against Self-Induced Events
- During switch/release operations, enable temporary event guard window to ignore resulting async events and prevent state corruption.

## 6. Window Focus Requirements

### 6.1 Candidate Filtering
Directional focus candidate window must:
- Be visible.
- Not be current window.
- Not be minimized.
- Not be desktop shell (`Progman`, `WorkerW`).
- Have non-empty title.
- Not be DWM cloaked.
- Not be fully occluded by Z-order predecessors.
- Not have minimized show command in window placement.

### 6.2 Selection Algorithm
- Compute directional eligibility and distances relative to current window.
- Primary metric: forward distance in chosen direction.
- Secondary metric: overlap/range distance on orthogonal axis.
- Choose minimal `(primary, secondary)`.

### 6.3 Focus Execution
- Set foreground to chosen candidate.
- Log focused process name best-effort.

### 6.4 Close Foreground
- `Win+W` posts `WM_CLOSE` to foreground window.

## 7. Config and Shortcut Requirements

### 7.1 Config Reader
- File format: line-based `keyword = value`.
- Ignore blank lines and lines beginning with `#`.
- Split on first `=` only.
- Preserve multiple same keyword entries in order.
- Missing/unreadable config returns empty entry set.

### 7.2 Bind Grammar
- `bind = <MODS... KEY>, <action>`
- Allowed modifiers before key: `SUPER|WIN|META|MOD`, `SHIFT`, `ALT`, `CTRL|CONTROL`.
- `SUPER` required.
- Last combo token is key name.
- Action is raw string after comma.

### 7.3 Key Name Resolution
- Support A-Z, 0-9, function keys F1-F24, and named keys like RETURN/ENTER, arrows, etc. per parser map.

### 7.4 Action Execution Model
- Split action into `verb` and `args`.
- If `${verb}.ps1` exists beside executable:
  - Launch `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script> <args>`.
- Else:
  - Execute verb+args as command via shell execution.
- Failures are swallowed (non-fatal behavior).

### 7.5 Script Behavior Contracts (Current Defaults)
- `launch-or-focus.ps1`:
  - Finds running processes by match name and locates a visible top-level window candidate.
  - If candidate exists and foreground window is NOT from the same target process set, focus/restore candidate.
  - If candidate does not exist, or target process is already foreground, launch a new instance.
- `launch-web.ps1`:
  - Resolves Chrome/Chromium executable and launches `--app=<url>`.
- `show-desktop.ps1`:
  - Minimizes all eligible top-level windows to mimic `Win+D` show-desktop behavior.

## 8. Tray and UX Integration Requirements

- Tray icon must remain visible during app lifetime.
- Tray menu items: Status, Reload config, Open config, Exit.
- Icon must display workspace number for primary monitor and update on relevant workspace changes.
- Startup and config-reload events should present balloon notifications.

## 9. Windows Shell Hotkey Conflict Requirement

- App must disable native Windows Win-key hotkeys via `NoWinKeys` policy to avoid conflicts (notably Win+number taskbar launch behavior).
- On policy change, restart Explorer to apply immediately.
- If policy is blocked/unavailable, continue without crashing.
- Selected shell behaviors should be reintroduced through config/script binds where needed.
- Current default reintroduced behavior: `Win+D` mapped to `show-desktop` script to mimic desktop-show/minimize-all behavior.

## 10. Reliability and Error Handling Requirements

- Hotkey processing path must avoid heavy work inside hook callback.
- Actions must be dispatched to UI thread via message window.
- I/O and process-launch failures should be caught and ignored where currently designed.
- Logging is best-effort only and must never block feature behavior.

## 11. Observability Requirements

- Diagnostic log file: `winland-hooklog.txt` next to executable.
- Log should capture workspace transitions and config parse ignores where applicable.

## 12. Security and Operational Considerations

- PowerShell scripts are executed with `ExecutionPolicy Bypass`; trust boundary is local config + local script files.
- Registry modification for `NoWinKeys` may require permissions and can be constrained by organization policy.
- Future hardening should consider script allowlist/signing or optional strict mode.
