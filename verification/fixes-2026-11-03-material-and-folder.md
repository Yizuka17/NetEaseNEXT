# 0.3.2.4：材质调节与文件夹歌单隔离

- 亚克力恢复 0–100 的透明度，Mica/Mica Alt 使用壁纸融合度。自定义档同时调整两个材质参数，更新现有控制器，保留旧设置数值。系统默认是独立状态；切回默认不会覆盖自定义数值。深浅主题同时同步材质着色与回退色，系统默认档也同步主题。
- 文件监视通知带上受影响的歌单范围，同一目录的非递归绑定忽略子目录变化。临时下载音频不会触发同步或进入列表；完成改名后只更新相关歌单。有效列表逐行更新，无变化时不重写库文件或重建当前列表。

## 验证环境

Windows 11 IoT Enterprise LTSC 2024 Evaluation（26100）运行在独立 QEMU 11.1 TCG 虚拟机。未启用宿主机硬件虚拟化、未安装宿主机驱动或改动反作弊。来宾使用 Microsoft Basic Display Adapter，1280×960，离线测试账号。虚拟机没有直接显示窗口；宿主机只读画面放在左侧副屏（实测 x=-1318、右边界=-32，未获得前台焦点）。安装和检查只访问新建虚拟盘及明确附加的只读镜像。

Windows 10 后台真实文件系统监视检查 5 项连续三轮通过。Win11 本地歌单隔离 8 项连续三轮、歌单下载 32 项和离线歌词/本地续播 19 项通过。测试实际确认下载目标目录的歌单在文件完成改名后更新，另一目录的打开歌单没有集合变更且选中对象不变。覆盖同目录多绑定、子文件夹递归规则、目录移动及无变化时不重写本地库。

首轮的测试记录文件读写竞争和 100% 缩放下的诊断宽度限制已修正。测试发现系统默认 Acrylic 在深色主题可能读取到浅色参数，显式同步配置主题后重测正确。窗口回归还复现了 WinUI 的延迟回调访问已脱离目标的问题；恢复自定义背景回调保护后，最终程序的窗口/侧栏/材质回归 30 项通过，包括实际原生窗口缩放、最小化、最大化、全屏和主题切换。

最终程序的材质专项 43 项通过，保存了三种材质 × 两种主题 × 七个状态的 42 张来宾帧。默认深色 Acrylic 的回退色为 RGB 44/44/44；自定义各档为 RGB 32/32/32。三种材质的调节参数都在 0、25、50、65、75、100 六档连续变化；纯色回退画面的标题栏和空白导航区像素在各自定义档相同。因此验证了参数与主题一致性，没有把该虚拟显示环境当作真实 GPU 的透明度验收。证据：[深色默认 Acrylic](material-and-folder-2026-11-03/Acrylic-Dark-system.png)、[深色 Mica 100%](material-and-folder-2026-11-03/Mica-Dark-100.png)、[像素样本](material-and-folder-2026-11-03/frame-measurements.json)、[检查结果](v0.3.2.4-guest-final-2026-11-03.json)。

Release 构建零警告、零错误。0.3.2.4 MSIX 以原有发布者证书签名，核对包内版本、主程序哈希与最终来宾程序一致，包内未包含 Node 或用户数据。没有覆盖宿主机的安装。发布只替换原 v0.3.2 的附件和说明，源码改动留在本地。

系统素材来自 [微软评估中心](https://www.microsoft.com/en-us/evalcenter/evaluate-windows-11-iot-enterprise-ltsc) 和 [QEMU 项目的 Windows 下载页](https://www.qemu.org/download/#windows)。材质配置依据 [Windows App SDK 系统背景说明](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)。

## VirtualBox 3D 加速复验

Windows 11 26100 来宾装入 Guest Additions 7.2.22。VirtualBox 宿主日志显示使用 RTX 5070 Ti，来宾 dxdiag 报告 VirtualBox Graphics Adapter (WDDM)、Direct3D 11.1 已启用。应用 DLL 的 SHA-256 与 0.3.2.4 签名包一致，43 项材质检查全部通过。

从来宾直接抓取 42 张画面，覆盖三种材质、两种主题的系统默认及 0、25、50、65、75、100 六档。标题栏样本的 30 组相邻档位均有像素变化。例如深色 Mica 在 65% 的平均 RGB 为 56.80/80.53/102.65，与纯色回退的 32/32/32 明显不同。没有复现此前部分档位不变化或突然变成纯白的问题。

[检查结果](material-3d-2026-11-03/checks.txt) · [像素样本](material-3d-2026-11-03/pixel-results.json) · [亚克力 65%](material-3d-2026-11-03/Acrylic-Dark-65.png) · [云母 100%](material-3d-2026-11-03/Mica-Dark-100.png) · [浅色云母 Alt 100%](material-3d-2026-11-03/MicaAlt-Light-100.png)
