# Winland

Winland brings an Omarchy / Hyprland-style **Super-key (Win) keyboard workflow** to Windows 11:
per-monitor workspaces you switch with `Win`+a number, move windows between them, focus windows by
direction, and launch apps — all from the keyboard, all configurable.

It's a tiny .NET 10 tray utility (actually three small cooperating processes — see
[Architecture](#architecture)). When it isn't running, your keyboard behaves completely normally.

---

## Quick start

Winland runs from a self-contained folder (`dist/`) that holds the executables, the config file, and
the helper scripts.

1. **Start it:** double-click **`start-winland.cmd`** (or run
   `powershell -ExecutionPolicy Bypass -File .\start-winland.ps1`).
2. Accept the **single UAC prompt** — Winland must run elevated (explained
   [below](#why-it-needs-administrator)).
3. A small **workspace-number icon** appears in the system tray. You're running.

To stop it: `start-winland.ps1 -Stop` (or the `run.ps1 -Stop` dev script). To start it automatically
at every logon without a UAC prompt, see [Auto-start at logon](#auto-start-at-logon).

> Running from source instead? Use `.\run.ps1` (builds and launches). See [Building](#building).

---

## The idea in 30 seconds

- A **workspace** is a numbered group of windows (1, 2, 3, …). There's no hard limit on the number.
- Each workspace lives on a **home monitor** — the monitor it was first opened on. It never wanders
  to another monitor on its own.
- A monitor shows **one workspace at a time**. Switching a monitor to another workspace minimizes
  ("puts away") the windows it was showing and restores the new workspace's windows.
- A window belongs to a workspace **only when you say so** — when you **move** it (`Win+Shift+N`) or
  **link** it (`Win+Space`). Windows that are open when Winland starts belong to no workspace until
  you claim them, and just restoring or dragging a window does *not* silently change which workspace
  it belongs to.
- There's also a **scratchpad** (`Win+S`): a workspace with no home monitor that pops up on whatever
  monitor your mouse is on, and disappears again on the next press.

---

## Keyboard shortcuts

These are the **defaults** — every one is just a line in `config.conf`, so you can change any of them
(see [Configuration](#configuration)).

### Switch workspaces

| Shortcut | What it does |
| --- | --- |
| `Win`+`1` … `Win`+`9` | Show workspace 1–9 on its home monitor (top-row digits **or** numpad). |
| `Win`+`Alt`+`1` … `9` | Show workspace 11–19. |

Pressing `Win`+`N` always **re-asserts** workspace N's layout on its monitor: it minimizes anything
there that doesn't belong to N, restores N's windows — **including ones you minimized** — and
**pulls back** any of N's windows you had dragged onto another monitor. Windows already visible stay
exactly where they are, so re-pressing the same workspace is a handy "tidy this monitor" button.

The **first** time you press `Win`+`N` for a workspace that has never been opened, it opens on the
monitor under your **mouse cursor** and is pinned there from then on.

### Move a window to another workspace

| Shortcut | What it does |
| --- | --- |
| `Win`+`Shift`+`1` … `9` | Move the **focused** window to workspace 1–9 and follow it there. |
| `Win`+`Shift`+`Alt`+`1` … `9` | Move the focused window to workspace 11–19. |

The window is unlinked from its old workspace and linked to the target. The target workspace becomes
active (on its home monitor) and the window ends up visible and focused — if the target lives on a
different monitor, the window is moved to that monitor.

### Link a window to the current workspace

| Shortcut | What it does |
| --- | --- |
| `Win`+`Space` | Link the **focused** window to the workspace currently shown on its monitor. |

This is how you "claim" a window into a workspace **without moving it** — useful for a window you
restored from the taskbar, or a newly opened app, so it stays put when you switch workspaces. (Nothing
moves or minimizes; it only changes membership.)

### The scratchpad

| Shortcut | What it does |
| --- | --- |
| `Win`+`S` | Toggle the **scratchpad** on the monitor under the mouse. |

The scratchpad is a roaming workspace with no home monitor: `Win+S` shows it (and its windows) on
whatever monitor your mouse is on; `Win+S` again hides it and restores that monitor's previous
workspace. Attach a window to it the same way as to any workspace — focus the window while the
scratchpad is shown and press `Win+Space`.

### Focus, close, and move workspaces between monitors

| Shortcut | What it does |
| --- | --- |
| `Win`+`←` `→` `↑` `↓` | Move focus to the nearest visible window in that direction. |
| `Win`+`W` | Close the focused window. |
| `Win`+`Shift`+`W` | **Release** the current workspace from its monitor (minimizes its windows and unpins it). Move your mouse to another monitor and press `Win`+`N` to re-open it there — that's how you relocate a whole workspace to a different monitor. |

### Apps and Windows shortcuts (defaults)

| Shortcut | What it does |
| --- | --- |
| `Win`+`Enter` | Windows Terminal (focus if already open) |
| `Win`+`Shift`+`B` | Firefox |
| `Win`+`Shift`+`N` | VS Code |
| `Win`+`Shift`+`A` | Gemini (as a desktop web app) |
| `Win`+`Shift`+`M` | Spotify (focus if open) |
| `Win`+`Shift`+`T` | Task Manager |
| `Win`+`E` / `Win`+`Shift`+`F` | File Explorer |
| `Win`+`D` | Show desktop |
| `Win`+`R` | Run dialog |
| `Win`+`Shift`+`S` | Snipping Tool (screen clip) |

---

## Configuration

All shortcuts live in **`config.conf`**, a plain text file sitting next to `winland-keys.exe` (in the
`dist/` folder). Edit it with any text editor.

**To apply changes, restart the keys daemon** — re-run `start-winland.ps1` (it stops the old instance
first). Config is read once at startup.

### Bind syntax

One shortcut per line:

```
bind = <MODIFIERS… KEY>, <command>
```

- Everything **before the comma** is the key combo. Tokens are space-separated and the **last token is
  the key**.
  - Modifiers: **`SUPER`** (required — Winland only acts while Win is held), `SHIFT`, `ALT`, `CTRL`.
  - Keys: `A`–`Z`, `0`–`9`, `NUMPAD0`–`NUMPAD9`, `F1`–`F24`, arrows (`LEFT` `RIGHT` `UP` `DOWN`),
    `RETURN`/`ENTER`, `SPACE`, `TAB`, `ESC`, and a few more.
- Everything **after the comma** is the command to run when the combo is pressed. Its **first word**
  decides how it runs:
  - a **`name.ps1`** script sitting next to the exe → runs that PowerShell script with the rest of the
    line as arguments;
  - a **`name.exe`** sitting next to the exe → runs that program directly (**this is how `winlandctl`
    is found**);
  - otherwise → the whole line runs as a normal shell command (`firefox`, `explorer.exe`, a `https://`
    URL, a `mailto:`/`ms-settings:` URI, …).

Lines starting with `#` are comments.

### Commands you can bind (`winlandctl` verbs)

The workspace and focus features are driven through the `winlandctl` helper. Bind any of these (or run
them yourself from a terminal):

| Command | Effect |
| --- | --- |
| `winlandctl workspace <n>` | Switch to / re-assert workspace `n` (any whole number ≥ 1). |
| `winlandctl movetoworkspace <n>` | Move the focused window to workspace `n` and follow it. |
| `winlandctl link-here` | Link the focused window to the workspace shown on its monitor. |
| `winlandctl scratchpad` | Toggle the roaming scratchpad on the monitor under the mouse. |
| `winlandctl workspace-release` | Release the current workspace from its monitor. |
| `winlandctl focus left\|right\|up\|down` | Focus the nearest window in that direction. |
| `winlandctl focus-app <process>` | Focus that app's window; fails if there is none (or it's already focused). |
| `winlandctl close` | Close the focused window. |

Because workspaces aren't capped, you can bind any key to any number — e.g.
`bind = SUPER F1, winlandctl workspace 20`. These commands work from **any** terminal, elevated or
not — the control pipe accepts every process of the logged-on user.

### Helper scripts (shipped next to the exe)

- `launch-or-focus.ps1 <process>` — focus an existing window of that process (via
  `winlandctl focus-app`), or launch it if none is open — also when it's already focused, so
  re-pressing the combo opens another instance. Example: `bind = SUPER SHIFT M, launch-or-focus spotify`.
- `launch-web.ps1 <url>` — open a URL as a chromeless desktop app window (Chrome/Chromium `--app=`).
- `show-desktop.ps1` — minimize all windows (like the native `Win+D`).

Drop your own `foo.ps1` next to the exe and reference it as `bind = … foo <args>` — no rebuild needed.

### Example config

```conf
# Switch workspaces
bind = SUPER 1, winlandctl workspace 1
bind = SUPER 2, winlandctl workspace 2

# Move the focused window to a workspace (and follow it)
bind = SUPER SHIFT 1, winlandctl movetoworkspace 1
bind = SUPER SHIFT 2, winlandctl movetoworkspace 2

# Link the focused window to the current workspace
bind = SUPER SPACE, winlandctl link-here

# Focus / close / release
bind = SUPER LEFT, winlandctl focus left
bind = SUPER W, winlandctl close
bind = SUPER SHIFT W, winlandctl workspace-release

# Apps and Windows shortcuts
bind = SUPER RETURN, launch-or-focus wt.exe WindowsTerminal -- -d E:\WinLand
bind = SUPER SHIFT B, firefox
bind = SUPER E, explorer.exe
bind = SUPER D, show-desktop
```

---

## Architecture

Winland is **three cooperating processes**, not one app:

| Executable | Role | Keyboard hook? |
| --- | --- | --- |
| **`winland-keys.exe`** | Hotkey daemon. Owns one global low-level keyboard hook; on each Super (Win) combo it runs that combo's configured command. | ✅ the only one |
| **`winland-env.exe`** | Environment service ("window manager"): per-monitor workspaces, directional focus, and the tray icon. Driven entirely over a control pipe. | ❌ none |
| **`winlandctl.exe`** | Tiny CLI that forwards one command to `winland-env` over a named pipe and exits. | ❌ none |

The flow that ties them together:

```
Win+1  →  winland-keys (hook)  →  runs "winlandctl workspace 1"  →  pipe  →  winland-env  →  switches workspace
```

Why split it up? Each part has one job: the hook lives in a tiny, rarely-changing process (so
restarting the window manager doesn't break your hotkeys, and vice-versa); the environment is a
**scriptable control surface** any tool can drive via `winlandctl`; and the keys daemon stays dumb —
it only maps a combo to a command line. The cost is that **both daemons must run**, and
`winlandctl.exe` must sit next to `winland-keys.exe` (the start scripts handle this).

`winland-keys` is **headless** (no window, no tray icon). Only `winland-env` has a tray icon, on the
primary monitor's taskbar: it draws a circle with the active workspace number and updates it as the
**primary monitor's** workspace changes. (Windows 11 doesn't let apps put custom widgets on
secondary-monitor taskbars, so the indicator lives in the one place every tray app gets.) Right-click
the icon for Status / Exit.

---

## Why it needs Administrator

Winland's daemons run **elevated**, for two reasons:

1. **Hotkeys over elevated windows.** A normal (medium-integrity) keyboard hook can't intercept input
   destined for an elevated window. If you run, say, Visual Studio as Administrator, `Win+3` would
   otherwise fall through to it as a literal "3". Likewise, `winland-env` can only minimize/restore/
   focus elevated windows because it is elevated itself.
2. **Reclaiming `Win`+`<number>`.** By default Windows uses `Win`+`<number>` to launch pinned taskbar
   apps. On startup `winland-keys` sets the **`NoWinKeys`** policy
   (`HKCU\…\Policies\Explorer\NoWinKeys = 1`) so the shell stops owning those combos; if it had to
   change the value it **restarts Explorer once** (asking it to exit cleanly first) so it takes effect
   immediately.

**The apps you launch do NOT inherit that elevation.** Binds run their commands and helper scripts
with your normal user token (borrowed from the desktop shell), so Firefox, VS Code, the terminal etc.
start unelevated, exactly as if you had launched them yourself. Only if that route is unavailable
(e.g. Explorer isn't running) does a launch fall back to the elevated token.

This intentionally suppresses Windows' built-in Win-key shortcuts while Winland is set up. It's
reversible — clear the `NoWinKeys` value (set it to `0` or delete it; see `NoWinKeys.reg`) and restart
Explorer to restore the defaults.

---

## Building

From the repo root, with the .NET 10 SDK:

```powershell
dotnet build Winland.slnx -c Debug
```

Building `winland-keys` also copies `winlandctl.exe` next to it, so the keys daemon can find it when
run from its build folder. The simplest way to launch from source is the dev script:

```powershell
.\run.ps1              # stop old → build (Debug) → start both daemons elevated (one UAC prompt)
.\run.ps1 -Dir .\dist  # skip the build, run the pre-built exes in dist
.\run.ps1 -NoBuild     # start whatever is already built
.\run.ps1 -Stop        # stop both daemons
```

It stops running daemons **before** building (a running daemon locks its own DLLs), so a rebuild never
fails on a locked file.

### Auto-start at logon

To start both daemons elevated at every sign-in without a UAC prompt (registers one Scheduled Task per
daemon, running with highest privileges):

```powershell
# run from an elevated PowerShell, in the folder with the exes (e.g. dist)
.\install-autostart.ps1            # register
.\install-autostart.ps1 -Uninstall # remove
```

---

## Project layout

```
Winland.slnx                              solution

src/
  Winland.Common/                         shared library (pipe protocol + logger)
  winland-keys/                           hotkey daemon (owns the keyboard hook)
    KeyboardHook.cs                       global low-level Win-key hook (dedicated thread)
    Config.cs / HotkeyConfig.cs           read config.conf, parse "bind" lines
    AppLauncher.cs                        run a bind's command (script / sibling .exe / shell)
    UnelevatedLauncher.cs                 start apps with the user's normal (unelevated) token
    WindowsHotkeyDisabler.cs              set NoWinKeys so the shell ignores Win+<key>
    config.conf                           the shortcut bindings you edit
    launch-or-focus.ps1 / launch-web.ps1 / show-desktop.ps1
  winland-env/                            environment service (no keyboard hook)
    WorkspaceManager.cs                   per-monitor workspaces (switch / move / link / scratchpad / release)
    WindowNavigator.cs                    Win+arrows focus, Win+W close, focus-app
    Dispatcher.cs                         map a winlandctl command to an operation
    DispatchServer.cs / UiInvoker.cs      named-pipe control channel
    TrayIcon.cs                           numbered workspace tray icon
  winlandctl/                             one-shot control CLI (sends one command over the pipe)

packaging/                                start-winland.* and install-autostart.ps1 (tracked source)
dist/                                     assembled output (gitignored): all exes + config + scripts
```
