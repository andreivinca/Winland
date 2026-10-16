# Winland Project Overview

## 1. What Winland Is

Winland brings keyboard-centric, tiling-WM-style desktop ergonomics to Windows 11. It is **four
cooperating tray/CLI/UI processes**, not a single app:

- **`winland-keys`** — the hotkey daemon. Owns one global keyboard hook and runs the command bound to
  each Super (Win) combo.
- **`winland-env`** — the environment service ("window manager"): per-monitor workspaces, directional
  window focus, and the tray presence. Has no keyboard hook; it is driven over a control pipe.
- **`winlandctl`** — a one-shot CLI that forwards a single command to `winland-env` over its pipe.
- **`winland-indicator`** — the on-screen workspace circles, one per monitor. Runs unelevated and only
  subscribes to `winland-env`'s broadcast event pipe.

Current core capabilities:
- Per-monitor numbered workspaces (unbounded; `1..9` and `11..19` bound by default), plus a roaming
  scratchpad (`Win+S`)
- Opt-in scrolling tiling: tiling a window (`Win+Space`) or moving it to a workspace (`Win+Shift+N`)
  makes it a tile in that workspace's row, which shares the monitor and scrolls once tiles no longer
  fit. Membership changes only on these explicit actions; every other window stays a normal window
  that Winland never tiles (it is still put away when its monitor switches workspace)
- Directional nearest-window focus (`Win+Arrows`), focus-or-launch by process, foreground close
  (`Win+W`), and show-desktop (`Win+D`)
- Configurable app shortcuts from text config (`config.conf`), launched with the user's normal
  (unelevated) token
- A per-monitor on-screen workspace indicator: a draggable, edge-docked translucent circle that
  updates live from `winland-env`

## 2. Primary Goals

- Reduce context-switch friction for keyboard-first users.
- Make multi-monitor workspace behavior deterministic.
- Keep customization simple through text configuration and script/CLI verbs.
- Keep the keyboard hook in a tiny, crash-isolated process, and expose the window manager as a
  scriptable control surface (`winlandctl`).
- Tell the user which workspace each monitor is on, without asking for privileges to do it.

## 3. Runtime Experience

- `winland-env` runs in the system tray; `winland-keys` is headless (no tray icon — it logs its
  startup status instead).
- `winland-keys` intercepts Super combos and runs each combo's configured command.
- For workspace/focus combos that command is `winlandctl <verb>`, which reaches `winland-env`.
- `winland-env`'s tray icon is a static Winland badge.
- `winland-indicator` shows each monitor's workspace ("S" for the scratchpad, a dash when the monitor
  shows no workspace) as a translucent circle on **every** monitor, docked to an edge of that
  monitor's work area and draggable along it.

## 4. The Super-key flow

```
Win+1  →  winland-keys (hook)  →  runs "winlandctl workspace 1"  →  winlandctl  →  pipe  →  winland-env
```

`winland-keys` resolves the combo to its bound command and runs it; nothing about workspaces lives in
the keys daemon. `winland-env` only ever acts on commands that arrive on its pipe.

## 5. Feature Summary

### 5.1 Workspaces
- `Win+1..9` switches/focuses a workspace (`winlandctl workspace N`); `Win+Alt+1..9` reaches 11..19.
- `Win+Shift+N` moves the focused window to workspace N as a tile (`winlandctl movetoworkspace N`);
  `Win+Space` tiles it into the current workspace (`winlandctl link-here`), `Win+Shift+Space` makes it
  a normal window again (`winlandctl unlink`). See `docs/features/tiling.md`.
- `Win+Left/Right` step through and scroll a tile row; `Win+Shift+Left/Right` move a tile along it
  (`winlandctl move-window left|right`).
- `Win+S` toggles the roaming scratchpad on the mouse monitor (`winlandctl scratchpad`).
- Workspaces are pinned to a home monitor after first use.
- `Win+Shift+W` releases the current workspace from its monitor (`winlandctl workspace-release`).

### 5.2 Window Focus
- `Win+Left/Up/Right/Down` focuses the nearest candidate window by direction
  (`winlandctl focus <dir>`).
- `winlandctl focus-app <process>` focuses an app's window by process name (used by the
  launch-or-focus binds).
