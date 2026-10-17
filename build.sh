#!/bin/bash
# Сборка Notely.app в ~/Applications и подпись локальным сертификатом (разрешения macOS не слетают при пересборке).
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release
APP="$HOME/Applications/Notely.app"
pkill -x Notely 2>/dev/null && sleep 1 || true
rm -rf "$APP"; mkdir -p "$APP/Contents/MacOS"
cp .build/release/Notely "$APP/Contents/MacOS/Notely"
mkdir -p "$APP/Contents/Resources" && cp Resources/Notely.icns "$APP/Contents/Resources/Notely.icns"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>app.notely.desktop</string>
  <key>CFBundleName</key><string>Notely</string>
  <key>CFBundleDisplayName</key><string>Notely</string>
  <key>CFBundleExecutable</key><string>Notely</string>
  <key>CFBundleIconFile</key><string>Notely</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.1</string>
  <key>CFBundleVersion</key><string>$(date +%Y%m%d%H%M)</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Notely слушает твой микрофон, чтобы подсказки учитывали твои ответы.</string>
  <key>NSScreenCaptureUsageDescription</key><string>Notely слушает звук созвона и делает скриншоты задач по запросу.</string>
</dict></plist>
PLIST
codesign --force --sign "Notely Local Signing" --identifier app.notely.desktop "$APP"
codesign -dv "$APP" 2>&1 | grep -E "Identifier|Authority"
# ярлык на рабочем столе (симлинк на приложение)
[ -e "$HOME/Desktop/Notely.app" ] || ln -s "$APP" "$HOME/Desktop/Notely.app"
touch "$APP"
echo "OK: $APP"
