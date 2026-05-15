param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0.66"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

Write-Host "Building VenueHost $Version..."
dotnet build .\VenueHost.sln -c $Configuration

$dist = Join-Path $repo "dist"
$stage = Join-Path $repo "release"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
New-Item -ItemType Directory -Force $dist | Out-Null

# Dalamud expects the plugin files at the root of the zip. Do not zip the whole build folder,
# otherwise nested folders or old latest.zip files can break installation/update.
$buildRoot = Join-Path $repo "VenueHost\bin"
$files = @(
    Get-ChildItem $buildRoot -Recurse -Filter "VenueHost.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "VenueHost.deps.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "VenueHost.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "Microsoft.Data.Sqlite.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "SQLitePCLRaw.core.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "SQLitePCLRaw.batteries_v2.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "SQLitePCLRaw.provider.e_sqlite3.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Get-ChildItem $buildRoot -Recurse -Filter "e_sqlite3.dll" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
) | Where-Object { $null -ne $_ }

foreach ($file in $files) {
    Copy-Item $file.FullName $stage -Force
}

$zipPath = Join-Path $dist "VenueHost-latest.zip"
Remove-Item -Force $zipPath -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -Force

Write-Host "Created $zipPath"
Write-Host "Zip contents:"
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::OpenRead($zipPath).Entries | ForEach-Object { " - $($_.FullName)" }
