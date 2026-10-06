[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageRoot,
    [Parameter(Mandatory = $true)][string]$ReportRoot
)
$ErrorActionPreference = 'Stop'
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$ReportRoot = [IO.Path]::GetFullPath($ReportRoot)
if ($ReportRoot.StartsWith($PackageRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $ReportRoot -eq $PackageRoot) { throw 'Fixture reports must stay outside the release package.' }
New-Item -ItemType Directory -Path $ReportRoot -Force | Out-Null
$dockExe = Join-Path $PackageRoot 'NativeDock/PersonalDock-v4.exe'
$stageExe = Join-Path $PackageRoot 'CampusStage/StageManager.exe'
$checks = @(
    @('/check-dock-runtime', 'runtime.txt'),
    @('/check-dock-watchdog', 'watchdog.txt'),
    @('/check-dock-recovery', 'recovery.txt'),
    @('/check-dock-presentation', 'presentation.txt'),
    @('/check-dock-pins', 'pins.txt'),
    @('/check-dock-hover', 'hover.txt'),
    @('/selftest', 'dock.txt'),
    @('/check-mac-appearance-local', 'appearance.txt')
)
function Invoke-Check([string]$Executable, [string]$Switch, [string]$Name) {
    $report = Join-Path $ReportRoot $Name
    $process = Start-Process -FilePath $Executable -ArgumentList @($Switch, ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw ('Fixture timed out: ' + $Name) }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report) -or -not ([IO.File]::ReadAllText($report)).StartsWith('PASS')) { throw ('Fixture failed: ' + $Name) }
    Write-Output ('PASS ' + $Name)
}
foreach ($check in $checks) { Invoke-Check $dockExe $check[0] $check[1] }
Invoke-Check $stageExe '--selftest' 'stage.txt'
# These command-line fixture branches run before normal desktop startup. They do
# not activate the sidebar/Dock, hide the taskbar, or control existing app windows.
