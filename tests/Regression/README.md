# 资源生命周期回归检查

运行（需要 .NET 8，无额外 NuGet 依赖）：

```sh
dotnet run --project tests/Regression/Regression.csproj
```

直接编译生产环境的缓存、音量服务和噪声算法源码，使用可控的音频设备、位图及 UI 队列替身，检查缓存失效、所有权转移、迟到事件、排队重启与设备异常释放。

这里不访问真实麦克风，也不验证 Windows 驱动或 Avalonia 渲染；实际显示和设备行为需要在 Windows 的 ClassIsland 中验证。
