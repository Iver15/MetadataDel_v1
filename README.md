# MetadataDel

MetadataDel удаляет метаданные из PDF, Word и Excel-файлов. На Windows установщик добавляет пункт **Удалить метаданные** в Проводник, на macOS установщик добавляет Finder Quick Action. CLI подходит для пакетной очистки файлов и папок.

Поддерживаемые форматы: `.pdf`, `.docx`, `.doc`, `.xlsx`, `.xls`.

## Возможности

- Очистка пользовательских и служебных свойств документов: автор, компания, тема, ключевые слова, комментарии, ревизии, сведения приложений и похожие поля.
- Обработка PDF в памяти с повторными попытками записи для сетевых дисков и временно заблокированных файлов.
- Дополнительная дочистка PDF через встроенный `exiftool` в Windows-дистрибутиве.
- Пакетная обработка через SendTo, папки и командную строку.
- Режим аудита: проверка файлов на признаки метаданных без изменения содержимого.
- Опциональные `.bak`-резервные копии перед изменением файлов.

## Быстрый старт для Windows

1. Скачайте `MetadataDel-Setup-<version>.exe` из GitHub Releases.
2. Запустите установщик.
3. Нажмите правой кнопкой на файл или папку и выберите **Удалить метаданные**.

Установщик кладёт в профиль пользователя:

- `MetadataDel.exe`;
- `app.ico`;
- `tools/win/exiftool.exe`;
- `tools/win/exiftool_files/`;
- ярлыки и интеграцию с Проводником.

Отдельно устанавливать .NET и `exiftool` не нужно.

## Быстрый старт для macOS

1. Скачайте `MetadataDel-<version>-osx-arm64.dmg` из GitHub Releases.
2. Откройте DMG и запустите **MetadataDel Installer**.
3. Нажмите **Установить или обновить**.
4. В Finder нажмите правой кнопкой на файл или папку и выберите **Быстрые действия** -> **Удалить метаданные (MetadataDel)**.

Установщик работает без `sudo` и кладёт файлы только в профиль пользователя:

- `~/.local/bin/metadatadel`;
- `~/.local/bin/mdel`;
- `~/Library/Services/Удалить метаданные (MetadataDel).workflow`;
- `~/Library/Application Support/MetadataDel/finder-action.sh`.

Для публичного релиза DMG нужно подписывать Developer ID и notarize через Apple. Без нотарификации macOS может показать предупреждение Gatekeeper при первом запуске.

Удаление выполняется из того же **MetadataDel Installer** кнопкой **Удалить**. Подробная инструкция по установке, удалению, Gatekeeper и сборке DMG: [docs/MACOS.md](docs/MACOS.md).

## Как пользоваться

### Один файл

Правый клик на `.pdf`, `.docx`, `.doc`, `.xlsx` или `.xls` -> **Удалить метаданные**.

### Много файлов

Выделите файлы -> **Отправить** -> **Удалить метаданные**.

Этот путь запускает один процесс MetadataDel для всех выбранных файлов и обычно быстрее контекстного меню на большом количестве документов.

### Папка

Правый клик на папке -> **Удалить метаданные**.

MetadataDel рекурсивно обработает все поддерживаемые файлы внутри папки и вложенных папок.

### Командная строка

```powershell
# Очистить файлы
MetadataDel.exe --log file.pdf document.docx report.xlsx

# Очистить все поддерживаемые файлы в папке
MetadataDel.exe --log "C:\Documents\Reports"

# Создать .bak-копии перед очисткой
MetadataDel.exe --log --backup file.pdf

# Проверить файлы без изменения
MetadataDel.exe --audit file.pdf document.docx

# Сбросить даты файловой системы на 1980-01-01 UTC
MetadataDel.exe --wipe-fs file.pdf
```

## Поддерживаемые форматы

