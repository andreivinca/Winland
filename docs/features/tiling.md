# Feature Deep Dive: Tiling

## 1. Objective

Give the windows the user chooses a real tiled layout — Hyprland/niri-style — while every other window
keeps behaving like a normal Windows window. Tiling is **opt-in per window**. A workspace member is
either a **tile** or **floating**:

- A window becomes a **tile** of a workspace by `Win+Space` (`link-here`), `Win+Shift+N`
  (`movetoworkspace N`), or by being opened from a Winland shortcut (`place-next-window`, §4.1).
- A tile the user **drags or resizes** with the mouse **floats**: it leaves the strip and Windows manages
  it where the user put it — even on another monitor — but it stays in its workspace (`_floating`). It
  is put away and brought back with the workspace, in place. `Win+Space` tiles it again, at its old
  place in the strip.
- `Win+Shift+Space` (`unlink`) removes a member from its workspace: a normal window again.
- Winland never tiles, moves or resizes a window that belongs to no workspace on its own. Like everything
  else on a monitor, it is put away (minimized) when that monitor switches workspace or is released,
  and comes back with none.

The code lives in `winland`: `WorkspaceManager.Tiling.cs` (behaviour), `StripLayout.cs` (geometry)
and `TileOrder.cs` (order). The last two are pure and covered by `tests/winland.Tests`.

## 2. The Scrolling Strip

A workspace's tiles stand in one row of full-height columns across the **work area** of the monitor
showing it, separated and surrounded by a gap.

- While every tile can be at least `BaseMinTileWidth` (560 px at 96 DPI) wide, all tiles show and share
  the width equally.
- Beyond that, the columns keep the width of a full view (`VisibleCount` columns across the monitor)
  and the row overflows. The **view** shows `VisibleCount` consecutive tiles starting at `First`, and
  scrolls by whole tiles.
- Gap and minimum width scale with the monitor's effective DPI (`GetDpiForMonitor`); `winland` is
  per-monitor-v2 DPI aware so all coordinates are physical pixels.

`StripLayout.Arrange(work, dpi, tileCount, first, reveal)` returns the scroll position and one slot per
tile. It scrolls the minimum needed to show tile `reveal` (`-1`: keep `first`, clamped to a full view).

### 2.1 Containment

Tiles outside the view are **minimized**, never parked off-screen: with several monitors, "off-screen"
is usually another monitor. Every slot — visible or not — lies inside its monitor's work area. A hidden
tile's slot is the edge column it would scroll in through, and it is written as the window's restore
position, so a hidden tile restored by hand (taskbar, `Alt+Tab`) reappears on its own monitor; the
foreground hook then scrolls the strip to it (§4).

Partially visible tiles at the edges (smooth scrolling) would need per-window clipping
(`SetWindowRgn`) and are not implemented.

## 3. Order and Commands

`_tileOrder` (workspace → members left to right) is **ordering metadata only**: `_windowWorkspace` stays
the membership authority, and `TilesOf` reconciles the order with it on every read (closed windows
pruned, unknown members appended). Floating members, members that their app has hidden (closed to the
tray), and members the shell has cloaked (a closed Store app, a window on another virtual desktop) keep
their place in the order but take no column.

| Command | Behaviour |
| --- | --- |
| `link-here` | Link the focused window to the workspace shown on its monitor as a tile: a new member right after that workspace's focused tile, a floating member back at its old place. On one of its tiles: snap it back into its column. No-op on a monitor showing no workspace. |
| `unlink` | Remove the focused window from its workspace; it stays where it is, in front, as a normal window. The strip closes the gap. |
| `toggle-float` | On a tile: float it where it is (still a member) and close the gap — like a drag. On a floating member: re-tile it into its **own** workspace at its old place, from any monitor; if that workspace isn't shown, `workspace` is switched to it (the scratchpad, shown only by its toggle, is put away instead). On a window in no workspace: `link-here`. |
| `movetoworkspace n` | Re-link as a tile of `n` (after `n`'s focused tile), switch to `n`, focus it. The old workspace closes the gap if it is on screen. |
| `focus left\|right` | On a tile: focus the neighbouring tile and scroll it into view. Past either end of the strip, on any other window, or for `up`/`down`: geometric `FocusNearest` (see `window-focus.md`). |
| `move-window left\|right` | On a tile: trade places with the neighbour (`TileOrder.MoveTo`). At the end of the strip: join the workspace shown on the adjacent monitor in that direction, entering at the near end (no-op if it shows none). On any other window: `MoveWindowToMonitor` to the adjacent monitor, membership unchanged. |

Showing a workspace (`ShowWorkspace`) lays out its strip, restores its minimized floating windows in
place, on whatever monitor they are (`RestoreInPlace`, maximized again if they were), focuses it, and
then raises its floating windows above everything (`RaiseFloating`: each passes through the topmost band
and straight back out — `HWND_TOPMOST` then `HWND_NOTOPMOST`, no activation, bottom-up so their order is
kept; a plain `HWND_TOP` from a background process is silently ignored; windows the user made
always-on-top are skipped). Restored without activation, a floating window would otherwise
return at its old Z position — under the tiles of the monitor it sits on. The scratchpad roams, so its
floating windows are moved to its monitor instead. Putting a workspace away (`PutAwayWorkspace`)
minimizes all its windows, wherever they are.

The viewport always keeps the workspace's focused tile (`_lastActive`) in view: every layout reveals it
unless a command reveals another tile.

After `link-here`, `toggle-float`, `movetoworkspace`, `focus` and `move-window`, the mouse is moved onto the focused
tile if focus or its position changed and the cursor isn't over it already (`CursorWarp`, see
`window-focus.md` §3.6).

