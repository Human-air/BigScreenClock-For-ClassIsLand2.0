using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 更新后弹一次的计数算法选择窗口：V3（连续/窗口锁定）或 V2（整段占比），
/// 顺便询问是否上传选择结果（只上传算法与插件版本）。
/// </summary>
public partial class NoiseAlgorithmChoiceWindow : Window
{
    private readonly PluginSettings _settings;
    private bool _saved;

    public NoiseAlgorithmChoiceWindow(PluginSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        V3Option.IsChecked = settings.NoiseAlgorithmVersion != 2;
        V2Option.IsChecked = settings.NoiseAlgorithmVersion == 2;
        ShareCheck.IsChecked = settings.ShareAlgorithmChoice;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            SaveAndClose();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 直接关窗也按当前选择保存，避免下次再弹
        if (!_saved) Save();
        base.OnClosing(e);
    }

    private void Confirm_Click(object? sender, RoutedEventArgs e) => SaveAndClose();

    private void SaveAndClose()
    {
        if (!_saved) Save();
        Close();
    }

    private void Save()
    {
        _saved = true;
        _settings.NoiseAlgorithmVersion = V3Option.IsChecked == true ? 3 : 2;
        _settings.ShareAlgorithmChoice = ShareCheck.IsChecked == true;
        _settings.AlgorithmChoicePrompted = true;
        Plugin.SaveSettings();
        AlgorithmChoiceReporter.TrySend(_settings);
    }
}
