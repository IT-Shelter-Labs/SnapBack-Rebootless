<#
  Tray / autostart smoke test for the published SnapBack.exe.

  1. Starts SnapBack the way Windows does at sign-in ("--autostart") with "start minimized" and
     "automatic baseline" on: no window may appear, the per-user Run entry must be written, and the
     automatic baseline must be taken once startup has settled.
  2. Measures SnapBack's own CPU time and memory while it idles in the notification area.
  3. Launches SnapBack again: the running instance must show its window and the new one exit.
  4. Turns "Start with Windows" off and on in Settings and checks the Run entry follows.
  5. Closes the window (it hides to the tray, the process keeps running), then "SnapBack.exe --quit"
     must make it exit cleanly.
  Nothing is ever closed by SnapBack in this test.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $DataDir,
    [string] $ScreenshotDir = 'screenshots',
    [int] $IdleSeconds = 60
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia-common.ps1')

$Exe = (Resolve-Path $Exe).Path
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Get-RunEntry { (Get-ItemProperty -Path $runKey -Name 'SnapBack' -ErrorAction SilentlyContinue).SnapBack }

New-Item -ItemType Directory -Force $DataDir | Out-Null
@'
{ "language": "en", "startWithWindows": true, "startMinimizedToTray": true, "autoBaselineOnSignIn": true,
  "autoBaselineMinDelaySeconds": 0, "autoBaselineQuietSeconds": 10, "showNotifications": true }
'@ | Set-Content (Join-Path $DataDir 'settings.json') -Encoding UTF8
$env:SNAPBACK_DATA_DIR = $DataDir
Remove-ItemProperty -Path $runKey -Name 'SnapBack' -ErrorAction SilentlyContinue

