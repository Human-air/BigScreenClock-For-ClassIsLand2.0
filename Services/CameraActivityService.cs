using System.Diagnostics;
using System.Runtime.InteropServices;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 摄像头占用检测（Media Foundation 传感器活动接口，Windows 10 1703+）。
///
/// 使用 <c>IMFSensorActivityMonitor</c> 接收 Media Foundation 的活动回调。
/// 仅对系统确认正在推流的设备生成占用状态。
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
    private static readonly IReadOnlyList<CameraActivity> Empty = Array.Empty<CameraActivity>();

    private IMFSensorActivitiesReportCallback? _callback;
    private IMFSensorActivityMonitor? _monitor;
    private bool _mfStarted;
    private bool _started;
    private readonly object _reportGate = new();
    private object? _reportSession;
    private (int Devices, long ActivityEntries, int StreamingDevices, int Occupied, int Failures)? _lastReportCounts;

    /// <summary>当前占用快照。MF 回调在后台线程整体替换这个引用，读方（UI 线程）拿到的一直是完整快照。</summary>
    private volatile IReadOnlyList<CameraActivity> _occupancy = Empty;

    /// <summary>
    /// 当前有进程在用的设备。名字与接口路径都带着：设备名不保证唯一（两个摄像头可能同名），
    /// 认设备得靠路径，见 <see cref="MonitoredCamera.Matches"/>。
    /// </summary>
    public IReadOnlyList<CameraActivity> Occupancy => _occupancy;

    /// <summary>摄像头检测是否可用（MF 平台起得来、监视器建得出来）。</summary>
    public bool IsSupported { get; private set; }

    /// <summary>不可用时的原因（设置页提示用），可用时为空。</summary>
    public string? LastError { get; private set; }

    /// <summary>开始监控（大屏时钟显示时调用）。重复调用无副作用。</summary>
    public void StartMonitoring()
    {
        if (_started) return;
        var windowsBuild = Environment.OSVersion.Version.Build;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
        {
            LastError = "摄像头占用监视需要 Windows 10 1703 或更高版本。";
            BackgroundDiagnostics.Write("camera-start-error", $"stage=platform build={windowsBuild}");
            return;
        }

        var stage = "initialize";
        try
        {
            EnsureMediaFoundation();
            var session = new object();
            lock (_reportGate)
            {
                _reportSession = session;
                _lastReportCounts = null;
            }

            stage = "create";
            _callback = new ReportCallback(report => OnActivitiesReport(session, report));
            var hr = PInvoke.MFCreateSensorActivityMonitor(_callback, out _monitor);
            if (hr.Failed || _monitor is null)
            {
                IsSupported = false;
                LastError = $"创建摄像头监视器失败（0x{(uint)hr:X8}）";
                BackgroundDiagnostics.Write("camera-start-error", $"stage={stage} build={windowsBuild} hresult=0x{(uint)hr:X8}");
                StopMonitoring();
                return;
            }

            stage = "start";
            _monitor.Start();
            _started = true;
            IsSupported = true;
            LastError = null;
            BackgroundDiagnostics.Write("camera-start", $"source=MediaFoundation build={windowsBuild}");
        }
        catch (Exception ex)
        {
            IsSupported = false;
            LastError = $"摄像头占用监视启动失败（0x{(uint)ex.HResult:X8}）";
            BackgroundDiagnostics.Write("camera-start-error", $"stage={stage} build={windowsBuild} hresult=0x{(uint)ex.HResult:X8}", ex);
            StopMonitoring();
        }
    }

    /// <summary>停止后丢弃本次监视的迟到回调。</summary>
    public void StopMonitoring()
    {
        lock (_reportGate)
        {
            _reportSession = null;
            _occupancy = Empty;
        }
        var monitor = _monitor;
        _monitor = null;
        _started = false;
        try
        {
            if (monitor is not null && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
            {
                monitor.Stop();
                BackgroundDiagnostics.Write("camera-stop", "source=MediaFoundation");
            }
        }
        catch (Exception ex)
        {
            BackgroundDiagnostics.Write("camera-stop-error", $"hresult=0x{(uint)ex.HResult:X8}", ex);
        }
        finally
        {
            ReleaseOwnedComObject(monitor);
            _callback = null;
        }
    }

    /// <summary>
    /// 枚举本机摄像头（设置页的设备列表用）。枚举失败返回空表，调用方按「没找到设备」提示。
    /// </summary>
    private static readonly object EnumerationGate = new();
    private static Task<List<CameraDevice>>? _enumerationTask;

    public static Task<List<CameraDevice>> EnumerateDevicesAsync()
    {
        lock (EnumerationGate)
        {
            if (_enumerationTask is not { IsCompleted: false })
                _enumerationTask = Task.Run(EnumerateDevices);
            return _enumerationTask;
        }
    }

    private static List<CameraDevice> EnumerateDevices()
    {
        var devices = new List<CameraDevice>();
        IMFAttributes? attributes = null;
        IntPtr array = IntPtr.Zero;
        uint count = 0;
        var enumerationSucceeded = false;
        var mediaFoundationStarted = false;
        try
        {
            // 设备列表和大屏监视器各自持有一份 MF 初始化引用，互不依赖生命周期。
            var hr = PInvoke.MFStartup(MfVersion, MfStartupLite);
            if (hr.Failed) return devices;
            mediaFoundationStarted = true;
            hr = PInvoke.MFCreateAttributes(out attributes, 1);
            if (hr.Failed || attributes is null) return devices;

            attributes.SetGUID(PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                               PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

            // Array entries retain the native references returned by MFEnumDeviceSources.
            // GetObjectForIUnknown creates an additional RCW acquisition, released separately below.
            var enumerateHr = MFEnumDeviceSourcesRaw(attributes, out array, out count);
            if (enumerateHr < 0) return devices;
            enumerationSucceeded = true;
            if (array == IntPtr.Zero) return devices;

            for (uint i = 0; i < count; i++)
            {
                var ptr = Marshal.ReadIntPtr(array, checked((int)i * IntPtr.Size));
                if (ptr == IntPtr.Zero) continue;
                object? ownedActivation = null;
                try
                {
                    ownedActivation = Marshal.GetObjectForIUnknown(ptr);
                    var activate = (IMFActivate)ownedActivation;
                    var name = AttrString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME).Trim();
                    var link = AttrString(activate, PInvoke.MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK).Trim();
                    if (name.Length > 0) devices.Add(new CameraDevice(name, link));
                }
                finally { ReleaseOwnedComObject(ownedActivation); }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[摄像头] 枚举设备失败：{ex}");
        }
        finally
        {
            try
            {
                if (array != IntPtr.Zero)
                {
                    try
                    {
                        // On failure the returned count is not guaranteed valid; do not dereference it.
                        if (enumerationSucceeded)
                            for (uint i = 0; i < count; i++)
                            {
                                var ptr = Marshal.ReadIntPtr(array, checked((int)i * IntPtr.Size));
                                if (ptr != IntPtr.Zero) Marshal.Release(ptr);
                            }
                    }
                    finally { CoTaskMemFreeRaw(array); }
                }
            }
            finally
            {
                ReleaseOwnedComObject(attributes);
                if (mediaFoundationStarted) PInvoke.MFShutdown();
            }
        }
        return devices;
    }

    // Only for factory-created/enumerated locals we own. Never apply to borrowed activity reports.
    private static void ReleaseOwnedComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.ReleaseComObject(value); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[摄像头] 释放本地 COM 引用失败：{ex.Message}"); }
    }

    /// <summary>MF 活动上报回调（MF 的后台线程调用）：把上报整理解成占用快照。</summary>
    private void OnActivitiesReport(object session, IMFSensorActivitiesReport report)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063)) return;
        lock (_reportGate)
            if (!ReferenceEquals(_reportSession, session)) return;

        var list = new List<CameraActivity>();
        int failures = 0;
        uint deviceCount;
        try { report.GetCount(out deviceCount); }
        catch (Exception ex)
        {
            lock (_reportGate)
            {
                if (!ReferenceEquals(_reportSession, session) || _lastReportCounts is { Devices: -1 }) return;
                _lastReportCounts = (-1, 0, 0, _occupancy.Count, 1);
                BackgroundDiagnostics.Write("camera-report-error", $"stage=count hresult=0x{(uint)ex.HResult:X8}", ex);
            }
            return; // 数据源读取失败不等同于所有摄像头都空闲。
        }

        long activityEntries = 0;
        int streamingDevices = 0;
        for (uint i = 0; i < deviceCount; i++)
        {
            try
            {
                report.GetActivityReport(i, out var device);
                if (device is null) continue;
                device.GetProcessCount(out var processCount);
                activityEntries += processCount;
                var process = FindStreamingProcess(device, processCount, ref failures);
                if (process is null) continue;
                streamingDevices++;

                var name = TryReadString(device.GetFriendlyName, ref failures);
                var link = TryReadString(device.GetSymbolicLink, ref failures);
                if (name.Length > 0 || link.Length > 0)
                    list.Add(new CameraActivity(name, link, process));
            }
            catch { failures++; } // 单个设备的信息异常不能截断后续设备。
        }

        lock (_reportGate)
        {
            if (!ReferenceEquals(_reportSession, session)) return;
            _occupancy = list;
            var counts = ((int)deviceCount, activityEntries, streamingDevices, list.Count, failures);
            if (_lastReportCounts == counts) return;
            _lastReportCounts = counts;
            BackgroundDiagnostics.Write("camera-report", $"devices={deviceCount} activityEntries={activityEntries} streamingDevices={streamingDevices} occupied={list.Count} readFailures={failures}");
        }
    }

    /// <summary>起 MF 平台。传感器接口属于 Media Foundation，没初始化直接返回 0xC00D3E85。</summary>
    private void EnsureMediaFoundation()
    {
        if (_mfStarted) return;

        var hr = PInvoke.MFStartup(MfVersion, MfStartupLite);
        if (hr.Failed) Marshal.ThrowExceptionForHR((int)hr);

        _mfStarted = true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.15063")]
    private static string? FindStreamingProcess(IMFSensorActivityReport device, uint processCount, ref int failures)
    {
        for (uint i = 0; i < processCount; i++)
        {
            try
            {
                device.GetProcessActivity(i, out var activity);
                if (activity is null) continue;
                activity.GetStreamingState(out var streaming);
                if (streaming) return ProcessName(activity);
            }
            catch { failures++; }
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.15063")]
    private static string ProcessName(IMFSensorProcessActivity activity)
    {
        try
        {
            activity.GetProcessId(out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return "未知程序"; }   // 进程已退出/受保护进程读不到名字
    }

    /// <summary>读 IMFActivate 上的字符串属性（MF 分配的内存要自己释放）。</summary>
    private static unsafe string AttrString(IMFActivate activate, Guid key)
    {
        PWSTR value = default;
        try
        {
            activate.GetAllocatedString(key, out value, out _);
            return value.Value == null ? "" : value.ToString();
        }
        catch { return ""; }
        finally
        {
            if (value.Value != null) PInvoke.CoTaskMemFree(value.Value);
        }
    }

    private unsafe delegate void StringGetter(PWSTR buffer, uint cch, out uint written);

    // 友好名与设备路径分别读取，长路径或缺一项时仍可使用另一项匹配。
    private static unsafe string TryReadString(StringGetter getter, ref int failures)
    {
        const int Size = 1024;
        var buffer = stackalloc char[Size];
        try
        {
            getter(buffer, Size, out var written);
            if (written > Size)
            {
                failures++;
                return "";
            }
            int length = (int)written;
            for (var i = 0; i < length; i++)
            {
                if (buffer[i] != '\0') continue;
                length = i;
                break;
            }
            return new string(buffer, 0, length).Trim();
        }
        catch
        {
            failures++;
            return "";
        }
    }

    [DllImport("mf.dll", ExactSpelling = true, EntryPoint = "MFEnumDeviceSources")]
    private static extern unsafe int MFEnumDeviceSourcesRaw(
        IMFAttributes attributes, out IntPtr sourceActivate, out uint count);

    [DllImport("ole32.dll", ExactSpelling = true, EntryPoint = "CoTaskMemFree")]
    private static extern void CoTaskMemFreeRaw(IntPtr memory);

    public void Dispose()
    {
        StopMonitoring();

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
