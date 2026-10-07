#!/usr/bin/env bash
# Install/upgrade the desktop app's Finder integration as one rollback-capable operation.
set -euo pipefail
RESOURCES="${1:?resources directory required}"
VERSION="${2:?version required}"
BIN_DIR="$HOME/.local/bin"
SUPPORT="$HOME/Library/Application Support/MetadataDel"
WORKFLOW="$HOME/Library/Services/Удалить метаданные (MetadataDel).workflow"
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/integration-safety.sh"
mdel_check_finder_paths
mdel_check_command_paths "$BIN_DIR" metadatadel
mkdir -p "$BIN_DIR" "$SUPPORT" "$HOME/Library/Services"
LOCK="$SUPPORT/install.lock"
if ! mkdir "$LOCK" 2>/dev/null; then echo 'Настройка Finder уже выполняется.' >&2; exit 2; fi
STAGE=""
TARGETS=("$BIN_DIR/metadatadel" "$BIN_DIR/mdel" "$WORKFLOW" "$SUPPORT/finder-action.sh" "$SUPPORT/command-path" "$SUPPORT/desktop-version")
MUTATING=0
finish() {
  result=$?
  trap - EXIT
  if [[ "$result" != 0 && "$MUTATING" == 1 ]]; then
    for i in "${!TARGETS[@]}"; do
      rm -rf "${TARGETS[$i]}"
      if [[ -e "$STAGE/old-$i" || -L "$STAGE/old-$i" ]]; then cp -pRP "$STAGE/old-$i" "${TARGETS[$i]}"; fi
    done
    echo 'Обновление не завершено. Предыдущая установка восстановлена.' >&2
  fi
  if [[ -n "$STAGE" ]]; then rm -rf "$STAGE"; fi
  rmdir "$LOCK" 2>/dev/null || true
  exit "$result"
}
trap finish EXIT
STAGE="$(mktemp -d "$BIN_DIR/.metadatadel-install.XXXXXX")"
cp "$RESOURCES/bin/metadatadel" "$STAGE/metadatadel"
chmod 755 "$STAGE/metadatadel"
"$STAGE/metadatadel" --help >/dev/null 2>&1
for i in "${!TARGETS[@]}"; do
  if [[ -e "${TARGETS[$i]}" || -L "${TARGETS[$i]}" ]]; then cp -pRP "${TARGETS[$i]}" "$STAGE/old-$i"; fi
done
MUTATING=1
mv -f "$STAGE/metadatadel" "$BIN_DIR/metadatadel"
rm -f "$BIN_DIR/mdel"
ln -s metadatadel "$BIN_DIR/mdel"
METADATADEL_INSTALL_DIR="$BIN_DIR" METADATADEL_COMMAND_NAME=metadatadel bash "$RESOURCES/scripts/mac/install-finder-action.sh"
printf '%s\n' "$VERSION" > "$SUPPORT/desktop-version"
echo 'Очистка правым кликом установлена. Приложение можно закрыть.'
