using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace NekoNoTe.Roblox;

/// <summary>
/// Roblox のログイン画面を出し、ログインできたら Cookie(.ROBLOSECURITY)を受け取って閉じる。
/// 毎回まっさらな保存場所を使うので、前にログインしたアカウントが残らない(別のアカウントを足せる)。
/// パスワードは NekoNoTe では扱わない(Roblox の画面に直接入れてもらう)。
/// </summary>
public sealed class RobloxLoginWindow : Window
{
    private readonly WebView2 _web = new();
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "NekoNoTe-RobloxLogin-" + Guid.NewGuid().ToString("N"));
    private bool _done;

    public string? Cookie { get; private set; }

    public RobloxLoginWindow()
    {
        Title = "Roblox にログイン - NekoNoTe";
        Width = 520;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        var hint = new TextBlock
        {
            Text = "ログインすると自動で閉じます。パスワードは Roblox の画面に直接入力され、NekoNoTe には保存されません。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 10, 14, 10),
            FontSize = 12,
        };
        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(hint);
        root.Children.Add(_web);
        Content = root;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            try { _web.Dispose(); } catch { }
            // ブラウザが終わってから、使い捨ての保存場所を消す
            _ = Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    try { if (Directory.Exists(_profile)) Directory.Delete(_profile, true); return; }
                    catch { await Task.Delay(500); }
                }
            });
        };
    }

    private async Task StartAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, _profile);
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.NavigationCompleted += async (_, _) => await CheckCookieAsync();
            _web.CoreWebView2.SourceChanged += async (_, _) => await CheckCookieAsync();
            _web.CoreWebView2.Navigate("https://www.roblox.com/login");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, "ログイン画面を開けませんでした。\n" + ex.Message, "Roblox", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task CheckCookieAsync()
    {
        if (_done || _web.CoreWebView2 == null) return;
        try
        {
            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync("https://www.roblox.com");
            var c = cookies.FirstOrDefault(x => x.Name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(x.Value));
            if (c == null) return;
            _done = true;
            Cookie = c.Value;
            DialogResult = true;
        }
        catch { }
    }
}
