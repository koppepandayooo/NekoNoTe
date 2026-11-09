using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace NekoNoTe.Overlay;

/// <summary>通知表示時に鳴らす効果音を再生する。</summary>
public static class NotificationSoundPlayer
{
    // MediaPlayerはGCされると再生が途切れるため、再生完了まで参照を保持しておく。
    private static readonly List<MediaPlayer> ActivePlayers = new();

    private static readonly string DebugLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NekoNoTe", "debug.log");

    private static void DebugLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DebugLogPath)!);
            File.AppendAllText(DebugLogPath, $"{DateTimeOffset.Now:HH:mm:ss.fff} [sound] {message}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>重要度の高い通知向けに、同じ音を短い間隔を空けて複数回鳴らす。</summary>
    public static void PlayRepeated(string soundKey, double volume, int repeatCount)
    {
        Play(soundKey, volume);
        var remaining = repeatCount - 1;
        if (remaining <= 0)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(850) };
        timer.Tick += (_, _) =>
        {
            Play(soundKey, volume);
            remaining--;
            if (remaining <= 0)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    public static void Play(string soundKey, double volume)
    {
        var path = ResolvePath(soundKey);
        if (path is null)
        {
            DebugLog($"Play('{soundKey}') -> no sound selected or unknown key");
            return;
        }

        if (!File.Exists(path))
        {
            DebugLog($"Play('{soundKey}') -> FILE NOT FOUND: {path}");
            return;
        }

        var player = new MediaPlayer { Volume = Math.Clamp(volume, 0.0, 1.0) };
        ActivePlayers.Add(player);
        player.MediaOpened += (_, _) => DebugLog($"MediaOpened: {path} (volume={player.Volume:0.00})");
        player.MediaEnded += (_, _) =>
        {
            DebugLog($"MediaEnded: {path}");
            Cleanup(player);
        };
        player.MediaFailed += (_, e) =>
        {
            DebugLog($"MediaFailed: {path} : {e.ErrorException?.GetType().Name}: {e.ErrorException?.Message}");
            Cleanup(player);
        };

        try
        {
            DebugLog($"Play('{soundKey}') -> opening {path}");
            player.Open(new Uri(path, UriKind.Absolute));
            player.Play();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            DebugLog($"Play('{soundKey}') -> exception: {ex.GetType().Name}: {ex.Message}");
            Cleanup(player);
        }
    }

    private static void Cleanup(MediaPlayer player)
    {
        ActivePlayers.Remove(player);
        player.Close();
    }

    private static string? ResolvePath(string soundKey)
    {
        var fileName = soundKey switch
        {
            "Chime" => "chime.wav",
            "Alert" => "alert.wav",
            "Beep" => "beep.wav",
            "Bell" => "bell.wav",
            "Soft" => "soft.wav",
            _ => null,
        };

        return fileName is null ? null : Path.Combine(AppContext.BaseDirectory, "Resources", "Sounds", fileName);
    }
}
