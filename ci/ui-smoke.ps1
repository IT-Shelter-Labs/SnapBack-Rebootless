<#
  UI smoke test for the published SnapBack.exe, driven through Windows UI Automation.

  - starts SnapBack with the given UI language and checks that localized texts are shown;
  - optionally switches the language live through Settings (no restart) and checks again;
  - optionally opens the Memory window and clicks a sort header;
  - saves screenshots and fails if SnapBack logged an error or exited.
  It never clicks "Return to baseline", so nothing is ever closed.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $DataDir,
    [Parameter(Mandatory)] [string] $Language,
    [Parameter(Mandatory)] [string[]] $Expect,
    [string] $SwitchTo = '',
    [string[]] $ExpectAfterSwitch = @(),
    [switch] $Interact,
    [string] $ScreenshotDir = 'screenshots'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'uia-common.ps1')

New-Item -ItemType Directory -Force $DataDir | Out-Null
"{ ""language"": ""$Language"" }" | Set-Content (Join-Path $DataDir 'settings.json') -Encoding UTF8
$env:SNAPBACK_DATA_DIR = $DataDir

$process = Start-Process $Exe -PassThru
try {
    $main = Find-TopWindow $process.Id 'MainWindow'
    Start-Sleep -Seconds 12   # first analysis
    Assert-Texts (Get-Texts $main) $Expect $Language
    Save-Screenshot "main-$Language"

    if ($Interact) {
        # Sorting: click the Memory column header; the header shows a direction arrow.
        Invoke-Element (Find-ById $main 'SortMemory')
        Start-Sleep -Seconds 1
        $header = (Find-ById $main 'SortMemory').Current.Name
        if ($header -notmatch '[↑↓]') { throw "Sort header shows no direction: '$header'" }
        Write-Host "Sort header after click: $header"

        # Memory window: advice and the (not clicked) Deep Cleanup button exist.
        Invoke-Element (Find-ById $main 'MemoryButton')
        $memory = Find-TopWindow $process.Id 'MemoryWindow'
        Start-Sleep -Seconds 3
        $advice = (Find-ById $memory 'AdviceTitle').Current.Name
        if (-not $advice) { throw 'Memory advice is empty' }
        Write-Host "Memory advice: $advice"
        $null = Find-ById $memory 'RunDeepCleanupButton'
        Save-Screenshot "memory-$Language"
        $memory.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
        Start-Sleep -Seconds 1

        # Dashboard: session drift and expected impact are shown, the profile picker exists.
        foreach ($id in 'DriftValue', 'ImpactValue', 'UptimeText') {
            $value = (Find-ById $main $id).Current.Name
            if (-not $value) { throw "Dashboard value '$id' is empty" }
            Write-Host "Dashboard $id = $value"
        }
        $null = Find-ById $main 'ProfileCombo'

        # Profiles window opens and lists the profile created by the CLI.
        Invoke-Element (Find-ById $main 'ProfilesButton')
        $profiles = Find-TopWindow $process.Id 'ProfilesWindow'
        Start-Sleep -Seconds 1
        $list = Find-ById $profiles 'ProfilesList'
        $names = Get-Texts $list
        if (-not $names) { throw 'The profiles window lists no profile' }
        Write-Host "Profiles: $($names -join ', ')"
        Save-Screenshot "profiles-$Language"
        Invoke-Element (Find-ById $profiles 'CloseProfilesButton')
        Start-Sleep -Seconds 2
    }

    if ($SwitchTo) {
        # Live language switch through Settings, without restarting SnapBack.
        Invoke-Element (Find-ById $main 'SettingsButton')
        $settings = Find-TopWindow $process.Id 'SettingsWindow'
        $combo = Find-ById $settings 'LanguageCombo'
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 500
        $item = $combo.FindFirst($Scope::Descendants, (New-Cond $AE::NameProperty $SwitchTo))
        if (-not $item) { throw "Language '$SwitchTo' not offered" }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Start-Sleep -Seconds 1
        Invoke-Element (Find-ById $settings 'SaveSettingsButton')
        Start-Sleep -Seconds 8
        if ($process.HasExited) { throw 'SnapBack exited after switching the language' }
        Assert-Texts (Get-Texts $main) $ExpectAfterSwitch "$Language -> $SwitchTo"
        Save-Screenshot "main-switched-to-$SwitchTo"
    }

    if ($process.HasExited) { throw "SnapBack exited unexpectedly (exit code $($process.ExitCode))" }
}
finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    $log = Get-SnapBackLog $DataDir
    Write-Host "---- SnapBack log ($Language) ----"
    Write-Host $log
}

if ($log -match '\[(ERR|CRT)\]') { throw "Errors were logged ($Language)" }
if ($log -notmatch "App: SnapBack .* started") { throw "SnapBack did not log its start-up ($Language)" }
Write-Host "UI smoke test passed ($Language)"
