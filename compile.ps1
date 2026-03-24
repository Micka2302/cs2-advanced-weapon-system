#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$solution = Join-Path $root 'cs2-advanced-weapon-system.sln'
$pluginName = 'cs2-advanced-weapon-system'
$buildOutputRoot = Join-Path $root 'BuildOutput'
$pluginBuildOutput = Join-Path $buildOutputRoot "plugins/$pluginName"
$configBuildOutput = Join-Path $buildOutputRoot "configs/plugins/$pluginName"
$compiledRoot = Join-Path $root 'compiled'
$pluginTarget = Join-Path $compiledRoot "plugins/$pluginName"
$configTarget = Join-Path $compiledRoot "configs/plugins/$pluginName"
$tomlynSource = Join-Path $root 'Tomlyn.dll'

# Clean staging directory
Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null
New-Item -ItemType Directory -Path $configTarget -Force | Out-Null

dotnet restore $solution
dotnet build $solution -c Release --no-restore --nologo

if (-not (Test-Path $pluginBuildOutput)) {
    throw "Plugin build output not found at $pluginBuildOutput"
}

if (-not (Test-Path $configBuildOutput)) {
    throw "Config build output not found at $configBuildOutput"
}

# Stage plugin + config files
Copy-Item -Path (Join-Path $pluginBuildOutput '*') -Destination $pluginTarget -Recurse -Force
Copy-Item -Path (Join-Path $configBuildOutput '*') -Destination $configTarget -Recurse -Force

# Explicitly include Tomlyn.dll required by this plugin
if (-not (Test-Path $tomlynSource)) {
    throw "Tomlyn.dll not found at $tomlynSource"
}
Copy-Item -Path $tomlynSource -Destination (Join-Path $pluginTarget 'Tomlyn.dll') -Force

# Keep only linux and Windows runtimes to mirror release packaging
$runtimeDir = Join-Path $pluginTarget 'runtimes'
if (Test-Path $runtimeDir) {
    $keep = @('linux-x64', 'win-x64')
    Get-ChildItem $runtimeDir -Directory | Where-Object { $keep -notcontains $_.Name } | Remove-Item -Recurse -Force
} else {
    Write-Host '[WARN] No runtimes directory found in build output.'
}

# Strip CSS API (already provided by server)
$cssApi = Join-Path $pluginTarget 'CounterStrikeSharp.API.dll'
if (Test-Path $cssApi) {
    Remove-Item $cssApi -Force
}

# Zip the staged plugin for convenience
$zipPath = Join-Path $compiledRoot "$pluginName.zip"
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $compiledRoot '*') -DestinationPath $zipPath

Write-Host "[OK] Build finished."
Write-Host " - Folder: $compiledRoot"
Write-Host " - Zip:    $zipPath"
