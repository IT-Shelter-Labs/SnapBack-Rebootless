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
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function New-Cond($property, $value) { New-Object System.Windows.Automation.PropertyCondition($property, $value) }

function Find-TopWindow([int] $processId, [string] $automationId, [int] $timeoutSec = 40) {
    $cond = New-Object System.Windows.Automation.AndCondition((New-Cond $AE::ProcessIdProperty $processId), (New-Cond $AE::AutomationIdProperty $automationId))
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $w = $AE::RootElement.FindFirst($Scope::Children, $cond)
        if ($w) { return $w }
        Start-Sleep -Milliseconds 500
    }
    throw "Window '$automationId' did not appear"
}

function Find-ById($element, [string] $id) {
    $e = $element.FindFirst($Scope::Descendants, (New-Cond $AE::AutomationIdProperty $id))
    if (-not $e) { throw "Element '$id' not found" }
    return $e
}

function Get-Texts($element) {
    $element.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ }
}

function Invoke-Element($element) {
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Assert-Texts($texts, [string[]] $expected, [string] $label) {
    foreach ($e in $expected) {
        if (-not ($texts | Where-Object { $_ -like "*$e*" })) {
            Write-Host "---- texts seen ($label) ----"; $texts | Select-Object -Unique | ForEach-Object { Write-Host "  $_" }
            throw "[$label] expected text not found: '$e'"
        }
        Write-Host "[$label] found: $e"
    }
}

function Save-Screenshot([string] $name) {
    try {
        New-Item -ItemType Directory -Force $ScreenshotDir | Out-Null
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bmp.Save((Join-Path $ScreenshotDir "$name.png"))
    } catch { Write-Host "Screenshot failed: $_" }
}

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
    $log = (Get-ChildItem (Join-Path $DataDir 'logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content) -join "`n"
    Write-Host "---- SnapBack log ($Language) ----"
    Write-Host $log
}

if ($log -match '\[(ERR|CRT)\]') { throw "Errors were logged ($Language)" }
if ($log -notmatch "App: SnapBack .* started") { throw "SnapBack did not log its start-up ($Language)" }
Write-Host "UI smoke test passed ($Language)"
