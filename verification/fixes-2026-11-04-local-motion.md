# 本地封面与原生动效

本地封面在卡片与详情之间使用 ConnectedAnimation，返回前先实现原卡片位置。切换图片 URI 时同步取消旧请求并清除旧 Source；目标歌单的封面与标题在异步磁盘读取前准备好。来自卡片的已解码图片作为当前封面保留到同一 URI 的解码完成，避免连接动画被清空。已取消的本地库读取不提交旧状态。

封面卡片悬停轻微放大，按下缩小，松手使用原生 SpringVector3NaturalMotionAnimation 回弹（阻尼 0.7、周期 60ms）。播放器常用按钮有指针和空格/回车按压反馈，重挂到播放器页时保留唯一一组事件处理器。页面 180ms 淡入并平移 12px，使用 Translation 保持 XAML 布局偏移。封面连接期间跳过整页位移，避免叠加。播放队列 160ms 展开、100ms 收起，收起期间保留列表内容。本地卡片新增、删除与重排使用原生主题过渡；目录刷新不重建无变化的卡片。动画可打断，关闭动画或隐藏窗口时恢复变换。

沿用现有 WinUI 3 控件、透明图标按钮和布局，没有新增宣传或说明文案。参考 Gallery 的交互原理后独立实现，未复制 Gallery 源码或资源。

## 验证

Windows 11 26100、VirtualBox 3D：22 项专项连续三轮通过，包括双向连接、同步清旧图、空封面、快速 A/B 切换、关闭动画、事件重挂、弹簧中断及队列出入场。三轮均确认系统动画开启且原生连接动画实际启动。文件夹歌单隔离 8 项通过。[结果](local-motion-2026-11-04/checks.txt)，[详情画面](local-motion-2026-11-04/final-detail.png)。

应用已编译和发布到本地测试目录；虚拟机测试后正常关闭，12 个临时测试目录已清理，原测试光盘已恢复。本次未更新 GitHub 或 Release。没有进行独立视觉评审；检查了实际原生窗口和 XAML 截图。VirtualBox 的录屏实际仅约每秒一帧，未用于宣称动画流畅度或帧率提升。

参考：[WinUI Gallery Motion 目录](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/SampleSupport/Data/ControlInfoData.json)、[原生弹簧动画](https://learn.microsoft.com/windows/apps/develop/composition/spring-animations)、[连接动画](https://learn.microsoft.com/windows/apps/develop/motion/connected-animation)。
