# NetEaseNEXT

原生 C# / XAML WinUI 3 网易云音乐客户端，支持 Windows 10 2004 及以上。

## 使用

[下载安装包](https://github.com/Yizuka17/NetEaseNEXT/releases/latest)。安装时可选择目录。也可以解压便携包，运行 `NetEaseNEXT.exe`，保留目录内所有文件。

- 歌手 → 专辑 → 歌曲；搜索、歌单、播放列表均显示封面。
- 歌单详情展示封面、简介、标签和创建者，打开时有封面过渡。支持收藏歌单、歌单评论，以及创建公开或私密歌单。添加歌曲到歌单使用“文件夹＋”图标。
- 独立播放页：左侧封面、进度和控件，右侧双语歌词；封面取色仅作用于此页面。进入和退出时提供封面连接动画及控件过渡。全屏沿用相同页面，控件栏使用原生图标切换。F11 切换全屏，Esc 退出，Alt+← 返回，Ctrl+F 搜索。
- 手动滚动歌词后暂停自动跟随，点击“回到当前歌词”恢复。暂停时也可自由滚动。
- 播放栏控件居中。顺序点击切换并提示；播放列表面板再次点击可收起。支持收藏到自己的歌单、红心及真实红心总数、音质、输出设备和下载。音质按当前歌曲和账号权限灰显；下载仍受账号权限限制。
- 歌曲评论区支持阅读、分页、发表、回复和点赞。动态也可查看评论、评论和点赞；通知展示评论原文，“回复我的评论”和“@我”分开查看。
- 手机扫码登录。登录后显示头像和名称，点击可查看消息、私信。私信支持文字回复及歌曲、专辑、歌单分享；当前不提供图片发送。侧栏提供动态与电台，电台节目可直接播放。
- 设置可选字体、材质透明度、缓存目录、下载目录和关闭到托盘。
- 托盘后台播放，SMTC 系统媒体控制，单实例唤回。

空间音效、音频增强按钮打开当前输出设备的 Windows 设置，具体选项由系统和驱动提供。Mica 需要 Windows 11，其取色来自桌面壁纸，不等同于透视窗口后方。系统不支持所选材质时使用纯色背景。

## 构建

需要 Windows、.NET 9 SDK、Node.js 22+。默认 x64。Windows App SDK 与构建工具由 NuGet 还原。

```powershell
pwsh -NoProfile -File scripts/build.ps1 -Publish
pwsh -NoProfile -File scripts/build-installer.ps1
```

便携包：`artifacts/win-x64`。安装包：`artifacts/installer`。安装器使用 Inno Setup 6.7.3，编译脚本按需取得编译器，不更改系统安装。可用 `scripts/run.ps1` 运行开发构建。

## 验证

```powershell
pwsh -NoProfile -File scripts/verify.ps1 -Network
# 使用当前登录会话验证社交读取，不发送消息或写入互动
pwsh -NoProfile -File scripts/verify.ps1 -SocialRead
# 加测隐藏暂停状态和 Node 空闲退出
pwsh -NoProfile -File scripts/verify.ps1 -MeasureBackground
```

应用内检查覆盖真实 XAML、音频播放、歌词、暂停时列表滚动、全屏、歌手/专辑、电台及材质回退。扫码确认需用户本人完成。Windows 11 材质实际观感、触屏交互及系统音效因设备而异，见 `verification` 中的验证记录。

## 数据与性能

账号会话以 Windows DPAPI 加密，绑定当前 Windows 用户，位于 `%LOCALAPPDATA%/NetEaseNEXT`。配置和限长日志也保存在此处。自选缓存目录保存封面和 API 临时状态，不移动账号凭据；旧目录不自动删除。封面缓存按 256 MiB 上限在启动时整理。下载目录只写入用户主动下载的歌曲。

音频由 Windows MediaPlayer 播放。列表虚拟化；封面根据 DPI 解码，UI 声明 Per-Monitor V2。模糊背景仅在换曲或进入播放页时生成，不持续渲染动画。界面进度更新仅在窗口可见且播放时运行，隐藏后停止。API 使用私有管道，按需启动 Node，45 秒空闲退出，不监听端口，不启用替代音源。

## 参考与依赖

- [Myune Music](https://github.com/xiaobaimc/myune_music)：参考播放栏、封面与歌词布局、封面模糊和取色处理思路，独立以 WinUI 3 实现，未复制源码或资源。
- [无羁音乐播放器](https://github.com/LanZhan-Harmony/WindowsMusicPlayer-TheUntamedMusicPlayer)：参考原生 Fluent 交互与短时动画。
- [Enhanced API](https://github.com/NeteaseCloudMusicApiEnhanced/api-enhanced)，固定 npm 版本 4.41.1。
- Microsoft Windows App SDK、Win2D、.NET；分发的 Node 许可位于 `third-party`，npm 依赖许可保留在 `api/node_modules`。

当前采用免安装或普通安装器分发，无需开发证书。若需要签名开发证书，`scripts/new-certificate.ps1` 生成有效期 17 年的证书，私钥留在当前用户证书存储，不自动信任。
