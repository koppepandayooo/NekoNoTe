using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfColor = System.Windows.Media.Color;

namespace NekoNoTe.Recorder;

/// <summary>«保存しました» を画面の下に少しだけ出す(フォーカスもクリックも奪わない。ゲーム中でも邪魔しない)</summary>
internal sealed class RecorderToast : Window
{
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 12, Foreground = new SolidColorBrush(WpfColor.FromArgb(0xC5, 0xFF, 0xFF, 0xFF)) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1300) };

    public RecorderToast()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        var icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Record24, FontSize = 18, Foreground = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x6B, 0x6B)), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        var text = new StackPanel { Children = { _title, _sub }, MaxWidth = 420 };
        Content = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 10, 20, 11), Margin = new Thickness(12),
            BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(WpfColor.FromArgb(0xEE, 0x22, 0x22, 0x2A)),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.5 },
            Child = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { icon, text } },
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { if (!_hide.IsEnabled) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x20 | 0x80000 | 0x08000000 | 0x80);   // クリックを通す・前に出ない
        };
    }

    public void Show(string title, string sub)
    {
        _title.Text = title;
        _sub.Text = sub;
        if (!IsVisible) Show();
        UpdateLayout();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Bottom - ActualHeight - 80;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _hide.Stop();
        _hide.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);
}
