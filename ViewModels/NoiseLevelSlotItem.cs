using System.ComponentModel;
using Avalonia.Media;

namespace EveningSelfStudyClock.ViewModels;

/// <summary>
/// 音量条档位文字的数据项（安静/良好/一般/吵闹/嘈杂）。
/// 当前档位高亮放大、其余淡化，由 <see cref="IsCurrent"/> 驱动。
/// 颜色与淡化程度随主题变化（浅底上要用压暗的档位色、且淡得少一点），由 <see cref="SetStyle"/> 更新。
/// </summary>
public class NoiseLevelSlotItem : INotifyPropertyChanged
{
    private bool _isCurrent;
    private IBrush _brush;
    private IBrush _currentBrush;
    private double _fadeOpacity = 0.35;

    public NoiseLevelSlotItem(string text, IBrush brush, IBrush currentBrush)
    {
        Text = text;
        _brush = brush;
        _currentBrush = currentBrush;
    }

    /// <summary>档位名（安静/良好/一般/吵闹/嘈杂）。</summary>
    public string Text { get; }

    /// <summary>未达到该档时的文字色（深色主题用原档位色，明亮主题用压暗版）。</summary>
    public IBrush Brush
    {
        get => _brush;
        private set { _brush = value; OnPropertyChanged(nameof(Brush)); OnPropertyChanged(nameof(TextBrush)); }
    }

    /// <summary>已达到该档（当前档位）时的强调色；只有「一般」与 <see cref="Brush"/> 不同。</summary>
    public IBrush CurrentBrush
    {
        get => _currentBrush;
        private set { _currentBrush = value; OnPropertyChanged(nameof(CurrentBrush)); OnPropertyChanged(nameof(TextBrush)); }
    }

    /// <summary>实际画出来的文字色：当前档位用强调色，否则用未达到色。</summary>
    public IBrush TextBrush => IsCurrent ? CurrentBrush : Brush;

    /// <summary>非当前档位的淡化程度（浅底上要淡得少些，否则糊在背景里）。</summary>
    public double FadeOpacity
    {
        get => _fadeOpacity;
        private set { _fadeOpacity = value; OnPropertyChanged(nameof(FadeOpacity)); OnPropertyChanged(nameof(TextOpacity)); }
    }

    /// <summary>换主题：同时更新未达到色 / 已达到色与淡化程度。</summary>
    public void SetStyle(IBrush brush, IBrush currentBrush, double fadeOpacity)
    {
        Brush = brush;
        CurrentBrush = currentBrush;
        FadeOpacity = fadeOpacity;
    }

    /// <summary>是否当前档位。变化时同时触发文字色/透明度/字号刷新。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged(nameof(IsCurrent));
            OnPropertyChanged(nameof(TextBrush));
            OnPropertyChanged(nameof(TextOpacity));
            OnPropertyChanged(nameof(TextFontSize));
        }
    }

    /// <summary>当前档位全亮、其余淡化。</summary>
    public double TextOpacity => IsCurrent ? 1.0 : FadeOpacity;

    /// <summary>当前档位放大、其余缩小。</summary>
    public double TextFontSize => IsCurrent ? 19 : 15;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
