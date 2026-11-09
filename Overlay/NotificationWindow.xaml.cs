using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using NekoNoTe.Models;

namespace NekoNoTe.Overlay;

public partial class NotificationWindow : Window
{
    // ウィンドウ自体が影用に14pxの余白を持つので、見た目で画面端から約16px離れる値
    private const int EdgeMargin = 2;
    // ネイティブウィンドウでも十分な描画フレーム数を確保し、横方向へ滑らかに抜ける時間。
    private static readonly TimeSpan MoveAnimationDuration = TimeSpan.FromMilliseconds(250);

    private enum MoveEasing
    {
        Enter,
        Reflow,
        Exit,
    }

    private readonly DispatcherTimer _dismissTimer;
    private readonly TimeSpan _displayDuration;
    private DispatcherTimer? _moveTimer;
    private int _currentStackOffset;
    private System.Windows.Forms.Screen? _currentScreen;
    private NotificationCorner _currentCorner;
    private bool _isClosing;

    public int PhysicalHeight { get; private set; }

    public NotificationWindow(AlertInfo alert, int durationSeconds)
    {
        InitializeComponent();
        TitleText.Text = alert.Title;
        BodyText.Text = alert.Body;
        BodyText.Visibility = string.IsNullOrWhiteSpace(alert.Body) ? Visibility.Collapsed : Visibility.Visible;
        TimeText.Text = alert.ReportedAt.ToLocalTime().ToString("HH:mm");
        ApplyKindStyle(alert);

        _displayDuration = TimeSpan.FromSeconds(Math.Max(2, durationSeconds));
        _dismissTimer = new DispatcherTimer
        {
            Interval = _displayDuration,
        };
        _dismissTimer.Tick += (_, _) =>
        {
            _dismissTimer.Stop();
            SlideOutAndClose();
        };
    }