## 4. Reacting to Window Events

`InstallTileHooks` adds `EVENT_OBJECT_DESTROY..EVENT_OBJECT_HIDE` (destroy, show, hide),
`EVENT_OBJECT_CLOAKED..EVENT_OBJECT_UNCLOAKED` and `EVENT_SYSTEM_MOVESIZEEND` to the existing foreground
hook. Only events on **tiles** are considered; floating members are left to Windows.

| Event | Reaction |
| --- | --- |
| destroy / hide / show / cloak / uncloak | Re-lay out the tile's workspace (its column closes or opens). |
| foreground on a tile out of view | Scroll its strip to it (subject to the workspace event guard). |
| move/size end | **Float** (`FloatIfMoved`): unless the tile's visible frame is still within 2 px of its column (a click on the title bar), or it is maximized or minimized (states of the tile, not moves), it joins `_floating` where it was dropped and the strip closes the gap. |

The callback only queues work (`_pendingReflows`, `_pendingDrags`, `_pendingLaunches`); a 50 ms
UI-thread timer performs it. That coalesces bursts and keeps layout out of the hook callback, which can
run in the middle of a workspace operation.

These events never change membership: a late `DESTROY` carrying a recycled handle costs at most a
needless layout, because membership is still pruned lazily by `IsWindow` (see `workspaces.md` §2).

### 4.1 Windows opened from a shortcut

`place-next-window` arms launch placement (`WorkspaceManager.LaunchPlacement.cs`): the next new app
window is moved to the monitor that was under the mouse and, if it is resizable (`WS_THICKFRAME` —
fixed-size dialogs such as Run are skipped), queued on the reflow timer to become a tile of the
workspace shown there (`TileLaunchedWindow`). Windows opened any other way stay unlinked.

## 5. Placing a Window

- **Shown tile** (`ShowTile`): if minimized or maximized, `SetWindowPlacement(SW_SHOWNOACTIVATE)` into
  the column first. Then `SetWindowPos` with the column grown by the window's **invisible resize
  border** (window rect minus `DWMWA_EXTENDED_FRAME_BOUNDS`), so the visible frames line up with the
  gaps.
- **Hidden tile** (`HideTile`): `SetWindowPlacement(SW_SHOWMINNOACTIVE)` with the parking column as
  the restore position.
- `WINDOWPLACEMENT` rectangles are in **workspace coordinates** (relative to the primary monitor's work
  area); `ToWorkspaceCoordinates` converts.
- Hung windows (`IsHungAppWindow`) are skipped, so one frozen app cannot freeze `winland` — and
  every shortcut with it.
- Nothing activates a window except the explicit focus at the end of a command.

## 6. Known Constraints

- Windows with a minimum size larger than their column overlap their neighbour.
- Maximizing a tile lasts until its workspace's next layout.
- A tile the user minimizes leaves an empty column until the next layout restores it.
- An app that repositions its own window just after opening can move a freshly launched tile out of its
  column; `Win+Space` or `Win+N` puts it back.
- Column widths are not adjustable yet; the minimum width and gap are constants in `StripLayout`.
- Only horizontal strips; no vertical stacking within a column.
- Layout state (order, scroll position, floating) is in memory only, like all workspace state.

## 7. Extension Guidance

- Keep geometry in `StripLayout` and ordering in `TileOrder` — pure and unit-tested — and Win32 in
  `WorkspaceManager.Tiling.cs`.
- Never position a tile outside its monitor's work area; hide it instead (§2.1).
- New window-event reactions must queue work for the reflow timer rather than lay out in the callback,
  and must never change membership.
- Every layout goes through `LayoutWorkspace`, which records `_arranged` — the source for "is this tile
  in view" and "was this tile moved".
