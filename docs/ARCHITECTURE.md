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
│  Baselines · Profiles · Rules · Planning ·  │
│  Safety · Execution · Memory · Insights ·   │
│  Environments · Presentation · Localization │
│  Persistence · Logging · Startup            │
│        ▲ ISystemProbe   ▲ IProcessController│
└────────┼────────────────┼───────────────────┘
         │                │
┌────────┴────────────────┴───────────────────┐
│ SnapBack.Windows                            │   Win32 / NT API, no decisions
│  WindowsSystemProbe · WindowsProcessController│
│  VerifiedProcess · RestartManager · Console │
│  MemoryInfoReader · StandbyListPurger       │
│  ShellNotifyIcon · AutostartRegistration    │
│  ProcessCommandRunner · ResourceModes       │
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

### 9. Memory analysis (`MemoryStatus`, `SessionStatistics`, `RestoreComparison`, `MemoryAdvisor`)

`WindowsSystemProbe.GetMemoryStatus` combines `GlobalMemoryStatusEx` (total, available),
`GetPerformanceInfo` (commit charge, system cache) and the PDH counters of the *Memory* object
(standby lists, modified list, free & zero list) — all readable by a standard user. Unknown values stay
`null`.

The operation window captures `SessionStatistics` (apps, processes, memory) from the analysis the user
confirmed, and again ~1.5 s after the graceful phase (and after a forced termination or *Check
again*). `RestoreComparison` turns the two into *Before → After* rows. The memory released by closing
apps is the sum of the private working sets of the processes that exited; it is reported on its own
line. `MemoryAdvisor` classifies the situation by **available** memory (which includes the standby
cache) into Healthy / Moderate / Low and explains it; it never recommends Deep Memory Cleanup.

### 10. Deep Memory Cleanup (`DeepCleanupService`, `MemoryCleaner`, `DeepCleanupHelper`)

```text
MemoryWindow ─► DeepCleanupService ─► MemoryCleaner ─┬─ elevated? ─► StandbyListPurger (in process)
  (button +       measure before/after               └─ otherwise ─► ShellElevatedRunner ("runas")
   confirmation)                                                       └─► SnapBack.exe --deep-cleanup <mode>
                                                                            └─► DeepCleanupHelper.Run ─► StandbyListPurger
                                                                                 exit code ─► outcome
```

- `MemoryListCommand` maps the two modes to `MemoryPurgeStandbyList` (4) and
  `MemoryPurgeLowPriorityStandbyList` (5) and rejects every other command.
- `StandbyListPurger` enables `SeProfileSingleProcessPrivilege` on its own token and calls
  `NtSetSystemInformation(SystemMemoryListInformation, &command, sizeof(int))`.
- The helper protocol is two fixed tokens in, one exit code out; `App.OnStartup` handles it before
  anything else (no window, no single-instance lock).
- `DeepCleanupReport` computes the cache reclaimed as the decrease of the standby list (or the increase
  of free memory when list sizes are unknown). It is kept separate from `RestoreComparison`'s numbers.
- Neither `CleanupOperation` nor `SnapBackEngine` can reach the cleaner; a test enforces this.

### 11. Lists: sorting, search, selection (`AppListOrdering`, `SelectionState`)

Pure functions in Core, used by the main view model:

- `Sort` by name (culture-aware, case-insensitive), memory (bytes — never formatted text),
  process count or risk rank (Safe < Caution < Manual review < Kept < Protected), ascending or
  descending, with stable tie-breakers.
- `Filter` matches every search term against the app name, the executables of all its processes, the
  detail hint (e.g. a script name) and its risk labels.
- `SelectionState` stores explicit choices by app-instance identity, so sorting, searching and
  refreshing (which create new `PlanItem` objects) never lose them; untouched items follow the
  planner's default; non-selectable items can never be selected; choices for vanished instances are
  pruned.

### 12. Localization

- `Loc` (Core) resolves keys from `SnapBack.Core/Localization/Strings*.resx` with an explicit culture
  (not thread-bound, so background work produces text in the chosen language) and falls back to
  English, then to the key itself.
- `Tr` (App) adds the UI resources (`SnapBack.App/Localization/Strings*.resx`) and is bindable:
  XAML uses `{l:Tr Key}`, a binding to `Tr.Instance[Key]`. When `Loc.CultureChanged` fires, the
  indexer notifies, every binding updates, and the main view model re-analyzes so that engine-made
  texts (step descriptions, summaries) are regenerated. No restart is needed.
