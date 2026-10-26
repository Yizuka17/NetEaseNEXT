#!/usr/bin/env bash
# Build only the Android player preview, without modifying the Windows project.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/android-player/generated/NetEaseNEXTMobile"
rm -rf "$OUT"
dotnet new unoapp \
  -o "$OUT" \
  -preset blank \
  -platforms android \
  -tfm net10.0 \
  -renderer native \
  -theme fluent

PROJECT="$(find "$OUT" -name NetEaseNEXTMobile.csproj -type f -print -quit)"
if [ -z "$PROJECT" ]; then
  echo "Uno template did not create the expected Android project" >&2
  exit 1
fi
APP="$(dirname "$PROJECT")"
test -f "$APP/MainPage.xaml"
mkdir -p "$APP/Models" "$APP/Services"
cp "$ROOT/NetEaseNEXT/Models/Music.cs" "$APP/Models/Music.cs"
cp "$ROOT/android-player/MainPage.xaml" "$APP/MainPage.xaml"
cp "$ROOT/android-player/MainPage.xaml.cs" "$APP/MainPage.xaml.cs"
cp "$ROOT/android-player/Services/BridgeClient.cs" "$APP/Services/BridgeClient.cs"
cp "$ROOT/android-player/Services/AndroidPlayback.cs" "$APP/Services/AndroidPlayback.cs"

# Android needs internet and, strictly for USB debugging, loopback HTTP.
MANIFEST="$APP/Platforms/Android/AndroidManifest.xml"
python3 - "$MANIFEST" <<'PY'
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

file = Path(sys.argv[1])
uri = "http://schemas.android.com/apk/res/android"
ET.register_namespace("android", uri)
tree = ET.parse(file)
root = tree.getroot()
app = root.find("application")
if app is None:
    raise RuntimeError("AndroidManifest.xml is missing <application>")
app.set("{" + uri + "}usesCleartextTraffic", "true")
if not any(n.get("{" + uri + "}name") == "android.permission.INTERNET"
           for n in root.findall("uses-permission")):
    root.insert(0, ET.Element("uses-permission", {"{" + uri + "}name": "android.permission.INTERNET"}))
tree.write(file, encoding="utf-8", xml_declaration=True)
PY

# No fast deployment: artifact APK must be installable from GitHub Actions.
dotnet build "$PROJECT" \
  -f net10.0-android \
  -c Debug \
  -p:AndroidPackageFormats=apk \
  -p:EmbedAssembliesIntoApk=true

echo "Built APK:"
find "$APP/bin/Debug" -type f -name '*.apk' -print
