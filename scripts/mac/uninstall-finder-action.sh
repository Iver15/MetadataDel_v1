#!/usr/bin/env bash
set -euo pipefail

source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/integration-safety.sh"
mdel_check_finder_paths

WORKFLOW_DIR="$HOME/Library/Services/Удалить метаданные (MetadataDel).workflow"
SUPPORT_SCRIPT="$HOME/Library/Application Support/MetadataDel/finder-action.sh"

rm -rf "$WORKFLOW_DIR"
rm -f "$HOME/Library/Application Support/MetadataDel/desktop-version"
rm -f "$SUPPORT_SCRIPT" "$HOME/Library/Application Support/MetadataDel/command-path"
# Журналы и пояснения к ошибкам содержат пути очищенных файлов, поэтому удаляются вместе с программой
# (включая папки прежних версий). Папка Backups с оригиналами документов не удаляется: это данные пользователя.
rm -rf "$HOME/Library/Application Support/MetadataDel/logs" "$HOME/Library/Logs/MetadataDel"
if [[ ! -L "$HOME/.local" && ! -L "$HOME/.local/share" && ! -L "$HOME/.local/share/MetadataDel" ]]; then
  rm -rf "$HOME/.local/share/MetadataDel/logs"
  rmdir "$HOME/.local/share/MetadataDel" 2>/dev/null || true
fi
rmdir "$HOME/Library/Application Support/MetadataDel" 2>/dev/null || true

if [ -x /System/Library/CoreServices/pbs ]; then
  /System/Library/CoreServices/pbs -flush >/dev/null 2>&1 || true
fi

echo "[OK] Finder Quick Action удалён:"
echo "     $WORKFLOW_DIR"
if [ -d "$HOME/Library/Application Support/MetadataDel/Backups" ]; then
  echo "[i] Резервные копии документов сохранены: $HOME/Library/Application Support/MetadataDel/Backups"
fi
