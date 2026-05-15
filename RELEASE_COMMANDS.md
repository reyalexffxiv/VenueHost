# Release commands

```powershell
cd G:\AmberDev\VenueHostRepo

# Clean local test folders from earlier manual packaging.
Remove-Item -Recurse -Force .\release, .\zipcheck -ErrorAction SilentlyContinue

# Build and create dist/VenueHost-latest.zip.
.\scripts\Build-DalamudPackage.ps1

# Verify the zip has VenueHost.json at the root and no nested latest.zip.
Remove-Item -Recurse -Force .\zipcheck -ErrorAction SilentlyContinue
Expand-Archive .\dist\VenueHost-latest.zip .\zipcheck -Force
Get-ChildItem .\zipcheck
Get-Content .\zipcheck\VenueHost.json

# Commit and push.
git pull --rebase origin master
git add .
git commit -m "Release v0.1.0.59 packaging cleanup"
git push
```

If Git says `dist/VenueHost-latest.zip` is ignored, use:

```powershell
git add -f dist/VenueHost-latest.zip
```
