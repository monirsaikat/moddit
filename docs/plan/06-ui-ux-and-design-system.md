# 06 — UI/UX and Design System

The visual target is "a first-party Windows 11 app from the Office/Edge era, with the density and keyboard
depth of a professional tool". Fluent 2 as the base; Modit adds a document-centric workspace, a command system,
and a batch/queue surface that consumer PDF tools lack.

## Application shell

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│ ⧉ Modit   [Doc A.pdf ×] [Doc B.pdf ×] [Batch: 12 files ×] [+]                 — □ ×   │  Title bar: custom, tabs in title bar (TabView.TabStripHeader), Mica
├────────┬────────────────────────────────────────────────────────────────────────────────┤
│ 📄 View│  Home │ Pages │ Annotate │ Forms │ Protect │ Convert │ Tools      🔍 Search  ⌘│  Ribbon-like CommandBar tabs (contextual), command palette (Ctrl+K)
│ ⊞ Pages│──────────────────────────────────────────────────────────────────────────────│
│ ✎ Annot│ ┌────────┐  ┌──────────────────────────────────────────┐  ┌────────────────┐ │
│ ☰ Forms│ │Thumbs/ │  │                                          │  │ Properties /   │ │
│ 🔒 Prot│ │Outline/│  │          Document canvas (Win2D)         │  │ Comments /     │ │
│ ⇄ Conv │ │Search/ │  │   tiles, overlays, selection, ink        │  │ Signatures /   │ │
│ ⚙ Tools│ │Attach  │  │                                          │  │ Options form   │ │
│        │ └────────┘  └──────────────────────────────────────────┘  └────────────────┘ │
│ ▣ Queue│  Left pane (collapsible, 220–360px)   Center            Right pane (300–420px)│
├────────┴────────────────────────────────────────────────────────────────────────────────┤
│ Page 12 of 148  ▸ 100% ▾  ⟲ ⟳  │ Fit width ▾ │ Continuous ▾ │ ● 2 jobs running ▸ │ ⓘ │  Status bar
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Window**: `Microsoft.UI.Xaml.Window` with `ExtendsContentIntoTitleBar`, `SystemBackdrop = MicaBackdrop`
  (fallback Acrylic → solid on Win10), min size 960×600, remembers size/position/monitor per DPI.
- **Tabs**: `TabView` in title bar; tab kinds: Document (PDF/image/SVG/text viewers), Batch Job, Converter Hub,
  Compare, Settings. Tear-off to new window (P1). Middle-click close, Ctrl+Tab cycling, drag reorder.
- **Left rail** (`NavigationView` compact, 48 px): switches the left pane content / mode. Keyboard: Ctrl+Shift+1..7
  (Ctrl+1/Ctrl+2/Ctrl+0 are reserved for zoom presets).
- **Command surface**: `CommandBar` per mode with `AppBarButton`s + `CommandBarFlyout` for context; global
  **Command Palette** (Ctrl+K / F1) listing every command with fuzzy search, shortcuts and recent — it is the
  escape hatch guaranteeing discoverability of 150+ features.
- **Right pane**: context-dependent: Properties (selected annotation/page/document), Comments list, Signatures,
  Options form for the active tool (e.g. Watermark parameters with live preview on canvas).
- **Status bar**: page navigator (editable), zoom combo, rotation, layout mode, job indicator (click → Queue),
  document security badge (🔒 encrypted / signed), managed-by-organization badge when policies apply.

## Key screens and flows

### 1. Home / Start (no document)
Large drop zone "Drop files here or Open". Action tiles: Merge PDFs, Split PDF, Compress, Convert files,
OCR, Protect, Sign, Batch. Recent files (pinned, thumbnails from cache), Recent recipes. Everything also in the
File menu. The Home surface is also what the Explorer "Open with Modit" lands on when multiple files are dropped.

### 2. Document viewer
Canvas with continuous scrolling, inertial smooth scroll, pinch/ctrl+wheel zoom anchored at cursor, page gaps
8 px, drop shadow per page (Win2D ShadowEffect cached), selection with handles, find bar (Ctrl+F, inline at top
of canvas with count and arrows), link cursor, hover tooltip for links. Context menu: Copy, Highlight, Add note,
Search web (policy), Rotate page, Delete page, Extract page, Properties.

### 3. Pages mode (page organizer)
Grid of large thumbnails (`GridView` with drag-drop, multi-select, rubber-band), rotate/delete/duplicate per
item hover toolbar, insert from file between items (drop indicator), "Insert blank", page range selection
textbox (`1-3, 7, 10-end, odd, even`), zoom slider for thumbnail size. Secondary document tabs can be dragged
into the grid (merge by drag). Undo stack visible in a flyout.

### 4. Merge dialog (fast path)
Modal `ContentDialog` → list of sources with per-row page range, drag reorder, "add files/folder", options
(bookmarks, output name), preview strip. Enter = Merge. Opens result in a new tab or saves directly (setting).

