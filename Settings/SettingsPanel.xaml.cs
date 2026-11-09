using System.Diagnostics;
using System.IO;
using System.Windows;
using NekoNoTe.Updates;

namespace NekoNoTe.Settings;

public partial class SettingsPanel : System.Windows.Controls.UserControl
{
    private const string RepositoryUrl = "https://github.com/koppepandayooo/NekoNoTe";

    private static readonly (string Key, string Label)[] Intensities =
    {
        ("1", "震度1 以上"), ("2", "震度2 以上"), ("3", "震度3 以上"), ("4", "震度4 以上"),
        ("5-", "震度5弱 以上"), ("5+", "震度5強 以上"), ("6-", "震度6弱 以上"), ("6+", "震度6強 以上"), ("7", "震度7"),
    };

    private static readonly string[] Prefectures =
    {
        "北海道", "青森県", "岩手県", "宮城県", "秋田県", "山形県", "福島県", "茨城県", "栃木県", "群馬県", "埼玉県", "千葉県", "東京都", "神奈川県",
        "新潟県", "富山県", "石川県", "福井県", "山梨県", "長野県", "岐阜県", "静岡県", "愛知県", "三重県", "滋賀県", "京都府", "大阪府", "兵庫県",
        "奈良県", "和歌山県", "鳥取県", "島根県", "岡山県", "広島県", "山口県", "徳島県", "香川県", "愛媛県", "高知県", "福岡県", "佐賀県", "長崎県",
        "熊本県", "大分県", "宮崎県", "鹿児島県", "沖縄県",
    };

    private readonly bool _ready;

    public SettingsPanel()
    {
        InitializeComponent();
        var s = AppSettings.Current;
        CloseToTrayToggle.IsChecked = s.CloseToTray;
        SourceCombo.SelectedIndex = s.QuakeSource switch { "Direct" => 1, "Off" => 2, _ => 0 };
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) return;
            var blocked = await Task.Run(FirewallCheck.IsBlocked);
            FirewallText.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        };
        foreach (var (_, label) in Intensities) IntensityCombo.Items.Add(label);
        IntensityCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Intensities, i => i.Key == s.MinEarthquakeIntensity));
        AreaCombo.Items.Add("(設定しない)");
        foreach (var p in Prefectures) AreaCombo.Items.Add(p);
        AreaCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Prefectures, s.AreaName) + 1);
        VolumeSlider.Value = s.NotificationVolume * 100;
        VolumeText.Text = $"{VolumeSlider.Value:0}%";
        _ready = true;
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        UpdateCheckToggle.IsChecked = AppSettings.Current.CheckUpdatesOnStartup;
        DataPathText.Text = AppSettings.DataDir;
        VersionText.Text = "NekoNoTe v" + UpdateService.CurrentVersion.ToString(3);
    }

    private void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CloseToTray = CloseToTrayToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void Source_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.QuakeSource = (SourceCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "Auto";
        AppSettings.Current.Save();
        ((App)System.Windows.Application.Current).RestartQuakeService();
    }

    private void Intensity_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready || IntensityCombo.SelectedIndex < 0) return;
        AppSettings.Current.MinEarthquakeIntensity = Intensities[IntensityCombo.SelectedIndex].Key;
        AppSettings.Current.Save();
    }

    private void Area_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        AppSettings.Current.AreaName = AreaCombo.SelectedIndex > 0 ? Prefectures[AreaCombo.SelectedIndex - 1] : "";
        AppSettings.Current.Save();
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        VolumeText.Text = $"{VolumeSlider.Value:0}%";
        AppSettings.Current.NotificationVolume = VolumeSlider.Value / 100;
        AppSettings.Current.Save();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e) =>
        ((App)System.Windows.Application.Current).ShowTestNotification();

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartupToggle.IsChecked == true);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StartupToggle.IsChecked = StartupRegistration.IsEnabled;
            ErrorText.Text = "変更できませんでした: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.CheckUpdatesOnStartup = UpdateCheckToggle.IsChecked == true;
        AppSettings.Current.Save();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.DataDir);
        Open(AppSettings.DataDir);
    }

    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(RepositoryUrl);
    private void License_Click(object sender, RoutedEventArgs e) => Open(RepositoryUrl + "/blob/main/LICENSE");

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
