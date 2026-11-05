# Установка Build Advisor 7.2.0

Один и тот же архив работает в Windows, Linux и macOS. Файлы игры в него не входят.

## Steam Workshop

Подпишитесь на **[Build Advisor в мастерской Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3832174623)**, запустите игру и выберите **Modded**. Steam сам установит и обновит мод.

## Ручная установка ([GitHub Releases](https://github.com/Tkachov0Vitalii/Build_advisor/releases), [Nexus Mods](https://www.nexusmods.com/slaythespire2/mods/1588))

1. Закройте игру.
2. Распакуйте `BuildAdvisor-7.2.0.zip` в папку `mods` игры (создайте её рядом с исполняемым файлом, если её нет). Должно получиться:

```text
mods/
  build-advisor/
    build-advisor.dll
    build-advisor.json
    build-advisor-run-priors.data
    segment-risk-v1.data
    score-weights-v1.data
    fonts/            PT Serif, Forum, Cinzel и лицензии
    README.md, CHANGELOG.md, LICENSE …
```

3. Запустите игру и выберите **Modded**.
4. В забеге нажмите красную печать **BD** у левого края экрана или **F8**. Язык (Русский / English) — внизу панели.

Папка `mods`:

- **Windows:** `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`
- **Linux:** `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/`
- **macOS:** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`

Для другой библиотеки: Steam → **Управление → Просмотреть локальные файлы**.

## Обновление

Закройте игру, удалите старую папку `mods/build-advisor` (и все старые копии, включая вложенную `build-advisor/build-advisor`), распакуйте новый архив. Две копии держать нельзя — игра загрузит обе. Настройки и кэш хранятся вне папки мода и сохраняются.

## Если что-то не так

В журнале игры `godot.log` (Windows: `%APPDATA%\SlayTheSpire2\logs\`) должна быть строка `Build Advisor 7.2.0 loaded; all patches installed; regression matrix passed.` В сообщение об ошибке на GitHub приложите этот журнал, скриншот, ОС и версию игры.
