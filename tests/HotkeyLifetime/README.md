# 全局热键服务生命周期检查

```sh
dotnet run --project tests/HotkeyLifetime/HotkeyLifetime.csproj
```

直接编译生产 `SnitchHotkeyService`，用简化设置和服务提供方验证：重复启动仅订阅一次、停止和重复释放会摘掉订阅、停止前已捕获但延迟送达的设置事件不会重新注册，以及长命设置对象不会阻止已释放服务被 GC。

测试关闭原生注册路径，因此可在 Linux/Windows 运行；它不验证 Windows 消息窗口、热键注册或线程退出本身，这些需实机检查。
