# Winland

A small .NET 10 set of tray utilities that bring Omarchy / Hyprland-style **Super-key (Win) keyboard
workflow** to Windows 11: per-monitor workspaces, directional window focus, and config-driven app
shortcuts.

Winland is **three cooperating processes**, not one app:

| Executable | Role | Keyboard hook? |
| --- | --- | --- |
| **`winland-keys.exe`** | Hotkey daemon. Owns one global low-level keyboard hook, and on each Super (Win) combo runs the command configured for it. | ✅ the only one |
| **`winland-env.exe`** | Environment service ("window manager"): per-monitor workspaces, directional focus, and the numbered tray icon. Driven entirely over a control pipe. | ❌ none |
| **`winlandctl.exe`** | Tiny CLI that forwards one command to `winland-env` over its pipe and exits. The bridge the keys daemon shells out to. | ❌ none |

The Super-key flow ties them together:

```
Win+1  →  winland-keys (hook)  →  runs "winlandctl workspace 1"  →  winlandctl  →  pipe  →  winland-env  →  switch workspace
```

`winland-keys` installs a single global low-level keyboard hook that owns the Win key. While Win is
held it swallows the combos it handles (so Windows never performs its own action) and runs each
combo's configured command; everything else passes through untouched. The hook is process-local —
when `winland-keys` isn't running, all keys behave normally.

## Why three processes?

The monolith was split (see `git log`) so that each part has one job:

- **Crash isolation.** The keyboard hook lives in a tiny, rarely-changing process. If the
  window-manager service (`winland-env`) crashes or you restart it while iterating, your hotkeys keep
  working — and vice-versa.
- **A scriptable control surface.** Because the environment is driven by `winlandctl` over a named
  pipe, *anything* can drive it — a hotkey, a script, a scheduled task, your own tooling — just by
  running `winlandctl <command>`. The keys daemon is simply the most common caller.
- **A dumb, predictable hotkey daemon.** `winland-keys` has no workspace or focus logic at all; it
  only maps a combo to a command line. All behavior lives behind the `winlandctl` verbs.

The cost is more moving parts: **both daemons must be running**, and `winlandctl.exe` must sit next
to `winland-keys.exe` so the keys daemon can find it. The start script below handles both.

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

Application launch/focus shortcuts are defined in `config.conf` (see below), not hardcoded.

Note: in this architecture **the workspace and window-navigator combos are themselves config
entries** (`bind = SUPER 1, winlandctl workspace 1`, `bind = SUPER LEFT, winlandctl focus left`, …) —
not reserved built-ins. The keys daemon doesn't special-case them; they're just binds whose command
happens to be `winlandctl`. That means you can rebind, remove, or repurpose any of them.

---

## Configuration (`config.conf`)

`config.conf` ships next to `winland-keys.exe` and is read on startup (and via the keys tray's
**Reload config**). It uses a simple, hand-parsed Hyprland/Omarchy-style grammar — one
`keyword = value` per line, `#` for comments.

A global reader (`Config`) tokenizes the file into `keyword = value` entries; each feature interprets
the keywords it cares about. Today the only keyword is `bind`, but the file is designed to grow more
configuration without changing the reader.

### Bind syntax

```
bind = <MODS… KEY>, <command>
```

- **Combo** (before the comma): space-separated tokens, the **last token is the key**.
  - Modifiers: `SUPER` (required — the hook only acts while Win is held), `SHIFT`, `ALT`, `CTRL`.
  - Keys: `A`–`Z`, `0`–`9`, `RETURN`/`ENTER`, arrows, `F1`–`F24`, `SPACE`, `TAB`, `ESC`,
    `NUMPAD0`–`NUMPAD9`, and a few more.
- **Command** (after the comma) is resolved at run time, with no special keywords. Its first token
  decides how it runs:
  - a **`<token>.ps1`** script next to the exe → that script runs with the rest of the line as args;
  - a **`<token>.exe`** sibling next to the exe → that program runs directly (**this is how
    `winlandctl` is found**);
  - otherwise the whole line is executed as a shell command (`firefox`, `explorer.exe`, a URI, …).

