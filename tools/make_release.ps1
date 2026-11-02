# Builds a release of Build Advisor on Windows (PowerShell 5.1+ or 7):
# version check, -warnaserror build, offline regression matrix, Harmony target validation,
# user ZIP and SHA256SUMS.txt. Same steps as tools/make_release.sh.
#
#   powershell -ExecutionPolicy Bypass -File tools\make_release.ps1 [-ManagedDir <data_sts2_...>]
#
# Game assemblies default to tools\bin\Debug\net9.0 (sts2.dll, GodotSharp.dll, 0Harmony.dll copied
# from the game), then to the default Steam folder. They are only referenced, never packaged.
param([string]$ManagedDir = "")
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $ManagedDir) {
    $candidates = @(
        (Join-Path $root "tools/bin/Debug/net9.0"),
        "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64")
    $ManagedDir = $candidates | Where-Object { Test-Path (Join-Path $_ "sts2.dll") } | Select-Object -First 1
}
if (-not $ManagedDir) { throw "Game assemblies not found. Pass -ManagedDir <folder with sts2.dll>." }
foreach ($required in "sts2.dll", "GodotSharp.dll", "0Harmony.dll") {
    if (-not (Test-Path (Join-Path $ManagedDir $required))) { throw "Missing $(Join-Path $ManagedDir $required)" }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"

function Invoke-Checked([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)" }
}
function Read-Text([string]$relative) { [IO.File]::ReadAllText((Join-Path $root $relative)) }

# Every place that carries the version must agree before anything is built.
$version = (Read-Text "build-advisor.json" | ConvertFrom-Json).version
if ((Read-Text "BuildAdvisor.csproj") -notmatch "<Version>$([regex]::Escape($version))</Version>") { throw "csproj <Version> != $version" }
if ((Read-Text "src/BuildAdvisorMod.cs") -notmatch "Current = `"$([regex]::Escape($version))`"") { throw "ModVersion.Current != $version" }
if ((Read-Text "CHANGELOG.md") -notmatch "(?m)^## $([regex]::Escape($version)) ") { throw "CHANGELOG has no $version entry" }
if ((Read-Text "README.md") -notmatch "Build Advisor $([regex]::Escape($version))") { throw "README title != $version" }
python (Join-Path $root "tools/check_release_metadata.py"); if ($LASTEXITCODE -ne 0) { throw "Release metadata check failed" }

Write-Host "== Build $version"
Invoke-Checked "Build" { dotnet build (Join-Path $root "BuildAdvisor.csproj") -c Release "-p:Sts2ManagedDir=$ManagedDir" -warnaserror -nologo -v quiet }

Write-Host "== Regression matrix"
Invoke-Checked "Regression matrix" { dotnet run --project (Join-Path $root "tools/RegressionRunner") -c Release "-p:Sts2ManagedDir=$ManagedDir" }

Write-Host "== Harmony targets"
$work = Join-Path ([IO.Path]::GetTempPath()) ("build-advisor-" + [guid]::NewGuid().ToString("N"))
try {
    Invoke-Checked "Offline tool build" { dotnet build (Join-Path $root "tools/OfflineAdvisorEvaluation.csproj") -c Release "-p:Sts2ManagedDir=$ManagedDir" -o (Join-Path $work "offline") -nologo -v quiet }
    Push-Location (Join-Path $work "offline")
    try { Invoke-Checked "Harmony target validation" { dotnet OfflineAdvisorEvaluation.dll --validate-patches } } finally { Pop-Location }

    Write-Host "== Package"
    $package = Join-Path $root "bin/Release/net9.0/package/BuildAdvisor"
    $stage = Join-Path $work "zip"
    $modFolder = Join-Path $stage "build-advisor"
    New-Item -ItemType Directory -Force -Path $modFolder | Out-Null
    foreach ($file in "build-advisor.dll", "build-advisor.json", "build-advisor-run-priors.data", "segment-risk-v1.data", "score-weights-v1.data") {
        Copy-Item (Join-Path $package $file) $modFolder
    }
    $fontFolder = Join-Path $modFolder "fonts"
    New-Item -ItemType Directory -Force -Path $fontFolder | Out-Null
    Copy-Item (Join-Path $package "fonts/*") $fontFolder
    foreach ($file in "README.md", "README_RU.md", "CHANGELOG.md", "INSTALL_RU.md", "INSTALL_EN.md", "LICENSE", "THIRD_PARTY_NOTICES.md") {
        Copy-Item (Join-Path $root $file) $modFolder
    }
    $out = Join-Path $root "release"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $zip = Join-Path $out "BuildAdvisor-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
    # Steam Workshop workspace for the Mega Crit uploader (content/ = the mod files).
    $wsRoot = Join-Path $work "workshop"
    $ws = Join-Path $wsRoot "BuildAdvisor-workshop"
    New-Item -ItemType Directory -Force -Path (Join-Path $ws "content") | Out-Null
    foreach ($file in "workshop.json", "image.png", "README.md") { Copy-Item (Join-Path $root "publish/workshop/$file") $ws }
    Copy-Item -Recurse (Join-Path $root "publish/workshop/previews") $ws
    Copy-Item -Recurse (Join-Path $modFolder "*") (Join-Path $ws "content")
    Get-ChildItem (Join-Path $ws "content") -File | Where-Object { $_.Name -match '^(README.*|CHANGELOG|INSTALL_.*)\.md$' } | Remove-Item
    $workshopZip = Join-Path $out "BuildAdvisor-$version-workshop.zip"
    if (Test-Path $workshopZip) { Remove-Item $workshopZip }
    [IO.Compression.ZipFile]::CreateFromDirectory($wsRoot, $workshopZip)
    $sums = @(
        "$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant())  BuildAdvisor-$version.zip",
        "$((Get-FileHash $workshopZip -Algorithm SHA256).Hash.ToLowerInvariant())  BuildAdvisor-$version-workshop.zip",
        "$((Get-FileHash (Join-Path $modFolder 'build-advisor.dll') -Algorithm SHA256).Hash.ToLowerInvariant())  build-advisor.dll")
    [IO.File]::WriteAllLines((Join-Path $out "SHA256SUMS.txt"), $sums)
    Write-Host "== Done: $zip"
    $sums | ForEach-Object { Write-Host $_ }
}
finally { if (Test-Path $work) { Remove-Item -Recurse -Force $work } }
