# NetEaseNEXT Android: QR login and single-player preview

Experimental Uno Platform Android app. The existing Windows WinUI 3 project
is unchanged, and its song model is reused from NetEaseNEXT/Models/Music.cs.

## What works in the preview

- Display a NetEase Cloud Music QR login code (scan it with the **NetEase Cloud
  Music app on another device**, then confirm).
- Keep the authenticated cookie **only inside the PC's running Node process**.
  The Android client never receives, stores or forwards the account cookie.
- Display the signed-in user's first 50 playlists. Selecting a playlist loads
  its first 100 tracks. Tap a track to play it in Android MediaPlayer; play,
  pause, stop and seek are supported. Streaming respects service-side access.
- The app is *not standalone*: it needs the PC's USB-only Node API bridge,
  including during account login and when changing songs.
- No password input, downloads, lyrics, playlists editing, background playback,
  MediaSession or release signing. No permanent login session is saved: stop
  the bridge to clear authentication, then scan again on next launch.

## Get APK from Actions

Open the green **Android player preview** job under GitHub Actions, and
download the artifact **NetEaseNEXT-Android-player-debug**. Unzip to retrieve
the Debug APK, then install on an Android phone with USB debugging enabled.
GitHub Actions builds with .NET 10, Uno, and the Android workload.

## Start USB preview server

Install Node.js 22+ and Android platform-tools on the PC. From repository root:

    npm ci --prefix api
    node android-player/server.mjs

Connect the Android phone by USB and approve debugging, then in a second
terminal execute:

    adb devices
    adb reverse tcp:32157 tcp:32157

Launch the APK, press 获取登录二维码, scan with NetEase Cloud Music on
**another device**, approve the login, and select one of your playlists.
Keep the PC server running while using the Android app. Closing the server
clears its in-memory login session.

Security scope: the server binds to 127.0.0.1 and has a fixed allowlist;
it accepts no arbitrary upstream methods or client-supplied cookies, and
never returns the login cookie to the phone. The developer build enables
HTTP only for loopback USB debugging, so do not publish or expose this bridge
on a LAN. USB reverse mapping may still be accessible to other apps on the
debugging phone; do not treat this as a production secure login design.

## Local build (optional)

    dotnet workload install android
    dotnet new install Uno.Templates
    bash android-player/build.sh

Output is in android-player/generated/, excluded from version control.
