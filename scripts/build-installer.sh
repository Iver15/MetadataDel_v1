#!/usr/bin/env bash

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_BIN="${DOTNET_BIN:-$HOME/.dotnet/dotnet}"

if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "dotnet not found at $DOTNET_BIN" >&2
  echo "Install the full .NET 8 SDK first, for example:" >&2
  echo "  curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 8.0 --install-dir ~/.dotnet" >&2
  exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
  echo "docker CLI not found. Install Docker Desktop first." >&2
  exit 1
fi

if ! docker version >/dev/null 2>&1; then
  echo "docker daemon is not running. Start Docker Desktop and retry." >&2
  exit 1
fi

echo "Generating Windows icon..."
python3 "$ROOT_DIR/scripts/render_windows_icon.py" "$ROOT_DIR/icon.svg" "$ROOT_DIR/MetadataDel.Cli/app.ico"
python3 "$ROOT_DIR/scripts/render_installer_images.py" "$ROOT_DIR/icon.svg" "$ROOT_DIR/installer/images"

echo "Publishing win-x64 build..."
"$DOTNET_BIN" publish "$ROOT_DIR/MetadataDel.Cli/MetadataDel.Cli.csproj" \
  -c Release \
  -r win-x64 \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:SelfContained=true \
  -o "$ROOT_DIR/publish"

echo "Building Inno Setup installer..."
docker run --rm -i -v "$ROOT_DIR:/work" amake/innosetup installer/MetadataDel.iss

echo "Done. Installer is in:"
ls -1 "$ROOT_DIR"/installer/MetadataDel-Setup-*.exe
