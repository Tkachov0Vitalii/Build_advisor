"""H3: fit the weights of Build Advisor's hand-set deck adjustments on the run corpus.

Input: the JSON lines written by `OfflineAdvisorEvaluation --features` (one card reward per line,
every offered card with its community baseline `c`, raw adjustments `r` and the player's pick).

Model: the mod's own score with one multiplier per adjustment key,
    S = fixed + sum_k w_k * raw_k
    score = clip(c + (S * max(0, 100 - c) / 70 if S > 0 else S), 0, 100)
    final = clip(score + w_similar * similar, 0, 100)
and a conditional logit over the offered cards: P(pick j) = softmax(final_j / temperature).

The weights are fitted on picks made in WON runs of the train split (what winning players
chose), with an L2 pull towards the hand values (w = 1) whose strength is chosen on the
validation split. Test numbers are reported once, for the selected model only.

usage: calibrate_score_weights.py features.jsonl output.data [--report report.json]
"""
from __future__ import annotations

import argparse
import datetime as dt
import json
import sys

import numpy as np
from scipy.optimize import minimize

KEYS = [
    "нужна защита", "быстрый урон", "усиление к поздней игре", "нужен добор", "даёт энергию",
    "дорогая при нехватке энергии", "сложность разыгрывания", "подготовка к боссу", "синергия архетипа",
    "улучшенная версия", "копия", "размер колоды", "ценность в акте", "несколько структурных минусов",
]
SIMILAR = "похожие ситуации"
ALL = KEYS + [SIMILAR]
LOW, HIGH = 0.0, 3.0


class Data:
    WIDTH = 4

    @staticmethod
    def load(path: str) -> tuple["Data", np.ndarray]:
        # Streams the features file into preallocated float32 arrays (the file is ~1 GB of
        # JSON; loading it whole as Python objects ran out of memory).
        with open(path, encoding="utf-8") as handle:
            total = sum(1 for _ in handle)
        data = Data.__new__(Data)
        w = Data.WIDTH
        data.mask = np.zeros((total, w), bool)
        data.c = np.zeros((total, w), np.float32)
        data.fixed = np.zeros((total, w), np.float32)
        data.raw = np.zeros((len(KEYS), total, w), np.float32)
        data.similar = np.zeros((total, w), np.float32)
        data.pick = np.zeros(total, np.int8)
        data.win = np.zeros(total, np.int8)
        data.act = np.zeros(total, np.int8)
        data.asc = np.zeros(total, np.int8)
        data.floor = np.zeros(total, np.int16)
        data.size = np.zeros(total, np.int16)
        data.run = np.empty(total, object)
        data.character = np.empty(total, object)
        split = np.empty(total, object)
        index = {k: i for i, k in enumerate(KEYS)}
        n = 0
        with open(path, encoding="utf-8") as handle:
            for line in handle:
                r = json.loads(line)
                if r.get("ab") or r["s"] not in ("train", "validation", "test") or len(r["o"]) > w or r["p"] >= len(r["o"]):
                    continue
                for j, o in enumerate(r["o"]):
                    data.mask[n, j] = True
                    data.c[n, j] = o["c"]
                    for key, value in o["r"].items():
                        if key in index:
                            data.raw[index[key], n, j] = value
                        elif key == SIMILAR:
                            data.similar[n, j] = value
                        else:
                            data.fixed[n, j] += value
                data.pick[n] = r["p"]
                data.win[n] = r["w"]
                data.act[n] = r["act"]
                data.asc[n] = min(20, max(0, r.get("a", 0) or 0))
                data.floor[n] = r.get("fl", 0)
                data.size[n] = r.get("n", 0)
                data.run[n] = r.get("h", "")
                data.character[n] = r["ch"]
                split[n] = r["s"]
                n += 1
        data.n = n
        for name in ("mask", "c", "fixed", "similar", "pick", "win", "act", "asc", "floor", "size", "run", "character"):
            setattr(data, name, getattr(data, name)[:n])
        data.raw = data.raw[:, :n]
        return data, split[:n]

    def __init__(self, rows: list[dict]):
        # Small in-memory construction (tests); the tool itself uses Data.load.
        width = max(len(r["o"]) for r in rows)
        n = len(rows)
        self.n = n
        self.mask = np.zeros((n, width), bool)
        self.c = np.zeros((n, width))
        self.fixed = np.zeros((n, width))
        self.raw = np.zeros((len(KEYS), n, width))
        self.similar = np.zeros((n, width))
        self.pick = np.array([r["p"] for r in rows])
        self.win = np.array([r["w"] for r in rows])
        self.act = np.array([r["act"] for r in rows])
        self.character = np.array([r["ch"] for r in rows])
        index = {k: i for i, k in enumerate(KEYS)}
        for i, r in enumerate(rows):
            for j, o in enumerate(r["o"]):
                self.mask[i, j] = True
                self.c[i, j] = o["c"]
                for key, value in o["r"].items():
                    if key in index:
                        self.raw[index[key], i, j] = value
                    elif key == SIMILAR:
                        self.similar[i, j] = value
                    else:
                        self.fixed[i, j] += value

    def scores(self, w: np.ndarray) -> np.ndarray:
        s = self.fixed + np.tensordot(w[: len(KEYS)].astype(np.float32), self.raw, axes=1)
        damped = np.where(s > 0, s * np.maximum(0.0, 100.0 - self.c) / 70.0, s)
        score = np.clip(self.c + damped, 0.0, 100.0)
        return np.clip(score + w[len(KEYS)] * self.similar, 0.0, 100.0)

    def nll(self, w: np.ndarray, temperature: float) -> float:
        z = self.scores(w) / temperature
        z = np.where(self.mask, z, -np.inf)
        z = z - z.max(axis=1, keepdims=True)
        log_norm = np.log(np.exp(z).sum(axis=1))
        return float(-(z[np.arange(self.n), self.pick] - log_norm).mean())

    def top1(self, w: np.ndarray) -> float:
        s = np.where(self.mask, self.scores(w), -np.inf)
        return float((s.argmax(axis=1) == self.pick).mean())

    def subset(self, keep: np.ndarray) -> "Data":
        other = Data.__new__(Data)
        other.n = int(keep.sum())
        for name in ("mask", "c", "fixed", "similar", "pick", "win", "act", "character", "asc", "floor", "size", "run"):
            if hasattr(self, name):
                setattr(other, name, getattr(self, name)[keep])
        other.raw = self.raw[:, keep]
        return other


