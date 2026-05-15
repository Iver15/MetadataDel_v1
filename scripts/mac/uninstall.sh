#!/usr/bin/env bash
set -euo pipefail

INSTALL_DIR="${METADATADEL_INSTALL_DIR:-$HOME/.local/bin}"
COMMAND_NAME="${METADATADEL_COMMAND_NAME:-metadatadel}"

rm -f "$INSTALL_DIR/$COMMAND_NAME" "$INSTALL_DIR/mdel"

bash "$(cd "$(dirname "$0")" && pwd)/uninstall-finder-action.sh"

echo "[OK] Удалены команды:"
echo "     $INSTALL_DIR/$COMMAND_NAME"
echo "     $INSTALL_DIR/mdel"
