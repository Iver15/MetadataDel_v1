#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")"/../.. && pwd)"
cd "$ROOT_DIR"

APP_NAME="MetadataDel"
PRODUCT_NAME="MetadataDel"
VERSION="${METADATADEL_VERSION:-2.4.1}"
RID="${METADATADEL_RID:-}"
DIST_DIR="$ROOT_DIR/dist/mac"
APP_DIR="$DIST_DIR/$APP_NAME.app"
RESOURCES_DIR="$APP_DIR/Contents/Resources"
DMG_DIR="$DIST_DIR/dmg-root"
TOOLS_DIR="$DIST_DIR/tools"
STAGING_DMG_PATH="$DIST_DIR/${PRODUCT_NAME}-${VERSION}-${RID:-host}.staging.dmg"
DMG_PATH="$DIST_DIR/${PRODUCT_NAME}-${VERSION}-${RID:-host}.dmg"
SIGN_IDENTITY="${SIGN_IDENTITY:-}"
NOTARY_PROFILE="${NOTARY_PROFILE:-}"
WAIT_FOR_NOTARIZATION="${WAIT_FOR_NOTARIZATION:-0}"
DMG_SIZE="${DMG_SIZE:-300m}"

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
case "$RID" in
  osx-arm64) APP_ARCH="arm64" ;;
  osx-x64) APP_ARCH="x86_64" ;;
  *) echo "[ERR] Неподдерживаемый RID: $RID" >&2; exit 1 ;;
esac
DMG_PATH="$DIST_DIR/${PRODUCT_NAME}-${VERSION}-${RID}.dmg"
STAGING_DMG_PATH="$DIST_DIR/${PRODUCT_NAME}-${VERSION}-${RID}.staging.dmg"

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

echo "[INFO] Сборка CLI для $RID..."
PUBLISH_DIR="$DIST_DIR/publish-$RID"
rm -rf "$PUBLISH_DIR"
"$DOTNET_BIN" publish ./MetadataDel.MacCli/MetadataDel.MacCli.csproj \
  -c Release -r "$RID" \
  -p:PublishSingleFile=true -p:SelfContained=true \
  -o "$PUBLISH_DIR"

if [ ! -f "$PUBLISH_DIR/MetadataDel" ]; then
  echo "[ERR] Не найден собранный файл: $PUBLISH_DIR/MetadataDel" >&2
  exit 1
fi

echo "[INFO] Сборка $APP_NAME.app..."
rm -rf "$APP_DIR"
mkdir -p "$APP_DIR/Contents/MacOS" "$RESOURCES_DIR/bin" "$RESOURCES_DIR/scripts/mac" "$TOOLS_DIR"

cp "$PUBLISH_DIR/MetadataDel" "$RESOURCES_DIR/bin/metadatadel"
chmod +x "$RESOURCES_DIR/bin/metadatadel"
cp scripts/mac/integration-safety.sh "$RESOURCES_DIR/scripts/mac/integration-safety.sh"
cp scripts/mac/install-app-integration.sh "$RESOURCES_DIR/scripts/mac/install-app-integration.sh"
cp scripts/mac/uninstall.sh "$RESOURCES_DIR/scripts/mac/uninstall.sh"
cp scripts/mac/finder-action.sh "$RESOURCES_DIR/scripts/mac/finder-action.sh"
cp scripts/mac/install-finder-action.sh "$RESOURCES_DIR/scripts/mac/install-finder-action.sh"
cp scripts/mac/uninstall-finder-action.sh "$RESOURCES_DIR/scripts/mac/uninstall-finder-action.sh"
chmod +x "$RESOURCES_DIR/scripts/mac/"*.sh

"${PYTHON:-python3}" scripts/mac/installer/render_icon.py "$DIST_DIR/AppIcon.iconset"
iconutil -c icns "$DIST_DIR/AppIcon.iconset" -o "$RESOURCES_DIR/AppIcon.icns"

