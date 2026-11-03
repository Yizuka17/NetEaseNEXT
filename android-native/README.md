# NetEaseNEXT Android native C# player

This is a direct Android prototype using the current native C# implementation
in NetEaseNEXT.Api. It does not use Node.js, any PC service, USB, adb reverse,
or an HTTP bridge. It shares NetEaseNEXT/Models/Music.cs and uses the Android
MediaPlayer and an Uno native-renderer mobile UI.

Features: scan QR with another NetEase Cloud Music device; view the first 50
playlists and first 100 tracks per playlist; select a song to stream, pause,
resume, seek or stop; artwork and song metadata. Playback respects account
entitlements and regional licensing.

The account cookie is held only in Android process memory (never stored in
source or an Actions secret) and is lost when the app process exits. Use a
second device to scan the login QR. There is no persistent login, background
playback/MediaSession, queue, local music or lyrics in this MVP.

GitHub Actions "Android native C# player" builds a Debug APK on this branch.
Download the NetEaseNEXT-Android-native-CSharp artifact from a successful run.

Build locally: install .NET 10, Uno templates and Android workload, then:

    dotnet workload install android
    dotnet new install Uno.Templates
    bash android-native/build.sh

The build script creates an Uno app, copies the existing Music.cs model and
injects a real ProjectReference to NetEaseNEXT.Api.csproj. The original
Windows WinUI project stays unchanged.

APK packaging success does not prove that live login and playback work on a
specific phone. Do not treat this preview as a finished music player.
