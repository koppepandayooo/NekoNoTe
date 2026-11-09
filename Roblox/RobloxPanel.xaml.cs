using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfColor = System.Windows.Media.Color;
using WpfImage = System.Windows.Controls.Image;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace NekoNoTe.Roblox;

/// <summary>Roblox タブの設定(多重起動・最後に入れたゲーム)</summary>
public sealed class RobloxSettings
{
    public bool MultiInstance { get; set; }
    public string Place { get; set; } = "";
    public string Job { get; set; } = "";

    private static string PathOf => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "roblox.json");

    public static RobloxSettings Load()
    {
        try { if (File.Exists(PathOf)) return JsonSerializer.Deserialize<RobloxSettings>(File.ReadAllText(PathOf)) ?? new(); } catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this));
        }
        catch { }
    }
}

public partial class RobloxPanel : System.Windows.Controls.UserControl
{
    private readonly RobloxSettings _settings = RobloxSettings.Load();
    private bool _loading = true;

    public RobloxPanel()
    {
        InitializeComponent();
        PlaceBox.Text = _settings.Place;
        JobBox.Text = _settings.Job;
        MultiSwitch.IsChecked = RobloxMultiInstance.Enabled;
        _loading = false;
        UpdateMultiText();
        UpdatePlaceInfo();
        RobloxAccounts.Changed += () => Dispatcher.BeginInvoke(Render);
        Loaded += async (_, _) => { Render(); await LoadHeadshotsAsync(); };
    }

    /// <summary>アプリの起動時に、前回オンにしていたら多重起動をオンにする</summary>
    public static void ApplyStartupSettings()
    {
        if (RobloxSettings.Load().MultiInstance) RobloxMultiInstance.Enable();
    }

    // ---------- 一覧 ----------

