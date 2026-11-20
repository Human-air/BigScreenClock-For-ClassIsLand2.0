using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Automation;
using ClassIsland.Core.Attributes;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Automation;

[ActionInfo("F0BEF813-CC4C-48B7-B073-513C7BD350B1", "显示大屏时钟", "\ue4c4", defaultGroupToMenu: "大屏时钟")]
public sealed class ShowClockAction(FullScreenClockViewModel viewModel) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        await Dispatcher.UIThread.InvokeAsync(viewModel.Show);
    }
}

[ActionInfo("EA32DD33-DA7D-4076-9AD0-259BFF886914", "隐藏大屏时钟", "\ue4cc", defaultGroupToMenu: "大屏时钟")]
public sealed class HideClockAction(FullScreenClockViewModel viewModel) : ActionBase
{
    protected override async Task OnInvoke()
    {
        await base.OnInvoke();
        await Dispatcher.UIThread.InvokeAsync(viewModel.Hide);
    }
}
