from __future__ import annotations

import argparse
import collections
import json
import math
import sqlite3
import time
from pathlib import Path
from typing import Any

from analyze_model_corpus import clean, deck_at, historical_deck, iter_points, split_for


def bucket(value: float, boundaries: tuple[float, ...]) -> int:
    return sum(value > boundary for boundary in boundaries)


def nearest_curve(curve: dict[str, int], floor: int) -> int:
    if not curve:
        return max(15, min(28, 12 + floor // 3))
    key = min((int(item) for item in curve), key=lambda item: abs(item - floor))
    return int(curve[str(key)])


def features(character: str, act: int, deck_size: int, expected: int, best: float, mean: float,
             copies: int, hp_ratio: float, ascension: int, room: str, offers: int) -> tuple[str, ...]:
    excess = deck_size - expected
    excess_bucket = bucket(excess, (-4, -1, 2, 5, 9))
    best_bucket = bucket(best, (.10, .20, .30, .40, .50))
    mean_bucket = bucket(mean, (.10, .20, .30, .40))
    copy_bucket = min(3, copies)
    values = (
        f"character={character}", f"act={act}",
        f"deck={bucket(deck_size, (12, 18, 24, 30, 36))}", f"excess={excess_bucket}",
        f"best={best_bucket}", f"mean={mean_bucket}", f"copies={copy_bucket}",
        f"hp={bucket(hp_ratio, (.25, .50, .75))}", f"ascension={bucket(ascension, (0, 4, 9))}",
        # The live RunContext does not currently expose the reward room type.
        # Do not train on a feature that production cannot reproduce.
        f"offers={min(4, offers)}",
        f"act_excess={act}:{excess_bucket}", f"best_excess={best_bucket}:{excess_bucket}",
        f"character_act={character}:{act}",
    )
    return values


def sigmoid(value: float) -> float:
    if value >= 0:
        return 1 / (1 + math.exp(-min(40, value)))
    exp = math.exp(max(-40, value))
    return exp / (1 + exp)


def fit(rows: dict[tuple[str, ...], list[int]], epochs: int = 100) -> tuple[float, dict[str, float]]:
    total = sum(value[0] for value in rows.values())
    positives = sum(value[1] for value in rows.values())
    rate = (positives + 1) / (total + 2)
    bias = math.log(rate / (1 - rate)); weights: dict[str, float] = collections.defaultdict(float)
    first: dict[str, float] = collections.defaultdict(float); second: dict[str, float] = collections.defaultdict(float)
    bias_first = bias_second = 0.0
    step = 0
    for epoch in range(epochs):
        gradients: dict[str, float] = collections.defaultdict(float); bias_gradient = 0.0
        for keys, (count, skipped) in rows.items():
            probability = sigmoid(bias + sum(weights[key] for key in keys))
            error = (probability * count - skipped) / total
            bias_gradient += error
            for key in keys:
                gradients[key] += error
        step += 1; learning_rate = .08
        bias_first = .9 * bias_first + .1 * bias_gradient
        bias_second = .999 * bias_second + .001 * bias_gradient * bias_gradient
        bias -= learning_rate * (bias_first / (1 - .9 ** step)) / (math.sqrt(bias_second / (1 - .999 ** step)) + 1e-8)
        for key in set(weights) | set(gradients):
            gradient = gradients[key] + .0008 * weights[key]
            first[key] = .9 * first[key] + .1 * gradient
            second[key] = .999 * second[key] + .001 * gradient * gradient
            weights[key] -= learning_rate * (first[key] / (1 - .9 ** step)) / (math.sqrt(second[key] / (1 - .999 ** step)) + 1e-8)
        if epoch % 25 == 24:
            print(f"fit epoch={epoch + 1}/{epochs}", flush=True)
    return bias, dict(weights)


def evaluate(rows: dict[tuple[str, ...], list[int]], bias: float, weights: dict[str, float], fixed_threshold: float | None = None) -> dict[str, Any]:
    total = positives = 0; brier = logloss = 0.0; scored = []
    for keys, (count, skipped) in rows.items():
        probability = min(1 - 1e-9, max(1e-9, sigmoid(bias + sum(weights.get(key, 0) for key in keys))))
        total += count; positives += skipped
        brier += skipped * (1 - probability) ** 2 + (count - skipped) * probability ** 2
        logloss -= skipped * math.log(probability) + (count - skipped) * math.log(1 - probability)
        scored.append((probability, count, skipped))
    best = None
    thresholds = [fixed_threshold] if fixed_threshold is not None else [step / 100 for step in range(5, 96)]
    for threshold in thresholds:
        tp = fp = tn = fn = 0
        for probability, count, skipped in scored:
            if probability >= threshold: tp += skipped; fp += count - skipped
            else: fn += skipped; tn += count - skipped
        recall_skip = tp / max(1, tp + fn); recall_take = tn / max(1, tn + fp)
        balanced = (recall_skip + recall_take) / 2
        item = (balanced, threshold, recall_skip, recall_take, tp + tn)
        if best is None or item > best: best = item
    assert best is not None
    return {"events": total, "skips": positives, "skip_rate": positives / max(1, total),
            "brier": brier / max(1, total), "logloss": logloss / max(1, total),
            "threshold": best[1], "balanced_accuracy": best[0], "skip_recall": best[2],
            "take_recall": best[3], "accuracy": best[4] / max(1, total)}


def main() -> int:
    parser = argparse.ArgumentParser(description="Train an explainable take-vs-skip model")
    parser.add_argument("corpus", type=Path)
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("report", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--progress", type=int, default=10000)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    choice = {(row["character"], row["card"], row["act"], row["deck_band"], row["copies_before"]): float(row["pick_rate"])
              for row in report["choice_contexts"] if row["train"]["offers"] >= 20}
    card_act: dict[tuple[str, str, int], list[int]] = collections.defaultdict(lambda: [0, 0])
    for row in report["choice_contexts"]:
        card_act[(row["character"], row["card"], row["act"])][0] += row["train"]["offers"]
        card_act[(row["character"], row["card"], row["act"])][1] += row["train"]["picks"]
    card_rate = {key: (picked + 6) / (offered + 24) for key, (offered, picked) in card_act.items()}
    aggregates = {split: collections.defaultdict(lambda: [0, 0]) for split in ("train", "validation", "test")}
    with sqlite3.connect(args.snapshot) as db:
        rows = list(db.execute("SELECT run_hash, character, group_hash FROM candidates WHERE status='done'"))
    started = time.monotonic()
    for index, (run_hash, character_raw, group_hash) in enumerate(rows, start=1):
        split = split_for(group_hash, run_hash); path = args.corpus / "runs" / character_raw.lower() / f"{run_hash}.run"
        try:
            run = json.loads(path.read_text(encoding="utf-8")); players = run.get("players") or []
            if not players: continue
            character = clean(players[0].get("character")).lower(); history = historical_deck(run)
            ascension = int(run.get("ascension") or 0); copies_cache: dict[int, collections.Counter] = {}
            for act, floor, point in iter_points(run):
                if str(point.get("map_point_type") or "").lower() == "shop": continue
                deck = deck_at(history, floor); counts = copies_cache.setdefault(floor, collections.Counter(deck)); deck_band = min(5, len(deck) // 8)
                expected = nearest_curve(report["expected_deck_by_floor"].get(character, {}), floor)
                room = str(point.get("map_point_type") or "unknown").lower()
                for stats in point.get("player_stats") or []:
                    choices = stats.get("card_choices") or []
                    offered = [clean((item.get("card") or {}).get("id")) for item in choices]
                    offered = [card for card in offered if card]
                    if len(offered) < 2: continue
                    signals = [choice.get((character, card, act, deck_band, min(3, counts[card])), card_rate.get((character, card, act), .24)) for card in offered]
                    max_copies = max((counts[card] for card in offered), default=0)
                    hp = int(stats.get("current_hp") or 0); max_hp = max(1, int(stats.get("max_hp") or hp or 1))
                    keys = features(character, act, len(deck), expected, max(signals), sum(signals) / len(signals), max_copies,
                                    hp / max_hp, ascension, room, len(offered))
                    target = 0 if any(item.get("was_picked") for item in choices) else 1
                    aggregates[split][keys][0] += 1; aggregates[split][keys][1] += target
        except Exception:
            continue
        if args.progress and index % args.progress == 0:
            events = sum(value[0] for split_rows in aggregates.values() for value in split_rows.values())
            print(f"scanned={index:,}/{len(rows):,} events={events:,} speed={index / max(.1, time.monotonic() - started):,.0f}/s", flush=True)
    bias, weights = fit(aggregates["train"])
    validation = evaluate(aggregates["validation"], bias, weights)
    test_diagnostic = evaluate(aggregates["test"], bias, weights)
    test = evaluate(aggregates["test"], bias, weights, validation["threshold"])
    # Deployment threshold is selected on validation only. Recalculate test classification at that fixed threshold later in C#.
    payload = {"schema": 1, "bias": round(bias, 8), "weights": {key: round(value, 8) for key, value in sorted(weights.items())},
               "threshold": validation["threshold"], "train_events": sum(x[0] for x in aggregates["train"].values()),
               "validation": validation, "test": test, "test_diagnostic_threshold": test_diagnostic, "feature_contract": "skip-v1"}
    args.output.write_text(json.dumps(payload, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(json.dumps({"weights": len(weights), "validation": validation, "test": test,
                      "test_diagnostic_threshold": test_diagnostic, "output": str(args.output)}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
