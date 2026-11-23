#!/usr/bin/env python3
"""7.5: build the compact corpus extract for one or more game versions from the Spire Codex export.

After a balance patch the bundled models (take/skip bars, Ancient relic values, relic synergies)
must be refitted on runs of the new version only. Spire Codex exports raw runs per game version:

    GET https://spire-codex.com/api/exports/runs?version=v0.111.1&limit=N[&cursor=...]
    -> gzipped JSONL of the raw game run JSON; the next page's cursor is in X-Next-Cursor.

Each run is converted on the fly into the extract format the measurement tools read
(the same fields as the owner's extract.py over the local corpus):

    h run hash, c character, a ascension, w win, ab abandoned, b build_id,
    deck [[card, floor added, upgrade level]], rel [[relic, floor added]],
    rm [[card, floor added, floor removed]], up [[card, floor]],
    pts [[floor, act, map point type, hp, max hp, gold, offered cards, offered upgrade levels, picked index]]

Only solo standard runs of the official characters are kept (the corpus sampling rule).
Output: <out>/part-NNNNNN.jsonl.gz (one per export page) and <out>/splits.tsv
(hash -> train/validation/test, 70/15/15 by a stable hash of the run hash), plus state.json
so an interrupted download resumes from the last cursor.

Usage:
    python3 tools/build_version_extract.py <out dir> v0.111.1 [v0.111.0 ...] [--max-runs 150000]
        (static per-version daily file, streamed; the default since 7.5)
    python3 tools/build_version_extract.py <out dir> v0.111.1@2026-11-16 [--page 1000] [--start <date>]
        (paged API from a date; times out when the version is rare after that date)
"""
import argparse
import gzip
import hashlib
import io
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = 'https://spire-codex.com/api/exports/runs'
CHARACTERS = {'IRONCLAD', 'SILENT', 'DEFECT', 'REGENT', 'NECROBINDER'}


def clean(value):
    return str(value or '').rsplit('.', 1)[-1].upper()


def convert(run):
    """Raw run JSON -> extract row, or None when the run is not a solo standard run."""
    players = run.get('players') or []
    if len(players) != 1 or (run.get('game_mode') or 'standard') != 'standard':
        return None
    player = players[0]
    character = clean(player.get('character'))
    if character not in CHARACTERS:
        return None
    row = {'h': run.get('run_hash') or hashlib.sha1(json.dumps(run, sort_keys=True).encode()).hexdigest()[:16],
           'c': character, 'a': run.get('ascension', 0), 'w': 1 if run.get('win') else 0,
           'ab': 1 if run.get('was_abandoned') else 0, 'b': run.get('build_id', ''),
           'deck': [[clean(x.get('id')), x.get('floor_added_to_deck', 1), x.get('current_upgrade_level', 0)] for x in player.get('deck') or []],
           'rel': [[clean(x.get('id')), x.get('floor_added_to_deck', 1)] for x in player.get('relics') or []],
           'rm': [], 'up': [], 'pts': []}
    floor = 0
    for act, points in enumerate(run.get('map_point_history') or [], 1):
        for point in points or []:
            floor += 1
            for stats in point.get('player_stats') or []:
                for card in stats.get('cards_removed') or []:
                    row['rm'].append([clean(card.get('id')), card.get('floor_added_to_deck', 1), floor])
                for card in stats.get('upgraded_cards') or []:
                    row['up'].append([clean(card), floor])
                choices = stats.get('card_choices') or []
                if len(choices) >= 2:
                    offered = [clean((x.get('card') or {}).get('id')) for x in choices]
                    ups = [(x.get('card') or {}).get('current_upgrade_level', 0) for x in choices]
                    picked = next((i for i, x in enumerate(choices) if x.get('was_picked')), -1)
                    row['pts'].append([floor, act, point.get('map_point_type', ''), stats.get('current_hp', 0),
                                       stats.get('max_hp', 0), stats.get('current_gold', 0), offered, ups, picked])
    return row


def split_of(run_hash):
    bucket = int(hashlib.sha256(run_hash.encode()).hexdigest()[:8], 16) % 100
    return 'train' if bucket < 70 else 'validation' if bucket < 85 else 'test'


def fetch(version, limit, cursor, start=None, attempts=5):
    """One export page; the gateway times out on big pages (20,000 runs gave 504), so retry with backoff."""
    # Without `start` the server scans the whole corpus for an older version (even 50 runs of
    # v0.111.0 timed out after 60 s); with the version's release date it answers in seconds.
    url = f'{API}?version={version}&limit={limit}' + (f'&cursor={cursor}' if cursor else f'&start={start}' if start else '')
    for attempt in range(attempts):
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'build-advisor-tools'})
            with urllib.request.urlopen(request, timeout=600) as response:
                return response.read(), response.headers.get('X-Next-Cursor')
        except (urllib.error.HTTPError, urllib.error.URLError, TimeoutError) as error:
            if attempt == attempts - 1:
                raise
            print(f'{version}: {error}; retry in {10 * (attempt + 1)}s', file=sys.stderr)
            time.sleep(10 * (attempt + 1))