    /// <summary>種類ごとにアイコンと色を変える(アプリ本体のカードと同じ見た目)</summary>
    private void ApplyKindStyle(AlertInfo alert)
    {
        var (symbol, label, color) = alert.Kind switch
        {
            AlertKind.EewWarning => (Wpf.Ui.Controls.SymbolRegular.Alert24, "緊急地震速報", System.Windows.Media.Color.FromRgb(0xFF, 0x5C, 0x5C)),
            AlertKind.Earthquake => (Wpf.Ui.Controls.SymbolRegular.Pulse24, "地震情報", System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF)),
            AlertKind.Tsunami => (Wpf.Ui.Controls.SymbolRegular.Water24, "津波情報", System.Windows.Media.Color.FromRgb(0x5C, 0x9D, 0xFF)),
            AlertKind.WarningAdvisory => (Wpf.Ui.Controls.SymbolRegular.Warning24, "気象警報・注意報", System.Windows.Media.Color.FromRgb(0xFF, 0xB0, 0x5C)),
            AlertKind.Schedule => (Wpf.Ui.Controls.SymbolRegular.CalendarLtr24, "予定", System.Windows.Media.Color.FromRgb(0x8A, 0xE0, 0x9A)),
            _ => (Wpf.Ui.Controls.SymbolRegular.Info24, "NekoNoTe", System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF)),
        };
        if (alert.IsCancelled)
        {
            symbol = Wpf.Ui.Controls.SymbolRegular.DismissCircle24;
            label += "(取り消し)";
            color = System.Windows.Media.Color.FromRgb(0xB0, 0xB0, 0xB8);
        }
        else if (alert.IsCritical && alert.Kind != AlertKind.Schedule)
        {
            color = System.Windows.Media.Color.FromRgb(0xFF, 0x5C, 0x5C);
        }

        var brush = new SolidColorBrush(color);
        KindIcon.Symbol = symbol;
        KindIcon.Foreground = brush;
        KindText.Text = label;
        KindText.Foreground = brush;
        IconChip.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, color.R, color.G, color.B));
        GlowStop.Color = System.Windows.Media.Color.FromArgb(0x38, color.R, color.G, color.B);
        RemainingTimeBar.Background = brush;
        if (alert.IsCritical && !alert.IsCancelled)
            Card.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, color.R, color.G, color.B));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeClickThrough(hwnd);
    }

    /// <summary>
    /// 指定モニターの指定コーナーを基準に表示する。画面端からわずかにスライドインしながら
    /// フェードインし、位置決めは物理ピクセルで行ってマルチモニターDPI差の影響を避ける。
    /// </summary>
    public void ShowOn(System.Windows.Forms.Screen screen, NotificationCorner corner, int stackOffsetPhysicalPixels)
    {
        Show();
        UpdateLayout();

        var (physicalWidth, physicalHeight) = MeasurePhysicalSize();
        PhysicalHeight = physicalHeight;

        var (finalX, finalY) = ComputePosition(screen, corner, physicalWidth, physicalHeight, stackOffsetPhysicalPixels);
        _currentStackOffset = stackOffsetPhysicalPixels;
        _currentScreen = screen;
        _currentCorner = corner;

        var isRightAnchored = corner is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        // 通知の一部が突然現れないよう、ウィンドウ全体を画面外から滑り込ませる。
        var startX = isRightAnchored ? screen.Bounds.Right + 8 : screen.Bounds.Left - physicalWidth - 8;

        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST,
            startX, finalY, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        AnimateWindowMove(startX, finalY, finalX, finalY, MoveAnimationDuration, MoveEasing.Enter);

        StartRemainingTimeAnimation();
        _dismissTimer.Start();
    }

    private void StartRemainingTimeAnimation()
    {
        if (RemainingTimeBar.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        var pop = new System.Windows.Media.Animation.DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(420))
        {
            BeginTime = TimeSpan.FromMilliseconds(120),
            EasingFunction = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.6 },
        };
        IconScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        IconScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0, _displayDuration)
        {
            EasingFunction = null,
            FillBehavior = System.Windows.Media.Animation.FillBehavior.HoldEnd,
        });
    }

    /// <summary>他の通知が閉じて詰める際、フェードやタイマーは再始動せず現在位置から新しい位置へスライドさせる。</summary>
    public void Reposition(System.Windows.Forms.Screen screen, NotificationCorner corner, int stackOffsetPhysicalPixels)
    {
        // 退出アニメーションを並び替えで上書きすると、完了時のCloseまで失われて
        // タイマー停止済みの通知が画面に残るため、退出中は位置変更を受け付けない。
        if (_isClosing)
        {
            return;
        }

        if (stackOffsetPhysicalPixels == _currentStackOffset)
        {
            return;
        }

        var (physicalWidth, physicalHeight) = MeasurePhysicalSize();
        var (fromX, fromY) = ComputePosition(screen, corner, physicalWidth, physicalHeight, _currentStackOffset);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryGetWindowPosition(hwnd, out var actualX, out var actualY))
        {
            fromX = actualX;
            fromY = actualY;
        }
        var (toX, toY) = ComputePosition(screen, corner, physicalWidth, physicalHeight, stackOffsetPhysicalPixels);
        _currentStackOffset = stackOffsetPhysicalPixels;

        AnimateWindowFollow(fromX, fromY, toX, toY);
    }

    /// <summary>
    /// currentY += (targetY - currentY) * 0.2 の追従方式。
    /// 固定時間で始点から補間せず、現在位置から毎フレーム残り距離の20%を進むため、
    /// 通知が続けて消えて目標位置が変わっても、不自然に止まったり跳ねたりしない。
    /// </summary>
    private void AnimateWindowFollow(int fromX, int fromY, int toX, int toY)
    {
        _moveTimer?.Stop();

        var hwnd = new WindowInteropHelper(this).Handle;
        var x = (double)fromX;
        var y = (double)fromY;
        _moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _moveTimer.Tick += (_, _) =>
        {
            x += (toX - x) * 0.2;
            y += (toY - y) * 0.2;

            if (Math.Abs(toX - x) < 0.75 && Math.Abs(toY - y) < 0.75)
            {
                x = toX;
                y = toY;
                _moveTimer!.Stop();
            }

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
                (int)Math.Round(x), (int)Math.Round(y), 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        };
        _moveTimer.Start();
    }

    private void AnimateWindowMove(
        int fromX,
        int fromY,
        int toX,
        int toY,
        TimeSpan duration,
        MoveEasing easing,
        Action? completed = null)
    {
        _moveTimer?.Stop();

        var hwnd = new WindowInteropHelper(this).Handle;
        var start = DateTime.UtcNow;

        _moveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _moveTimer.Tick += (_, _) =>
        {
            var t = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / duration.TotalMilliseconds);
            var eased = easing switch
            {
                // 入場はすばやく姿を見せて、所定位置へ柔らかく着地する。
                MoveEasing.Enter => 1 - Math.Pow(1 - t, 3),
                // currentY 追従に近い、最初にしっかり動いて柔らかく止まる曲線。
                MoveEasing.Reflow => 1 - Math.Pow(1 - t, 3),
                // ネイティブウィンドウでは3次ease-inの最初の数フレームが1px未満になり、
                // 静止しているように見える。開始直後から動き、終端も滑らかなsmoothstepを使う。
                MoveEasing.Exit => t * t * (3 - 2 * t),
                _ => t,
            };
            var x = (int)Math.Round(fromX + (toX - fromX) * eased);
            var y = (int)Math.Round(fromY + (toY - fromY) * eased);

            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            if (t >= 1.0)
            {
                _moveTimer!.Stop();
                completed?.Invoke();
            }
        };
        _moveTimer.Start();
    }

    private static (int x, int y) ComputePosition(
        System.Windows.Forms.Screen screen, NotificationCorner corner, int width, int height, int stackOffset)
    {
        var x = corner is NotificationCorner.TopRight or NotificationCorner.BottomRight
            ? screen.Bounds.Right - width - EdgeMargin
            : screen.Bounds.Left + EdgeMargin;

        // 上端基準のコーナーは下向きに積み上げ、下端基準のコーナーは上向きに積み上げる。
        var y = corner is NotificationCorner.TopRight or NotificationCorner.TopLeft
            ? screen.Bounds.Top + EdgeMargin + stackOffset
            : screen.Bounds.Bottom - height - EdgeMargin - stackOffset;

        return (x, y);
    }

    private (int width, int height) MeasurePhysicalSize()
    {
        var source = PresentationSource.FromVisual(this);
        var dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return ((int)Math.Ceiling(ActualWidth * dpiX), (int)Math.Ceiling(ActualHeight * dpiY));
    }

    private void SlideOutAndClose()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _moveTimer?.Stop();
        if (_currentScreen is null)
        {
            Close();
            return;
        }

        var (width, height) = MeasurePhysicalSize();
        var (fromX, fromY) = ComputePosition(_currentScreen, _currentCorner, width, height, _currentStackOffset);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NativeMethods.TryGetWindowPosition(hwnd, out var actualX, out var actualY))
        {
            fromX = actualX;
            fromY = actualY;
        }
        var exitsRight = _currentCorner is NotificationCorner.TopRight or NotificationCorner.BottomRight;
        var toX = exitsRight
            ? _currentScreen.Bounds.Right + 8
            : _currentScreen.Bounds.Left - width - 8;
        AnimateWindowMove(fromX, fromY, toX, fromY, MoveAnimationDuration, MoveEasing.Exit, Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _dismissTimer.Stop();
        _moveTimer?.Stop();
        base.OnClosed(e);
    }
}
