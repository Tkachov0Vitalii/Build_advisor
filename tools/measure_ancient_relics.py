#!/usr/bin/env python3
"""7.4: what each Ancient relic is worth at the moment it is chosen, measured on the run corpus.

Spire Codex relic lift mixes every point of the run: an Ancient relic taken at the start of act 3
only exists in runs that already survived two acts, so its lift is inflated by survival. Here every
Ancient pick is compared with the other Ancient picks of the same character at the same act start:
all of those runs reached the same point, so survival cancels.

    value(R, character, act) = win rate of runs that took R at that Ancient
                             - win rate of all runs that took an Ancient relic there

shrunk toward 0 by n / (n + PRIOR). The table is fitted on train; validation + test give a check:
the correlation of the per-relic values between the two halves and the win-rate gap between the
top and bottom thirds of relics ranked on train.

Input: the corpus extract (`rel` = [relic, floor taken]; `pts` give each floor's act), the split
file and the Spire Codex relic catalogue (`rarity` "Ancient Relic").
Output: data/ancient-relics-v1.data (JSON) and a report.
"""
import argparse
import gzip
import json
import os
import sys
from collections import defaultdict

PRIOR = 150          # pseudo-runs at the act average
MIN_RUNS = 40        # per (character, act, relic) to be listed at all


def norm(value):
    return ''.join(ch for ch in str(value or '').upper() if ch.isalnum())


def act_of(floor, pts):
    """The act of the first recorded floor at or after `floor` (an Ancient opens its act)."""
    later = [p[1] for p in pts if p[0] >= floor]
    return min(3, max(1, later[0])) if later else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('extract_dir')
    parser.add_argument('splits')
    parser.add_argument('relic_catalog')
    parser.add_argument('out')
    parser.add_argument('--report', required=True)
    parser.add_argument('--builds', nargs='*', default=None,
                        help='keep only runs whose build_id starts with one of these (e.g. v0.111); 7.5')
    args = parser.parse_args()

    split_of = {}
    for line in open(args.splits, encoding='utf-8'):
        parts = line.split()
        if len(parts) == 2:
            split_of[parts[0]] = parts[1]
    catalog = json.load(open(args.relic_catalog, encoding='utf-8'))
    catalog = catalog if isinstance(catalog, list) else catalog.get('relics')
    ancient = {norm(r['id']) for r in catalog if 'ancient' in (r.get('rarity') or '').lower()}
    names = {norm(r['id']): r['name'] for r in catalog}

    # cells[split][(character, act)][relic] = [wins, n]
    cells = {s: defaultdict(lambda: defaultdict(lambda: [0, 0])) for s in ('train', 'validation', 'test')}
    for name in sorted(os.listdir(args.extract_dir)):
        if not name.endswith('.jsonl.gz'):
            continue
        with gzip.open(os.path.join(args.extract_dir, name), 'rt', encoding='utf-8') as handle:
            for line in handle:
                run = json.loads(line)
                split = split_of.get(run['h'])
                if split not in cells or run.get('ab') or (args.builds and not str(run.get('b', '')).startswith(tuple(args.builds))):
                    continue
                seen_acts = set()
                for relic, floor in run['rel']:
                    key = norm(relic)
                    if key not in ancient:
                        continue
                    act = 1 if floor <= 1 else act_of(floor, run['pts'])
                    if act is None or act in seen_acts:
                        continue          # one Ancient pick per act (later ones are from other sources)
                    seen_acts.add(act)
                    for character in (run['c'].lower(), 'all'):
                        cell = cells[split][(character, act)][key]
                        cell[0] += run['w']
                        cell[1] += 1

    def table(splits):
        merged = defaultdict(lambda: defaultdict(lambda: [0, 0]))
        for split in splits:
            for group, relics in cells[split].items():
                for relic, (w, n) in relics.items():
                    merged[group][relic][0] += w
                    merged[group][relic][1] += n
        out = {}
        for (character, act), relics in merged.items():
            total_w = sum(w for w, _ in relics.values())
            total_n = sum(n for _, n in relics.values())
            if total_n == 0:
                continue
            mean = total_w / total_n
            for relic, (w, n) in relics.items():
                if n < MIN_RUNS:
                    continue
                value = (w / n - mean) * n / (n + PRIOR)
                out.setdefault(character, {}).setdefault(str(act), {})[relic] = {'pp': round(100 * value, 2), 'n': n}
        return out

    train = table(['train'])
    held = table(['validation', 'test'])

    # Check 1: correlation of per-relic values (all characters, each act) between train and held-out.
    pairs = [(train[c][a][r]['pp'], held[c][a][r]['pp']) for c in train for a in train[c] for r in train[c][a]
             if c == 'all' and r in held.get(c, {}).get(a, {})]
    xs, ys = [p[0] for p in pairs], [p[1] for p in pairs]
    mx, my = sum(xs) / len(xs), sum(ys) / len(ys)
    cov = sum((x - mx) * (y - my) for x, y in pairs)
    corr = cov / ((sum((x - mx) ** 2 for x in xs) * sum((y - my) ** 2 for y in ys)) ** .5)

    # Check 2: on held-out runs, the win rate of runs whose Ancient pick was in the train top third
    # minus the bottom third, per character and act (weighted).
    gap_num = gap_den = 0.0
    for (character, act), relics in cells['validation'].items():
        if character == 'all':
            continue
        ranked = train.get(character, {}).get(str(act), {})
        if len(ranked) < 6:
            continue
        order = sorted(ranked, key=lambda r: ranked[r]['pp'])
        third = len(order) // 3
        low, high = set(order[:third]), set(order[-third:])
        rows = [(r, w, n) for split in ('validation', 'test') for r, (w, n) in cells[split][(character, act)].items()]
        hw = sum(w for r, w, n in rows if r in high); hn = sum(n for r, w, n in rows if r in high)
        lw = sum(w for r, w, n in rows if r in low); ln = sum(n for r, w, n in rows if r in low)
        if hn and ln:
            weight = hn + ln
            gap_num += weight * (hw / hn - lw / ln)
            gap_den += weight

    model = {'contract': 'ancient-relics-v1', 'prior': PRIOR, 'values': train,
             'source': 'corpus extract, train split; value = win rate of the pick minus the act average, shrunk'}
    json.dump(model, open(args.out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False, sort_keys=True)
    report = {'pairs_checked': len(pairs), 'train_vs_heldout_correlation': round(corr, 3),
              'heldout_top_minus_bottom_third_pp': round(100 * gap_num / gap_den, 2) if gap_den else None,
              'picks_per_act': {f'{c}:{a}': sum(n for _, n in relics.values()) for (c, a), relics in cells['train'].items() if c == 'all'},
              'top_by_act': {a: sorted(((names.get(r, r), v['pp'], v['n']) for r, v in train['all'][a].items()), key=lambda x: -x[1])[:8]
                             for a in sorted(train.get('all', {}))},
              'bottom_by_act': {a: sorted(((names.get(r, r), v['pp'], v['n']) for r, v in train['all'][a].items()), key=lambda x: x[1])[:8]
                                for a in sorted(train.get('all', {}))}}
    json.dump(report, open(args.report, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    json.dump(report, sys.stdout, indent=1, ensure_ascii=False)
    print()


if __name__ == '__main__':
    main()
