# Installing Build Advisor 6.22.1

Build Advisor uses the same package on Windows, Linux, and macOS. No game files are redistributed.

## Quick install

1. Quit Slay the Spire 2 completely.
2. Extract `build-advisor` into the game's `mods` directory.
3. The DLL must end up at `…/mods/build-advisor/build-advisor.dll`.
4. Start the game, open Mods, enable **Build Advisor**, and restart if requested.
5. During a run, press the round mod icon. Choose **Русский / English** in the top bar; the preference is saved.

## `mods` locations

- **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`
- **Linux:** `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/`
- **macOS:** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`

For a custom library, use Steam's **Manage → Browse local files** and locate or create `mods` beside the executable.

```text
build-advisor/
  build-advisor.dll
  build-advisor.json
  build-advisor-run-priors.data
  tandrex-emblem.png
```

Do not install two versions at once. Quit the game before updating or removing `mods/build-advisor`. Settings and local history use the OS local-application-data folder, while network caches use the game's data directory; both may remain across updates.

If the mod is not detected, check for an accidental nested folder and duplicate installations. Include the latest game log, OS, CPU architecture, and game version in bug reports. On macOS, app-bundle changes can trigger Gatekeeper warnings; use a trusted archive and do not disable system protection globally.
