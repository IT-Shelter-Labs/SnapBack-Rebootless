# Architecture

SnapBack is a .NET 10 solution with a strict split between a **platform-independent core** (all
decisions) and a **thin Windows layer** (all system calls). The core never calls the OS, so every
safety-relevant decision is unit-tested on any platform with simulated processes.

```text
┌────────────────────┐   ┌────────────────────┐
│ SnapBack.App (WPF) │   │ SnapBack.Cli       │   presentation
└─────────┬──────────┘   └─────────┬──────────┘
          └──────────┬─────────────┘
                     ▼
┌─────────────────────────────────────────────┐
│ SnapBack.Core                               │   decisions (pure, tested)
│  Model · Classification · Grouping ·        │
│  Baselines · Planning · Safety · Execution  │
│  Persistence · Logging · Startup            │
│        ▲ ISystemProbe   ▲ IProcessController│
└────────┼────────────────┼───────────────────┘
         │                │
┌────────┴────────────────┴───────────────────┐
│ SnapBack.Windows                            │   Win32 / NT API, no decisions
│  WindowsSystemProbe · WindowsProcessController│
│  VerifiedProcess · RestartManager · Console │
└─────────────────────────────────────────────┘
```

## Data flow

```text
Capture ─► Group ─► Compare with baseline ─► Plan ─► Preview / dry run ─► Graceful close ─► (Force)
snapshot   apps     InBaseline / Restarted /   candidates  same steps,       WM_CLOSE / Ctrl+C /   separate,
                    NewInstance / New          + steps     live guard        Restart Manager       authorized
```

### 1. Snapshot (`ISystemProbe`, `WindowsSystemProbe`)

- One `NtQuerySystemInformation(SystemProcessInformation)` call returns every process with PID,
  parent PID, **creation time**, session, image name and memory counters — without opening any
  handle. This makes the snapshot consistent and covers processes SnapBack cannot open.
- For processes in SnapBack's own session, a `PROCESS_QUERY_LIMITED_INFORMATION` handle adds the
  executable path, token owner, elevation, integrity level, critical flag, PPL protection level,
  package name and (for interpreters only) a command-line hint. The handle's creation time must match
  the snapshot, otherwise the data is discarded.
- Window information comes from one `EnumWindows` pass: visible "main" windows (what Alt+Tab shows),
  hidden top-level windows (tray apps), hung state and titles. UWP frames are attributed to the app
  that owns the hosted `CoreWindow`.
- Service PIDs come from the Service Control Manager (`EnumServicesStatusEx`, read-only).
- File metadata (description, company, PE subsystem) is cached per path.

Unknown values stay `null`, and the classifier treats `null` as "protect".

### 2. Identity

`ProcessIdentity = (PID, creation time as FILETIME)`. It is used everywhere: baselines, plans, the
guard, force authorizations, Restart Manager registration (`RM_UNIQUE_PROCESS` is the same pair).

### 3. Classification (`ProcessClassifier`)

A pure function `ProcessInfo × SessionContext → SafetyAssessment (level + reasons)`:

1. **Protected** rules first: kernel/PID 0/4, SnapBack itself, other session, other user, critical,
   PPL, service host, shell/session/security components (by name or package), anything under the
   Windows directory, elevated, and every "could not verify" case.
2. **Manual review**: console subsystem, interpreters/runtimes, VMs, installers/updaters, apps that
   launched SnapBack, Windows user tools.
3. **Caution**: editors/IDEs/office/creative apps, unsaved-changes window titles, browsers, terminals,
   sync/transfer, launchers, recording, not responding.

### 4. Grouping (`AppGrouper`)

Processes are sorted by creation time and attached to their parent's group when they are clearly the
same application:

- same executable as the parent or the group root (Chrome, Electron apps, VS Code);
- a generic helper host (`msedgewebview2.exe`, `conhost.exe`, crash handlers);
- inside the group's own install folder **and** same publisher (or no publisher), e.g.
  `steamwebhelper.exe` under the Steam folder — but a game in `steamapps` from another publisher
  becomes its own app "started by Steam".

The install-folder rule never applies below protected roots or to shared folders (Program Files,
AppData, the Windows directory, …). Parent links require the parent to be older than the child.

