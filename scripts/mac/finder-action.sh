#!/usr/bin/env bash
set -euo pipefail

# Обёртка для Automator / Finder Quick Action.
# В Automator используйте действие "Запустить скрипт оболочки",
# оболочка: /bin/zsh, передать входные данные: как аргументы.

export PATH="$HOME/.local/bin:/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin"

APP_NAME="MetadataDel"
BIN="$HOME/.local/bin/metadatadel"
LOG_DIR="$HOME/Library/Logs/MetadataDel"
LOG_FILE="$LOG_DIR/finder-action.log"

mkdir -p "$LOG_DIR"

notify() {
  local title="$1"
  local message="$2"
  /usr/bin/osascript -e "display notification \"${message//\"/\\\"}\" with title \"${title//\"/\\\"}\"" >/dev/null 2>&1 || true
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

if "$BIN" --log --backup=on "$@" >>"$LOG_FILE" 2>&1; then
  notify "$APP_NAME" "Метаданные удалены: $# объект(ов)."
  exit 0
fi

status=$?
message="Очистка завершилась с ошибкой. Журнал: $LOG_FILE"
echo "[ERR] exit=$status" >>"$LOG_FILE"
notify "$APP_NAME" "$message"
exit "$status"
