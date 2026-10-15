# MSIX 与后台缓存

- 默认安装包改为自包含 MSIX 0.2.1.0，使用 Windows SDK 的 MakePri、MakeAppx 和 SignTool；便携发布仍可用。CI 构建明确标记为 unsigned 的验证包，本机默认使用绑定证书签名。
- 生成并绑定 17 年 RSA 3072 / SHA-256 签名证书，发布者为 `CN=NetEaseNEXT Development`，到期日为 2043-10-15。私钥保留在当前用户证书存储，源码及安装包不包含私钥；公钥 `.cer` 随包提供。再次运行生成脚本复用绑定，不自动轮换证书。
- 打包版使用 Windows 注册的 `NetEaseNEXT_1wr5cf65fxq72!App` 身份，不再自行注册旧 AUMID、创建开始菜单快捷方式或设置 EXE 重启命令。系统负责安装目录和唯一开始菜单入口。
- 迁移顺序为签名验证、MSIX 安装验证、旧安装版卸载、按原始目标清理两个旧快捷方式。原来的根目录快捷方式与后续的子目录快捷方式造成重复入口；实机迁移后 Get-StartApps 只剩一个 MSIX 入口。
- 显式禁用 AppData 文件写入虚拟化，沿用既有 DPAPI 登录态、配置、播放状态与缓存路径。未清除用户数据。第一次侧载需要管理员确认，将当前签名的公钥证书导入本机 TrustedPeople；已在本机完成，SignTool /pa 验证成功。
- 隐藏窗口继续停止 UI 定时器、动画、消息轮询、额外缓存下载并取消无用请求，保留完成的头像、封面、歌词与列表绑定。封面复用缓存不主动清空，同一图片恢复不重新解码；相同封面和主题不重复生成模糊背景。备用播放器仍会释放，以停止额外媒体工作。

验证：

- 新发布的便携版通过 131 项运行检查，包含隐藏/恢复对象复用和后台采样，见 `integration-msix-background-2026-10-15.json`。20 秒隐藏播放样本中本进程累计 CPU 为 359.375 ms，工作集 299.3 MiB；这是当前设备的单次样本，不是系统瓦数测量。
- 实际安装的 MSIX 通过 130 项播放检查（含 SMTC 应用身份），见 `msix-playback-2026-10-15.json`；4 项启动与播放状态恢复检查通过，见 `msix-startup-2026-10-15.json`。
- MSIX 的 23 项 Shell/深浅色/原生菜单检查在可见启动下通过，见 `msix-shell-visible-2026-10-15.json`。首次隐藏启动的无人值守菜单检查受到 Windows 前台资格限制，记录保留在 `msix-shell-2026-10-15.json`，没有据此更改托盘产品逻辑。
- 使用内核的 GetPackageFullName / GetApplicationUserModelId 查询实际测试进程，确认运行在 MSIX 身份下；安装目录位于 WindowsApps。签名和安装器校验通过后才移除普通安装版，最后从 MSIX 安装目录重新打开应用。

包、安装辅助脚本及公钥位于 `artifacts/msix`。本地签名指纹绑定位于被忽略的 `certificates/signing.json`，不加入 Git。
