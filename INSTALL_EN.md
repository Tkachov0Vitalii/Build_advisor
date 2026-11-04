# Installing Build Advisor 7.1.0

The same package works on Windows, Linux and macOS. No game files are redistributed.

## Steam Workshop

Subscribe to **[Build Advisor in the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3832174623)**, start the game and choose **Modded**. Steam installs and updates the mod.

## Manual install ([GitHub Releases](https://github.com/Tkachov0Vitalii/Build_advisor/releases), [Nexus Mods](https://www.nexusmods.com/slaythespire2/mods/1588))

1. Quit Slay the Spire 2.
2. Extract `BuildAdvisor-7.1.0.zip` into the game's `mods` folder (create it next to the game executable if it does not exist). The result:

```text
mods/
  build-advisor/
    build-advisor.dll
    build-advisor.json
    build-advisor-run-priors.data
    segment-risk-v1.data
    score-weights-v1.data
    fonts/            PT Serif, Forum, Cinzel + licenses
    README.md, CHANGELOG.md, LICENSE …
```

3. Start the game and choose **Modded**.
4. In a run, click the red wax seal **BD** at the left edge of the screen or press **F8**. Language (Русский / English) is at the bottom of the panel.

`mods` locations:

- **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`
- **Linux:** `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/`
- **macOS:** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`

For another library use Steam's **Manage → Browse local files**.

## Updating

Quit the game, delete the old `mods/build-advisor` folder (or every older copy, including a nested `build-advisor/build-advisor`), extract the new archive. Never keep two copies: the game would load both. Settings and caches live outside the mod folder and survive updates.

## Troubleshooting

The game log `godot.log` (Windows: `%APPDATA%\SlayTheSpire2\logs\`) must contain `Build Advisor 7.1.0 loaded; all patches installed; regression matrix passed.` When reporting a bug on GitHub, attach this log, a screenshot, your OS and the game version.
