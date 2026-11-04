# Fluent Motion 最小原生小样

微软 Windows Motion 的 Delightful 原则以任务栏图标的向下/向上弹跳举例。Fluent 2 当前将相应品质描述为 Appealing，同时要求动效有目的、自然、保持一致并限制在焦点元素。

- 官方原则：https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion#delightful-unexpected-moments-of-joy-with-purpose
- Fluent 2：https://fluent2.microsoft.design/motion
- 动画曲线参考 Windows Motion 的 Strong Entrance 表。公开文档没有给出任务栏完整实现；本例为独立近似。

## 最小实现

`NetEaseNEXT/Diagnostics/FluentMotionSample.xaml.cs` 的位移动画核心 10 行 C#，使用单个 Vector3KeyFrameAnimation。600ms 内向上 6px，22% 时到顶，58% 时开始回落，在原生 NavigationView 指示条收尾时复位。只操作被点击的 IconElement，文字不移动。指示条完全使用原生控件，不另写滑条。窗口布局、取消旧目标及系统关闭动画的兼容代码另计。

入口 `--fluent-motion-sample` 直接打开独立窗口，不构造播放器、账号或 API 服务。

## 实测

Release publish 成功，Win11 26100 / VirtualBox SVGA 3D 中启动，并实际点选验证。原始记录 132 个来宾帧，保留实际时间戳；短片截取窗口区域和一次点击过程。捕获率不是应用帧率基准。没有据此声称精确复刻任务栏实现。

[原生小样视频](fluent-motion.mp4)。按用户要求，副屏虚拟机保留运行；来宾桌面 `Fluent Motion` 快捷方式可重新打开小样。未发布 Release。

## 回落修订

按用户观察加入向下 1.2px 的落地过冲；速度先试 420ms，随后按用户反馈微调至 480ms。22% 到达 -6px，42% 结束顶点停留，82% 到达 +1.2px，最终回正。指示条改用一个原生 Vector3Transition，共享 480ms 时长，避免硬编码 600ms 的 NavigationView 自带指示动画不同步；正常窗口缩放会重新定位。原视频保留 600ms 初版，不能用作本次时长的证据。
