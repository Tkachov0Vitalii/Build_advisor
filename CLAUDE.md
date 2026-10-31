# Build Advisor — Slay the Spire 2 mod

In-game RU/EN deck advisor: scores card rewards, shop items, relic choices and event card screens against the current deck. C# / .NET 9, Harmony patches over the game's Godot nodes, data from spire-codex.com plus bundled models trained on 177k runs.

The owner speaks Russian: reply in Russian. Code, comments and commit messages are in English. All user-facing text is written in Russian first and translated to English through the dictionary in `src/ModLocalization.cs` (see "Localization").

## Commands

```powershell
# build (finds the game in the default Steam folder; otherwise -p:Sts2ManagedDir=<data_sts2_windows_x86_64>)
dotnet build BuildAdvisor.csproj -c Release
# offline regression matrix (must print Regression=True)
dotnet run --project tools/RegressionRunner -c Release
# full release: version check, -warnaserror build, regression, Harmony target check, ZIP + SHA256SUMS
powershell -ExecutionPolicy Bypass -File tools\make_release.ps1      # Windows
tools/make_release.sh                                                # Linux/macOS/Git Bash
```

Game assemblies for builds outside the game folder live in `tools/bin/Debug/net9.0` (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`, `sts2.xml`). They are references only and are never packaged or committed (`.gitignore`). After a game update copy fresh ones from `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64` and run the release script: the Harmony step lists hooks the update broke.

Installed mod: `<game>\mods\build-advisor\` (contents of the `build-advisor` folder of the release ZIP). The game log line to check: `Build Advisor <version> loaded; all patches installed; regression matrix passed.`

## Layout

- `src/` — the mod. Entry point `BuildAdvisorMod.Initialize` installs each Harmony patch class separately (one renamed game method must not disable the rest).
- `src/RunContextReader.cs` — reads the run by reflection: local player (`LocalContext.GetMe`, co-op safe), deck with duplicates and upgrade levels (`CardStates` is index-aligned with `Cards`), relics, HP, gold, route, run seed.
- `src/DeckBuildScoring.cs` — the score: community baseline (Codex Score) + deck adjustments (`Adjust(value, label)`; every adjustment appears in the formula). Also `RelicKnowledge` (relic roles taken from the game assembly).
- `src/TakeSkipModel.cs` — take/skip on one scale in percentage points of win rate: skip = 0, card = Spire Codex `lift` + deck adjustments × 0.19 pp per score point.
- `src/SpireCodexMetricsClient.cs`, `CardCatalogClient.cs`, `RelicCatalogClient.cs`, `PickCoachClient.cs` — network + disk cache; every request goes through `NetworkSettings` (offline mode).
- `src/CardUpgrades.cs`, `TierOverrides.cs`, `PersonalRunDiary.cs`, `ArchetypePreferences.cs`, `RunHistoryInsights.cs`, `SegmentRiskModel.cs`.
- `src/AdviceOverlay.cs` — all UI (panel, badges, search, F8 hotkey in `LiveUpdater`).
- `src/BuildAdvisorRegression.cs` — regression matrix; runs at game start and via `tools/RegressionRunner`.
- `tools/` — offline evaluation and model training scripts, release scripts. `data/` — bundled models.
- `docs/ROADMAP.md` — the improvement plan (light / medium / heavy, status per task). `docs/DEVELOPMENT.md` — architecture and safe-change rules.

## Rules for every change

1. **Regression first.** Every behaviour change gets a positive and a negative scenario in `BuildAdvisorRegression.cs` and is added to `Run()`. Then mutation-check it: temporarily break the logic, confirm `Regression=False`, restore.
2. **Hermetic tests.** While `BuildAdvisorRegression.IsRunning`, personal data is ignored (diary, local `.run` files, tier overrides). New personal-data features must respect the flag. Tests must not write user files (no `NetworkSettings.Offline = …`, no diary saves).
3. **No Godot calls in testable logic.** `GD.Print`, `ProjectSettings`, nodes crash outside the engine. Keep them in UI/patch code or behind paths the tests never reach.
4. **Async.** Continuations return to the Godot main thread through its sync context; parse JSON with `await Task.Run(...)`. Check `GodotObject.IsInstanceValid` after every await. Swallow `OperationCanceledException` separately from other errors.
5. **Reflection.** Use `ReflectionIds.Member` (cached getters, safe with hidden properties). Prefer typed game API when it exists — verify it in `sts2.dll` first (the game ships `sts2.xml` docs next to it).
6. **Network data.** API rates are percent (43.0), scale is decided per response. Validate a payload before writing the cache; never cache an empty response.
7. **Build must be warning-free** (`-warnaserror` in the release script).

## Localization

User-facing text is Russian in code. English mode replaces substrings using `English` in `src/ModLocalization.cs` (longest key first, case-insensitive). For every new Russian string add a `("русский", "English")` pair; the regressions `EnglishAdviceHasNoRussianFragments` and `NewTextIsTranslated` fail on leftover Cyrillic. Server texts (Pick Coach) are requested in the UI language.

## Versioning and release

`major.minor.patch`: new or changed user behaviour → minor, fix without behaviour change → patch. The version must match in `build-advisor.json`, `BuildAdvisor.csproj` `<Version>`, `ModVersion.Current` in `src/BuildAdvisorMod.cs`, the `README.md` title and a `## X.Y.Z — date` entry at the top of `CHANGELOG.md` (the release script refuses otherwise). Also bump `INSTALL_*.md` and `PUBLICATION_CHECKLIST_RU.md`, mark finished tasks `✅ X.Y` in `docs/ROADMAP.md`, then commit and tag `vX.Y.Z`.

Commit messages: imperative subject `X.Y.Z: summary`, body with what changed and what was verified.

## Facts already verified (do not re-derive)

- Spire Codex brackets: only `all`, `a10`, `wr30`, `wr50`, `wr75`; `a0`..`a9` return 400 `unknown bracket` (`RunContext.Bracket` maps A<10 to `all`). Relics also take `act=1..3`, cards `include_skip`. Ancient events are not in `events`.
- Player HP is on `Player.Creature.CurrentHp/MaxHp`; potions in `Player.PotionSlots` (null = empty), `MaxPotionCount`.
- Spire Codex: `/api/runs/metrics/{cards|relics|potions}?bracket=a10[&character=ironclad]` → `rows[]` with `score, tier, elo, win_rate, pick_rate, lift, lift_n, use_rate, pick_rate_by_act` (percent). `upgraded` rows exist only for `bracket=all` and have no lift. `/api/runs/metrics/events` → `{event, option, win_rate, lift, lift_n}` for 56 events. `/api/runs/metrics/campfires`, `/shops` exist. Catalogue `/api/cards?lang=eng` has `upgrade` (`cost` absolute, other keys are deltas like `"+2"`, scaling keys `strengthpower`, `dexterity`, `focuspower`).
- lift ≈ 0.19 × (Codex Score − 27), residual SD 1.9 pp (491 cards, A10).
- Character rows: own cards hold 88–100 % of global samples, colorless 14–41 %, other classes ≤ 6 % (survivor-biased). Merge rule: ≥ 200 samples and ≥ 10 % share.
- Game: `LocalContext.GetMe(IEnumerable<Player>)` returns null in single player; `RunState.Rng.StringSeed` is the run seed; `NEventOptionButton.Option` → `EventOption.TextKey`, `WillKillPlayer`; `NMerchantInventory.OnPurchaseCompleted` fires after a purchase; `CardModel.CurrentUpgradeLevel/MaxUpgradeLevel/IsUpgraded`. Game binds F1–F7, F9, F10 (NInputManager) and F11 (dev console) — F8 is the mod's panel key.
- Max-energy relics (override `ModifyMaxEnergy`): Blessed Antler, Blood-Soaked Rose, Bread, Ectoplasm, Pael's Flesh, Philosopher's Stone, Prismatic Gem, Pumpkin Candle, Sozu, Spiked Gauntlets, Velvet Choker, Whispering Earring.

## Current state

Latest release: see the top of `CHANGELOG.md` (6.20.0: bracket/HP fixes, M2 events, M3 potions, M4 breakdown). Next planned work: `docs/ROADMAP.md`, release 6.21.0 — M6 (campfire advice), M8 (card art from game resources), M9 (Ancient/boss relics). Not yet verified in the running game: 6.17–6.20 UI (offline toggle, shop badges, "Card+", F8, purchase refresh, event ★ lines, rewards-screen potion panel `NRewardsScreen._Ready`, breakdown hover size).
