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