- `Languages.Resolve` maps the setting (`system`, `en`, `ru`, `zh-CN`) and the Windows display
  language to a supported culture (`ru-*` → ru; `zh-CN`/`zh-SG`/`zh-Hans` → zh-CN; anything else →
  English).
- Decisions never depend on translated text. Tests check that all languages contain the same keys and
  placeholders, that every key used in code exists, and the fallback rules. The CLI stays English for
  stable scripting.

### 13. Profiles (`Profile`, `ProfileSet`, `ProfileStore`)

- A profile is a named `Baseline` plus environment suggestions (`Wsl`/`Docker`: keep or suggest
  stopping). `ProfileSet` holds all profiles, the default one (startup, automatic baseline) and the
  selected one (what the session is compared with). Selecting a profile closes nothing.
- The engine analyzes against the selected profile (`Analyze()`) or a given one
  (`Analyze(snapshot, profileId)` / `AnalyzeFor`). Comparison and planning are unchanged: a profile is
  just another baseline, so every safety property of the baseline path applies.
- Create (from the current state), update (re-capture, keeping name and options), rename (cleaned and
  made unique), add/remove apps, delete, set default, select. `ProfileSet.Normalize` repairs loaded
  data (duplicate IDs, missing baselines, dangling default/current IDs, invalid enums).
- Migration: if `profiles.json` does not exist, the 0.2 `baseline.json` becomes the profile *Clean*
  (the old file is left as it is).

### 14. Rules (`AppRule`, `PlannerOptions.Rules`)

- Per app key: *AlwaysKeep*, *AlwaysClose*, *AskEveryTime*, stored in settings (the 0.2 keep list is
  migrated into *AlwaysKeep* rules).
- Precedence in `CleanupPlanner`: protection first (no rule can change it) → *AlwaysClose* beats
  profile membership → profile membership or *AlwaysKeep* keeps the app → everything else is a
  candidate. *AlwaysClose* pre-selects, *AskEveryTime* never pre-selects. Rules change neither the
  risk level nor the close steps, and `SafetyGuard`/`VerifiedProcess` still re-check every action.

### 15. Insights (`SessionDrift`, `ImpactEstimate`, `MemoryTracker`, `NotificationPolicy`)

- `SessionDrift.Compute(analysis, now)`: score 0–100 = new apps ×4 (≤40) + new-app private memory
  as % of RAM ×2 (≤40) + extra processes ÷8 (≤10) + baseline age (≤10 after 24 h, only while
  something is new). < 20 low, < 50 medium, else high. Session uptime is estimated from the oldest
  process of the user in the session (sign-in), which also works with Fast Startup.
- `ImpactEstimate.Compute(analysis, itemsToClose)`: breakdown of *in use* (= total − available) into
  new apps, profile/kept apps and the rest; cache (standby + modified) reported separately; estimate
  = private memory of the selectable items; level by share of RAM (≥10 % high, ≥4 % or ≥8 apps
  medium). Explanations are generated in the UI language.
- `MemoryTracker`: fed by every analysis (`SnapBackEngine.Analyze`), at most one sample per 10
  minutes; one series per app instance (root identity), committed private bytes; ≤96 samples per
  series (the older half is thinned when full), ≤64 series (largest kept), exited instances dropped,
  protected processes never tracked; in memory only. `Assess` flags *Growing* (≥200 MB, ×1.3) or
  *Unusual* (≥2 h, ≥500 MB, ×1.5, ≥70 % rising steps, still near its maximum) using medians of the
  first/last three samples.
- `NotificationPolicy`: high drift (with ≥1 GB reclaimable and scarce memory or high impact; 6 h
  cooldown; re-armed only after the drift drops), unusual growth once per instance (3 h cooldown),
  ≤3 per day, none while the window is visible or when disabled.

### 16. WSL and Docker (`EnvironmentDetector`, `EnvironmentService`)

- Detection from the snapshot only: WSL = `vmmemWSL`, or `vmmem` together with `wslservice`/`wslhost`/
  `wslrelay`; Docker = `com.docker.backend.exe` or `Docker Desktop.exe` in the user's session. These
  processes carry `RiskReason.ManagedEnvironment` (protected), so they are never candidates.
