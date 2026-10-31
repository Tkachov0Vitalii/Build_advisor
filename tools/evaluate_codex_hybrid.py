from __future__ import annotations

import argparse
import collections
import json
import math
from pathlib import Path
from typing import Any, Callable


def metric_rows(root: Any) -> list[dict[str, Any]]:
    if isinstance(root, list):
        return root
    for key in ("rows", "data", "items", "metrics", "results"):
        if isinstance(root.get(key), list):
            return root[key]
    return []


def load_scores(path: Path, corrected: bool) -> dict[str, float]:
    result: dict[str, float] = {}
    for row in metric_rows(json.loads(path.read_text(encoding="utf-8"))):
        if corrected and row.get("upgraded") is True:
            continue
        card_id = str(row.get("id") or row.get("entity_id") or "").upper()
        score = row.get("score", row.get("codex_score"))
        if card_id and isinstance(score, (int, float)):
            result[card_id] = float(score)
    return result


def isotonic_calibrator(rows: list[dict[str, Any]], scores: dict[str, float]) -> Callable[[str], float | None]:
    totals = collections.defaultdict(lambda: [0, 0])
    for row in rows:
        score = scores.get(row["card"])
        if score is None:
            continue
        totals[score][0] += row["train"]["offers"]
        totals[score][1] += row["train"]["picks"]
    blocks: list[list[float]] = []  # min score, max score, offers, picks
    for score, (offers, picks) in sorted(totals.items()):
        if offers <= 0:
            continue
        blocks.append([score, score, offers, picks])
        while len(blocks) >= 2:
            left, right = blocks[-2], blocks[-1]
            left_rate = (left[3] + 1) / (left[2] + 4)
            right_rate = (right[3] + 1) / (right[2] + 4)
            if left_rate <= right_rate:
                break
            blocks[-2:] = [[left[0], right[1], left[2] + right[2], left[3] + right[3]]]
    def predict(card_id: str) -> float | None:
        score = scores.get(card_id)
        if score is None or not blocks:
            return None
        block = min(blocks, key=lambda item: 0 if item[0] <= score <= item[1] else min(abs(score - item[0]), abs(score - item[1])))
        return (block[3] + 1) / (block[2] + 4)
    predict.blocks = blocks  # type: ignore[attr-defined]
    return predict


def brier_add(total: list[float], probability: float, positives: int, observations: int) -> None:
    negatives = observations - positives
    total[0] += positives * (1 - probability) ** 2 + negatives * probability ** 2
    total[1] += observations


def evaluate(rows: list[dict[str, Any]], split: str, predictors: dict[str, Callable[[dict[str, Any]], float | None]]) -> dict[str, Any]:
    totals = {name: [0.0, 0.0] for name in predictors}
    slices: dict[tuple[str, str], list[float]] = collections.defaultdict(lambda: [0.0, 0.0])
    for row in rows:
        held = row[split]
        observations, positives = held["offers"], held["picks"]
        if observations <= 0:
            continue
        for name, predictor in predictors.items():
            probability = predictor(row)
            if probability is None:
                continue
            brier_add(totals[name], probability, positives, observations)
            if name == "hybrid":
                for label in (row["character"], f"act-{row['act']}", f"deck-band-{row['deck_band']}", f"copies-{row['copies_before']}"):
                    brier_add(slices[("hybrid", label)], probability, positives, observations)
            if name == "codex":
                for label in (row["character"], f"act-{row['act']}", f"deck-band-{row['deck_band']}", f"copies-{row['copies_before']}"):
                    brier_add(slices[("codex", label)], probability, positives, observations)
    result = {name: {"brier": values[0] / values[1], "observations": int(values[1])}
              for name, values in totals.items() if values[1]}
    slice_result = {}
    labels = sorted({label for _, label in slices})
    for label in labels:
        codex = slices[("codex", label)]
        hybrid = slices[("hybrid", label)]
        if codex[1] and hybrid[1]:
            cb, hb = codex[0] / codex[1], hybrid[0] / hybrid[1]
            slice_result[label] = {"observations": int(hybrid[1]), "codex_brier": cb, "hybrid_brier": hb,
                                   "improvement": (cb - hb) / cb if cb else 0}
    return {"models": result, "slices": slice_result}


def main() -> int:
    parser = argparse.ArgumentParser(description="Compare corrected Codex metrics with the contextual corpus model")
    parser.add_argument("report", type=Path)
    parser.add_argument("metrics", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    rows = report["choice_contexts"]
    corrected = load_scores(args.metrics, corrected=True)
    overwritten = load_scores(args.metrics, corrected=False)
    codex = isotonic_calibrator(rows, corrected)
    bad_codex = isotonic_calibrator(rows, overwritten)

    def context(row: dict[str, Any]) -> float:
        return float(row["pick_rate"])

    def codex_value(row: dict[str, Any]) -> float | None:
        return codex(row["card"])

    def bad_codex_value(row: dict[str, Any]) -> float | None:
        return bad_codex(row["card"])

    candidates = []
    for step in range(21):
        weight = step / 20
        def hybrid(row: dict[str, Any], weight: float = weight) -> float | None:
            official = codex_value(row)
            return None if official is None else official * (1 - weight) + context(row) * weight
        result = evaluate(rows, "validation", {"hybrid": hybrid})["models"]["hybrid"]
        candidates.append((result["brier"], weight, result["observations"]))
    _, best_weight, _ = min(candidates)

    def hybrid(row: dict[str, Any]) -> float | None:
        official = codex_value(row)
        return None if official is None else official * (1 - best_weight) + context(row) * best_weight

    predictors = {"overwritten_codex": bad_codex_value, "codex": codex_value, "context": context, "hybrid": hybrid}
    validation = evaluate(rows, "validation", predictors)
    test = evaluate(rows, "test", predictors)
    payload = {"schema": 1, "best_context_weight": best_weight, "validation_candidates": candidates,
               "validation": validation, "test": test, "corrected_metric_cards": len(corrected),
               "overwritten_metric_cards": len(overwritten)}
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")

    lines = ["# A/B: Spire Codex и контекстная модель", "",
             f"Оптимальный вес контекста выбран только на validation: **{best_weight:.0%}**.", "",
             "| Выборка | Модель | Brier | Наблюдения |", "|---|---|---:|---:|"]
    for split_name, result in (("validation", validation), ("test", test)):
        for name, item in result["models"].items():
            lines.append(f"| {split_name} | {name} | {item['brier']:.6f} | {item['observations']:,} |")
    codex_test = test["models"]["codex"]["brier"]
    hybrid_test = test["models"]["hybrid"]["brier"]
    lines += ["", f"На test гибрид изменяет Brier относительно исправленного Codex на **{(codex_test - hybrid_test) / codex_test:+.2%}**.", "",
              "## Срезы test", "", "| Срез | Наблюдения | Codex | Гибрид | Изменение |", "|---|---:|---:|---:|---:|"]
    for label, item in sorted(test["slices"].items()):
        lines.append(f"| {label} | {item['observations']:,} | {item['codex_brier']:.6f} | {item['hybrid_brier']:.6f} | {item['improvement']:+.2%} |")
    lines += ["", "`overwritten_codex` воспроизводит старую ошибку, где улучшенная строка перезаписывала базовую."]
    args.output.with_suffix(".md").write_text("\n".join(lines), encoding="utf-8")
    print(json.dumps({"weight": best_weight, "validation": validation["models"], "test": test["models"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
