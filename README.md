# SnapBack

**English** · [Русский](README.ru.md) · [简体中文](README.zh-CN.md)

**Return your Windows session to its fresh-start state — without rebooting.**

Right after you sign in, Windows is in a fairly clean state: system processes, your startup apps,
nothing else. A few hours later there are browsers, Discord, Telegram, an IDE, Python and Node.js
processes, WSL, Docker, launchers and background helpers. SnapBack records the state you want (the
*baseline*, or several named *profiles* such as *Clean*, *Work* or *Gaming*), shows how far the session
has drifted from it and what actually uses your memory, and brings the session back **the polite
way** — closing extra applications the same way you would by clicking ✕, so apps can save your work
or ask you first.

SnapBack is **not** a "RAM cleaner" or a "PC booster". Its main job is to close applications you
started after the baseline, with your consent. An optional, clearly separated *Deep Memory Cleanup*
exists for the rare cases where you explicitly want to drop the Windows file cache.

---

## How it works

1. **Baseline.** Click *Make current state the baseline* (or let SnapBack take one automatically a
   few minutes after you sign in, once your startup apps have finished launching). The baseline
   records *applications*, not just process IDs.
2. **Compare.** SnapBack groups processes into applications (Chrome's 30 `chrome.exe` processes are
   one app; VS Code with its helpers is one app) and compares them with the baseline. An app that was
   in the baseline stays there even after a restart or an update; new child processes of a baseline
   app are never a reason to close it.
3. **Review.** You see what appeared since the baseline — icon, memory, process count and a risk
   assessment — and can sort by any column and search:

   | Application           | Memory | Assessment                 |
   |-----------------------|-------:|----------------------------|
   | Discord               | 650 MB | Safe                       |
   | Visual Studio Code    | 1.4 GB | Possible unsaved data      |
   | python.exe — train.py | 740 MB | Manual review              |
   | Google Chrome         | 3.2 GB | Was present in baseline    |

   Only apps marked **Safe** are pre-selected. You can untick anything, or add apps to your
   *Always keep* list.
4. **Preview.** Before anything happens, a preview lists every single step SnapBack will take. A
   **dry run** re-checks every step against the live system without acting.
5. **Close politely.** Selected apps are asked to close the normal way and get time to save or to ask
   you. Nothing is force-terminated.
6. **Decide.** Apps that did not close (for example because they ask "Save changes?") are reported.
   You can switch to them and press *Check again*, or — separately and explicitly — force-terminate
   specific ones.
7. **Before → After.** The result shows apps, processes, RAM in use, available memory and cache
   before and after, plus the memory released by the apps that closed.

## Dashboard: drift and expected impact

The top of the window shows what you need to decide, not a full system monitor:

```text
Profile: Clean            Session uptime: 10 h 21 min
Session drift   Apps   RAM in use   New since baseline   Expected impact
High            31     18.4 GB      12 apps              High
+12 · +87 ·     286    available    7.1 GB               ~7.1 GB from
+7.1 GB         proc.  13.2 GB                           9 selected apps
```

- **Session drift** (*Low / Medium / High*) — how far the session has moved away from the profile.
  A score from 0 to 100 adds four capped parts: new applications (4 points each, up to 40), the
  memory they use relative to installed RAM (2 points per percent, up to 40), extra processes
  (1 point per 8, up to 10) and the age of the baseline (up to 10 after a day, only while something is
  new). Below 20 is low, below 50 medium, above that high. Cached memory is never counted.
- **Expected impact** — an estimate made *before* anything is closed: the private memory of the
  apps that would be closed, compared with installed memory (10 % or more is high, 4 % or more — or
  eight apps and more — medium). Its tooltip and the preview break *RAM in use* down into new apps,
  profile apps and system/other, and show the cache separately because it is already available:

  ```text
  RAM in use: 23.4 GB
    New apps:          7.8 GB
    Profile apps:      4.9 GB
    System and other: 10.7 GB
  Cache / standby: 7.0 GB (already available, not in use)
  Estimated reclaim: ~7.8 GB      Expected impact: High
  ```

  When most memory is not used by apps added after the baseline, SnapBack says so — and it never
  promises an exact number: shared memory, apps that refuse to close and Windows' cache make the real
  result differ.

