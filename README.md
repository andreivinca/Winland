# Winland

A small .NET 10 WinForms tray application that brings Omarchy / Hyprland-style **Super-key (Win)
keyboard workflow** to Windows 11: per-monitor workspaces, directional window focus, and
config-driven app shortcuts.

Winland installs a single global low-level keyboard hook that owns the Win key. While Win is held it
swallows the combos it handles (so Windows never performs its own action) and routes them to the
matching feature; everything else passes through untouched. The hook is process-local — when Winland
isn't running, all keys behave normally.

---

## Features

### 1. Workspaces (`Win+1` … `Win+9`)

Nine workspaces, switched with `Win+1`..`Win+9` (top-row digits or numpad). The model is
**per-monitor and home-pinned**:

- Each workspace is pinned to a **home monitor** and never moves between monitors on its own.
- On launch, monitors are ordered left-to-right and the first workspaces are homed across them
  (leftmost monitor = workspace 1, and so on). Only currently visible (non-minimized) windows are
  assigned.
- The **active monitor is the one under the mouse cursor**. The first time you press `Win+N` for an
  unhomed workspace, it opens that (empty) workspace on the cursor's monitor and pins it there.
  Afterwards `Win+N` always returns to that monitor.
- Switching a workspace **minimizes** whatever its home monitor was showing and **restores** that
  workspace's windows. No other monitor is touched.
- Activating, maximizing, restoring, or dragging a window pulls it into its monitor's current
  workspace (and unlinks it from any previous one), so windows follow what you actually do.

**Moving a workspace to another monitor** — release it, then re-summon it:

- `Win+Shift+W` **releases** the current workspace: its windows are minimized (still assigned to it)
  and the workspace is unpinned from its monitor.
- Move the mouse to another monitor and press `Win+N` — because the workspace is now unhomed, it
  re-pins to the cursor's monitor and restores its windows there.

### 2. Window navigator (`Win+Arrows`, `Win+W`)

- `Win+←` / `Win+↑` / `Win+→` / `Win+↓` — move focus to the **nearest visible window** in that
  direction. Selection is geometric (nearest edge in the primary axis, best overlap in the other),
  and it skips minimized, cloaked, off-screen, and fully-occluded windows.
- `Win+W` — close the foreground window.

### 3. App shortcuts (config-driven)

Application launch/focus shortcuts are defined in `config.conf` (see below), not hardcoded. Each
`bind` line maps a `Super`-based combo to an action. Built-in window management combos
(workspaces / arrows / close / release) stay reserved and are not configurable.

---

## Configuration (`config.conf`)

`config.conf` ships next to the executable and is read on startup (and via the tray's **Reload
config**). It uses a simple, hand-parsed Hyprland/Omarchy-style grammar — one `keyword = value` per
line, `#` for comments.

A global reader (`Config`) tokenizes the file into `keyword = value` entries; each feature interprets
the keywords it cares about. Today the only keyword is `bind`, but the file is designed to grow more
configuration (monitor rules, settings, etc.) without changing the reader.

### Bind syntax

```
bind = <MODS… KEY>, <action>
```

- **Combo** (before the comma): space-separated tokens, the **last token is the key**.
  - Modifiers: `SUPER` (required — the hook only acts while Win is held), `SHIFT`, `ALT`, `CTRL`.
  - Keys: `A`–`Z`, `0`–`9`, `RETURN`/`ENTER`, arrows, `F1`–`F24`, `SPACE`, `TAB`, `ESC`, and a few more.
- **Action** (after the comma) is resolved at run time, with no special keywords:
  - If the first token names a **`<token>.ps1`** script next to the exe, that script runs with the
    rest of the line as arguments.
  - Otherwise the whole line is executed as a command.

### Shipped helper scripts

- `launch-or-focus.ps1 <process>` — focus an existing window of that process if one is open, else
  launch it (focus-if-open behavior).
- `launch-web.ps1 <url>` — open a URL as a chromeless desktop app window (Chrome/Chromium `--app=`).

Drop any `foo.ps1` next to the exe and reference it as `bind = … foo <args>` — no code change needed.

