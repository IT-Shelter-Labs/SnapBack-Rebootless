<#
  Shared UI Automation helpers for the CI smoke tests (dot-sourced).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function New-Cond($property, $value) { New-Object System.Windows.Automation.PropertyCondition($property, $value) }

function Find-TopWindow([int] $processId, [string] $automationId, [int] $timeoutSec = 40) {
    # Owned dialogs are often exposed as children of their owner window rather than of the desktop,
    # so look at the process's top-level windows and inside them.
    $byId = New-Cond $AE::AutomationIdProperty $automationId
    $byProcess = New-Cond $AE::ProcessIdProperty $processId
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        foreach ($top in $AE::RootElement.FindAll($Scope::Children, $byProcess)) {
            if ($top.Current.AutomationId -eq $automationId) { return $top }
            $inner = $top.FindFirst($Scope::Descendants, $byId)
            if ($inner) { return $inner }
        }
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
        if (-not $ScreenshotDir) { $ScreenshotDir = 'screenshots' }
        New-Item -ItemType Directory -Force $ScreenshotDir | Out-Null
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
        [System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $bmp.Save((Join-Path $ScreenshotDir "$name.png"))
    } catch { Write-Host "Screenshot failed: $_" }
}


function Get-TopWindows([int] $processId) {
    $AE::RootElement.FindAll($Scope::Children, (New-Cond $AE::ProcessIdProperty $processId))
}

function Wait-Until([scriptblock] $condition, [int] $timeoutSec, [string] $what) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for: $what"
}

function Get-SnapBackLog([string] $dataDir) {
    (Get-ChildItem (Join-Path $dataDir 'logs') -Filter *.log -ErrorAction SilentlyContinue | Get-Content) -join "`n"
}