    private void Render()
    {
        AccountList.Children.Clear();
        var items = RobloxAccounts.Items.OrderByDescending(a => a.LastUsed).ToList();
        CountText.Text = items.Count > 0 ? $"{items.Count} 個" : "";
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var a in items) AccountList.Children.Add(Row(a));
    }

    private FrameworkElement Row(RobloxAccount a)
    {
        var avatar = new Border
        {
            Width = 46, Height = 46, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0),
            Background = new SolidColorBrush(WpfColor.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)), ClipToBounds = true,
            Child = a.HeadshotUrl != null
                ? new WpfImage { Source = new BitmapImage(new Uri(a.HeadshotUrl)), Stretch = System.Windows.Media.Stretch.UniformToFill }
                : new SymbolIcon { Symbol = SymbolRegular.Person24, FontSize = 20 },
        };

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var top = new StackPanel { Orientation = WpfOrientation.Horizontal };
        top.Children.Add(new WpfTextBlock { Text = a.DisplayName != "" ? a.DisplayName : a.Name, FontSize = 14, FontWeight = FontWeights.SemiBold });
        if (a.Alias != "")
            top.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 0, 7, 1), Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(WpfColor.FromArgb(0x26, 0x60, 0xCD, 0xFF)), VerticalAlignment = VerticalAlignment.Center,
                Child = new WpfTextBlock { Text = a.Alias, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush") },
            });
        names.Children.Add(top);
        names.Children.Add(new WpfTextBlock
        {
            Text = "@" + a.Name + (a.LastUsed > DateTime.MinValue ? $" · 最後に起動 {a.LastUsed:M/d HH:mm}" : ""),
            FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0),
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
        });

        var launch = new WpfButton
        {
            Height = 34, Padding = new Thickness(14, 0, 14, 0), Style = (Style)FindResource("AccentButtonStyle"),
            Content = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                Children = { new SymbolIcon { Symbol = SymbolRegular.Play24, FontSize = 14 }, new WpfTextBlock { Text = "起動", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        launch.Click += async (_, _) => await LaunchAsync(a, launch);

        var menu = new ContextMenu();
        var alias = new WpfMenuItem { Header = "メモを付ける…" };
        alias.Click += (_, _) => EditAlias(a);
        var remove = new WpfMenuItem { Header = "このアカウントを外す" };
        remove.Click += (_, _) =>
        {
            if (System.Windows.MessageBox.Show(Window.GetWindow(this), $"«{a.Name}» を一覧から外しますか？(Roblox のアカウント自体は消えません)", "Roblox",
                    System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Question) == System.Windows.MessageBoxResult.OK)
                RobloxAccounts.Remove(a);
        };
        menu.Items.Add(alias);
        menu.Items.Add(remove);
        var more = new WpfButton { Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), ContextMenu = menu,
            Content = new SymbolIcon { Symbol = SymbolRegular.MoreHorizontal24, FontSize = 15 } };
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };

        var right = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(launch);
        right.Children.Add(more);

        var dock = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Add(avatar);
        dock.Children.Add(right);
        dock.Children.Add(names);
        return new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(WpfColor.FromArgb(0x0F, 0xFF, 0xFF, 0xFF)), Child = dock,
        };
    }

    private async Task LoadHeadshotsAsync()
    {
        var need = RobloxAccounts.Items.Where(a => a.HeadshotUrl == null).ToList();
        if (need.Count == 0) return;
        var map = await RobloxApi.HeadshotsAsync(need.Select(a => a.UserId));
        foreach (var a in need) if (map.TryGetValue(a.UserId, out var u)) a.HeadshotUrl = u;
        Render();
    }

    private void EditAlias(RobloxAccount a)
    {
        var box = new System.Windows.Controls.TextBox { Text = a.Alias, Margin = new Thickness(0, 8, 0, 0), MaxLength = 30 };
        var ok = new WpfButton { Content = "保存", Width = 90, Height = 32, Style = (Style)FindResource("AccentButtonStyle"), IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var win = new Window
        {
            Title = "メモ - " + a.Name, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window)),
            Content = new StackPanel { Margin = new Thickness(18), Children = { new WpfTextBlock { Text = "メイン・サブ など、見分けるためのメモ" }, box, ok } },
        };
        ok.Click += (_, _) => { a.Alias = box.Text.Trim(); RobloxAccounts.Save(); win.Close(); };
        win.ShowDialog();
    }

    // ---------- 追加 ----------

    private async void AddLogin_Click(object sender, RoutedEventArgs e)
    {
        var win = new RobloxLoginWindow { Owner = Window.GetWindow(this) };
        if (win.ShowDialog() != true || win.Cookie == null) return;
        await AddCookieAsync(win.Cookie);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        MoreMenu.PlacementTarget = (UIElement)sender;
        MoreMenu.IsOpen = true;
    }

    private async void AddCookie_Click(object sender, RoutedEventArgs e)
    {
        var box = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 8, 0, 0) };
        var ok = new WpfButton { Content = "追加", Width = 90, Height = 32, Style = (Style)FindResource("AccentButtonStyle"), IsDefault = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        string? cookie = null;
        var win = new Window
        {
            Title = "Cookie で追加", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window)),
            Content = new StackPanel
            {
                Margin = new Thickness(18),
                Children =
                {
                    new WpfTextBlock { Text = ".ROBLOSECURITY の値を貼り付けてください。", TextWrapping = TextWrapping.Wrap },
                    box, ok,
                },
            },
        };
        ok.Click += (_, _) => { cookie = box.Password; win.Close(); };
        win.ShowDialog();
        if (!string.IsNullOrWhiteSpace(cookie)) await AddCookieAsync(cookie);
    }

    private async Task AddCookieAsync(string cookie)
    {
        try
        {
            var acc = await RobloxApi.ValidateAsync(cookie);
            if (acc == null) { ShowMessage("ログインできませんでした。Cookie が古いか、間違っています。", bad: true); return; }
            RobloxAccounts.AddOrUpdate(acc);
            ShowMessage($"«{acc.Name}» を追加しました。");
            await LoadHeadshotsAsync();
        }
        catch (Exception ex) { ShowMessage("追加できませんでした: " + ex.Message, bad: true); }
    }

    private async void ImportRam_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Roblox Account Manager の AccountData.json",
            Filter = "AccountData.json|AccountData.json|JSON|*.json",
            FileName = "AccountData.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dialog.FileName;
        try
        {
            var list = RobloxAccounts.ReadAccountManager(path);
            int ok = 0, expired = 0;
            foreach (var a in list)
            {
                // ログインが切れていないか確かめてから入れる(名前も新しくなる)
                var v = await RobloxApi.ValidateAsync(a.Cookie);
                if (v == null) { expired++; continue; }
                v.Alias = a.Alias;
                v.BrowserTrackerId = a.BrowserTrackerId;
                RobloxAccounts.AddOrUpdate(v);
                ok++;
            }
            ShowMessage($"{ok} 個読み込みました。" + (expired > 0 ? $"ログインが切れていた {expired} 個は読み込んでいません。" : ""), bad: ok == 0);
            await LoadHeadshotsAsync();
        }
        catch (Exception ex) { ShowMessage(ex.Message, bad: true); }
    }

    private async void CheckAll_Click(object sender, RoutedEventArgs e)
    {
        var bad = new List<string>();
        foreach (var a in RobloxAccounts.Items.ToList())
            if (await RobloxApi.ValidateAsync(a.Cookie) == null) bad.Add(a.Name);
        ShowMessage(bad.Count == 0 ? "全部ログインできています。" : $"ログインが切れています: {string.Join("、", bad)}(追加し直してください)", bad: bad.Count > 0);
    }

    // ---------- 起動 ----------

    private async Task LaunchAsync(RobloxAccount a, WpfButton button)
    {
        var place = RobloxApi.ParsePlaceId(PlaceBox.Text);
        if (PlaceBox.Text.Trim() != "" && place == 0) { ShowMessage("ゲームの URL かプレイス ID を読み取れませんでした。", bad: true); return; }
        if (RobloxMultiInstance.RobloxRunning() && !RobloxMultiInstance.Enabled)
            ShowMessage("Roblox がもう起動しています。多重起動がオフなので、前の Roblox は閉じられます。");
        button.IsEnabled = false;
        try
        {
            await RobloxApi.LaunchAsync(a, place, JobBox.Text);
            ShowMessage($"«{a.Name}» で起動しています…");
        }
        catch (Exception ex) { ShowMessage(ex.Message, bad: true); }
        finally { button.IsEnabled = true; }
    }

    private void Place_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Place = PlaceBox.Text;
        _settings.Job = JobBox.Text;
        _settings.Save();
        UpdatePlaceInfo();
    }

    private void UpdatePlaceInfo()
    {
        var id = RobloxApi.ParsePlaceId(PlaceBox.Text);
        PlaceInfoText.Text = PlaceBox.Text.Trim() == "" ? "空: ゲームを選ばずに開く"
            : id > 0 ? $"プレイス ID: {id}" : "読み取れません(https://www.roblox.com/games/… の URL か数字)";
    }

    // ---------- 多重起動 ----------

    private void Multi_Click(object sender, RoutedEventArgs e)
    {
        if (MultiSwitch.IsChecked == true)
        {
            if (!RobloxMultiInstance.Enable())
            {
                MultiSwitch.IsChecked = false;
                ShowMessage("Roblox が起動しているのでオンにできませんでした。Roblox を全部閉じてからオンにしてください。", bad: true);
            }
        }
        else RobloxMultiInstance.Disable();
        _settings.MultiInstance = RobloxMultiInstance.Enabled;
        _settings.Save();
        UpdateMultiText();
    }

    private void UpdateMultiText()
    {
        MultiText.Text = RobloxMultiInstance.Enabled
            ? "オン: Roblox を何個でも同時に起動できます。NekoNoTe を閉じる(タスクトレイからも終了する)とオフに戻ります。"
            : "オフ: Roblox は1個だけ。オンにする時は、先に Roblox を全部閉じてください。";
    }

    // ---------- メッセージ ----------

    private void ShowMessage(string msg, bool bad = false)
    {
        MessageText.Text = msg;
        MessageBar.Background = new SolidColorBrush(bad ? WpfColor.FromArgb(0x26, 0xFF, 0x8A, 0x5C) : WpfColor.FromArgb(0x1F, 0x60, 0xCD, 0xFF));
        MessageBar.BorderBrush = new SolidColorBrush(bad ? WpfColor.FromArgb(0x40, 0xFF, 0x8A, 0x5C) : WpfColor.FromArgb(0x33, 0x60, 0xCD, 0xFF));
        MessageBar.Visibility = Visibility.Visible;
    }

    private void CloseMessage_Click(object sender, RoutedEventArgs e) => MessageBar.Visibility = Visibility.Collapsed;
}
