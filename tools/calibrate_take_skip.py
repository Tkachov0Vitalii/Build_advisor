#!/usr/bin/env python3
"""7.2: calibrate the take/skip verdict on the run corpus.

Input: the JSONL written by `OfflineAdvisorEvaluation --take-skip` (every card reward of the
corpus, skipped ones included, with each offer's win-rate delta `d` as the mod computes it, the
learned skip probability `sk`, act, deck size `n` and the expected deck size `ex`).

The 7.1 verdict compared the best card's delta with 0 (skip). The average community lift is
about +7 pp, so skip almost never won. Here the reference is the delta of the card players
typically take in the same act and deck-size bucket ("typical pick"); the card is advised only
if it beats that reference by a margin, and the learned skip probability can tip close calls.

Parameters are chosen on the train split to match the actions of winning runs; validation and
test check (1) agreement with winners and (2) an outcome comparison where the old and new
verdicts disagree: did runs whose player did what the new verdict says win more often?

Output: a small JSON model (data/take-skip-v1.data) and a report.
"""
import argparse
import json
import sys
from collections import defaultdict

import numpy as np

ACTS = (1, 2, 3)
EXCESS_EDGES = (-4, -1, 2, 5, 9)          # same buckets as the learned skip model


def excess_bucket(n, ex):
    value = n - ex
    return sum(value > edge for edge in EXCESS_EDGES)


def load(path):
    cols = defaultdict(list)
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            try:
                d = json.loads(line)
            except json.JSONDecodeError:   # a line still being written
                continue
            if d.get("ab"):
                continue
            offers = d["o"]
            if len(offers) < 2:
                continue
            deltas = [float(o["d"]) for o in offers]
            best = int(np.argmax(deltas))
            picked = d["p"]
            cols["split"].append({"train": 0, "validation": 1, "test": 2}.get(d["s"], 3))
            cols["win"].append(d["w"])
            cols["act"].append(min(3, max(1, d["act"])))
            cols["xb"].append(excess_bucket(d["n"], d["ex"]))
            cols["sk"].append(d["sk"] / 100.0)
            cols["best_d"].append(deltas[best])
            cols["best_i"].append(best)
            cols["picked"].append(picked)
            cols["picked_d"].append(deltas[picked] if 0 <= picked < len(deltas) else np.nan)
    out = {k: np.asarray(v) for k, v in cols.items()}
    out["skipped"] = out["picked"] < 0
    return out


def typical_pick(data, mask):
    """Median delta of the card players took, per (act, excess bucket); act-level fallback."""
    table = {}
    for act in ACTS:
        act_rows = mask & (data["act"] == act) & ~data["skipped"]
        act_median = float(np.nanmedian(data["picked_d"][act_rows]))
        for xb in range(len(EXCESS_EDGES) + 1):
            rows = act_rows & (data["xb"] == xb)
            table[f"{act}:{xb}"] = round(float(np.nanmedian(data["picked_d"][rows])) if rows.sum() >= 2000 else act_median, 2)
    return table


def reference(data, table):
    return np.array([table[f"{a}:{x}"] for a, x in zip(data["act"], data["xb"])])


_REFERENCE = {}


def verdict(data, table, margin, skip_weight):
    # Take when the best card beats the typical pick by `margin`; each 10 points of learned skip
    # probability above 50% raise the bar by `skip_weight` pp.
    key = id(table)
    if key not in _REFERENCE:
        _REFERENCE[key] = reference(data, table)
    bar = _REFERENCE[key] + margin + skip_weight * np.maximum(0.0, data["sk"] - 0.5) * 10.0
    return data["best_d"] >= bar


def agreement(data, take, mask):
    """Share of decisions where the verdict matches the player's action (skip / the advised card)."""
    rows = mask
    same = np.where(take, ~data["skipped"] & (data["picked"] == data["best_i"]), data["skipped"])
    return float(same[rows].mean()), float((~take[rows]).mean())


