# Чеклист перед публикацией репозитория

Этот чеклист нужен владельцу проекта перед переводом репозитория MetadataDel в публичный режим.

## Обязательные проверки

- Проверить, что файл `LICENSE` содержит актуальный текст AGPL-3.0.
- Проверить, что README и `.csproj` указывают `AGPL-3.0-or-later`.
- Убедиться, что `THIRD_PARTY_NOTICES.md` соответствует фактическому составу релизного дистрибутива.
- Проверить, что в истории и текущем дереве нет приватных файлов, токенов, абсолютных путей, персональных данных и временных артефактов.
- Запустить тесты:

```bash
~/.dotnet/dotnet test MetadataDel.Core.Tests/MetadataDel.Core.Tests.csproj
```

## Релизная проверка Windows

- Пересобрать `MetadataDel.Cli/app.ico` из `icon.svg`, если иконка менялась:

```bash
python3 scripts/render_windows_icon.py icon.svg MetadataDel.Cli/app.ico
```

- Собрать Windows publish:

```bash
~/.dotnet/dotnet publish MetadataDel.Cli/MetadataDel.Cli.csproj -c Release -r win-x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:SelfContained=true \
  -o ./publish
```

- Проверить, что в `publish` есть:

```text
MetadataDel.exe
app.ico
```

- Собрать установщик:

```bash
./scripts/build-installer.sh
```

## Релизная проверка macOS

Подробная инструкция по macOS-дистрибутиву: [MACOS.md](MACOS.md).

- Собрать DMG для текущей архитектуры macOS:

```bash
bash scripts/mac/build-dmg.sh
```

- Проверить артефакт:

```bash
hdiutil imageinfo dist/mac/MetadataDel-2.4.0-osx-arm64.dmg
hdiutil attach dist/mac/MetadataDel-2.4.0-osx-arm64.dmg -nobrowse -readonly
```

- Проверить, что в DMG есть:

```text
MetadataDel.app
Applications -> /Applications
```

- Для публичного релиза подписать и нотарифицировать DMG:

```bash
SIGN_IDENTITY="Developer ID Application: Example Developer (ABCDE12345)" \
NOTARY_PROFILE="metadata-del-notary" \
WAIT_FOR_NOTARIZATION=1 \
bash scripts/mac/build-dmg.sh
```

- Для внутренней ad-hoc сборки без Developer ID заранее предупредить тестировщиков:
  - запускать приложение через правый клик -> **Открыть**;
  - при блокировке проверить **System Settings -> Privacy & Security**;
  - не считать `spctl ... rejected` ошибкой для неподписанного DMG.

Важно: в документации нельзя публиковать реальные имена Developer ID, Team ID, Apple ID, keychain profile с персональными данными или app-specific password.

- Проверить пользовательский сценарий:
  - открыть `MetadataDel.app`;
  - скопировать приложение в «Программы» и включить очистку правым кликом;
  - убедиться, что появились `~/.local/bin/metadatadel`, `~/.local/bin/mdel` и Finder Quick Action;
  - обработать тестовый PDF/DOCX/XLSX через Finder;
  - выбрать **Настройки → Удалить интеграцию** и проверить удаление интеграции.

## Проверка пользовательских сценариев

- `MetadataDel.exe --diagnostics` показывает ожидаемые форматы.
- `MetadataDel.exe --install` добавляет контекстное меню файлов, папок и пункт SendTo.
- SendTo обрабатывает несколько файлов одним запуском.
- Обработка папки рекурсивно находит `.pdf`, `.docx`, `.doc`, `.xlsx`, `.xls`.
- Заблокированный PDF получает очищенную копию `*.MetadataDel.cleaned.pdf`.
- При ошибке рядом с файлом создаётся `*.MetadataDel-ошибка.txt`.
- `MetadataDel.exe --uninstall` удаляет интеграцию и файлы из `%LocalAppData%\Programs\MetadataDel`.

## GitHub Release

- Создать тег формата `v<version>`, например `v2.3.0`.
- Дождаться workflow `Build Windows Installer`.
- Проверить артефакты релиза:
  - `MetadataDel-Setup-<version>.exe`;
  - `MetadataDel-win-x64.zip`;
  - `MetadataDel-<version>-osx-arm64.dmg`.
- В release notes указать основные изменения, поддерживаемые форматы и лицензионное примечание по PDF-зависимости.

## Создание отдельного публичного репозитория

Не открывайте текущий приватный репозиторий публично, если в его истории были тестовые документы, бинарные артефакты или приватные файлы. Для публичной публикации создайте новый репозиторий без старой истории.

Рекомендуемый порядок:

```bash
PUBLIC_DIR="$HOME/MetadataDel-public"
rm -rf "$PUBLIC_DIR"
mkdir -p "$PUBLIC_DIR"

rsync -a \
  --exclude '.git/' \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude 'dist/' \
  --exclude 'publish/' \
  --exclude 'publish-mac/' \
  --exclude 'publish-mac-x64/' \
  --exclude '.DS_Store' \
  ./ "$PUBLIC_DIR/"

cd "$PUBLIC_DIR"
git init
git add .
git commit -m "Initial public release"
```

После этого проверьте `git status`, `git ls-files` и поиск по приватным маркерам уже в новом каталоге, затем привяжите новый GitHub remote.

## Проверка desktop-версии 2.4

- Пройти матрицу [DESKTOP-ACCEPTANCE.md](DESKTOP-ACCEPTANCE.md); сборка сама по себе не подтверждает установку на чистой ОС.
- Правый клик проверить при закрытом GUI, после обновления с 2.3.0 и после извлечения DMG.
- Windows Inno: `scripts/tests/windows-install-smoke.ps1` запускается только в одноразовом профиле с явным `-AllowInstallInCurrentProfile`.
- MSI остаётся per-machine; проверять отдельно от пользовательского Inno/portable.
- GitHub workflow `Build macOS application` создаёт ad-hoc артефакты arm64/x64. Их нельзя обозначать нотарифицированными: публичную подпись и notarization выполнить отдельно с доступными сертификатами.
- Подписать Windows EXE, Setup/uninstaller и MSI доступным сертификатом до публичного выпуска. SmartScreen проверяется отдельно; валидная подпись не гарантирует отсутствия предупреждения.
