<#
.SYNOPSIS
    Builds the plugin and writes the release archive to artifacts\.

.DESCRIPTION
    Derives the version from Plugin.cs, which is the single place it lives,
    and refuses to package when the csproj drifted away from it - an archive
    named after a version the assembly does not carry is worse than no
    archive. Runs the soft-dependency check before packaging, because a
    violation there breaks OTHER mods and would not show up in any playtest of
    this one.
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifactRoot = Join-Path $repositoryRoot "artifacts"
$projectPath = Join-Path $repositoryRoot "RaidReviewOverlay.csproj"
$assemblyName = "maschine-RaidReviewOverlay"

$pluginSource = Get-Content -LiteralPath (Join-Path $repositoryRoot "Plugin.cs") -Raw
$versionMatch = [regex]::Match($pluginSource, 'PluginVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $versionMatch.Success) {
    throw "PluginVersion was not found in Plugin.cs"
}
$modVersion = $versionMatch.Groups[1].Value

$projectText = Get-Content -LiteralPath $projectPath -Raw
$projectVersionMatch = [regex]::Match($projectText, '<AssemblyVersion>([0-9]+\.[0-9]+\.[0-9]+)</AssemblyVersion>')
if (-not $projectVersionMatch.Success) {
    throw "AssemblyVersion was not found in $projectPath"
}
if ($projectVersionMatch.Groups[1].Value -ne $modVersion) {
    throw "Version mismatch: Plugin.cs says $modVersion, the csproj says $($projectVersionMatch.Groups[1].Value)."
}

$stageRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "RaidReviewOverlay-v$modVersion"))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot "$assemblyName-v$modVersion.zip"))
if (-not $stageRoot.StartsWith([System.IO.Path]::GetFullPath($artifactRoot), [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release staging path escaped the repository artifact directory."
}

if (Test-Path -LiteralPath $stageRoot) { Remove-Item -LiteralPath $stageRoot -Recurse -Force }
if (Test-Path -LiteralPath $archivePath) { Remove-Item -LiteralPath $archivePath -Force }

# No deploy into the game install: packaging should not touch what is running.
dotnet build $projectPath -c $Configuration --no-incremental -p:DeployToSpt=false
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

& (Join-Path $PSScriptRoot "Test-SoftDependency.ps1") -Configuration $Configuration
& (Join-Path $PSScriptRoot "Test-ConfigKeys.ps1")

# The DLL goes straight into plugins, without a folder of its own: it is a
# single file with nothing to keep next to it.
$pluginDirectory = Join-Path $stageRoot "BepInEx\plugins"
New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot "bin\$Configuration\$assemblyName.dll") -Destination $pluginDirectory

# No documents in the archive: the whole thing is meant to be extracted over the
# game folder in one go, so anything outside BepInEx lands loose in the SPT root.
# README, LICENSE and CHANGELOG live in the repository and on the release page.

# Never ship the soft dependency: the player installs Anvil-WebOverlay itself,
# and a stale copy next to the real one is a version conflict waiting to load.
$stray = Get-ChildItem -LiteralPath $pluginDirectory -Filter "Anvil-WebOverlay*" -ErrorAction SilentlyContinue
if ($stray) {
    throw "The staging directory contains Anvil-WebOverlay files: $($stray.Name -join ', ')"
}

# The archive root holds directories only, and only ones the game has. A loose
# file here would be extracted straight into the SPT installation root.
$looseFiles = Get-ChildItem -LiteralPath $stageRoot -File
if ($looseFiles) {
    throw "Files in the archive root would extract into the game folder: $($looseFiles.Name -join ', ')"
}
$rootEntries = Get-ChildItem -LiteralPath $stageRoot | ForEach-Object { $_.Name }
$unexpected = $rootEntries | Where-Object { $_ -ne "BepInEx" }
if ($unexpected) {
    throw "Unexpected entries in the archive root: $($unexpected -join ', ')"
}

Compress-Archive -Path (Join-Path $stageRoot "BepInEx") -DestinationPath $archivePath

Write-Host "Release package: $archivePath" -ForegroundColor Green