def existing_hashes(out_dir):
    """Run hashes already in the extract dir (e.g. an older corpus merged in), so they are not doubled."""
    seen = set()
    for name in os.listdir(out_dir):
        if name.endswith('.jsonl.gz'):
            with gzip.open(os.path.join(out_dir, name), 'rt', encoding='utf-8') as handle:
                seen.update(json.loads(line)['h'] for line in handle if line.strip())
    return seen


def stream_static(version, args, state, state_path, splits):
    """The whole version from its static daily file (/exports/runs-<version>.jsonl.gz), streamed:
    v0.111.1 was 52 MB, v0.111.0 1.8 GB on 2026-11-23. The paged API timed out for both that day."""
    if version in state['done']:
        return
    known = existing_hashes(args.out)
    url = f'https://spire-codex.com/exports/runs-{version}.jsonl.gz'
    request = urllib.request.Request(url, headers={'User-Agent': 'build-advisor-tools'})
    started = time.time()
    kept = seen = 0
    part = os.path.join(args.out, 'part-%06d.jsonl.gz' % state['part'])
    with urllib.request.urlopen(request, timeout=600) as response, \
            gzip.GzipFile(fileobj=response) as raw, gzip.open(part, 'wt', compresslevel=6) as out:
        for line in io.TextIOWrapper(raw, encoding='utf-8'):
            if not line.strip():
                continue
            seen += 1
            try:
                row = convert(json.loads(line))
            except (ValueError, TypeError, AttributeError):
                row = None
            if row is None or row['h'] in known:
                continue
            known.add(row['h'])
            out.write(json.dumps(row, separators=(',', ':')) + '\n')
            splits.write(f"{row['h']}\t{split_of(row['h'])}\n")
            kept += 1
            if kept >= args.max_runs:
                break
            if seen % 20000 == 0:
                print(f'{version}: seen {seen} kept {kept} ({time.time() - started:.0f}s)', file=sys.stderr)
    splits.flush()
    state['part'] += 1
    state['kept'] += kept
    state['seen'] += seen
    state['done'].append(version)
    json.dump(state, open(state_path, 'w'), indent=1)
    print(f'{version}: static file seen {seen} kept {kept} ({time.time() - started:.0f}s)', file=sys.stderr)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('out')
    parser.add_argument('versions', nargs='+')
    parser.add_argument('--max-runs', type=int, default=150_000, help='kept runs over all versions (paged API) or per version (static file)')
    parser.add_argument('--page', type=int, default=1_000)
    parser.add_argument('--start', help='ISO date to start from. The server scans forward from it and times out where the version is rare '
                             '(v0.111.0 from 2026-08-10 timed out, from 2026-11-01 1,000 runs took 17 s): pick a date when the version was the current one')
    args = parser.parse_args()
    os.makedirs(args.out, exist_ok=True)
    state_path = os.path.join(args.out, 'state.json')
    state = json.load(open(state_path)) if os.path.exists(state_path) else {'cursors': {}, 'done': [], 'kept': 0, 'seen': 0, 'part': 0}
    splits = open(os.path.join(args.out, 'splits.tsv'), 'a', encoding='utf-8')
    for spec in args.versions:
        # "v0.111.1@2026-11-16": a start date per version (the export times out where a version is rare).
        version, _, own_start = spec.partition('@')
        start = own_start or args.start
        if not own_start and not args.start:
            stream_static(version, args, state, state_path, splits)
            continue
        while version not in state['done'] and state['kept'] < args.max_runs:
            started = time.time()
            try:
                body, cursor = fetch(version, args.page, state['cursors'].get(version), start)
            except Exception as error:
                # The tail of an older version is sparse and times out: keep what was fetched.
                print(f'{version}: giving up after retries ({error}); keeping {state["kept"]} runs', file=sys.stderr)
                state['done'].append(version)
                json.dump(state, open(state_path, 'w'), indent=1)
                continue
            part = os.path.join(args.out, 'part-%06d.jsonl.gz' % state['part'])
            kept = seen = 0
            with gzip.open(io.BytesIO(body), 'rt', encoding='utf-8') as source, gzip.open(part, 'wt', compresslevel=6) as out:
                for line in source:
                    if not line.strip():
                        continue
                    seen += 1
                    try:
                        row = convert(json.loads(line))
                    except (ValueError, TypeError, AttributeError):
                        row = None
                    if row is None:
                        continue
                    out.write(json.dumps(row, separators=(',', ':')) + '\n')
                    splits.write(f"{row['h']}\t{split_of(row['h'])}\n")
                    kept += 1
            splits.flush()
            state['part'] += 1
            state['kept'] += kept
            state['seen'] += seen
            if cursor and seen > 0:
                state['cursors'][version] = cursor
            else:
                state['done'].append(version)
            json.dump(state, open(state_path, 'w'), indent=1)
            print(f'{version}: page {state["part"]} seen {seen} kept {kept} total kept {state["kept"]} '
                  f'({time.time() - started:.0f}s)', file=sys.stderr)
    print(json.dumps(state), file=sys.stderr)


if __name__ == '__main__':
    main()