| Формат | Что очищается | Примечания |
|--------|---------------|------------|
| PDF | Info dictionary, XMP, аннотации, формы, вложения, PieceInfo, часть служебных следов iText/Producer | `exiftool` используется автоматически, если найден рядом с приложением |
| DOCX | Core, Extended и Custom properties, комментарии, ревизии, tracked changes, статистика документа, Template, AppVersion и связанные поля | Microsoft Office не нужен |
| XLSX | Свойства книги, авторы, комментарии, подключения, внешние ссылки, статистика, Template, AppVersion и связанные поля | Microsoft Office не нужен |
| DOC | OLE property streams со свойствами документа | Microsoft Office не нужен для CLI-очистки |
| XLS | OLE property streams со свойствами документа | Microsoft Office не нужен для CLI-очистки |

Контекстное меню для некоторых legacy-форматов зависит от того, как Windows определяет связанные приложения. CLI и обработка папок работают по расширению файла.

## Опции CLI

| Опция | Описание |
|-------|----------|
| `--log` | Записывать лог в `%LocalAppData%\MetadataDel\logs\` |
| `--backup` / `--backup=on` | Создать резервную копию `.bak` перед изменением |
| `--backup=off` | Явно отключить резервные копии |
| `--aggressive-pdf` | Показать предупреждение, если `exiftool` недоступен или не смог дочистить PDF |
| `--wipe-fs`, `--wipe-fs-timestamps` | Сбросить даты создания, изменения и доступа файла на `1980-01-01 UTC` |
| `--audit <files>` | Проверить файлы на признаки метаданных без очистки |
| `--install` | Установить приложение в профиль пользователя и добавить интеграцию с Проводником |
| `--uninstall` | Удалить приложение и интеграцию с Проводником |
| `--diagnostics` | Проверить доступные точки интеграции с Проводником |
| `--help`, `-h` | Показать справку |

Коды возврата:

- `0` - все файлы обработаны успешно;
- `1` - часть файлов обработана, часть завершилась ошибкой;
- `2` - ни один файл не обработан или команда вызвана неверно.

## Установка без Setup.exe

Портативный вариант:

1. Скачайте `MetadataDel.exe`.
2. Положите рядом каталог `tools/win/` из ZIP-дистрибутива, если нужна максимальная очистка PDF через `exiftool`.
3. Запустите:

```powershell
MetadataDel.exe --install
```

Удаление:

```powershell
MetadataDel.exe --uninstall
```

Также можно удалить MetadataDel через **Параметры Windows -> Приложения**.

## Сборка из исходников

Требуется .NET 8 SDK. Для Windows-сборки с macOS используйте полный SDK из `dotnet-install.sh`, потому что brew-версия может не содержать Windows Desktop targets.

### Windows exe

```bash
~/.dotnet/dotnet publish MetadataDel.Cli/MetadataDel.Cli.csproj -c Release -r win-x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:SelfContained=true \
  -p:NuGetAudit=false \
  -o ./publish
```

### macOS CLI

Apple Silicon:

```bash
~/.dotnet/dotnet publish MetadataDel.MacCli/MetadataDel.MacCli.csproj -c Release -r osx-arm64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:NuGetAudit=false \
  -o ./publish-mac
```

Intel:

```bash
~/.dotnet/dotnet publish MetadataDel.MacCli/MetadataDel.MacCli.csproj -c Release -r osx-x64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -p:NuGetAudit=false \
  -o ./publish-mac-x64
```

Быстрый запуск на macOS во время разработки:

```bash
bash scripts/mac/mdel /path/to/file1.pdf /path/to/file2.docx
```

Простая локальная установка на macOS:

```bash
bash scripts/mac/install.sh
```

Скрипт соберёт консольную macOS-версию и установит команды `metadatadel` и `mdel` в `~/.local/bin` без `sudo`.
Также он добавит Finder Quick Action: правый клик по файлу или папке -> **Быстрые действия** -> **Удалить метаданные (MetadataDel)**.

После установки:

```bash
mdel --log --backup=on /path/to/file.pdf
metadatadel --audit /path/to/file.pdf
```

Удаление:

```bash
bash scripts/mac/uninstall.sh
```

Сборка пользовательского macOS DMG:

```bash
bash scripts/mac/build-dmg.sh
```

Артефакт появится в `dist/mac/MetadataDel-<version>-<rid>.dmg`.

Подписанная и нотарифицированная сборка:

```bash
SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
NOTARY_PROFILE="metadata-del-notary" \
WAIT_FOR_NOTARIZATION=1 \
bash scripts/mac/build-dmg.sh
```

Подробности по установщику, удалению и запуску без Apple Developer ID описаны в [docs/MACOS.md](docs/MACOS.md).

### Тесты

```bash
~/.dotnet/dotnet test MetadataDel.Core.Tests/MetadataDel.Core.Tests.csproj -p:NuGetAudit=false
```

Тесты кроссплатформенные и программно создают PDF, DOCX и XLSX-файлы.

## Установщики и релизы

### Inno Setup

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" installer\MetadataDel.iss
```

