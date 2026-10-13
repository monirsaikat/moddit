# 11 — Testing and Quality

## Pyramid

| Layer | Scope | Tooling | Runs |
|-------|-------|---------|------|
| Unit | Core logic: range grammar, EditLog compiler, naming engine, text clustering, sniffers, options schema, policy overlay, license verification, CMS building, redaction geometry | xUnit v3, FluentAssertions, FsCheck (property-based) | Every PR, < 60 s |
| Native binding | Each `LibraryImport` exercised at least once; SafeHandle disposal; error paths | xUnit, runs against `native/bin` | Every PR |
| Engine integration | Host process round trips: open/render/text/annots/forms/save; crash+respawn; shared memory; cancellation | xUnit, spawns real hosts | Every PR, < 5 min |
| Golden rendering | Render fixture pages at fixed DPI; compare to committed PNGs with perceptual tolerance (per-pixel ΔE ≤ 2 for ≥ 99.5% pixels; SSIM ≥ 0.995); separate baselines per RID if needed | Verify.Xunit + own comparer (libvips) | Every PR |
| Operation end-to-end | Every `Operation` via Core API and via CLI on fixtures; verify outputs structurally (QPDF `--check`, sniff, page counts, text extraction) | xUnit + CLI process tests | Every PR, < 10 min |
| Conversion matrix | Generated test per registry edge: convert fixture → re-sniff → dimension/page/duration assertions | xUnit theory from registry | Every PR (packs-dependent edges nightly on runner with packs) |
| UI automation | Smoke flows: open, scroll, search, merge dialog, convert hub, settings; accessibility FastPass | FlaUI (UIA3) or Appium WinAppDriver; axe-windows CLI | Nightly + release |
| Performance | Budgets table from 07; micro benchmarks; frame timing | BenchmarkDotNet, E2E bench, WPR | Nightly on self-hosted Baseline; PR warn-only |
| Fuzzing | PDF open/render/text/annots; image decode; SVG parse; archive extract; CSV/JSON parsers | SharpFuzz (libFuzzer) in-process for managed parsers; host-level corpus runner for native engines (mutational via `radamsa`-style mutator we write) | Nightly 2 h; release 24 h |
| Install matrix | MSI/MSIX/portable install/upgrade/uninstall on Win10 1809, 22H2, Win11 23H2/24H2, x64 + ARM64 VMs | Hyper-V lab script (PowerShell DSC) | Release |
| Security | Dependency vulnerabilities, license policy, secret scan, binary signing check, SBOM validation | `dotnet list package --vulnerable`, nuget-license, gitleaks, signtool verify, cyclonedx-cli | Every PR |
| Manual | Acrobat/Edge/Chrome/Preview compatibility of produced PDFs; printer tests; pen devices; screen readers | Checklist in `docs/dev/manual-test-plan.md` | Release |

## Key test designs

### Golden rendering
- Fixtures rendered at 96 and 192 dpi, pages selected to cover fonts (Type1, TrueType, CID, Type3), shadings,
  transparency, JPX, annotations, forms. Baselines updated via `tools/update-goldens.ps1` with PR review of diffs
  (diff images attached by test output).
- Tolerance strategy avoids flakiness from AA differences between PDFium builds: compare on 2× downsampled
  grayscale with SSIM, plus hard check on text bounding boxes count.

### Save safety
- Property test: random EditLog sequences on random fixtures → save → reopen → virtual page sequence equals
  expected; QPDF check passes; file size within 1.2× of inputs sum.
- Crash injection: environment variable `MODIT_TEST_CRASH_AT=save:beforeReplace|afterTemp` kills process; test
  asserts original intact and temp cleaned on next start.

### IPC and hosts
- Kill host during tile render → UI receives tiles after respawn; no duplicate slot writes (generation check).
- Memory limit hit → operation reports `ENGINE_MEMORY_LIMIT`, UI stays responsive.
- 10,000 sequential small RPCs → p99 < 1 ms on Baseline.

