# SnapBack

**Return your Windows session to its fresh-start state — without rebooting.**

Right after you sign in, Windows is in a fairly clean state: system processes, your startup apps,
nothing else. A few hours later there are browsers, Discord, Telegram, an IDE, Python and Node.js
processes, launchers and background helpers. SnapBack remembers the clean state (the *baseline*),
shows you what has appeared since, and closes the extra applications **the polite way**: the same way
you would by clicking ✕, so apps can save your work or ask you first.

SnapBack is **not** a "RAM cleaner" or a "PC booster". It does not trim working sets, purge the
standby list, disable services, edit the registry or delete files. It closes applications you started
after the baseline, with your consent, and nothing else.

> [Русская версия README](README.ru.md)

---

## How it works

1. **Baseline.** Click *Make current state the baseline* (or let SnapBack take one automatically a
   few minutes after you sign in, once your startup apps have finished launching). The baseline
   records *applications*, not just PIDs.
2. **Compare.** Later, SnapBack groups every process into applications (Chrome's 30 `chrome.exe`
   processes are one app; VS Code with its helpers is one app) and compares them with the baseline.
3. **Review.** You see what appeared since the baseline, with icon, memory, process count and a
   risk assessment:

   | Application        | Memory | Assessment                 |
   |--------------------|-------:|----------------------------|
   | Discord            | 650 MB | Safe                       |
   | Visual Studio Code | 1.4 GB | Possible unsaved data      |
   | python.exe — train.py | 740 MB | Manual review           |
   | Google Chrome      | 3.2 GB | Was present in baseline    |

   Only apps marked **Safe** are pre-selected. You can untick anything, or add apps to your
   *Always keep* list.
