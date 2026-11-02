# Publishes the current version on GitHub from the owner's computer (the cloud session cannot
# create tags or releases). Needs GitHub CLI (winget install GitHub.cli) and "gh auth login".
# Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File C:\Users\sunst\source\repos\BuildAdvisor\tools\publish_github_release.ps1
# Windows PowerShell 5.1 turns stderr of native tools (git progress) into errors under "Stop",
# so exit codes are checked by hand.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
function Check($what) { if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $what" -ForegroundColor Red; exit 1 } }

$version = (Get-Content (Join-Path $root "build-advisor.json") -Raw | ConvertFrom-Json).version
$tag = "v$version"
$notes = "docs/releases/$tag.md"
$assets = @("release/BuildAdvisor-$version.zip", "release/BuildAdvisor-$version-workshop.zip", "release/SHA256SUMS.txt")
foreach ($file in @($notes) + $assets) {
    if (-not (Test-Path $file)) { Write-Host "Missing $file (run tools\make_release.ps1 first)" -ForegroundColor Red; exit 1 }
}
gh auth status *> $null; Check "gh auth status (run: gh auth login)"
gh auth setup-git *> $null

$remotes = @(git remote)
if ($remotes -notcontains "origin") { git remote add origin https://github.com/Tkachov0Vitalii/Build_advisor.git; Check "git remote add" }
git fetch origin main *> $null; Check "git fetch origin main"
git tag --list $tag | Out-String | ForEach-Object { if ($_.Trim().Length -eq 0) { git tag $tag origin/main } }
git push origin --tags; Check "git push --tags"

gh release view $tag *> $null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag @assets --clobber; Check "gh release upload"
} else {
    gh release create $tag @assets --title "Build Advisor $version" --notes-file $notes --latest; Check "gh release create"
}
Write-Host "Published $tag" -ForegroundColor Green