Скрипт ожидает готовый каталог `publish` и включает его целиком в `MetadataDel-Setup-<version>.exe`.

### Сборка Inno Setup с macOS

```bash
./scripts/build-installer.sh
```

Скрипт пересобирает `MetadataDel.Cli/app.ico` из `icon.svg`, публикует Windows-сборку в `./publish` и запускает Inno Setup в Docker.

### MSI для доменного развёртывания

```powershell
dotnet tool install --global wix
pwsh installer/msi/build-msi.ps1 -Version 2.2.1
```

MSI ставит приложение в `Program Files` и регистрирует контекстное меню в HKLM. Этот вариант рассчитан на GPO/корпоративное развёртывание.

### GitHub Actions

При пуше тега `v*` workflow собирает self-contained Windows exe, Inno Setup установщик и ZIP-дистрибутив, затем прикрепляет артефакты к GitHub Release.

Перед публичной публикацией и релизом используйте [docs/PUBLISHING.md](docs/PUBLISHING.md).

## Ограничения

- MetadataDel удаляет известные документные метаданные, но не является криминалистическим инструментом и не гарантирует удаление любой возможной скрытой информации из произвольного файла.
- `--wipe-fs` сбрасывает даты файловой системы, но не удаляет владельца файла, ACL, сетевые атрибуты и свойства, которые хранит Windows или файловый сервер.
- Если PDF открыт другой программой и исходный файл нельзя перезаписать, MetadataDel создаёт очищенную копию рядом: `<имя>.MetadataDel.cleaned.pdf`.
- Для максимально полной очистки PDF в Windows-дистрибутиве должен присутствовать `tools/win/exiftool.exe` вместе с каталогом `tools/win/exiftool_files/`.

## Лицензии и сторонние компоненты

Код проекта распространяется по лицензии GNU Affero General Public License v3.0 or later. Полный текст лицензии находится в [LICENSE](LICENSE).

PDF-обработка использует `itext7` 7.2.5, пакет с AGPL-лицензией. Windows-дистрибутив включает `exiftool` и runtime-файлы из Windows-пакета ExifTool. Подробности перечислены в [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Перед распространением бинарных сборок проверьте, что выбранная лицензия проекта и состав дистрибутива совместимы с лицензиями зависимостей.

## FAQ

**Нужен ли .NET на компьютере пользователя?**

Нет. Релизный `MetadataDel.exe` собирается как self-contained приложение.

**Нужно ли отдельно ставить exiftool?**

Нет, если используется `MetadataDel-Setup-<version>.exe` или ZIP-дистрибутив с каталогом `tools/win/`.

**Что делает `--aggressive-pdf`?**

Сам режим очистки не меняется: MetadataDel всегда пытается запустить `exiftool`, если он найден. Флаг только включает предупреждение, если `exiftool` недоступен или завершился ошибкой.

**Безопасно ли запускать на важных файлах?**

Для важных файлов используйте `--backup`: рядом будет создана `.bak`-копия. Без этого флага MetadataDel перезаписывает исходный файл.

**Что делать, если очистка не удалась?**

Рядом с проблемным файлом создаётся `<имя>.MetadataDel-ошибка.txt` с описанием ошибки.

**Как быстрее очистить десятки файлов?**

Используйте **Отправить -> Удалить метаданные** или передайте папку в CLI. Так файлы обрабатываются одним процессом.
