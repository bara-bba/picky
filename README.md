# Picky

A Screenpresso-style screen-capture tool styled after Windows PowerToys: Fluent dark
theme, rounded corners, Fluent-blue accent (`#0078D4`), and a system-tray workflow.

## Features
- **Tray-resident**: lives in the notification area with a drawn viewfinder icon.
  Left-click the tray icon to open the gallery; right-click for the full menu
  (capture, gallery, control panel, exit). Closing the control panel hides it to the
  tray instead of quitting.
- **Global shortcut**: a selectable system-wide hotkey triggers a region capture from
  anywhere. Default is **Win+Shift+S**, but that combo is reserved by the Windows
  Snipping Tool, so Picky automatically falls back to **Ctrl+Shift+S** and says so.
  Pick from Win+Shift+S / Ctrl+Shift+S / PrtScn / Ctrl+Shift+1 in the control panel.
  Implemented with `RegisterHotKey` against a hidden message window (`HotKeyService.cs`).
- **Region capture** via a drag-select full-screen overlay (Win+Shift+S-style crosshair).
- **Full-screen capture** (hides the app's own windows before grabbing).
- **Auto-save**: every capture is written as a timestamped PNG into the chosen folder
  (Screenpresso-style), then shown in a preview.
- **Gallery** (`GalleryWindow`): a light-box of past screenshots as thumbnail cards
  (newest first) with filename + timestamp; click a card to reopen it in the preview.
- **Choosable capture folder**: pick it from the control panel ("Capture folder…") or
  the gallery ("Change folder…"). Persisted to `%APPDATA%\Picky\settings.json`;
  defaults to `Pictures\Picky`.
- **Preview window** with Copy-to-clipboard, Open-folder (selects the file in Explorer),
  and Save-As-PNG.
- **PowerToys-matching look**: a shared Fluent dark theme (`Theme.xaml`) with named
  brushes and accent/subtle/icon button styles (proper hover + pressed states), plus
  immersive dark mode + rounded corners applied via `DwmSetWindowAttribute`
  (see `Native/DwmHelper.cs`) — the same DWM attributes PowerToys' WinUI 3 utilities use.

## Not yet implemented
- Annotation tools (arrows, text, blur, highlight) — Screenpresso's core differentiator.
  `PreviewWindow` is the natural place to add an `InkCanvas`/shape-drawing layer.
- Scrolling capture, video/GIF recording.

## Requirements
- .NET 8 SDK with the Windows Desktop workload (`dotnet workload list` should show
  `Microsoft.NET.Sdk.WindowsDesktop` available, or just install the .NET 8 SDK on
  Windows, which includes it).
- Windows 10 20H1 (build 19041)+ for the Mica/rounded-corner DWM attributes.

**Build & run verified** on .NET SDK 8.0.423 (Windows 10 19045): builds clean (one
benign `WFAC010` DPI-manifest warning) and runs — main window, tray icon, capture,
auto-save, gallery, and folder picker all exercised end-to-end.

## Run
```
cd src/Picky
dotnet run
```
