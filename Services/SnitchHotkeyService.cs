using System.Runtime.InteropServices;
using EveningSelfStudyClock.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 快捷键「打小报告」服务：注册系统级全局热键（默认 Ctrl+Alt+R，设置页可改），
/// 按下后向飞书群发送一张橙色「现场报告」卡片。
/// 热键用 Win32 RegisterHotKey + 专用消息窗口线程实现，CI 在任何界面时都生效。
/// 说明：抓拍照片与现场录音的上传通道已按需求移除，这里只剩卡片推送。
/// </summary>
public class SnitchHotkeyService : IHostedService, IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0x5352; // 'S''R'
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>触发冷却（秒），防止误触连发。测试按钮不受限。</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(10);

    private readonly PluginSettings _settings;
    private readonly IServiceProvider _sp;
    private readonly object _gate = new();

    private Thread? _thread;
    private IntPtr _hwnd;
    private uint _registeredMods;
    private uint _registeredVk;
    private DateTime _lastTrigger = DateTime.MinValue;
    private static bool _classRegistered;

    // 防止非托管回调被 GC 回收
    private WndProcDelegate? _wndProcKeepAlive;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public SnitchHotkeyService(PluginSettings settings, IServiceProvider sp)
    {
        _settings = settings;
        _sp = sp;
        _wndProcKeepAlive = WndProcCore;
    }

    // ===== 生命周期 =====

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        ApplyRegistration();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        Unregister();
        QuitThread();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        Unregister();
        QuitThread();
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.EnableSnitch) or nameof(PluginSettings.SnitchHotkey))
            ApplyRegistration();
    }

    // ===== 热键注册 =====

    /// <summary>按设置（重）注册热键。注册失败不抛异常，仅忽略（群里看不到的内部能力）。</summary>
    private void ApplyRegistration()
    {
        lock (_gate)
        {
            UnregisterInternal();

            if (!_settings.EnableSnitch) return;
            if (!TryParseHotkey(_settings.SnitchHotkey, out var mods, out var vk)) return;

            if (!EnsureThreadAndWindow()) return;
            if (!RegisterHotKey(_hwnd, HOTKEY_ID, mods, vk)) return;

            _registeredMods = mods;
            _registeredVk = vk;
        }
    }

    private void Unregister()
    {
        lock (_gate)
        {
            UnregisterInternal();
        }
    }

    private void UnregisterInternal()
    {
        if (_hwnd != IntPtr.Zero && _registeredMods != 0)
        {
            try { UnregisterHotKey(_hwnd, HOTKEY_ID); } catch { }
        }
        _registeredMods = 0;
        _registeredVk = 0;
    }

    /// <summary>启动专用消息泵线程并创建 message-only 窗口（惰性，一次即可）。</summary>
    private bool EnsureThreadAndWindow()
    {
        if (_hwnd != IntPtr.Zero) return true;

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _hwnd = CreateMessageWindow();
            ready.Set();
            if (_hwnd == IntPtr.Zero) return;

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        })
        {
            IsBackground = true,
            Name = "SnitchHotkey",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return ready.Wait(3000) && _hwnd != IntPtr.Zero;
    }

    private void QuitThread()
    {
        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            try { PostMessageW(hwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { DestroyWindow(hwnd); } catch { }
        }
        _hwnd = IntPtr.Zero;
    }

    private IntPtr CreateMessageWindow()
    {
        try
        {
            var hInstance = GetModuleHandleW(null);
            const string className = "CI_ESC_SnitchHotkeyWnd";

            if (!_classRegistered)
            {
                var wc = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProcKeepAlive!,
                    hInstance = hInstance,
                    lpszClassName = className,
                };
                if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != 7 /* ERROR_CLASS_ALREADY_EXISTS */)
                    return IntPtr.Zero;
                _classRegistered = true;
            }

            return CreateWindowExW(0, className, "", 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private IntPtr WndProcCore(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY && wParam == HOTKEY_ID)
        {
            // 后台执行，绝不阻塞消息泵；失败静默
            _ = Task.Run(() => _ = TriggerAsync(ignoreCooldown: false));
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>解析 "Ctrl+Alt+R" 格式热键字符串。支持 Ctrl/Alt/Shift/Win + 字母/数字/F1~F24。</summary>
    public static bool TryParseHotkey(string? text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false; // 必须带修饰键，防误触

        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= 0x0002; break; // MOD_CONTROL
                case "ALT": mods |= 0x0001; break;               // MOD_ALT
                case "SHIFT": mods |= 0x0004; break;             // MOD_SHIFT
                case "WIN" or "WINDOWS": mods |= 0x0008; break;  // MOD_WIN
                default: return false;
            }
        }

        var key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z') vk = key[0];
        else if (key.Length == 1 && key[0] is >= '0' and <= '9') vk = key[0];
        else if (key.Length is 2 or 3 && key[0] == 'F' &&
                 int.TryParse(key[1..], out var f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
        else return false;

        return mods != 0 && vk != 0;
    }

    // ===== 报告流水线 =====

    /// <summary>
    /// 执行一次打小报告。热键触发走冷却；设置页测试按钮传 ignoreCooldown = true。
    /// 返回结果文字（供设置页显示）。
    /// </summary>
    public async Task<string> TriggerAsync(bool ignoreCooldown)
    {
        lock (_gate)
        {
            if (!ignoreCooldown && DateTime.Now - _lastTrigger < Cooldown)
                return $"⏳ 冷却中（{Cooldown.TotalSeconds:0} 秒内只触发一次），刚才的按键已忽略";
            _lastTrigger = DateTime.Now;
        }

        var notifier = _sp.GetService<FeishuNotifier>();
        if (notifier is null)
            return "❌ 推送服务不可用";
        if (string.IsNullOrWhiteSpace(_settings.FeishuWebhookUrl))
            return "❌ 未填写飞书 Webhook 地址，报告未发送";

        var course = CurrentCourseNameSafe();
        var hotkeyText = string.IsNullOrWhiteSpace(_settings.SnitchHotkey) ? "Ctrl+Alt+R" : _settings.SnitchHotkey;

        var (ok, msg) = await notifier.SendSnitchAsync(course, hotkeyText).ConfigureAwait(false);
        return ok ? "✅ 报告卡片已发送" : $"❌ 卡片发送失败：{msg}";
    }

    private string CurrentCourseNameSafe()
    {
        try
        {
            return _sp.GetRequiredService<ClassIsland.Core.Abstractions.Services.ILessonsService>()
                .CurrentSubject?.Name ?? "";
        }
        catch
        {
            return "";
        }
    }

    // ===== Win32 =====

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