### Annotations/forms compatibility
- Produced annotations validated against `tests/schemas/pdf-annot-required-keys.json` and opened in Acrobat
  Reader in the manual matrix; XFDF round trip equality tests.

### OCR
- Synthetic fixtures generated from known text with fonts/rotations/noise → WER/word-match assertions; invisible
  text layer alignment: each OCR word box intersects rendered dark pixels ≥ 70%.

### Redaction
- Fixtures with planted strings in text objects, images (rendered text), and paths; after apply: extract text
  (PDFium + our own content stream tokenizer) → zero occurrences; image regions blacked (pixel check); report
  matches marks.

### Signatures
- Sign fixtures with test CA (generated in test), verify with our verifier and with `openssl cms -verify` on the
  extracted CMS; ByteRange coverage tests; tamper (flip a byte) → invalid; timestamp token parse tests with a
  recorded TSA response.

### Conversion
- Image ops property tests: resize dimensions math, EXIF orientation normalization, alpha handling on JPEG
  (background applied), animated frame count preserved.
- SVG→PDF vector check: output PDF content stream contains path operators and no large image XObject.
- Archive safety fixtures: `..\\` entries, absolute paths, 1 GB-from-1 KB bomb (ratio), 100k entries, reparse
  attempts → rejected with specific error codes.
- Media: ffprobe asserts on duration/codec/resolution of outputs; stream copy trim exactness ±1 frame.

### UI
- FlaUI flows on packaged app: open fixture → assert page count in status bar; type in find bar → hit count;
  drag a thumbnail → order change reflected; Convert Hub → output file exists.
- Pseudo-loc build visual snapshot of each page at 1366×768 and 1920×1080, 100%/150% DPI → no clipping (text
  measured vs container via UIA bounds).
- Theme snapshots light/dark/high contrast for each screen (Verify image snapshots, tolerant).

### CLI
- `modit --help` tree snapshot; every subcommand `--json` output validates against generated schema; exit codes
  table test; stdin/stdout piping (`type a.pdf | modit convert - --to png --out -`).

### Policy and licensing
- For each policy key: set registry in test hive (redirect via `MODIT_POLICY_ROOT` env in test builds) → assert
  UI command disabled with tooltip text and CLI exit 3.
- License: valid/expired/tampered/wrong product/wrong machine/volume → gate outcomes; clock skew tolerance.

## CI pipeline (`ci.yml`)

1. Checkout, setup .NET 10, cache NuGet and `native/bin` (keyed by manifest hash), `fetch-native.ps1`.
2. Build x64 and ARM64 (Release), warnings as errors.
3. Unit + native + engine integration + operation E2E + conversion matrix (no-pack edges) + golden rendering.
4. License scan, vulnerability scan, gitleaks, format check (`dotnet format --verify-no-changes`).
5. Coverage report (Coverlet → ReportGenerator) with threshold 80% on `Core`, `Pdf`, `Convert.*`.
6. Package unsigned MSIX + MSI for PR artifacts; UI smoke on hosted runner (packaged, x64).
7. Nightly: perf on self-hosted Baseline, fuzz 2 h, pack-dependent conversion tests, install matrix smoke.
8. Release: all above + signing + SBOM + checksums + GitHub Release + winget PR + appinstaller publish.

## Quality gates (merge-blocking)

- All tests green; coverage threshold; zero analyzer warnings; no new `TODO` without issue link; golden diffs
  acknowledged; perf PR check not worse than 10% on micro benchmarks (hosted runner, informational) and nightly
  Baseline must be green before release branch cut.

## Bug severity and SLAs

| Severity | Definition | Rule |
|----------|------------|------|
| P0 | Data loss, corrupt output, crash of UI, security | Block release; fix first |
| P1 | Feature broken, no workaround; perf budget miss > 50% | Block release |
| P2 | Degraded with workaround; visual defects | Fix within milestone |
| P3 | Cosmetic, edge-case formats | Backlog |