- `EnvironmentService.InspectAsync` runs read-only commands through `ICommandRunner` only when the
  environment is already running: `wsl --list --running --quiet` (`WSL_UTF8=1`, UTF-16 tolerant),
  `docker desktop status` (support for `docker desktop stop`) and `docker ps --format {{.Names}}`.
- `StopAsync(EnvironmentStopConfirmation)`: the confirmation is created from what the user was shown
  and expires after 5 minutes. Right before stopping, the environment is inspected again; if a
  distribution/container appeared that was not shown (or the list became unknown), nothing is done
  (`Changed`). Then exactly `wsl --shutdown` or `docker desktop stop` runs (fixed arguments, no shell,
  timeouts; the command is never killed), and the process list is polled until the environment is
  gone. Nothing ever terminates `vmmem` or Docker processes.
- `ProcessCommandRunner` (Windows) starts the tool directly with an argument list, no window, captured
  output; `EnvironmentToolLocator` finds `%SystemRoot%\System32\wsl.exe` and Docker Desktop's
  `docker.exe` (then `PATH`).

### 17. Tray, background mode, autostart (App)

- `TrayController`: hidden top-level `HwndSource` (not message-only, so it receives `TaskbarCreated`
  and re-adds the icon after Explorer restarts) + `ShellNotifyIcon` (`Shell_NotifyIconW`,
  `NOTIFYICON_VERSION_4`, tooltip, `NIIF_RESPECT_QUIET_TIME` notifications). The context menu is a WPF
  `ContextMenu` built on open (current language and profiles). Menu actions only open the window,
  select a profile, refresh or exit; "Return to baseline" opens the normal preview.
- `App` owns one `MainViewModel` for the whole run; the window is created on demand, the close button
  hides it (or exits, per setting), `ShutdownMode = OnExplicitShutdown`. `SingleInstance` uses a
  mutex plus two named events: activate (second launch) and exit (`--quit`). Exit is refused while an
  operation is running.
- `BackgroundMonitor`: `engine.Analyzed` → tooltip + notification policy; one analysis every 15
  minutes while the window is hidden, on a thread in `THREAD_MODE_BACKGROUND_BEGIN`; EcoQoS
  (`ProcessPowerThrottling`) is on while hidden. Nothing polls in between.
- Autostart: `AutostartRegistration.Sync` keeps the per-user Run value (`"…\SnapBack.exe" --autostart`)
  equal to the setting (also fixing a moved path or the 0.2 `--auto-baseline` entry). With
  `--autostart`: tray only if *start minimized* is on; if *automatic baseline* is on,
  `AutoBaselineRunner` waits for `StartupSettleDetector` (min. time since sign-in, quiet period of
  unchanged apps **and** CPU below 50 %, max. wait) and updates the target profile once per sign-in
  (`AutoBaselinePolicy`).

## Persistence

`%LOCALAPPDATA%\SnapBack` (or `SNAPBACK_DATA_DIR`): `settings.json` (schema 2: rules, startup,
tray, notifications), `profiles.json`, `history.json` (one record per operation, upserted by
operation ID: profile, before/after statistics, forced terminations, Deep Cleanup, stopped
environments; trimmed to the history limit), `logs\snapback-YYYYMMDD.log`. Writes are atomic (temp
file + replace). A corrupt file is moved aside (`*.corrupt-<timestamp>`) and defaults are used.
Settings are clamped on load.

## Extending SnapBack

- **New known apps**: add names to `KnownProcesses` (with a test in `ClassifierTests`).
- **New close methods**: add a `StepKind`, extend `CloseStrategy`, implement it in
  `IProcessController` (with handle verification), and cover it in `CleanupOperationTests`
  using `FakeSystem`.
- **Other front-ends** (scheduled cleanup, PowerShell module): use `SnapBackEngine`; it exposes
  analysis, profiles, dry runs, operations, history and the memory tracker.
- **More managed environments** (e.g. Podman, Hyper-V VMs): add detection to `EnvironmentDetector`,
  protection to `KnownProcesses.ManagedEnvironmentProcesses`, and inspect/stop commands with the same
  confirmation-and-recheck protocol in `EnvironmentService`.
- **New language**: add `Strings.<culture>.resx` next to both existing resource sets, add the code to
  `Languages.Supported`/`NativeName`/`FromSystem`, and extend `Cultures` in `LocalizationTests`.
