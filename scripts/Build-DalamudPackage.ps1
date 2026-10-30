param(
    [string]$Configuration = "Release",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$projectPath = Join-Path $repo "VenueHost\VenueHost.csproj"
$manifestPath = Join-Path $repo "VenueHost\VenueHost.json"
$pluginMasterPath = Join-Path $repo "pluginmaster.json"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content $projectPath
    $Version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    throw "Could not determine VenueHost version from $projectPath."
}

$versionCore = ($Version -split '-', 2)[0]
if ($versionCore -notmatch '^\d+\.\d+\.\d+$') {
    throw "Expected a three-part semantic version, got '$Version'."
}
$expectedAssemblyVersion = "$versionCore.0"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if ($manifest.AssemblyVersion -ne $expectedAssemblyVersion) {
    throw "Version mismatch: expected '$expectedAssemblyVersion' but VenueHost.json AssemblyVersion is '$($manifest.AssemblyVersion)'."
}

$pluginMaster = Get-Content $pluginMasterPath -Raw | ConvertFrom-Json
$pluginMasterVersion = @($pluginMaster)[0].AssemblyVersion
if ($pluginMasterVersion -ne $expectedAssemblyVersion) {
    throw "Version mismatch: expected '$expectedAssemblyVersion' but pluginmaster.json AssemblyVersion is '$pluginMasterVersion'."
}

# Clean only the selected configuration. This prevents stale output DLLs from
# an earlier SDK/package version silently entering the public plugin ZIP.
Write-Host "Cleaning VenueHost ($Configuration)..."
dotnet clean .\VenueHost.sln -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet clean failed with exit code $LASTEXITCODE."
}

Write-Host "Building VenueHost $Version..."
dotnet build .\VenueHost.sln -c $Configuration /p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE. Package was not created."
}

$dist = Join-Path $repo "dist"
$stage = Join-Path $repo "release"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
New-Item -ItemType Directory -Force $dist | Out-Null

# Dalamud expects the plugin files at the root of the zip. Do not zip the whole build folder,
# otherwise nested folders or old latest.zip files can break installation/update.
$buildRoot = Join-Path $repo "VenueHost\bin\$Configuration"
$builtDll = Get-ChildItem $buildRoot -Recurse -File -Filter 'VenueHost.dll' |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
if ($null -eq $builtDll) { throw "Compiled VenueHost.dll missing under $buildRoot" }
$buildOutputDir = $builtDll.DirectoryName
$requiredFileNames = @(
    "VenueHost.dll",
    "VenueHost.deps.json",
    "VenueHost.json",
    "Microsoft.Data.Sqlite.dll",
    "SQLitePCLRaw.core.dll",
    "SQLitePCLRaw.batteries_v2.dll",
    "SQLitePCLRaw.provider.e_sqlite3.dll",
    "e_sqlite3.dll"
)

foreach ($fileName in $requiredFileNames) {
    $file = Get-ChildItem $buildOutputDir -Recurse -File -Filter $fileName |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $file) {
        throw "Required package file '$fileName' was not found under $buildRoot."
    }

    Copy-Item $file.FullName $stage -Force
}

$stagedManifest = Get-Content (Join-Path $stage "VenueHost.json") -Raw | ConvertFrom-Json
if ($stagedManifest.AssemblyVersion -ne $expectedAssemblyVersion) {
    throw "Staged manifest version mismatch: expected '$expectedAssemblyVersion', got '$($stagedManifest.AssemblyVersion)'."
}

$zipPath = Join-Path $dist "VenueHost-latest.zip"
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force

Write-Host "Created $zipPath"
Write-Host "Zip contents:"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = $zip.Entries
    $entryNames = $entries | ForEach-Object { $_.FullName }
    foreach ($required in $requiredFileNames) {
        if ($entryNames -notcontains $required) {
            throw "Zip validation failed: '$required' is missing from the package root."
        }
    }

    foreach ($entry in $entries) {
        " - $($entry.FullName)"
    }
}
finally {
    $zip.Dispose()
}
