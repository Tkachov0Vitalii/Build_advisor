from __future__ import annotations

import argparse
import collections
import json
import math
import sqlite3
import time
from pathlib import Path
from typing import Any

from analyze_model_corpus import clean, historical_deck, split_for


def bucket(value: float, boundaries: tuple[float, ...]) -> int:
    return sum(value > boundary for boundary in boundaries)


def sigmoid(value: float) -> float:
    value = max(-40.0, min(40.0, value))
    return 1.0 / (1.0 + math.exp(-value))


def flatten(run: dict[str, Any]) -> list[tuple[int, int, dict[str, Any]]]:
    rows: list[tuple[int, int, dict[str, Any]]] = []
    floor = 0
    for act, points in enumerate(run.get("map_point_history") or [], start=1):
        for point in points or []:
            floor += 1
            rows.append((act, floor, point))
    return rows


def deck_size_at(history: list[tuple[str, int, int | None]], floor: int) -> int:
    return sum(1 for _, added, removed in history if added <= floor and (removed is None or removed > floor))


def relic_count_at(run: dict[str, Any], floor: int) -> int:
    players = run.get("players") or []
    if not players:
        return 0
    return sum(1 for relic in players[0].get("relics") or [] if int(relic.get("floor_added_to_deck") or 1) <= floor)


def point_stats(point: dict[str, Any]) -> dict[str, Any] | None:
    stats = point.get("player_stats") or []
    return stats[0] if stats else None


def room_type(point: dict[str, Any]) -> str:
    return str(point.get("map_point_type") or "unknown").lower()


