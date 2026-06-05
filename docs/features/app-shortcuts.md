# Feature Deep Dive: App Shortcuts

## 1. Objective

Allow users to define custom Win-key shortcuts in `config.conf` that launch apps/commands or invoke shipped/custom PowerShell script verbs.

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
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "<script>" <args>`
- Uses `UseShellExecute=false`, `CreateNoWindow=true`.

### 4.3 Command Mode
If no matching script file exists:
- Execute `verb` + `args` directly with shell execution.

### 4.4 Failure Policy
All launch errors are swallowed to preserve core app stability.

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

## 6. Interaction with Built-in Actions

- Built-in actions are resolved before config binds.
- If user defines bind that conflicts with built-in combo, built-in wins.

## 7. Operational Considerations

- Config reload is manual via tray menu (`Reload config`).
- Scripts are expected next to executable (copied from project during build).
- Execution policy bypass increases flexibility but expands trust assumptions.

## 8. Extension Guidance

When adding new shortcut capabilities:
1. Preserve `bind` grammar backward compatibility.
2. Keep parser tolerant (ignore invalid lines, log reasons).
3. Keep execution failures non-fatal.
4. Consider validating/sanitizing arguments if introducing privileged operations.
