using System.ComponentModel;
namespace EveningSelfStudyClock.Models
{
    public sealed class PluginSettings
    {
        private PropertyChangedEventHandler? _changed;
        public bool EnableSnitch { get; set; }
        public string SnitchHotkey { get; set; } = "Ctrl+Alt+R";
        public string FeishuWebhookUrl { get; set; } = "";
        public int Subscribers => _changed?.GetInvocationList().Length ?? 0;
        public event PropertyChangedEventHandler? PropertyChanged { add => _changed += value; remove => _changed -= value; }
        public PropertyChangedEventHandler? CapturePendingEvent() => _changed;
    }
}
namespace EveningSelfStudyClock.Services
{
    public sealed class FeishuNotifier
    {
        public Task<(bool, string)> SendSnitchAsync(string course, string hotkey) => Task.FromResult((true, ""));
    }
}
namespace ClassIsland.Core.Abstractions.Services
{
    public interface ILessonsService { Subject? CurrentSubject { get; } }
    public sealed record Subject(string Name);
}