$process = Start-Process $Exe -ArgumentList '--autostart' -PassThru
try {
    # ---- 1. Autostart: tray only, Run entry, automatic baseline -----------------------------
    Wait-Until { (Get-SnapBackLog $DataDir) -match 'Tray icon shown' } 60 'tray icon'
    $log = Get-SnapBackLog $DataDir
    $trayShown = $log -match 'Tray icon shown: True'
    Write-Host "Tray icon shown: $trayShown"
    Start-Sleep -Seconds 3
    if ($process.HasExited) { throw "SnapBack exited (code $($process.ExitCode))" }

    $visible = @(Get-TopWindows $process.Id | Where-Object { $_.Current.AutomationId -eq 'MainWindow' })
    if ($trayShown) {
        if ($visible.Count -ne 0) { throw 'The main window opened although SnapBack should start minimized to the tray' }
        if ($log -notmatch 'Started in the notification area') { throw 'SnapBack did not log the tray start' }
        Write-Host 'Started in the notification area without a window'
    } else {
        # Without a taskbar the window must be shown instead (SnapBack never becomes invisible).
        Write-Host 'No taskbar on this machine: SnapBack shows its window instead'
        $null = Find-TopWindow $process.Id 'MainWindow'
    }

    $expected = "`"$Exe`" --autostart"
    $entry = Get-RunEntry
    if ($entry -ne $expected) { throw "Run entry is '$entry', expected '$expected'" }
    Write-Host "Run entry: $entry"

    Wait-Until { (Get-SnapBackLog $DataDir) -match 'Automatic baseline taken' } 240 'automatic baseline'
    if (-not (Test-Path (Join-Path $DataDir 'profiles.json'))) { throw 'No profile was saved' }
    Write-Host (((Get-SnapBackLog $DataDir) -split "`n") | Where-Object { $_ -match 'AutoBaseline' }) -Separator "`n"

    # Best effort: find the icon in the notification area (depends on the runner's shell).
    try {
        $tray = $AE::RootElement.FindFirst($Scope::Children, (New-Cond $AE::ClassNameProperty 'Shell_TrayWnd'))
        if ($tray) {
            $icon = $tray.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
                Where-Object { $_.Current.Name -like 'SnapBack*' } | Select-Object -First 1
            Write-Host "Tray button found by UI Automation: $([bool]$icon) $($icon.Current.Name)"
        }
    } catch { Write-Host "Tray lookup skipped: $_" }

    # ---- 2. Idle cost in the background -----------------------------------------------------
    Start-Sleep -Seconds 5
    $p = Get-Process -Id $process.Id
    $cpuBefore = $p.TotalProcessorTime
    Start-Sleep -Seconds $IdleSeconds
    $p = Get-Process -Id $process.Id
    $cpuUsed = ($p.TotalProcessorTime - $cpuBefore).TotalMilliseconds
    $privateMb = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    $workingMb = [math]::Round($p.WorkingSet64 / 1MB, 1)
    Write-Host "Idle for $IdleSeconds s in the tray: CPU $([math]::Round($cpuUsed)) ms, private $privateMb MB, working set $workingMb MB, threads $($p.Threads.Count)"
    if ($cpuUsed -gt 2000) { throw "SnapBack used $cpuUsed ms of CPU while idle" }
    if ($privateMb -gt 400) { throw "SnapBack uses $privateMb MB of private memory in the background" }

    # ---- 3. A second launch shows the running instance's window -----------------------------
    $second = Start-Process $Exe -PassThru
    if (-not $second.WaitForExit(30000)) { throw 'The second instance did not exit' }
    if ($second.ExitCode -ne 0) { throw "The second instance exited with $($second.ExitCode)" }
    $main = Find-TopWindow $process.Id 'MainWindow'
    Start-Sleep -Seconds 8
    Save-Screenshot 'tray-activated-window'
    foreach ($id in 'DriftValue', 'ImpactValue') { Write-Host "Dashboard $id = $((Find-ById $main $id).Current.Name)" }

    # ---- 4. "Start with Windows" follows the setting -----------------------------------------
    foreach ($wanted in $false, $true) {
        Invoke-Element (Find-ById $main 'SettingsButton')
        $settings = Find-TopWindow $process.Id 'SettingsWindow'
        $check = Find-ById $settings 'StartWithWindowsCheck'
        $toggle = $check.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $isOn = $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
        if ($isOn -ne $wanted) { $toggle.Toggle() }
        Invoke-Element (Find-ById $settings 'SaveSettingsButton')
        Wait-Until { [bool](Get-RunEntry) -eq $wanted } 10 "Run entry = $wanted"
        Write-Host "Start with Windows = $wanted -> Run entry: '$(Get-RunEntry)'"
        Start-Sleep -Seconds 1
    }

    # ---- 5. Close to tray, then --quit ------------------------------------------------------
    if ($trayShown) {
        $main.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Wait-Until { @(Get-TopWindows $process.Id | Where-Object { $_.Current.AutomationId -eq 'MainWindow' }).Count -eq 0 } 15 'window hidden'
        Start-Sleep -Seconds 2
        if ($process.HasExited) { throw 'Closing the window exited SnapBack instead of hiding it to the tray' }
        Write-Host 'Close button hid the window; SnapBack keeps running in the tray'
    }

    $quit = Start-Process $Exe -ArgumentList '--quit' -PassThru
    if (-not $quit.WaitForExit(30000)) { throw '--quit did not finish' }
    if (-not $process.WaitForExit(30000)) { throw 'SnapBack did not exit after --quit' }
    Write-Host "SnapBack exited after --quit (exit code $($process.ExitCode))"
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    Remove-ItemProperty -Path $runKey -Name 'SnapBack' -ErrorAction SilentlyContinue
    $log = Get-SnapBackLog $DataDir
    Write-Host '---- SnapBack log (tray) ----'
    Write-Host $log
}

if ($log -match '\[(ERR|CRT)\]') { throw 'Errors were logged (tray)' }
if ($log -notmatch 'SnapBack exiting') { throw 'SnapBack did not shut down cleanly' }
Write-Host 'Tray / autostart smoke test passed'
