# Contributing to Build Advisor

Thanks for helping! Bug reports with a log are the most valuable contribution.

## Reporting a bug

Open an issue with the **Bug report** template and attach:

- the mod version and the game version (top-right corner of the game),
- `godot.log` (Windows: `%APPDATA%\SlayTheSpire2\logs\godot.log`),
- a screenshot of the screen where the advice was wrong,
- what you expected.

## Code

- C# / .NET 9, Harmony patches over Godot nodes. Read [CLAUDE.md](CLAUDE.md) (verified game facts and project rules) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) first.
- Every behaviour change needs a positive and a negative scenario in `src/BuildAdvisorRegression.cs`; `dotnet run --project tools/RegressionRunner -c Release` must print `Regression=True`.
- The build must be warning-free (`-warnaserror`).
- User-facing text is written in Russian and translated through `src/ModLocalization.cs`; add an English pair for every new string.
- Game assemblies (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`) are never committed.

Code, comments and commit messages are in English; issues may be in English or Russian.
