#!/bin/bash
# Builds Remotely for macOS as universal binaries (Intel + Apple silicon) and packages them: Remotely.app (viewer), Remotely Host.app, remotely-cli, and a .dmg.
#   mac/build.sh [version]
# Needs a Mac with Xcode or the Command Line Tools (Swift 5.9+). The apps are signed ad hoc (no Apple developer account): the first launch needs
# right-click > Open, and macOS asks again for Screen Recording / Accessibility after each rebuild because an ad hoc signature changes.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-0.4.0}"
OUT="$PWD/dist"
rm -rf "$OUT" && mkdir -p "$OUT"

swift build -c release --arch arm64 --arch x86_64
BIN=".build/apple/Products/Release"

plist() { # name executable identifier agent(true|false)
cat <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>$1</string>
  <key>CFBundleDisplayName</key><string>$1</string>
  <key>CFBundleExecutable</key><string>$2</string>
  <key>CFBundleIdentifier</key><string>$3</string>
  <key>CFBundleIconFile</key><string>Remotely</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSUIElement</key>$4
  <key>NSScreenCaptureUsageDescription</key><string>Remotely Host shows this screen to your other devices, through your own server, when you connect to this Mac.</string>
</dict></plist>
PLIST
}

bundle() { # appdir name executable-in-bin executable-name identifier agent
  local app="$1"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/Fonts"
  cp "$BIN/$3" "$app/Contents/MacOS/$4"
  cp Remotely.icns "$app/Contents/Resources/"
  cp Fonts/*.ttf Fonts/OFL-IBM-Plex.txt "$app/Contents/Resources/Fonts/"
  plist "$2" "$4" "$5" "$6" > "$app/Contents/Info.plist"
  codesign --force --deep --sign - "$app"
}

bundle "$OUT/Remotely.app" "Remotely" RemotelyViewerMac Remotely com.remotely.viewer "<false/>"
bundle "$OUT/Remotely Host.app" "Remotely Host" remotely-host-mac remotely-host-mac com.remotely.host "<true/>"
cp "$BIN/remotely-cli" "$OUT/remotely-cli"

cat > "$OUT/READ ME FIRST.txt" <<TXT
Remotely $VERSION for macOS (Intel and Apple silicon)

1. Drag "Remotely.app" (control other computers) and/or "Remotely Host.app" (let your devices control THIS Mac) to Applications.
2. Open them with right-click > Open the first time (they are signed ad hoc, not by Apple).
3. Remotely Host asks for two permissions: Screen Recording and Accessibility (System Settings > Privacy & Security). Allow both, then reopen it.
4. In Remotely Host, enter your server address, key pin and a pairing code (from "Remotely Server info" on your server).
   To start it at login: Applications/Remotely Host.app/Contents/MacOS/remotely-host-mac install
5. macOS cannot show the login screen to a remote viewer. The host works while someone is signed in; turn on automatic login for an unattended Mac.

remotely-cli is a command-line viewer for scripting and testing.
TXT

hdiutil create -volname "Remotely $VERSION" -srcfolder "$OUT" -ov -format UDZO "$PWD/Remotely-$VERSION-mac.dmg" >/dev/null
for f in "$OUT/Remotely.app/Contents/MacOS/Remotely" "$OUT/Remotely Host.app/Contents/MacOS/remotely-host-mac" "$OUT/remotely-cli"; do
  archs="$(lipo -archs "$f")"; echo "$f: $archs"
  case "$archs" in *x86_64*arm64*|*arm64*x86_64*) ;; *) echo "not universal: $f"; exit 1 ;; esac
done
echo "Built $PWD/Remotely-$VERSION-mac.dmg"
