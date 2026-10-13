# Modit — Instructions for the implementing model

Modit is a Windows-only (x64 + ARM64) desktop app for PDF viewing/merging/editing and broad file conversion,
built in C# on .NET 10 + WinUI 3 with native engines (PDFium, QPDF, libvips, resvg) running in sandboxed
out-of-process hosts. Target quality: enterprise, production-ready, policy-controllable, with a first-class CLI.

## Where the plan lives

The complete plan is in `docs/plan/`. Start with `docs/plan/README.md`, then read `01`, `02`, `03`, `09`, and `12`
before writing code. `docs/plan/12-build-playbook-for-opus.md` is the ordered task list with Definitions of Done.
`docs/plan/13-open-questions-and-risks.md` holds owner decisions and their default assumptions.

## Hard rules

- Decisions marked **LOCKED** in `docs/plan/03-technology-decisions.md` are not reopened without an ADR in
  `docs/adr/` that the owner approves.
- No AGPL/SSPL/non-commercial dependencies. Check licenses before adding any package or binary (ADR-018).
- Native code (PDFium, QPDF, libvips, resvg, Tesseract) never runs in the UI process. It runs in `Modit.Engine.Host`.
- Every user-facing Operation ships with a CLI subcommand, an audit entry, localized strings, accessibility names,
  keyboard access, and tests, in the same change.
- Verify before reporting done: build x64, run affected tests, launch the app or CLI on a fixture. Paste outputs.
- Deviations from the plan are recorded as a short ADR and reflected back into `docs/plan/`.
- Releases are produced only by `release.yml` on `v*` tags, published to the public `modit-releases` repo, and
  consumed by the in-app Velopack updater. Never publish release assets by hand. Every milestone ends with a beta
  tag so the installer and update path are exercised continuously (`docs/plan/15-release-and-auto-update-github.md`).
- Install, first-run, repair and uninstall behavior is specified in `docs/plan/14-installation-and-first-run.md`;
  the clean-uninstall and update-path scripts under `tools/release/` must pass before a release.

## Build and test

```
dotnet tool restore                                   # vpk (Velopack CLI), pinned in dotnet-tools.json
powershell -File tools/fetch-native.ps1 -Rid win-x64,win-arm64
dotnet build Modit.sln -c Debug                       # Platform defaults to x64; add -p:Platform=ARM64 for ARM
dotnet test --solution Modit.sln                      # xUnit v3 on Microsoft.Testing.Platform
dotnet run --project src/Modit.App
powershell -File tools/build.ps1 -Version 0.1.0       # installer, update packages, portable zip in artifacts/
powershell -File tools/release/verify-update-path.ps1 -FromSetup <old Setup.exe> -Feed artifacts/releases/win-x64-stable -ToVersion <new>
```

Current progress and known gaps are in `docs/STATUS.md`. Deviations from the plan are in `docs/adr/`.

## Conventions

See `docs/plan/09-repository-structure-and-conventions.md`: file-scoped namespaces, sealed by default, records
for Operations/DTOs, `Async` suffix with trailing `CancellationToken`, `ILogger<T>` with `LoggerMessage`,
no user-visible string literals outside `.resw`, geometry in PDF points with explicit unit names.
