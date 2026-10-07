#!/usr/bin/env bash
# Shared preflight for paths managed by MetadataDel. No mutations here.
mdel_plain_directory() {
  if [[ -L "$1" || ( -e "$1" && ! -d "$1" ) ]]; then
    echo "Путь занят ссылкой или посторонним файлом: $1" >&2; return 2
  fi
}
mdel_plain_file() {
  if [[ -L "$1" || ( -e "$1" && ! -f "$1" ) ]]; then
    echo "Путь занят ссылкой или папкой: $1" >&2; return 2
  fi
}
mdel_check_finder_paths() {
  local support="$HOME/Library/Application Support/MetadataDel"
  local workflow="$HOME/Library/Services/Удалить метаданные (MetadataDel).workflow"
  local candidate
  for candidate in "$HOME/Library" "$HOME/Library/Application Support" "$support" "$HOME/Library/Services" "$HOME/Library/Logs" "$workflow"; do
    mdel_plain_directory "$candidate" || return $?
  done
  for candidate in "$support/finder-action.sh" "$support/command-path" "$support/desktop-version"; do
    mdel_plain_file "$candidate" || return $?
  done
  if [[ -d "$workflow" ]]; then
    if [[ -n "$(/usr/bin/find "$workflow" -type l -print -quit)" ]]; then
      echo 'В действии Finder обнаружены ссылки. Автоматическое изменение остановлено.' >&2; return 2
    fi
    if [[ ! -f "$workflow/Contents/Info.plist" ]] || [[ "$(/usr/libexec/PlistBuddy -c 'Print :NSServices:0:NSMenuItem:default' "$workflow/Contents/Info.plist" 2>/dev/null)" != 'Удалить метаданные (MetadataDel)' ]]; then
      echo 'Существующее действие Finder не распознано как MetadataDel.' >&2; return 2
    fi
  fi
  if [[ -f "$support/finder-action.sh" ]] && ! /usr/bin/grep -q 'APP_NAME="MetadataDel"' "$support/finder-action.sh"; then
    echo 'Существующий скрипт Finder не распознан как MetadataDel.' >&2; return 2
  fi
}
mdel_check_command_paths() {
  local directory="$1" command="$2" candidate
  # Check every parent below HOME, including custom CLI locations.
  candidate="$directory"
  while [[ "$candidate" != "$HOME" && "$candidate" != / && -n "$candidate" ]]; do
    mdel_plain_directory "$candidate" || return $?
    candidate="$(dirname "$candidate")"
  done
  mdel_plain_file "$directory/$command" || return $?
  if [[ "$command" != mdel && ( -e "$directory/mdel" || -L "$directory/mdel" ) ]]; then
    if [[ ! -L "$directory/mdel" || ( "$(readlink "$directory/mdel")" != "$command" && "$(readlink "$directory/mdel")" != "$directory/$command" ) ]]; then
      echo 'Команда mdel занята другим файлом. Она сохранена; настройка остановлена.' >&2; return 2
    fi
  fi
  if [[ -e "$directory/$command" ]]; then
    if [[ ! -x "$directory/$command" ]] || ! "$directory/$command" --help 2>&1 | /usr/bin/grep -q MetadataDel; then
      echo 'Существующая команда не распознана как MetadataDel. Она сохранена.' >&2; return 2
    fi
  fi
}
