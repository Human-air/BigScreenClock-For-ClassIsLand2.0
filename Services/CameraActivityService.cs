using System.Diagnostics;
using System.Runtime.InteropServices;
using EveningSelfStudyClock.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 摄像头占用检测（Media Foundation 传感器活动接口，Windows 10 1703+）。
///
/// 原理：<c>IMFSensorActivityMonitor</c> 把「哪个进程正在用哪个摄像头」的变化实时回调过来
/// （系统相机、微信/QQ 通话、浏览器网页调用摄像头都算），不用轮询。相机有关的进程与服务
/// 都集中在 mfsensorgroup.dll，不需要额外驱动或管理员权限。
///
/// 用法：大屏时钟显示时 <see cref="StartMonitoring"/>，收起时 <see cref="StopMonitoring"/>；
/// 期间随时读 <see cref="Occupancy"/> 取当前快照。系统不支持（老系统/接口失败）时
/// <see cref="IsSupported"/> 为 false，功能静默关闭，不影响其它提醒。
/// </summary>
public sealed class CameraActivityService : IDisposable
{
    /// <summary>MF_VERSION：MFStartup 要求的最低版本号（0x0002 主版本 + 0x0070 次版本）。</summary>
    private const uint MfVersion = 0x00020070;

    /// <summary>MFSTARTUP_LITE：只起 MF 平台的核心部分，够传感器接口用。</summary>
    private const uint MfStartupLite = 1;

    /// <summary>空的占用快照（未监控/无占用时用）。</summary>
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private IMFSensorActivitiesReportCallback? _callback;
    private IMFSensorActivityMonitor? _monitor;
    private bool _mfStarted;
    private bool _started;

    /// <summary>当前占用快照：设备名 → 正在使用它的程序名（忽略大小写）。
    /// MF 回调在后台线程整体替换这个引用，读方（UI 线程）拿到的一直是完整快照。</summary>
    private volatile IReadOnlyDictionary<string, string> _occupancy = Empty;

    /// <summary>当前有进程在用的设备（设备名 → 程序名）。</summary>
    public IReadOnlyDictionary<string, string> Occupancy => _occupancy;

    /// <summary>摄像头检测是否可用（MF 平台起得来、监视器建得出来）。</summary>
    public bool IsSupported { get; private set; }

    /// <summary>不可用时的原因（设置页提示用），可用时为空。</summary>
    public string? LastError { get; private set; }

    /// <summary>开始监控（大屏时钟显示时调用）。重复调用无副作用。</summary>
    public void StartMonitoring()
    {
        if (_started) return;
        try
        {
            EnsureMediaFoundation();

            if (_monitor == null)
            {
                _callback = new ReportCallback(OnActivitiesReport);
                var hr = PInvoke.MFCreateSensorActivityMonitor(_callback, out var monitor);
                if (hr.Failed)
                {
                    LastError = $"创建摄像头监视器失败（0x{(uint)hr:X8}）";
                    _callback = null;
                    return;
                }

                _monitor = monitor;
            }

            _monitor.Start();
            _started = true;
            IsSupported = true;
            LastError = null;
            System.Diagnostics.Debug.WriteLine("[摄像头] 开始监控占用状态");
        }
        catch (Exception ex)
        {
            IsSupported = false;
            LastError = $"此系统不支持摄像头占用检测（{ex.GetType().Name}: {ex.Message}）";
            System.Diagnostics.Debug.WriteLine($"[摄像头] 启动失败：{ex}");
        }
    }

    /// <summary>停止监控（收起大屏时钟时调用）。停止后占用快照会清空，避免留下上次的状态。</summary>
    public void StopMonitoring()
    {
        if (!_started) return;
        try { _monitor?.Stop(); }
        catch { /* 监视器已失效，忽略 */ }

        _started = false;
        _occupancy = Empty;
        System.Diagnostics.Debug.WriteLine("[摄像头] 停止监控");
    }

    /// <summary>
    /// 枚举本机摄像头（设置页的设备列表用）。枚举失败返回空表，调用方按「没找到设备」提示。
    /// </summary>
    public List<CameraDevice> EnumerateDevices()
    {
        var devices = new List<CameraDevice>();
        try
        {
            EnsureMediaFoundation();

            var hr = PInvoke.MFCreateAttributes(out var attributes, 1);
            if (hr.Failed) return devices;

            attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                               PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

            // MFEnumDeviceSources 返回的是「接口指针数组」，CsWin32 生成的托管签名把数组当成了
            // 单个 out 接口，这里用原始声明按真实 ABI 取（调用后数组与各接口都要自己释放）。
            var enumerateHr = MFEnumDeviceSourcesRaw(attributes, out var array, out var count);
            if (enumerateHr < 0) return devices;

            try
            {
                for (var i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    if (ptr == IntPtr.Zero) continue;

                    var activate = (IMFActivate)Marshal.GetObjectForIUnknown(ptr);
                    var name = AttrString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME).Trim();
                    var link = AttrString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK).Trim();
                    if (name.Length > 0) devices.Add(new CameraDevice(name, link));
                }
            }
            finally
            {
                for (var i = 0; i < count; i++)
                {
                    var ptr = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    if (ptr != IntPtr.Zero) Marshal.Release(ptr);
                }
                CoTaskMemFreeRaw(array);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[摄像头] 枚举设备失败：{ex}");
        }

