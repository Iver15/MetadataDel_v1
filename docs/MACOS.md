# MetadataDel для macOS

Документ описывает установку, удаление и сборку macOS-дистрибутива MetadataDel.

## Что получает пользователь

macOS-дистрибутив поставляется как DMG:

```text
MetadataDel-<version>-osx-arm64.dmg
```

Внутри DMG находится приложение:

```text
MetadataDel Installer.app
```

Установщик не требует `sudo` и работает только в профиле текущего пользователя.

## Установка

1. Откройте `MetadataDel-<version>-osx-arm64.dmg`.
2. Запустите **MetadataDel Installer.app**.
3. Нажмите **Установить или обновить**.
4. Откройте Finder.
5. Нажмите правой кнопкой на файл или папку.
6. Выберите **Быстрые действия** -> **Удалить метаданные (MetadataDel)**.

После установки появляются:

```text
~/.local/bin/metadatadel
~/.local/bin/mdel
~/Library/Services/Удалить метаданные (MetadataDel).workflow
~/Library/Application Support/MetadataDel/finder-action.sh
```

Команда `metadatadel` доступна для ручного запуска из терминала, если `~/.local/bin` добавлен в `PATH`.

## Удаление

1. Запустите **MetadataDel Installer.app**.
2. Нажмите **Удалить**.

Удаляются:

```text
~/.local/bin/metadatadel
~/.local/bin/mdel
~/Library/Services/Удалить метаданные (MetadataDel).workflow
~/Library/Application Support/MetadataDel/finder-action.sh
```

Журналы обработки остаются здесь:

```text
~/Library/Logs/MetadataDel/
```

Журналы не удаляются автоматически, чтобы пользователь мог посмотреть причины ошибок после очистки.

## Запуск без Apple Developer ID

Если DMG собран без Developer ID и notarization, приложение можно передавать коллегам, но macOS может показать предупреждение Gatekeeper.

Обычный способ запуска:

1. Откройте DMG.
2. Нажмите правой кнопкой на **MetadataDel Installer.app**.
3. Выберите **Открыть**.
4. Подтвердите запуск в системном диалоге.

Если macOS всё равно блокирует запуск, откройте:

```text
System Settings -> Privacy & Security
```

и разрешите запуск заблокированного приложения.

Крайний вариант для внутреннего тестирования:

```bash
xattr -dr com.apple.quarantine "/Applications/MetadataDel Installer.app"
```

Эту команду не нужно включать в публичную инструкцию для обычных пользователей. Для публичного релиза нужен Developer ID и notarization.

## Сборка DMG

Для сборки нужен .NET 8 SDK и стандартные инструменты macOS Command Line Tools.

Apple Silicon:

```bash
bash scripts/mac/build-dmg.sh
```

Артефакт появится здесь:

```text
dist/mac/MetadataDel-<version>-osx-arm64.dmg
```

Для Intel Mac:

```bash
METADATADEL_RID=osx-x64 bash scripts/mac/build-dmg.sh
```

## Подписанная и нотарифицированная сборка

Для публичного релиза используйте Developer ID Application certificate и профиль `notarytool`.

Пример:

```bash
SIGN_IDENTITY="Developer ID Application: Example Developer (ABCDE12345)" \
NOTARY_PROFILE="metadata-del-notary" \
WAIT_FOR_NOTARIZATION=1 \
bash scripts/mac/build-dmg.sh
```

Скрипт выполнит:

- сборку self-contained CLI;
- сборку `MetadataDel Installer.app`;
- генерацию `.icns` из логотипа;
- подпись `.app`;
- упаковку DMG;
- установку фирменной иконки для смонтированного тома DMG;
- установку фирменной Finder-иконки для локального файла `.dmg`;
- подпись DMG;
- отправку на notarization;
- `stapler staple`;
- проверку Gatekeeper через `spctl`.

Важно: иконка самого файла `.dmg` хранится в macOS metadata (`com.apple.ResourceFork` и FinderInfo). Она видна локально в Finder и обычно сохраняется при копировании между macOS-дисками, но может потеряться при загрузке на GitHub, в мессенджеры или на файловые сервисы, которые отбрасывают extended attributes. Иконка приложения внутри DMG и иконка смонтированного тома сохраняются надёжно.

## Проверка перед отправкой коллегам

```bash
bash scripts/mac/build-dmg.sh
hdiutil imageinfo dist/mac/MetadataDel-2.2.1-osx-arm64.dmg
codesign --verify --deep --strict --verbose=2 "dist/mac/MetadataDel Installer.app"
"dist/mac/MetadataDel Installer.app/Contents/Resources/bin/metadatadel" --help
GetFileInfo dist/mac/MetadataDel-2.2.1-osx-arm64.dmg
xattr -l dist/mac/MetadataDel-2.2.1-osx-arm64.dmg
```

Проверка содержимого DMG:

```bash
mount_dir="$(mktemp -d)"
hdiutil attach dist/mac/MetadataDel-2.2.1-osx-arm64.dmg -mountpoint "$mount_dir" -nobrowse -readonly
find "$mount_dir" -maxdepth 2 -print
hdiutil detach "$mount_dir"
```

Для неподписанной сборки команда ниже ожидаемо покажет `rejected`:

```bash
spctl -a -vvv -t install dist/mac/MetadataDel-2.2.1-osx-arm64.dmg
```

Это нормально для внутренней ad-hoc сборки. Для публичного релиза результат должен быть успешным после Developer ID signing и notarization.