4. **Preview.** Before anything happens, a preview lists every single step SnapBack will take
   ("send a close request to 2 windows of Code.exe (PID 1234)", "left alone: conhost.exe — Windows
   component", …). A **dry run** re-checks every step against the live system without acting.
5. **Close politely.** Selected apps are asked to close the normal way and get time to save or ask
   you. Nothing is force-terminated.
6. **Decide.** Apps that did not close (for example because they ask "Save changes?") are reported.
   You can switch to them, press *Check again*, or — separately and explicitly — choose to
   force-terminate specific ones.
7. **Result.** You see how many apps closed, how much memory they used, and how the available
   memory changed.

## Safety first

SnapBack is built around the assumption that **closing the wrong process is far worse than closing
too little**. Highlights (full details in [docs/SAFETY.md](docs/SAFETY.md)):

- **Only your own apps in your own session.** Processes of other users, other sessions, services,
  elevated (administrator) processes, critical and protected processes, Windows components (anything
  under `C:\Windows`, the shell, security software, …) are **protected**. There is no setting that
  can unprotect them.
- **Fail closed.** If any safety-relevant fact about a process cannot be verified (owner, path,
  elevation, critical flag, protection level), the process is protected.
- **PIDs are never trusted alone.** Every process is identified by *PID + creation time*. Before
  every action the identity, executable path, session, owner and protection status are re-verified
  **on the very handle the action is performed through**, so a recycled PID can never be hit.
- **Three independent layers.** The classifier, a safety guard that re-checks the live process right
  before each action, and the Windows layer that verifies the handle. A UI bug cannot get a protected
  process closed.
- **Polite by default, forced only on request.** Window apps receive a normal close request
  (`WM_CLOSE`). Tray apps are asked through Windows Restart Manager — the mechanism installers use —
  **without** the force flag. Console programs receive Ctrl+C, and only if no unrelated program shares
  their console. Forced termination requires a separate confirmation that covers exact process
  identities and expires after five minutes.
- **No administrator rights, no driver, no services.** SnapBack runs as a normal user
  (`asInvoker`), uses documented Win32 APIs, and does not touch Windows services at all.

## Requirements

- Windows 10 (1809+) or Windows 11, x64 or ARM64.
- Release builds are self-contained — no separate .NET installation needed.

## Installation

Download the latest `SnapBack-<version>-win-x64.zip` (or `win-arm64`) from the
[Releases](../../releases) page, extract it anywhere (for example `%LOCALAPPDATA%\Programs\SnapBack`)
and run `SnapBack.exe`. Verify the download with `SHA256SUMS.txt` if you like.

There is no installer and nothing is registered system-wide. To uninstall, disable the automatic
baseline in Settings (if you enabled it), delete the folder and `%LOCALAPPDATA%\SnapBack`.

## Using SnapBack

### The window

- **Make current state the baseline** — what is running now becomes the clean state.
- **New since baseline** — candidates for closing, with checkboxes.
- **Kept** — baseline apps and your keep list. New windows or child processes of these apps are
  never a reason to close them.
- **Protected** — everything SnapBack will never touch, with the reason.
- **Return to baseline…** — opens the preview; nothing happens until you confirm there.
- **Always keep selected** — adds apps to your keep list (whitelist).
- **History** — recent operations: what was closed, what remained, memory released.
- **Settings** — close timeout, pre-selection of "caution" apps, automatic baseline, keep list.

### Automatic baseline (optional)

Enable *Create a baseline automatically after I sign in* in Settings. SnapBack then adds itself to
your *Startup apps* (the per-user `Run` key — its only registry write) and, at sign-in, waits until
at least 2 minutes have passed **and** no new apps have started for 45 seconds (both configurable,
with a 10-minute cap), takes the baseline and exits. It never closes anything in this mode, and it
keeps an existing baseline from the same boot.

### Command line

`snapback-cli.exe` shares the engine and all safety checks with the window:

```text
snapback-cli status                       baseline, running apps, memory, what is new
snapback-cli baseline create [--force]    make the current state the baseline
snapback-cli baseline show [--all] [--json]
snapback-cli list [--all] [--json]        apps started after the baseline
snapback-cli preview [selection] [--json] dry run: every step + live safety verdict
snapback-cli restore [selection] [--yes]  ask the selected apps to close (never forced)
snapback-cli history [--json]

selection: --app <name> (repeatable) | --include-caution | --include-review
```

The CLI deliberately **cannot** force-terminate anything.

## Building from source

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`).

```powershell
git clone https://github.com/IT-Shelter-Labs/SnapBack-Rebootless.git
cd SnapBack-Rebootless
dotnet build SnapBack.sln -c Release
dotnet test SnapBack.sln -c Release

# Run the app
dotnet run --project src/SnapBack.App

# Self-contained single-file build
dotnet publish src/SnapBack.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
dotnet publish src/SnapBack.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

The platform-independent core and its 140+ tests also build and run on Linux and macOS
(`dotnet test tests/SnapBack.Core.Tests`). The Windows integration tests start a small dedicated test
app and only ever act on that app.

## Project layout

```text
src/
  SnapBack.Core/      platform-independent engine: model, classification, grouping, baselines,
                      planning, safety guard, cleanup orchestration, persistence, logging
  SnapBack.Windows/   Win32 implementation: process snapshot, windows, services, Restart Manager,
                      console Ctrl+C, handle-verified actions
  SnapBack.App/       WPF desktop app (Fluent theme, light/dark)
  SnapBack.Cli/       command-line interface
tests/
  SnapBack.Core.Tests/     unit tests of the safety-critical logic (run anywhere)
  SnapBack.Windows.Tests/  native layout tests + integration tests against real Windows
  SnapBack.TestTarget/     tiny window app the integration tests close
docs/
  ARCHITECTURE.md     design and algorithms
  SAFETY.md           threat model: how SnapBack could hurt you, and what prevents it
```

## Data and privacy

Everything lives in `%LOCALAPPDATA%\SnapBack` (override with the `SNAPBACK_DATA_DIR` environment
variable): `settings.json`, `baseline.json`, `history.json` and daily logs in `logs\` (kept for 14
days). Window titles and command lines are shown in the UI only; they are never written to disk or to
the logs. SnapBack makes no network connections.

## FAQ

**Will it close my browser and lose my tabs?** Browsers are never pre-selected. If you select one, it
receives the same close request as clicking ✕, so it restores its tabs as usual next time.

**An app asked me to save and SnapBack says "still open".** That is intended: SnapBack waits for
*you*. Answer the prompt, then press *Check again*.

**Why is Discord/Telegram closed through "Restart Manager"?** Such apps hide to the tray when their
window is closed. Restart Manager asks them to exit the way Windows does before installing updates,
which they handle by quitting cleanly — without any force.

**Why can't SnapBack close an app that runs as administrator?** By design. Elevated programs are
protected; SnapBack never asks for elevation. See [docs/SAFETY.md](docs/SAFETY.md).

**Does it free memory?** Closing apps releases the memory they used — that is the real effect. The
result screen shows both the memory used by the closed apps and the change in available memory.
SnapBack does not do "memory optimization" tricks; Windows manages caches better on its own.

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). Safety-relevant changes need
tests and a short rationale in the pull request. Please report security issues privately as described
in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