## Profiles

Instead of one baseline you can keep several profiles — for example *Clean*, *Work*, *Gaming*,
*Streaming* — each with its own list of apps that should stay running. In **Profiles** you can create
a profile from the current state, rename it, update it from the current state, remove or add
individual apps, delete it and choose the default profile. The profile picker in the main window
selects which profile the session is compared with; choosing a profile closes nothing.

*Prepare for Gaming* is then just: select *Gaming* (Steam, Discord, GPU tools, the launcher) and press
*Return to “Gaming”* — the usual preview offers to close the IDE, extra browsers and work apps. A
profile can also suggest stopping WSL and Docker (see below); you confirm that every time. There are
no hard-coded profiles: the suggested names are just editable suggestions.

The single `baseline.json` of earlier versions becomes the profile *Clean* automatically.

## Rules: always keep, always close, ask every time

Right-click an app in the list (or use *Settings → Rules*):

- **Always keep** — never offered for closing, in any profile.
- **Always close** — pre-selected whenever you return to a profile, even if it was running when the
  profile was captured.
- **Ask every time** — offered, but never pre-selected.

Rules only decide what is offered and ticked. They never change protection, never skip the preview,
the polite close or the live safety checks, and *Always close* never forces anything.

## Memory over time

While SnapBack runs it notes the committed memory of each running app at most every 10 minutes (one
sample per analysis it does anyway; at most 96 samples per app and 64 apps, kept in memory only). The
**Memory** window shows apps whose memory keeps growing:

```text
Discord              Unusual memory growth
420 MB → 1.4 GB in 9 h
10:00 420 MB · 13:00 610 MB · 16:00 940 MB · 19:00 1.4 GB
```

*Unusual memory growth* means: over at least two hours, at least +500 MB and ×1.5, rising in most
steps. SnapBack never calls it a leak — caches and more work (tabs, documents) grow too; restarting
the app releases the memory.

## WSL and Docker

WSL (`vmmemWSL`/`vmmem`, `wslhost`, `wslservice`) and Docker Desktop (`Docker Desktop.exe`,
`com.docker.*`, `vpnkit`) are recognized and **never closed as ordinary apps** — they are protected.
When they run, the dashboard shows them with their memory and their own stop action:

- **Shut down WSL…** runs `wsl --shutdown`.
- **Stop Docker…** runs `docker desktop stop` (Docker Desktop 4.37 or newer; older versions are
  reported as not supported — quit Docker Desktop from its tray icon).

Before stopping, SnapBack lists what will stop — running distributions (`wsl --list --running`) or
running containers (`docker ps`) — and warns that Linux services, containers and unsaved work inside
them stop too. Right before running the command it checks again; if something new started in the
meantime, nothing is stopped and you are asked again. SnapBack never kills `vmmem` or Docker
processes, and it only runs these read-only commands while the environment is already running, so it
never starts WSL or Docker itself.

## Memory: what is really used, what is just cache

Windows keeps recently used data in memory as a **standby cache**. That cache counts as
**available**: Windows hands it to any app that needs memory, instantly. A high "Cached" number is
normal and healthy, and "free" memory is simply unused memory.

SnapBack therefore reports both sides honestly:

- **RAM in use** and **Available** — what apps and the system really occupy, and what is available.
- **Cached (standby + modified)** — reusable cache.
- **Memory released by closing apps** — the private memory of the apps that exited. This is the real
  effect of *Return to baseline*.

The **Memory** window explains the current situation in plain words. When plenty of memory is
available it says so and does not suggest any cleanup; when memory is low it explains that closing
apps is what helps.

### Deep Memory Cleanup (optional, advanced)

A separate action in the Memory window (also reachable from the result of *Return to baseline*). It is
**never run automatically** and is **never part of Return to baseline**.

- **Mechanism:** `NtSetSystemInformation(SystemMemoryListInformation, …)` with the standby-list
  purge commands — the same mechanism Sysinternals RAMMap uses for *Empty Standby List*. Two modes:
  *entire standby cache* (`MemoryPurgeStandbyList`) or *low-priority standby only*
  (`MemoryPurgeLowPriorityStandbyList`).
