# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [0.2.0] - Unreleased

### Added
- Memory analysis: RAM in use, available, standby/modified cache, free and committed memory
  (GlobalMemoryStatusEx, GetPerformanceInfo, performance counters), with plain-language advice that
  distinguishes real use from reusable cache.
- Before → After results of *Return to baseline*: apps, processes, RAM in use, available memory and
  cache; memory released by closing apps reported separately.
- Optional **Deep Memory Cleanup** (standby-list purge via `NtSetSystemInformation`), never automatic
  and never part of Return to baseline. Elevation is requested for this single operation through a
  one-shot helper with a strict two-value protocol; its result is reported separately.
- Sorting by name, memory (numeric), process count and risk via clickable column headers with ↑/↓,
  and a search box (Ctrl+F). The selection survives sorting, searching and refreshing.
- Full localization: English, Русский, 简体中文; language picker with *System default* and English
  fallback; switching without restart. `README.zh-CN.md`; language links in all READMEs.
- `snapback-cli memory`.
- Tests for memory before/after, Deep Cleanup and the elevation path, sorting, search, selection and
  localization; CI drives the GUI through UI Automation in all three languages and exercises the
  elevated helper.

### Changed
- The CLI executable is `snapback-cli.exe` (it previously collided with `SnapBack.exe` on
  case-insensitive file systems).

## [0.1.0]

### Added
- Baselines describing the user session as applications (survive PID changes, restarts and updates),
  created manually or automatically after sign-in once startup has settled.
- Process grouping into applications (process tree, helper hosts, install folder + publisher).
- Fail-closed safety classification: Safe, Caution, Manual review, Protected.
- Comparison with the baseline: in baseline, restarted, extra instance, new, started by a baseline app.
- Preview of every step and a dry run against live safety checks.
- Graceful closing: close requests to windows, Ctrl+C for unshared consoles, Windows Restart Manager
  (never forced) for tray apps.
- Separate, explicit, identity-bound and time-limited force-termination of apps that did not close.
- Keep list (whitelist), history of operations, settings, file logging.
- WPF app with Fluent theme (light/dark) and a command-line interface.
- Unit tests for all safety-critical logic and Windows integration tests.
