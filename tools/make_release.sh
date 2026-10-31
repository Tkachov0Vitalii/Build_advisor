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

echo "== Build $version"
dotnet build "$root/BuildAdvisor.csproj" -c Release -p:Sts2ManagedDir="$managed" -warnaserror -nologo -v quiet

echo "== Regression matrix"
rm -rf "$work"; mkdir -p "$work/harness"
cat > "$work/harness/Harness.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><LangVersion>latest</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems><StartupObject>RegressionHarness</StartupObject>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$root/src/*.cs" />
    <Compile Include="$root/tools/RegressionHarness.cs" />
    <None Include="$root/data/*.data" CopyToOutputDirectory="Always" />
    <Reference Include="sts2"><HintPath>$managed/sts2.dll</HintPath></Reference>
    <Reference Include="GodotSharp"><HintPath>$managed/GodotSharp.dll</HintPath></Reference>
    <Reference Include="0Harmony"><HintPath>$managed/0Harmony.dll</HintPath></Reference>
  </ItemGroup>
</Project>
EOF
dotnet build "$work/harness/Harness.csproj" -c Release -o "$work/harness/out" -nologo -v quiet
(cd "$work/harness/out" && dotnet Harness.dll)

echo "== Harmony targets"
dotnet build "$root/tools/OfflineAdvisorEvaluation.csproj" -c Release -p:Sts2ManagedDir="$managed" -o "$work/offline" -nologo -v quiet
(cd "$work/offline" && dotnet OfflineAdvisorEvaluation.dll --validate-patches)

echo "== Package"
package="$root/bin/Release/net9.0/package/BuildAdvisor"
stage="$work/zip"; mkdir -p "$stage/build-advisor" "$out"
cp "$package/build-advisor.dll" "$package/build-advisor.json" "$package/build-advisor-run-priors.data" \
   "$package/segment-risk-v1.data" "$package/tandrex-emblem.png" "$stage/build-advisor/"
cp "$root/README.md" "$root/CHANGELOG.md" "$root/INSTALL_RU.md" "$root/INSTALL_EN.md" \
   "$root/LICENSE" "$root/THIRD_PARTY_NOTICES.md" "$stage/"
zip_path="$out/BuildAdvisor-$version.zip"
rm -f "$zip_path"
(cd "$stage" && zip -qrX "$zip_path" .)
(cd "$out" && sha256sum "BuildAdvisor-$version.zip" > SHA256SUMS.txt &&
  (cd "$stage/build-advisor" && sha256sum build-advisor.dll) >> SHA256SUMS.txt)
rm -rf "$work"
echo "== Done: $zip_path"
cat "$out/SHA256SUMS.txt"
