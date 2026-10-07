#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/resources/bin" "$WORK/resources/scripts/mac" "$WORK/home"
cp "$ROOT/scripts/mac/"*.sh "$WORK/resources/scripts/mac/"
printf '#!/bin/sh\nprintf "MetadataDel help\\n"\n' > "$WORK/resources/bin/metadatadel"
chmod +x "$WORK/resources/bin/metadatadel"
install_app() { HOME="$WORK/home" bash "$ROOT/scripts/mac/install-app-integration.sh" "$WORK/resources" 2.4.0; }
install_app >/dev/null
SUPPORT="$WORK/home/Library/Application Support/MetadataDel"
for name in command-path finder-action.sh desktop-version; do
  cp "$SUPPORT/$name" "$WORK/saved"
  rm "$SUPPORT/$name"
  printf 'foreign data' > "$WORK/foreign"
  ln -s "$WORK/foreign" "$SUPPORT/$name"
  if install_app >/dev/null 2>&1; then echo "Unexpected installation over $name symlink" >&2; exit 1; fi
  [[ "$(cat "$WORK/foreign")" == 'foreign data' ]]
  rm "$SUPPORT/$name"; cp "$WORK/saved" "$SUPPORT/$name"
done
# A symlink in a parent directory must be rejected before any writes.
cp -Rp "$SUPPORT" "$WORK/support-copy"
mv "$SUPPORT" "$WORK/support-real"
ln -s "$WORK/support-real" "$SUPPORT"
if install_app >/dev/null 2>&1; then echo 'Expected parent symlink refusal' >&2; exit 1; fi
rm "$SUPPORT"; mv "$WORK/support-real" "$SUPPORT"
# Symlinks inside the workflow must not reach their external targets.
WF="$WORK/home/Library/Services/Удалить метаданные (MetadataDel).workflow/Contents/document.wflow"
cp "$WF" "$WORK/workflow-saved"; rm "$WF"
printf 'external workflow data' > "$WORK/foreign"
ln -s "$WORK/foreign" "$WF"
if install_app >/dev/null 2>&1; then echo 'Expected workflow link refusal' >&2; exit 1; fi
[[ "$(cat "$WORK/foreign")" == 'external workflow data' ]]
rm "$WF"; cp "$WORK/workflow-saved" "$WF"
rm "$WORK/home/.local/bin/mdel"
printf 'foreign command' > "$WORK/home/.local/bin/mdel"
if HOME="$WORK/home" bash "$ROOT/scripts/mac/uninstall.sh" >/dev/null 2>&1; then echo 'Expected refusal to delete foreign alias' >&2; exit 1; fi
[[ "$(cat "$WORK/home/.local/bin/mdel")" == 'foreign command' ]]
[[ -x "$WORK/home/.local/bin/metadatadel" ]]
echo 'Ownership: symlink targets and foreign alias preserved PASS'
