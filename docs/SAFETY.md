# SnapBack safety model

SnapBack closes programs. Done carelessly, that can crash Windows, corrupt data or lose unsaved
work. This document lists the ways SnapBack could cause harm and what prevents each of them. It is the
reference for reviewing any change that touches classification, planning or execution.

## Principles

1. **Closing too little is acceptable; closing the wrong thing is not.** Every ambiguous case is
   resolved towards protection.
2. **Fail closed.** If a safety-relevant attribute cannot be determined, the process is protected.
3. **Never trust a PID alone.** A process is identified by *(PID, creation time)*.
4. **Defence in depth.** No single component's correctness is relied upon; three independent layers
   must all agree before anything happens.
5. **Polite first, forced only by explicit, specific consent.**
6. **Least privilege.** No administrator rights, no driver, no service, no system-wide changes.

## Layers

```text
 UI selection ──► CleanupPlanner ──► SafetyGuard (live re-check) ──► VerifiedProcess (handle) ──► action
                  only candidates     per process, right before     identity + safety verified on
                  are selectable      each action                   the handle the action uses
```

| Layer | Where | What it guarantees |
|---|---|---|
| Classification | `Core/Classification/ProcessClassifier.cs` | Every process gets *Safe / Caution / ManualReview / Protected*. Protected rules are evaluated first and nothing can lower them. |
| Planning | `Core/Planning/CleanupPlanner.cs` | Only apps that appeared after the baseline and are not protected or kept become selectable candidates. Protected members of an app (e.g. `conhost.exe`) are left alone. |
| Safety guard | `Core/Safety/SafetyGuard.cs` | Right before each action, re-inspects the live process: still the same identity? same executable? still not protected? Is the item a selectable candidate at all (protects against UI bugs)? |
| Verified handle | `Windows/VerifiedProcess.cs` | Opens the process, verifies creation time, exit state, executable path, Windows-component rule, critical flag, PPL protection, session, owner and elevation **on that handle**, and the action is performed while holding it, so the PID cannot be recycled in between. |
| Restart Manager check | `Windows/WindowsProcessController.cs` | Before `RmShutdown`, `RmGetList` must report every registered process as a plain window app in our session; services, consoles, Explorer and critical processes abort the request. |

## Threats and mitigations

### Damaging Windows

| Threat | Mitigation |
|---|---|
| Ending a critical process (csrss, wininit, smss, …) → bug check | `IsProcessCritical` → Protected; checked again on the handle. Name list protects these regardless of location. Kernel pseudo processes and PID 0/4 hard-coded. |
| Ending a protected process (anti-malware, DRM) | Protection level (PPL) → Protected; unknown level → Protected. |
| Stopping a Windows service / service host | SCM is queried for service PIDs → Protected. `svchost.exe` protected by name. Restart Manager refuses `RmService`. SnapBack has no service management at all. |
| Killing the shell or session infrastructure (explorer, dwm, sihost, ctfmon, StartMenuExperienceHost, …) | Name list + Windows-directory rule + shell package prefixes → Protected. `RmExplorer` refused by Restart Manager check. |
| Anything from `C:\Windows` | Protected (except a short list of user tools such as Notepad or cmd, which always need manual review). Enforced again on the handle. |
| Processes of other users or sessions | Session and owner SID compared with SnapBack's own → Protected; re-checked on the handle. |
| Elevated (administrator) processes | Token elevation / integrity ≥ High → Protected. SnapBack never requests elevation; even if started elevated it keeps refusing elevated targets. |
| A process SnapBack cannot open or fully inspect | `AccessDenied`, `OwnerUnknown`, `ImagePathUnknown`, `CannotVerify` → Protected. |
| A malicious program named like a system file, outside `C:\Windows` | Name-based protection applies anywhere, so it is protected too (over-protection is acceptable). |
| SnapBack closing itself, or the app it was launched from | Self protected; ancestors require manual review ("closing it may close SnapBack too"). |
| Settings or JSON files tampered with | No setting can unprotect anything. Rules are evaluated after protection; *Always keep* only adds protection and *Always close* only pre-selects a candidate. Profiles only list apps to keep. Corrupt files are quarantined and defaults used. |

### Acting on the wrong process

| Threat | Mitigation |
|---|---|
| PID reused between analysis and action | Identity = (PID, creation time). Guard compares the live identity; the handle check compares the creation time and executable path; the action happens while the verified handle is held. |
| PID reused between enumeration and enrichment | The probe compares the handle's creation time with the snapshot's before trusting any enriched data; on mismatch the process stays "unverified" → Protected. |
| False parent/child relationship through a recycled parent PID | A parent must be older than its child; otherwise the link is ignored. |
| Ctrl+C reaching unrelated programs sharing a console | Before sending, SnapBack lists every process attached to the console; any process that is not part of the operation and not a plain shell aborts the request. Console windows are never sent `WM_CLOSE` (that would end every program in the console). |
| Restart Manager affecting extra processes | `RmGetList` must contain only the registered PIDs, all in our session, all window applications. Registration itself uses PID + start time. |
| UI bug passes a kept or protected item | Executor and guard refuse any item that is not a selectable candidate; covered by tests. |

