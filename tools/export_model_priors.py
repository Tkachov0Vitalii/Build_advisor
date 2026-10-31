from __future__ import annotations

import argparse
import collections
import datetime as dt
import json
from pathlib import Path
from typing import Any


def clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def main() -> int:
    parser = argparse.ArgumentParser(description="Export compact, validated Build Advisor priors")
    parser.add_argument("report", type=Path)
    parser.add_argument("evaluation", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    evaluation = json.loads(args.evaluation.read_text(encoding="utf-8"))

    # Keep the legacy final-deck table for backward compatibility and diagnostics.
    legacy = collections.defaultdict(lambda: [0, 0, 0, 0])
    for row in report["duplicates"]:
        train = row["train"]
        key = (row["character"], row["card"])
        if row["copies"] >= 2:
            legacy[key][0] += train["runs"]
            legacy[key][1] += train["wins"]
        elif row["copies"] == 1:
            legacy[key][2] += train["runs"]
            legacy[key][3] += train["wins"]

    # Direct choice propensity answers the relevant question better than final
    # deck win rate: when a copy was actually offered, did players take it?
    choice = collections.defaultdict(lambda: [0, 0])
    for row in report["choice_contexts"]:
        train = row["train"]
        key = (row["character"], row["card"], row["act"], row["copies_before"])
        choice[key][0] += train["offers"]
        choice[key][1] += train["picks"]

    preferences: list[dict[str, Any]] = []
    groups = sorted({key[:3] for key in choice})
    for character, card, act in groups:
        base_offers, base_picks = choice[(character, card, act, 0)]
        if base_offers < 40:
            continue
        base_rate = (base_picks + 8) / (base_offers + 32)
        for copies_before in (1, 2, 3):
            offers, picks = choice[(character, card, act, copies_before)]
            if offers < 30:
                continue
            rate = (picks + base_rate * 32) / (offers + 32)
            adjustment = clamp((rate - base_rate) * 50, -15, 8)
            preferences.append({
                "character": character,
                "card": card,
                "act": act,
                "copies_before": copies_before,
                "offers": offers,
                "pick_rate": round(rate, 6),
                "baseline_rate": round(base_rate, 6),
                "adjustment": round(adjustment, 3),
            })

    choice_preferences: dict[str, list[Any]] = {}
    for row in report["choice_contexts"]:
        train = row["train"]
        if train["offers"] < 20:
            continue
        key = f"{row['character']}|{row['card']}|{row['act']}|{row['deck_band']}|{row['copies_before']}"
        choice_preferences[key] = [row["pick_rate"], train["offers"]]

    duplicate_rows = [
        {"character": character, "card": card, "duplicate_runs": values[0], "duplicate_wins": values[1],
         "single_runs": values[2], "single_wins": values[3]}
        for (character, card), values in sorted(legacy.items()) if values[0] >= 10 and values[2] >= 10
    ]
    payload = {
        "schema": 2,
        "generated_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "runs": report["runs"],
        "training_runs": report["splits"]["train"],
        "validation": evaluation["results"]["validation"]["context"],
        "test": evaluation["results"]["test"]["context"],
        "expected_deck_by_floor": report["expected_deck_by_floor"],
        "duplicates": duplicate_rows,
        "duplicate_preferences": preferences,
        "choice_preferences": choice_preferences,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(json.dumps({"runs": payload["runs"], "training_runs": payload["training_runs"],
                      "duplicates": len(duplicate_rows), "duplicate_preferences": len(preferences),
                      "choice_preferences": len(choice_preferences),
                      "bytes": args.output.stat().st_size, "output": str(args.output)}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
