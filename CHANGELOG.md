# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [0.1.0] - Unreleased

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
