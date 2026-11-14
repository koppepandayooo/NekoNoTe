using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;

namespace NekoNoTe.LanShare;

public partial class LanSharePanel : System.Windows.Controls.UserControl
{
    private static LanShareService Svc => LanShareService.Instance;
    private static readonly string DownloadsFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private IReadOnlyList<SharePeer> _peers = Array.Empty<SharePeer>();
    private SharePeer? _selected;
    private CancellationTokenSource? _transfer;
    private bool _searching;
    private Action? _messageAction;

    public LanSharePanel()
    {
        InitializeComponent();
        _searchTimer.Tick += async (_, _) => await SearchAsync(quiet: true);
        // 見えた時に探す。画面のスレッドの続きで動くように Dispatcher を通す
        // (どこから表示が切り替わっても、await の後に別のスレッドから画面を触らないように)
        IsVisibleChanged += (_, _) => Dispatcher.InvokeAsync(async () =>
        {
            if (IsVisible)
            {
                RenderLocal();
                _searchTimer.Start();
                await SearchAsync(quiet: true);
            }
            else _searchTimer.Stop();
        });
        Svc.StateChanged += () => Dispatcher.BeginInvoke(RenderLocal);
        Svc.FileReceived += (name, from) => Dispatcher.BeginInvoke(() =>
        {
            RenderLocal();
            if (IsVisible) ShowMessage($"«{name}» を受け取りました({from})");
        });
        RenderLocal();
    }

    /// <summary>アプリの起動時に、前回オンにしていたら共有をオンにする</summary>
    public static void ApplyStartupSettings()
    {
        if (Svc.Settings.Enabled) Svc.SetEnabled(true);
    }

    // ===================== この PC =====================