        return devices;
    }

    /// <summary>MF 活动上报回调（MF 的后台线程调用）：把上报整理解成占用快照。</summary>
    private void OnActivitiesReport(IMFSensorActivitiesReport report)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            report.GetCount(out var deviceCount);
            for (uint i = 0; i < deviceCount; i++)
            {
                report.GetActivityReport(i, out var device);
                if (device == null) continue;

                var name = ReadString(device.GetFriendlyName).Trim();
                if (name.Length == 0) name = ReadString(device.GetSymbolicLink).Trim();   // 兜底：拿不到名字就用设备路径
                if (name.Length == 0) continue;

                // 一个设备可能被多个进程用（如相机 App + 后台预览）；优先记「正在推流」的那个
                string? process = null;
                device.GetProcessCount(out var processCount);
                for (uint j = 0; j < processCount; j++)
                {
                    device.GetProcessActivity(j, out var activity);
                    if (activity == null) continue;

                    activity.GetProcessId(out var pid);
                    var processName = ProcessName(pid);
                    activity.GetStreamingState(out var streaming);
                    if (streaming)
                    {
                        process = processName;
                        break;
                    }
                    process ??= processName;   // 只打开没推流（暂停预览等）也算占用
                }

                if (process != null) map[name] = process;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[摄像头] 处理活动上报失败：{ex.Message}");
        }

        _occupancy = map;
    }

    /// <summary>起 MF 平台。传感器接口属于 Media Foundation，没初始化直接返回 0xC00D3E85。</summary>
    private void EnsureMediaFoundation()
    {
        if (_mfStarted) return;

        var hr = PInvoke.MFStartup(MfVersion, MfStartupLite);
        if (hr.Failed) throw new InvalidOperationException($"MFStartup 失败（0x{(uint)hr:X8}）");

        _mfStarted = true;
    }

    private static string ProcessName(uint pid)
    {
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch { return "未知程序"; }   // 进程已退出/受保护进程读不到名字
    }

    /// <summary>读 IMFActivate 上的字符串属性（MF 分配的内存要自己释放）。</summary>
    private static unsafe string AttrString(IMFActivate activate, Guid key)
    {
        try
        {
            activate.GetAllocatedString(key, out var value, out _);
            if (value.Value == null) return "";
            var text = value.ToString();
            PInvoke.CoTaskMemFree(value.Value);
            return text;
        }
        catch { return ""; }
    }

    private unsafe delegate void StringGetter(PWSTR buffer, uint cch, out uint written);

    /// <summary>MF 的字符串 getter 都是「传缓冲区 + 传出写入长度」，写入长度含结尾 0。</summary>
    private static unsafe string ReadString(StringGetter getter)
    {
        const int Size = 256;
        var buffer = stackalloc char[Size];
        getter(buffer, Size, out var written);

        var length = Math.Min((int)written, Size);
        if (length <= 0) return "";
        for (var i = 0; i < length; i++)
        {
            if (buffer[i] != '\0') continue;
            length = i;
            break;
        }
        return new string(buffer, 0, length);
    }

    [DllImport("mf.dll", ExactSpelling = true, EntryPoint = "MFEnumDeviceSources")]
    private static extern unsafe int MFEnumDeviceSourcesRaw(
        IMFAttributes attributes, out IntPtr sourceActivate, out uint count);

    [DllImport("ole32.dll", ExactSpelling = true, EntryPoint = "CoTaskMemFree")]
    private static extern void CoTaskMemFreeRaw(IntPtr memory);

    public void Dispose()
    {
        StopMonitoring();

        _monitor = null;
        _callback = null;

        if (!_mfStarted) return;
        try { PInvoke.MFShutdown(); } catch { }
        _mfStarted = false;
    }

    /// <summary>MF 的上报回调。实现为托管类，交给 COM 时会自动生成 CCW。</summary>
    private sealed class ReportCallback : IMFSensorActivitiesReportCallback
    {
        private readonly Action<IMFSensorActivitiesReport> _onReport;

        public ReportCallback(Action<IMFSensorActivitiesReport> onReport) => _onReport = onReport;

        public void OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            try { _onReport(report); }
            catch { /* 回调里不能再抛出去 */ }
        }
    }
}
