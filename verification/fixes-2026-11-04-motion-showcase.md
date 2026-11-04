# 全套原生动效预览

沿用 NetEaseNEXT 的 WinUI 3 控件、主题和透明图标按钮。实现覆盖 Gallery Motion 七类的适用场景，并加入 SDK 自带的动画图标与矢量播放，没有复制 Gallery 源码或示例资源。

| 分类 | 本次落点 |
| --- | --- |
| Animation interop / Natural motion | 封面悬停、按钮按下与原生弹簧释放，可随时中断 |
| Connected Animation | 本地歌单封面进入详情和返回卡片；保持同一 URI 的源图直到解码完成 |
| Easing Functions | 短促按压、减速入场、较快退出的统一节奏 |
| Implicit Transitions | 设置表面背景的 BrushTransition、队列图标的 OpacityTransition |
| Page Transitions | 前进从右侧、返回从左侧的短距离过渡；封面连接期间不叠加整页位移 |
| Theme Transitions | 本地卡片增删与重新排布；播放队列的显示与隐藏 |
| ParallaxView | 本地及在线歌单封面随 TrackList 滚动，最大位移 16px |
| AnimatedIcon | 标题栏及播放器页返回箭头使用 AnimatedBackVisualSource，支持指针状态 |
| AnimatedVisualPlayer | 下载完成时播放一次 AnimatedAcceptVisualSource；新的反馈会取消旧播放 |

应用或系统关闭动画时，恢复静态图标、清除列表过渡、将视差归零、停止矢量播放。隐藏窗口同样停止反馈和待完成的连接动画。Surface 的原生属性过渡与底层 Composition 分配到不同元素，避免 WinUI 对两套动画同时接管同一对象的限制。

## 检查和预览

Win11 26100 的 3D 来宾完成 28 项交互检查连续三轮，以及 8 项文件夹歌单隔离回归：[结果](motion-showcase-2026-11-04/checks.txt)。最终演示构建再次通过 28 项，见 [最终演示检查](motion-showcase-2026-11-04/final-tour-checks.txt)。源码发布构建成功，未更新 GitHub 或 Release。

[原生画面预览](motion-showcase-2026-11-04/motion-preview.mp4) 包含 30 首与 12 首歌曲的隔离歌单：悬停、回弹、进入、滚动、返回、切换另一封面、队列展开/收起、页面方向、主题过渡和完成矢量。歌单、静音音频和封面均为诊断数据，没有修改用户音乐库。录制直接读取来宾帧缓冲；262 张原生帧按实际时间戳编码，不作为帧率或性能基准。

看过代表性原生画面，并通过自动化检查确认连接动画实际启动、图像切换无旧图、关闭动画后状态完整。未进行独立视觉评审。测试后清理 14 个来宾临时目录，正常关闭虚拟机，恢复原测试光盘。

参考：[Gallery Motion](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/SampleSupport/Data/ControlInfoData.json)、[弹簧动画](https://learn.microsoft.com/windows/apps/develop/composition/spring-animations)、[动画图标](https://learn.microsoft.com/windows/apps/develop/ui/controls/animated-icon)、[矢量动画播放](https://learn.microsoft.com/windows/apps/develop/ui/controls/animated-visual-player)。
