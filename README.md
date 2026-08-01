# Picky

A Screenpresso-style screen-capture tool styled after Windows PowerToys: Fluent dark theme,
rounded corners, a configurable accent colour, and a system-tray workflow.

Screenshots and screen recordings, annotation, and a gallery you can drag files out of —
built to behave correctly on multi-monitor, mixed-DPI desktops.

## Features

### Capture
- **Region snip** — the desktop is frozen first, then you select on the still image, so open
  menus and tooltips can be captured. Hover a window to auto-detect its bounds and single-click
  to grab it, or drag a freeform marquee.
- **Pixel loupe** — a square magnifier follows the cursor showing 15×15 source pixels at 9× with
  a grid line on every pixel boundary, a crosshair, a marker on the exact pixel under the cursor,
  and a live size / coordinate readout.
- **Whole screen** or **all screens**, plus a per-display submenu in the tray when more than one
  monitor is attached.
- **Auto-save** — every capture is written as a timestamped PNG into the chosen folder, then the
  gallery pops up docked in the corner with the new item selected.

### Recording
- **MP4 screen recording** of a dragged region or a whole display, via a bundled ffmpeg
  (`gdigrab` → H.264, 15 fps, CRF 30).
- **Breathing frame** — a red border pulses around the area being recorded. It is drawn in the
  pixels *outside* the recorded rect, so it never appears in the video.
- A floating bar shows elapsed time with a Stop button; finishing a recording opens the gallery
  the same way a screenshot does.

### Gallery
- Thumbnail cards (newest first) for PNG/JPG screenshots **and** MP4 recordings; clips get a
  poster frame extracted with ffmpeg, a play badge, and their duration.
- **Drag files out** — select cards and drag them into Slack, a browser, an email, Explorer,
  anything that accepts dropped files, exactly as if dragging from a folder.
- Multi-select via Ctrl/Shift-click or a rubber-band marquee, reading-order arrow-key navigation,
  delete to the Recycle Bin, rename, and copy-path (Ctrl+C).
- Screenshot and Record buttons on the toolbar; right-click either one to choose the mode
  (region / this screen / all screens).
- Double-click opens images in the annotation editor and videos in your default player.

### Annotation editor
- **Arrow**, **Rectangle**, **Text** and **Select** tools, an 8-colour palette, stroke thickness,
  and a font picker for text.
- Objects stay editable after placement: move, resize from corner handles, rotate, drag arrow
  endpoints, multi-select with a marquee, delete, undo.
- **Hold Shift to constrain angles** to 0/45/90/135° — while drawing an arrow, while dragging an
  arrow endpoint, or while rotating any object (the object's absolute orientation snaps, not the
  drag delta). Shift also makes a resize uniform.
- Selection outlines are drawn as black-and-white "marching ants" and handles have dark edges, so
  they stay visible over any screenshot regardless of the accent colour.
- Copy to clipboard, Open folder, and Save As PNG render at full resolution.

### Preferences
- Capture folder (persisted to `%APPDATA%\Picky\settings.json`; defaults to `Pictures\Picky`).
- Global shortcut, chosen from **Win+Shift+S**, **Ctrl+Shift+S**, **PrtScn** or **Ctrl+Shift+1**.
  Note Windows reserves Win+Shift+S for its own Snipping Tool — Picky reports when a combo can't
  be claimed rather than silently downgrading. PrtScn also can't be claimed via `RegisterHotKey`,
  so it falls back to a low-level keyboard hook.
- Accent colour (applied live across the whole UI) and default pen colour.
- **Start with Windows** — registers under the per-user `HKCU\…\CurrentVersion\Run` key, so no
  elevation is needed. The registry is treated as the source of truth, so removing the entry from
  Task Manager's Startup tab is reflected back in the checkbox.

### Multi-monitor / mixed DPI
Capture geometry is handled entirely in physical pixels obtained from Win32, because
`SystemParameters.VirtualScreen*` and WinForms' `Screen.Bounds` divide by a *single* DPI and so
return inconsistent numbers on a mixed-DPI desktop. The snip overlay is sized and positioned with
`SetWindowPos` rather than WPF's DIP-based `Left`/`Top`, and selection coordinates are derived from
the ratio between the frozen bitmap and the rendered canvas — correct for every monitor at once.
Window auto-detect uses `DWMWA_EXTENDED_FRAME_BOUNDS` so the invisible resize border (~7px at 100%,
12px at 175%) isn't included. Popups dock to the monitor under the cursor, not always the primary.

## Requirements
- .NET 8 SDK (the Windows Desktop workload is included in the Windows SDK install).
- Windows 10 20H1 (build 19041)+ for the Mica / rounded-corner DWM attributes.
- `ffmpeg.exe` in `src/Picky/tools/` for recording and video thumbnails. It is git-ignored —
  fetch it separately. Without it, screenshots still work; recording does not.

Built and run on .NET SDK 8.0.423. Builds clean apart from one benign `WFAC010` DPI-manifest
warning.

## Run
```
cd src/Picky
dotnet run
```

## Diagnostics
```
Picky.exe --probe <folder>
```
Writes the detected display layout, a capture of the whole virtual desktop and of each display, and
a window-to-display map — all in physical pixels, with the mean brightness of each grab. Reporting
brightness next to the expected geometry separates the two failure modes that look alike: a
wrong-sized image (a coordinate bug) versus a correctly-sized but black one (nothing was being
rendered, e.g. a sleeping display).

```
Picky.exe --emit-icon <path>
```
Writes the app `.ico` (build-time helper).

## Notes / limitations
- `CaptureAllScreens` produces one flat image spanning the virtual desktop's bounding box, so on a
  ragged layout the areas no monitor covers come out black. Capture a single display to avoid that.
- Recording uses GDI (`gdigrab`); a display that is asleep captures as black.
- The recording frame is drawn outside the recorded rect, so for a whole-display recording it falls
  outside that display and is effectively invisible.
- No scrolling capture, and no video playback inside Picky (clips open in the default player).
