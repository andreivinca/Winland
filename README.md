# Winland

Winland brings an Omarchy / Hyprland-style **Super-key (Win) keyboard workflow** to Windows 11:
per-monitor workspaces you switch with `Win`+a number, opt-in **scrolling tiling** for the windows you
put in them, directional focus, and app launching — all from the keyboard, all configurable. Windows you
don't tile keep behaving like normal Windows windows.

It's a tiny .NET 10 tray app, `winland.exe`, which runs two small helper processes of its own — see
[Architecture](#architecture). When it isn't running, your keyboard behaves completely normally.

---

## Quick start

Winland runs from a self-contained folder (`dist/`) that holds the executables, the config file, and
the helper scripts.

1. **Start it:** double-click **`winland.exe`**.
2. Accept the **UAC prompt** — Winland must run elevated (explained
   [below](#why-it-needs-administrator)).
3. A small **Winland icon** appears in the system tray, and a **translucent circle** appears
   on each monitor showing that monitor's workspace. You're running.

To stop it: right-click the tray icon → **Exit** (this also ends its helper processes). To start it
automatically at every logon without a UAC prompt, see [Auto-start at logon](#auto-start-at-logon).

> Running from source instead? Use `.\run.ps1` (builds and launches). See [Building](#building).

---

## The idea in 30 seconds

- A **workspace** is a numbered group of windows (1, 2, 3, …). There's no hard limit on the number.
- Each workspace lives on a **home monitor** — the monitor it was first opened on. It never wanders
  to another monitor on its own; to move it, drag its number on the
  [on-screen indicator](#the-on-screen-workspace-indicator) onto the other monitor.
- A workspace's windows are **tiles**: they stand side by side in a row across its monitor and share
  the width. When they no longer fit, the row **scrolls** instead of squeezing them: tiles keep a
  usable width, and the view moves along the row as you change focus. A tile never shows on another
  monitor.
- A monitor shows **one workspace at a time**. Switching a monitor to another workspace minimizes
  ("puts away") everything it was showing and lays out the new workspace's tiles — switch to an empty
  workspace and the monitor is empty.
- A window joins a workspace **only when you say so** — when you **tile** it (`Win+Space`), **move** it
  to a workspace (`Win+Shift+N`), or open it with a **Winland shortcut** (it opens as a tile of the
  workspace on the monitor under the mouse). Every other window is a **normal Windows window**: you
  open, drag, resize, maximize and snap it as usual, and Winland never tiles or resizes it. It belongs to
  no workspace, so a workspace switch puts it away with everything else on that monitor, and it doesn't
  come back with any workspace. Windows that are open when Winland starts are all normal windows.
- **Drag a tile** (or resize it) with the mouse — or press `Win+T` — and it leaves the row and
  **floats** right where it is — Windows handles it from then on — but it stays in its workspace: it's put
  away and brought back with it, exactly where you left it and in front of the tiles, even on another
  monitor. `Win+T` puts it back in its own workspace's row.
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

Pressing `Win`+`N` always **re-asserts** workspace N on its monitor: it minimizes anything there that
isn't N's (normal windows included), lays N's tiles out again — **including ones you minimized** or
maximized — and brings back N's floating windows where you left them. Re-pressing the same workspace is
a handy "tidy this monitor" button.

Switching a monitor away from a workspace puts away **all** of that workspace's windows — also ones
floating on another monitor.

The **first** time you press `Win`+`N` for a workspace that has never been opened, it opens on the
monitor under your **mouse cursor** and is pinned there from then on.

### Move a window to another workspace

| Shortcut | What it does |
| --- | --- |
| `Win`+`Shift`+`1` … `9` | Move the **focused** window to workspace 1–9 and follow it there. |
| `Win`+`Shift`+`Alt`+`1` … `9` | Move the focused window to workspace 11–19. |

The window becomes a tile of the target workspace, right after the tile that had focus there, and its
old workspace closes the gap. The target workspace becomes active (on its home monitor) and the window
ends up in view and focused — if the target lives on a different monitor, the window moves there.

### Tiling

| Shortcut | What it does |
| --- | --- |
| `Win`+`Space` | **Tile** the focused window into the workspace shown on its monitor. On a floating window of that workspace, put it back in its place in the row; on a tile, snap it back into its column. |
| `Win`+`Shift`+`Space` | **Unlink** the focused window: it leaves its workspace and becomes a normal window again, and the other tiles close the gap. |
| `Win`+`T` | **Pop out / put back.** On a tile: it floats where it is, still in its workspace, and the row closes the gap. On a floating window: it goes back into its **own** workspace's row at its old place — even from another monitor (if that workspace isn't shown, it is shown). On a window in no workspace: same as `Win`+`Space`. |
| `Win`+`←` `→` | On a tile: focus the tile beside it, scrolling the row if it's out of view. Past either end of the row (or on any other window), focus the nearest window in that direction. |
| `Win`+`Shift`+`←` `→` | On a tile: swap it with its neighbour; from the end of the row it moves onto the next monitor's workspace. On any other window: move it to the next monitor (a floating window stays in its workspace). |

**The mouse follows the keyboard.** When a shortcut focuses another window (`Win`+arrows, launch-or-focus
shortcuts) or moves the focused one (`Win`+`Shift`+arrows, `Win`+`Space`, `Win`+`Shift`+`N`), the
cursor jumps to the middle of that window — unless it is already over it. So the next click, scroll, or
"focus follows mouse" hover lands on the window you just moved to. Switching to a workspace
(`Win`+`N`, `Win`+`S`, or dropping its indicator circle on another monitor) does the same with the
window it focuses there; an empty workspace brings the
mouse to the middle of its monitor instead, so an app you launch next opens there. (The mouse's monitor
is also where a never-used workspace or the scratchpad opens, so after a switch that is the monitor
you switched on.)

How the row is laid out:

- Tiles fill the monitor's work area, full height, with small gaps, in the order you added them (a new
  tile goes right after the focused one).
- While every tile can be at least **560 px** wide (scaled with the monitor's DPI), they share the
  width equally: one tile fills the monitor, two take half each, three a third each.
- After that the tiles keep that width and the row **scrolls by whole tiles**. Tiles outside the view
  are minimized, so they never show up on a neighbouring monitor. Focusing one — with `Win`+`←`/`→`,
  or by hand from the taskbar or `Alt`+`Tab` — scrolls the row to it.
- **Dragging or resizing** a tile with the mouse makes it float (see above); just clicking its title bar
  doesn't. Maximizing a tile lasts until the next layout of its workspace (e.g. the next `Win`+`N`).
- When a tile closes, or its app hides it (e.g. "close to tray"), the other tiles close the gap.

### The scratchpad

| Shortcut | What it does |
| --- | --- |
| `Win`+`S` | Toggle the **scratchpad** on the monitor under the mouse. |

The scratchpad is a roaming workspace with no home monitor: `Win+S` shows it (and its tiles) on
whatever monitor your mouse is on; `Win+S` again hides it and restores that monitor's previous
workspace. Add a window to it the same way as to any workspace — focus the window while the
scratchpad is shown and press `Win+Space`.

### Focus, close, and move workspaces between monitors

| Shortcut | What it does |
| --- | --- |
| `Win`+`←` `→` `↑` `↓` | Move focus to the nearest visible window in that direction (`←`/`→` step through a tile row first — see [Tiling](#tiling)). |
| `Win`+`W` | Close the focused window. |
| `Win`+`Shift`+`W` | **Release** the current workspace from its monitor (minimizes its windows and unpins it). Move your mouse to another monitor and press `Win`+`N` to re-open it there. (The direct way to move a whole workspace to another monitor is to drag its [indicator circle](#the-on-screen-workspace-indicator) there.) |

### Apps and Windows shortcuts (defaults)

| Shortcut | What it does |
| --- | --- |
| `Win`+`Enter` | Windows Terminal (focus if already open) |
| `Win`+`Shift`+`B` | Firefox |
| `Win`+`Shift`+`N` | VS Code |
| `Win`+`Shift`+`A` | Gemini (as a desktop web app; focus if already open) |
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

**To apply changes, restart Winland** (tray icon → Exit, then start `winland.exe` again — or re-run
`run.ps1`). Config is read once, when the keys helper starts.

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
| `winlandctl movetoworkspace <n>` | Move the focused window to workspace `n` (as a tile) and follow it. |
| `winlandctl link-here` | Tile the focused window into the workspace shown on its monitor. |
| `winlandctl unlink` | Unlink the focused window from its workspace — a normal window again. |
| `winlandctl toggle-float` | Pop the focused tile out of its row, or put a floating window back into its own workspace's row. |
| `winlandctl scratchpad` | Toggle the roaming scratchpad on the monitor under the mouse. |
| `winlandctl workspace-release` | Release the current workspace from its monitor. |
| `winlandctl focus left\|right\|up\|down` | Focus the next tile (`left`/`right`, scrolling) or the nearest window in that direction. |
| `winlandctl move-window left\|right` | Move the focused tile along its row / onto the next monitor; a normal window to the next monitor. |
| `winlandctl focus-app <process>` | Focus that app's window; fails if there is none (or it's already focused). |
| `winlandctl focus-web <url>` | The same for a browser `--app` window opened for that URL. |
| `winlandctl close` | Close the focused window. |
| `winlandctl place-next-window` | Open the next new app window on the monitor under the mouse, as a tile of the workspace shown there (expires after 10 s). |

Because workspaces aren't capped, you can bind any key to any number — e.g.
`bind = SUPER F1, winlandctl workspace 20`. These commands work from **any** terminal, elevated or
not — the control pipe accepts every process of the logged-on user.

### Helper scripts (shipped next to the exe)

- `launch-or-focus.ps1 <process>` — focus an existing window of that process (via
  `winlandctl focus-app`), or launch it if none is open — also when it's already focused, so
  re-pressing the combo opens another instance. Example: `bind = SUPER SHIFT M, launch-or-focus spotify`.
- `launch-web.ps1 <url>` — open a URL as a chromeless desktop app window (Chrome/Chromium `--app=`).
- `launch-or-focus-web.ps1 <url>` — `launch-or-focus` for web apps: focus the app window already open
  for that URL (via `winlandctl focus-web`), or open one with `launch-web` — also when it's already
  focused. Example: `bind = SUPER SHIFT A, launch-or-focus-web https://gemini.google.com`.

Apps opened by a shortcut appear on the **monitor under the mouse**, as a **tile** of the workspace
shown there (fixed-size dialogs such as Run are placed but not tiled): plain shell-command binds do this
automatically, and `launch-or-focus` / `launch-web` call `winlandctl place-next-window` before they
launch. A script of your own can do the same.
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

# Tile / unlink the focused window, and move a tile along its row
bind = SUPER SPACE, winlandctl link-here
bind = SUPER SHIFT SPACE, winlandctl unlink
bind = SUPER T, winlandctl toggle-float
bind = SUPER SHIFT LEFT, winlandctl move-window left
bind = SUPER SHIFT RIGHT, winlandctl move-window right

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

## The on-screen workspace indicator

Winland puts a small **translucent white circle on every monitor** showing the
workspace that monitor is currently displaying — the number, `S` for the scratchpad, or `–` when the
monitor shows no workspace. It updates the moment a workspace changes: `winland` broadcasts the
new state and each circle redraws.

**Move it wherever you like.** Drag a circle with the left mouse button:

- It is always **docked to one edge** of the monitor's work area, a few pixels in.
- Dragging slides it **along that edge only** — pulling away from the edge does nothing.
- Bring the mouse **near another edge** and the circle **re-docks** there, then slides along that one.
- It never steals focus from the app you're working in.

**Drag a workspace to another monitor.** Pull a circle showing a number off its monitor and it comes
with the mouse; drop it anywhere on another monitor and that **whole workspace moves there**:

- Its tiles are laid out on the new monitor, and its floating windows on the old monitor come along.
- The new monitor shows the moved workspace and puts away what it was showing (that workspace stays
  homed there, ready for `Win`+`N`).
- The old monitor goes back to **the workspace it showed before** — or shows nothing (`–`) when it
  has no other.
- The workspace now lives on the new monitor: `Win`+`N` shows it there from now on.

For example, with `3` on monitor 1 and `5` on monitor 2, drop monitor 1's `3` on monitor 2: monitor 2
now shows workspace 3 with all its windows, and monitor 1 shows whatever it showed before 3. Let go
back on the circle's own monitor and it just re-docks there, as above. The `–` and `S` circles have
nothing to carry and stay on their monitor.

Where you park each circle is remembered between runs (in `winland-indicator-dock.txt`, next to the
exe, per monitor).

The circles come from `winland-indicator.exe`, which runs **unelevated**. It reads workspace state,
and its only request to `winland` is the move above. If it isn't running — or `winland` isn't —
everything else works exactly as before.

---

## Architecture

Winland is **one app you start, `winland.exe`, which runs two helper processes** (its *companions*)
and ends them when it exits. Plus a CLI any tool can call:

| Executable | Role | Keyboard hook? |
| --- | --- | --- |
| **`winland.exe`** | The main app: the "window manager" (per-monitor workspaces, tiling, directional focus) and the tray icon, driven entirely over a control pipe. Starts the two companions below and ends them when it exits. Elevated. | ❌ none |
| **`winland-keys.exe`** | Companion: the hotkey daemon. Owns one global low-level keyboard hook; on each Super (Win) combo it runs that combo's configured command. Runs with Winland's (elevated) rights. | ✅ the only one |
| **`winland-indicator.exe`** | Companion: the per-monitor workspace circles. Subscribes to Winland's broadcast event pipe and draws; its one request back is moving a workspace when you drop a circle on another monitor. Runs **unelevated**. | ❌ none |
| **`winlandctl.exe`** | Tiny CLI that forwards one command to Winland over a named pipe and exits. | ❌ none |

The flow that ties them together:

```
Win+1  →  winland-keys (hook)  →  runs "winlandctl workspace 1"  →  pipe  →  winland  →  switches workspace
```

Why split it up? Each part has one job: the hook lives in a tiny, rarely-changing process; the window
manager is a **scriptable control surface** any tool can drive via `winlandctl`; the keys daemon stays
dumb — it only maps a combo to a command line; and the indicator, a UI you click and drag, runs
without administrator rights. Starting and ending the companions is Winland's job alone
(`Companions.cs`): they are put in a Windows job object that ends them whenever Winland ends — Exit,
a crash, or Task Manager — while the apps you launch from shortcuts are kept out of it.

Only `winland` has a tray icon, on the primary monitor's taskbar — a static Winland badge; right-click
it for Status / Exit. Workspace numbers are shown by `winland-indicator`'s floating circles on every
monitor. (Windows 11 doesn't let apps put custom widgets on taskbars, which is why the indicator
floats instead.)

---

## Why it needs Administrator

Winland and its keys helper run **elevated**, for two reasons:

1. **Hotkeys over elevated windows.** A normal (medium-integrity) keyboard hook can't intercept input
   destined for an elevated window. If you run, say, Visual Studio as Administrator, `Win+3` would
   otherwise fall through to it as a literal "3". Likewise, `winland` can only minimize/restore/
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
dotnet test Winland.slnx          # unit tests (tiling layout and order)
```

Building `winland` also builds its companions and puts them — with `winlandctl.exe`, `config.conf` and
the helper scripts — next to `winland.exe`, so its build folder runs as is. The simplest way to launch
from source is the dev script:

```powershell
.\run.ps1              # stop Winland → build (Debug) → start winland.exe (one UAC prompt)
.\run.ps1 -Dir .\dist  # skip the build, run the pre-built winland.exe in dist
.\run.ps1 -NoBuild     # start whatever is already built
.\run.ps1 -Stop        # stop Winland
```

It stops Winland **before** building (a running process locks its own DLLs), so a rebuild never fails
on a locked file. `publish-dist.ps1` assembles a self-contained `dist/` folder.

### Auto-start at logon

To start Winland at every sign-in without a UAC prompt (registers one Scheduled Task that runs
`winland.exe` with highest privileges; it starts its companions itself):

```powershell
# run from an elevated PowerShell, in the folder with winland.exe (e.g. dist)
.\install-autostart.ps1            # register
.\install-autostart.ps1 -Uninstall # remove
```

---

## Project layout

```
Winland.slnx                              solution

src/
  Winland.Common/                         shared library (pipe protocol, control-pipe client, logger, unelevated launcher)
    UnelevatedLauncher.cs                 start a process with the user's normal (unelevated) token
    WinlandClient.cs                      send one command over Winland's control pipe (keys daemon, indicator)
  winland/                                the main app: window manager, tray icon, companions
    WinlandApp.cs                         composes everything; Exit ends it all
    Companions.cs / KillOnCloseJob.cs     start winland-keys and winland-indicator; end them with Winland
    WorkspaceManager.cs                   per-monitor workspaces (switch / move / scratchpad / release)
    WorkspaceManager.Tiling.cs            tiling: tile / float / unlink, scrolling strips, reacting to window events
    WorkspaceManager.MoveWorkspace.cs     move a whole workspace to another monitor (indicator drag)
    StripLayout.cs / TileOrder.cs         the strip's geometry and tile order (pure, unit-tested)
    PreviousWorkspace.cs                  which workspace a monitor goes back to (pure, unit-tested)
    CursorWarp.cs                         the mouse follows focus after focus/move commands
    WindowNavigator.cs                    Win+arrows focus, Win+W close, focus-app
    Dispatcher.cs                         map a winlandctl command to an operation
    DispatchServer.cs / UiInvoker.cs      named-pipe control channel
    EventServer.cs / PipeAcl.cs           broadcast pipe: pushes every monitor's workspace to subscribers
    TrayIcon.cs                           tray presence (static icon, Status/Exit menu)
  winland-keys/                           companion: hotkey daemon (owns the keyboard hook)
    KeyboardHook.cs                       global low-level Win-key hook (dedicated thread)
    Config.cs / HotkeyConfig.cs           read config.conf, parse "bind" lines
    AppLauncher.cs                        run a bind's command (script / sibling .exe / shell)
    WindowsHotkeyDisabler.cs              set NoWinKeys so the shell ignores Win+<key>
    config.conf                           the shortcut bindings you edit
    launch-or-focus.ps1 / launch-web.ps1 / launch-or-focus-web.ps1 / show-desktop.ps1
  winland-indicator/                      companion: per-monitor on-screen workspace circles (unelevated)
    IndicatorApp.cs                       one window per monitor, display changes, dock persistence, workspace moves
    IndicatorWindow.cs                    one circle: layered rendering + drag/dock/carry behavior
    EventClient.cs / UiPoster.cs          subscribe to winland's events, hand them to the UI thread
  winlandctl/                             one-shot control CLI (sends one command over the pipe)

tests/
  winland.Tests/                          unit tests for the tiling layout, tile order, mouse moves and workspace fallback (MSTest)

packaging/                                install-autostart.ps1 (tracked source, copied into dist)
dist/                                     assembled output (gitignored): all exes + config + scripts
```