    private void RenderLocal()
    {
        var s = Svc.Settings;
        EnabledToggle.IsChecked = Svc.Enabled;
        PinText.Text = s.Pin;
        PinText.Opacity = Svc.Enabled ? 1 : 0.45;
        FolderText.Text = s.Folder;
        FolderText.ToolTip = s.Folder;
        StateText.Text = Svc.Enabled
            ? "オン: 同じネットワークの PC から、このフォルダのファイルを見る・受け取る・送り込むことができます(PIN が必要です)。"
            : "オフ: ほかの PC からは見えません。ほかの PC のファイルを受け取ったり送ったりするのは、オフのままでもできます。";

        var files = Svc.LocalFiles();
        LocalCountText.Text = files.Count > 0 ? $"ここにあるファイル({files.Count} 個)" : "ここにあるファイル(まだありません)";
        LocalList.Children.Clear();
        foreach (var f in files)
        {
            var row = FileRow(f, null);
            row.ToolTip = "ダブルクリックで開く";
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) Open(Path.Combine(s.Folder, f.Name));
            };
            LocalList.Children.Add(row);
        }
    }

    private void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (!Svc.SetEnabled(EnabledToggle.IsChecked == true))
        {
            EnabledToggle.IsChecked = false;
            StateText.Text = "オンにできませんでした(ポートが別のアプリに使われています)";
        }
    }

    private void ChangePin_Click(object sender, RoutedEventArgs e) => Svc.ChangePin();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Svc.Settings.Folder);
        Open(Svc.Settings.Folder);
    }

    private void Local_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;
        Directory.CreateDirectory(Svc.Settings.Folder);
        var copied = 0;
        foreach (var path in paths.Where(File.Exists))
        {
            try
            {
                var dest = Path.Combine(Svc.Settings.Folder, LanShareService.SafeName(Path.GetFileName(path)));
                if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(path, dest, overwrite: false);
                copied++;
            }
            catch (IOException) { ShowMessage($"«{Path.GetFileName(path)}» は同じ名前のファイルがあるのでコピーしませんでした", bad: true); }
            catch (UnauthorizedAccessException) { ShowMessage($"«{Path.GetFileName(path)}» をコピーできませんでした", bad: true); }
        }
        RenderLocal();
        if (copied > 0) ShowMessage($"{copied} 個を共有フォルダにコピーしました");
    }

    private void Card_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    // ===================== ほかの PC =====================

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(quiet: false);

    private async Task SearchAsync(bool quiet)
    {
        if (_searching) return;
        _searching = true;
        SearchButton.IsEnabled = false;
        try
        {
            _peers = await Svc.DiscoverAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
        {
            _peers = Array.Empty<SharePeer>();
        }
        finally
        {
            _searching = false;
            SearchButton.IsEnabled = true;
        }

        // 選んでいた PC がいなくなったら選び直す(アドレスが変わっただけなら付け替える)
        if (_selected != null)
        {
            var same = _peers.FirstOrDefault(p => p.Id == _selected.Id);
            if (same == null && _transfer == null) Select(null);
            else if (same != null) _selected = same;
        }
        RenderPeers();
        if (_selected == null && _peers.Count == 1 && _transfer == null) await SelectAsync(_peers[0]);
        if (!quiet && _peers.Count == 0) ShowMessage("見つかりませんでした");
    }

    private void RenderPeers()
    {
        PeerList.Children.Clear();
        PeerEmptyText.Visibility = _peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var peer in _peers)
        {
            var selected = peer.Id == _selected?.Id;
            var button = new WpfButton
            {
                Height = 36,
                Padding = new Thickness(14, 0, 14, 0),
                Margin = new Thickness(0, 0, 8, 8),
                Content = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Children =
                    {
                        new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Desktop24, FontSize = 15 },
                        new TextBlock { Text = peer.Name, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal },
                        new TextBlock { Text = peer.EndPoint.Address.ToString(), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Opacity = 0.6 },
                    },
                },
            };
            if (selected)
            {
                button.Background = new SolidColorBrush(WpfColor.FromArgb(0x33, 0x60, 0xCD, 0xFF));
                button.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0x99, 0x60, 0xCD, 0xFF));
            }
            button.Click += async (_, _) => await SelectAsync(peer);
            PeerList.Children.Add(button);
        }
    }

    private void Select(SharePeer? peer)
    {
        _selected = peer;
        RemoteList.Children.Clear();
        PinRow.Visibility = Visibility.Collapsed;
        RemoteHeader.Visibility = peer != null ? Visibility.Visible : Visibility.Collapsed;
        DropHintText.Visibility = peer != null ? Visibility.Visible : Visibility.Collapsed;
        if (peer != null) RemoteTitleText.Text = $"{peer.Name} の共有フォルダ";
        RenderPeers();
    }

    private async Task SelectAsync(SharePeer peer)
    {
        if (_transfer != null) return;
        Select(peer);
        await LoadRemoteAsync();
    }

    private string? PinFor(SharePeer peer) => Svc.Settings.PeerPins.TryGetValue(peer.Id, out var pin) ? pin : null;

    private void AskPin(SharePeer peer, string? reason)
    {
        PinPromptText.Text = (reason != null ? reason + "。" : "") + $"{peer.Name} の LAN 共有タブに出ている 4 桁の PIN を入れてください";
        PinRow.Visibility = Visibility.Visible;
        PinBox.Text = "";
        PinBox.Focus();
    }

    private async void PinOk_Click(object sender, RoutedEventArgs e) => await SubmitPinAsync();

    private async void PinBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SubmitPinAsync();
    }

    private async Task SubmitPinAsync()
    {
        if (_selected == null) return;
        var pin = PinBox.Text.Trim();
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            PinPromptText.Text = "4 桁の数字を入れてください";
            return;
        }
        Svc.Settings.PeerPins[_selected.Id] = pin;
        Svc.Settings.Save();
        PinRow.Visibility = Visibility.Collapsed;
        await LoadRemoteAsync();
    }

    /// <summary>選んだ PC の一覧を読み直す</summary>
    private async Task LoadRemoteAsync()
    {
        if (_selected is not { } peer) return;
        if (PinFor(peer) is not { } pin)
        {
            AskPin(peer, null);
            return;
        }
        try
        {
            var files = await Svc.ListAsync(peer, pin, CancellationToken.None);
            if (_selected?.Id != peer.Id) return;
            RemoteList.Children.Clear();
            if (files.Count == 0)
                RemoteList.Children.Add(new TextBlock { Text = "(まだ何もありません)", Style = (Style)FindResource("NoteTextStyle") });
            foreach (var f in files)
            {
                var get = new WpfButton { Content = "受け取る", Height = 28, Padding = new Thickness(10, 0, 10, 0) };
                get.Click += async (_, _) => await DownloadAsync(peer, f);
                RemoteList.Children.Add(FileRow(f, get));
            }
        }
        catch (SharePinException ex)
        {
            Svc.Settings.PeerPins.Remove(peer.Id);
            Svc.Settings.Save();
            AskPin(peer, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Text.Json.JsonException)
        {
            ShowMessage($"{peer.Name} につながりませんでした: {ex.Message}", bad: true);
        }
    }

    private async Task DownloadAsync(SharePeer peer, ShareFile file)
    {
        if (PinFor(peer) is not { } pin) { AskPin(peer, null); return; }
        await RunTransferAsync($"受け取り中: {file.Name}", async (progress, ct) =>
        {
            var saved = await Svc.DownloadAsync(peer, pin, file.Name, DownloadsFolder, progress, ct);
            ShowMessage($"«{Path.GetFileName(saved)}» を «ダウンロード» に保存しました", action: ("開く", () => OpenAndSelect(saved)));
        });
    }

    private async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "送るファイルを選ぶ", Multiselect = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        await UploadAsync(dialog.FileNames);
    }

    private async void Remote_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths) return;
        if (_selected == null)
        {
            ShowMessage("先に送り先の PC を選んでください", bad: true);
            return;
        }
        await UploadAsync(paths.Where(File.Exists).ToArray());
    }

    private async Task UploadAsync(string[] paths)
    {
        if (_selected is not { } peer || paths.Length == 0) return;
        if (PinFor(peer) is not { } pin) { AskPin(peer, null); return; }
        var sent = 0;
        await RunTransferAsync("", async (progress, ct) =>
        {
            foreach (var path in paths)
            {
                ProgressText.Text = $"送信中({sent + 1}/{paths.Length}): {Path.GetFileName(path)}";
                Progress.Value = 0;
                await Svc.UploadAsync(peer, pin, path, progress, ct);
                sent++;
            }
            ShowMessage(sent == 1 ? $"«{Path.GetFileName(paths[0])}» を {peer.Name} に送りました" : $"{sent} 個のファイルを {peer.Name} に送りました");
        });
        await LoadRemoteAsync();
    }

    /// <summary>送る・受け取るを 1 つずつ動かす(進み具合を出して、やめられるようにする)</summary>
    private async Task RunTransferAsync(string title, Func<IProgress<double>, CancellationToken, Task> work)
    {
        if (_transfer != null) return;
        _transfer = new CancellationTokenSource();
        ProgressRow.Visibility = Visibility.Visible;
        MessageRow.Visibility = Visibility.Collapsed;
        ProgressText.Text = title;
        Progress.Value = 0;
        RemoteList.IsEnabled = PeerList.IsEnabled = RemoteHeader.IsEnabled = false;
        var progress = new Progress<double>(v => Progress.Value = v);
        try
        {
            await work(progress, _transfer.Token);
        }
        catch (OperationCanceledException)
        {
            ShowMessage("やめました");
        }
        catch (SharePinException ex)
        {
            if (_selected != null)
            {
                Svc.Settings.PeerPins.Remove(_selected.Id);
                Svc.Settings.Save();
                AskPin(_selected, ex.Message);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ShowMessage($"失敗しました: {ex.Message}", bad: true);
        }
        finally
        {
            _transfer.Dispose();
            _transfer = null;
            ProgressRow.Visibility = Visibility.Collapsed;
            RemoteList.IsEnabled = PeerList.IsEnabled = RemoteHeader.IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _transfer?.Cancel();

    // ===================== 共通 =====================

    private FrameworkElement FileRow(ShareFile f, FrameworkElement? action)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4), Background = System.Windows.Media.Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var border = new Border
        {
            Padding = new Thickness(10, 6, 8, 6),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(WpfColor.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)),
            Child = grid,
        };
        var name = new TextBlock { Text = f.Name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = f.Name };
        var size = new TextBlock { Text = SizeText(f.Size), Opacity = 0.6, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(size, 1);
        grid.Children.Add(name);
        grid.Children.Add(size);
        if (action != null)
        {
            action.Margin = new Thickness(10, 0, 0, 0);
            Grid.SetColumn(action, 2);
            grid.Children.Add(action);
        }
        return border;
    }

    private static string SizeText(long n) => n switch
    {
        < 1024 => $"{n} B",
        < 1024 * 1024 => $"{n / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:0.0} MB",
        _ => $"{n / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private void ShowMessage(string text, bool bad = false, (string Label, Action Run)? action = null)
    {
        MessageRow.Visibility = Visibility.Visible;
        MessageText.Text = text;
        MessageText.Foreground = bad ? new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x8A, 0x5C)) : (TryFindResource("TextFillColorPrimaryBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White);
        _messageAction = action?.Run;
        MessageButton.Visibility = action != null ? Visibility.Visible : Visibility.Collapsed;
        if (action != null) MessageButton.Content = action.Value.Label;
    }

    private void MessageButton_Click(object sender, RoutedEventArgs e) => _messageAction?.Invoke();

    private static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    private static void OpenAndSelect(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); } catch { }
    }
}
