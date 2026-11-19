#!/usr/bin/env bash
set -euo pipefail

# Обёртка для Automator / Finder Quick Action.
# В Automator используйте действие "Запустить скрипт оболочки",
# оболочка: /bin/zsh, передать входные данные: как аргументы.

export PATH="$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

APP_NAME="MetadataDel"
BIN="$HOME/.local/bin/metadatadel"
CONFIG="$HOME/Library/Application Support/MetadataDel/command-path"
if [ -f "$CONFIG" ]; then IFS= read -r BIN < "$CONFIG"; fi
LOG_DIR="$HOME/Library/Logs/MetadataDel"
LOG_FILE="$LOG_DIR/finder-action-$(date -u '+%Y-%m-%d').log"

mkdir -p "$LOG_DIR"
chmod 700 "$LOG_DIR" 2>/dev/null || true
# Журналы содержат пути документов: храним 30 дней. Удаляются только собственные файлы с точным именем.
/usr/bin/find "$LOG_DIR" -maxdepth 1 -type f \( -name 'finder-action-[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9].log' -o -name 'finder-action.log' \) -mtime +30 -delete 2>/dev/null || true

# AppleScript string literal: escape backslashes first, then quotes.
applescript_string() {
  printf '%s' "$1" | /usr/bin/sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

notify() {
  local title message
  title="$(applescript_string "$1")"
  message="$(applescript_string "$2")"
  /usr/bin/osascript -e "display notification \"$message\" with title \"$title\"" >/dev/null 2>&1 || true
}

{
  printf '\n[%s] args=%s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$#"
  printf 'binary=%s\n' "$BIN"
} >>"$LOG_FILE"

if [ ! -x "$BIN" ]; then
  message="Не найдена команда $BIN. Переустановите MetadataDel через MetadataDel Installer."
  echo "[ERR] $message" >>"$LOG_FILE"
  notify "$APP_NAME" "$message"
  exit 1
fi

if [ "$#" -eq 0 ]; then
  message="Finder не передал выбранные файлы. Запускайте действие из контекстного меню Finder."
  echo "[ERR] $message" >>"$LOG_FILE"
  notify "$APP_NAME" "$message"
  exit 2
fi

# "--": имена файлов, начинающиеся с "-", не должны читаться как параметры.
if "$BIN" --log --backup=on -- "$@" >>"$LOG_FILE" 2>&1; then
  notify "$APP_NAME" "Метаданные удалены: $# объект(ов). Копии оригиналов — в папке резервных копий MetadataDel."
  exit 0
else
  status=$?
fi
case "$status" in
  # 1: часть файлов очищена не полностью (метаданные остались) или не обработана.
  1) message="Очищено не полностью: в части файлов остались метаданные или произошла ошибка. Подробности: $LOG_DIR" ;;
  130) message="Очистка прервана. Исходные файлы, которые не успели обработаться, не изменены." ;;
  *) message="Очистка завершилась с ошибкой. Журнал и пояснения: $LOG_DIR" ;;
esac
echo "[ERR] exit=$status" >>"$LOG_FILE"
notify "$APP_NAME" "$message"
exit "$status"
