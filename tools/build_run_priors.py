from __future__ import annotations

import collections
import json
import statistics
import sys
from datetime import datetime, timezone
from pathlib import Path


def clean(value: object) -> str:
    return str(value or "").rsplit(".", 1)[-1].upper()


def main() -> None:
    source = Path(sys.argv[1]) / "runs"
    destination = Path(sys.argv[2])
    deck_at_floor: dict[str, dict[int, list[int]]] = collections.defaultdict(lambda: collections.defaultdict(list))
    duplicates: dict[tuple[str, str], list[int]] = collections.defaultdict(lambda: [0, 0, 0, 0])
    run_count = 0

    # Downloads made by older tools use .run directly under runs/; the newer
    # Spire Codex downloader stores full run payloads as JSON below SpireRuns/runs.
    # Read both layouts recursively, but ignore manifest/list metadata JSON files.
    paths = list(source.rglob("*.run")) + [
        path for path in source.rglob("*.json")
        if not path.name.startswith("_")
    ]
    for path in paths:
        try:
            run = json.loads(path.read_text(encoding="utf-8"))
            players = run.get("players") or []
            if not players or run.get("was_abandoned"):
                continue
            player = players[0]
            character = clean(player.get("character")).lower()
            deck = player.get("deck") or []
            max_floor = sum(len(act) for act in run.get("map_point_history") or [])
            win = bool(run.get("win"))
            run_count += 1

            # Successful runs are the target curve. Cards are reconstructed at each floor from
            # floor_added_to_deck, avoiding the final-deck/survival-time bias.
            if win:
                additions = [max(1, int(card.get("floor_added_to_deck") or 1)) for card in deck]
                for floor in range(1, max_floor + 1):
                    deck_at_floor[character][floor].append(sum(added <= floor for added in additions))

            acquired = [clean(card.get("id")) for card in deck if int(card.get("floor_added_to_deck") or 0) > 1]
            for card_id, copies in collections.Counter(acquired).items():
                if not card_id:
                    continue
                row = duplicates[(character, card_id)]
                if copies >= 2:
                    row[0] += 1
                    row[1] += int(win)
                else:
                    row[2] += 1
                    row[3] += int(win)
        except Exception:
            continue

    expected = {
        character: {str(floor): round(statistics.median(values)) for floor, values in floors.items() if len(values) >= 5}
        for character, floors in deck_at_floor.items()
    }
    duplicate_rows = [
        {"character": character, "card": card, "duplicate_runs": row[0], "duplicate_wins": row[1], "single_runs": row[2], "single_wins": row[3]}
        for (character, card), row in duplicates.items() if row[0] >= 3 and row[2] >= 3
    ]
    result = {
        "schema": 1,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "runs": run_count,
        "expected_deck_by_floor": expected,
        "duplicates": sorted(duplicate_rows, key=lambda item: (item["character"], item["card"])),
    }
    destination.write_text(json.dumps(result, ensure_ascii=False, separators=(",", ":")), encoding="utf-8")
    print(json.dumps({"runs": run_count, "characters": len(expected), "duplicate_rows": len(duplicate_rows), "bytes": destination.stat().st_size}))


if __name__ == "__main__":
    main()
