# Публикация Build Advisor

Всё, что нужно для выкладки версии, собирает `tools/make_release.ps1` (или `.sh`) в папку `release/`:

| Файл | Куда |
|---|---|
| `BuildAdvisor-X.Y.Z.zip` | GitHub Releases, Nexus Mods (ручная установка) |
| `BuildAdvisor-X.Y.Z-workshop.zip` | Steam Workshop (рабочая папка для загрузчика Mega Crit) |
| `SHA256SUMS.txt` | GitHub Releases, рядом с архивами |

## Steam Workshop

Загрузчик — официальный инструмент Mega Crit: https://github.com/megacrit/sts2-mod-uploader (раздел Releases → `ModUploader.exe`). Steam должен быть запущен, у аккаунта — купленная игра.

1. Распакуйте `BuildAdvisor-X.Y.Z-workshop.zip` рядом с `ModUploader.exe`. Получится папка `BuildAdvisor-workshop` с `workshop.json`, `image.png`, `previews/` и `content/` (файлы мода).
2. `ModUploader.exe upload -w BuildAdvisor-workshop`. `mod_id.txt` (предмет 3832174623) уже лежит в рабочей папке — загрузчик обновит ту же страницу Workshop.
3. В `workshop.json` стоит `"visibility": "private"`: проверьте страницу в Steam, подпишитесь, запустите игру. Потом смените на `"public"` (в файле или на сайте Steam).
4. Обновление: впишите `changeNote` в `publish/workshop/workshop.json` и снова выполните `upload -w`.

Теги загрузчик берёт из `tags`; «Tools & APIs» зарезервирован за модами-библиотеками — Build Advisor им не является.

## Nexus Mods

1. Страница игры Slay the Spire 2 → Upload mod.
2. Файл: `BuildAdvisor-X.Y.Z.zip` (категория Main files). Описание: `publish/nexus-description.bbcode`. Картинки: `docs/images/logo.png`, `docs/images/reward-screen.jpg`.
3. Требования: нет зависимостей; мод не меняет правила игры.

## GitHub Releases

Облачная сессия Claude не может создавать теги и релизы (ограничение прокси), поэтому релиз создаётся с вашего компьютера одной командой (нужен GitHub CLI `gh` и вход `gh auth login`):

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish_github_release.ps1
```

Скрипт отправляет теги `vX.Y.Z`, создаёт релиз текущей версии с заметками из `docs/releases/vX.Y.Z.md` и прикладывает оба архива и `SHA256SUMS.txt`.
