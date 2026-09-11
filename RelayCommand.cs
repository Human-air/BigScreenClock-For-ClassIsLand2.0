using System;
using System.Windows.Input;

namespace EveningSelfStudyClock;

/// <summary>
/// 极简命令封装：把无参 Action 包装成 ICommand（设置页按钮、托盘菜单项等 UI 命令共用）。
/// 挂在根命名空间，子命名空间（.Settings / .Services / .ViewModels）按 C# 命名空间外层查找规则直接可见。
/// </summary>
internal class RelayCommand : ICommand
{
    private readonly Action _action;
    public RelayCommand(Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
#pragma warning disable CS0067
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
