using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows;
using NekoNoTe.Models;
using NekoNoTe.Overlay;
using NekoNoTe.Roblox;
using NekoNoTe.Services;
using NekoNoTe.Settings;
using Forms = System.Windows.Forms;

namespace NekoNoTe;

public partial class App : System.Windows.Application
{
    public const string BackgroundArgument = "--background";

    private Mutex? _single;
    private MainWindow _mainWindow = null!;
    private Forms.NotifyIcon? _tray;
    private AlertHistoryStore _history = null!;
    private AlertStateStore _stateStore = null!;
    private NotificationManager _notifications = null!;
    private P2pQuakeService _quake = null!;
    private readonly HashSet<DateTimeOffset> _notifiedEarthquakeTimes = new();

    /// <summary>本当に終わる時(トレイに隠さない)</summary>
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "NekoNoTe.SingleInstance", out var first);
        if (!first)
        {
            System.Windows.MessageBox.Show("NekoNoTe はもう起動しています。", "NekoNoTe");
            Shutdown();
            return;
        }
        NekoNoTe.Behaviors.Motion.Init();
        RobloxPanel.ApplyStartupSettings();

        _history = new AlertHistoryStore();
        _stateStore = new AlertStateStore();
        _notifications = new NotificationManager(
            () => Forms.Screen.PrimaryScreen!, () => 7, () => NotificationCorner.TopRight,
            () => "Chime", () => AppSettings.Current.NotificationVolume);

        _mainWindow = new MainWindow(_history);
        CreateTray();

        // 作業中に Windows の確認が出ないよう、受信を始める前に初回の確認を済ませる
        _quake = new P2pQuakeService(AppSettings.Current);
        _quake.AlertReceived += alert => Dispatcher.Invoke(() => HandleNewAlert(alert, notify: true));
        _quake.StatusChanged += (connected, text) => Dispatcher.Invoke(() => _mainWindow.SetQuakeStatus(connected, text));
        _quake.Start();
        _ = CatchUpSinceLastRunAsync();

        if (!e.Args.Contains(BackgroundArgument))
        {
            _mainWindow.Show();
            _ = PrimeNetworkAsync();
        }
    }

    /// <summary>
    /// Windows のファイアウォールの確認は、初めて待ち受けた時に出る。
    /// 親を途中で引き継いだ時(作業中やゲーム中)に出ないよう、初回の起動で一度だけ待ち受けて済ませておく
    /// </summary>
    private async Task PrimeNetworkAsync()
    {
        var settings = AppSettings.Current;
        if (settings.NetworkPrimed) return;
        System.Windows.MessageBox.Show(_mainWindow,
            "地震情報を同じネットワークの PC と共有するために、このあと Windows の確認が出ることがあります。\n«プライベート ネットワーク» にチェックを入れて許可してください。",
            "NekoNoTe", MessageBoxButton.OK, MessageBoxImage.Information);
        var relay = QuakeLanRelay.Instance;
        var alreadyOn = relay.Enabled;
        if (alreadyOn || relay.SetEnabled(true))
        {
            settings.NetworkPrimed = true;
            settings.Save();
            if (!alreadyOn)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                // この間に親になっていたら止めない
                if (!_quake.IsHosting) relay.SetEnabled(false);
            }
        }
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => ShowMainWindow());
        menu.Items.Add("終了", null, (_, _) => ExitApplication());
        _tray = new Forms.NotifyIcon
        {
            Icon = Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : SystemIcons.Application,
            Text = "NekoNoTe",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseDoubleClick += (_, _) => ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ExitApplication()
    {
        IsExiting = true;
        _quake?.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        Shutdown();
    }

    /// <summary>受信方法を変えた時に、つなぎ直す</summary>
    public void RestartQuakeService()
    {
        _quake.Stop();
        _quake.Start();
    }

    public void ShowTestNotification()
    {
        _notifications.Show(new AlertInfo
        {
            Id = "test:" + Guid.NewGuid(),
            Kind = AlertKind.Earthquake,
            Title = "通知のテスト",
            Body = "地震が起きると、ここにお知らせが出ます。",
            ReportedAt = DateTimeOffset.Now,
        });
    }

    private void HandleNewAlert(AlertInfo alert, bool notify)
    {
        if (!_stateStore.IsNew(alert.Id)) return;
        _stateStore.MarkSeen(alert.Id);

        // 取消報は別件として積まず、元の緊急地震速報を «取消» に置き換える
        if (alert.CancelsAlertId is { } cancelledId)
        {
            if (_history.MarkCancelled(cancelledId) is not null && notify) _notifications.Show(alert);
            return;
        }

        _history.Add(alert);

        // 同じ地震の 2 回目以降の更新や、起動していなかった間の分は、一覧にだけ入れる
        var alreadyNotified = alert.Kind == AlertKind.Earthquake && _notifiedEarthquakeTimes.Contains(alert.ReportedAt);
        if (notify && alert.ShouldNotify && !alreadyNotified)
        {
            if (alert.Kind is AlertKind.Earthquake or AlertKind.EewWarning) _mainWindow.ShowRealtimeAlert(alert);
            _notifications.Show(alert);
            if (alert.Kind == AlertKind.Earthquake) _notifiedEarthquakeTimes.Add(alert.ReportedAt);
        }
    }

    private async Task CatchUpSinceLastRunAsync()
    {
        var since = _history.Items
            .Where(a => a.Kind is AlertKind.Earthquake or AlertKind.EewWarning or AlertKind.Tsunami)
            .Select(a => a.ReportedAt)
            .DefaultIfEmpty(DateTimeOffset.Now.AddDays(-7))
            .Max();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            foreach (var alert in await _quake.FetchMissedSinceStartupAsync(since, cts.Token))
                HandleNewAlert(alert, notify: false);
        }
        catch (OperationCanceledException) { }   // 次の起動の時に取り直す
    }

    /// <summary>インストーラーを裏で動かして自分は終わる(入れ終わるとインストーラーが NekoNoTe を起動する)</summary>
    public void RunInstallerAndExit(string installer)
    {
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS") { UseShellExecute = true });
        ExitApplication();
    }
}
