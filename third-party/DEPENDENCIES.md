# Native C# dependencies

The app uses .NET 9, Windows App SDK and Win2D. Their distribution notices are
included in this directory. There is no Node.js runtime in the app package.

- NetEase protocol mapping and encryption were ported from
  `@neteasecloudmusicapienhanced/api` 4.41.1. Original project:
  https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced . MIT license:
  `netease-api/LICENSE`. No alternative music sources are enabled.
- QRCoder 1.8.1: https://github.com/codebude/QRCoder/tree/v1.8.1 . MIT license:
  `qrcoder/LICENSE`.
- BouncyCastle.Cryptography 2.7.0: https://github.com/bcgit/bc-csharp/tree/release-2.7.0 .
  MIT license: `bouncycastle/LICENSE`. Used for X25519.
- TagLibSharp 2.3.0: https://github.com/mono/taglib-sharp . LGPL-2.1-only:
  `taglibsharp/COPYING`, authors: `taglibsharp/AUTHORS`.
  Distributed as an unmodified separate `TagLibSharp.dll`; users may replace it
  with a compatible build. Corresponding upstream source release:
  https://github.com/mono/taglib-sharp/releases/tag/TaglibSharp-2.3.0.0 .
  No modifications are made to that library. The app is not single-file bundled.
- Fluent System Icons sources, fixed version and MIT license are under
  `fluent-system-icons`.

The optional "MusicUnlock-style NetEase direct" source independently implements
the official NetEase GET endpoint and bitrate fallback described in
https://github.com/Lin-M11/MusicUnlock/blob/58c4f9b6e13ffc8dd8bd86273f39fe9f585ec428/src/main/kotlin/musicunlock/ncm/NeteaseApi.kt
and `NeteaseProvider.kt`. No MusicUnlock executable, Kotlin runtime, player
implementation or assets are copied or distributed. Requests use music.163.com;
the user's NetEase session is never forwarded to another provider.

`NODE-LICENSE.txt` is retained only as a notice for historical versions; it does
not indicate a runtime dependency in this release.
