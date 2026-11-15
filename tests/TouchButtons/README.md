# 大屏按钮与快速设置命中回归检查

```sh
dotnet run --project tests/TouchButtons/TouchButtons.csproj
```

使用与插件相同的 Avalonia 11.3.17，包含两组检查：

- 从生产 XAML 读取按钮的点击模式及尺寸，验证触摸、触控笔和鼠标按下立即响应，抬手滑出时不重复触发，右键不触发，空格键仍正常触发。
- 从生产 XAML 摘取按钮和快速设置遮罩的实际祖先层级及布局属性，直接链接生产 `QuickSettingsBounds` 边界计算。通过 `Window.MouseMove/MouseDown/MouseUp` 走真实 Avalonia 命中测试，覆盖全屏／左半屏／右半屏、设置开／关、Press／Release 两种点击模式，以及退出／最小化两个按钮。设置打开时，首次点击必须到达目标按钮；卡片内部不关闭设置；中间空白区域一击关闭设置且不误触按钮。

测试省略与命中无关的时钟、课程和设置表单内容，以不同顶栏高度覆盖常规与紧凑布局。它们验证 Avalonia 输入路由与遮罩几何，不模拟 Windows 触屏驱动；希沃设备上的实际触屏行为仍需实机验证。

可传入另一份 `ClockSurface.axaml` 路径运行同一检查。旧的 Release 按钮配置或可参与命中测试的整屏遮罩应导致检查失败。
