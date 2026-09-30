namespace EveningSelfStudyClock.Models;

/// <summary>
/// 一个摄像头设备。
/// </summary>
/// <param name="Name">设备名（如「ASUS FHD webcam」）。占用状态按它匹配，设置页里勾的也是它。</param>
/// <param name="SymbolicLink">
/// 设备接口路径。仅作识别备用：MF 活动上报里的这一串是全大写、枚举出来的是小写，
/// 而且换 USB 口会变，所以匹配（<see cref="CameraAlertBuilder"/>）只认设备名。
/// </param>
public sealed record CameraDevice(string Name, string SymbolicLink);
