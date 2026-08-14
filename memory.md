---
tags:
  - tool
---

# screenpresso-powertoys-style — session memory

## Snapshot — 2026-07-28
**Goal:** Rework the ScreenSnap capture flow to be tray-only + instant snip, and turn the post-capture gallery into a Screenpresso-style docked popup with real multi-select, keyboard nav, and delete.

**Replayable steps:**
1. Made the app tray-resident with no startup GUI — in `App.xaml.cs` `OnStartup`, removed `_mainWindow.Show()` (still construct `MainWindow` so tray/hotkey references work; control panel opens on demand from the tray).
2. Kept **Win+Shift+S** as the hotkey with no Ctrl fallback — simplified `App.InitHotKey()` to just `ApplyHotKey(preferred, persist:false)`; the status line reports if Windows blocks the combo instead of silently downgrading.
3. Post-capture now pops the gallery docked bottom-right instead of a centered PreviewWindow — in `CaptureController.ShowCapture`, replaced `new PreviewWindow(...).Show()` with `((App)Application.Current).ShowGalleryDocked()`.
4. Added `ShowGalleryDocked()` + `DockLowerRight(Window)` in `App.xaml.cs` (positions via `SystemParameters.WorkArea`, 12px margin, set BEFORE `Show()` to avoid a center→corner flash). Refactored `ShowGallery()` into `ShowGallery(bool dockLowerRight)`; sets `_gallery.AutoCloseOnDeactivate = dockLowerRight`.
5. Click-outside-dismiss — `GalleryWindow` got a public `AutoCloseOnDeactivate` bool and `OnDeactivated` override that calls `Close()` when true (docked popup only; tray-opened gallery stays put).
6. Converted the thumbnail `ItemsControl` (of Buttons) to a `ListBox` with `SelectionMode="Extended"`, `WrapPanel` ItemsPanel, and a custom `ItemContainerStyle` (accent border on `IsSelected`, CardHover on hover). Gives Shift+click / Ctrl+click / arrow selection for free.
7. Rubber-band marquee drag-select — added an overlay `Canvas` (`IsHitTestVisible="False"`, `ClipToBounds="True"`) with a `Rectangle`; handlers `PreviewMouseLeftButtonDown/Move/Up` on the ListBox. Skip marquee if the hit-tested `OriginalSource` is inside a `ListBoxItem` or `ScrollBar`. Ctrl+drag adds to existing selection (`_preDragSelection` snapshot). Intersection via `container.TransformToAncestor(ThumbnailList)`.
8. Double-click opens preview — `MouseDoubleClick="Thumbnail_DoubleClick"` walks up to the `ListBoxItem` under the cursor, opens `PreviewWindow.FromFile(item.Path)`.
9. Delete/Canc key removes selected — `PreviewKeyDown` → `DeleteSelected()` sends each selected file to the **Recycle Bin** via `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`, then `Refresh()`.
10. **Linear (reading-order) arrow navigation** — overrode Left/Right/Up/Down in `PreviewKeyDown` (`e.Handled = true` to defeat WPF's column-based nav). Left/Right = flat index ±1 (wraps across rows), Up/Down = ±columns. `ColumnsPerRow()` counts top-row cards by comparing `TransformToAncestor` Y positions. Tracks `_currentIndex` (moving end) + `_selectionAnchor` (fixed end); `SelectLinearRange` selects `[anchor..target]`. `_syncingSelection` guard stops `SelectionChanged` from resetting the anchor during programmatic range changes; a genuine single mouse-select resets anchor to the clicked card.

**Key files:**
- `src/ScreenSnap/App.xaml.cs` — startup (tray-only), hotkey init, `ShowGallery(bool)`, `ShowGalleryDocked`, `DockLowerRight`.
- `src/ScreenSnap/CaptureController.cs` — `ShowCapture` now routes to docked gallery.
- `src/ScreenSnap/GalleryWindow.xaml` / `.xaml.cs` — ListBox multi-select, marquee overlay, keyboard nav, delete, auto-close.

**Commands:**
- Build: `cd src/ScreenSnap && dotnet build -v q`
- Run: launch `src/ScreenSnap/bin/Debug/net8.0-windows10.0.19041.0/ScreenSnap.exe`
- Kill before rebuild (exe lock): PowerShell `Get-Process ScreenSnap | Stop-Process -Force`

**Gotchas / notes:**
- A running ScreenSnap instance locks `ScreenSnap.exe` → build fails with MSB3027 "file is locked". Always kill the process before `dotnet build`.
- `Point`/`Size` are ambiguous in `GalleryWindow.xaml.cs` (ImplicitUsings pulls in `System.Drawing`). Added `using Point = System.Windows.Point;` and `using Size = System.Windows.Size;`.
- `Microsoft.VisualBasic.FileIO` resolves with no extra package reference (it's in the Windows Desktop shared framework).
- WrapPanel as ListBox ItemsPanel disables virtualization → all containers are realized, which is what makes marquee-intersection and `ColumnsPerRow()` reliable.
- Only the benign `WFAC010` DPI-manifest warning remains on a clean build.
- Open question flagged to user: if Windows' own Win+Shift+S (Snipping Tool) is enabled, the OS may grab the combo before ScreenSnap's `RegisterHotKey` and the overlay won't fire — may need a reclaim strategy.
- Selection layout-shift fix: the `IsSelected` trigger originally bumped `BorderThickness` 1→2, which nudged the thumbnail content by 1px on select. Fix = keep thickness constant (1px) and change only `BorderBrush` (Stroke → Accent) in `GalleryWindow.xaml`. Rule of thumb: never change border thickness on a state trigger if the content shouldn't move — swap color only.

## Snapshot — 2026-07-28 (follow-up polish)
**Goal:** Fine-tune the docked gallery: flush-to-corner placement and correct row-wise Shift+Arrow selection.

**Replayable steps:**
1. Flush lower-right placement — in `App.DockLowerRight`, removed the 12px margin so `Left = work.Right - Width` and `Top = work.Bottom - Height` (popup sits flush against the work-area corner, above the taskbar).
2. Row-growing Shift+Up fix — in `GalleryWindow.ThumbnailList_SelectionChanged`, generalized anchor seating from "only when 1 item selected" to any mouse/marquee selection: compute min/max selected index, set `_selectionAnchor = max` (block bottom, fixed end) and `_currentIndex = min` (block top, moving end). Now Shift+Up moves the top edge up by `cols` and pulls in the whole row above (rows 1+2 together); guarded by `_syncingSelection` so keyboard range-selects don't re-seat the anchor mid-sequence.

**Key files:** `App.xaml.cs` (`DockLowerRight`), `GalleryWindow.xaml.cs` (`ThumbnailList_SelectionChanged`).

**Gotchas / notes:**
- Single-anchor limitation (documented to user): right after a marquee row-select the anchor is pinned to the block's bottom, so a *first* Shift+Down moves the top edge instead of extending below; it behaves consistently once Shift+Up/Down is pressed once. Alternative "always grow from the pushed edge" model was offered but not implemented.
- `SelectedItems` holds the same `CaptureItem` instances as `_items`, so `_items.IndexOf((CaptureItem)obj)` maps selection → index by reference equality.

## Snapshot — 2026-07-28 (tray popup + hook note)
**Goal:** Make the tray-icon left-click open the gallery as the same lower-right docked popup as after a capture.

**Replayable steps:**
1. In `App.CreateTrayIcon`, changed the `icon.MouseClick` left-button handler from `ShowGallery()` to `ShowGalleryDocked()` — tray click now pops the gallery flush in the lower-right corner and dismisses on click-away (same `AutoCloseOnDeactivate` path as post-capture). Tray menu "Open Gallery" still uses the plain centered `ShowGallery()`.

**Key files:** `App.xaml.cs` (`CreateTrayIcon` mouse-click handler).

**Notes / open items:**
- Possible foreground-activation risk on tray-click popups (Windows foreground rules): if the docked gallery ever flashes and closes instantly, add a force-foreground on show. Not observed yet — flagged to user to confirm.
- Shift+Up row-select still reported "not as expected" by user; asked for their exact repro (marquee vs click selection) before changing the single-anchor model. Unresolved.
- Hook behavior clarified to user: the `session-snapshot.ps1` Stop hook fires at the end of *every* assistant turn (not only real session end), which is why the "session is ending" prompt recurs mid-conversation. Offered to make it non-blocking or manual-only; no change made yet.

## Snapshot — 2026-07-28 (freeze-frame capture, hotkey hook, tray/prefs, annotation editor)
**Goal:** Big round of capture-UX work: window auto-detect + fullscreen fallback, Print Screen hotkey fix, freeze-frame capture for menus, tray/preferences rework, gallery copy-path, and an image annotation editor.

**Replayable steps:**
1. Removed the Stop hook entirely — deleted the `hooks` block from `C:\Users\loren\.claude\settings.json` (only `model`/`effortLevel`/`theme` remain). Snapshots now only via `/snap` or cron.
2. Region overlay window auto-detect — `RegionSelectWindow.xaml.cs`: on hover, `EnumWindows` (top Z-order first) finds the frontmost visible, non-minimized, non-cloaked (`DwmGetWindowAttribute` DWMWA_CLOAKED=14), non-tool-window under the cursor (`GetCursorPos`), highlights its rect; single-click grabs it, drag (>4px) does freeform. Also fixed a latent multi-monitor bug: manual-drag px conversion now adds the virtual-screen origin.
3. Single-click with no window detected → full screen of the monitor under the cursor (`Screen.FromPoint`).
4. Print Screen hotkey fix — `HotKeyService.cs`: `RegisterHotKey` can't claim PrtScn (Windows reserves it), so added a **WH_KEYBOARD_LL** fallback (`SetWindowsHookEx`) that matches the def's vk + modifier state (`GetAsyncKeyState`) and swallows the key. RegisterHotKey still used for combos that succeed; hook torn down on switch/dispose. No "Apply" button — hotkey applies on ComboBox selection.
5. Auto-select newest after capture — thread the saved path via `App.ShowGalleryDocked(string)` → `GalleryWindow.SelectByPath` (falls back to newest). `SelectFirst`/`SelectByPath`/`SelectIndex` refactor.
6. Freeze-frame capture (Snipping-Tool style) — `CaptureController.CaptureRegion()` (now parameterless): hide all visible app windows → `Dispatcher.Invoke(Render)` + 80ms sleep → `ScreenCapture.CaptureVirtualScreen(out bounds)` → show `RegionSelectWindow(ImageSource)` with the frozen bitmap as backdrop (`<Image Name="Frozen">` behind the Dim) → crop the selected px region from the frozen bitmap (offset by virtual-screen origin). Lets you capture open menus/tooltips.
7. Crash fix (capture with gallery+prefs open): reshow loop only calls `Show()` on windows still in `Application.Current.Windows` (a docked gallery auto-closes while hidden; Show() on a closed window throws).
8. Tray/Preferences rework — tray left-click = docked gallery, right-click = standard light `ContextMenuStrip` (Preferences / Exit). MainWindow retitled "Preferences", footer = Gallery / Close (Close hides to tray; Exit is in the tray menu). Gallery button + gallery ⚙ Preferences button both open docked. Gallery toolbar buttons Change/Open folder fixed to 118px.
9. Gallery crash fix + guards: docked gallery auto-close suppressed (`_suppressAutoClose`) while the folder picker OR a context menu is open (`ContextMenuOpening`/`Closed`).
10. Copy path — gallery right-click ContextMenu "Copy path" + **Ctrl+C**, copies selected `CaptureItem.Path`(s) newline-joined to clipboard (for pasting into the CLI, since image paste wasn't working). Right-click selects the card under cursor first.
11. Popup opens with a selection — `SelectFirst` on show so arrow keys work immediately.
12. **Annotation editor** in `PreviewWindow` (double-click a thumbnail): toolbar with Arrow / Rectangle / Text ToggleButtons (radio behavior), 6-color swatch palette, Undo. Arrow = `StreamGeometry` shaft+head Path; Rectangle = Shapes.Rectangle; Text = TextBox → commit to TextBlock (Enter/blur commit, Esc cancel). Copy/Save As render `EditorSurface` (Image+Canvas grid) via `RenderTargetBitmap` at dpi=96*(bitmapWidth/ActualWidth) → full-resolution PNG. Window set `ResizeMode=NoResize` to keep annotation coords aligned.

**Key files:** `RegionSelectWindow.xaml(.cs)` (auto-detect + frozen backdrop), `HotKeyService.cs` (LL-hook fallback), `CaptureController.cs` (freeze-frame), `ScreenCapture.cs` (`CaptureVirtualScreen`), `App.xaml.cs` (tray menu, ShowGalleryDocked overloads), `MainWindow.xaml(.cs)` (Preferences), `GalleryWindow.xaml(.cs)` (copy-path, guards, select-first), `PreviewWindow.xaml(.cs)` (annotation editor).

**Gotchas / notes:**
- `PreviewWindow.xaml.cs` mixes System.Drawing + WPF: aliased `Point`/`MediaColor`/`MediaBrush`/`MediaBrushes`, fully-qualified `System.Windows.Shapes.*` and `System.Windows.Media.*` to dodge `Rectangle`/`Color`/`Brush`/`Image` ambiguity. `ImageSource` is in `System.Windows.Media` (not Imaging).
- Freeze-frame trade-off: live window auto-highlight can't detect a frozen transient menu (it's not an enumerable window) — drag still selects it.
- Menu styling saga: user wanted native look → tried `ToolStripRenderMode.System` (looked "Windows 98") → rolled back to default light `ContextMenuStrip`.
- Dead code now: `CaptureController.CaptureFullScreen`, `TrayIcon.CreateDarkMenu` (both unused; left in place, flagged to user).
- Editor limits (v1): no move/edit-after-placement, no per-object delete (only Undo), fixed stroke/font size, original file unchanged until Save As.


## Snapshot — 2026-07-31 (multi-monitor / mixed-DPI capture)

**Goal:** "With multiple screens the instant capture of the screens is faulted." Fix multi-monitor capture.

**Test rig (worst case, worth remembering):** 3 displays, mixed DPI, one rotated, negative origin.
`Display1` = internal 4K laptop panel 3840×2400 @175% at **X=-3840**; `Display2` (primary) 1920×1200 @100% at X=0; `Display3` = Dell U2421E rotated **portrait** 1200×1920 at X=1920. VirtualScreen = `{-3840,0,6960,2400}`. Hybrid GPU: NVIDIA RTX 4000 Ada + Intel Iris Xe.

**Root cause #1 — coordinate space (FIXED, verified).**
`SystemParameters.VirtualScreen*` and WinForms `Screen.Bounds` divide by a *single* DPI, so on mixed-DPI they return internally inconsistent numbers (positions in real px, sizes scaled). A DPI-unaware process saw the desktop as `6960×1920` and Display1 as `2194×1371`; reality is `6960×2400` / `3840×2400`. The overlay was therefore sized from a desktop 480px shorter than actual, and Display1 coords were off by 1.75×.

**Replayable steps:**
1. Added `Native/MonitorInfo.cs` — physical-pixel truth via `GetSystemMetrics(SM_*VIRTUALSCREEN)` + `EnumDisplayMonitors`/`GetMonitorInfoW`. Monitors sorted L→R, 1-based `Index`, `Label`, `FromPoint`/`FromCursor`/`CursorPosition`. Correct only because the manifest is `PerMonitorV2`.
2. Rewrote `ScreenCapture.cs` — explicit `BitBlt` from `GetDC(NULL)` (spans the whole virtual desktop, negative coords fine) with `SRCCOPY|CAPTUREBLT` to include layered windows (menus/tooltips). **`Format32bppRgb`, not `...Argb`** — BitBlt never writes the alpha byte, so an Argb surface saves as a fully transparent PNG. Added `CaptureMonitor`, `CaptureMonitorUnderCursor`, and `ToImageSource` (moved here from CaptureController).
3. **Killed the per-monitor DPI scalar entirely** in `RegionSelectWindow`. Overlay is now positioned/sized with `SetWindowPos` in raw physical px from `OnSourceInitialized` (WPF `Left/Top/Width/Height` cannot express a mixed-DPI span). Canvas↔px mapping is the *ratio* `_canvasPx.Width / RootCanvas.ActualWidth` — exact for every monitor at once because the backdrop image is stretched to fill the window. Both corners mapped through the same transform so w/h can't drift a rounding step.
4. `RegionSelectWindow.xaml` → `Grid` of auto-stretching layers (no manual DIP sizing). **Dropped `AllowsTransparency`** — it forced software rendering on a 6960×2400 window; the frozen screenshot *is* the backdrop so transparency was never needed. Added an un-dimmed cut-out (`Bright` image clipped to the selection), a live `N × N px` readout, right-click-to-cancel, and hint text placed on the monitor **under the cursor** (was always the leftmost).
5. Overlay self-freezes when constructed with no backdrop, so `new RegionSelectWindow()` (the recording path) still works now that the window is opaque.
6. `CaptureController` — hides **all** Picky windows (was only `owner`, so the gallery got baked in); `CaptureRegion` / `CaptureCurrentScreen` / `CaptureScreen(monitor)` / `CaptureAllScreens`. Deleted dead `CaptureFullScreen` (it only ever grabbed `PrimaryScreen`).
7. `Native/WindowPlacement.cs` + `App.DockLowerRight` — popups dock to the work area of the monitor **under the cursor**. `SystemParameters.WorkArea` is primary-only, so the gallery used to jump to the primary display after capturing elsewhere. Two-pass: park on the target monitor so `GetDpiForWindow` reports its scale, then place flush.
8. Tray menu: Capture region / this screen / all screens + a per-display submenu rebuilt on `Opening` (monitors hot-plug). `RunAfterMenuClosed` defers capture ~180ms via `DispatcherTimer` — the tray menu is a **WinForms** popup, not a WPF `Window`, so `HideOwnWindows()` can't see it and it would otherwise appear in the shot.
9. Added `Diagnostics.cs` — `Picky.exe --probe <folder>` dumps layout + per-display captures + a window→display map with **mean brightness** per grab. Brightness is the key signal: correct-size-but-black separates a compositor problem from a coordinate problem.

**Verification:** all grabs match expected px exactly (`6960x2400`, `3840x2400`, `1920x1200`, `1200x1920`). Composite at offset 3840 vs standalone Display2 grab = **960/960 sampled pixels identical**, proving negative-origin blit + `crop.Offset(-bounds.X,…)` math.

**Root cause #2 — GDI can't read composited windows (OPEN, needs Desktop Duplication).**
On this machine GDI returns only the DWM base/wallpaper layer:
- Display2 → mean 78.2 but **wallpaper only**, no windows, despite a maximized Kiro + File Explorer genuinely on it.
- Display1 & Display3 → **mean 0.0 (pure black)** though they host a full-screen media player, Chrome, Slack, PowerShell.
`SRCCOPY` vs `SRCCOPY|CAPTUREBLT` vs `Graphics.CopyFromScreen` are byte-identical, so it is not a CAPTUREBLT issue — it is GDI as a whole. Confirmed the displays are live (the media player's window title changed between probe runs). The original code used `CopyFromScreen`, so it was equally affected — this is the real "faulted" report.
**Fix requires DXGI Desktop Duplication** (`IDXGIOutput1::DuplicateOutput`) or WinRT `Windows.Graphics.Capture`, reading the composed framebuffer from the GPU. Must enumerate adapters and match each output to its owning adapter (hybrid GPU), retry `AcquireNextFrame` (a static desktop yields `DXGI_ERROR_WAIT_TIMEOUT`), and keep GDI as fallback. Decision pending: hand-rolled COM interop (keeps the project's zero-NuGet posture) vs `Vortice.Windows`.

**Gotchas / notes:**
- `using System.Windows.Media` + implicit `System.Drawing` → **`PixelFormat` is ambiguous**; fully qualify `System.Drawing.Imaging.PixelFormat`. Same family as the existing `Point`/`Size` ambiguity.
- Window→display mapping is worthless unless **DWM-cloaked** windows are filtered (`DWMWA_CLOAKED`=14): a window on another virtual desktop still reports `IsWindowVisible` and non-iconic, which made the first map claim content on empty displays.
- `GetWindowRect`/`Screen.Bounds` from a DPI-unaware process (e.g. plain PowerShell) return **virtualized** coords — never trust them for capture geometry; query from the PMv2 process.
- `PrintWindow` P/Invoke needs `CharSet.Unicode` on `GetWindowTextW` or titles come back as a single character (ANSI marshalling of UTF-16).
- The virtual desktop is a bounding box: on a ragged layout the uncovered regions are black in `CaptureAllScreens` — inherent, use per-display capture to avoid it.
- Process name is now `Picky`; kill it before rebuilding (MSB3027 lock) — `Get-Process Picky | Stop-Process -Force`.
- No .NET SDK was installed on this host; used the official `dotnet-install.ps1` to put **SDK 8.0.423** in `%LocalAppData%\Microsoft\dotnet` (no admin, PATH untouched). Build/run needs `$env:DOTNET_ROOT` + that `dotnet.exe`.


## Snapshot — 2026-07-31 (CORRECTION to root cause #2, + video in gallery)

**⚠️ Retraction — "GDI can't read composited windows" was WRONG.**
The previous snapshot concluded GDI could only read the DWM wallpaper layer, and recommended a
DXGI Desktop Duplication rewrite. That conclusion does not hold. Re-measuring later the same
session, **all three displays returned real content** (Display1 mean 78.0, Display2 220.0,
Display3 54.7) through plain `BitBlt`. The earlier all-black readings were **transient: the
monitors were asleep** while the user was idle. GDI returns black for a DPMS-off display.

The faulty reasoning worth remembering: I treated the media player's *window title changing*
between probes as proof the display was live. Titles change because audio keeps playing with the
monitors asleep — it proves playback, not rendering. **A window title is not evidence of display
output.** Verify liveness by sampling the same region twice and confirming pixels change, or by
confirming a non-zero mean at a moment the user is known to be at the machine.
⇒ No Desktop Duplication work is required. GDI capture is fine on this hardware.

**gdigrab is NOT upside down (measured, not assumed).**
User reported recordings came out upside down. Tested with row-brightness-profile correlation
against a simultaneous `BitBlt` reference (robust to content drift, unlike pixel equality):

| region | asIs | flipped |
|---|---|---|
| Display2 primary (0,0 1920×1200) | **1.0000** | 0.1159 |
| Display3 portrait (1920,0 1200×1920) | **1.0000** | −0.2049 |
| Display1 4K @175% (−3840,0 3840×2400) | **1.0000** | −0.2843 |
| straddling two monitors (−400,100 1200×800) | **1.0000** | 0.1073 |

Full encode pipeline (the exact `RecordingController` args + `-t 2`) also correct: extracted
frame 1 vs live reference `asIs=0.9999`, `flipped=0.5330`, and no rotation/displaymatrix side
data in the MP4. Finally, decoded the user's **actual** clip (`Picky_20260731_145533.mp4`,
684×412, 5:48): first and middle frames are **visibly upright** (Italian Spotify UI reads
normally). So nothing in capture→encode flips.
Also learned: **gdigrab receives physical pixel coordinates correctly**, including negative
`offset_x` — it matched `BitBlt` at the same physical coords exactly. So there is no DPI bug in
the recording path either.
*Unresolved:* what the user actually saw. Their clip's content **is wrongly cropped** (text
truncated at the left: "giunto il giorno" should be "Aggiunto il giorno"), and it was recorded at
14:55 — *before* the coordinate fix landed. A region offset vertically from what was selected can
easily read as "wrong". Asked the user to re-record on the fixed build and to say which player
they viewed it in.

**Replayable steps — video in the gallery:**
1. Added `VideoThumbnailer.cs` — poster frame + duration via the bundled ffmpeg, cached in
   `%LocalAppData%\Picky\thumbnails` as `<name>-<length>-<writeTicks:X>.png` plus a `.meta`
   sidecar holding the duration, so a cache hit needs **no** ffmpeg call at all. Cache is
   deliberately **outside the capture folder** — the gallery enumerates that folder, so
   thumbnails written there would appear as captures. `SemaphoreSlim(2)` caps concurrent ffmpeg
   spawns. Seeks to `min(1.0, duration*0.1)` because frame 0 of a screen recording is often a
   blank first paint. `scale=480:-2` keeps aspect with an even height.
2. `CaptureItem` now implements `INotifyPropertyChanged` with `IsVideo`, settable `Thumbnail`
   and `DurationText`. Video posters load on a `Task.Run` and marshal back via the dispatcher, so
   a folder of clips doesn't freeze the gallery. `BitmapImage.Freeze()` is what makes handing it
   across threads legal. `LoadBitmap` now try/catches so a truncated file yields a card with no
   preview instead of throwing.
3. `Refresh()` enumerates `CaptureItem.SupportedPatterns` = png/jpg/jpeg/mp4, de-duplicated by
   full path — on Windows `*.jpg` can also match `.jpeg` through 8.3 short names.
4. `GalleryWindow.xaml`: item template thumbnail area became a `Grid` with a centred circular
   play badge and a bottom-right duration chip, both gated on `IsVideo` via a
   `BooleanToVisibilityConverter` registered in `Window.Resources`.
5. Double-click routes on type: images → `PreviewWindow` (the annotator), videos →
   `Process.Start(UseShellExecute)` so the default player handles them. `PreviewWindow` is an
   image editor and cannot display video.

**Verified:** build clean (only the pre-existing `WFAC010`). Replayed the thumbnailer's exact
ffmpeg invocations against the user's real clip: duration regex → `5:48`, poster → 480×290 PNG,
cache key well-formed. The rendered card itself still needs a visual confirmation from the user.

**Notes / open items:**
- Recording is still `gdigrab`; a clip of a sleeping display will be black for the same DPMS
  reason. Not a bug to fix in code.
- No video playback inside Picky (no `MediaElement` preview) — external player only. Could add a
  `MediaElement`-based preview later if wanted.


## Snapshot — 2026-07-31 (drag captures out of the gallery)

**Goal:** "Lemme drag images in gallery that have been selected as if a folder." — make the gallery
a drag source so selected captures can be dropped into any app that accepts files.

**Replayable steps:** all in `GalleryWindow.xaml.cs`.
1. New state: `_dragStart`, `_dragArmed`, `_dragging`, `_pendingSelectionCollapse`.
2. `ThumbnailList_PreviewMouseLeftButtonDown` — pressing a card now *arms* a drag
   (`_dragArmed`) and returns, leaving selection to the ListBox so Ctrl/Shift-click still work.
   The empty-space path (marquee) is unchanged; the scrollbar check was split out of the old
   combined early-return.
3. `ThumbnailList_PreviewMouseMove` — drag-out is checked **before** marquee logic; once the
   pointer clears `SystemParameters.Minimum{Horizontal,Vertical}DragDistance`, calls
   `StartFileDrag()`.
4. `StartFileDrag()` — builds `DataObject(DataFormats.FileDrop, string[])` (+ `Text` with the
   newline-joined paths) and calls `DragDrop.DoDragDrop(..., DragDropEffects.Copy)`.
5. `ThumbnailList_PreviewMouseLeftButtonUp` — now always clears `_dragArmed` and applies the
   deferred selection collapse before the pre-existing marquee teardown.
6. `OnDeactivated` gained `&& !_dragging`.

**The non-obvious bit — preserving a multi-selection.**
WPF's `ListBox` collapses a multi-selection to the single pressed item on **mouse-down**, so a
naive implementation can never drag more than one file: by the time the drag starts,
`SelectedItems` holds one entry. Explorer defers that collapse to **mouse-up**. Replicated by
swallowing the press (`e.Handled = true`) only when *all* of: no Ctrl/Shift modifier, the pressed
card `IsSelected`, `SelectedItems.Count > 1`, and `e.ClickCount == 1`. The pressed card is stashed
in `_pendingSelectionCollapse`; mouse-up collapses to it, while `StartFileDrag()` nulls it so a
real drag keeps the whole selection. The `ClickCount == 1` guard matters: handling mouse-down
stops the bubbling event reaching the ListBox, which would otherwise kill `MouseDoubleClick`
(double-click-to-open) whenever several cards were selected.

**Other decisions:**
- `DragDropEffects.Copy` **only** — allowing `Move` would let a drop target relocate the original
  file out of the capture folder and silently empty the gallery.
- `_dragging` guards `OnDeactivated`: the docked post-capture gallery auto-closes on deactivate,
  and dragging into another app deactivates it, which would abort the drop mid-flight. The window
  is intentionally left open after the drop (Explorer-like) rather than force-closed.
- Marquee state and mouse capture are cleared *before* `DoDragDrop`, since that call pumps a
  nested message loop — otherwise the rubber-band rectangle can be left painted on screen with
  the mouse still captured.
- Videos drag too; it's a file drop, so restricting it to images would be arbitrary.

**Verified:**
- Payload contract checked against a real `DataObject`: advertises `Text, UnicodeText,
  System.String, FileDrop, FileNameW, FileName` — the same set Explorer publishes — and
  `GetFileDropList()` round-tripped all 3 paths. So any Explorer-compatible drop target accepts it.
- Gallery confirmed to actually load and render: added a **temporary** `--gallery` startup flag,
  screenshotted the window, saw the `.mp4` card with poster frame, play badge and `5:48` chip, then
  removed the flag and rebuilt (0 references left). Worth repeating: a clean build does **not**
  prove a WPF window loads — `StaticResource` lookups and template errors only throw when the
  window is first shown, and `GalleryWindow` is constructed lazily on first open.
- Video thumbnail cache populated by the real app run (`...png` 12,951 bytes + `.meta`), proving
  `VideoThumbnailer` works end-to-end and not just in a replayed ffmpeg command.

**Not done / possible follow-ups:** no custom drag ghost image (WPF shows no preview by default —
would need an adorner); no drag *into* the gallery to import files.


## Snapshot — 2026-07-31 (auto-detect too wide; Shift = 45° snapping)

**Confirmed:** drag-out of selected captures works.

### Bug — click-to-grab captured a rectangle wider/taller than the window

User's `Picky_20260731_155905.png` was **1174×1159**; the window (Chrome) is really **1160×1152**.
Cause: `GetWindowRect` reports the window **including its invisible DWM resize border**. Measured
insets on this machine:

| window | GetWindowRect | visible (DWM) | insets |
|---|---|---|---|
| Chrome / PowerShell / File Explorer | 1174×1159 | 1160×1152 | L7 T0 R7 B7 |
| maximised Explorer on the 175% panel | 3864×2340 | 3840×2316 | L12 T12 R12 B12 |
| Kiro, Screenpresso, Program Manager | — | same | L0 T0 R0 B0 |

So the overshoot is ~14px wide / 7px tall at 100%, **scales with DPI**, and there is no inset on
the top edge. The padding contains whatever sat *behind* the window, which is why it reads as a
stray strip rather than obvious blank space.

**Fix:** use `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS /* 9 */, out RECT, …)`, which
returns the frame the user actually sees, falling back to `GetWindowRect` for windows DWM doesn't
composite (some console/legacy windows report inset 0 anyway, so the fallback is a safe no-op).
Note the DWM call needs a **second `DllImport` overload taking `out RECT`** alongside the existing
`out int` one used for `DWMWA_CLOAKED`.

**Refactor:** window interop was duplicated between `RegionSelectWindow` and `Diagnostics` (both had
their own `IsCloaked`, `EnumWindows`, `GetWindowRect`, `RECT`). Consolidated into
`Native/WindowInfo.cs`: `TryVisibleBounds`, `TryOuterBounds`, `IsCloaked`, `IsToolWindow`,
`GetTitle`, `ForEachTopLevel`, `IsVisible`, `IsMinimised` — all returning `Rectangle` in physical px.
`RegionSelectWindow.TryWindowUnderPoint` now yields a `Rectangle` and its whole Win32 block shrank
to just `SetWindowPos`. `--probe` reports **both** visible and outer bounds plus the computed inset,
through the *same* helper the capture uses, so the diagnostic can't drift from real behaviour.

### Feature — Shift constrains angles to 45/90/135/…

`PreviewWindow` already had a full manipulation layer (`Tool.Select`, `Manip.{Resize,Rotate,Endpoint}`,
rotate handle, marquee, multi-select) — **`memory.md` was out of date** in claiming "no
move/edit-after-placement". Check the code, not these notes.

Added `SnapAngleDegrees = 45`, `ShiftHeld`, and `SnapToAngle(from, to)` (projects onto the nearest
45° ray, preserving length). Wired into three places:
1. **Drawing an arrow** — `Canvas_MouseMove`: `BuildArrow(_start, ShiftHeld ? SnapToAngle(...) : p, …)`.
2. **Dragging an arrow endpoint** — `ApplyEndpoint`: snaps relative to the *other* endpoint as pivot.
3. **Rotating** — `ApplyRotate`: snaps the object's **absolute** orientation, not the drag delta.

That third point is the subtle one. Snapping the delta would leave a shape that started at 7°
sitting at 52°/97°/… The absolute angle is recovered from the start matrix via
`Math.Atan2(_m0.M12, _m0.M11)`, rounded to a 45° multiple, and the *difference* applied:
```
baseDeg = atan2(_m0.M12, _m0.M11)·180/π
target  = round((baseDeg + deltaDeg)/45)·45
deltaDeg = target − baseDeg
```
(Non-uniform scale in the matrix would skew that extracted angle slightly; acceptable here.)

**Bug caught in my own change:** `Canvas_MouseLeftButtonUp` committed `_arrowPoints[path] = (_start, end)`
using the **raw** mouse-up point. Since those stored endpoints drive the endpoint handles and any
later re-render, a Shift-drawn arrow would visually jump off its clean angle the instant it was
selected. Commit now snaps too and re-runs `BuildArrow`. Lesson: when a gesture transforms input,
snap/transform at **every** place the value is persisted, not just where it's previewed.

**Verified:**
- Border fix through the real code path (`--probe`): Chrome now `visible = 1160x1152` vs
  `outer = 1174x1159` — exactly the user's wrong file size — and 12px insets handled on the 175% panel.
- Snapping math by compiling the **verbatim** `SnapToAngle` method and asserting over 8 cases:
  result angle is an exact multiple of 45 (<1e-4), length preserved (<1e-4), and the zero-length
  degenerate case returns as-is without NaN. Rotation formula checked separately over 6
  start-angle/delta pairs.
- Build clean (only pre-existing `WFAC010`).

**Deliberately not done:** Shift-drag to force a **square** while drawing a rectangle. It's the
conventional companion to angle snapping, but the request was specifically about angles, so it was
left out rather than silently widening scope. `ApplyResize` already uses Shift for uniform scaling.


## Snapshot — 2026-07-31 (startup entry, visible selection outline, gallery toolbar + capture buttons)

### Root cause worth remembering: accent changes don't reach `StaticResource`

`AccentTheme.SetBrush` does `Application.Current.Resources[key] = new SolidColorBrush(c)` — it
**replaces** the entry. Anything bound with `{StaticResource Brush.Accent}` resolved once at load and
keeps Theme.xaml's original `#FF0078D4`. With the user's accent set to `#FFFFFF`, that's why the
gallery's selected-card border and marquee were still **blue** while everything else went white.
Fixed by switching those to `{DynamicResource Brush.Accent}`. **Rule: never use StaticResource for
accent brushes.** The hardcoded marquee `Fill="#330078D4"` is now built in code from
`AccentTheme.Current` with `A = 0x33`.

### Editor: selection outline / handles were invisible

Ask was to draw the outline "in the negative of the pixels". WPF shapes have **no
difference/invert blend mode** (that's UWP/Win2D), so used the standard stand-in: two-tone
**marching ants** — `CreateAntsPair()` returns a solid **white** polygon (under) plus a **black
dashed** one (over, dash `{3,3}`), so the gaps reveal white and the edge reads on any background.
Both layers must be given **identical `Points`** or the pattern stops registering. Applied to
`_outline`/`_outlineUnder` and to each multi-selection outline (both layers go into `_multiOutlines`
so `HideOverlay` still cleans up).

The handles had the same disease and it was worse: corner and endpoint handles were
`Fill = White, Stroke = accent`, and the rotate handle `Fill = accent, Stroke = White` — with a white
accent they were **white-on-white**. All three now use black strokes. Lesson: overlay chrome must
not depend on a user-chosen colour for its contrast.

Swatch selection rings (`PreviewWindow.HighlightSwatch`, `MainWindow.HighlightPenColor`) were
hardcoded `White` / `Brush.TextPrimary`; both now read `Brush.Accent` live from resources.

### Preferences: Start with Windows

`Native/StartupRegistration.cs` → per-user `HKCU\…\CurrentVersion\Run`, value `Picky`, command is the
**quoted** `Environment.ProcessPath` (the path contains spaces). Per-user deliberately: HKLM or a
scheduled task needs elevation. **The registry is the source of truth, not settings.json** — the user
can delete the entry from Task Manager's Startup tab without Picky knowing, so
`MainWindow.InitStartWithWindows` reads the registry, seeds the checkbox from it, and writes any
drift back to settings. `IsEnabled()` also returns false when the stored command points at a
*different* exe, so a moved or published build correctly shows unticked. If the registry write fails
the checkbox is reverted rather than left asserting something untrue.
New `DarkCheckBox` style in Theme.xaml (accent fill + `Brush.OnAccent` tick, both DynamicResource).

### Gallery toolbar

Reordered to actions-left / path-right, and added **Screenshot** and **Record** buttons whose
`ContextMenu` picks the mode (region / this screen / all screens; region / this screen for record).
Left-click takes the common default (drag a region); `Record` toggles to `⏹ Stop` while recording via
`UpdateRecordButton()` called from `Refresh()`.

`App.StartRecording` was region-only; now `internal void StartRecording(Rectangle? region = null)` —
pass a rect to record it directly, null to drag one. Added `IsRecording` and `StopRecordingFromUi()`
for the gallery. `ToggleRecording()` still works via the default argument.

`RunHidden(action)` hides the gallery, waits 180ms on a `DispatcherTimer`, then acts — otherwise the
gallery is in its own screenshot/recording. It deliberately does **not** re-show: a capture reopens
the gallery with the new item, and a recording must stay out of frame.
Toolbar `ContextMenu`s got `Opened="ContextMenu_Opened"` (sets `_suppressAutoClose`) because a menu
taking focus would otherwise auto-close the docked gallery and take the menu with it.

**Verified:**
- `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)` to render each window unoccluded — plain BitBlt kept
  catching whatever was on top, and `SetForegroundWindow` from another process is blocked by the
  foreground lock. Good technique for screenshotting a specific window regardless of Z-order.
- Startup registration exercised through the real code: initial → enable → verify → disable → verify
  → restore. Confirmed the entry in HKCU Run.
- Build back to the single pre-existing `WFAC010` warning.

**Caught in my own change:** `private App Owner => …` in `GalleryWindow` triggered **CS0108** — it
hid `Window.Owner`, which WPF uses for dialog ownership. Renamed to `PickyApp`. Watch for accidental
shadowing when adding convenience properties to a `Window` subclass.

**Not done:** the pen-colour palettes in Preferences and the editor are still two separate hardcoded
hex lists (`MainWindow.BuildPenColors` / `PreviewWindow.BuildSwatches`) that must be kept in sync.


## Snapshot — 2026-08-04 (Ctrl+W to close editor; verified Esc already cancels capture/record)

**Ask:** "add features to ctrl+w editor images and to press esc while trying to execute a screenshot
or screen record to exit without doing anything."

**Ctrl+W (new)** — `PreviewWindow.OnKeyDown`: added an `else if (ctrl && e.Key == Key.W)` branch
alongside the existing Ctrl+Z/S/C handlers, guarded by the same `!typing` check so it doesn't fire
while a text annotation box has focus. Calls `Close()` — the *same* path as the title-bar close
button — so `OnClosing`'s unsaved-changes prompt (Save / Save a copy… / Don't save / Cancel) still
runs; Ctrl+W does not bypass it.

**Esc-to-cancel (investigated, already correct, no change needed):** Both the screenshot region-pick
and the screen-record region-pick reuse the same `RegionSelectWindow`, which already has
`KeyDown="Window_KeyDown"` → `Cancel()` → `DialogResult = false` (mirrors right-click-to-cancel).
Traced both call sites to confirm the `false` result is honoured:
- `CaptureController.CaptureRegion()`: `accepted = overlay.ShowDialog() == true; ... if (!accepted) return;` — returns before cropping or saving anything.
- `App.StartRecording(region: null)`: `if (overlay.ShowDialog() != true) return;` — returns before
  `_recorder.Start(...)` is ever called, so ffmpeg never launches.
So pressing Esc on the overlay already aborts cleanly for both flows with zero side effects. Nothing
to fix — this was a verification task, not a bug.

**Verified:** `dotnet build -v q` clean (only the pre-existing `WFAC010` warning). Needed
`$env:DOTNET_ROOT = "$env:LocalAppData\Microsoft\dotnet"` + prepend to PATH first — no system-wide
SDK on this host (see the 2026-07-31 multi-monitor snapshot for why). Did not runtime-test Ctrl+W by
launching the app in this session (no running Picky instance / display interaction available) —
logic mirrors the already-working Ctrl+Z/S/C branches exactly, same `!typing` guard, same
`Close()` call the title-bar X uses.


## Snapshot — 2026-07-31 (on-screen frame around the recorded region)

**Goal:** show a border marking what's being recorded.

**The catch that shapes the design:** the recorder is `gdigrab`, i.e. it grabs the screen. Anything
painted *over* the region would be **baked into the video**. So the frame is drawn strictly in the
pixels *outside* the recorded rect.

**`RecordingBorder.cs`** — four thin (3px) opaque windows rather than one big outlined window:
```
top    (x-t, y-t, w+2t, t)      bottom (x-t, y+h, w+2t, t)
left   (x-t, y,   t,    h)      right  (x+w, y,   t,    h)
```
Top/bottom run the full inflated width so the corners are covered without a fifth piece.
Reasons for strips over a single window:
- Nothing overlaps the recorded area, so the frame provably can't be captured.
- A large `AllowsTransparency` window would sit over the recorded pixels, costing DWM compositing on
  exactly the area being captured every frame, and risking the grab picking up its blended pixels.
- Opaque strips need no `AllowsTransparency`, so no software-rendering penalty.

Each strip: `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` applied in `SourceInitialized`
so it's click-through and never steals activation from whatever is being recorded, and positioned by
`SetWindowPos` in physical px (WPF Left/Top can't express mixed-DPI positions — same reason as the
capture overlay). Colour `#E81123`, matching the palette red / record-bar dot; a red frame stays
readable regardless of the user's accent.

**Matching the real capture rect:** `RecordingController.Start` shrinks width/height to even numbers
for H.264. Added `RecordingController.Region` holding the *effective* rect, and the border uses that
— otherwise the frame would be up to 1px out of alignment with what's actually recorded.

**Wiring:** `_recordBorder.Show(_recorder.Region)` in `StartRecording` after a successful start;
`Hide()` in `StopRecording` and again in `OnExit` in case we're shut down mid-recording.

**Verified** with a temporary `--rec-test` hook (recorded a known 500,300 640x480 rect, then removed
the hook — 0 refs left):
- On screen, all four sides read exactly `rgb(232,17,35)` just *outside* the rect, while
  just-inside and centre pixels were unchanged → the frame covers none of the recorded area.
- Extracted a video frame: exactly 640x480, and **0 of 1344 sampled edge pixels were red** → the
  frame is not in the recording.
- Visual check confirmed a closed rectangle with clean corners.
- Deleted the test mp4 + its cached thumbnail from the capture folder afterwards.

**Known limitation (flagged to user):** for a whole-display recording the frame lands outside that
display — off-desktop, or as a thin red line on the edge of an adjacent monitor — so it's effectively
invisible. Drawing it inside would put it in the video. Left as-is deliberately.

**Gotcha:** `RecordingBorder.cs` hit the usual `Brush`/`Color` ambiguity (`System.Drawing` vs
`System.Windows.Media`) — fixed with the project's existing `MediaBrush`/`MediaColor` aliases. Any new
file mixing WPF types with the implicit `System.Drawing` using will need these.


## Snapshot — 2026-07-31 (breathing pulse on the recording frame)

**Goal:** make the recording frame "breathe" rather than sit static.

**Why the colour is animated, not the opacity.** The strips are deliberately opaque (see previous
snapshot), and WPF only honours `Window.Opacity` when `AllowsTransparency = true`. Turning that on
would make them layered windows in software rendering, right next to a live screen recording. So the
pulse is a `ColorAnimation` on the fill instead:

- `#E81123` (bright) ⇄ `#6E0A11` (dim), half-cycle 1100ms, `AutoReverse`, `RepeatBehavior.Forever`
- `SineEase` / `EaseInOut` so it reads as breathing rather than blinking
- One `SolidColorBrush` shared by all four strips, so a single animation keeps the frame in step
- The brush is created per `Show()` and released in `Hide()`, which first calls
  `BeginAnimation(SolidColorBrush.ColorProperty, null)` to stop the clock. A long-lived animated
  `static` brush would keep ticking after recording ended.

**Verified** by sampling the live strip pixel with `GetPixel` every 250ms during a real recording:
red channel swept 227 → 112 → 232 → 112 → 174 smoothly, peaking at exactly 232 (`0xE8`) and
troughing near 110 (`0x6E`), ~2.2s per cycle, 13 distinct values in 14 samples — and values clustered
near the extremes, which is the sine easing showing up in the data.

**A verification trap worth remembering.** A loose "is this reddish" threshold
(`R>90 && G<70 && B<80`) flagged 20 of 3864 sampled video edge pixels and printed "LEAK detected".
It was a false positive: the recorded window's own UI contains dark reds (a red ✗ status icon).
The tell is arithmetic — a genuinely leaked 3px border sampled every 7px would flag **~950 pixels per
frame**, not five. Re-tested by scanning **every** pixel of each outermost row/column: 0/640 and
0/480 reddish, mean RGB ≈ (8,8,8). No leak. Lessons: (1) sanity-check a positive against the
magnitude the hypothesis predicts, (2) scan whole edge lines rather than sparse samples when the
thing you're looking for is a contiguous line, (3) **don't delete the artefact in the same step that
reports the result** — the first video was deleted during cleanup and had to be re-recorded to
investigate.

**Also:** the whitespace-literal `.Replace("        _trayIcon = CreateTrayIcon();\r\n    }", …)` used to
re-insert the temp hook silently matched 0 times the second time around (it had worked once before),
so the build ran without the hook and produced no recording — looked like a feature failure. Use a
whitespace-tolerant regex plus an explicit match-count assertion when patching source from a script.


## Snapshot — 2026-07-31 (gallery after recording; mojibake root cause; auto-detect gap measured)

### The text artifact ("Global shortcut: PrtScn â€" press it anywhere…") — self-inflicted

`App.xaml.cs` was **genuinely double-encoded** (8 spots), not misdecoded by the compiler: every other
source file is also BOM-less and renders fine, so Roslyn reads UTF-8 correctly.

**Cause:** an early patch used `Get-Content -Raw` on a BOM-less UTF-8 file. PowerShell 5.1 decodes
that with the **ANSI codepage (CP1252)**, so `—` (E2 80 94) became `â€"` in memory; `Set-Content
-Encoding utf8` then wrote those three chars back as real UTF-8. Classic double-encode.

**Diagnosis technique:** dump every non-ASCII char with code points. All of them were
CP1252-representable (U+00E2, U+20AC, U+201D, U+00A6, U+008F, U+00B9, U+2019, U+2020), which proves the
corruption is *uniform* and therefore losslessly reversible.

**Repair (canonical un-double-encode):**
```powershell
$recovered = [Text.Encoding]::UTF8.GetString([Text.Encoding]::GetEncoding(1252).GetBytes($broken))
```
Recovered exactly 5 × `—`, 3 × `…`, 1 × `→`, 1 × `⏹`. Written back **with a BOM** so a future
`Get-Content -Raw` cannot repeat the mistake.

**Verified in the compiled assembly** rather than by eye — .NET user strings sit in the `#US` heap as
UTF-16LE, so searching `Picky.dll` for the literal and printing surrounding code points showed
`U+2014 press it anywhere to snip.`, `Capture regionU+2026`, `U+23F9 Stop recording`. Good trick for
confirming a string literal survived the toolchain.

**Rules for patching source from PowerShell:** use `[IO.File]::ReadAllText(path)` /
`WriteAllText(path, text, UTF8Encoding($true))` — never `Get-Content -Raw` + `Set-Content` on
BOM-less UTF-8. And prefer the editor tool over scripted regex surgery when the file has non-ASCII.

### Gallery opens after saving a recording

`StopRecording` showed a tray balloon; now calls `ShowGalleryDocked(path)` so a finished clip gets the
same treatment as a screenshot (docked gallery, new item selected). Balloon dropped — it vanished
before you could act on it.

### "Small gap in auto selection" — measured, not reproduced

Measured a real window's edges against the bounds auto-detect captures
(`DWMWA_EXTENDED_FRAME_BOUNDS`), sampling mean row/col brightness at offsets −3…+4:

| edge | outside (−3…−1) | **offset 0** | inside (+1…+4) |
|---|---|---|---|
| top | 34 / 33 / 33 | **67** | 32 32 32 32 |
| left | 28 / 22 / 18 | **60** | 20 20 26 26 |
| right | 28 / 28 / 27 | **64** | 32 31 32 48 |
| bottom | 28 / 28 / 28 | **49** | 4 4 4 41 |

The bright line at offset 0 on every edge is the window's **own 1px border highlight**, so the
captured rect lands exactly on the window — auto-detect is pixel-accurate, no inflation.

Ruled out as the cause of a *background* sliver: `RecordingController` rounds w/h **down** to even for
H.264, so the recorded rect is at most 1px *smaller* than the window (e.g. 782×1487 → 782×1486). That
makes the frame overlap the window's last row rather than expose background — growing to even instead
would *create* a sliver, so the current shrink is the right choice and was left alone.

Remaining hypothesis (asked the user to confirm): **Windows 11 rounded corners.** A rectangular grab
of a rounded window necessarily includes a few background pixels in each corner, which reads as a
small gap between a rectangular red frame and the window. Not fixable for video (MP4 has no alpha);
for PNG screenshots the corners could be alpha-masked if wanted.


## Snapshot — 2026-07-31 (square pixel loupe on the capture overlay)

**Ask:** the reference screenshot showed Screenpresso's *circular* magnifier — zoomed pixel grid,
crosshair, size label — wanted in Picky but **square**.

**Implementation** in `RegionSelectWindow`:
- `LensPixels = 15` source pixels across (odd, so there's a true centre pixel), `LensCell = 9` DIPs
  each → a 135×135 viewport. **These constants are duplicated in the XAML** (viewport 135, grid tile
  9, centre marker `Margin="63"` = 7×9, crosshair `Margin="67,0,67,0"` = 135−67−67 = 1 DIP). Change
  one, change all four.
- `RenderOptions.BitmapScalingMode="NearestNeighbor"` is what makes magnified pixels hard-edged
  blocks instead of a blur.
- Grid drawn as a `DrawingBrush` tiled at 9×9 with `ViewportUnits="Absolute"` — one tile per source
  pixel, so grid lines always land on real pixel boundaries and the brush can stay static (the image
  only ever translates by whole multiples of `LensCell`).
- Magnification uses a fresh `CroppedBitmap` over the frozen backdrop per mouse move (15×15 source
  px — trivial). Rejected the alternative of one giant scaled `Image`: at zoom 9 the virtual desktop
  would be a 62,640-DIP-wide quad, well past sane texture limits.
- **Edge handling:** the crop is clamped into the bitmap, then `LensOffset` (a `TranslateTransform`)
  shifts the image back by `(clamped - wanted) * LensCell`, so the pixel under the cursor stays under
  the crosshair even in the corners of the desktop. Without this the crosshair silently lies near the
  edges.
- Crops from `_frozenBitmap` (the **undimmed** screenshot), so the loupe shows true colours while the
  backdrop around it is dimmed — and shows exactly what will be captured.
- Label shows the selection size when there's a selection/auto-detected candidate, else the cursor's
  physical-pixel coordinates.
- Positioned at cursor + 22 DIP, flipping side/vertical rather than sliding off, then clamped.

**Replaced** the separate `ReadoutPanel` (added earlier that day) — its `N × N px` text now lives in
the loupe label, matching the reference tool and removing a second floating panel near the cursor.

**Verified** by screenshotting the live overlay: grid, white crosshair, red centre-pixel marker and a
`1115 × 628` readout all render, with the dim backdrop and un-dimmed cut-out behind.

**Testing gotcha worth remembering:** the first attempt captured the wrong area and looked like the
overlay had failed. Two causes — (1) the overlay must be up *before* `SetCursorPos`, since the loupe
follows real `WM_MOUSEMOVE` messages, and (2) **the user is working on this machine and moves the
mouse**, so a cursor position set before launch is stale by the time of capture. Fix: launch → wait →
`SetCursorPos` → `GetCursorPos` → capture relative to where the cursor *actually* is. Also confirmed
the overlay was alive by enumerating its window (`6960x2400 @ -3840,0`) before concluding anything.
