#!/usr/bin/env python3
"""7.4 (H7): which relics make which kinds of card worth more, measured on the run corpus.

For every card reward (the corpus extract: offered cards, the pick, the relics held at that floor
and the run's outcome) and every (relic, card feature) pair, a difference in differences:

    synergy = [win(picked an F card | F offered, R held) - win(picked no F card | F offered, R held)]
            - [the same without R]

within character x act strata (weighted by the R-held rewards). Player skill and run length
raise both "picked" and "not picked" win rates alike, so they cancel in the first difference;
the second difference leaves what holding R adds to taking an F card. Pairs are found on the
train split and kept only when validation and test agree in sign and keep at least half the size.

Output: a JSON list of kept pairs with their train/validation/test estimates, consumed by the mod
as data/relic-synergies-v1.data (pp of win rate per pick of a matching card).
"""
import argparse
import gzip
import json
import math
import os
import sys
from collections import defaultdict

MIN_REWARDS = 300          # rewards with the relic held and the feature offered (train)
MIN_EACH = 60              # picks and non-picks in that set
TRAIN_Z = 2.0
HELD_Z = 2.0
MAX_PP = 5.0


def norm(value):
    return ''.join(ch for ch in str(value or '').upper() if ch.isalnum())


def features(card):
    """Card features a relic can care about (catalogue row)."""
    out = set()
    kind = (card.get('type') or '').lower()
    if kind in ('attack', 'skill', 'power'):
        out.add('type:' + kind)
    cost = card.get('cost')
    if card.get('is_x_cost'):
        out.add('cost:x')
    elif isinstance(cost, int):
        out.add('cost:0' if cost == 0 else 'cost:1' if cost == 1 else 'cost:2+')
    for keyword in card.get('keywords_key') or []:
        out.add('kw:' + norm(keyword))
    for power in card.get('powers_applied') or []:
        out.add('power:' + norm(power.get('power_key')))
    if card.get('block'):
        out.add('does:block')
    if card.get('cards_draw'):
        out.add('does:draw')
    if card.get('energy_gain'):
        out.add('does:energy')
    if (card.get('hit_count') or 0) >= 2:
        out.add('does:multihit')
    if card.get('spawns_cards'):
        out.add('does:spawn')
    if 'STRIKE' in norm(card.get('id')):
        out.add('name:strike')
    return out


# 7.4: candidates are pre-registered from the relic's own text: a pair is tested only when the
# relic names the card feature (an exploratory scan of all 50,894 pairs found about as many
# train-significant pairs as chance predicts, and most survivors were reward-composition effects
# such as Lasting Candy adding a Power to rewards).
TEXT_FEATURES = [
    ('type:attack', ('attack',)), ('type:skill', ('skill',)), ('type:power', ('power',)),
    ('kw:EXHAUST', ('exhaust',)), ('kw:RETAIN', ('retain',)), ('kw:ETHEREAL', ('ethereal',)),
    ('name:strike', ('strike',)), ('cost:0', ('0 cost', 'costs 0', 'cost 0', '[energy:0]')), ('cost:x', ('x cost', 'x-cost')),
    ('cost:2+', ('2 or more', 'costs 2', 'cost 2')),
    ('power:VULNERABLE', ('vulnerable',)), ('power:WEAK', ('weak',)), ('power:POISON', ('poison',)),
    ('power:STRENGTH', ('strength',)), ('power:DEXTERITY', ('dexterity',)), ('power:DOOM', ('doom',)),
    ('does:block', ('block',)), ('does:draw', ('draw',)), ('does:multihit', ('times',)),
]


