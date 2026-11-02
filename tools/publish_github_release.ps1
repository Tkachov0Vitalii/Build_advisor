# Publishes the current version on GitHub from the owner's computer (the cloud session cannot
# create tags or releases). Needs GitHub CLI: winget install GitHub.cli; gh auth login.
#   powershell -ExecutionPolicy Bypass -File tools\publish_github_release.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$version = (Get-Content (Join-Path $root "build-advisor.json") -Raw | ConvertFrom-Json).version
$tag = "v$version"
$notes = Join-Path $root "docs/releases/$tag.md"
$assets = @("release/BuildAdvisor-$version.zip", "release/BuildAdvisor-$version-workshop.zip", "release/SHA256SUMS.txt")
foreach ($file in @($notes) + $assets) { if (-not (Test-Path $file)) { throw "Missing $file — run tools\make_release.ps1 first." } }
if (-not (git remote | Select-String -Quiet "^origin$")) { git remote add origin https://github.com/Tkachov0Vitalii/Build_advisor.git }
git fetch origin main
if (-not (git tag --list $tag)) { git tag $tag origin/main }
git push origin --tags
gh release view $tag 2>$null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag @assets --clobber
} else {
    gh release create $tag @assets --title "Build Advisor $version" --notes-file $notes --latest
}
Write-Host "Published $tag"
