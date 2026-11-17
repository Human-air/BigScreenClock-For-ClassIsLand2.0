# UI 刷新队列生命周期

```sh
dotnet run --project tests/UiWork/UiWork.csproj
```

直接链接生产 `CoalescedUiWork`，检查大量并发请求与执行期间请求仅保留一次刷新、退出时作废旧回调、重新显示后旧会话不干扰新会话、调度失败可恢复，以及已排队回调在停止后不再保留目标对象。无需 Avalonia 或额外 NuGet 包。