def candidates(relic_rows):
    out = set()
    for row in relic_rows:
        text = (row.get('description') or '').lower()
        for feature, words in TEXT_FEATURES:
            if any(word in text for word in words):
                out.add((norm(row['id']), feature))
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('extract_dir')
    parser.add_argument('splits')
    parser.add_argument('catalog', help='Spire Codex /api/cards?lang=eng')
    parser.add_argument('relic_catalog', help='Spire Codex /api/relics?lang=eng')
    parser.add_argument('out')
    parser.add_argument('--report')
    args = parser.parse_args()

    split_of = {}
    for line in open(args.splits, encoding='utf-8'):
        parts = line.split()
        if len(parts) == 2:
            split_of[parts[0]] = parts[1]
    rows = json.load(open(args.catalog, encoding='utf-8'))
    rows = rows if isinstance(rows, list) else rows.get('cards')
    card_features = {norm(r['id']): features(r) for r in rows}
    relic_rows = json.load(open(args.relic_catalog, encoding='utf-8'))
    relic_rows = relic_rows if isinstance(relic_rows, list) else relic_rows.get('relics')
    relic_names = {norm(r['id']): r['name'] for r in relic_rows}
    starter = {norm(r['id']) for r in relic_rows if 'starter' in (r.get('rarity') or '').lower()}

    # counts[split][(relic or '', feature, stratum)] = [picked wins, picked n, not wins, not n]
    counts = {s: defaultdict(lambda: [0, 0, 0, 0]) for s in ('train', 'validation', 'test')}
    base = {s: defaultdict(lambda: [0, 0, 0, 0]) for s in ('train', 'validation', 'test')}
    runs = 0
    for name in sorted(os.listdir(args.extract_dir)):
        if not name.endswith('.jsonl.gz'):
            continue
        with gzip.open(os.path.join(args.extract_dir, name), 'rt', encoding='utf-8') as handle:
            for line in handle:
                run = json.loads(line)
                split = split_of.get(run['h'])
                if split not in counts or run.get('ab'):
                    continue
                runs += 1
                win = run['w']
                relics = [(norm(r), floor) for r, floor in run['rel'] if norm(r) not in starter]
                for floor, act, _kind, _hp, _max, _gold, offered, _ups, picked in run['pts']:
                    if len(offered) < 2:
                        continue
                    held = {r for r, got in relics if got < floor}
                    stratum = f"{run['c']}:{min(3, max(1, act))}"
                    offered_features = [card_features.get(norm(c), set()) for c in offered]
                    picked_features = offered_features[picked] if 0 <= picked < len(offered) else set()
                    for feature in set().union(*offered_features):
                        took = feature in picked_features
                        for key, table in (((None, feature, stratum), base[split]),):
                            cell = table[key]
                            cell[0 if took else 2] += win
                            cell[1 if took else 3] += 1
                        for relic in held:
                            cell = counts[split][(relic, feature, stratum)]
                            cell[0 if took else 2] += win
                            cell[1 if took else 3] += 1

    # Index the cells by (relic, feature) once: scanning every cell per pair is quadratic.
    index = {}
    for split in counts:
        by_pair = defaultdict(list)
        for (r, f, stratum), cell in counts[split].items():
            by_pair[(r, f)].append((stratum, cell))
        index[split] = by_pair

    def estimate(split, relic, feature):
        """Weighted DiD over strata, with a normal SE; None when too thin."""
        total_w = 0
        acc = 0.0
        var = 0.0
        picks = nots = 0
        for stratum, cell in index[split].get((relic, feature), []):
            bw = base[split].get((None, feature, stratum))
            if bw is None:
                continue
            wp, np_, wn, nn = cell
            # Rewards without the relic = all rewards minus those with it.
            ow, on, xw, xn = bw[0] - wp, bw[1] - np_, bw[2] - wn, bw[3] - nn
            if min(np_, nn, on, xn) < 10:
                continue
            p1, p0, q1, q0 = wp / np_, wn / nn, ow / on, xw / xn
            did = (p1 - p0) - (q1 - q0)
            v = p1 * (1 - p1) / np_ + p0 * (1 - p0) / nn + q1 * (1 - q1) / on + q0 * (1 - q0) / xn
            weight = np_ + nn
            acc += weight * did
            var += weight * weight * v
            total_w += weight
            picks += np_
            nots += nn
        if total_w == 0:
            return None
        return {'did_pp': round(100 * acc / total_w, 2), 'se_pp': round(100 * math.sqrt(var) / total_w, 2),
                'rewards': total_w, 'picks': picks, 'not_picks': nots}

    registered = candidates(relic_rows)
    pairs = sorted(p for p in index['train'] if p in registered)
    found, kept = [], []
    for relic, feature in pairs:
        train = estimate('train', relic, feature)
        if not train or train['rewards'] < MIN_REWARDS or min(train['picks'], train['not_picks']) < MIN_EACH:
            continue
        val, test = estimate('validation', relic, feature), estimate('test', relic, feature)
        entry = {'relic': relic, 'name': relic_names.get(relic, relic), 'feature': feature, 'train': train, 'validation': val, 'test': test}
        found.append(entry)
        if train['se_pp'] <= 0 or abs(train['did_pp']) / train['se_pp'] < TRAIN_Z or not val or not test:
            continue
        sign = 1 if train['did_pp'] > 0 else -1
        # Held-out: validation and test pooled by inverse variance, same sign, z >= HELD_Z.
        wv, wt = 1 / max(val['se_pp'], 1e-6) ** 2, 1 / max(test['se_pp'], 1e-6) ** 2
        held_pp = (val['did_pp'] * wv + test['did_pp'] * wt) / (wv + wt)
        held_se = (wv + wt) ** -.5
        entry['heldout'] = {'did_pp': round(held_pp, 2), 'se_pp': round(held_se, 2)}
        if held_pp * sign / held_se >= HELD_Z:
            # Shipped size: one SE below the held-out estimate, at most the train estimate and MAX_PP.
            entry['pp'] = round(sign * min(abs(train['did_pp']), abs(held_pp) - held_se, MAX_PP), 2)
            kept.append(entry)
    kept.sort(key=lambda x: -abs(x['pp']))
    json.dump({'contract': 'relic-synergies-v1', 'runs': runs, 'kept': kept}, open(args.out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    if args.report:
        json.dump({'runs': runs, 'tested_pairs': len(pairs), 'train_significant': len(found), 'kept': len(kept), 'found': found},
                  open(args.report, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
    print(f'runs {runs} pairs {len(pairs)} train-significant {len(found)} kept {len(kept)}', file=sys.stderr)
    for entry in kept:
        print(entry['name'], entry['feature'], entry['pp'], entry['train']['did_pp'], entry['validation']['did_pp'], entry['test']['did_pp'], entry['train']['rewards'], file=sys.stderr)


if __name__ == '__main__':
    main()
