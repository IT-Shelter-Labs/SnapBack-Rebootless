# Contributing to SnapBack

Thanks for helping! SnapBack closes programs on people's computers, so correctness and safety come
before features.

## Getting started

```powershell
dotnet build SnapBack.sln
dotnet test SnapBack.sln
dotnet run --project src/SnapBack.App
```

Use `SNAPBACK_DATA_DIR` to keep a development baseline/settings separate from your real one:

```powershell
$env:SNAPBACK_DATA_DIR = "$env:TEMP\snapback-dev"
```

The core (`src/SnapBack.Core`, `tests/SnapBack.Core.Tests`) builds and tests on Linux/macOS too.

## Ground rules

- Read [docs/SAFETY.md](docs/SAFETY.md) before touching classification, planning, the guard, the
  controller or the executor.
- Never add a way to lower a *Protected* classification, and never make forced termination implicit.
- Every behaviour change in `SnapBack.Core` needs tests. The fakes in `tests/SnapBack.Core.Tests/Fakes`
  simulate processes, windows, consoles and Restart Manager.
- Any new Win32 action must go through `VerifiedProcess` and act while holding the verified handle.
- Do not log or persist window titles, command lines or other user content.
- Builds treat warnings as errors; keep it that way.

## Translations

User-facing text lives in resource files, never in code:

- `src/SnapBack.Core/Localization/Strings.resx` (+ `.ru.resx`, `.zh-CN.resx`) — engine texts;
- `src/SnapBack.App/Localization/Strings.resx` (+ translations) — UI texts.

Add a key to all three files; use `Loc.Get`/`Loc.Format` in Core and `{l:Tr Key}` / `Tr.Get` in the
app. Avoid plural-dependent sentences ("Processes: {0}" instead of "{0} processes"). The tests fail if
a key is missing in any language, if placeholders differ, or if code uses an unknown key.

## Adding a known application

Add its executable name to the right set in `src/SnapBack.Core/Classification/KnownProcesses.cs`
and a case to `ClassifierTests`. When in doubt between two levels, pick the stricter one.

## Pull requests

Describe what changes for the user and, for safety-relevant code, why it is safe. Keep PRs focused.
CI builds on Windows, runs all unit tests and the Windows integration tests, publishes the app and
smoke-tests the CLI.
