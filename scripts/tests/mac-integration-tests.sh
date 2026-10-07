#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/resources/bin" "$WORK/resources/scripts/mac" "$WORK/home"
cp "$ROOT/scripts/mac/"*.sh "$WORK/resources/scripts/mac/"
cat > "$WORK/resources/bin/metadatadel" <<'STUB'
#!/bin/sh
printf 'MetadataDel help\n'
STUB
chmod +x "$WORK/resources/bin/metadatadel"
run_install() { HOME="$WORK/home" bash "$ROOT/scripts/mac/install-app-integration.sh" "$WORK/resources" "$1"; }
run_install 2.4.0
cmp "$WORK/resources/bin/metadatadel" "$WORK/home/.local/bin/metadatadel"
[[ "$(readlink "$WORK/home/.local/bin/mdel")" == metadatadel ]]
[[ "$(cat "$WORK/home/Library/Application Support/MetadataDel/desktop-version")" == 2.4.0 ]]
run_install 2.4.0
cp "$WORK/home/.local/bin/metadatadel" "$WORK/previous"
# A helper failure after replacing the executable must restore the old working installation.
printf '\n# new version\n' >> "$WORK/resources/bin/metadatadel"
printf '#!/bin/bash\nexit 17\n' > "$WORK/resources/scripts/mac/install-finder-action.sh"
if run_install 2.5.0; then echo 'Expected rollback failure' >&2; exit 1; fi
cmp "$WORK/previous" "$WORK/home/.local/bin/metadatadel"
[[ "$(cat "$WORK/home/Library/Application Support/MetadataDel/desktop-version")" == 2.4.0 ]]
[[ -e "$WORK/home/Library/Services/Удалить метаданные (MetadataDel).workflow/Contents/document.wflow" ]]
printf 'document' > "$WORK/home/important.docx"
printf 'backup' > "$WORK/home/important.docx.bak"
HOME="$WORK/home" bash "$ROOT/scripts/mac/uninstall.sh"
[[ ! -e "$WORK/home/.local/bin/metadatadel" ]]
[[ ! -e "$WORK/home/Library/Services/Удалить метаданные (MetadataDel).workflow" ]]
[[ -f "$WORK/home/important.docx" && -f "$WORK/home/important.docx.bak" ]]
echo 'Integration: fresh/reinstall/rollback/uninstall preservation PASS'
