param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\src\PhotoNotes.Desktop\bin\Release\net10.0-windows\PhotoNotes.Desktop.exe')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$root = Join-Path ([System.IO.Path]::GetTempPath()) ('PhotoNotes-smoke-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$destination = Join-Path $root 'copies'
$app = $null

function Wait-Until([scriptblock]$Condition, [string]$Description) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 30) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $Description"
}

function Find-Control([string]$Id) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $control = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $control) { throw "Control not found: $Id" }
    return $control
}

function Invoke-Control([string]$Id) {
    $control = Find-Control $Id
    if (-not $control.Current.IsEnabled) { throw "Control is disabled: $Id" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

try {
    [void][System.IO.Directory]::CreateDirectory($source)
    $nested = Join-Path $source 'nested'
    [void][System.IO.Directory]::CreateDirectory($nested)
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aYZkAAAAASUVORK5CYII=')
    [System.IO.File]::WriteAllBytes((Join-Path $source 'first.png'), $png)
    [System.IO.File]::WriteAllBytes((Join-Path $nested 'renamed.png'), $png)
    $app = Start-Process -FilePath $Executable -PassThru
    Wait-Until {
        $app.Refresh()
        if ($app.HasExited) { throw "Desktop app exited: $($app.ExitCode)" }
        $app.MainWindowHandle -ne [IntPtr]::Zero
    } 'desktop window to open'
    $script:window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    Wait-Until { $null -ne (Find-Control 'SourceFolderInput') } 'controls to initialize'
    foreach ($input in @(@('SourceFolderInput', $source), @('DestinationFolderInput', $destination))) {
        $pattern = (Find-Control $input[0]).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $pattern.SetValue($input[1])
    }
    Invoke-Control 'ScanButton'
    Wait-Until { (Find-Control 'PreviewButton').Current.IsEnabled } 'scan to complete'
    $summary = (Find-Control 'SummaryText').Current.Name
    if ($summary -notlike '2 images*1 extra identical files*0 issues') {
        throw "Unexpected scan summary: $summary"
    }
    Invoke-Control 'PreviewButton'
    Wait-Until { (Find-Control 'CopyButton').Current.IsEnabled } 'copy preview'
    if (Test-Path -LiteralPath $destination) { throw 'Preview created the destination directory.' }
    Invoke-Control 'CopyButton'
    $script:yes = $null
    Wait-Until {
        if ((Find-Control 'StatusText').Current.Name -like 'Copy finished:*') { return $true }
        $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Yes')
        $invokeCondition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::IsInvokePatternAvailableProperty, $true)
        $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $invokeCondition)
        $script:yes = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $null -ne $script:yes
    } 'confirmation dialog'
    if ($null -ne $script:yes) {
        $script:yes.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    Wait-Until { (Find-Control 'StatusText').Current.Name -like 'Copy finished:*' } 'copy completion'
    $status = (Find-Control 'StatusText').Current.Name
    if ($status -ne 'Copy finished: 1 copied, 0 failed. Originals unchanged.') {
        throw "Unexpected copy status: $status"
    }
    $copies = @(Get-ChildItem -LiteralPath $destination -Recurse -File)
    if ($copies.Count -ne 1 -or $copies[0].Directory.Name -ne 'Undated') {
        throw 'Expected one deduplicated copy in Undated.'
    }
    if ((Get-FileHash -LiteralPath $copies[0].FullName).Hash -ne
        (Get-FileHash -LiteralPath (Join-Path $source 'first.png')).Hash) {
        throw 'Copy content does not match the source.'
    }
    if (@(Get-ChildItem -LiteralPath $source -Recurse -File).Count -ne 2) {
        throw 'Original files were modified.'
    }
    Write-Output "Desktop smoke test passed. $summary $status"
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id }
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
