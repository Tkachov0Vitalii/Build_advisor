from __future__ import annotations

import argparse
import hashlib
import sqlite3
from pathlib import Path


def split_for(group_hash: str | None, run_hash: str) -> str:
    token = group_hash or run_hash
    bucket = int(hashlib.sha256(token.encode("utf-8")).hexdigest()[:8], 16) % 10
    return "test" if bucket == 0 else "validation" if bucket == 1 else "train"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    with sqlite3.connect(args.snapshot) as db:
        rows = db.execute("SELECT run_hash, character, group_hash FROM candidates WHERE status='done'")
        selected = [(split_for(group_hash, run_hash), character, run_hash)
                    for run_hash, character, group_hash in rows
                    if split_for(group_hash, run_hash) != "train"]
    args.output.write_text("".join(f"{split}\t{character}\t{run_hash}\n" for split, character, run_hash in selected), encoding="utf-8")
    print(f"holdout_runs={len(selected)} output={args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
