using System.Windows;

namespace NekoNoTe.Updates;

public partial class UpdatePanel : System.Windows.Controls.UserControl
{
    private const string CheckFailed = "確認できませんでした(インターネットの接続を確かめてください)";
    private ReleaseInfo? _release;
    private bool _busy;

    /// <summary>新しいバージョンがあるかどうかが分かった時(サイドバーの印に使う)</summary>
    public event Action<bool>? UpdateAvailableChanged;

    public UpdatePanel()
    {
        InitializeComponent();
        CurrentText.Text = "v" + UpdateService.CurrentVersion.ToString(3);
        StatusText.Text = "確認していません";
    }

    /// <summary>起動した時の確認</summary>
    public async Task CheckSilentlyAsync()
    {
        try { await RunCheckAsync(); }
        catch { StatusText.Text = CheckFailed; }
    }

    private async Task RunCheckAsync()
    {
        StatusText.Text = "確認しています…";
        var latest = await UpdateService.GetLatestAsync();
        var available = latest != null && latest.Version > UpdateService.CurrentVersion;
        _release = available ? latest : null;

        InstallButton.Visibility = available && UpdateService.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        var notes = available ? FormatNotes(latest!.Notes) : "";
        NotesTitle.Visibility = NotesText.Visibility = notes != "" ? Visibility.Visible : Visibility.Collapsed;
        NotesText.Text = notes;
        StatusText.Text = !available ? "最新のバージョンです"
            : UpdateService.CanInstall ? $"新しいバージョン {latest!.Tag} があります"
            : $"新しいバージョン {latest!.Tag} があります(GitHub からインストーラーを入れてください)";
        UpdateAvailableChanged?.Invoke(available);
    }

    /// <summary>
    /// GitHub のリリースの本文(Markdown)を、読みやすい文字にする。
    /// «変更点» の見出しがあれば、そこだけを出す
    /// </summary>
    private static string FormatNotes(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').ToList();
        var start = lines.FindIndex(l => l.TrimStart().StartsWith('#') && l.Contains("変更点"));
        if (start >= 0)
        {
            var end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('#'));
            lines = lines.GetRange(start + 1, (end < 0 ? lines.Count : end) - start - 1);
        }

        var result = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");   // リンクは文字だけ
            line = line.Replace("**", "").Replace("`", "").Replace("«", "「").Replace("»", "」");
            if (line.StartsWith('#')) line = line.TrimStart('#').Trim();
            else if (line.StartsWith("- ") || line.StartsWith("* ")) line = "・" + line[2..].Trim();
            result.Add(line);
        }
        return string.Join("\n", result).Trim();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        CheckButton.IsEnabled = false;
        try { await RunCheckAsync(); }
        catch { StatusText.Text = CheckFailed; }
        finally
        {
            _busy = false;
            CheckButton.IsEnabled = true;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _release is not { } release) return;
        _busy = true;
        CheckButton.IsEnabled = InstallButton.IsEnabled = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = "ダウンロードしています…";
        try
        {
            var installer = await UpdateService.DownloadAsync(release, new Progress<double>(v => Progress.Value = v));
            StatusText.Text = "アップデートしています…";
            ((App)System.Windows.Application.Current).RunInstallerAndExit(installer);
        }
        catch (Exception ex)
        {
            StatusText.Text = "アップデートできませんでした: " + ex.Message;
            Progress.Visibility = Visibility.Collapsed;
            _busy = false;
            CheckButton.IsEnabled = InstallButton.IsEnabled = true;
        }
    }
}
