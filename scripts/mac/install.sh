#!/usr/bin/env bash
set -euo pipefail

# Локальная установка MetadataDel для macOS без sudo.

ROOT_DIR="$(cd "$(dirname "$0")"/../.. && pwd)"
cd "$ROOT_DIR"

INSTALL_DIR="${METADATADEL_INSTALL_DIR:-$HOME/.local/bin}"
COMMAND_NAME="${METADATADEL_COMMAND_NAME:-metadatadel}"
RID="${METADATADEL_RID:-}"

if [ -z "$RID" ]; then
  case "$(uname -m)" in
    arm64) RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *)
      echo "[ERR] Неподдерживаемая архитектура macOS: $(uname -m)" >&2
      exit 1
      ;;
  esac
fi

find_dotnet() {
  if command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
    return
  fi
  for p in \
    "$HOME/.dotnet/dotnet" \
    "/usr/local/share/dotnet/dotnet" \
    "/usr/local/bin/dotnet" \
    "/usr/share/dotnet/dotnet" \
    "/opt/homebrew/share/dotnet/dotnet" \
    "/opt/homebrew/bin/dotnet"
  do
    if [ -x "$p" ]; then echo "$p"; return; fi
  done
}

DOTNET_BIN="${DOTNET:-$(find_dotnet || true)}"
if [ -z "$DOTNET_BIN" ]; then
  echo "[ERR] .NET SDK не найден. Установите .NET 8 SDK или задайте DOTNET=/path/to/dotnet." >&2
  exit 1
fi

if ! command -v exiftool >/dev/null 2>&1; then
  echo "[WARN] exiftool не найден. PDF будет очищаться базово, но для полной дочистки установите:" >&2
  echo "       brew install exiftool" >&2
fi

PUBLISH_DIR="./publish-mac"
if [ "$RID" = "osx-x64" ]; then
  PUBLISH_DIR="./publish-mac-x64"
fi

echo "[INFO] Сборка MetadataDel для $RID..."
"$DOTNET_BIN" publish ./MetadataDel.MacCli/MetadataDel.MacCli.csproj \
  -c Release -r "$RID" \
  -p:PublishSingleFile=true -p:SelfContained=true \
  -o "$PUBLISH_DIR"

if [ ! -f "$PUBLISH_DIR/MetadataDel" ]; then
  echo "[ERR] Не найден собранный файл: $PUBLISH_DIR/MetadataDel" >&2
  exit 1
fi

mkdir -p "$INSTALL_DIR"
cp "$PUBLISH_DIR/MetadataDel" "$INSTALL_DIR/$COMMAND_NAME"
chmod +x "$INSTALL_DIR/$COMMAND_NAME"

if [ "$COMMAND_NAME" != "mdel" ]; then ln -sf "$COMMAND_NAME" "$INSTALL_DIR/mdel"; fi

echo "[OK] Установлено:"
echo "     $INSTALL_DIR/$COMMAND_NAME"
echo "     $INSTALL_DIR/mdel"
echo

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *)
    echo "[INFO] Добавьте каталог в PATH, если команда не находится из нового терминала:"
    echo "       echo 'export PATH=\"$INSTALL_DIR:\$PATH\"' >> ~/.zshrc"
    echo "       source ~/.zshrc"
    echo
    ;;
esac

echo "Проверка:"
"$INSTALL_DIR/$COMMAND_NAME" --help >/dev/null
echo "  $COMMAND_NAME --help"
echo "  mdel --log --backup=on /path/to/file.pdf"

echo
METADATADEL_INSTALL_DIR="$INSTALL_DIR" METADATADEL_COMMAND_NAME="$COMMAND_NAME" bash "$ROOT_DIR/scripts/mac/install-finder-action.sh"
echo
echo "В Finder: правый клик по файлу или папке -> Быстрые действия -> Удалить метаданные (MetadataDel)"
