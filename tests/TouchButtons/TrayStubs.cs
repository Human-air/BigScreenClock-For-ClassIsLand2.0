using System.ComponentModel;
using Avalonia.Controls;
namespace ClassIsland.Core.Abstractions.Services
{
    public interface ITaskBarIconService { TrayIcon? MainTaskBarIcon { get; } }
}
namespace EveningSelfStudyClock.Models
{
    public sealed class PluginSettings : INotifyPropertyChanged
    {
        private bool _show = true;
        public bool ShowTrayMenuEntry { get => _show; set { _show = value; PropertyChanged?.Invoke(this,new(nameof(ShowTrayMenuEntry))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
namespace EveningSelfStudyClock.ViewModels
{
    public sealed class FullScreenClockViewModel { public int Shows; public void Show() => Shows++; }
}
namespace TrayTests
{
    public sealed class Provider : IServiceProvider, ClassIsland.Core.Abstractions.Services.ITaskBarIconService
    {
        public TrayIcon? MainTaskBarIcon { get; set; }
        public object? GetService(Type type) => type == typeof(ClassIsland.Core.Abstractions.Services.ITaskBarIconService) ? this : null;
    }
}