def decision_agreement(data, take, mask):
    """Take-or-skip only: does the verdict match whether the player took a card at all."""
    return float((take[mask] == ~data["skipped"][mask]).mean())


def outcome(data, old_take, new_take, mask):
    """Where the verdicts disagree: win rate of runs that followed the new verdict vs the old one."""
    rows = mask & (old_take != new_take)
    followed_new = np.where(new_take, ~data["skipped"], data["skipped"])
    followed_old = np.where(old_take, ~data["skipped"], data["skipped"])
    new_rows = rows & followed_new
    old_rows = rows & followed_old
    return {
        "disagreements": int(rows.sum()),
        "share": round(float(rows.sum() / max(1, mask.sum())), 4),
        "followed_new": int(new_rows.sum()), "win_new": round(float(data["win"][new_rows].mean()), 4) if new_rows.any() else None,
        "followed_old": int(old_rows.sum()), "win_old": round(float(data["win"][old_rows].mean()), 4) if old_rows.any() else None,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("features")
    parser.add_argument("model_out")
    parser.add_argument("--report", required=True)
    args = parser.parse_args()

    data = load(args.features)
    train, validation, test = (data["split"] == i for i in range(3))
    winners = data["win"] == 1
    old_take = data["best_d"] >= 0.5          # 7.1 rule (TakeMargin 0.5 pp)

    table = typical_pick(data, train)
    # Constraint: advise skipping about as often as winning runs skip (± 3 pp on train); among
    # those rules, maximise agreement with winners' take-or-skip decisions. Without the constraint
    # the fit drifts to "follow the learned skip model only" (imitation, which H3 found does not
    # improve wins) or to skipping half of all rewards.
    target = float(data["skipped"][train & winners].mean())
    best = None
    for margin in np.arange(-8.0, 6.01, 0.25):
        for skip_weight in (0.0, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0):
            take = verdict(data, table, margin, skip_weight)
            skip_rate = float((~take[train]).mean())
            if abs(skip_rate - target) > 0.03:
                continue
            score = decision_agreement(data, take, train & winners)
            if best is None or score > best[0]:
                best = (score, float(margin), skip_weight, skip_rate)
    _, margin, skip_weight, _ = best
    new_take = verdict(data, table, margin, skip_weight)

    report = {"rows": int(len(data["win"])), "typical_pick": table, "margin": margin, "skip_weight": skip_weight, "splits": {}}
    for name, mask in (("train", train), ("validation", validation), ("test", test)):
        old_agree, old_skip = agreement(data, old_take, mask & winners)
        new_agree, new_skip = agreement(data, new_take, mask & winners)
        report["splits"][name] = {
            "decisions": int(mask.sum()),
            "player_skip_rate": round(float(data["skipped"][mask].mean()), 4),
            "winner_skip_rate": round(float(data["skipped"][mask & winners].mean()), 4),
            "advised_skip_rate_old": round(float((~old_take[mask]).mean()), 4),
            "advised_skip_rate_new": round(float((~new_take[mask]).mean()), 4),
            "winner_agreement_old": round(old_agree, 4), "winner_agreement_new": round(new_agree, 4),
            "winner_decision_agreement_old": round(decision_agreement(data, old_take, mask & winners), 4),
            "winner_decision_agreement_new": round(decision_agreement(data, new_take, mask & winners), 4),
            "outcome_where_verdicts_differ": outcome(data, old_take, new_take, mask),
        }
    json.dump(report, open(args.report, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    model = {"contract": "take-skip-v1", "typical_pick": table, "margin": margin, "skip_weight": skip_weight,
             "excess_edges": list(EXCESS_EDGES), "source": f"{report['rows']} corpus rewards, fitted on train"}
    json.dump(model, open(args.model_out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    json.dump(report, sys.stdout, indent=2, ensure_ascii=False)
    print()


if __name__ == "__main__":
    main()