def fit(train: Data, l2: float, start: np.ndarray | None = None) -> tuple[np.ndarray, float]:
    # Parameters: the weights, then log(temperature).
    x0 = np.concatenate([start if start is not None else np.ones(len(ALL)), [np.log(10.0)]])

    def objective(x: np.ndarray) -> float:
        w, temperature = x[:-1], float(np.exp(x[-1]))
        return train.nll(w, temperature) + l2 * float(((w - 1.0) ** 2).sum())

    bounds = [(LOW, HIGH)] * len(ALL) + [(np.log(1.0), np.log(60.0))]
    result = minimize(objective, x0, method="L-BFGS-B", bounds=bounds, options={"maxiter": 300, "eps": 1e-4})
    return result.x[:-1], float(np.exp(result.x[-1]))


def temperature_only(data: Data, w: np.ndarray) -> float:
    result = minimize(lambda t: data.nll(w, float(np.exp(t[0]))), [np.log(10.0)], method="L-BFGS-B",
                      bounds=[(np.log(1.0), np.log(60.0))])
    return float(np.exp(result.x[0]))


def evaluate(data: Data, w: np.ndarray, temperature: float) -> dict:
    return {"decisions": data.n, "nll": round(data.nll(w, temperature), 5), "top1": round(data.top1(w), 4)}


