# 切歌指示条、图标反馈与字体设置

- 歌曲列表与浮动播放列表共用单个移动指示条；只在选中行改变时移动，滚动跟随行位置，快速连点从当前位置继续。播放成功后同步当前行，多选时保留复选框。
- 本地/已下载下一首预先准备一个暂停的解码器，使用前核对文件路径、大小、修改时间、账号与目标音质；隐藏后取消准备。上一首保持直接切换。
- 返回箭头把状态直接发给 AnimatedIcon。图标反馈仅作用于被点击的图标；标签、计数和按钮命中区域保持原位，程序内部选择侧栏条目不会触发其它图标弹跳。
- 用户最终选择的节奏：点击即起跳，中段比前一版快约 20%，顶点停留更久。600ms 内约 133ms 抬起、停留至 350ms、250ms 回落；切歌加载完成后重排剩余弧线，使回落与列表指示条到达终点一起结束。关闭动画或隐藏应用时清除变换。
- 新增 12–22 基础字号（默认 14）及 400/500/600/700 字重，保留既有标题层级，跳过图标字体。设置、新加载行、弹层会应用偏好；重复应用不会累积缩放，恢复标准字重后新文本也能恢复。

## 验证

发布构建通过。Win11 26100 / VirtualBox SVGA 3D 中，42 项原生界面及本地解码检查连续三轮通过，另有 8 项歌单文件夹隔离检查通过；最终演示再次检查同一构建。虚拟声卡使用 null 后端，仅测试本地静音 WAV，不访问在线账号或下载权限。

程序 DLL SHA-256：`F6F7126FDD9264F4266E3D908FB60601ED310045C14F799411F59837B3B069C1`。

证据见 [检查记录](motion-refinement-2026-11-04/checks.txt)、[原生画面](motion-refinement-2026-11-04/motion-preview.mp4)。预览仅记录该虚拟机的帧与时间，不是宿主机帧率或实际音频输出延迟的基准。切歌耗时记录为调用完成时间，不能据此声称无缝播放。

原生参考：[AnimatedIcon 状态](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/animated-icon)、[Windows motion](https://learn.microsoft.com/en-us/windows/apps/design/motion/)、[NavigationView 指示条时长](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/NavigationView/NavigationView.cpp)。独立实现交互，未复制 Gallery 源码或资产。

按用户要求，副屏保留运行中的虚拟机和可手动操作的预览；GitHub/Release 未替换。
