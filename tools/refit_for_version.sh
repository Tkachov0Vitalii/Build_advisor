#!/usr/bin/env bash
# 7.5: refit the bundled models on runs of one game version after a balance patch.
#   tools/refit_for_version.sh <work dir> <slice version> <export version> [<export version> ...]
# e.g. tools/refit_for_version.sh ../_refit v0.111.0 v0.111.1 v0.111.0
#   <slice version>   Spire Codex stats slice used for the metrics (a "stat_versions" entry,
#                     see https://spire-codex.com/api/runs/versions)
#   <export version>  build_id values whose runs are exported (newest first)
# Steps (each is skipped when its output exists, so the script resumes):
#   1. runs -> extract (tools/build_version_extract.py; MAX_RUNS, default 150000; START = a date when
#      the oldest export version was current, e.g. START=2026-11-01 for v0.111.0)
#   2. catalogues + card metrics of the slice (all runs and per character)
#   3. Ancient relic values  -> <work>/ancient.json, report, C# table preview
#   4. relic x card synergies -> <work>/synergies.json, report
#   5. take/skip features (C# replay) + bars -> <work>/take-skip.json, report
# Nothing in src/ is changed: compare the reports with docs/*-report.json and copy what passes
# (docs/PATCH_REFRESH.md lists the checks).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
work="$1"; slice="$2"; shift 2
mkdir -p "$work/api"
managed="${STS2_MANAGED_DIR:-$root/tools/bin/Debug/net9.0}"
api="https://spire-codex.com/api"

echo "== 1. extract ($*)"
python3 "$root/tools/build_version_extract.py" "$work/extract" "$@" --max-runs "${MAX_RUNS:-150000}" ${START:+--start "$START"}

echo "== 2. catalogues and $slice metrics"
get() { [ -s "$2" ] || curl -sSf --retry 3 -m 120 "$1" -o "$2"; }
get "$api/cards?lang=eng" "$work/api/cards-eng.json"
get "$api/cards?lang=rus" "$work/api/cards-rus.json"
get "$api/relics?lang=eng" "$work/api/relics-eng.json"
get "$api/runs/metrics/cards?bracket=$slice" "$work/api/metrics-cards-all.json"
for character in ironclad silent defect regent necrobinder; do
  get "$api/runs/metrics/cards?bracket=$slice&character=$character" "$work/api/metrics-cards-all-$character.json"
done
python3 - "$work/api/metrics-cards-all.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
if not d.get('data_through') or not any((r.get('lift_n') or 0) > 0 for r in d.get('rows', [])):
    sys.exit('the slice has no data yet (data_through null or no lifts): pick an older stat version')
print('slice runs', d['total_runs'], 'data through', d['data_through'])
PY

echo "== 3. Ancient relics"
[ -s "$work/ancient.json" ] || python3 "$root/tools/measure_ancient_relics.py" "$work/extract" "$work/extract/splits.tsv" \
  "$work/api/relics-eng.json" "$work/ancient.json" --report "$work/ancient-report.json" > /dev/null
python3 "$root/tools/gen_ancient_table.py" "$work/ancient.json" "$work/AncientRelicValues.Generated.cs" --builds "game versions $*"

echo "== 4. relic synergies"
[ -s "$work/synergies.json" ] || python3 "$root/tools/measure_relic_synergies.py" "$work/extract" "$work/extract/splits.tsv" \
  "$work/api/cards-eng.json" "$work/api/relics-eng.json" "$work/synergies.json" --report "$work/synergies-report.json"

echo "== 5. take/skip"
offline="$work/offline"
[ -f "$offline/OfflineAdvisorEvaluation.dll" ] || dotnet build "$root/tools/OfflineAdvisorEvaluation.csproj" -c Release \
  -p:Sts2ManagedDir="$managed" -o "$offline" -nologo -v quiet
[ -s "$work/takeskip.jsonl" ] || (cd "$offline" && dotnet OfflineAdvisorEvaluation.dll --take-skip "$work/extract" \
  "$work/extract/splits.tsv" "$work/api/cards-rus.json" "$work/api" "$work/takeskip.jsonl")
[ -s "$work/lift-centers.json" ] || (cd "$offline" && dotnet OfflineAdvisorEvaluation.dll --lift-centers "$work/api" \
  "$work/api/cards-rus.json" > "$work/lift-centers.json")
python3 "$root/tools/calibrate_take_skip.py" "$work/takeskip.jsonl" "$work/lift-centers.json" "$work/take-skip.json" \
  --report "$work/take-skip-report.json" > /dev/null

echo "== done: $work/{ancient,synergies,take-skip}-report.json, $work/AncientRelicValues.Generated.cs, $work/take-skip.json"
