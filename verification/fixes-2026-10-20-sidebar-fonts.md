# 侧栏图标来源与主题颜色

2026-10-20，0.2.6.0，本机 Windows 10 22H2，x64。对应 [GitHub Release v0.2.6](https://github.com/Yizuka17/NetEaseNEXT/releases/tag/v0.2.6) 的源码与签名安装包。

## 为什么 0.2.5 仍有系统差异

0.2.5 内置了图标字体，但没有让所有应用图标都明确使用它。侧栏普通态仍使用 `Icon="People"` 等创建的 SymbolIcon，选中态混用字体图标及手绘 PathIcon。全局 SymbolThemeFontFamily 的覆盖没有改变这些 SymbolIcon 的实际内部字体：

[修复前运行记录](sidebar-theme-before-2026-10-20.json)实测“动态”、发现、歌单、播客及队列的普通态均是 SymbolIcon，内部 TextBlock 的 FontFamily 为 `Segoe Fluent Icons,Segoe MDL2 Assets`。因此仍会随系统字体环境变化。上轮仅核对显式 FontIcon 的字体，没有覆盖这个内部路径。

WinUI 3 提供 SymbolIcon、FontIcon 等控件，SymbolIcon 没有统一的 Filled 属性来切换所有图标的实心/空心。微软 [Fluent System Icons](https://github.com/microsoft/fluentui-system-icons) 提供官方 Regular 与 Filled 两套图形，可随应用内置；[SymbolIcon 文档](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.symbolicon?view=windows-app-sdk-1.8)说明了系统图标字体与回退。

## 黑色图标及修复范围

用户截图中选中的“我的播客”对应旧手绘 PathIcon。该实现通过替换 Icon 对象切换选中态，没有显式保留图标与导航项前景色的绑定；设置项还会在 WinUI 模板加载时被重置为默认 AnimatedIcon。

本机常规深浅主题切换没有稳定复现原截图的黑色，所以不把它记为已完整复现的单一根因。已确认的字体来源、混合图标实现和设置项图标重置问题均已修复：

- 每个侧栏项保留同一个 FontIcon，切换时只改变 Glyph。所有普通态及选中态均来自包内微软官方 Regular/Filled 图形，删除手绘选中态。
- 以单向绑定将图标 Foreground 连接到导航项 Foreground，不固定为白色，跟随深色、浅色及导航项主题颜色。
- 保留包内设置图标，避免 Native NavigationView 后续创建默认 AnimatedIcon 时覆盖它；现有设置旋转和隐藏后停止机制继续使用。
- 增加不带系统回退的 AppIconFontFamily，应用自行定义的 FontIcon/FontIconSource 显式使用该资源。搜索、表情、图片切换按钮也不再创建 SymbolIcon。
- WinUI 原生控件模板保留独立的 SymbolThemeFontFamily 回退以处理未包含的符号；窗口标题栏、系统菜单仍由 Windows 绘制。因此不能把“应用图标已内置”解释为所有系统界面绘制也被替换。

## 验证

- 静态核对：31 个 XAML FontIcon 均指定 AppIconFontFamily，所用字形全部存在于包内字体；应用源码没有残留 `new SymbolIcon(...)`。字体子集为 73 个码位，11,252 字节，保留 MIT 许可与来源哈希。
- [字体检查](font-strict-2026-10-20.json)：8 项通过。
- [侧栏尺寸及旋转检查](sidebar-layout-strict-2026-10-20.json)：20 项通过。
- [主题第 1 轮](sidebar-theme-2026-10-20-run1.json)、[第 2 轮](sidebar-theme-2026-10-20-run2.json)、[第 3 轮](sidebar-theme-2026-10-20-run3.json)：每轮 44 项通过，覆盖 7 个侧栏项的普通/选中态，以及深色→浅色→深色共 42 组状态。核对实际墨迹颜色、无系统回退的字体来源、Icon 对象保持不变及前景色绑定仍存在。
- 已查看实际 WinUI 深浅主题渲染，播客选中态在深色中为白色，在浅色中为深色。渲染记录见本地 `sidebar-theme-2026-10-20/after-dark.png` 和 `after-light.png`。这些诊断图用于核对图标，不当作完整窗口布局验收。
- [完整回归](integration-sidebar-theme-2026-10-20.json)：136 项通过；10 项 Node 接口测试通过。构建保留原有 Player.cs CS1998 警告。
- 未在 Windows 11 实机运行，不宣称跨系统像素完全相同；通过显式包内字形消除了依赖系统字体选择的路径。

签名产物：`NetEaseNEXT-0.2.6.0-x64.msix`。[包检查](msix-sidebar-theme-2026-10-20.json)记录发布前签名与字体、程序、XAML 及许可证载荷哈希。本轮构建验收未安装该包；包检查 JSON 中的发布状态保留检查当时的值。
