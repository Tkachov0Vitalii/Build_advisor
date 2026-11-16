#!/usr/bin/env python3
"""Calibrate the take/skip threshold on the run corpus (7.2, reworked in 7.3.1).

Input: the JSONL written by `OfflineAdvisorEvaluation --take-skip` (every card reward of the
corpus, skipped ones included: each offer's raw win-rate delta `d` = lift + deck adjustments and
its community lift `l`, the learned skip probability `sk`, act, deck size `n`, expected deck size
`ex`), and the lift scale of the statistics the corpus was replayed with
(`OfflineAdvisorEvaluation --lift-centers <metrics dir> <catalog>` -> {"character": [centre, spread]},
taken over the character's own cards).
A `.npz` written by a previous run (`--cache`) can be given instead of the JSONL.

Method
1. Lifts are read relative to the centre of the statistics in use and in units of their spread
   (REFERENCE_SPREAD pp per interquartile range), so the threshold does not depend on the
   bracket: "a10" lifts are about 2 pp lower and a quarter wider than "all" lifts. 7.2 used raw
   lifts fitted on "all" and advised skipping almost always at ascension 10.
2. A high learned skip probability lowers a reward's score by SKIP_WEIGHT pp per 10 points
   above 50 % (unchanged from 7.2).
3. For each act and deck-size bucket the threshold is the quantile of the best card's score at
   the skip rate of winning runs in that bucket (train split). 7.2 used the median delta of the
   picked card and one global margin: the overall skip rate matched, the per-act rates did not.

The report gives, on validation and test, the advised and the winners' skip rate per act and per
ascension group, and the agreement with winners' take-or-skip decisions.
"""
import argparse
import json
import sys

import numpy as np

EXCESS_EDGES = (-4, -1, 2, 5, 9)          # same buckets as the learned skip model
CHARACTERS = ("ironclad", "silent", "defect", "regent", "necrobinder")
REFERENCE_SPREAD = 6.0                    # pp per interquartile range of card lifts ("all" bracket)
SKIP_WEIGHT = 6.0
MIN_ROWS = 2000


def load(path, cache=None):
    if path.endswith(".npz"):
        return dict(np.load(path))
    cols = {k: [] for k in "split win a act xb sk best bestlift skipped ch".split()}
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            try:
                d = json.loads(line)
            except json.JSONDecodeError:
                continue
            if d.get("ab") or len(d["o"]) < 2:
                continue
            deltas = [float(o["d"]) for o in d["o"]]
            best = int(np.argmax(deltas))
            lift = d["o"][best].get("l")
            cols["split"].append({"train": 0, "validation": 1, "test": 2}.get(d["s"], 3))
            cols["win"].append(d["w"])
            cols["a"].append(d["a"])
            cols["act"].append(min(3, max(1, d["act"])))
            cols["xb"].append(sum(d["n"] - d["ex"] > edge for edge in EXCESS_EDGES))
            cols["sk"].append(d["sk"])
            cols["best"].append(deltas[best])
            cols["bestlift"].append(np.nan if lift is None else float(lift))
            cols["skipped"].append(d["p"] < 0)
            name = str(d["ch"]).lower()
            cols["ch"].append(CHARACTERS.index(name) if name in CHARACTERS else len(CHARACTERS))
    data = {k: np.asarray(v) for k, v in cols.items()}
    if cache:
        np.savez_compressed(cache, **data)
    return data


def score(data, scales):
    """Best card: normalised lift + deck adjustments - skip-probability term."""
    centre = np.array([scales[c][0] for c in CHARACTERS] + [np.mean([scales[c][0] for c in CHARACTERS])])[data["ch"]]
    spread = np.array([scales[c][1] for c in CHARACTERS] + [REFERENCE_SPREAD])[data["ch"]]
    lift = data["bestlift"]
    deck = data["best"] - np.where(np.isnan(lift), data["best"], lift)
    normalised = np.where(np.isnan(lift), data["best"] - centre, (lift - centre) * REFERENCE_SPREAD / spread)
    return normalised + deck - SKIP_WEIGHT * np.maximum(0.0, data["sk"] / 100.0 - 0.5) * 10.0


def fit(data, value, train, winners):
    bars = {}
    for act in (1, 2, 3):
        act_rows = train & (data["act"] == act)
        act_rate = float(data["skipped"][act_rows & winners].mean())
        for xb in range(len(EXCESS_EDGES) + 1):
            rows = act_rows & (data["xb"] == xb)
            enough = rows.sum() >= MIN_ROWS and (rows & winners).sum() >= MIN_ROWS // 4
            rate = float(data["skipped"][rows & winners].mean()) if enough else act_rate
            bars[f"{act}:{xb}"] = round(float(np.quantile(value[rows if enough else act_rows], rate)), 2)
    return bars


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("features")
    parser.add_argument("lift_centers")
    parser.add_argument("model_out")
    parser.add_argument("--report", required=True)
    parser.add_argument("--cache")
    args = parser.parse_args()

    data = load(args.features, args.cache)
    scales = json.load(open(args.lift_centers, encoding="utf-8"))
    value = score(data, scales)
    train, validation, test = (data["split"] == i for i in range(3))
    winners = data["win"] == 1
    bars = fit(data, value, train, winners)
    take = value >= np.array([bars[f"{a}:{x}"] for a, x in zip(data["act"], data["xb"])])

    def rates(mask):
        return {"decisions": int(mask.sum()),
                "advised_skip": round(float((~take[mask]).mean()), 4),
                "winners_skip": round(float(data["skipped"][mask & winners].mean()), 4),
                "players_skip": round(float(data["skipped"][mask].mean()), 4),
                "agreement_with_winners": round(float((take[mask & winners] == ~data["skipped"][mask & winners]).mean()), 4)}

    report = {"rows": int(len(data["win"])), "reference_spread": REFERENCE_SPREAD, "skip_weight": SKIP_WEIGHT,
              "lift_scales": scales, "bars": bars, "splits": {}}
    for name, mask in (("train", train), ("validation", validation), ("test", test)):
        entry = rates(mask)
        entry["by_act"] = {str(act): rates(mask & (data["act"] == act)) for act in (1, 2, 3)}
        entry["by_character"] = {name: rates(mask & (data["ch"] == index)) for index, name in enumerate(CHARACTERS)}
        entry["by_ascension"] = {label: rates(mask & (data["a"] >= lo) & (data["a"] <= hi))
                                 for label, lo, hi in (("0-4", 0, 4), ("5-9", 5, 9), ("10+", 10, 99))}
        report["splits"][name] = entry
    json.dump(report, open(args.report, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    json.dump({"contract": "take-skip-v2", "bars": bars, "reference_spread": REFERENCE_SPREAD, "skip_weight": SKIP_WEIGHT,
               "excess_edges": list(EXCESS_EDGES), "source": f"{report['rows']} corpus rewards, fitted on train"},
              open(args.model_out, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
    json.dump(report, sys.stdout, indent=2, ensure_ascii=False)
    print()


if __name__ == "__main__":
    main()
