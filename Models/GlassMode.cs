namespace EveningSelfStudyClock.Models;

/// <summary>
/// 组件玻璃底的档次，取值与 <see cref="PluginSettings.GlassMode"/> 的整数值一致。
/// 设置里存 int，这个枚举只是给代码里用的名字。
/// </summary>
public enum GlassMode
{
    /// <summary>关闭：不画玻璃，外观与加这个功能之前完全一致。</summary>
    Off = 0,

    /// <summary>半透明：只叠一层半透明底 + 描边 + 投影，不模糊。开销极低。</summary>
    Translucent = 1,

    /// <summary>磨砂：玻璃里透出预烘的模糊底图，真模糊（静态，不随帧重算）。</summary>
    Frosted = 2,

    /// <summary>
    /// 真•高光折射：贴边的画面被推弯、带一点分色，迎光的那段周长上有一道镜边高光。
    /// 也是预烘的（与磨砂同理，静态不随帧重算），只是算的是折射位移而不是模糊。
    /// </summary>
    Liquid = 3,
}
