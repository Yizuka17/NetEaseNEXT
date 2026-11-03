#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/android-native/generated/NetEaseNEXTMobile"
rm -rf "$OUT"
dotnet new unoapp -o "$OUT" -preset blank -platforms android -tfm net10.0 -renderer skia -theme fluent -toolkit
PROJECT="$(find "$OUT" -type f -name NetEaseNEXTMobile.csproj -print -quit)"
test -n "$PROJECT"
APP="$(dirname "$PROJECT")"
test -f "$APP/MainPage.xaml"
mkdir -p "$APP/Models" "$APP/Services"
cp "$ROOT/NetEaseNEXT/Models/Music.cs" "$APP/Models/Music.cs"
cp "$ROOT/android-native/MainPage.xaml" "$APP/MainPage.xaml"
cp "$ROOT/android-native/MainPage.xaml.cs" "$APP/MainPage.xaml.cs"
cp "$ROOT/android-native/Services/MobileMusicClient.cs" "$APP/Services/MobileMusicClient.cs"
cp "$ROOT/android-native/Services/AndroidPlayback.cs" "$APP/Services/AndroidPlayback.cs"
python3 - "$PROJECT" "$ROOT" <<'PY'
from pathlib import Path
import os
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
project = Path(sys.argv[1])
api = Path(sys.argv[2]) / "NetEaseNEXT.Api" / "NetEaseNEXT.Api.csproj"
assert api.is_file()
relative = os.path.relpath(api, project.parent).replace(os.sep, "/")
source = project.read_text("utf-8")
assert "</Project>" in source
source = source.replace("</Project>", '<PropertyGroup><ApplicationId>com.yizuka17.neteasenext.androidpreview</ApplicationId></PropertyGroup>\n<ItemGroup><ProjectReference Include="' + escape(relative) + '" /></ItemGroup>\n</Project>')
project.write_text(source, "utf-8")
manifest = project.parent / "Platforms" / "Android" / "AndroidManifest.xml"
android = "http://schemas.android.com/apk/res/android"
ET.register_namespace("android", android)
doc = ET.parse(manifest)
root = doc.getroot()
app = root.find("application")
assert app is not None
# Only audio CDNs may use HTTP; API credentials still use HTTPS.
app.set("{" + android + "}usesCleartextTraffic", "true")
key = "{" + android + "}name"
if not any(e.get(key) == "android.permission.INTERNET" for e in root.findall("uses-permission")):
    root.insert(0, ET.Element("uses-permission", {key: "android.permission.INTERNET"}))
doc.write(manifest, encoding="utf-8", xml_declaration=True)
print("Direct C# API ProjectReference:", relative)
PY
dotnet build "$PROJECT" -c Debug -f net10.0-android -p:AndroidPackageFormats=apk -p:EmbedAssembliesIntoApk=true
find "$APP/bin/Debug" -name '*.apk' -type f -print