- `Win+W` sends a close request to the foreground window (`winlandctl close`).
- `Win+D` minimizes every window (the `show-desktop.ps1` script).

### 5.3 On-Screen Workspace Indicator
- One translucent circle per monitor showing that monitor's workspace (number, "S" for the
  scratchpad, "–" for none/unknown), updated live from `winland-env`'s event pipe.
- Left-drag slides the circle **along** the work-area edge it is docked to; coming near another edge
  re-docks it there. It can never leave its monitor or take focus.
- The dock edge and position are remembered per monitor across restarts.
- See `docs/features/workspace-indicator.md`.

### 5.4 App Shortcuts
- User-defined `bind = ...` lines in `config.conf`.
- Supports modifiers and key names; `SUPER` is required.
- A bind's command resolves at run time: a `<verb>.ps1` script, a sibling `<verb>.exe` (this is how
  `winlandctl` is found), or a plain shell command.
- The workspace/focus/close combos are themselves ordinary binds (their command is `winlandctl …`) —
  not reserved built-ins — so they can be rebound or removed.

## 6. Tech Stack

- C# / .NET 10 (`net10.0-windows` for the WinForms processes; `net10.0` for the `winlandctl` CLI)
- WinForms for the tray UI, the layered indicator windows, and UI-thread message pumps
- Win32/DWM/GDI interop for hooks, monitor/window operations, focus behavior, and per-pixel-alpha
  rendering (`UpdateLayeredWindow`)
- Two local **named pipes**: `\\.\pipe\winland-env` (commands, `winlandctl` → `winland-env`) and
  `\\.\pipe\winland-env-events` (state broadcast, `winland-env` → `winland-indicator`)
- PowerShell scripts for extensible launcher actions

## 7. Configuration and Assets

Files packaged next to `winland-keys.exe`:
- `config.conf`
- `launch-or-focus.ps1`, `launch-web.ps1`, `launch-or-focus-web.ps1`, `show-desktop.ps1`
- `winlandctl.exe` (copied beside the keys daemon by its build, so binds can run it)

Config model:
- Global key-value lines (`keyword = value`)
- Shortcut bindings consumed from `bind` entries

## 8. Starting Winland

Both daemons must run, both elevated (UIPI + the elevated control pipe require it). The indicator is
optional and runs at the user's normal level.

- **Packaged:** double-click `dist/start-winland.cmd` (or run `start-winland.ps1`). One UAC prompt;
  it stops old instances and starts the daemons, plus the indicator if its exe is present. `-Stop`
  stops all three. Tracked source in `packaging/`.
- **From source:** `run.ps1` at the repo root (stop → build → start). `install-autostart.ps1`
  registers all three as logon Scheduled Tasks — the daemons elevated (no UAC at sign-in), the
  indicator at `-RunLevel Limited`.

## 9. Repository / Code Orientation

```
src/Winland.Common/   Ipc.cs, Log.cs                       shared pipe protocol + logging
src/winland-keys/     KeysApp.cs, KeyboardHook.cs,          hotkey daemon (the only keyboard hook)
                      Config.cs, HotkeyConfig.cs,
                      AppLauncher.cs, UnelevatedLauncher.cs,
                      WindowsHotkeyDisabler.cs
src/winland-env/      EnvApp.cs, DispatchServer.cs,         environment service (pipe-driven)
                      EventServer.cs, PipeAcl.cs,
                      UiInvoker.cs, Dispatcher.cs,
                      WorkspaceManager.cs, WindowNavigator.cs, TrayIcon.cs
src/winland-indicator/ IndicatorApp.cs, IndicatorWindow.cs, on-screen workspace circles (unelevated)
                      EventClient.cs, UiPoster.cs, DockState.cs
src/winlandctl/       Program.cs                            control CLI (one-shot)
```

## 10. Documentation Index

For detailed requirements and internals:
- `docs/requirements/business-requirements.md`
- `docs/requirements/technical-requirements.md`
- `docs/architecture-overview.md`
- `docs/features/workspaces.md`
- `docs/features/tiling.md`
- `docs/features/window-focus.md`
- `docs/features/app-shortcuts.md`
- `docs/features/workspace-indicator.md`