### Example

```conf
bind = SUPER RETURN, wt.exe -d E:\                          # run Windows Terminal
bind = SUPER SHIFT B, firefox                               # run Firefox
bind = SUPER SHIFT N, code                                  # run VS Code
bind = SUPER SHIFT A, launch-web https://gemini.google.com  # open Gemini as an app window
bind = SUPER SHIFT M, launch-or-focus spotify               # focus Spotify, or launch it
bind = SUPER SHIFT T, launch-or-focus taskmgr               # focus Task Manager, or launch it
```

---

## System tray icon

Winland lives in the system tray. Its icon is **drawn at runtime** — a white circle with the active
workspace number in black — and updates whenever you switch workspaces. Right-click for the menu
(Status / Reload config / Open config / Exit); double-click for status.

**Why only the main monitor's taskbar?** The tray (notification area) exists only on the primary
monitor's taskbar in the default Windows 11 configuration, so the numbered icon naturally shows there.
This is intentional: it keeps Winland small and dependency-free. **Windows 11 does not allow apps to
draw custom widgets on the taskbar** (the old Win10-era deskband/toolbar API is gone), so a true
per-monitor on-taskbar workspace indicator would require display hacks — overlay windows, injected UI,
or a shell replacement. Winland avoids all of that and uses the one place the OS already gives every
tray app: a single icon on the primary taskbar.

---

## ⚠️ Important: Winland disables some default Windows behavior

For its shortcuts to work reliably, Winland needs two things from the OS, and it sets them up itself:

1. **Runs elevated (as Administrator).** A normal (medium integrity) keyboard hook is blocked by
   Windows from intercepting input destined for an elevated window (UIPI). If you run, say, Visual
   Studio as Administrator, `Win+3` would otherwise fall through to it as a literal "3". Running
   Winland elevated lets the hook see those keystrokes. (The app manifest requests administrator
   rights; you'll see a UAC prompt at launch.)

2. **Turns off Windows' own `Win`+`<key>` shortcuts (the `NoWinKeys` policy).** Otherwise the shell
   still acts on combos Winland owns — most visibly `Win`+`<number>`, which Windows uses to launch
   pinned taskbar apps. Winland sets the `NoWinKeys` policy
   (`HKCU\…\Policies\Explorer\NoWinKeys = 1`) on startup so the shell no longer claims those combos.
   If it had to change the value, it **restarts Explorer once** so the change takes effect
   immediately; otherwise it's a no-op.

This means built-in Windows Win-key shortcuts (Win+number, etc.) are intentionally suppressed while
the policy is set. The behavior is reversible — clear the `NoWinKeys` value (set it to `0` or delete
it) and restart Explorer to restore Windows' defaults.

---

## Build & run

```powershell
dotnet build Winland/Winland.csproj -c Debug
```

The executable is produced at `Winland/bin/Debug/net10.0-windows/Winland.exe`. It will prompt for
administrator rights on launch.

To start it elevated automatically at logon without a UAC prompt, create a Task Scheduler task that
runs the exe **with highest privileges** at sign-in.

---

## Project layout

```
Program.cs                                        entry point
WinlandApp.cs                                     root handler: wires features + owns the action dispatch table
KeyboardHook.cs                                   shared keyboard hook ("hotkey register")
Config.cs                                         global config reader/parser
WindowsHotkeyDisabler/  WindowsHotkeyDisabler.cs  turn off Windows' own Win+<key> hotkeys (NoWinKeys)
Workspaces/             WorkspaceManager.cs        per-monitor workspaces
WindowFocus/            WindowNavigator.cs         Win+arrows focus + Win+W close
AppShortcuts/           HotkeyConfig.cs            interpret "bind" entries
                        AppLauncher.cs             run a bind (script or command)
                        launch-or-focus.ps1
                        launch-web.ps1
UI/                     TrayIcon.cs                tray presence (numbered icon, menu, balloons, status)
config.conf                                       user-editable shortcuts (copied next to the exe)
app.manifest                                      requests administrator elevation
```
