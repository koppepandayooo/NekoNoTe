using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NekoNoTe.Overlay;
using NekoNoTe.Updates;

namespace NekoNoTe.Settings;

public partial class SettingsPanel : System.Windows.Controls.UserControl
{
    private const string RepositoryUrl = "https://github.com/koppepandayooo/NekoNoTe";

    public sealed record MonitorItem(string Label, string DeviceName)
    {
        public override string ToString() => Label;   // 閉じている時の表示に使われる
    }
    public sealed record OptionItem(string Name, string Key)
    {
        public override string ToString() => Name;
    }

    private static readonly OptionItem[] Intensities =
    {
        new("震度1以上", "1"), new("震度2以上", "2"), new("震度3以上", "3"), new("震度4以上", "4"),
        new("震度5弱以上", "5-"), new("震度5強以上", "5+"), new("震度6弱以上", "6-"), new("震度6強以上", "6+"), new("震度7", "7"),
        // 地震情報と緊急地震速報の予報は通知しない(緊急地震速報の警報だけ通知する)
        new("通知しない(緊急地震速報の警報だけ)", "none"),
    };

    private static readonly OptionItem[] Corners =
    {
        new("右上", "TopRight"), new("左上", "TopLeft"), new("右下", "BottomRight"), new("左下", "BottomLeft"),
    };

    private static readonly OptionItem[] Sounds =
    {
        new("なし", "None"), new("チャイム(3音)", "Chime"), new("ポップ(短い2音)", "Beep"), new("ベル(きれいめ)", "Bell"),
        new("ソフト(やわらかい)", "Soft"), new("アラート(目立つ)", "Alert"),
    };

    private static readonly string[] Prefectures =
    {
        "北海道", "青森県", "岩手県", "宮城県", "秋田県", "山形県", "福島県", "茨城県", "栃木県", "群馬県", "埼玉県", "千葉県", "東京都", "神奈川県",
        "新潟県", "富山県", "石川県", "福井県", "山梨県", "長野県", "岐阜県", "静岡県", "愛知県", "三重県", "滋賀県", "京都府", "大阪府", "兵庫県",
        "奈良県", "和歌山県", "鳥取県", "島根県", "岡山県", "広島県", "山口県", "徳島県", "香川県", "愛媛県", "高知県", "福岡県", "佐賀県", "長崎県",
        "熊本県", "大分県", "宮崎県", "鹿児島県", "沖縄県",
    };

    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly bool _ready;

    /// <summary>背景の画像・ぼかし・暗さが変わった時</summary>
    public event Action? BackgroundChanged;

    private static AppSettings S => AppSettings.Current;
    private static App CurrentApp => (App)System.Windows.Application.Current;

