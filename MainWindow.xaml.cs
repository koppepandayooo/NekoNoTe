using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace NekoNoTe;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private FrameworkElement _activePanel;
    private string? _shownBackground;

    /// <summary>サイドバーのボタンと、開くパネルの組み合わせ</summary>
    private (ToggleButton Button, FrameworkElement Panel)[] Pages =>
        new (ToggleButton, FrameworkElement)[]
        {
            (HomeNavButton, HomePanel),
            (EarthquakeNavButton, EarthquakePanel),
            (RobloxNavButton, RobloxPanel),
            (LanShareNavButton, LanSharePanel),
            (UpdateNavButton, UpdatePanel),
            (SettingsNavButton, SettingsPanel),
        };

    public MainWindow(NekoNoTe.Services.AlertHistoryStore history)
    {
        InitializeComponent();
        // 画面が小さい PC では、はみ出さない大きさにする
        var scale = Math.Min(1, Math.Min(SystemParameters.WorkArea.Width * 0.94 / Width, SystemParameters.WorkArea.Height * 0.90 / Height));
        Width = Math.Max(MinWidth, Width * scale);
        Height = Math.Max(MinHeight, Height * scale);
        EarthquakePanel.Attach(history);
        HomePanel.Attach(history);
        SettingsPanel.BackgroundChanged += ApplyBackground;
        ApplyBackground();
        _activePanel = HomePanel;
        HomePanel.OpenEarthquakeRequested += () => NavButton_Click(EarthquakeNavButton, new RoutedEventArgs());
        HomePanel.OpenRobloxRequested += () => NavButton_Click(RobloxNavButton, new RoutedEventArgs());
        HomePanel.OpenSettingsRequested += () => NavButton_Click(SettingsNavButton, new RoutedEventArgs());
        void UpdateLanShareBadge() => LanShareNavActivity.Visibility =
            NekoNoTe.LanShare.LanShareService.Instance.Enabled ? Visibility.Visible : Visibility.Collapsed;
        NekoNoTe.LanShare.LanShareService.Instance.StateChanged += () => Dispatcher.BeginInvoke(UpdateLanShareBadge);
        UpdateLanShareBadge();
        UpdatePanel.UpdateAvailableChanged += available =>
            UpdateNavActivity.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) =>
        {
            if (NekoNoTe.Settings.AppSettings.Current.CheckUpdatesOnStartup) await UpdatePanel.CheckSilentlyAsync();
        };
    }

    /// <summary>背景画像と、そのぼかし・暗さを設定に合わせる</summary>
    private void ApplyBackground()
    {
        var settings = NekoNoTe.Settings.AppSettings.Current;
        BackgroundBlur.Radius = Math.Clamp(settings.BackgroundBlur, 0, 100);
        BackgroundShade.Opacity = Math.Clamp(settings.BackgroundDim, 0, 100) / 100.0;
        if (settings.BackgroundImagePath == _shownBackground) return;
        _shownBackground = settings.BackgroundImagePath;

        System.Windows.Media.Imaging.BitmapImage? bitmap = null;
        if (!string.IsNullOrEmpty(_shownBackground) && System.IO.File.Exists(_shownBackground))
        {
            try
            {
                bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_shownBackground, UriKind.Absolute);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or UnauthorizedAccessException)
            {
                bitmap = null;
            }
        }

        BackgroundImage.Source = bitmap;
        var show = bitmap != null ? Visibility.Visible : Visibility.Collapsed;
        BackgroundImage.Visibility = BackgroundShade.Visibility = show;
        if (bitmap != null)
        {
            // パネルをごく薄くして、背景の色がカード越しに透けるすりガラス風にする
            SidebarBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, 0x05, 0x05, 0x0A));
            ContentBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x05, 0x05, 0x0A));
        }
        else
        {
            SidebarBorder.Background = System.Windows.Media.Brushes.Transparent;
            ContentBorder.Background = (System.Windows.Media.Brush)FindResource("LayerBrush");
        }
    }

    public void ShowTestAlert(NekoNoTe.Models.AlertInfo alert)
    {
        EarthquakePanel.ShowTestAlert(alert);
        NavButton_Click(EarthquakeNavButton, new RoutedEventArgs());
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        var app = (App)System.Windows.Application.Current;
        if (app.IsExiting) return;
        if (NekoNoTe.Settings.AppSettings.Current.CloseToTray)
        {
            e.Cancel = true;   // 閉じてもトレイに残って、地震の通知を受ける
            Hide();
        }
        else app.ExitApplication();
    }

    public void SetQuakeStatus(bool connected, string text)
    {
        var brush = (System.Windows.Media.Brush)FindResource(connected ? "AccentBrush" : "TextSecondaryBrush");
        EarthquakeNavDot.Fill = brush;
        EarthquakeNavText.Foreground = brush;
        EarthquakeNavText.Text = text;
        HomePanel.SetQuakeStatus(connected, text);
    }

    /// <summary>地震が起きた時、画面が出ていれば地震タブを開いて最新の情報を選ぶ</summary>
    public void ShowRealtimeAlert(NekoNoTe.Models.AlertInfo alert)
    {
        EarthquakePanel.SelectAlert(alert);
        if (IsVisible) NavButton_Click(EarthquakeNavButton, new RoutedEventArgs());
    }

    /// <summary>履歴の項目が置き換わった時、それを選んでいたら新しい方を選び直す(詳細・カウントダウンを更新する)</summary>
    public void ReplaceAlertSelection(NekoNoTe.Models.AlertInfo oldAlert, NekoNoTe.Models.AlertInfo newAlert) =>
        EarthquakePanel.ReplaceSelection(oldAlert, newAlert);

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        var target = Pages.First(p => ReferenceEquals(p.Button, clicked)).Panel;

        // ToggleButton は選択済みでも押すと一度 OFF になるため、同じタブなら選択を戻して何もしない
        if (ReferenceEquals(_activePanel, target))
        {
            clicked.IsChecked = true;
            return;
        }

        foreach (var (button, _) in Pages) button.IsChecked = ReferenceEquals(button, clicked);
        _activePanel = target;
        CrossFadeToPanel(target);
    }

    /// <summary>タブ切り替え時、旧パネルをフェードアウトしてから新パネルをフェードインする</summary>
    private void CrossFadeToPanel(FrameworkElement target)
    {
        const int fadeOutMs = 110;
        const int fadeInMs = 180;

        foreach (var (_, panel) in Pages)
        {
            if (ReferenceEquals(panel, target) || panel.Visibility != Visibility.Visible) continue;
            var fadeOut = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(fadeOutMs));
            fadeOut.Completed += (_, _) => panel.Visibility = Visibility.Collapsed;
            panel.BeginAnimation(OpacityProperty, fadeOut);
        }

        target.Opacity = 0;
        target.Visibility = Visibility.Visible;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeInMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(fadeOutMs - 30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        target.UpdateLayout();
        NekoNoTe.Behaviors.Motion.PanelIn(target, TimeSpan.FromMilliseconds(fadeOutMs - 30));
    }
}
