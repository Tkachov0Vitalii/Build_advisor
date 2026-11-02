#!/usr/bin/env bash
# Builds a release of Build Advisor: compile, offline regression matrix, Harmony target
# validation, user ZIP and SHA256SUMS.txt.
#
#   tools/make_release.sh [path/to/data_sts2_PLATFORM_ARCH]
#
# The game assemblies default to tools/bin/Debug/net9.0 (sts2.dll, GodotSharp.dll, 0Harmony.dll
# copied from the game). They are only referenced, never packaged.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
managed="${1:-$root/tools/bin/Debug/net9.0}"
version="$(python3 -c "import json,sys; print(json.load(open(sys.argv[1]))['version'])" "$root/build-advisor.json")"
# Outside the repo: generated obj/*.cs files must never join the main project glob.
work="$(mktemp -d)"
out="$root/release"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

for required in sts2.dll GodotSharp.dll 0Harmony.dll; do
  [ -f "$managed/$required" ] || { echo "Missing $managed/$required" >&2; exit 1; }
done

# Every place that carries the version must agree before anything is built.
grep -q "<Version>$version</Version>" "$root/BuildAdvisor.csproj" || { echo "csproj <Version> != $version" >&2; exit 1; }
grep -q "Current = \"$version\"" "$root/src/BuildAdvisorMod.cs" || { echo "ModVersion.Current != $version" >&2; exit 1; }
grep -q "^## $version " "$root/CHANGELOG.md" || { echo "CHANGELOG has no $version entry" >&2; exit 1; }
grep -q "Build Advisor $version" "$root/README.md" || { echo "README title != $version" >&2; exit 1; }
python3 "$root/tools/check_release_metadata.py"

echo "== Build $version"
dotnet build "$root/BuildAdvisor.csproj" -c Release -p:Sts2ManagedDir="$managed" -warnaserror -nologo -v quiet

echo "== Regression matrix"
dotnet run --project "$root/tools/RegressionRunner" -c Release -p:Sts2ManagedDir="$managed"

echo "== Harmony targets"
dotnet build "$root/tools/OfflineAdvisorEvaluation.csproj" -c Release -p:Sts2ManagedDir="$managed" -o "$work/offline" -nologo -v quiet
(cd "$work/offline" && dotnet OfflineAdvisorEvaluation.dll --validate-patches)

echo "== Package"
package="$root/bin/Release/net9.0/package/BuildAdvisor"
stage="$work/zip"; mkdir -p "$stage/build-advisor" "$out"
cp "$package/build-advisor.dll" "$package/build-advisor.json" "$package/build-advisor-run-priors.data" \
   "$package/segment-risk-v1.data" "$package/score-weights-v1.data" "$stage/build-advisor/"
mkdir -p "$stage/build-advisor/fonts" && cp "$package/fonts/"* "$stage/build-advisor/fonts/"
cp "$root/README.md" "$root/README_RU.md" "$root/CHANGELOG.md" "$root/INSTALL_RU.md" "$root/INSTALL_EN.md" \
   "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" "$stage/build-advisor/"
zip_path="$out/BuildAdvisor-$version.zip"
rm -f "$zip_path"
(cd "$stage" && zip -qrX "$zip_path" .)
# Steam Workshop workspace for the Mega Crit uploader: workshop.json, image.png, previews/,
# content/ = the mod files (the same files as the build-advisor folder of the user ZIP).
ws="$work/workshop/BuildAdvisor-workshop"; mkdir -p "$ws/content"
cp -r "$root/publish/workshop/workshop.json" "$root/publish/workshop/image.png" "$root/publish/workshop/README.md" "$root/publish/workshop/mod_id.txt" "$root/publish/workshop/previews" "$ws/"
cp -r "$stage/build-advisor/." "$ws/content/"
rm -f "$ws/content/"README*.md "$ws/content/CHANGELOG.md" "$ws/content/"INSTALL_*.md
workshop_zip="$out/BuildAdvisor-$version-workshop.zip"
rm -f "$workshop_zip"
(cd "$work/workshop" && zip -qrX "$workshop_zip" .)
(cd "$out" && sha256sum "BuildAdvisor-$version.zip" "BuildAdvisor-$version-workshop.zip" > SHA256SUMS.txt &&
  (cd "$stage/build-advisor" && sha256sum build-advisor.dll) >> SHA256SUMS.txt)
rm -rf "$work"
echo "== Done: $zip_path"
cat "$out/SHA256SUMS.txt"
