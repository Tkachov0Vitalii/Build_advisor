import fs from "node:fs";
import path from "node:path";

const [sourceRoot, destination] = process.argv.slice(2);
if (!sourceRoot || !destination) throw new Error("Usage: node build_run_priors.mjs <run-root> <output>");

function clean(value) {
  return String(value ?? "").split(".").at(-1).toUpperCase();
}

function walk(directory, output = []) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) walk(fullPath, output);
    else if ((entry.name.endsWith(".run") || (entry.name.endsWith(".json") && !entry.name.startsWith("_")))) output.push(fullPath);
  }
  return output;
}

const deckAtFloor = new Map();
const duplicates = new Map();
let runCount = 0;

for (const file of walk(path.join(sourceRoot, "runs"))) {
  try {
    const run = JSON.parse(fs.readFileSync(file, "utf8"));
    const player = run.players?.[0];
    if (!player || run.was_abandoned) continue;
    const character = clean(player.character).toLowerCase();
    const deck = Array.isArray(player.deck) ? player.deck : [];
    const maxFloor = (run.map_point_history ?? []).reduce((sum, act) => sum + (Array.isArray(act) ? act.length : 0), 0);
    const won = Boolean(run.win);
    runCount++;

    if (won) {
      const additions = deck.map(card => Math.max(1, Number(card.floor_added_to_deck) || 1));
      if (!deckAtFloor.has(character)) deckAtFloor.set(character, new Map());
      const byFloor = deckAtFloor.get(character);
      for (let floor = 1; floor <= maxFloor; floor++) {
        if (!byFloor.has(floor)) byFloor.set(floor, []);
        byFloor.get(floor).push(additions.filter(added => added <= floor).length);
      }
    }

    const acquired = deck.filter(card => Number(card.floor_added_to_deck) > 1).map(card => clean(card.id)).filter(Boolean);
    const counts = new Map();
    for (const card of acquired) counts.set(card, (counts.get(card) ?? 0) + 1);
    for (const [card, copies] of counts) {
      const key = `${character}|${card}`;
      const row = duplicates.get(key) ?? [0, 0, 0, 0];
      if (copies >= 2) { row[0]++; row[1] += Number(won); }
      else { row[2]++; row[3] += Number(won); }
      duplicates.set(key, row);
    }
  } catch { /* Skip damaged or non-run files. */ }
}

const median = values => {
  const sorted = [...values].sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : Math.round((sorted[middle - 1] + sorted[middle]) / 2);
};
const expected = Object.fromEntries([...deckAtFloor].map(([character, floors]) => [character,
  Object.fromEntries([...floors].filter(([, values]) => values.length >= 5).map(([floor, values]) => [floor, median(values)]))
]));
const duplicateRows = [...duplicates].flatMap(([key, row]) => {
  if (row[0] < 3 || row[2] < 3) return [];
  const [character, card] = key.split("|");
  return [{ character, card, duplicate_runs: row[0], duplicate_wins: row[1], single_runs: row[2], single_wins: row[3] }];
}).sort((a, b) => a.character.localeCompare(b.character) || a.card.localeCompare(b.card));
const result = { schema: 1, generated_utc: new Date().toISOString(), runs: runCount, expected_deck_by_floor: expected, duplicates: duplicateRows };
fs.writeFileSync(destination, JSON.stringify(result));
console.log(JSON.stringify({ runs: runCount, characters: Object.keys(expected).length, duplicate_rows: duplicateRows.length, bytes: fs.statSync(destination).size }));
