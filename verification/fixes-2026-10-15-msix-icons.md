# MSIX 图标资源路径修复

0.2.1.0 的 MakePri 配置以 `PackageAssets` 文件夹作为 seed，导致逻辑资源名变成 `Files/AppList.png`；清单引用的却是 `PackageAssets/AppList.png`。物理图片完整，但 Windows 无法按清单路径找到候选资源。通过 IShellItemImageFactory 读取实际应用图标，复现为默认白纸图标。

0.2.1.1 使用显式 `.resfiles` 清单保留 `PackageAssets/` 目录层级，同时只合并一次已有 WinUI PRI。图标绘制、深浅色图片、运行二进制和签名证书不变。

`scripts/test-package-icons.ps1` 现为打包流程的一部分：检查商店图标、44/150 图标的清单路径能在 PRI 中解析、每个候选文件存在，以及深浅色无底板候选资源。旧安装包的 5 项检查全部失败，修复后的暂存包和实际安装包全部通过。安装包检查结果见 `msix-icons-2026-10-15.json`。

本机使用原证书更新至 0.2.1.1。再次通过 Windows Shell 的 IShellItemImageFactory 读取实际应用图标，已返回正确的白色线条标志；前后提取结果保存在本机 `artifacts/icon-diagnosis/shell-before.png`、`shell-after.png`。没有删除系统图标缓存或重启 Explorer。

安装后的 4 项启动、队列及播放状态恢复检查通过，见 `msix-icon-startup-2026-10-15.json`。Get-StartApps 仍只返回一个 `NetEaseNEXT_1wr5cf65fxq72!App` 入口。包位于 `artifacts/msix/NetEaseNEXT-0.2.1.1-x64.msix`，签名证书指纹保持不变。