    public SettingsPanel()
    {
        InitializeComponent();
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); SaveAll(); };

        var monitors = System.Windows.Forms.Screen.AllScreens
            .Select((s, i) => new MonitorItem($"モニター{i + 1}: {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? "(メイン)" : "")}", s.DeviceName))
            .ToList();
        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedIndex = Math.Max(0, monitors.FindIndex(m => m.DeviceName == S.MonitorDeviceName));
        CornerCombo.ItemsSource = Corners;
        CornerCombo.SelectedItem = Corners.FirstOrDefault(o => o.Key == S.NotificationCorner) ?? Corners[0];
        DurationSlider.Value = S.NotificationDurationSeconds;
        SoundCombo.ItemsSource = Sounds;
        SoundCombo.SelectedItem = Sounds.FirstOrDefault(o => o.Key == S.NotificationSound) ?? Sounds[1];
        VolumeSlider.Value = S.NotificationVolume * 100;

        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        CloseToTrayToggle.IsChecked = S.CloseToTray;
        UpdateCheckToggle.IsChecked = S.CheckUpdatesOnStartup;

        BlurSlider.Value = Math.Clamp(S.BackgroundBlur, 0, 100);
        DimSlider.Value = Math.Clamp(S.BackgroundDim, 0, 100);
        UpdateBackgroundPathText();

        SourceCombo.SelectedIndex = S.QuakeSource switch { "Direct" => 1, "Off" => 2, _ => 0 };
        WolfxToggle.IsChecked = S.UseWolfxEew;
        IntensityCombo.ItemsSource = Intensities;
        IntensityCombo.SelectedItem = Intensities.FirstOrDefault(o => o.Key == S.MinEarthquakeIntensity) ?? Intensities[2];
        AreaCombo.Items.Add("(設定しない)");
        foreach (var p in Prefectures) AreaCombo.Items.Add(p);
        AreaCombo.SelectedIndex = Math.Max(0, Array.IndexOf(Prefectures, S.AreaName) + 1);

        DataPathText.Text = AppSettings.DataDir;
        VersionText.Text = "NekoNoTe v" + UpdateService.CurrentVersion.ToString(3);

        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) return;
            var blocked = await Task.Run(FirewallCheck.IsBlocked);
            FirewallText.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        };
        _ready = true;
    }

    private void ScheduleSave()
    {
        if (!_ready) return;
        _autoSave.Stop();
        _autoSave.Start();
    }

    private void SaveAll()
    {
        S.MonitorDeviceName = (MonitorCombo.SelectedItem as MonitorItem)?.DeviceName;
        S.NotificationCorner = (CornerCombo.SelectedItem as OptionItem)?.Key ?? S.NotificationCorner;
        S.NotificationDurationSeconds = (int)DurationSlider.Value;
        S.NotificationSound = (SoundCombo.SelectedItem as OptionItem)?.Key ?? S.NotificationSound;
        S.NotificationVolume = VolumeSlider.Value / 100;
        S.CloseToTray = CloseToTrayToggle.IsChecked == true;
        S.CheckUpdatesOnStartup = UpdateCheckToggle.IsChecked == true;
        S.MinEarthquakeIntensity = (IntensityCombo.SelectedItem as OptionItem)?.Key ?? S.MinEarthquakeIntensity;
        S.AreaName = AreaCombo.SelectedIndex > 0 ? Prefectures[AreaCombo.SelectedIndex - 1] : "";
        S.Save();
        ShowSavedFeedback();
    }

    private void ShowSavedFeedback()
    {
        SavedText.Visibility = Visibility.Visible;
        SavedText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
            fadeOut.Completed += (_, _) => SavedText.Visibility = Visibility.Collapsed;
            SavedText.BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }

    private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e) => ScheduleSave();
    private void Setting_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ScheduleSave();
    private void Option_Click(object sender, RoutedEventArgs e) => ScheduleSave();

    private void SoundCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScheduleSave();
        if (_ready && SoundCombo.SelectedItem is OptionItem sound) NotificationSoundPlayer.Play(sound.Key, VolumeSlider.Value / 100);
    }

    private void Source_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        S.QuakeSource = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto";
        S.Save();
        ShowSavedFeedback();
        CurrentApp.RestartQuakeService();
    }

    private void Wolfx_Click(object sender, RoutedEventArgs e)
    {
        S.UseWolfxEew = WolfxToggle.IsChecked == true;
        S.Save();
        ShowSavedFeedback();
        CurrentApp.ApplyWolfxSetting();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupRegistration.Set(StartupToggle.IsChecked == true);
            ErrorText.Visibility = Visibility.Collapsed;
            ShowSavedFeedback();
        }
        catch (Exception ex)
        {
            StartupToggle.IsChecked = StartupRegistration.IsEnabled;
            ErrorText.Text = "変更できませんでした: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    // ---------- テスト ----------

    /// <summary>まだ保存されていない変更も反映して試す</summary>
    private void SaveBeforeTest()
    {
        _autoSave.Stop();
        SaveAll();
    }

    private void TestNotification_Click(object sender, RoutedEventArgs e)
    {
        SaveBeforeTest();
        CurrentApp.ShowTestNotification();
    }

    private void TestEew_Click(object sender, RoutedEventArgs e)
    {
        SaveBeforeTest();
        CurrentApp.ShowTestEew();
    }

    // ---------- 背景 ----------

    private void UpdateBackgroundPathText() =>
        BackgroundPathText.Text = string.IsNullOrEmpty(S.BackgroundImagePath) ? "未設定" : Path.GetFileName(S.BackgroundImagePath);

    private void Background_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        S.BackgroundBlur = BlurSlider.Value;
        S.BackgroundDim = DimSlider.Value;
        BackgroundChanged?.Invoke();
        ScheduleSave();
    }

    private void ChooseBackground_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "背景画像を選択",
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.webp|すべてのファイル|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        SetBackground(dialog.FileName);
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e) => SetBackground(null);

    private void SetBackground(string? path)
    {
        S.BackgroundImagePath = path;
        UpdateBackgroundPathText();
        BackgroundChanged?.Invoke();
        S.Save();
        ShowSavedFeedback();
    }

    // ---------- データ・このアプリについて ----------

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
