from __future__ import annotations

import argparse
import collections
import json
import math
import statistics
from pathlib import Path
from typing import Any


def clamp_probability(value: float) -> float:
    return min(1 - 1e-9, max(1e-9, value))


def add_metric(target: dict[str, float], probability: float, positives: int, total: int) -> None:
    if total <= 0:
        return
    probability = clamp_probability(probability)
    negatives = total - positives
    target["n"] += total
    target["positives"] += positives
    target["brier"] += positives * (1 - probability) ** 2 + negatives * probability ** 2
    target["logloss"] += -(positives * math.log(probability) + negatives * math.log(1 - probability))


def finish(metric: dict[str, float]) -> dict[str, float]:
    total = metric["n"] or 1
    return {"observations": int(metric["n"]), "positive_rate": metric["positives"] / total,
            "brier": metric["brier"] / total, "logloss": metric["logloss"] / total}


def weighted_quantile(items: list[tuple[float, int]], q: float) -> float:
    ordered = sorted(items)
    total = sum(weight for _, weight in ordered)
    threshold = total * q
    seen = 0
    for value, weight in ordered:
        seen += weight
        if seen >= threshold:
            return value
    return ordered[-1][0] if ordered else 0


def main() -> int:
    parser = argparse.ArgumentParser(description="Evaluate Build Advisor corpus priors on held-out splits")
    parser.add_argument("report", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    rows = report["choice_contexts"]

    global_counts = collections.Counter()
    card_counts = collections.Counter()
    for row in rows:
        train = row["train"]
        global_counts[(row["character"], row["act"], "offers")] += train["offers"]
        global_counts[(row["character"], row["act"], "picks")] += train["picks"]
        card_counts[(row["character"], row["card"], row["act"], "offers")] += train["offers"]
        card_counts[(row["character"], row["card"], row["act"], "picks")] += train["picks"]

    results: dict[str, Any] = {}
    calibration: dict[str, list[dict[str, float]]] = {}
    for split in ("validation", "test"):
        metrics = {name: collections.defaultdict(float) for name in ("global", "card", "context", "quality")}
        bins = {name: [collections.defaultdict(float) for _ in range(10)] for name in ("card", "context", "quality")}
        for row in rows:
            held = row[split]
            total, positives = held["offers"], held["picks"]
            if total <= 0:
                continue
            key = (row["character"], row["act"])
            offers = global_counts[key + ("offers",)]
            picks = global_counts[key + ("picks",)]
            global_rate = picks / offers if offers else 1 / 3
            card_key = (row["character"], row["card"], row["act"])
            card_offers = card_counts[card_key + ("offers",)]
            card_picks = card_counts[card_key + ("picks",)]
            card_rate = (card_picks + global_rate * 48) / (card_offers + 48)
            probabilities = {"global": global_rate, "card": card_rate,
                             "context": float(row["pick_rate"]), "quality": float(row["quality"])}
            for name, probability in probabilities.items():
                add_metric(metrics[name], probability, positives, total)
                if name != "global":
                    bucket = min(9, int(probability * 10))
                    bins[name][bucket]["n"] += total
                    bins[name][bucket]["predicted"] += probability * total
                    bins[name][bucket]["actual"] += positives
        finished = {name: finish(metric) for name, metric in metrics.items()}
        baseline = finished["global"]["brier"]
        for item in finished.values():
            item["brier_improvement_vs_global"] = (baseline - item["brier"]) / baseline if baseline else 0
        results[split] = finished
        calibration[split] = []
        for name, groups in bins.items():
            for index, group in enumerate(groups):
                if group["n"]:
                    calibration[split].append({"model": name, "bin": index, "n": int(group["n"]),
                                               "predicted": group["predicted"] / group["n"],
                                               "actual": group["actual"] / group["n"]})

    distributions = {}
    for name, field in (("context", "pick_rate"), ("quality", "quality")):
        weighted = [(float(row[field]), int(row["train"]["offers"])) for row in rows if row["train"]["offers"]]
        distributions[name] = {f"p{int(q * 100):02d}": weighted_quantile(weighted, q)
                               for q in (0.01, 0.05, 0.25, 0.5, 0.75, 0.95, 0.99)}

    output = args.output or args.report.with_name("model-evaluation.json")
    payload = {"schema": 1, "results": results, "distributions": distributions, "calibration": calibration}
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    lines = ["# Проверка модели на отложенных данных", "",
             "Меньше Brier и log-loss — лучше. Улучшение считается относительно одной средней вероятности выбора для персонажа и акта.", ""]
    for split in ("validation", "test"):
        lines += [f"## {split}", "", "| Модель | Наблюдения | Brier | Log-loss | Улучшение |",
                  "|---|---:|---:|---:|---:|"]
        for name, item in results[split].items():
            lines.append(f"| {name} | {item['observations']:,} | {item['brier']:.6f} | {item['logloss']:.6f} | {item['brier_improvement_vs_global']:+.2%} |")
        lines.append("")
    lines += ["## Распределение обучающих вероятностей", ""]
    for name, values in distributions.items():
        lines.append(f"- {name}: " + ", ".join(f"{key}={value:.3f}" for key, value in values.items()))
    lines += ["", "`quality` допускается к интеграции только если она улучшает обе отложенные части и остаётся калиброванной."]
    output.with_suffix(".md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"output": str(output), "results": results, "distributions": distributions}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
