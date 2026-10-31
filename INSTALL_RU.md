# Установка Build Advisor 6.22.2

Build Advisor использует один пакет на Windows, Linux и macOS. Архив не содержит файлов игры.

## Быстрая установка

1. Полностью закройте Slay the Spire 2.
2. Распакуйте папку `build-advisor` из архива в папку `mods` установленной игры.
3. DLL должна находиться по пути `…/mods/build-advisor/build-advisor.dll`.
4. Запустите игру, откройте меню модов, включите **Build Advisor** и перезапустите игру, если загрузчик этого попросит.
5. В забеге нажмите круглую иконку мода. Язык меняется списком **Русский / English** в верхней строке и сохраняется между запусками.

## Где находится `mods`

- **Windows (Steam):** `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\`
- **Linux (Steam):** `~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/`
- **macOS (Steam):** `~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/`

Для другой библиотеки Steam: правой кнопкой по игре → **Управление → Просмотреть локальные файлы**. Найдите папку `mods` рядом с исполняемым файлом или создайте её.

```text
build-advisor/
  build-advisor.dll
  build-advisor.json
  build-advisor-run-priors.data
  segment-risk-v1.data
  tandrex-emblem.png
```

Не устанавливайте две версии одновременно и не оставляйте второй манифест с тем же `id: build-advisor`.

## Обновление, удаление и ошибки

Для обновления закройте игру и замените пять файлов. Для удаления удалите только `mods/build-advisor`. Настройки и локальная история лежат в системном каталоге локальных данных приложения `BuildAdvisor`, а сетевой кеш — в каталоге данных игры; при обновлении удалять их не нужно.

Если мод не запускается, проверьте отсутствие вложения `build-advisor/build-advisor`, единственность установленной версии и приложите к сообщению свежий лог игры, ОС, архитектуру процессора и версию игры. На macOS изменение `.app` модами может вызвать предупреждение Gatekeeper: используйте доверенный архив и не отключайте системную защиту целиком.