### Losing user data

| Threat | Mitigation |
|---|---|
| Unsaved documents in editors/IDEs/office | Such apps (and any window whose title shows unsaved changes, e.g. `*file.txt`, `● main.py`) are *Caution* and not pre-selected. They are closed with the normal close request, so they can prompt. If they keep a window open, SnapBack reports "may be asking you to save" and **waits for you**; it never escalates. |
| Scripts / jobs (Python, Node.js, consoles, build tools) | *Manual review*, never pre-selected. Closed with Ctrl+C (like pressing it yourself), not killed. |
| Downloads, sync, torrents, recording, VMs, installers | Dedicated reasons (*May be transferring files*, *Recording*, *Virtual machine*, *Installer or updater*) → Caution or Manual review, never pre-selected. |
| Background processes without any window | *Manual review*: there is no polite way to close them, so SnapBack only reports them. |
| Children that a kept app still needs | New processes inside a baseline app's group are part of that app and never candidates. Separate apps started *by* a baseline app are flagged "Started by …" and not pre-selected. |
| Automatic forced termination of hanging apps | Does not exist. Hung apps are reported as *Not responding*. |
| Forced termination | Only after a confirmation dialog listing the apps and warning about data loss. The resulting `ForceAuthorization` covers the exact process identities, expires after 5 minutes, and is only valid for processes that already went through the polite phase of the same operation. The CLI cannot force at all. |

### Deep Memory Cleanup

| Threat | Mitigation |
|---|---|
| Cleanup runs as a hidden side effect of Return to baseline | It lives in a separate service (`DeepCleanupService`) that `CleanupOperation` and `SnapBackEngine` cannot reach (verified by a test). It only runs from an explicit button followed by a confirmation dialog. |
| "Optimizing" memory by trimming working sets (`EmptyWorkingSet`, `MemoryEmptyWorkingSets`) | Not implemented and not reachable: `MemoryListCommand.IsAllowed` accepts only the two standby purges (4 and 5); everything else is rejected before the system call, and tests enumerate the forbidden values. |
| Losing data waiting to be written (modified list) | Never touched: `MemoryFlushModifiedList` is not an allowed command, and purging standby never discards dirty pages. |
| Running the whole app elevated | Not needed. Only the purge needs elevation; it runs in a separate, short-lived helper started with one UAC prompt. SnapBack itself stays a standard-user process. |
| Abusing the elevated helper | The helper accepts exactly `--deep-cleanup standby` or `--deep-cleanup low-priority` (strict, case-sensitive parser), refuses to work unless elevated, reads no input files, writes no output files (only its own log), and answers only with an exit code. It has no other functionality, so a forged invocation can at most empty a cache — and would still need the user's UAC consent. |
| Misleading numbers ("freed 6 GB!") | SnapBack reports memory released by closing apps and cache dropped by the cleanup separately and never adds them up; it explains that the cache was already available and that *Available* barely changes. The advisor never presents the cleanup as necessary. |
| Declined UAC prompt or failure | Treated as a normal outcome: nothing changed, the reason is shown, nothing else is affected. |

### Profiles and rules

| Threat | Mitigation |
|---|---|
| A rule or profile closes a system component | Protection is decided first in `CleanupPlanner`; a protected group is never a candidate whatever the rules say (tests set *Always close* on svchost, Explorer and an elevated app). |
| *Always close* turns into "close without asking" | It only pre-selects. The preview and its confirmation, the polite close, the live guard and handle verification run as always; an app that asks to save is left waiting, and forced termination still needs its own confirmation (tested). |
| *Always close* skips the live re-check | No: the guard and `VerifiedProcess` act on the live process; a target that became protected (e.g. restarted elevated) is skipped (tested). |
| Switching to a profile closes apps by itself | Selecting a profile only changes what the session is compared with. Returning to it is the normal *Return to baseline* flow with preview and confirmation; the tray menu only opens that preview. |
| A stale or foreign profile | Profiles carry the user SID; a profile of another user is refused (`BaselineFactory.Validate`). Profiles from earlier boots match apps by application key only, as before. |

### WSL and Docker