sed "s#<string>2.3.0</string>#<string>$VERSION</string>#g" \
  scripts/mac/installer/Info.plist > "$APP_DIR/Contents/Info.plist"

clang -arch "$APP_ARCH" -mmacosx-version-min=12.0 -fobjc-arc scripts/mac/app/*.m \
  -framework Cocoa -framework UniformTypeIdentifiers \
  -o "$APP_DIR/Contents/MacOS/$APP_NAME"
clang -fobjc-arc scripts/mac/installer/set_file_icon.m \
  -framework Cocoa \
  -o "$TOOLS_DIR/set-file-icon"

chmod +x "$APP_DIR/Contents/MacOS/$APP_NAME"

if [ -n "$SIGN_IDENTITY" ]; then
  echo "[INFO] Подпись приложения: $SIGN_IDENTITY"
  codesign --force --options runtime --timestamp --entitlements scripts/mac/app/dotnet.entitlements --sign "$SIGN_IDENTITY" "$RESOURCES_DIR/bin/metadatadel"
  codesign --force --strict --options runtime --timestamp --sign "$SIGN_IDENTITY" "$APP_DIR"
else
  echo "[INFO] Подпись приложения ad-hoc"
  codesign --force --deep --sign - "$APP_DIR"
fi

codesign --verify --deep --strict --verbose=2 "$APP_DIR"

echo "[INFO] Упаковка DMG..."
rm -rf "$DMG_DIR" "$DMG_PATH" "$STAGING_DMG_PATH"
mkdir -p "$DMG_DIR"
cp -R "$APP_DIR" "$DMG_DIR/"
ln -s /Applications "$DMG_DIR/Applications"

hdiutil create \
  -volname "$PRODUCT_NAME" \
  -srcfolder "$DMG_DIR" \
  -ov \
  -format UDRW \
  -fs HFS+ \
  -size "$DMG_SIZE" \
  "$STAGING_DMG_PATH"

mount_dir="$(mktemp -d)"
cleanup_mount() {
  hdiutil detach "$mount_dir" >/dev/null 2>&1 || true
  rmdir "$mount_dir" 2>/dev/null || true
}
trap cleanup_mount EXIT
hdiutil attach "$STAGING_DMG_PATH" -mountpoint "$mount_dir" -nobrowse -readwrite >/dev/null
cp "$RESOURCES_DIR/AppIcon.icns" "$mount_dir/.VolumeIcon.icns"
SetFile -a C "$mount_dir"
SetFile -a V "$mount_dir/.VolumeIcon.icns"
sync
hdiutil detach "$mount_dir" >/dev/null
rmdir "$mount_dir"
trap - EXIT

hdiutil convert "$STAGING_DMG_PATH" \
  -format UDZO \
  -imagekey zlib-level=9 \
  -o "$DMG_PATH"
rm -f "$STAGING_DMG_PATH"

"$TOOLS_DIR/set-file-icon" "$DMG_PATH" "$RESOURCES_DIR/AppIcon.icns"
SetFile -a C "$DMG_PATH" || true
touch "$DMG_PATH"

if [ -n "$SIGN_IDENTITY" ]; then
  echo "[INFO] Подпись DMG"
  codesign --force --sign "$SIGN_IDENTITY" "$DMG_PATH"
  codesign -dv --verbose=2 "$DMG_PATH" >/dev/null
fi

if [ -n "$NOTARY_PROFILE" ]; then
  echo "[INFO] Отправка на notarization"
  if [ "$WAIT_FOR_NOTARIZATION" = "1" ]; then
    xcrun notarytool submit "$DMG_PATH" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG_PATH"
    xcrun stapler validate "$DMG_PATH"
    spctl -a -vvv -t install "$DMG_PATH"
  else
    xcrun notarytool submit "$DMG_PATH" --keychain-profile "$NOTARY_PROFILE"
  fi
fi

echo "[OK] DMG готов:"
echo "     $DMG_PATH"