Each group has an **app key**: the normalized executable path with version folders replaced by
wildcards (`Discord\app-1.0.9165\` → `Discord\app-*\`, Store package versions, numeric version
folders), or the package family for packaged apps. App keys survive restarts and self-updates.

The group's assessment is the root's assessment; other members can add reasons but raise the level to
*Caution* at most. Protected members (e.g. a `conhost.exe` inside a Python group) are excluded from
all actions. Window presence decides between *runs in background* (hidden windows — can be asked via
Restart Manager) and *no window* (manual review).

### 5. Baseline and comparison (`BaselineFactory`, `BaselineComparer`)

A baseline stores, per app key: display name, executable, the root identity of each instance, process
count and memory — plus user SID, session, boot time and creation time.

Comparison:

1. Same boot (boot time within 5 minutes, or any baseline root still alive): instances whose root
   identity is in the baseline are **InBaseline**. New child processes are simply members of that
   group — never a reason to close it.
2. Per app key, baseline instances that disappeared leave "vacancies"; newer instances fill them as
   **RestartedSinceBaseline** (the app was restarted or updated).
3. Remaining instances of a baseline app are **NewInstanceOfBaselineApp**; everything else is **New**.
4. New groups whose launcher group is part of the baseline (and not protected, i.e. not Explorer) are
   marked **started by** that app.

A baseline from a previous boot still works, matched by app key only, and the UI suggests refreshing it.

### 6. Planning (`CleanupPlanner`, `CloseStrategy`)

Each group becomes a `PlanItem` in one of three lists: **Candidates**, **Kept** (baseline / keep list),
**Protected**. Candidate levels include comparison reasons (extra instance, started by a baseline app
→ at least Caution). Only *Safe* items are pre-selected (optionally *Caution*; never *Manual review*).

`CloseStrategy` produces the exact steps for each candidate, and is shared with the executor so the
preview cannot diverge from what happens:

| Member | Step |
|---|---|
| has visible main windows | `CloseWindows` (`WM_CLOSE`, like clicking ✕) |
| root with console subsystem | `ConsoleInterrupt` (Ctrl+C, only if the console is not shared with unrelated programs) |
| nothing above applies, has (hidden) windows | `RestartManager` (`RmShutdown` without force) |
| root with no windows at all | `NoGracefulMethod` (reported, not killed) |
| other members | `ExitsWithApp` |
| protected members | `LeftAlone` |

### 7. Execution (`CleanupOperation`)

One operation per confirmation. Graceful phase:

1. **Preflight** — every actionable member is re-inspected and passed through `SafetyGuard`; exited or
   recycled processes are dropped, refusals become notes, a refused root skips the whole app.
2. **Request** — close requests / Ctrl+C / Restart Manager as planned.
3. **Wait** — polls state and windows. An app whose windows disappeared but which is still running
   (tray) is asked again via Restart Manager after a short delay. Apps finish early when nothing more
   can happen; otherwise until the timeout (default 20 s).
4. **Result** — `Closed`, `PartiallyClosed` (main process gone, helpers remain), `StillRunning` with
   a reason (*waiting for user*, *not responding*, *declined*, *no graceful method*, *shared console*,
   *did not exit*, *cancelled*), `Skipped`, `AlreadyExited`.

`Recheck()` later notices apps you closed yourself (e.g. after answering a save prompt).

Force phase (`ForceTerminateAsync`) is a separate call that requires a `ForceAuthorization` created
from the user's confirmation. It covers exact identities, expires after 5 minutes, and is only valid
for processes that went through the graceful phase. Each process is re-inspected, guarded and then
terminated through a verified handle.

A **dry run** evaluates every planned step against live data through the same guard without acting.

### 8. Windows actions (`WindowsProcessController`, `VerifiedProcess`)

Every action starts by opening the target and verifying — on that handle — creation time, not exited,
executable path, not a Windows component, not critical, not PPL, same session, same user, not
elevated. The action is performed while the handle is held, so the PID cannot be recycled meanwhile.
Restart Manager additionally checks `RmGetList` (only registered PIDs, our session, window apps only)
and never passes `RmForceShutdown`. Ctrl+C attaches to the target's console, inspects all attached
processes, ignores the event itself, and detaches.

## Persistence

`%LOCALAPPDATA%\SnapBack` (or `SNAPBACK_DATA_DIR`): `settings.json`, `baseline.json`,
`history.json`, `logs\snapback-YYYYMMDD.log`. Writes are atomic (temp file + replace). A corrupt
file is moved aside (`*.corrupt-<timestamp>`) and defaults are used. Settings are clamped on load.

## Automatic baseline

`SnapBack.exe --auto-baseline` (registered in the per-user Run key only when enabled) runs headless:
`StartupSettleDetector` waits until a minimum time after sign-in has passed **and** the set of user
apps has been stable for a quiet period (or a maximum wait expires), then creates the baseline and
exits. An existing baseline from the same boot is kept.

## Extending SnapBack

- **New known apps**: add names to `KnownProcesses` (with a test in `ClassifierTests`).
- **New close methods**: add a `StepKind`, extend `CloseStrategy`, implement it in
  `IProcessController` (with handle verification), and cover it in `CleanupOperationTests`
  using `FakeSystem`.
- **Other front-ends** (tray icon, scheduled cleanup, PowerShell module): use `SnapBackEngine`; it
  already exposes analysis, baselines, dry runs, operations and history.
- **Localization**: user-facing strings live in the view models and `RiskReasons`; moving them to
  resources is a self-contained change.
