# Release commands

```powershell
cd G:\AmberDev\VenueHostRepo

# Clean local test folders from earlier manual packaging.
Remove-Item -Recurse -Force .\release, .\zipcheck -ErrorAction SilentlyContinue

# Build and create dist/VenueHost-latest.zip.
powershell -ExecutionPolicy Bypass -File .\scripts\Build-DalamudPackage.ps1

# Verify the zip has VenueHost.json at the root and includes the SQLite native dll.
Remove-Item -Recurse -Force .\zipcheck -ErrorAction SilentlyContinue
Expand-Archive .\dist\VenueHost-latest.zip .\zipcheck -Force
Get-ChildItem .\zipcheck
Get-Content .\zipcheck\VenueHost.json
[System.Reflection.AssemblyName]::GetAssemblyName((Resolve-Path ".\zipcheck\VenueHost.dll")).Version

# Commit and push.
git pull --rebase origin master
git add .
git add -f .\dist\VenueHost-latest.zip
git commit -m "Contrast implementation plus minor bugfixes"
git push
```
