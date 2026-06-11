# Feature Deep Dive: App Shortcuts

## 1. Objective

Allow users to define custom Win-key shortcuts in `config.conf` that launch apps/commands or invoke shipped/custom PowerShell script verbs. This is the `winland-keys` daemon's only job: map each Super combo to a command and run it. The workspace/focus combos are themselves binds whose command is `winlandctl <verb>`.

## 2. Configuration Model

### 2.1 Config Entry Format
Shortcut lines use:

`bind = <MODS... KEY>, <action>`

Example from shipped config:
- `bind = SUPER RETURN, launch-or-focus wt.exe WindowsTerminal -LaunchArgs "-d E:\"`
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
    including dash-prefixed args; the script path is single-quoted (and any `'` doubled).
- Uses `UseShellExecute=false`, `CreateNoWindow=true`.

### 4.3 Sibling Exe Mode
Else if `<baseDir>/<verb>.exe` exists:
- Run it directly with `args` (`UseShellExecute=false`, `CreateNoWindow=true`).
- **This is how `winlandctl` is found.** As a child of the elevated keys daemon it inherits
  elevation, so it can reach `winland-env`'s pipe.

### 4.4 Command Mode
If neither a script nor a sibling exe matches:
- Execute the whole line directly with shell execution (`UseShellExecute=true`).

### 4.5 Failure Policy
All launch errors are swallowed to preserve daemon stability.

## 5. Bundled Script Verbs

## 5.1 `launch-or-focus.ps1`
Goal:
- Focus existing app window for target process name, or launch app if not found.
- If the target app is already focused, launch a new instance.

Inputs:
- `-App` (required)
- `-Match` (optional process-name matcher)
- `-LaunchArgs` (optional launch args)

Positional usage in Winland config is also supported because script parameters are ordered as `App`, `Match`, `LaunchArgs`.

Note: Avoid dash-prefixed positional `LaunchArgs` in config for this script (for example `-d ...`), because PowerShell can interpret them as script parameters when invoked this way.

Behavior summary:
- Enumerates running processes matching `Match`.
- Enumerates visible top-level windows for matching pids.
- Prefers non-minimized candidate.
- If a candidate is found and the foreground window is NOT one of the target pids, restores/focuses it with temporary foreground-lock timeout adjustment.
- If no candidate is found, or if the target app is already the foreground app, starts a new process instance.

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
- Enumerates top-level windows.
- Skips non-eligible windows (hidden, already minimized, owned/tool windows, shell desktop/taskbar classes, cloaked, untitled).
- Minimizes eligible windows via `ShowWindow(..., SW_MINIMIZE)`.

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
  `3` (and the action silently does nothing). Other binds work whenever `winland-keys` runs.

## 8. Extension Guidance

When adding new shortcut capabilities:
1. Preserve `bind` grammar backward compatibility.
2. Keep parser tolerant (ignore invalid lines, log reasons).
3. Keep execution failures non-fatal.
4. Consider validating/sanitizing arguments if introducing privileged operations.
