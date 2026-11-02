"""Checks that every file carrying the version agrees and that the publishing files are valid.
Runs on GitHub Actions (no game assemblies needed) and before a release.
usage: python tools/check_release_metadata.py"""
import json
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
errors = []
manifest = json.loads((root / "build-advisor.json").read_text(encoding="utf-8"))
version = manifest["version"]
for key in ("id", "name", "author", "description", "version", "has_dll", "affects_gameplay"):
    if key not in manifest:
        errors.append(f"build-advisor.json: missing {key}")
checks = {
    "BuildAdvisor.csproj": f"<Version>{version}</Version>",
    "src/BuildAdvisorMod.cs": f'Current = "{version}"',
    "README.md": f"Build Advisor {version}",
    "README_RU.md": f"Build Advisor {version}",
    "INSTALL_EN.md": f"Build Advisor {version}",
    "INSTALL_RU.md": f"Build Advisor {version}",
    "PUBLICATION_CHECKLIST_RU.md": f"BuildAdvisor-{version}.zip",
}
for name, needle in checks.items():
    if needle not in (root / name).read_text(encoding="utf-8"):
        errors.append(f"{name}: '{needle}' not found")
if not re.search(rf"^## {re.escape(version)} ", (root / "CHANGELOG.md").read_text(encoding="utf-8"), re.M):
    errors.append(f"CHANGELOG.md: no '## {version}' entry")
if not (root / "docs" / "releases" / f"v{version}.md").exists():
    errors.append(f"docs/releases/v{version}.md is missing")
workshop = json.loads((root / "publish/workshop/workshop.json").read_text(encoding="utf-8"))
for key in ("title", "description", "visibility", "changeNote"):
    if not workshop.get(key):
        errors.append(f"workshop.json: empty {key}")
if workshop.get("visibility") not in ("private", "public", "unlisted", "friends_only"):
    errors.append("workshop.json: bad visibility")
for image in [root / "publish/workshop/image.png", *(root / "publish/workshop/previews").glob("*")]:
    if image.stat().st_size >= 1_000_000:
        errors.append(f"{image.name}: Steam needs < 1 MB")
weights = json.loads((root / "data/score-weights-v1.data").read_text(encoding="utf-8"))
if weights.get("contract") != "score-weights-v1" or not all(0 <= v <= 3 for v in weights["weights"].values()):
    errors.append("score-weights-v1.data: bad contract or weight out of range")
fonts = root / "assets/fonts"
for font in ("PTSerif-Regular.ttf", "PTSerif-Bold.ttf", "Forum-Regular.ttf", "Cinzel-Variable.ttf"):
    if not (fonts / font).exists():
        errors.append(f"assets/fonts/{font} is missing")
print("\n".join(errors) if errors else f"Release metadata OK for {version}")
sys.exit(1 if errors else 0)