- **Clears:** standby pages (cached data that is not in use). They become free memory immediately.
- **Never touches:** memory that applications use (no working-set trimming, no `EmptyWorkingSet`),
  the modified list (data waiting to be written to disk), committed memory, system settings,
  services or the page file. Only the two purge commands can ever be sent; the others are rejected
  in code and in tests.
- **Effect:** *Available* memory barely changes — the cache was already available. Windows rebuilds
  the cache as you work, so apps and files may open a little slower for a while. SnapBack shows the
  cache reclaimed separately from the memory released by closing apps; the two are never added up.
- **Rights:** the purge needs administrator rights and `SeProfileSingleProcessPrivilege`. SnapBack
  itself keeps running as a normal user: for this one operation Windows shows a UAC prompt and
  starts a short-lived helper (`SnapBack.exe --deep-cleanup standby|low-priority`). The helper
  accepts only these two fixed values, reads and writes no files, and reports through its exit code
  only. Declining the prompt changes nothing.

## Safety first

SnapBack is built around one assumption: **closing the wrong process is far worse than closing too
little**. Details: [docs/SAFETY.md](docs/SAFETY.md).

- **Only your own apps in your own session.** Processes of other users, other sessions, services,
  elevated (administrator) processes, critical and protected processes and Windows components are
  **protected**. No setting can unprotect them.
- **Fail closed.** If any safety-relevant fact about a process cannot be verified (owner, path,
  elevation, critical flag, protection level), the process is protected.
- **PIDs are never trusted alone.** Every process is identified by *PID + creation time*. Identity,
  executable, session, owner and protection are re-verified **on the very handle the action is
  performed through**, so a recycled PID can never be hit.
- **Three independent layers:** classification, a safety guard that re-checks the live process right
  before each action, and handle verification in the Windows layer. A UI bug cannot get a protected
  process closed.
- **Polite by default, forced only on request.** Window apps get a normal close request
  (`WM_CLOSE`), tray apps are asked through Windows Restart Manager without the force flag, console
  programs get Ctrl+C only if no unrelated program shares their console. Forced termination needs a
  separate confirmation that covers exact process identities and expires after five minutes.
- **Profiles and rules cannot weaken any of this.** *Always close* only pre-selects; protected
  processes stay protected; WSL and Docker are stopped only with their own commands after a warning.
- **No administrator rights for normal use, no driver, no services.** The only elevated code path is
  the optional Deep Memory Cleanup helper described above.

## Requirements

- Windows 10 (1809+) or Windows 11, x64 or ARM64.
- Release builds are self-contained — no separate .NET installation is needed.

## Installation

Download `SnapBack-<version>-win-x64.zip` (or `win-arm64`) from the [Releases](../../releases) page,
extract it anywhere (for example `%LOCALAPPDATA%\Programs\SnapBack`) and run `SnapBack.exe`. Verify
the download with `SHA256SUMS.txt` if you like.

There is no installer and nothing is registered system-wide. To uninstall, turn off *Start SnapBack
with Windows* in Settings (if you enabled it), exit SnapBack from its tray icon, then delete the
folder and `%LOCALAPPDATA%\SnapBack`.

## Using SnapBack

- **Profile** picker and **Profiles** — which state to compare with; create, rename, update, edit,
  delete profiles and choose the default one.
- **Make current state the baseline** — what is running now becomes the selected profile.
- **New since baseline / Kept / Protected** — the three lists. Click a column header (*Application*,
  *Memory*, *Processes*, *Assessment*) to sort; click again to reverse. Memory is sorted by the real
  number of bytes. Use the search box (Ctrl+F) to filter by app name, executable or script name.
  Your selection is kept while sorting, searching and refreshing. Right-click an app for rules.
- **Return to baseline…** (*Return to “Gaming”…* with several profiles) — opens the preview with the
  expected impact; nothing happens until you confirm there.
- **Always keep selected** — adds an *Always keep* rule for the selected apps.
- **Memory** — memory details, advice, memory over time and the optional Deep Memory Cleanup.
- **History** — per operation: profile, date and time, apps / processes / RAM in use / available
  memory before → after, memory released by apps, apps closed, forced terminations, whether Deep
  Memory Cleanup was used (and how much cache it dropped, shown separately), stopped environments and
  duration. The history keeps the last 50 operations (configurable up to 500).
