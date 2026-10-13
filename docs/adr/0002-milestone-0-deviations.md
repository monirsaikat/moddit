# ADR 0002 — Milestone 0: deviations from the plan

Status: accepted for milestone 0, items marked **OWNER** need the product owner's approval because they touch
LOCKED decisions. Date: 2026-10-13.

| # | Plan said | What was built | Why | Follow-up |
|---|-----------|----------------|-----|-----------|
| 1 | Engine host and CLI publish as Native AOT (ADR-001, **LOCKED**) | Both are self-contained JIT builds with ReadyToRun, published into the app folder so all three executables share one .NET runtime | StreamJsonRpc with the MessagePack formatter generates proxies at run time and is not AOT-safe as configured; risk R2 in plan 13 predicted this | **OWNER**: either accept R2R hosts, or schedule an AOT spike (StreamJsonRpc source-generated proxies + Nerdbank.MessagePack) in M1. Host cold start is measured before deciding |
| 2 | Windows App SDK metapackage | Individual component packages (Base, Foundation, InteractiveExperiences, WinUI, DWrite, Runtime) | The 2.x metapackage ships the AI, ML, Search and Widgets runtimes (onnxruntime, DirectML): about 60 MB Modit never loads | None. Add a component only when a feature needs it |
| 3 | (not addressed) | `DisableTransitiveFrameworkReferences=true` | Microsoft.VisualStudio.Threading (dependency of StreamJsonRpc) pulls the whole WPF/WinForms desktop framework into a self-contained publish (+60 MB) | Watch for a `FileNotFoundException` on WindowsBase; none seen in tests or in the installed app |
| 4 | Restricted token and low integrity for hosts (plan 08) | Job Object only: kill-on-close, per-process memory limit, die-on-unhandled-exception | Kept M0 to what could be verified end to end; token restriction interacts with file access (risk R4) | M1: restricted token with the UI process passing file handles |
| 5 | Host pool, heartbeat every 2 s, shared-memory slot ring | One host per client, liveness detected from process exit and pipe failure, one named shared buffer per render | Sufficient for the M0 page view; the pool and ring belong to the tile renderer | M1.2 |
| 6 | `AnalysisLevel=latest-all` | `latest-recommended`, warnings as errors, curated `.editorconfig` | `latest-all` flags hundreds of style-only findings in binding code | Revisit per project when the code base settles |
| 7 | xUnit on VSTest, `dotnet test Modit.sln --filter` | xUnit v3 on Microsoft.Testing.Platform: `dotnet test --solution Modit.sln` | .NET 10 SDK no longer supports the VSTest target for MTP test projects | Plan 09/11/12 and CLAUDE.md updated |
| 8 | All ~35 projects created empty in M0.1 | Only the 10 projects M0 uses, plus 2 test projects | Empty projects add build time and nothing else | Each milestone creates the projects it fills |
| 9 | resvg bindings in M0.5 | Not started | resvg publishes no Windows C-API binary; it must be built from source with Rust for x64 and ARM64 | M3.3, with a `native-refresh` build job |
| 10 | Win2D canvas, left mode rail | Virtualized list of whole-page renders at 100 %; Home, Document and Settings tabs; no mode rail | The canvas is milestone 1; a rail with no modes would be dead UI | M1.1 replaces the page list |
| 11 | Velopack channel per RID where beta users also receive stable builds | Channels `win-<arch>-stable` and `win-<arch>-beta` are separate feeds | A Velopack client only reads its own channel feed | Decide in M6: publish stable builds to both feeds, or tell beta users to switch back |
| 12 | Authenticode check of downloaded update packages; staged rollout; deferral | Decision logic for rollout, deferral and skipped versions is implemented and tested, but not yet wired into the check. No signature verification | Builds are unsigned until a Trusted Signing account exists (plan 13 Q1) | Wire both when signing is available |
| 13 | git-cliff release notes | Plain `git log` since the previous tag | Avoids a tool dependency until commit conventions are in use | M6.2 |

## Measured in milestone 0 (developer machine, not the Baseline profile)

| Item | Value | Budget (plan 07) |
|------|-------|------------------|
| App folder after publish (x64) | 211 MB | not set |
| Installer `Setup.exe` | 97 MB | 90 MB |
| Full update package | 90 MB | — |
| Delta update package 0.1.0 → 0.1.1 | 0.18 MB | — |
| PDFium render, Letter page at 60 dpi | 5 ms | — |

The installer is 7 MB over budget. IL trimming is the remaining lever and is scheduled for the M8 performance pass.
Cold start and RPC latency have not been measured against the budgets yet.