### The `winlandctl` verbs

The environment service understands these (drive it from any bind, or from a terminal):

```
winlandctl workspace 1..9      switch the cursor monitor to that workspace
winlandctl workspace-release   release the current workspace from the cursor monitor
winlandctl focus left|right|up|down   move focus to the nearest window that way
winlandctl close               close the foreground window
```

Exit codes: `0` OK · `1` the env replied `ERR` (or no reply) · `2` bad usage · `3` couldn't reach
`winland-env` (is it running?).

### Shipped helper scripts (next to the exe)

- `launch-or-focus.ps1 <process>` — focus an existing window of that process if one is open, else
  launch it (focus-if-open behavior).
- `launch-web.ps1 <url>` — open a URL as a chromeless desktop app window (Chrome/Chromium `--app=`).
- `show-desktop.ps1` — minimize all eligible top-level windows (mimics the native `Win+D`).

Drop any `foo.ps1` next to the exe and reference it as `bind = … foo <args>` — no code change needed.

### Example (excerpt from the shipped config)

```conf
# Environment control (winland-env, via winlandctl)
bind = SUPER 1, winlandctl workspace 1
bind = SUPER LEFT, winlandctl focus left
bind = SUPER W, winlandctl close
bind = SUPER SHIFT W, winlandctl workspace-release

# Re-register default Windows functionality (NoWinKeys turns these off)
bind = SUPER E, explorer.exe
bind = SUPER D, show-desktop

# App shortcuts
bind = SUPER RETURN, launch-or-focus wt.exe WindowsTerminal -- -d E:\WinLand
bind = SUPER SHIFT B, firefox
bind = SUPER SHIFT A, launch-web https://gemini.google.com
bind = SUPER SHIFT M, launch-or-focus spotify
```

---

## Starting Winland

Both daemons must run, **both elevated** (see below). Two entry points are provided.

### From a packaged folder (`dist/`) — recommended

`dist/` holds all three exes side by side plus the config and scripts. To start everything:

- **Double-click `start-winland.cmd`**, or run `powershell -ExecutionPolicy Bypass -File .\start-winland.ps1`.

It self-elevates with a **single UAC prompt**, stops any previous instances (so you never get two
keys daemons double-firing every shortcut), then starts `winland-env.exe` and `winland-keys.exe`.
`start-winland.ps1 -Stop` stops both. The tracked source for these lives in `packaging/`.

### From source (development)

`run.ps1` at the repo root builds and runs straight from the build outputs:

```powershell
.\run.ps1              # stop old → build (Debug) → start both daemons elevated (one UAC prompt)
.\run.ps1 -Dir .\dist  # skip the build and start the pre-built exes in dist
.\run.ps1 -NoBuild     # start what's already built
.\run.ps1 -Stop        # stop both daemons
```

It stops the running daemons **before** building (a running daemon locks its own DLLs), so a rebuild
never fails on a locked file.

---

## System tray icons

