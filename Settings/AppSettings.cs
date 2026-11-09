using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace NekoNoTe.Settings;

/// <summary>アプリの設定。%APPDATA%\NekoNoTe\settings.json</summary>
public sealed class AppSettings
{
    public static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe");
    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public bool CheckUpdatesOnStartup { get; set; } = true;
    /// <summary>閉じてもタスクトレイに残る(緊急地震速報を受けるため)</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Auto = 同じネットワークの PC と自動で共有、Direct = このPCで直接受信、Off = 受信しない</summary>
    public string QuakeSource { get; set; } = "Auto";
    /// <summary>共有のためのネットワークの確認(ファイアウォール)を初回に済ませた</summary>
    public bool NetworkPrimed { get; set; }
    public string MinEarthquakeIntensity { get; set; } = "3";
    /// <summary>緊急地震速報のカウントダウンに使う地域(都道府県)</summary>
    public string AreaName { get; set; } = "";
    public double NotificationVolume { get; set; } = 0.5;
    public string? MonitorDeviceName { get; set; }
    public string NotificationCorner { get; set; } = "TopRight";
    public int NotificationDurationSeconds { get; set; } = 7;
    public string NotificationSound { get; set; } = "Chime";
    public string? BackgroundImagePath { get; set; }
    public double BackgroundBlur { get; set; } = 30;
    public double BackgroundDim { get; set; } = 80;

    private static AppSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Windows にサインインした時に NekoNoTe を起動するかどうか(今のユーザーだけ)</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NekoNoTe";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(ValueName, $"\"{exe}\" {App.BackgroundArgument}");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Windows ファイアウォールで、この exe の受信がブロックされているかを見る(読むだけ)</summary>
public static class FirewallCheck
{
    public static bool IsBlocked()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe || Type.GetTypeFromProgID("HNetCfg.FwPolicy2") is not { } type) return false;
            dynamic policy = Activator.CreateInstance(type)!;
            bool blocked = false, allowed = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                if (app == null || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(bool)rule.Enabled || (int)rule.Direction != 1 || ((int)rule.Profiles & 2) == 0) continue;   // 受信・プライベート
                if ((int)rule.Action == 0) blocked = true; else allowed = true;
            }
            return blocked && !allowed;
        }
        catch { return false; }
    }
}
