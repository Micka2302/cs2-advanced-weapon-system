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

# Explicitly include Tomlyn.dll only when it is still present in the dependency graph.
# The plugin now uses JSON configs directly, but CounterStrikeSharp may still reference Tomlyn.
$depsPath = Join-Path $pluginBuildOutput "$pluginName.deps.json"
if (-not (Test-Path $depsPath)) {
    throw "Dependency manifest not found at $depsPath"
}

$depsJson = Get-Content -Raw $depsPath | ConvertFrom-Json
$tomlynLibraryName = $depsJson.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'Tomlyn/*' } | Select-Object -First 1
if ($tomlynLibraryName) {
    $tomlynVersion = ($tomlynLibraryName -split '/', 2)[1]
    $nugetLocalsLine = (& dotnet nuget locals global-packages --list | Select-Object -First 1)
    if (-not $nugetLocalsLine) {
        throw "Unable to resolve NuGet global-packages path."
    }

    $globalPackagesPath = ($nugetLocalsLine -split ':\s*', 2)[1].Trim()
    if ([string]::IsNullOrWhiteSpace($globalPackagesPath)) {
        throw "NuGet global-packages path is empty."
    }

    $tomlynSourceCandidates = @(
        (Join-Path $globalPackagesPath "tomlyn/$tomlynVersion/lib/net8.0/Tomlyn.dll"),
        (Join-Path $globalPackagesPath "tomlyn/$tomlynVersion/lib/netstandard2.0/Tomlyn.dll")
    )
    $tomlynSource = $tomlynSourceCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $tomlynSource) {
        throw "Tomlyn.dll v$tomlynVersion not found in NuGet global-packages."
    }

    Copy-Item -Path $tomlynSource -Destination (Join-Path $pluginTarget 'Tomlyn.dll') -Force
}

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