def outcome_check(data: Data, hand: np.ndarray, fitted: np.ndarray) -> dict:
    # Decisions where the two models disagree: did runs that took the fitted pick win more often
    # than runs that took the hand-weight pick? (Observational; reported, never optimised.)
    a = np.where(data.mask, data.scores(hand), -np.inf).argmax(axis=1)
    b = np.where(data.mask, data.scores(fitted), -np.inf).argmax(axis=1)
    differ = a != b
    took_fitted = differ & (data.pick == b)
    took_hand = differ & (data.pick == a)
    rate = lambda m: round(float(data.win[m].mean()), 4) if m.any() else None
    return {"disagreements": int(differ.sum()), "share": round(float(differ.mean()), 4),
            "took_fitted": int(took_fitted.sum()), "win_rate_fitted": rate(took_fitted),
            "took_hand": int(took_hand.sum()), "win_rate_hand": rate(took_hand)}


def outcome_design(data: Data) -> tuple[np.ndarray, list[str]]:
    # Picked card minus the mean of the offered cards, per feature, plus run-state controls.
    rows = np.arange(data.n)
    count = data.mask.sum(axis=1)

    def delta(values: np.ndarray) -> np.ndarray:
        chosen = values[rows, data.pick]
        return chosen - np.where(data.mask, values, 0).sum(axis=1) / count

    columns = [delta(data.c)] + [delta(data.raw[k]) for k in range(len(KEYS))] + [delta(data.similar)]
    names = ["community"] + KEYS + [SIMILAR]
    controls = []
    for act in (2, 3):
        controls.append((data.act == act).astype(np.float32))
    for character in ("silent", "defect", "regent", "necrobinder"):
        controls.append((data.character == character).astype(np.float32))
    controls += [data.asc / 10.0, data.floor / 50.0, data.size / 30.0]
    return np.column_stack(columns + controls).astype(np.float64), names


def outcome_fit(train: Data, seed: int = 11, rounds: int = 12) -> dict:
    # Does picking a card with more of adjustment k (than its alternatives) go with winning the
    # run? Logistic regression of the run result on the deltas; a weight is the effect of one
    # adjustment point relative to one point of community score (the scale the mod is built on).
    from sklearn.linear_model import LogisticRegression
    x, names = outcome_design(train)
    y = train.win.astype(int)
    scale = x.std(axis=0)
    scale[scale == 0] = 1.0

    def ratios(index: np.ndarray) -> np.ndarray:
        model = LogisticRegression(C=1.0, max_iter=400)
        model.fit(x[index] / scale, y[index])
        beta = model.coef_[0] / scale
        return beta[1:len(names)] / beta[0]

    point = ratios(np.arange(train.n))
    runs = np.unique(train.run)
    by_run = {r: i for i, r in enumerate(runs)}
    run_index = np.array([by_run[r] for r in train.run])
    rng = np.random.default_rng(seed)
    samples = []
    for _ in range(rounds):
        pick = rng.integers(0, len(runs), len(runs))
        weight = np.bincount(pick, minlength=len(runs))
        index = np.repeat(np.arange(train.n), weight[run_index])
        samples.append(ratios(index))
    se = np.array(samples).std(axis=0)
    return {"names": names[1:], "ratio": point, "se": se}


RELEASE_LOW, RELEASE_HIGH = 0.5, 2.0


