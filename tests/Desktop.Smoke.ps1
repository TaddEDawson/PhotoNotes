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
$noteFile = $null

function Wait-Until([scriptblock]$Condition, [string]$Description) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 30) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out: $Description"
}

function Find-Control([string]$Id, [switch]$Optional) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    $control = $script:window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $control -and -not $Optional) { throw "Control not found: $Id" }
    return $control
}

function Invoke-Control([string]$Id) {
    $control = Find-Control $Id
    if (-not $control.Current.IsEnabled) { throw "Control is disabled: $Id" }
    $pattern = $control.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Set-ControlValue([string]$Id, [string]$Value) {
    $pattern = (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Get-ControlValue([string]$Id) {
    $pattern = (Find-Control $Id).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Start-App {
    $script:app = Start-Process -FilePath $Executable -PassThru
    Wait-Until {
        $script:app.Refresh()
        if ($script:app.HasExited) { throw "Desktop app exited: $($script:app.ExitCode)" }
        $script:app.MainWindowHandle -ne [IntPtr]::Zero
    } 'desktop window to open'
    $script:window = [System.Windows.Automation.AutomationElement]::FromHandle($script:app.MainWindowHandle)
    Wait-Until { $null -ne (Find-Control 'SourceFolderInput' -Optional) } 'controls to initialize'
}

try {
    [void][System.IO.Directory]::CreateDirectory($source)
    $nested = Join-Path $source 'nested'
    [void][System.IO.Directory]::CreateDirectory($nested)
    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aYZkAAAAASUVORK5CYII=')
    # Unique trailing bytes isolate this test's content-keyed note from real user notes.
    $png = [byte[]]($png + [System.Text.Encoding]::UTF8.GetBytes([Guid]::NewGuid().ToString('N')))
    [System.IO.File]::WriteAllBytes((Join-Path $source 'first.png'), $png)
    [System.IO.File]::WriteAllBytes((Join-Path $nested 'renamed.png'), $png)
    $hash = (Get-FileHash -LiteralPath (Join-Path $source 'first.png')).Hash.ToLowerInvariant()
    $noteFile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "PhotoNotes\Notes\$hash.json"
    if (Test-Path -LiteralPath $noteFile) { throw 'Unexpected pre-existing test note.' }
    Start-App
    if ((Find-Control 'NotesInput').Current.IsEnabled) { throw 'Notes editor must require a selected content ID.' }
    foreach ($input in @(@('SourceFolderInput', $source), @('DestinationFolderInput', $destination))) {
        Set-ControlValue $input[0] $input[1]
    }
    Invoke-Control 'ScanButton'
    Wait-Until { (Find-Control 'PreviewButton').Current.IsEnabled } 'scan to complete'
    $summary = (Find-Control 'SummaryText').Current.Name
    if ($summary -notlike '2 images*1 extra identical files*0 issues') {
        throw "Unexpected scan summary: $summary"
    }
    Wait-Until { (Find-Control 'NotesInput').Current.IsEnabled } 'notes editor to load'
    $description = 'n' * 2048
    Set-ControlValue 'NotesInput' $description
    if ((Find-Control 'NoteCharacterCount').Current.Name -ne '2048 / 2048 characters (unsaved)') {
        throw 'Notes editor did not retain the full 2048-character description.'
    }
    Invoke-Control 'SaveNoteButton'
    Wait-Until { (Find-Control 'StatusText').Current.Name -like 'Note saved locally.*' } 'note save'
    $savedNote = Get-Content -LiteralPath $noteFile -Raw | ConvertFrom-Json
    if ($savedNote.ContentHash -ne $hash -or $savedNote.Description -ne $description) {
        throw 'Persisted note does not match the editor or content hash.'
    }
    $rows = (Find-Control 'ImagesGrid').FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::DataItem))
    $rows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    if ((Get-ControlValue 'NotesInput') -ne $description) { throw 'Renamed duplicate did not share the saved note.' }
    Set-ControlValue 'NotesInput' 'unsaved draft'
    Invoke-Control 'ScanButton'
    Wait-Until { (Find-Control 'StatusText').Current.Name -like 'Unsaved note:*' } 'unsaved note protection'
    if ((Get-ControlValue 'NotesInput') -ne 'unsaved draft') { throw 'Blocked rescan discarded the draft.' }
    Invoke-Control 'PreviewButton'
    Wait-Until { (Find-Control 'CopyButton').Current.IsEnabled } 'copy preview'
    if ((Get-ControlValue 'NotesInput') -ne 'unsaved draft') { throw 'Preview discarded the unsaved note draft.' }
    Invoke-Control 'RevertNoteButton'
    if ((Get-ControlValue 'NotesInput') -ne $description) { throw 'Revert did not restore the saved note.' }
    Invoke-Control 'VoiceTypingButton'
    if (-not (Find-Control 'NotesInput').Current.HasKeyboardFocus) { throw 'Voice typing did not focus the notes editor.' }
    if ((Find-Control 'StatusText').Current.Name -notlike 'Press Win+H*') { throw 'Voice typing guidance is missing.' }
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
    [void]$app.CloseMainWindow()
    if (-not $app.WaitForExit(5000)) { throw 'App did not close after saving notes.' }
    Start-App
    Set-ControlValue 'SourceFolderInput' $destination
    Invoke-Control 'ScanButton'
    Wait-Until { (Find-Control 'NotesInput').Current.IsEnabled } 'copied-image notes after restart'
    if ((Get-ControlValue 'NotesInput') -ne $description) {
        throw 'Copied image did not retain the shared note across an app restart.'
    }
    Set-ControlValue 'NotesInput' ''
    Invoke-Control 'SaveNoteButton'
    Wait-Until { (Find-Control 'StatusText').Current.Name -like 'Note saved locally.*' } 'note clearing'
    if ((Get-Content -LiteralPath $noteFile -Raw | ConvertFrom-Json).Description -ne '') {
        throw 'Saving an empty description did not clear the note.'
    }
    [void]$app.CloseMainWindow()
    if (-not $app.WaitForExit(5000)) { throw 'App did not close after clearing notes.' }
    [System.IO.File]::WriteAllText($noteFile, '{broken JSON')
    Start-App
    Set-ControlValue 'SourceFolderInput' $destination
    Invoke-Control 'ScanButton'
    Wait-Until { (Find-Control 'PreviewButton').Current.IsEnabled } 'scan with corrupt note'
    if ((Find-Control 'NotesInput').Current.IsEnabled -or
        (Find-Control 'NoteIdentityText').Current.Name -notlike 'Notes could not be loaded.*') {
        throw 'Corrupt notes were not explicitly reported and protected from editing.'
    }
    if ([System.IO.File]::ReadAllText($noteFile) -ne '{broken JSON') { throw 'Corrupt notes were overwritten.' }
    Write-Output "Desktop smoke test passed. $summary $status Notes: 2048 characters, shared duplicates, unsaved protection, voice focus, restart, clearing, and corrupt-data handling verified."
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        [void]$app.CloseMainWindow()
        if (-not $app.WaitForExit(5000)) { Stop-Process -Id $app.Id }
    }
    if ($null -ne $noteFile -and (Test-Path -LiteralPath $noteFile)) {
        Remove-Item -LiteralPath $noteFile -Force
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
