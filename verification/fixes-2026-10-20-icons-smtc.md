# Release 拆包、内置图标字体与 SMTC

2026-10-20，Windows 10 22H2（19045），x64。远端 main 已更新到 `5243871`。本轮修改仍在工作区，未提交或推送。

## 工作目录

源码及 Git 仓库已迁移到 `E:\NetEaseNEXT_Dev`，包含未提交修改、本地签名绑定和构建产物。原 `E:\NetEaseNEXT` 已为空，仍被当前 Codex 工作目录句柄占用，无法移除空目录。后续构建均在新路径完成。

目录同名不会使已安装 MSIX 自动加载开发目录文件。实际运行的 0.2.3 位于 `C:\Program Files\WindowsApps\NetEaseNEXT_0.2.3.0_x64__1wr5cf65fxq72`，其包家族及应用标识均指向已安装包。

## Release 拆包结果

从 GitHub Release 下载的 `NetEaseNEXT-0.2.2.0-x64.msix` SHA256 为 `88CC73C063170D30F5C30A279B3310274552164E59D440EACBBB0F9076DD4703`。

- 4 个应用 ICO/PNG 资源与本地原 0.2.3 完全相同。
- 87 个 Shell PNG 的压缩文件字节不同，但解码后的全部 RGBA 像素相同，见[像素核对](release-icon-pixels-2026-10-20.json)。不能把 PNG 压缩差异解释为图标设计变化。
- WinUI 运行库、App.xbf 相同。MainWindow.xbf 仅源文件哈希区域有差异；反编译出的侧栏选中态和 SMTC 初始化逻辑与源码一致。

本机没有安装 Segoe Fluent Icons，只有 Segoe MDL2 Assets 等旧图标字体。应用使用系统字体时会回退，截图中的轮廓播放按钮符合这一差异。[Microsoft 的图标字体说明](https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font)也区分 Windows 10 与 Windows 11 的字体可用性。

## 随包字体

新增 `Assets/Fonts/NetEaseNEXTIcons.ttf`，大小 10,184 字节、66 个码位映射，覆盖应用使用的图标及常用 WinUI Symbol 别名。播放、侧栏、弹出提示及代码动态创建的图标统一使用该字体；更改正文字体不再影响图标。原生控件使用而未包含的码位保留系统字体回退。

字体来自 Microsoft [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) 的 MIT 许可项目，固定提交 `83b26348c811f072429427ff48362238656524fe`，保留许可证和来源哈希。没有分发禁止再分发的 Segoe Fluent Icons 文件。此处采用 Fluent System Icons 的对应图形，不承诺与 Segoe 每一笔完全一致。

生成工具为 `scripts/generate-icon-font.py`，依赖 fonttools 4.67.0；常规构建直接复制已生成字体，无需下载或安装字体工具。顺序/单曲循环、静音和全屏状态按相应语义映射。

[字体专项](bundled-font-2026-10-20.json) 8 项通过。以下为真实 WinUI 渲染：上排内置字体，下排本机 MDL2。

![内置字体与旧系统字体对比](bundled-font-preview-2026-10-20.png)

## SMTC

用户确认的现象是系统卡片只有应用标识，没有歌曲信息。本轮发现并修复两处明确问题：

1. 恢复上次播放队列时只恢复播放器状态与时间线，没有发布曲名、歌手、专辑。现在恢复和实际换曲共用元数据发布路径，恢复仍保持暂停；没有当前歌曲时禁用空会话，并清理旧元数据。
2. 原实现把 HTTP 封面 URI 直接交给系统。实际全局媒体会话的 Thumbnail 为 null。现在异步取得本地缓存文件，通过 `RandomAccessStreamReference.CreateFromFile` 发布；换曲取消旧封面任务，检查当前曲目及请求身份，避免旧图覆盖新曲目。

[SMTC 专项](smtc-controls-2026-10-20.json) 15 项通过。测试通过 Windows 全局媒体会话 API 读取真实曲名、歌手、专辑和封面流，并从系统会话发送播放、暂停、下一首、跳转进度命令，确认播放器响应；隐藏后命令仍生效，恢复暂停队列也能发布歌曲及封面。测试静音执行。

应用仍只维护一个窗口 SMTC；备用解码器关闭自动 CommandManager，避免多个预解码器生成多张媒体卡片。[Microsoft 的 SMTC 文档](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/integrate-with-systemmediatransportcontrols)说明多个自动接入的 MediaPlayer 会产生独立系统媒体会话。

### 验证边界

本机全局会话 SourceAppUserModelId 仍返回 `NetEaseNEXT.exe`，而不是期望的 AUMID。已安装 0.2.3 本身的包身份正确，歌曲信息也可由全局 API 读出，因此不能把 Source 标识差异当作“歌曲信息已修复”的证明。新版检查直接寻找本次测试曲目，再验证其控制命令，另行记录标识差异，没有伪造通过结果。

使用 [computer-use 技能](C:/Users/SANSH/.codex/plugins/cache/openai-bundled/computer-use/26.1013.40934/skills/computer-use/SKILL.md)读取了已安装播放器的界面文本，确认 Space Oddity 及歌手。截图捕获两次失败，恢复后仍为 `window capture timed out: timed out waiting on channel`；系统媒体卡片画面未能目视验收。当前安装版本仍为 0.2.3，本轮未安装 0.2.4，也未改变证书信任。用户看到的“只有标识”是否完全消失，仍需新包安装后的实际系统卡片验收。

## 构建与回归

- Release 构建成功，保留既有 `Player.cs` CS1998 警告。
- 9 项 Node 接口测试、6 项真实网络检查通过。
- [完整应用回归](integration-font-smtc-2026-10-20.json) 137 项通过，包含播放缓存、预解码、SMTC 元数据、歌词、后台播放、托盘恢复及布局检查。
- 托盘恢复保留同一封面、背景、头像和歌词对象。首次失败来自测试跳过生产账号初始化、在恢复时首次载入头像；测试现先完成首次头像加载再取快照，未通过绕开账号更新来掩盖问题。
- 后台播放 20.003 秒样本累计主进程 CPU 109.375 ms，工作集 270.2 MiB，私有内存 198.2 MiB；仅代表本次完整回归后的样本。

## 签名 MSIX

产物：`E:\NetEaseNEXT_Dev\artifacts\msix\NetEaseNEXT-0.2.4.0-x64.msix`。

复用本机生成的长期签名密钥，证书有效期至 2043-10-20，指纹 `548494EE74C33B2A77C73CC4E25CE0EEDD5E8733`。私钥保留在当前用户证书库，随包 CER 仅含公钥。

[包检查](msix-font-smtc-2026-10-20.json)确认 Authenticode 为 Valid，包签名、CER 与本地绑定一致；实际 MSIX 中应用 DLL、PRI、字体和字体许可均与经过测试的发布目录 SHA256 一致。[Shell 资源检查](msix-font-icons-2026-10-20.json) 5 项通过。

包 SHA256：`6FCAEBAC5E377269ABFA63B0D62EB2BF544184C409FDC8AC67519A988DA805E3`。同目录附 `INSTALL.txt` 和 `SHA256SUMS.txt`。
