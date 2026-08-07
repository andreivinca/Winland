# Feature Deep Dive: App Shortcuts

## 1. Objective

Allow users to define custom Win-key shortcuts in `config.conf` that launch apps/commands or invoke shipped/custom PowerShell script verbs. This is the `winland-keys` daemon's only job: map each Super combo to a command and run it. The workspace/focus combos are themselves binds whose command is `winlandctl <verb>`.

## 2. Configuration Model

### 2.1 Config Entry Format
Shortcut lines use:

`bind = <MODS... KEY>, <action>`

Example from shipped config:
- `bind = SUPER RETURN, launch-or-focus wt.exe WindowsTerminal -- -d E:\WinLand`
- `bind = SUPER SHIFT M, launch-or-focus spotify`
- `bind = SUPER SHIFT A, launch-web https://gemini.google.com`
- `bind = SUPER D, show-desktop`

### 2.2 Parser Responsibilities
- `Config` reads file and yields `(keyword, value)` entries.
- `HotkeyConfig.Parse` consumes only `bind` values.
- Invalid binds are ignored and logged.

## 3. Combo Grammar and Resolution

### 3.1 Modifier Rules
Supported modifier tokens before key:
- `SUPER|WIN|META|MOD` (required)
- `SHIFT`
- `ALT`
- `CTRL|CONTROL`

Last token is key name.

### 3.2 Key Name Mapping
`HotkeyConfig.KeyNameToVk` supports:
- Letters `A-Z`
- Digits `0-9`
- Named keys (ENTER, SPACE, TAB, ESC, arrows, etc.)
- `F1..F24`

### 3.3 Runtime Matching
At key-down with Win held:
1. Build modifier bitmask from physical Shift/Alt/Ctrl state.
2. Iterate parsed binds in file order.
3. Match exact `Vk` and `Modifiers`.
4. Dispatch corresponding action id.

## 4. Action Execution Semantics

Entry point: `AppLauncher.Run(BindAction)`.

### 4.1 Command Split
- Split action into leading `verb` and remaining `args`.
- Supports quoted executable path in first token.

### 4.2 Script Mode
If `<baseDir>/<verb>.ps1` exists:
- Launch:
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '<script>' <args>"`
  - `-Command` (not `-File`) is used so a `--` token in the bind passes the rest through verbatim,
    including dash-prefixed args; the script path is single-quoted (any `'` doubled) and double
    quotes in the args are escaped so they can't cut the `-Command` string short.
- The PowerShell process runs **unelevated** (see §4.5), windowless, with the exe folder as its
  working directory.

### 4.3 Sibling Exe Mode
Else if `<baseDir>/<verb>.exe` exists:
- Run it directly with `args` (`UseShellExecute=false`, `CreateNoWindow=true`).
- **This is how `winlandctl` is found.**

### 4.4 Command Mode
If neither a script nor a sibling exe matches:
- Run the whole line with shell semantics (App Paths lookup, URIs, documents) — unelevated, via
  `cmd /c start "" <command>` (see §4.5). cmd treats `&` as a separator, so quote bare URLs with
  query parameters in a bind.

### 4.5 Elevation of Launched Apps
The keys daemon is elevated, but the apps a bind starts must not be: scripts and shell commands are
launched with the user's normal token, borrowed from the desktop shell by `UnelevatedLauncher`
(`CreateProcessWithTokenW`). If that route is unavailable (Explorer not running, UAC disabled), the
launch falls back to a plain — elevated — start.

### 4.6 Failure Policy
All launch errors are swallowed to preserve daemon stability.

## 5. Bundled Script Verbs

The scripts run unelevated (§4.5). Window work that must reach elevated windows lives in
`winland-env`, reached through `winlandctl` (whose pipe accepts unelevated callers); script-local
window work (e.g. `show-desktop.ps1`) is subject to UIPI and leaves elevated apps' windows alone.

## 5.1 `launch-or-focus.ps1`
Goal:
- Focus an existing window of the target process, or launch the app if there is nothing to focus.
- If the target app is already focused, launch a new instance.

Inputs (positional): `App` (required), `Match` (optional process-name matcher, defaults to App's
file name without extension), then launch arguments. Everything after a `--` token is passed to the
app verbatim, so dash-prefixed args survive, e.g.
`launch-or-focus wt.exe WindowsTerminal -- -d E:\`.

Behavior summary:
- Runs `winlandctl focus-app <Match>`; `winland-env` focuses the app's frontmost non-minimized
  window (restoring a minimized one if that is all there is).
- Exit code 0 means a window was focused — done.
- Any failure (no window, app already foreground, `winland-env` not running) → `Start-Process` a new
  instance.

## 5.2 `launch-web.ps1`
Goal:
- Open URL as app-style browser window via Chrome/Chromium `--app=` mode.

Behavior summary:
- Resolves browser executable via App Paths registry keys, then known install locations, then `chrome` fallback.
- Launches browser with `--app=<url>`.

## 5.3 `show-desktop.ps1`
Goal:
- Mimic native `Win+D` behavior by minimizing all eligible top-level desktop windows.

Behavior summary:
- Enumerates top-level windows (Win32 via `Add-Type`).
- Skips non-eligible windows (hidden, already minimized, owned/tool windows, shell desktop/taskbar
  classes, cloaked, untitled).
- Minimizes eligible windows via `ShowWindow(..., SW_MINIMIZE)`.
- Runs unelevated, so windows of elevated apps stay up (UIPI).

## 6. No Built-in Actions / Bind Precedence

- There are no hardcoded built-in combos. Workspaces, arrows, close, and release are all ordinary
  binds whose command is `winlandctl <verb>`, so they can be rebound or removed like any other.
- On a key-down, the **first** bind in file order matching `(vk + modifiers)` wins. If two binds
  share a combo, the earlier line is used.

## 7. Operational Considerations

- Config is read at startup; restart the keys daemon to apply changes (the start scripts stop old
  instances first).
- Scripts and `winlandctl.exe` are expected next to `winland-keys.exe` (copied during build).
- Execution policy bypass increases flexibility but expands trust assumptions.
- Workspace/focus binds require `winland-env` to be running; otherwise `winlandctl` exits with code
  `3` (and the action silently does nothing). Other binds work whenever `winland-keys` runs —
  `launch-or-focus` then degrades to always launching.

## 8. Extension Guidance

When adding new shortcut capabilities:
1. Preserve `bind` grammar backward compatibility.
2. Keep parser tolerant (ignore invalid lines, log reasons).
3. Keep execution failures non-fatal.
4. Consider validating/sanitizing arguments if introducing privileged operations.
