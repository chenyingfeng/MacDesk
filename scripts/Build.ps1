[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [string]$NugetSource = '',
    [string]$ArtifactsPath = '',
    [string]$StageVersion = '0.8.3.24',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repoRoot 'artifacts' }
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
# Each build uses a new empty staging directory, so a previous user's data cannot
# accidentally enter a release. Only the explicit files below are packaged.
$buildRoot = Join-Path $ArtifactsPath ('build-' + [Guid]::NewGuid().ToString('N'))
$packageRoot = Join-Path $buildRoot 'MacDesk'
$stageRoot = Join-Path $packageRoot 'CampusStage'
$dockRoot = Join-Path $packageRoot 'NativeDock'
$reportRoot = Join-Path $buildRoot 'checks'
New-Item -ItemType Directory -Path $stageRoot, $dockRoot, $reportRoot -Force | Out-Null
$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$csc = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { throw 'Windows x64 with .NET Framework 4.x is required.' }

function Invoke-Dotnet([string[]]$Arguments) {
    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed with exit code ' + $LASTEXITCODE) }
}

Push-Location $repoRoot
try {
    $project = Join-Path $repoRoot 'src/StageManager/StageManager.csproj'
    $restoreArgs = @('restore', $project, '--runtime', 'win-x64', '--configfile', (Join-Path $repoRoot 'NuGet.Config'), ('-p:Version=' + $StageVersion))
    if ($NugetSource) { $restoreArgs += @('--source', $NugetSource, '-p:NuGetAudit=false') }
    Invoke-Dotnet $restoreArgs
    # Keep runtime/native dependencies as replaceable files, including LGPL
    # libuiohook used by SharpHook. Do not embed them inside a single executable.
    Invoke-Dotnet @('publish', $project, '--no-restore', '--configuration', 'Release', '--runtime', 'win-x64', '--self-contained', 'true', ('-p:Version=' + $StageVersion), '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:ContinuousIntegrationBuild=true', '-o', $stageRoot)
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $stageRoot 'StageManager.runtimeconfig.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $frameworks = @($runtimeConfig.runtimeOptions.includedFrameworks)
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        $selected = @($frameworks | Where-Object { $_.name -eq $name })
        if ($selected.Count -ne 1 -or $selected[0].version -ne '10.0.12') { throw ('Runtime redistribution notices require ' + $name + ' 10.0.12; update and verify notices before changing the runtime.') }
    }
    $dockSource = Join-Path $repoRoot 'src/Dock'
    $dockExe = Join-Path $dockRoot 'PersonalDock-v4.exe'
    $compilerArgs = @('/nologo', '/utf8output', '/target:winexe', '/optimize+', '/platform:x64', ('/out:' + $dockExe), ('/win32manifest:' + (Join-Path $dockSource 'highdpi.manifest')))
    foreach ($assembly in @('PresentationFramework', 'PresentationCore', 'WindowsBase', 'UIAutomationTypes', 'UIAutomationClient')) {
        $compilerArgs += '/reference:' + (Join-Path $frameworkRoot ('WPF/' + $assembly + '.dll'))
    }
    $compilerArgs += @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xaml.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Web.Extensions.dll', ('/reference:' + (Join-Path $frameworkRoot 'Accessibility.dll')))
    foreach ($file in @('PersonalDock.cs', 'DockAppLauncher.cs', 'DockLinkLauncher.cs', 'DockDocumentLauncher.cs', 'MacBottomDock.cs', 'NativeDockIcons.cs', 'DockHoverChecks.cs', 'DockPins.cs', 'DockPinPicker.cs', 'MacBottomDock.Pins.cs', 'DockPinChecks.cs', 'DockTrayRecovery.cs', 'DockRuntime.cs', 'DockWatchdog.cs')) {
        $compilerArgs += Join-Path $dockSource $file
    }
    foreach ($file in @('BrowserActivationIntent.cs', 'DocumentTarget.cs', 'TaskbarToggleQueue.cs')) {
        $compilerArgs += Join-Path $repoRoot ('src/StageManager/Services/' + $file)
    }
    & $csc @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Dock compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $dockSource 'native-dock.config') -Destination ($dockExe + '.config')
    $launcherExe = Join-Path $packageRoot 'MacDesk.exe'
    & $csc '/nologo' '/utf8output' '/target:winexe' '/optimize+' '/platform:x64' '/reference:System.dll' '/reference:System.Drawing.dll' '/reference:System.Windows.Forms.dll' ('/out:' + $launcherExe) ('/win32manifest:' + (Join-Path $repoRoot 'src/Launcher/app.manifest')) (Join-Path $repoRoot 'src/Launcher/Program.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Launcher compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'src/Launcher/launcher.config') -Destination ($launcherExe + '.config')
    foreach ($file in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        $doc = Join-Path $repoRoot $file
        if (-not (Test-Path -LiteralPath $doc)) { throw ('Required release document is missing: ' + $file) }
        Copy-Item -LiteralPath $doc -Destination (Join-Path $packageRoot $file)
    }
    # These source-controlled directories have been audited for public release.
    # In particular, native LGPL corresponding source and full terms accompany
    # the replaceable uiohook.dll, rather than relying on a future download.
    foreach ($directory in @('docs', 'licenses', 'third_party/libuiohook')) {
        $source = Join-Path $repoRoot $directory
        if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw ('Required release directory is missing: ' + $directory) }
        $destination = Join-Path $packageRoot $directory
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
    }
    $sourceRepository = if ($env:GITHUB_REPOSITORY -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { 'https://github.com/' + $env:GITHUB_REPOSITORY } else { 'See the source repository accompanying this distribution.' }
    $sourcePointer = @(('MacDesk source: ' + $sourceRepository), ('Stage version: ' + $StageVersion), ('Dock version: ' + (Get-Item -LiteralPath $dockExe).VersionInfo.FileVersion), 'Matching libuiohook source and license terms: third_party/libuiohook/ and licenses/')
    if ($env:GITHUB_REPOSITORY -match '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -and $env:GITHUB_SHA -match '^[0-9a-fA-F]{40}$') { $sourcePointer += 'Build commit: ' + $sourceRepository + '/commit/' + $env:GITHUB_SHA }
    [IO.File]::WriteAllLines((Join-Path $packageRoot 'SOURCE.txt'), $sourcePointer, (New-Object Text.UTF8Encoding($false)))
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'Test.ps1') -PackageRoot $packageRoot -ReportRoot $reportRoot }
    $hashLines = @()
    foreach ($file in (Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
        $hashLines += (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
    }
    [IO.File]::WriteAllLines((Join-Path $packageRoot 'SHA256.txt'), $hashLines, (New-Object Text.UTF8Encoding($false)))
    $zip = Join-Path $ArtifactsPath 'MacDesk-windows-x64.zip'
    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -CompressionLevel Optimal -Force
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($zip + '.sha256'), $zipHash + '  MacDesk-windows-x64.zip' + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    Write-Output ('Package: ' + $zip)
    Write-Output ('Fixture reports: ' + $reportRoot)
    Write-Output ('SHA256: ' + $zipHash)
} finally { Pop-Location }