Each daemon has its own tray icon (both on the primary monitor's taskbar):

- **`winland-env`** draws its icon at runtime — a white circle with the active workspace number in
  black — and updates it whenever the **primary monitor's** workspace changes. Right-click for the
  menu (Status / Exit); double-click for status.
- **`winland-keys`** shows a status/menu icon (Status / Reload config / Open config / Exit).

**Why only the main monitor's taskbar?** The tray (notification area) exists only on the primary
monitor's taskbar in the default Windows 11 configuration. **Windows 11 does not allow apps to draw
custom widgets on the taskbar** (the old Win10-era deskband/toolbar API is gone), so a true
per-monitor on-taskbar workspace indicator would require display hacks. Winland avoids all of that
and uses the one place the OS already gives every tray app: a single icon on the primary taskbar.

---

## ⚠️ Important: Winland disables some default Windows behavior

For its shortcuts to work reliably, Winland needs two things from the OS:

1. **Both daemons run elevated (as Administrator).** A normal (medium integrity) keyboard hook is
   blocked by Windows from intercepting input destined for an elevated window (UIPI). If you run,
   say, Visual Studio as Administrator, `Win+3` would otherwise fall through to it as a literal "3".
   Elevation also matters for the control pipe: `winland-env` creates it elevated, so the
   `winlandctl` the keys daemon launches must be elevated too (it inherits elevation from the
   elevated keys daemon). The app manifests request administrator rights; you'll see a UAC prompt
   (one, via the start script).

2. **`winland-keys` turns off Windows' own `Win`+`<key>` shortcuts (the `NoWinKeys` policy).**
   Otherwise the shell still acts on combos Winland owns — most visibly `Win`+`<number>`, which
   Windows uses to launch pinned taskbar apps. The keys daemon sets the `NoWinKeys` policy
   (`HKCU\…\Policies\Explorer\NoWinKeys = 1`) on startup. If it had to change the value, it
   **restarts Explorer once** so the change takes effect immediately; otherwise it's a no-op.

This means built-in Windows Win-key shortcuts are intentionally suppressed while the policy is set.
The behavior is reversible — clear the `NoWinKeys` value (set it to `0` or delete it; see
`NoWinKeys.reg`) and restart Explorer to restore Windows' defaults.

---

## Build & run

```powershell
dotnet build Winland.slnx -c Debug
```

Per-project outputs land in each project's `bin/Debug/net10.0-windows/` (or `net10.0/` for
`winlandctl`). Building `winland-keys` also **copies `winlandctl.exe` (and its runtime files) next to
`winland-keys.exe`**, so the keys daemon can find it when run straight from its build folder. The
easiest way to actually launch from source is `.\run.ps1` (above).

To start both daemons elevated automatically at logon without a UAC prompt, use the autostart
installer (registers one Scheduled Task per daemon, running with highest privileges at sign-in):

```powershell
# run elevated, from the folder containing the exes (e.g. dist)
.\install-autostart.ps1            # register
.\install-autostart.ps1 -Uninstall # remove
```

---

## Project layout

```
Winland.slnx                              solution

src/
  Winland.Common/                         shared library, referenced by all three exes
    Ipc.cs                                pipe name + OK/ERR protocol constants
    Log.cs                                append-only winland-hooklog.txt logger

  winland-keys/                           the hotkey daemon (owns the keyboard hook)
    Program.cs                            entry point (single-instance mutex)
    KeysApp.cs                            tray app: hook + config + run each combo's command
    KeyboardHook.cs                       global low-level Win-key hook on a dedicated thread
    Config.cs                             config.conf reader/tokenizer
    HotkeyConfig.cs                       interpret "bind" entries into combos + commands
    AppLauncher.cs                        run a bind's command (script / sibling .exe / shell)
    WindowsHotkeyDisabler.cs              set NoWinKeys so the shell ignores Win+<key>
    KeysTray.cs                           tray presence (status, reload/open config, exit)
    config.conf                           user-editable shortcut bindings
    launch-or-focus.ps1 / launch-web.ps1 / show-desktop.ps1
    app.manifest                          requests administrator elevation

  winland-env/                            the environment service (no keyboard hook)
    Program.cs                            entry point
    EnvApp.cs                             root: workspace engine + tray + pipe server
    DispatchServer.cs                     named-pipe listener (control channel)
    UiInvoker.cs                          marshal a pipe command onto the UI thread
    Dispatcher.cs                         map a command ("workspace 1") to an operation
    WorkspaceManager.cs                   per-monitor workspaces
    WindowNavigator.cs                    Win+arrows focus + Win+W close
    TrayIcon.cs                           numbered workspace tray icon
    app.manifest                          requests administrator elevation

  winlandctl/                             the control CLI (one-shot)
    Program.cs                            send one command to winland-env over the pipe

packaging/
  start-winland.ps1 / start-winland.cmd   start both daemons elevated (tracked source)
  install-autostart.ps1                   register the two daemons as logon Scheduled Tasks

dist/                                     local assembled output (gitignored): all three exes,
                                          config, scripts, start-winland.*, install-autostart.ps1
```