### 5. Convert Hub (tab)
Left: dropped files list with sniffed formats and sizes. Center-top: "Convert to" format chooser grouped by
family with search; disabled formats show why (needs pack → install card). Center: options form generated from
schema with "Advanced" expander and preset chips. Right: output settings (folder, naming, collisions). Bottom:
"Convert N files" primary button + estimate. Running jobs move to the Queue with per-file progress and thumbnails
of results; "Open folder" on completion; errors inline with retry.

### 6. Queue (tab or flyout)
`ListView` of jobs with step pipeline chips, progress, pause/resume/cancel, reorder priority, "Open output",
error details; persisted across restarts; filter by state. Toast on completion (if window not focused; policy).

### 7. Tool panes (right pane forms with live preview)
Watermark, Header/Footer/Page numbers/Bates, Compress (shows estimated size as you change preset — uses quick
sampling of 3 pages), Protect, OCR (language picker, engine, detect text layer), Crop (interactive handles on
canvas), Redact (marks list with "Apply redactions" guarded by confirmation + report), Sign (cert picker from
Windows store with details, appearance editor, place on page), Split (mode tabs with resulting file list
preview), Metadata editor, Bookmarks editor (tree with drag).

### 8. Settings
Categories: General, Appearance (theme, accent, Mica), Viewer, Editing defaults (author name, highlight color),
Conversions & Engine packs, Output & Naming, Shortcuts (editable), Privacy (telemetry opt-in), Enterprise
(license, policies view read-only), Advanced (cache sizes, host pool), About (versions, third-party notices).
Managed settings show a lock icon and tooltip "Managed by your organization".

### 9. Compare (P2)
Two-up canvases with synced scroll/zoom, difference overlay toggle, change list in right pane.

## Design tokens

Use WinUI theme resources; do not invent a parallel palette. Project-specific tokens in `Themes/Tokens.xaml`:

| Token | Light | Dark | Use |
|-------|-------|------|-----|
| `CanvasBackgroundBrush` | #F3F3F3 (LayerFillColorDefault) | #202020 | Behind pages |
| `PageShadow` | 0 2 8 rgba(0,0,0,.18) | 0 2 8 rgba(0,0,0,.6) | Page drop shadow |
| `SelectionFillBrush` | Accent @ 30% | Accent @ 35% | Text selection |
| `SearchHitBrush` / `SearchHitCurrentBrush` | #FFE57F @ 60% / #FFB300 @ 70% | same | Find |
| `RedactMarkBrush` | #D13438 @ 40% stroke 2px | same | Redaction marks |
| `AnnotationDefaultColors` | Yellow #FFF176, Green #AED581, Blue #81D4FA, Pink #F48FB1, Orange #FFB74D | same | Highlighter palette |
| Spacing | 4/8/12/16/24/32 | | `x:Double` resources `SpaceXS..Space2XL` |
| Radius | 4 (controls), 8 (cards/panes) | | `ControlCornerRadius`, `OverlayCornerRadius` |
| Typography | Segoe UI Variable: Caption 12, Body 14, BodyStrong 14/600, Subtitle 20, Title 28 | | WinUI `TextBlock` styles |
| Icons | Segoe Fluent Icons (glyph codes in `Icons.cs`) + custom `PathIcon`s for PDF-specific glyphs | | Never raster icons |

Density: default comfortable; "Compact" setting switches to WinUI compact resource dictionary.

## Interaction principles

1. **Direct manipulation first**: drag pages, drag files into the grid, drag handles to crop, draw to redact.
2. **Live preview**: tool panes update the canvas overlay as parameters change; "Apply" commits to the EditLog.
3. **Undo everything** (Ctrl+Z / Ctrl+Y) in a document tab, including page ops, annotations, metadata edits.
   Undo stack persisted in recovery journal.
4. **Never block**: every long operation is a job with progress and cancel; dialogs never spin.
5. **Progressive disclosure**: common 5 options visible, rest under "Advanced". Presets before parameters.
6. **Explain blocked actions**: disabled commands carry tooltips ("Requires owner password", "Blocked by policy",
   "Install LibreOffice pack").
7. **Confirmation only for irreversible**: Apply redactions, overwrite original, delete from disk, remove security.
   Everything else is undoable or produces a new file.
8. **Keyboard parity**: every command has a shortcut or is reachable via palette; focus visuals always visible.

## Keyboard shortcuts (defaults, editable)

