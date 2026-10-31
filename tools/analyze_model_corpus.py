from __future__ import annotations

import argparse
import collections
import datetime as dt
import hashlib
import json
import math
import os
import sqlite3
import statistics
import sys
import time
from pathlib import Path
from typing import Any, Iterable


CHARACTERS = ("IRONCLAD", "SILENT", "DEFECT", "REGENT", "NECROBINDER")


def clean(value: Any) -> str:
    return str(value or "").rsplit(".", 1)[-1].upper()


def percentile(values: list[int], q: float) -> int:
    if not values:
        return 0
    ordered = sorted(values)
    return ordered[round((len(ordered) - 1) * q)]


def split_for(group_hash: str | None, run_hash: str) -> str:
    # All runs belonging to a known player stay in the same split. Anonymous
    # submissions fall back to the run hash and are reported separately.
    token = group_hash or run_hash
    bucket = int(hashlib.sha256(token.encode("utf-8")).hexdigest()[:8], 16) % 10
    return "test" if bucket == 0 else "validation" if bucket == 1 else "train"


def online_backup(source: Path, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    temporary.unlink(missing_ok=True)
    src = sqlite3.connect(f"file:{source.as_posix()}?mode=ro", uri=True, timeout=60)
    dst = sqlite3.connect(temporary)
    try:
        src.execute("PRAGMA busy_timeout=60000")
        src.backup(dst, pages=4096, sleep=0.05)
        dst.commit()
    finally:
        dst.close()
        src.close()
    # Windows virus scanners can briefly retain the just-created database handle.
    # Retrying os.replace keeps the snapshot atomic without touching the live corpus.
    for attempt in range(20):
        try:
            os.replace(temporary, destination)
            break
        except PermissionError:
            if attempt == 19:
                raise
            time.sleep(0.1)


def run_path(root: Path, character: str, run_hash: str) -> Path:
    return root / "runs" / character.lower() / f"{run_hash}.run"


def iter_points(run: dict[str, Any]) -> Iterable[tuple[int, int, dict[str, Any]]]:
    floor = 0
    for act_index, act in enumerate(run.get("map_point_history") or [], start=1):
        for point in act or []:
            floor += 1
            yield act_index, floor, point


def historical_deck(run: dict[str, Any]) -> list[tuple[str, int, int | None]]:
    players = run.get("players") or []
    if not players:
        return []
    cards: list[tuple[str, int, int | None]] = []
    for card in players[0].get("deck") or []:
        card_id = clean(card.get("id"))
        if card_id:
            cards.append((card_id, max(1, int(card.get("floor_added_to_deck") or 1)), None))
    floor = 0
    for act in run.get("map_point_history") or []:
        for point in act or []:
            floor += 1
            for stats in point.get("player_stats") or []:
                for card in stats.get("cards_removed") or []:
                    card_id = clean(card.get("id"))
                    if card_id:
                        cards.append((card_id, max(1, int(card.get("floor_added_to_deck") or 1)), floor))
    return cards


def deck_at(cards: list[tuple[str, int, int | None]], floor: int) -> list[str]:
    # Decisions are recorded on the same floor on which the selected card is
    # added. Excluding same-floor additions is essential: otherwise
    # copies_before reveals which offered card was picked (target leakage).
    # Floor-one cards are the starting deck and must remain visible.
    return [card_id for card_id, added, removed in cards
            if (added == 1 or added < floor) and (removed is None or removed >= floor)]


def add_counter(target: collections.Counter, key: tuple[Any, ...], amount: int = 1) -> None:
    target[key] += amount


def wilson_shrunk_rate(successes: int, total: int, prior: float, strength: float = 24.0) -> float:
    return (successes + prior * strength) / (total + strength) if total >= 0 else prior


def main() -> int:
    parser = argparse.ArgumentParser(description="Snapshot and audit the Build Advisor Spire Codex corpus")
    parser.add_argument("corpus", type=Path, help="Folder containing corpus.sqlite3 and runs/")
    parser.add_argument("output", type=Path, help="Destination folder for the immutable analysis snapshot")
    parser.add_argument("--snapshot-only", action="store_true")
    parser.add_argument("--limit", type=int, default=0, help="Development-only limit; 0 scans all completed runs")
    parser.add_argument("--progress", type=int, default=1000)
    args = parser.parse_args()

    source_db = args.corpus / "corpus.sqlite3"
    snapshot_db = args.output / "corpus.snapshot.sqlite3"
    if not source_db.exists():
        parser.error(f"missing {source_db}")
    started = time.monotonic()
    print(f"Creating a consistent SQLite backup: {snapshot_db}", flush=True)
    online_backup(source_db, snapshot_db)

    with sqlite3.connect(snapshot_db) as db:
        db.row_factory = sqlite3.Row
        columns = {row[1] for row in db.execute("PRAGMA table_info(candidates)")}
        required = {"run_hash", "character", "status", "win", "group_hash", "submitted_at", "build_id"}
        if not required.issubset(columns):
            raise RuntimeError(f"unsupported candidates schema; missing {sorted(required - columns)}")
        query = """SELECT run_hash, character, submitted_at, win, ascension, build_id, group_hash,
                          decisions, encounters, bytes
                   FROM candidates WHERE status='done' ORDER BY submitted_at DESC, run_hash"""
        if args.limit > 0:
            query += f" LIMIT {int(args.limit)}"
        rows = list(db.execute(query))
    print(f"Snapshot contains {len(rows):,} completed runs", flush=True)
    if args.snapshot_only:
        return 0

    runs_by_character = collections.Counter()
    wins_by_character = collections.Counter()
    abandoned_by_character = collections.Counter()
    split_counts = collections.Counter()
    anonymous_groups = 0
    corrupt = 0
    missing = 0
    decision_count = 0
    skip_count = collections.Counter()
    deck_hist: dict[tuple[str, int, str, bool], list[int]] = collections.defaultdict(list)
    # char, card, act, deck-band, copies-before, split -> offers/picks/win offers/win picks
    choice = collections.Counter()
    pairwise = collections.Counter()
    # char, card, copy-band, act, split -> runs/wins
    duplicate_runs = collections.Counter()
    build_counts = collections.Counter()
    dates: list[str] = []

    for index, row in enumerate(rows, start=1):
        character = clean(row["character"])
        path = run_path(args.corpus, character, row["run_hash"])
        if not path.exists():
            missing += 1
            continue
        try:
            run = json.loads(path.read_text(encoding="utf-8"))
            players = run.get("players") or []
            if not players:
                corrupt += 1
                continue
            actual_character = clean(players[0].get("character")) or character
            win = bool(run.get("win"))
            abandoned = bool(run.get("was_abandoned"))
            split = split_for(row["group_hash"], row["run_hash"])
            if not row["group_hash"]:
                anonymous_groups += 1
            runs_by_character[actual_character] += 1
            wins_by_character[actual_character] += int(win)
            abandoned_by_character[actual_character] += int(abandoned)
            split_counts[split] += 1
            build_counts[str(run.get("build_id") or row["build_id"] or "unknown")] += 1
            if row["submitted_at"]:
                dates.append(str(row["submitted_at"]))

            history = historical_deck(run)
            max_floor = sum(len(act or []) for act in run.get("map_point_history") or [])
            if win and not abandoned:
                for floor in range(1, max_floor + 1):
                    deck_hist[(actual_character, floor, split, win)].append(len(deck_at(history, floor)))

            acquired = [clean(card.get("id")) for card in players[0].get("deck") or []
                        if int(card.get("floor_added_to_deck") or 0) > 1 and clean(card.get("id"))]
            for card_id, copies in collections.Counter(acquired).items():
                copy_band = min(copies, 4)
                last_floor = max((int(card.get("floor_added_to_deck") or 1)
                                  for card in players[0].get("deck") or [] if clean(card.get("id")) == card_id), default=1)
                act = 1 if last_floor <= 16 else 2 if last_floor <= 33 else 3
                add_counter(duplicate_runs, (actual_character, card_id, copy_band, act, split, "runs"))
                add_counter(duplicate_runs, (actual_character, card_id, copy_band, act, split, "wins"), int(win))

            for act, floor, point in iter_points(run):
                if str(point.get("map_point_type") or "").lower() == "shop":
                    continue
                before = deck_at(history, floor)
                deck_size = len(before)
                deck_band = min(5, deck_size // 8)
                copies = collections.Counter(before)
                for stats in point.get("player_stats") or []:
                    choices = stats.get("card_choices") or []
                    offered = [clean((item.get("card") or {}).get("id")) for item in choices]
                    offered = [card_id for card_id in offered if card_id]
                    if len(offered) < 2:
                        continue
                    decision_count += 1
                    chosen = next((clean((item.get("card") or {}).get("id")) for item in choices if item.get("was_picked")), "")
                    if not chosen:
                        skip_count[(actual_character, act, split)] += 1
                    for card_id in offered:
                        copy_before = min(copies[card_id], 3)
                        base = (actual_character, card_id, act, deck_band, copy_before, split)
                        add_counter(choice, base + ("offers",))
                        add_counter(choice, base + ("picks",), int(card_id == chosen))
                        add_counter(choice, base + ("win_offers",), int(win))
                        add_counter(choice, base + ("win_picks",), int(win and card_id == chosen))
                    if chosen:
                        for rejected in offered:
                            if rejected != chosen:
                                pairwise[(actual_character, act, chosen, rejected, split)] += 1
        except (OSError, ValueError, TypeError, json.JSONDecodeError):
            corrupt += 1
        if args.progress and index % args.progress == 0:
            elapsed = max(0.1, time.monotonic() - started)
            print(f"Scanned {index:,}/{len(rows):,} runs; {index / elapsed:,.0f} runs/s; decisions={decision_count:,}", flush=True)

    # Train-only baselines and context estimates. Validation/test never influence these values.
    global_train: dict[tuple[str, int], list[int]] = collections.defaultdict(lambda: [0, 0])
    for key, value in choice.items():
        character, _card, act, _deck_band, _copy_before, split, metric = key
        if split == "train" and metric in {"offers", "picks"}:
            global_train[(character, act)][0 if metric == "picks" else 1] += value

    card_rows: list[dict[str, Any]] = []
    grouped_keys = sorted({key[:5] for key in choice})
    for character, card, act, deck_band, copy_before in grouped_keys:
        record: dict[str, Any] = {"character": character.lower(), "card": card, "act": act,
                                  "deck_band": deck_band, "copies_before": copy_before}
        for split in ("train", "validation", "test"):
            offers = choice[(character, card, act, deck_band, copy_before, split, "offers")]
            picks = choice[(character, card, act, deck_band, copy_before, split, "picks")]
            win_offers = choice[(character, card, act, deck_band, copy_before, split, "win_offers")]
            win_picks = choice[(character, card, act, deck_band, copy_before, split, "win_picks")]
            record[split] = {"offers": offers, "picks": picks, "win_offers": win_offers, "win_picks": win_picks}
        prior_picks, prior_offers = global_train[(character, act)]
        prior = prior_picks / prior_offers if prior_offers else 1 / 3
        train = record["train"]
        record["pick_rate"] = round(wilson_shrunk_rate(train["picks"], train["offers"], prior), 6)
        # Winning choices are useful evidence, but capped so outcome correlation cannot
        # overwhelm direct within-offer preference.
        win_rate = wilson_shrunk_rate(train["win_picks"], train["win_offers"], prior)
        record["quality"] = round(0.75 * record["pick_rate"] + 0.25 * win_rate, 6)
        card_rows.append(record)

    expected: dict[str, dict[str, int]] = {}
    for character in CHARACTERS:
        curve: dict[str, int] = {}
        floors = sorted({floor for (who, floor, split, win) in deck_hist if who == character and win})
        for floor in floors:
            values: list[int] = []
            for split in ("train",):
                values.extend(deck_hist[(character, floor, split, True)])
            if len(values) >= 25:
                curve[str(floor)] = round(statistics.median(values))
        expected[character.lower()] = curve

    duplicate_rows: list[dict[str, Any]] = []
    duplicate_keys = sorted({key[:4] for key in duplicate_runs})
    for character, card, copies, act in duplicate_keys:
        item = {"character": character.lower(), "card": card, "copies": copies, "act": act}
        for split in ("train", "validation", "test"):
            total = duplicate_runs[(character, card, copies, act, split, "runs")]
            wins = duplicate_runs[(character, card, copies, act, split, "wins")]
            item[split] = {"runs": total, "wins": wins}
        if item["train"]["runs"] >= 10:
            duplicate_rows.append(item)

    summary_characters = {}
    for character in CHARACTERS:
        count = runs_by_character[character]
        summary_characters[character.lower()] = {
            "runs": count,
            "wins": wins_by_character[character],
            "win_rate": round(wins_by_character[character] / count, 6) if count else 0,
            "abandoned": abandoned_by_character[character],
        }

    report = {
        "schema": 2,
        "generated_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "source_snapshot": str(snapshot_db),
        "runs": sum(runs_by_character.values()),
        "decisions": decision_count,
        "missing_files": missing,
        "corrupt_files": corrupt,
        "anonymous_group_runs": anonymous_groups,
        "splits": dict(split_counts),
        "date_min": min(dates) if dates else None,
        "date_max": max(dates) if dates else None,
        "characters": summary_characters,
        "builds": dict(build_counts.most_common()),
        "expected_deck_by_floor": expected,
        "choice_contexts": card_rows,
        "duplicates": duplicate_rows,
    }
    args.output.mkdir(parents=True, exist_ok=True)
    json_path = args.output / "model-corpus-report.json"
    json_path.write_text(json.dumps(report, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")

    lines = [
        "# Аудит корпуса Build Advisor",
        "",
        f"Снимок: {report['runs']:,} корректных забегов, {decision_count:,} решений по картам.",
        f"Период: {report['date_min'] or 'неизвестно'} — {report['date_max'] or 'неизвестно'}.",
        f"Отсутствующих файлов: {missing}; повреждённых: {corrupt}; без устойчивой группы игрока: {anonymous_groups:,}.",
        "",
        "## Баланс персонажей",
        "",
        "| Персонаж | Забеги | Победы | Доля побед | Брошено |",
        "|---|---:|---:|---:|---:|",
    ]
    for character, item in summary_characters.items():
        lines.append(f"| {character} | {item['runs']:,} | {item['wins']:,} | {item['win_rate']:.1%} | {item['abandoned']:,} |")
    lines += [
        "",
        "## Разделение",
        "",
        *(f"- {name}: {count:,}" for name, count in sorted(split_counts.items())),
        "",
        "## Что можно использовать",
        "",
        "- Медианный размер колоды считается только по победным train-забегам и по состоянию на каждом этаже.",
        "- Ценность выбора считается внутри реального предложения и отдельно по акту, размеру колоды и числу уже имеющихся копий.",
        "- Validation и test сохранены отдельно и не влияют на вычисленные коэффициенты.",
        "- Корреляция с победой ограничена 25% веса; она не трактуется как причинный эффект карты.",
        "",
        "## Ограничения",
        "",
        "- Неизвестные игроки группируются по отдельному забегу; для них исключить утечку стиля игрока невозможно.",
        "- Исторические решения игроков не всегда оптимальны, поэтому модель должна дополнять, а не заменять игровую логику.",
        "- Перед включением в мод нужны метрики на validation/test и регрессии известных контрпримеров.",
    ]
    (args.output / "AUDIT.md").write_text("\n".join(lines), encoding="utf-8")
    elapsed = time.monotonic() - started
    print(json.dumps({"runs": report["runs"], "decisions": decision_count, "contexts": len(card_rows),
                      "duplicates": len(duplicate_rows), "seconds": round(elapsed, 1), "output": str(args.output)},
                     ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