| Threat | Mitigation |
|---|---|
| Killing `vmmem`/`vmmemWSL` or Docker processes corrupts the VM or loses container data | They carry `ManagedEnvironment` (protected) and are never candidates, whatever the rules. They are only stopped with `wsl --shutdown` / `docker desktop stop`. |
| Stopping WSL ends Linux services, jobs or unsaved work; stopping Docker ends containers | Before stopping, SnapBack lists the running distributions or containers and warns explicitly (including that Docker Desktop depends on WSL). Nothing stops without the user's confirmation. |
| Something starts between the warning and the stop | The confirmation records exactly what was shown and expires after 5 minutes; right before the command, SnapBack inspects again and does nothing if anything new is running or the list can no longer be read. |
| Old Docker Desktop without `docker desktop stop` | Reported as not supported; SnapBack does not fall back to closing its processes. |
| SnapBack starts WSL/Docker by inspecting them | Read-only commands are only run when the process list already shows the environment running. |
| Command injection | Commands are fixed executables with fixed argument lists, started without a shell; nothing user-provided is passed. |

### Tray, autostart and background mode

| Threat | Mitigation |
|---|---|
| Running elevated at sign-in | Autostart is the per-user `Run` entry (no administrator rights, no task, no service), written only when the user enables it and removed when disabled. |
| Automatic actions in the background | The background only analyzes (every 15 minutes) and may show a notification. It never closes anything; the automatic baseline only records a profile. |
| A forged "activate"/"exit" signal | The named events are per user session; they can only show the window or ask SnapBack to exit, and exit is refused while an operation runs. |
| SnapBack itself slows the machine down | One analysis per 15 minutes on a background-priority thread with EcoQoS; no polling; bounded in-memory memory history. CI measures idle CPU time and memory. |
| Notification spam / misleading alerts | Off with one setting; at most 3 per day; never about cached memory; growth is called "unusual", never a leak. |

### Other

| Threat | Mitigation |
|---|---|
| One failing process aborts everything / crashes the app | Every per-process query and action is wrapped; failures become notes on that item. Unhandled UI exceptions are logged and reported without closing the window. |
| Privacy | Window titles and command lines are shown in the UI only, never persisted or logged. Command lines are reduced to a script or module name. No network access. |
| Privilege escalation via SnapBack | No service, no driver, no IPC surface beyond the per-session "show window" / "exit" events. The only elevated code is the Deep Memory Cleanup helper, which needs the user's UAC consent and can do nothing but purge the standby cache. The only registry write is the opt-in per-user autostart entry. |
| Language switching changes behaviour | Localization only affects texts. All decisions use enums and identities, never translated strings; tests check that every language has every string with the same placeholders. |

## Elevation model

SnapBack runs as a standard user (`asInvoker`) and never asks for elevation to close apps, to start
with Windows (per-user Run entry) or to stop WSL/Docker (their own commands run as the user). An
elevated SnapBack could close elevated programs, but those are exactly the programs most likely to be
doing something sensitive (installers, admin tools, debuggers); the value is small and the risk large.

The single exception is the optional Deep Memory Cleanup, which Windows only permits to elevated
processes. It follows the rule this document set for any future elevation: a separate, short-lived,
explicitly requested elevation for a single confirmed operation, never a permanently elevated
process. The elevated helper (`SnapBack.exe --deep-cleanup …`) has no window, no single-instance
lock, a two-value command whitelist and an exit-code-only answer.

## What SnapBack never does

- Trim working sets, flush the modified list or "optimize" memory behind your back. (The standby
  cache can be purged only through the explicit, separately confirmed Deep Memory Cleanup.)
- Stop, disable or reconfigure services.
- Change process priorities or affinities.
- Delete files, clean temp folders or edit the registry (beyond the opt-in Run entry).
- Touch Windows Defender or any security software.
- Use a kernel driver.
- Run Deep Memory Cleanup automatically or as part of Return to baseline.
- Close anything from the background, the tray menu or a notification without the preview.
- Terminate `vmmem`, WSL or Docker processes; they are only stopped with their own commands.
- Let a rule or profile lower a *Protected* classification or make forced termination implicit.

## Known limitations

- Classification relies partly on executable names and heuristics. Unknown apps fall back to the
  window-based rules: a normal window app is *Safe* (it can always prompt you), a windowless one needs
  manual review.
- Apps that ignore both the close request and Restart Manager can only be closed by forced
  termination (your decision) or by you.
- Restart Manager sends the same "the app is being closed" notification installers use. A few apps
  save state and then wait instead of exiting; they are reported as still running.
- Processes started by an elevated helper of an app are left running (they are elevated, hence
  protected).

## Reviewing changes

A change to classification, the guard, the controller or the executor must:

- keep every existing safety test green, and add tests for the new behaviour;
- never add a way (setting, flag, list, rule, profile) to lower a *Protected* classification;
- never make forced termination implicit or automatic;
- keep identity verification on the handle that performs the action.
