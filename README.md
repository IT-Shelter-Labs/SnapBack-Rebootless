# SnapBack

**English** · [Русский](README.ru.md) · [简体中文](README.zh-CN.md)

**Return your Windows session to its fresh-start state — without rebooting.**

Right after you sign in, Windows is in a fairly clean state: system processes, your startup apps,
nothing else. A few hours later there are browsers, Discord, Telegram, an IDE, Python and Node.js
processes, launchers and background helpers. SnapBack remembers the clean state (the *baseline*),
shows you what has appeared since, and closes the extra applications **the polite way** — the same way
you would by clicking ✕, so apps can save your work or ask you first.

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
- **No administrator rights for normal use, no driver, no services.** The only elevated code path is
  the optional Deep Memory Cleanup helper described above.

## Requirements

- Windows 10 (1809+) or Windows 11, x64 or ARM64.
- Release builds are self-contained — no separate .NET installation is needed.

## Installation

Download `SnapBack-<version>-win-x64.zip` (or `win-arm64`) from the [Releases](../../releases) page,
extract it anywhere (for example `%LOCALAPPDATA%\Programs\SnapBack`) and run `SnapBack.exe`. Verify
the download with `SHA256SUMS.txt` if you like.

There is no installer and nothing is registered system-wide. To uninstall, disable the automatic
baseline in Settings (if you enabled it), then delete the folder and `%LOCALAPPDATA%\SnapBack`.

## Using SnapBack

- **Make current state the baseline** — what is running now becomes the clean state.
- **New since baseline / Kept / Protected** — the three lists. Click a column header (*Application*,
  *Memory*, *Processes*, *Assessment*) to sort; click again to reverse. Memory is sorted by the real
  number of bytes. Use the search box (Ctrl+F) to filter by app name, executable or script name.
  Your selection is kept while sorting, searching and refreshing.
- **Return to baseline…** — opens the preview; nothing happens until you confirm there.
- **Always keep selected** — adds apps to your keep list (whitelist).
- **Memory** — memory details, advice and the optional Deep Memory Cleanup.
- **History** — recent operations: what was closed, what remained, memory released.
- **Settings** — language, close timeout, pre-selection of "caution" apps, automatic baseline, keep
  list, data folder.

### Language

English, Русский and 简体中文 are built in. *Settings → Language* offers *System default* (follows
the Windows display language; unsupported languages fall back to English) or a fixed language. The
change applies immediately, without restarting.

### Automatic baseline (optional)

Enable *Create a baseline automatically after I sign in* in Settings. SnapBack then adds itself to
your *Startup apps* (the per-user `Run` key — its only registry write) and, at sign-in, waits until
at least 2 minutes have passed **and** no new apps have started for 45 seconds (both configurable,
with a 10-minute cap), takes the baseline and exits. It never closes anything in this mode.

## Command line

`snapback-cli.exe` shares the engine and all safety checks with the window. Its output is English.

```text
snapback-cli status                       baseline, running apps, memory, what is new
snapback-cli baseline create [--force]    make the current state the baseline
snapback-cli baseline show [--all] [--json]
snapback-cli list [--all] [--json]        apps started after the baseline
snapback-cli preview [selection] [--json] dry run: every step + live safety verdict
snapback-cli restore [selection] [--yes]  ask the selected apps to close (never forced)
snapback-cli history [--json]
snapback-cli memory [--json]              memory in use vs. reusable cache, with advice

selection: --app <name> (repeatable) | --include-caution | --include-review
```

The CLI deliberately **cannot** force-terminate anything and does not run Deep Memory Cleanup.

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

The platform-independent core and its 240+ tests also build and run on Linux and macOS
(`dotnet test tests/SnapBack.Core.Tests`). CI builds on Windows, runs unit and integration tests,
publishes the app, and drives the real GUI through UI Automation in all three languages.

Translations live in `src/SnapBack.Core/Localization/Strings*.resx` (engine texts) and
`src/SnapBack.App/Localization/Strings*.resx` (UI texts). Tests verify that every language has every
string with the same placeholders.

## Project layout

```text
src/
  SnapBack.Core/      platform-independent engine: model, classification, grouping, baselines,
                      planning, safety guard, cleanup orchestration, memory analysis, deep-cleanup
                      protocol, sorting/search, localization, persistence, logging
  SnapBack.Windows/   Win32 implementation: process snapshot, windows, services, Restart Manager,
                      console Ctrl+C, handle-verified actions, memory counters, standby purge
  SnapBack.App/       WPF desktop app (Fluent theme, light/dark, three languages)
  SnapBack.Cli/       command-line interface
tests/                unit tests, Windows integration tests, a test target app
ci/ui-smoke.ps1       UI Automation smoke test used by CI
docs/                 ARCHITECTURE.md, SAFETY.md
```

## Data and privacy

Everything lives in `%LOCALAPPDATA%\SnapBack` (override with the `SNAPBACK_DATA_DIR` environment
variable): `settings.json`, `baseline.json`, `history.json` and daily logs in `logs\` (kept for 14
days). Window titles and command lines are shown in the UI only; they are never written to disk or to
the logs. SnapBack makes no network connections.

## FAQ

**Will it close my browser and lose my tabs?** Browsers are never pre-selected. If you select one, it
receives the same close request as clicking ✕ and restores its tabs as usual next time.

**An app asked me to save and SnapBack says "still open".** Intended: SnapBack waits for *you*. Answer
the prompt, then press *Check again*.

**Available memory barely changed after Deep Memory Cleanup — is it broken?** No. The standby cache
was already counted as available; the cleanup only moved it to "free". That is why SnapBack never
recommends it as necessary.

**Why does Deep Memory Cleanup ask for administrator permission?** Windows only lets elevated
processes purge the standby list. SnapBack asks for that one operation only, through a helper that
can do nothing else.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Safety-relevant changes need tests and a short rationale.
Please report security issues privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
