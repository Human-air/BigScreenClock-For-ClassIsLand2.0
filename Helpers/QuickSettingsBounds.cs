using Avalonia;

namespace EveningSelfStudyClock.Helpers;

/// <summary>快速设置卡片与关闭命中区共用边界，让顶栏和按钮栏始终可以直接点击。</summary>
public static class QuickSettingsBounds
{
    public static (Thickness Card, Thickness DismissArea) GetMargins(
        double? headerBottom, double? buttonTop, double surfaceHeight)
    {
        var top = headerBottom is { } header ? header + 12 : 20;
        var bottom = buttonTop is { } buttons ? Math.Max(12, surfaceHeight - buttons + 12) : 20;
        return (new Thickness(0, top, 20, bottom), new Thickness(0, top, 0, bottom));
    }
}
