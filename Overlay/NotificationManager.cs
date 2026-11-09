using NekoNoTe.Models;

namespace NekoNoTe.Overlay;

/// <summary>
/// 通知オーバーレイを指定コーナーに縦積みで表示・管理する。
/// 複数件が同時に発生した場合は既存の通知を押し出す形で並べて表示する。
/// </summary>
public sealed class NotificationManager
{
    private const int Gap = -16; // 通知ウィンドウは影のために外側へ余白(14px)を持つので、その分詰める
    private const int CriticalRepeatCount = 3;

    private readonly List<NotificationWindow> _active = new();
    private readonly Func<System.Windows.Forms.Screen> _screenProvider;
    private readonly Func<int> _durationSecondsProvider;
    private readonly Func<NotificationCorner> _cornerProvider;
    private readonly Func<string> _soundProvider;
    private readonly Func<double> _volumeProvider;

    public NotificationManager(
        Func<System.Windows.Forms.Screen> screenProvider,
        Func<int> durationSecondsProvider,
        Func<NotificationCorner> cornerProvider,
        Func<string> soundProvider,
        Func<double> volumeProvider)
    {
        _screenProvider = screenProvider;
        _durationSecondsProvider = durationSecondsProvider;
        _cornerProvider = cornerProvider;
        _soundProvider = soundProvider;
        _volumeProvider = volumeProvider;
    }

    public void Show(AlertInfo alert)
    {
        var screen = _screenProvider();
        var corner = _cornerProvider();
        var window = new NotificationWindow(alert, _durationSecondsProvider());

        window.Closed += (_, _) =>
        {
            _active.Remove(window);
            Reflow(screen, corner);
        };

        var offset = _active.Sum(w => w.PhysicalHeight + Gap);
        _active.Add(window);
        window.ShowOn(screen, corner, offset);

        var repeatCount = alert.IsCritical ? CriticalRepeatCount : 1;
        NotificationSoundPlayer.PlayRepeated(_soundProvider(), _volumeProvider(), repeatCount);
    }

    private void Reflow(System.Windows.Forms.Screen screen, NotificationCorner corner)
    {
        var offset = 0;
        foreach (var window in _active)
        {
            window.Reposition(screen, corner, offset);
            offset += window.PhysicalHeight + Gap;
        }
    }
}