| Action | Shortcut |
|--------|----------|
| Open / Save / Save As / Close tab | Ctrl+O / Ctrl+S / Ctrl+Shift+S / Ctrl+W |
| Find / Next / Prev | Ctrl+F / F3 or Enter / Shift+F3 |
| Zoom in/out/fit width/fit page/100% | Ctrl+= / Ctrl+- / Ctrl+1 / Ctrl+2 / Ctrl+0 |
| Rotate view CW/CCW | Ctrl+R / Ctrl+Shift+R |
| Go to page | Ctrl+G |
| Command palette | Ctrl+K, F1 |
| Modes: View/Pages/Annotate/Forms/Protect/Convert/Tools | Ctrl+Shift+1..7 |
| Highlight / Underline / Strikethrough / Note / Free text | H / U / S / N / T (in Annotate mode) |
| Rotate page(s) / Delete page(s) / Duplicate | Ctrl+] / Delete / Ctrl+D (in Pages mode) |
| Undo / Redo | Ctrl+Z / Ctrl+Y |
| Toggle left/right pane | Ctrl+B / Ctrl+Shift+B |
| Print | Ctrl+P |
| Merge dialog / Split / Compress / Convert | Ctrl+M / Ctrl+Shift+P / Ctrl+Shift+C / Ctrl+Shift+V |

## Accessibility (ship-blocking)

- Every interactive element has `AutomationProperties.Name`; canvas exposes a custom `AutomationPeer` tree:
  document → pages → text blocks (from text layer) and annotations, so Narrator can read page text.
- Full keyboard navigation incl. canvas (arrow keys scroll, Tab cycles links/fields/annotations).
- High contrast: all custom brushes mapped to `SystemColor*` in `HighContrast` dictionary; canvas overlays use
  system highlight colors.
- Text scaling honors Windows text size settings; min 4.5:1 contrast on all text; focus rects 2 px.
- Reduced motion setting disables parallax/scroll animations.
- Automated: Accessibility Insights FastPass in CI (via `axe-windows` CLI) with zero errors gate.

## Localization

- All strings in `Strings/<lang>/Resources.resw`; `x:Uid` in XAML; `ResourceLoader` in code. Format strings with
  named placeholders documented in comments. Pseudo-loc build (`qps-ploc`) in CI; UI must survive 40% longer
  strings without clipping (use wrapping, min widths, no fixed widths for text).
- RTL support (ar, he) is P3 but do not block it: use `FlowDirection`-safe layouts (no hard-coded Left/Right
  margins where symmetric is possible).

## Windows integration

- **Explorer context menu** (Win11 modern menu): sparse MSIX package registering `IExplorerCommand` COM server
  (`Modit.Shell.dll`, C++/WinRT or C# with `ComWrappers` + AOT): "Open with Modit", "Merge with Modit" (multi-
  select PDFs), "Convert with Modit ▸ PNG/JPEG/WebP/PDF", "Compress PDF". Classic menu via registry for Win10.
- **File associations**: PDF (optional default), images (open-with only), SVG, and `.moditrecipe`.
- **Protocol**: `modit://open?path=...`, `modit://convert?...` for shell → app activation (single-instance
  redirect via `AppInstance.FindOrRegisterForKey`).
- **Jump list**: Recent, Tasks (Merge, Convert, Batch).
- **Drag-drop**: from Explorer, Outlook attachments (CF_FILEDESCRIPTOR → temp copy), browsers (URL/HTML → HTML→PDF).
- **Print to Modit** virtual printer: out of scope (would require a driver).
- **Toast notifications** for job completion (AppNotificationManager); policy switch.
- **Share contract** (P2) and **Clipboard**: paste image → new PDF/convert; copy page as image.
- **Pen/Ink**: `InkCanvas` overlay in Annotate → Ink tool, pressure, palm rejection, convert strokes to PDF ink annots.
- **Dark/light** follows system; accent color follows system (Mica tint).

## Visual quality bar

- 60 fps scrolling with tiles streaming in; no white flashes (low-res placeholder first, crossfade 80 ms).
- Thumbnails fade in, never pop; skeleton placeholders.
- Loading states: `ProgressRing` only after 300 ms (avoid flicker).
- Empty states with illustration (Fluent emoji-style line art, inline SVG via `PathIcon`), one sentence, one action.
- Error states: `InfoBar` (severity-colored), never `MessageBox` for recoverable issues.
- Micro-animations: WinUI implicit `ThemeTransition`s only; no custom bounce.

## Component inventory (custom controls to build)

| Control | Purpose |
|---------|---------|
| `DocumentCanvas` | Win2D virtual canvas, tile compositor, overlays, input handling |
| `PageThumbnailStrip` / `PageOrganizerGrid` | Virtualized thumbnails with drag-drop and selection |
| `PageRangeBox` | Textbox with range grammar validation and live count |
| `OptionsFormControl` | Renders JSON schema into Fluent controls |
| `FormatPicker` | Grouped, searchable target format chooser with availability states |
| `JobProgressCard` / `QueueView` | Job UI |
| `CommandPalette` | Fuzzy command search |
| `FileDropZone` | Drop target with hover affordance, validation |
| `AnnotationToolbar` / `ColorPaletteFlyout` | Annotation tools |
| `CertificatePicker` | Windows cert store browser |
| `CropHandlesOverlay`, `RedactMarksOverlay`, `SelectionOverlay`, `SearchHitsOverlay` | Canvas overlays |
| `ManagedSettingBadge` | Policy lock indicator |
