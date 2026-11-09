using System.Windows;
using System.Windows.Threading;
using NekoNoTe.Models;
using NekoNoTe.Roblox;
using NekoNoTe.Services;

namespace NekoNoTe.Home;

/// <summary>起動した時に最初に出る画面。地震の状態と Roblox のアカウント数をまとめて見せる</summary>
public partial class HomePanel : System.Windows.Controls.UserControl
{
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };
    private AlertHistoryStore _history = null!;

    public event Action? OpenEarthquakeRequested;
    public event Action? OpenRobloxRequested;
    public event Action? OpenSettingsRequested;

    public HomePanel()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
    }

    public void Attach(AlertHistoryStore history)
    {
        _history = history;
        history.Items.CollectionChanged += (_, _) => UpdateRecentEarthquake();
        UpdateRecentEarthquake();
        RobloxAccounts.Changed += () => Dispatcher.BeginInvoke(UpdateRoblox);
        UpdateRoblox();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm");
        DateText.Text = now.ToString("yyyy年M月d日(ddd)");
        GreetingText.Text = now.Hour switch { >= 5 and < 11 => "おはようございます", >= 11 and < 18 => "こんにちは", _ => "こんばんは" };
    }

    private void UpdateRecentEarthquake()
    {
        var latest = _history.Items
            .Where(a => a.Kind is AlertKind.Earthquake or AlertKind.EewWarning && !a.IsCancelled)
            .OrderByDescending(a => a.ReportedAt)
            .FirstOrDefault();
        RecentEmptyText.Visibility = latest is null ? Visibility.Visible : Visibility.Collapsed;
        RecentContent.Visibility = latest is null ? Visibility.Collapsed : Visibility.Visible;
        if (latest is null) return;
        RecentTitleText.Text = latest.EarthquakeSummaryLabel;
        RecentPlaceText.Text = latest.Detail?.HypocenterName is { Length: > 0 } name ? name : "不明";
        RecentTimeText.Text = latest.ReportedAt.ToLocalTime().ToString("MM/dd(ddd) HH:mm");
    }

    private void UpdateRoblox()
    {
        var count = RobloxAccounts.Items.Count;
        RobloxCountText.Text = count > 0 ? $"アカウント {count} 個" : "アカウントなし";
        RobloxNoteText.Text = count > 0 ? "Roblox タブから、選んだアカウントで起動できます。" : "Roblox タブでログインすると、ここに数が出ます。";
    }

    public void SetQuakeStatus(bool connected, string text)
    {
        var brush = (System.Windows.Media.Brush)FindResource(connected ? "AccentBrush" : "TextSecondaryBrush");
        QuakeDot.Fill = brush;
        QuakeStatusText.Text = text;
        var off = text == "オフ";
        StatusLineText.Text = off ? "地震情報の受信はオフです" : connected ? "地震情報を受信しています" : "地震情報に再接続しています…";
        QuakeNoteText.Text = off
            ? "設定の «受信のしかた» でオンにできます。"
            : "地震が起きると、自動で通知します。遅れたり届かないことがあるので、スマホの緊急速報もオンにしておいてください。";
    }

    private void OpenEarthquake_Click(object sender, RoutedEventArgs e) => OpenEarthquakeRequested?.Invoke();
    private void OpenRoblox_Click(object sender, RoutedEventArgs e) => OpenRobloxRequested?.Invoke();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsRequested?.Invoke();
}
