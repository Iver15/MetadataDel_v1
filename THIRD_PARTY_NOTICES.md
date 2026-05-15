# Сторонние компоненты и лицензии

MetadataDel использует внешние библиотеки и инструменты. Этот файл перечисляет компоненты, которые важны для сборки, распространения и проверки лицензий.

## Лицензия проекта

MetadataDel распространяется по лицензии GNU Affero General Public License v3.0 or later. Полный текст лицензии находится в `LICENSE`.

Выбор AGPL связан с использованием `itext7` 7.2.5 для PDF-обработки.

## NuGet-зависимости

| Компонент | Где используется | Лицензия / примечание |
|-----------|------------------|------------------------|
| `itext7` 7.2.5 | Очистка PDF | AGPL. Условия лицензии: https://www.gnu.org/licenses/agpl.html |
| `DocumentFormat.OpenXml` 3.0.2 | Очистка `.docx` и `.xlsx` | MIT |
| `System.IO.Packaging` 8.0.1 | Работа с OpenXML-пакетами | MIT |
| `OpenMcdf` 2.3.1 | Очистка OLE-документов `.doc` и `.xls` | MPL-2.0 |
| `Microsoft.Office.Interop.Word` | Windows-target сборка, совместимость с Word Interop | Пакет NuGet без явного license expression в `.nuspec`; используется только в `net8.0-windows` |
| `Microsoft.Office.Interop.Excel` | Windows-target сборка, совместимость с Excel Interop | Пакет NuGet без явного license expression в `.nuspec`; используется только в `net8.0-windows` |
| `xunit`, `Microsoft.NET.Test.Sdk` | Тестовый проект | Не входят в пользовательский дистрибутив |

## Встроенный ExifTool для Windows

Windows-дистрибутив может включать:

- `MetadataDel.Cli/tools/win/exiftool.exe`;
- `MetadataDel.Cli/tools/win/exiftool_files/`.

Этот набор основан на:

- ExifTool by Phil Harvey: https://exiftool.org/
- Strawberry Perl: https://strawberryperl.com/
- Windows launcher by Oliver Betz: https://oliverbetz.de/pages/Artikel/ExifTool-for-Windows

Лицензионные файлы Windows-пакета поставляются рядом с бинарником в `MetadataDel.Cli/tools/win/exiftool_files/`.

## Примечание о распространении

При публикации исходного кода и бинарных сборок нужно сохранять лицензию AGPL, предоставлять доступ к исходному коду соответствующей версии и не удалять лицензионные файлы, которые поставляются рядом с встроенным `exiftool`.