def features(character: str, act: int, floor: int, hp: int, max_hp: int, gold: int,
             ascension: int, deck_size: int, relics: int, current: str, next_room: str) -> tuple[str, ...]:
    hp_bucket = bucket(hp / max(1, max_hp), (.20, .35, .50, .70, .90))
    deck_bucket = bucket(deck_size, (12, 18, 24, 30, 36))
    gold_bucket = bucket(gold, (49, 99, 149, 249))
    relic_bucket = bucket(relics, (2, 5, 8, 12))
    act_floor = min(5, max(0, (floor - 1) % 18 // 3))
    return (
        f"character={character}", f"act={act}", f"act_floor={act_floor}",
        f"hp={hp_bucket}", f"deck={deck_bucket}", f"gold={gold_bucket}", f"relics={relic_bucket}",
        f"ascension={bucket(ascension, (0, 4, 9))}", f"current={current}", f"next={next_room}",
        f"character_act={character}:{act}", f"act_hp={act}:{hp_bucket}",
        f"next_hp={next_room}:{hp_bucket}", f"next_act={next_room}:{act}",
    )


def fit(rows: dict[tuple[str, ...], list[int]], epochs: int = 100) -> tuple[float, dict[str, float]]:
    total = sum(row[0] for row in rows.values()); positives = sum(row[1] for row in rows.values())
    rate = (positives + 1) / (total + 2)
    bias = math.log(rate / (1 - rate)); weights: dict[str, float] = collections.defaultdict(float)
    first: dict[str, float] = collections.defaultdict(float); second: dict[str, float] = collections.defaultdict(float)
    bf = bs = 0.0; step = 0
    for epoch in range(epochs):
        gradients: dict[str, float] = collections.defaultdict(float); bg = 0.0
        for keys, (count, dangerous) in rows.items():
            error = (sigmoid(bias + sum(weights[k] for k in keys)) * count - dangerous) / total
            bg += error
            for key in keys: gradients[key] += error
        step += 1; rate = .08
        bf = .9 * bf + .1 * bg; bs = .999 * bs + .001 * bg * bg
        bias -= rate * (bf / (1 - .9 ** step)) / (math.sqrt(bs / (1 - .999 ** step)) + 1e-8)
        for key in set(weights) | set(gradients):
            gradient = gradients[key] + .0008 * weights[key]
            first[key] = .9 * first[key] + .1 * gradient
            second[key] = .999 * second[key] + .001 * gradient * gradient
            weights[key] -= rate * (first[key] / (1 - .9 ** step)) / (math.sqrt(second[key] / (1 - .999 ** step)) + 1e-8)
        if epoch % 25 == 24: print(f"fit epoch={epoch + 1}/{epochs}", flush=True)
    return bias, dict(weights)


def fit_platt(rows: dict[tuple[str, ...], list[int]], bias: float, weights: dict[str, float]) -> tuple[float, float]:
    a, b = 1.0, 0.0
    total = sum(row[0] for row in rows.values())
    for _ in range(300):
        ga = gb = 0.0
        for keys, (count, dangerous) in rows.items():
            raw = bias + sum(weights.get(k, 0.0) for k in keys)
            error = sigmoid(a * raw + b) * count - dangerous
            ga += error * raw / total; gb += error / total
        a -= .05 * ga; b -= .05 * gb
    return a, b


def evaluate(rows: dict[tuple[str, ...], list[int]], bias: float, weights: dict[str, float], a: float, b: float) -> dict[str, Any]:
    total = positives = 0; brier = logloss = 0.0; bins = [[0, 0, 0.0] for _ in range(10)]
    for keys, (count, dangerous) in rows.items():
        raw = bias + sum(weights.get(k, 0.0) for k in keys)
        probability = min(1 - 1e-9, max(1e-9, sigmoid(a * raw + b)))
        total += count; positives += dangerous
        brier += dangerous * (1 - probability) ** 2 + (count - dangerous) * probability ** 2
        logloss -= dangerous * math.log(probability) + (count - dangerous) * math.log(1 - probability)
        index = min(9, int(probability * 10)); bins[index][0] += count; bins[index][1] += dangerous; bins[index][2] += probability * count
    reliability = [{"count": n, "predicted": round(ps / n, 4), "observed": round(y / n, 4)} for n, y, ps in bins if n]
    ece = sum(item["count"] * abs(item["predicted"] - item["observed"]) for item in reliability) / max(1, total)
    return {"events": total, "dangerous": positives, "rate": positives / max(1, total),
            "brier": brier / max(1, total), "logloss": logloss / max(1, total), "ece": ece,
            "reliability": reliability}


def main() -> int:
    parser = argparse.ArgumentParser(description="Train calibrated three-floor danger prediction")
    parser.add_argument("corpus", type=Path); parser.add_argument("snapshot", type=Path); parser.add_argument("output", type=Path)
    parser.add_argument("--progress", type=int, default=10000); parser.add_argument("--limit", type=int, default=0)
    args = parser.parse_args()
    with sqlite3.connect(args.snapshot) as db:
        query = "SELECT run_hash, character, group_hash FROM candidates WHERE status='done'"
        if args.limit: query += f" LIMIT {int(args.limit)}"
        runs = list(db.execute(query))
    aggregated = {split: collections.defaultdict(lambda: [0, 0]) for split in ("train", "validation", "test")}
    started = time.monotonic(); corrupt = 0
    for index, (run_hash, character_raw, group_hash) in enumerate(runs, start=1):
        try:
            run = json.loads((args.corpus / "runs" / character_raw.lower() / f"{run_hash}.run").read_text(encoding="utf-8"))
            if run.get("was_abandoned"): continue
            players = run.get("players") or []
            if not players: continue
            character = clean(players[0].get("character")).lower(); points = flatten(run); history = historical_deck(run)
            ascension = int(run.get("ascension") or 0); split = split_for(group_hash, run_hash)
            for i in range(len(points) - 3):
                act, floor, point = points[i]; future = points[i + 1:i + 4]
                # Do not let a target cross acts: the prediction is explicitly for the next local segment.
                if future[-1][0] != act: continue
                stats = point_stats(point)
                if not stats: continue
                hp = int(stats.get("current_hp") or 0); max_hp = max(1, int(stats.get("max_hp") or hp or 1))
                if hp <= 0: continue
                damage = sum(int((point_stats(row[2]) or {}).get("damage_taken") or 0) for row in future)
                future_hp = int((point_stats(future[-1][2]) or {}).get("current_hp") or 0)
                dangerous = int(future_hp <= 0 or damage >= .25 * max_hp)
                keys = features(character, act, floor, hp, max_hp, int(stats.get("current_gold") or 0), ascension,
                                deck_size_at(history, floor), relic_count_at(run, floor), room_type(point), room_type(future[0][2]))
                aggregated[split][keys][0] += 1; aggregated[split][keys][1] += dangerous
        except Exception: corrupt += 1
        if args.progress and index % args.progress == 0:
            events = sum(v[0] for rows in aggregated.values() for v in rows.values())
            print(f"scanned={index:,}/{len(runs):,} events={events:,} speed={index / max(.1, time.monotonic()-started):,.0f}/s", flush=True)
    bias, weights = fit(aggregated["train"]); a, b = fit_platt(aggregated["validation"], bias, weights)
    payload = {"schema": 1, "feature_contract": "segment-danger-v1", "target": "death or >=25% max-HP damage within next 3 map points",
               "bias": round(bias, 8), "weights": {k: round(v, 8) for k, v in sorted(weights.items())},
               "calibration": {"slope": round(a, 8), "intercept": round(b, 8)},
               "train_events": sum(v[0] for v in aggregated["train"].values()),
               "validation": evaluate(aggregated["validation"], bias, weights, a, b),
               "test": evaluate(aggregated["test"], bias, weights, a, b), "corrupt": corrupt}
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({"weights": len(weights), "validation": payload["validation"], "test": payload["test"], "output": str(args.output)}, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
