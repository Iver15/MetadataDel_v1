#!/usr/bin/env bash
set -euo pipefail

WORKFLOW_DIR="$HOME/Library/Services/Удалить метаданные (MetadataDel).workflow"
SUPPORT_SCRIPT="$HOME/Library/Application Support/MetadataDel/finder-action.sh"

rm -rf "$WORKFLOW_DIR"
rm -f "$SUPPORT_SCRIPT"

if [ -x /System/Library/CoreServices/pbs ]; then
  /System/Library/CoreServices/pbs -flush >/dev/null 2>&1 || true
fi

echo "[OK] Finder Quick Action удалён:"
echo "     $WORKFLOW_DIR"
