# NetEaseNEXT Android: single player preview

The Windows WinUI 3 project is unchanged. This directory is an experimental Uno
Platform Android app generated during CI, with the original C# song model linked
from NetEaseNEXT/Models/Music.cs.

## Scope

One screen only: enter a NetEase Cloud Music song ID, fetch its public song
metadata and authorized stream URL, display title/artist/artwork, play/pause,
seek and stop using Android MediaPlayer. It has no login, search, playlists,
lyrics, background media session or downloading.

This is **not** a standalone NetEase Cloud Music client. Because the desktop
project currently hosts the Enhanced API in a local Node subprocess, the
Android preview needs a local USB debugging bridge. No account cookie is
accepted by the bridge; songs unavailable without login remain unavailable.
Audio comes from the original provider, subject to its permissions.

## Build with GitHub Actions

Open **Actions -> Android player preview**, select a completed green run, and
download the artifact named **NetEaseNEXT-Android-player-debug**. Unzip it to
find the APK and install it on an Android device with USB debugging enabled.
This is a developer-signed Debug APK, not an app-store release.

The workflow runs on pushes to the preview branch and on pull requests. After
merging to main, you can also use Run workflow.

## Run the read-only API bridge on the PC

Install Node.js 22+ and Android platform-tools, then run from the repository:

    cd api
    npm ci
    cd ..
    node android-player/server.mjs

With the phone connected and USB debugging accepted:

    adb devices
    adb reverse tcp:32157 tcp:32157

Install the APK and launch it. Paste the numeric ID from a NetEase Cloud Music
song URL and tap 加载并播放. Keep USB connected and the Node service running
while loading songs. Playback may continue after the initial lookup while the
stream URL remains valid, but this does not implement offline listening.

The bridge listens only on 127.0.0.1. Android cleartext network traffic is
enabled strictly for this development preview; do not publish this build or
expose the bridge beyond loopback.

## Build locally (optional)

On Linux/macOS with .NET 10 and the Android SDK/workload, run:

    dotnet workload install android
    dotnet new install Uno.Templates
    bash android-player/build.sh

Build output is placed under android-player/generated/ (not tracked).