- **Settings** — language, startup and tray, notifications, close timeout, pre-selection of
  "caution" apps, rules, data folder.

### Notification area (tray)

While SnapBack runs, its icon sits in the notification area. Its tooltip shows the status
(*SnapBack · Clean — 7 extra apps · ~5.4 GB reclaimable*), and its menu offers *Open SnapBack*,
*Return to baseline…* (opens the window and the usual preview — nothing is closed from the menu
directly), *Choose profile*, *Update “profile” from current state…*, *Refresh* and *Exit*.

Closing the window hides it to the notification area (the first time, a notification explains this);
*Settings → When I close the window* can make the close button exit instead. Starting `SnapBack.exe`
again shows the running instance; `SnapBack.exe --quit` asks it to exit.

In the background SnapBack does almost nothing: one analysis every 15 minutes while the window is
hidden, on a background-priority thread, with Windows' efficiency mode (EcoQoS) on. No polling in
between; waiting costs no CPU.

### Notifications

Optional (*Settings → Show notifications*) and deliberately rare: when the drift is high **and**
returning to the profile would reclaim at least 1 GB of app memory while memory is getting scarce (or
the expected impact is high) — at most every 6 hours and only again after the drift has dropped —
and once per app instance for unusual memory growth. Never more than three a day, never while the
window is open, never because of cached memory. Windows' *Do not disturb* applies.

### Start with Windows

*Settings → Startup*:

- **Start SnapBack with Windows** — adds the per-user `Run` entry (`"…\SnapBack.exe" --autostart`),
  visible in *Settings → Apps → Startup*. No administrator rights, no scheduled task, no service. The
  entry follows the setting and is corrected if you move SnapBack.
- **Start minimized to the notification area** — at sign-in no window opens.
- **Automatically capture the baseline after startup** — updates the chosen profile (the default one
  unless you pick another) once per sign-in, after at least 2 minutes, when no new apps have started
  for 45 seconds **and** the CPU is no longer busy (all configurable, with a 10-minute cap). It never
  closes anything.

### Language

English, Русский and 简体中文 are built in. *Settings → Language* offers *System default* (follows
the Windows display language; unsupported languages fall back to English) or a fixed language. The
change applies immediately, without restarting.

## Command line

`snapback-cli.exe` shares the engine and all safety checks with the window. Its output is English.

```text
snapback-cli status [--json]              profile, drift, memory, expected impact, WSL/Docker
snapback-cli baseline create [--force]    make the current state the selected profile
snapback-cli baseline show [--all] [--json]
snapback-cli list [--all] [--json]        apps started after the baseline
snapback-cli preview [selection] [--json] dry run: every step + live safety verdict
snapback-cli restore [selection] [--yes]  ask the selected apps to close (never forced)
snapback-cli history [--json]
snapback-cli memory [--json]              memory in use vs. reusable cache, with advice
snapback-cli profiles [--json]
snapback-cli profile create|update|rename|delete|default|use <name> [<new name>]
snapback-cli rules [--json]
snapback-cli rule keep|close|ask|clear <app>
snapback-cli env [--json]                 WSL / Docker status (read-only)

selection: --app <name> (repeatable) | --include-caution | --include-review
--profile <name>: status/list/preview/restore against that profile (without selecting it)
```

The CLI deliberately **cannot** force-terminate anything, does not run Deep Memory Cleanup and does
not stop WSL or Docker (the window shows the warning first).

## Limitations

- Classification partly relies on known executable names; unknown apps are judged by their windows
  (a window app can always prompt you; a windowless one needs manual review).
- Apps that run as administrator are never closed (by design). If User Account Control is turned off,
  all apps run elevated and SnapBack shows them as protected.
- An app that ignores both the close request and Restart Manager can only be closed by you or by an
  explicit forced termination.
- Deep Memory Cleanup does not make more memory *available*; it only empties a cache that Windows
  would reuse anyway. Most of the time it is not needed.
