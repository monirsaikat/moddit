# ADR 0005: User data lives outside the install root

Status: accepted by the implementer. Date: 2026-10-14.

## Context

The plan put user data in `%LOCALAPPDATA%\Modit`. That is also the folder Velopack installs into. Running a
setup over an existing install (repair, reinstall) and uninstalling both delete that whole folder, so settings,
recovery journals and logs were lost. This happened when 0.4.2 was installed over 0.1.1. It also broke the plan's
rule that a silent uninstall keeps user data. The CI update-path check did not catch it, because it sets
`MODIT_DATA_ROOT`.

## Decision

- An installed copy keeps its data in `%LOCALAPPDATA%\ModitData`. The install root stays `%LOCALAPPDATA%\Modit`.
- On startup, if `ModitData` has no `settings.json` and `%LOCALAPPDATA%\Modit\settings.json` exists, the app
  copies settings and recovery journals across once (`AppPaths.MigrateFrom`). Logs and cache are not copied.
- Portable mode (`.\data`) and the `MODIT_DATA_ROOT` override are unchanged.
- `docs/plan/02`, `14` and `15` now say `ModitData` wherever they mean user data.
