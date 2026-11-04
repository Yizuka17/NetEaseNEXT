# 图标回弹实现

实现位于 `NetEaseNEXT/Services/FluentMotion.cs`，使用 WinUI 3 Composition 关键帧，沿用用户确认的 480ms 回弹小样。这里是可预测的回弹曲线，不是通过质量、阻尼和刚度求解的物理弹簧。

- 总时长：480ms。
- 22% 到达 6px 位移；42% 结束顶点停留。
- 82% 到达相反方向的 20% 位移，即 1.2px 过冲。
- 100% 回正。曲线控制点为 `(0.85,0)`、`(0.75,1)`。
- 方向向量：侧栏与下一首 `(6,0,0)`，上一首 `(-6,0,0)`，常规底栏 `(0,-6,0)`。
- 播放/暂停另用 2px 下沉与 0.86 倍缩放；返回、设置和账号不加回弹。搜索使用 SDK 的 AnimatedFindVisualSource。

在 UI 线程上的点击回调中调用：

```csharp
FluentMotion.Bounce(icon, new Vector3(6, 0, 0));
```

只传图标本身，保持按钮命中区域和文字位置不动。正式界面的调用处会遵守应用和系统的动画开关；隐藏后停止动画并归零。具有 XAML 隐式属性过渡的图标使用单独容器，避免与 Composition 同时控制同一个元素。

侧栏和歌曲行使用 `MoveIndicator`：前 1/3 时间将指示条伸展到新旧位置之间，后 2/3 收缩至目标。两处共用时长和曲线，按实际行高与间距计算伸展比例。滚动或重新布局时直接对齐，避免把滚动误判成切歌动画。

[微软 Windows Motion 原则](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/motion)；指示条参考原生 NavigationView 的伸展/收缩行为，未复制参考播放器源码或资源。