- Memory list sizes come from Windows performance counters; if those are disabled, SnapBack falls back
  to the system cache size and shows unknown values as "—".
- Drift, expected impact and memory growth are heuristics. Private memory of apps does not add up to
  "RAM in use" exactly (shared memory, drivers, the kernel), which is why the rest is shown as
  *system and other*.
- Memory history exists only while SnapBack runs (it is not saved), so growth shows up after a few
  hours of running in the background.
- Stopping Docker needs Docker Desktop 4.37 or newer (`docker desktop stop`). Docker Engine installed
  inside a WSL distribution stops with WSL. WSL detection uses `vmmemWSL` (Windows 11) or `vmmem`
  together with WSL's helper processes.
- The tray icon needs Explorer's taskbar; without it SnapBack shows its window instead.

## Building from source

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`).

```powershell
git clone https://github.com/IT-Shelter-Labs/SnapBack-Rebootless.git
cd SnapBack-Rebootless
dotnet build SnapBack.sln -c Release
dotnet test SnapBack.sln -c Release
dotnet run --project src/SnapBack.App

# Self-contained single-file build
dotnet publish src/SnapBack.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet publish src/SnapBack.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The platform-independent core and its 300+ tests also build and run on Linux and macOS
(`dotnet test tests/SnapBack.Core.Tests`). CI builds on Windows, runs unit and integration tests,
publishes the app, drives the real GUI through UI Automation in all three languages, and checks the
tray start, the autostart entry, the automatic baseline, idle CPU and memory use, activation of the
running instance, close-to-tray and `--quit`.

Translations live in `src/SnapBack.Core/Localization/Strings*.resx` (engine texts) and
`src/SnapBack.App/Localization/Strings*.resx` (UI texts). Tests verify that every language has every
string with the same placeholders.

## Project layout

```text
src/
  SnapBack.Core/      platform-independent engine: model, classification, grouping, baselines,
                      profiles, rules, planning, safety guard, cleanup orchestration, memory analysis,
                      drift / impact / memory growth, notification policy, WSL/Docker protocol,
                      deep-cleanup protocol, sorting/search, localization, persistence, logging
  SnapBack.Windows/   Win32 implementation: process snapshot, windows, services, Restart Manager,
                      console Ctrl+C, handle-verified actions, memory counters, standby purge,
                      notification-area icon, autostart entry, command runner, background modes
  SnapBack.App/       WPF desktop app (Fluent theme, light/dark, three languages)
  SnapBack.Cli/       command-line interface
tests/                unit tests, Windows integration tests, a test target app
ci/                   UI Automation smoke tests used by CI (GUI, tray/autostart)
docs/                 ARCHITECTURE.md, SAFETY.md
```

## Data and privacy

Everything lives in `%LOCALAPPDATA%\SnapBack` (override with the `SNAPBACK_DATA_DIR` environment
variable): `settings.json`, `profiles.json`, `history.json` and daily logs in `logs\` (kept for 14
days). The memory history is kept in memory only. Window titles and command lines are shown in the UI only; they are never written to disk or to
the logs. SnapBack makes no network connections.

## FAQ

**Will it close my browser and lose my tabs?** Browsers are never pre-selected. If you select one, it
receives the same close request as clicking ✕ and restores its tabs as usual next time.

**An app asked me to save and SnapBack says "still open".** Intended: SnapBack waits for *you*. Answer
the prompt, then press *Check again*.

**Available memory barely changed after Deep Memory Cleanup — is it broken?** No. The standby cache
was already counted as available; the cleanup only moved it to "free". That is why SnapBack never
recommends it as necessary.

**Why is Docker Desktop in the "Protected" list?** Its processes are stopped with Docker's own
command (*Stop Docker…*), never by closing them one by one — that could corrupt its VM or leave
containers half-stopped.

**SnapBack keeps running after I close the window.** That is the notification-area mode; exit from
the tray icon's menu, or choose *Exit SnapBack* under *Settings → When I close the window*.

**Why does Deep Memory Cleanup ask for administrator permission?** Windows only lets elevated
processes purge the standby list. SnapBack asks for that one operation only, through a helper that
can do nothing else.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Safety-relevant changes need tests and a short rationale.
Please report security issues privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