def outcome_weights(fit: dict) -> np.ndarray:
    # Move a weight away from the hand value only as far as the evidence allows (positive-part
    # shrinkage by twice the bootstrap error: a ratio within 2 SE of 1 keeps the hand value), and at most to
    # half or double the hand value in one calibration: these are observational associations.
    ratio, se = fit["ratio"], fit["se"]
    gap = ratio - 1.0
    reliability = np.clip(1.0 - (2.0 * se) ** 2 / np.maximum(gap ** 2, 1e-12), 0.0, 1.0)
    weights = np.clip(1.0 + gap * reliability, RELEASE_LOW, RELEASE_HIGH)
    weights[se == 0] = 1.0  # never observed in the corpus (e.g. boss preparation needs a boss)
    return weights


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("features")
    parser.add_argument("output")
    parser.add_argument("--report")
    parser.add_argument("--max-train", type=int, default=400000)
    parser.add_argument("--method", choices=("imitation", "outcome"), default="outcome")
    args = parser.parse_args()
    data, split = Data.load(args.features)
    won = data.win == 1
    train = data.subset((split == "train") & won)
    if train.n > args.max_train:
        rng = np.random.default_rng(7)
        keep = np.zeros(train.n, bool)
        keep[rng.choice(train.n, args.max_train, replace=False)] = True
        train = train.subset(keep)
    valid_won = data.subset((split == "validation") & won)
    valid_all = data.subset(split == "validation")
    test_won = data.subset((split == "test") & won)
    test_all = data.subset(split == "test")
    print(f"decisions: train(won)={train.n} validation(won)={valid_won.n} test(won)={test_won.n}", file=sys.stderr)

    hand = np.ones(len(ALL))
    hand_t = temperature_only(train, hand)
    candidates = []
    previous = None
    for l2 in (0.1, 0.03, 0.01, 0.003, 0.001, 0.0):
        w, t = fit(train, l2, previous)
        previous = w
        score = valid_won.nll(w, t)
        candidates.append({"l2": l2, "weights": w, "temperature": t, "validation_nll": score})
        print(f"l2={l2}: validation nll {score:.5f} top1 {valid_won.top1(w):.4f}", file=sys.stderr)
    best = min(candidates, key=lambda x: x["validation_nll"])
    w, t = best["weights"], best["temperature"]
    report = {
        "hand": {"temperature": round(hand_t, 3), "train": evaluate(train, hand, hand_t),
                 "validation_won": evaluate(valid_won, hand, hand_t), "validation_all": evaluate(valid_all, hand, hand_t),
                 "test_won": evaluate(test_won, hand, hand_t), "test_all": evaluate(test_all, hand, hand_t)},
        "fitted": {"l2": best["l2"], "temperature": round(t, 3), "train": evaluate(train, w, t),
                   "validation_won": evaluate(valid_won, w, t), "validation_all": evaluate(valid_all, w, t),
                   "test_won": evaluate(test_won, w, t), "test_all": evaluate(test_all, w, t)},
        "outcome_check_test": outcome_check(test_all, hand, w),
        "l2_search": [{"l2": c["l2"], "validation_nll": round(c["validation_nll"], 5)} for c in candidates],
    }
    train_all = data.subset(split == "train")
    print(f"outcome model on {train_all.n} decisions", file=sys.stderr)
    fit_outcome = outcome_fit(train_all if train_all.n <= args.max_train * 2 else train_all.subset(
        np.random.default_rng(3).random(train_all.n) < (args.max_train * 2) / train_all.n))
    w_outcome = outcome_weights(fit_outcome)
    t_outcome = temperature_only(train, w_outcome)
    report["outcome_model"] = {
        "ratios": {k: [round(float(r), 3), round(float(e), 3)] for k, r, e in zip(fit_outcome["names"], fit_outcome["ratio"], fit_outcome["se"])},
        "weights": {k: round(float(v), 2) for k, v in zip(ALL, w_outcome)},
        "validation_won": evaluate(valid_won, w_outcome, t_outcome), "test_won": evaluate(test_won, w_outcome, t_outcome),
        "outcome_check_test": outcome_check(test_all, hand, w_outcome),
        "outcome_check_validation": outcome_check(valid_all, hand, w_outcome),
    }
    report["outcome_check_validation"] = outcome_check(valid_all, hand, w)
    if args.method == "outcome":
        w, t = w_outcome, t_outcome
    weights = {k: round(float(v), 2) for k, v in zip(ALL, w)}
    payload = {
        "contract": "score-weights-v1",
        "created": dt.date.today().isoformat(),
        "source": "Spire Codex public run corpus (solo, 177k runs); method: " + args.method,
        "decisions": {"train": train.n, "validation": valid_won.n, "test": test_won.n},
        "weights": weights,
        "metrics": report,
    }
    json.dump(payload, open(args.output, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    if args.report:
        json.dump(report, open(args.report, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
    print(json.dumps({"method": args.method, "weights": weights, "hand": report["hand"]["test_won"], "imitation": report["fitted"]["test_won"],
                      "imitation_outcome": report["outcome_check_test"], "outcome_model": report["outcome_model"]}, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
