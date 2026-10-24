# 主题即时切换与曲目多选

2026-10-24，0.2.7.0 开发阶段，Windows 10 本机验证。该版本未单独发布，相关修复已纳入 [GitHub Release v0.2.8](https://github.com/Yizuka17/NetEaseNEXT/releases/tag/v0.2.8)。本报告保留该阶段的验收结果。

## 主题

评论与其他 ContentDialog 原先只有 XamlRoot，未显式同步主窗口的运行时主题。仅绑定 ActualTheme 也不能可靠收到更新：首轮真实 XAML 检查中，主界面切到浅色后打开中的评论仍停留在深色。

统一通过 ShowThemedDialogAsync 显示所有 11 处弹窗。在主窗口 ActualThemeChanged 时显式同步当前弹窗和打开的 Popup；浮层与歌曲菜单打开时也同步主题。系统颜色通知与 Windows 主题通知会重新读取应用颜色偏好，固定浅色/深色设置保持不变。

同时检查并补齐标题栏按钮颜色、歌词外观及播放页封面取色背景的刷新。封面背景复用图片缓存，窗口隐藏时不启动新的背景生成。主界面主题资源仍由 WinUI 处理。

相关平台行为参考微软 [ContentDialog 文档](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.contentdialog) 和 [主题资源说明](https://learn.microsoft.com/en-us/windows/apps/develop/ui/theming)。本报告的运行结果来自本机检查。

## 多选

- 歌单歌曲列表、完整播放列表及播放栏队列的原生右键/触摸长按菜单新增“多选”。从触发菜单的歌曲开始选中，显示原生复选框和操作栏。
- 全选当前已加载歌曲、清空选择、按显示顺序添加至下一首、批量收藏到自建歌单、依账号权限逐首下载。
- 自建歌单可一次请求移除多个不同歌曲；检查所有者、会话、页面和曲目，成功后更新数量与分页，失败时保留歌曲和选择。移除歌单歌曲不会修改播放队列。
- 队列可批量移出，保留当前歌曲；原始顺序与随机队列一起更新，播放状态通知和随机重排不会清空仍存在歌曲的选择。
- 多选时关闭单击与双击播放，空格留给原生选择操作。“完成”、Esc、导航、隐藏或关闭队列会退出相应多选状态。列表尚未全部加载时，按钮标为“全选已加载”。

## 验证与限制

- [主题与多选专项](theme-batch-2026-10-24.json)：40 项通过。保持评论弹窗打开，以修改 AppsUseLightTheme 并发送 Windows 设置变化通知模拟外部切换，覆盖深→浅→深；正文颜色实际为白色/深色，原评论和请求次数保持不变。检查也覆盖打开中的浮层与菜单、标题栏和缓存封面的背景切色。
- 专项使用真实 WinUI 控件和本地 Node 服务替身验证移除成功、拒绝、过期页面及所有者限制，没有写入真实账号歌单。
- [侧栏主题](sidebar-theme-batch-2026-10-24.json)：44 项通过。
- [完整回归](integration-theme-batch-2026-10-24.json)：136 项通过；Node 接口测试 10 项通过。构建保留已有 Player.cs CS1998 警告。
- 已查看[浅色评论](theme-batch-2026-10-24/theme-batch-comments-light.png)、[深色评论](theme-batch-2026-10-24/theme-batch-comments-dark.png)和[队列多选](theme-batch-2026-10-24/theme-batch-queue.png)实际渲染，截图只含测试数据。
- 未在 Windows 11 实机或物理触摸设备上验收；右键/长按使用原生 ContextFlyout，运行检查验证菜单与触摸 Holding 开关，未声称完成物理长按测试。批量收藏与下载复用现有实际接口，未对真实账号做批量写入或批量下载验收。

本地安装包为 `artifacts/msix/NetEaseNEXT-0.2.7.0-x64.msix`，继续使用 0.2.6 相同的签名证书。[包检查](msix-theme-batch-2026-10-24.json)确认签名有效，程序、XAML、PRI 与字体载荷和最终测试版本一致。SHA256：`1D8489A5A4AC74DBE860E794CCEE90E55A52DC6F08730F4B04738C8D31F895D9`。构建验收未安装该包。
