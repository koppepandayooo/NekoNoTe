using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NekoNoTe.Models;
using NekoNoTe.Services;
using Brush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using ShapePath = System.Windows.Shapes.Path;

namespace NekoNoTe.Earthquake;

/// <summary>地震タブ(一覧・震度分布・詳細・統計)。NekoNoTe の地震タブと同じもの</summary>
public partial class EarthquakePanel : System.Windows.Controls.UserControl
{
    private sealed record StatBarRow(string Label, int Count, double BarWidth, Brush BarBrush);

    private AlertHistoryStore _history = null!;
    private ListCollectionView _earthquakeView = null!;
    private readonly Dictionary<string, ShapePath> _mapPrefPaths = new();
    private readonly SolidColorBrush _mapBaseFill = new(MediaColor.FromRgb(0x1c, 0x20, 0x2a));
    private DispatcherTimer? _earthquakeCountdownTimer;
    private EarthquakeDetail? _activeEarthquakeDetail;
    private Canvas? _epicenterMarker;

    public EarthquakePanel() => InitializeComponent();

    public void Attach(AlertHistoryStore history)
    {
        _history = history;
        BuildEarthquakeMap();
        _earthquakeView = new ListCollectionView(history.Items)
        {
            Filter = item => item is AlertInfo alert && alert.Kind is AlertKind.Earthquake or AlertKind.EewWarning or AlertKind.Tsunami,
        };
        _earthquakeView.SortDescriptions.Add(new SortDescription(nameof(AlertInfo.ReportedAt), ListSortDirection.Descending));
        _earthquakeView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AlertInfo.LocalReportDate)));
        EarthquakeList.ItemsSource = _earthquakeView;
        history.Items.CollectionChanged += (_, _) =>
        {
            UpdateEmptyEarthquakeVisibility();
            UpdateEarthquakeStats();
        };
        UpdateEmptyEarthquakeVisibility();
        UpdateEarthquakeStats();
    }

    public void SelectAlert(AlertInfo alert) => EarthquakeList.SelectedItem = alert;

    private void UpdateEmptyEarthquakeVisibility()
    {
        var hasItems = _earthquakeView.Cast<object>().Any();
        EmptyEarthquakeText.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        if (hasItems && EarthquakeList.SelectedItem is null)
        {
            EarthquakeList.SelectedIndex = 0;
        }
    }

    private void EarthquakeSubTab_Click(object sender, RoutedEventArgs e)
    {
        var showStats = ReferenceEquals(sender, EarthquakeSubTabStatsButton);
        EarthquakeListView.Visibility = showStats ? Visibility.Collapsed : Visibility.Visible;
        EarthquakeStatsView.Visibility = showStats ? Visibility.Visible : Visibility.Collapsed;

        // 選ばれていない方はスタイル指定を外し、テーマ(WPF-UI)の標準ボタンの見た目に戻す。
        var selectedStyle = (Style)FindResource("AccentButtonStyle");
        var (selected, normal) = showStats
            ? (EarthquakeSubTabStatsButton, EarthquakeSubTabListButton)
            : (EarthquakeSubTabListButton, EarthquakeSubTabStatsButton);
        selected.Style = selectedStyle;
        normal.ClearValue(StyleProperty);
    }

    private static readonly (int Scale, string Label)[] ScaleBuckets =
    {
        (10, "震度1"), (20, "震度2"), (30, "震度3"), (40, "震度4"),
        (45, "震度5弱"), (50, "震度5強"), (55, "震度6弱"), (60, "震度6強"), (70, "震度7"),
    };

    private const double StatBarMaxWidth = 190;

    /// <summary>地震タブの「統計」サブタブを、履歴にある地震情報から集計し直す。</summary>
    private void UpdateEarthquakeStats()
    {
        // 緊急地震速報は同じ地震の地震情報と二重計上になるため、集計は地震情報(確定報)のみ。
        var quakes = _history.Items.Where(a => a.Kind == AlertKind.Earthquake).ToList();
        var accentBrush = (Brush)FindResource("AccentBrush");

        EarthquakeStatsSummaryText.Text = quakes.Count == 0
            ? "集計対象の地震情報がありません"
            : $"集計対象: 直近の地震情報 {quakes.Count}件 ({quakes.Min(a => a.ReportedAt).ToLocalTime():MM/dd HH:mm} ~ {quakes.Max(a => a.ReportedAt).ToLocalTime():MM/dd HH:mm})";

        var hypocenterGroups = quakes
            .Select(a => a.Detail?.HypocenterName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .GroupBy(name => name!)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(10)
            .ToList();
        StatsHypocenterEmptyText.Visibility = hypocenterGroups.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        StatsHypocenterList.ItemsSource = BuildStatRows(
            hypocenterGroups.Select(x => (x.Name, x.Count, accentBrush)).ToList());

        var scaleCounts = quakes
            .Select(a => a.Detail?.Points is { Count: > 0 } points ? (int?)points.Max(p => p.Scale) : null)
            .Where(s => s.HasValue)
            .GroupBy(s => s!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
        StatsScaleList.ItemsSource = BuildStatRows(ScaleBuckets
            .Select(b => (b.Label, scaleCounts.GetValueOrDefault(b.Scale), (Brush)new SolidColorBrush(ScaleColor(b.Scale))))
            .ToList());

        var today = DateTime.Today;
        var dailyCounts = quakes
            .GroupBy(a => a.ReportedAt.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Count());
        StatsDailyList.ItemsSource = BuildStatRows(Enumerable.Range(0, 14)
            .Select(offset => today.AddDays(-offset))
            .Select(day => (day.ToString("MM/dd(ddd)"), dailyCounts.GetValueOrDefault(day), accentBrush))
            .ToList());
    }

    private static List<StatBarRow> BuildStatRows(IReadOnlyList<(string Label, int Count, Brush Brush)> rows)
    {
        var max = rows.Count > 0 ? rows.Max(r => r.Count) : 0;
        return rows
            .Select(r => new StatBarRow(
                r.Label, r.Count,
                r.Count == 0 || max == 0 ? 0 : Math.Max(3, r.Count / (double)max * StatBarMaxWidth),
                r.Brush))
            .ToList();
    }

    private void EarthquakeList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateEarthquakeDetail(EarthquakeList.SelectedItem as AlertInfo);

    private void DetailDismissButton_Click(object sender, RoutedEventArgs e)
    {
        EarthquakeList.SelectedItem = null;
        UpdateEarthquakeDetail(null);
    }

    private void UpdateEarthquakeDetail(AlertInfo? alert)
    {
        foreach (var path in _mapPrefPaths.Values)
        {
            path.Fill = _mapBaseFill;
        }

        _earthquakeCountdownTimer?.Stop();
        _earthquakeCountdownTimer = null;
        _activeEarthquakeDetail = null;
        HideEarthquakeMapOverlay();
        TsunamiDetailBorder.Visibility = Visibility.Collapsed;

        if (alert?.Kind == AlertKind.Tsunami)
        {
            EarthquakeDetailGrid.Visibility = Visibility.Collapsed;
            NoDetailSelectedText.Visibility = Visibility.Collapsed;
            TsunamiDetailBorder.Visibility = Visibility.Visible;
            TsunamiDetailTitleText.Text = alert.Title;
            TsunamiDetailTimeText.Text = alert.ReportedAt.ToLocalTime().ToString("yyyy/MM/dd(ddd) HH:mm:ss");
            TsunamiDetailBodyText.Text = alert.Body;
            return;
        }

        if (alert?.Detail is not { } detail)
        {
            EarthquakeDetailGrid.Visibility = Visibility.Collapsed;
            NoDetailSelectedText.Visibility = Visibility.Visible;
            return;
        }

        NoDetailSelectedText.Visibility = Visibility.Collapsed;
        EarthquakeDetailGrid.Visibility = Visibility.Visible;
        _activeEarthquakeDetail = detail;
        UpdateEarthquakeMapOverlay(detail);

        // 取消済み、または主要動の到達予想時刻から一定時間たった速報では、カウントダウンを出さない。
        var showCountdown = detail.ArrivalTime is { } arrival
            && !alert.IsCancelled
            && DateTimeOffset.Now - arrival < CountdownHideAfter;
        DetailCountdownBorder.Visibility = showCountdown ? Visibility.Visible : Visibility.Collapsed;

        if (showCountdown)
        {
            UpdateEarthquakeCountdown(detail.ArrivalTime!.Value);
            _earthquakeCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _earthquakeCountdownTimer.Tick += (_, _) =>
            {
                if (_activeEarthquakeDetail is not { } activeDetail) return;
                if (activeDetail.ArrivalTime is { } activeArrival) UpdateEarthquakeCountdown(activeArrival);
            };
            _earthquakeCountdownTimer.Start();
        }

        DetailTitleText.Text = alert.EarthquakeSummaryLabel;
        DetailTimeText.Text = alert.ReportedAt.ToLocalTime().ToString("yyyy/MM/dd(ddd) HH:mm:ss");
        DetailHypocenterText.Text = detail.HypocenterName ?? "不明";
        DetailDepthText.Text = detail.Depth is { } depth ? $"{depth:0}km" : "不明";
        DetailMagnitudeText.Text = detail.Magnitude is { } mag ? $"M{mag:0.0}" : "不明";
        DetailCoordinateText.Text = detail.Latitude is { } lat && detail.Longitude is { } lon
            ? $"北緯{lat:0.0}° 東経{lon:0.0}°"
            : "不明";
        DetailTsunamiText.Text = FormatTsunami(detail.DomesticTsunami);
        DetailPointsList.ItemsSource = detail.Points;

        foreach (var group in detail.Points.GroupBy(p => p.Prefecture))
        {
            if (_mapPrefPaths.TryGetValue(group.Key, out var path))
            {
                var maxScale = group.Max(p => p.Scale);
                path.Fill = new SolidColorBrush(ScaleColor(maxScale));
            }
        }
    }

    /// <summary>主要動の到達予想時刻(0秒)を過ぎてから、カウントダウン表示を消すまでの時間。</summary>
    private static readonly TimeSpan CountdownHideAfter = TimeSpan.FromMinutes(10);

    private void UpdateEarthquakeCountdown(DateTimeOffset arrival)
    {
        if (DateTimeOffset.Now - arrival >= CountdownHideAfter)
        {
            DetailCountdownBorder.Visibility = Visibility.Collapsed;
            _earthquakeCountdownTimer?.Stop();
            _earthquakeCountdownTimer = null;
            return;
        }

        var remaining = (arrival - DateTimeOffset.Now).TotalSeconds;
        if (remaining > 0)
        {
            DetailCountdownNumberText.Text = Math.Ceiling(remaining).ToString("0");
            DetailCountdownCaptionText.Text = "秒後(推定)";
            return;
        }

        DetailCountdownNumberText.Text = "0";
        DetailCountdownCaptionText.Text = "まもなく、または既に到達(推定)";

    }

    private void HideEarthquakeMapOverlay()
    {
        if (_epicenterMarker is not null) _epicenterMarker.Visibility = Visibility.Collapsed;
    }

    private void UpdateEarthquakeMapOverlay(EarthquakeDetail detail)
    {
        if (_epicenterMarker is null ||
            detail.Latitude is not { } latitude || detail.Longitude is not { } longitude)
        {
            HideEarthquakeMapOverlay();
            return;
        }

        var (x, y) = ProjectToJapanMap(latitude, longitude);
        if (x is < 0 or > JapanMapData.ViewWidth || y is < 0 or > JapanMapData.ViewHeight)
        {
            HideEarthquakeMapOverlay();
            return;
        }

        Canvas.SetLeft(_epicenterMarker, x - _epicenterMarker.Width / 2);
        Canvas.SetTop(_epicenterMarker, y - _epicenterMarker.Height / 2);
        _epicenterMarker.Visibility = Visibility.Visible;

    }

    private static (double X, double Y) ProjectToJapanMap(double latitude, double longitude)
    {
        // JapanMapDataの簡略図に合わせたアフィン変換。離島を含む図形位置から校正している。
        var x = 21.47 * longitude + 4.91 * latitude - 2790.0;
        var y = 1458.0 - 30.86 * latitude;
        return (x, y);
    }

    private static string FormatTsunami(string? code) => code switch
    {
        null => "情報なし",
        "None" => "津波の心配なし",
        "Unknown" => "不明",
        "Checking" => "調査中",
        "NonEffective" => "若干の海面変動の可能性(被害の心配なし)",
        "Watch" => "津波注意報の発表対象",
        "Warning" => "津波警報級の可能性あり",
        _ => code,
    };

    private void BuildEarthquakeMap()
    {
        var stroke = (System.Windows.Media.Brush)FindResource("BorderBrush2");

        EarthquakeMapHost.Width = JapanMapData.ViewWidth;
        EarthquakeMapHost.Height = JapanMapData.ViewHeight;
        EarthquakeMapHost.ClipToBounds = true;

        foreach (var (name, data) in JapanMapData.PrefecturePaths)
        {
            var path = new ShapePath
            {
                Data = Geometry.Parse(data),
                Fill = _mapBaseFill,
                Stroke = stroke,
                StrokeThickness = 0.6,
            };
            EarthquakeMapHost.Children.Add(path);
            _mapPrefPaths[name] = path;
        }


        _epicenterMarker = new Canvas
        {
            Width = 34,
            Height = 34,
            IsHitTestVisible = false,
        };
        var epicenterStroke = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x3B, 0x30));
        _epicenterMarker.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 4, Y1 = 4, X2 = 30, Y2 = 30,
            Stroke = epicenterStroke, StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        _epicenterMarker.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 30, Y1 = 4, X2 = 4, Y2 = 30,
            Stroke = epicenterStroke, StrokeThickness = 6,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        EarthquakeMapHost.Children.Add(_epicenterMarker);
        HideEarthquakeMapOverlay();
    }

    private static MediaColor ScaleColor(int scale) => scale switch
    {
        >= 70 => MediaColor.FromRgb(0x7C, 0x3A, 0xED),
        >= 60 => MediaColor.FromRgb(0xB9, 0x1C, 0x1C),
        >= 55 => MediaColor.FromRgb(0xDC, 0x26, 0x26),
        >= 50 => MediaColor.FromRgb(0xEF, 0x44, 0x44),
        >= 45 => MediaColor.FromRgb(0xF9, 0x73, 0x16),
        >= 40 => MediaColor.FromRgb(0xEA, 0xB3, 0x08),
        >= 30 => MediaColor.FromRgb(0x84, 0xCC, 0x16),
        >= 20 => MediaColor.FromRgb(0x22, 0xC5, 0x5E),
        >= 10 => MediaColor.FromRgb(0x3B, 0x82, 0xF6),
        _ => MediaColor.FromRgb(0x1C, 0x20, 0x2A),
    };
}
