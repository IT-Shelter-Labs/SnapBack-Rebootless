# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- IT Shelter Telegram link in the main window and a community contact card in Settings,
  localized in English, Russian and Simplified Chinese. Links open only on user request.
- An actual application screenshot and IT Shelter contact links in all three READMEs.

## [1.0.0] - 2026-10-06

First public release. It contains everything listed below; versions 0.1–0.3 were development
milestones and were never published.

### Highlights
- Baseline and named **profiles** (*Clean*, *Work*, *Gaming*, …) of your session, compared by
  application, not by PID.
- **Return to baseline**: polite closing (close request, Ctrl+C, Restart Manager), preview and dry
  run, forced termination only after a separate confirmation, fail-closed protection of system,
  elevated, other-user and critical processes, PID-reuse-safe actions.
- Dashboard with **session drift**, **expected impact**, RAM in use vs. available vs. cache.
- **Rules** (always keep / always close / ask every time), **memory growth** tracking,
  **WSL and Docker** handled with their own commands.
- Notification-area icon, **start with Windows**, automatic baseline after startup, rare optional
  notifications.
- Optional **Deep Memory Cleanup** (standby cache), never automatic, UAC only for that operation.
- English, Русский, 简体中文; command-line interface `snapback-cli.exe`.

## [0.3.0] - development (not released)

### Added
- **Profiles** (*Clean*, *Work*, *Gaming*, …) instead of a single baseline: create from the current
  state, rename, update, add/remove apps, delete, default profile, profile picker; switching always
  goes through the preview. The 0.2 `baseline.json` is migrated into the profile *Clean*.
- **Rules** per app: *Always keep*, *Always close*, *Ask every time* (context menu, Settings, CLI).
  Rules only decide what is offered and pre-selected; protection and polite closing are unchanged.
- **Session drift** (low/medium/high) and **session uptime** on the dashboard.
- **Expected impact** before returning to a profile, with a breakdown of RAM in use (new apps /
  profile apps / system and other) and the cache shown separately; clearly marked as an estimate.
- **Memory over time**: bounded in-memory history per app instance and conservative *unusual memory
  growth* detection (never reported as a certain leak).
- **WSL and Docker Desktop** support: detection, protection from ordinary closing, *Shut down WSL…*
  (`wsl --shutdown`) and *Stop Docker…* (`docker desktop stop`) with a warning listing running
  distributions/containers and a re-check right before stopping; optional per-profile suggestion.
- **Notification-area icon** with status tooltip and menu; close-to-tray (configurable);
  `--tray`, `--quit`; background analysis every 15 minutes at background priority with EcoQoS.
- **Start with Windows**, *start minimized to the tray*, *automatically capture the baseline after
  startup* (now also waits for the CPU to calm down; once per sign-in; chosen profile).
- Optional, rate-limited **notifications** (high drift, unusual memory growth).
- **History** per operation: profile, apps/processes/RAM before → after, memory released, apps
  closed, forced terminations, Deep Memory Cleanup used, stopped environments, duration.
- CLI: `profiles`, `profile …`, `rules`, `rule …`, `env`, `status --json`, `--profile`.
- Tests for profiles, rules, drift, impact, memory growth, notifications, WSL/Docker safety, history,
  automatic baseline and settings; CI checks tray start, autostart entry, automatic baseline, idle
  CPU/memory, activation, close-to-tray and `--quit`.

### Changed
- The keep list became *Always keep* rules (migrated automatically).
- `--auto-baseline` (0.2) is now `--autostart`; SnapBack stays in the notification area instead of
  exiting after the automatic baseline. Existing Run entries are updated automatically.
- Docker Desktop processes are protected (*WSL / Docker*) instead of "manual review".

## [0.2.0] - development (not released)

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

## [0.1.0] - development (not released)

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

[1.0.0]: https://github.com/IT-Shelter-Labs/SnapBack-Rebootless/releases/tag/v1.0.0
