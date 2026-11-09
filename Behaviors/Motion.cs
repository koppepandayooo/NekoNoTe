using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NekoNoTe.Behaviors;

/// <summary>
/// アプリ全体の動き。
/// ・ボタンを押すと少し沈む
/// ・タブを開いた時にカードが下から順に浮かび上がる
/// ・カードにマウスを乗せると少し持ち上がる
/// </summary>
public static class Motion
{
    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Spring = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    private static readonly DependencyProperty HoverAttachedProperty =
        DependencyProperty.RegisterAttached("HoverAttached", typeof(bool), typeof(Motion));

    private static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    public static void Init()
    {
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((s, _) => Press((FrameworkElement)s, true)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler((s, _) => Press((FrameworkElement)s, false)), true);
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Primitives.ButtonBase), UIElement.MouseLeaveEvent,
            new System.Windows.Input.MouseEventHandler((s, _) => Press((FrameworkElement)s, false)), true);
    }

    private static void Press(FrameworkElement el, bool down)
    {
        // スクロールバーの部品などは除く(見た目のあるボタンだけ)
        if (el is RepeatButton || el is Thumb) return;
        if (!down && el.RenderTransform is not ScaleTransform) return;
        var scale = EnsureScale(el);
        var to = down ? (el.ActualWidth > 160 ? 0.985 : 0.95) : 1.0;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(down ? 90 : 260))
        {
            EasingFunction = down ? EaseOut : Spring,
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private static ScaleTransform EnsureScale(FrameworkElement el)
    {
        if (el.RenderTransform is ScaleTransform { IsFrozen: false } s) return s;
        s = new ScaleTransform(1, 1);
        el.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        el.RenderTransform = s;
        return s;
    }

    /// <summary>パネルを少し下から滑り込ませ、中のカードを順番に出す。</summary>
    public static void PanelIn(FrameworkElement panel, TimeSpan begin)
    {
        var shift = new TranslateTransform(0, 14);
        panel.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, Delayed(14, 0, begin, 320));

        var cards = new List<FrameworkElement>();
        Collect(panel, cards);
        int i = 0;
        foreach (var card in cards)
        {
            AttachHover(card);
            if (i >= 12) break;      // 画面外のカードまで待たせない
            var g = (TransformGroup)card.RenderTransform;
            var t = (TranslateTransform)g.Children[1];
            var delay = begin + TimeSpan.FromMilliseconds(35 * i);
            card.BeginAnimation(UIElement.OpacityProperty, Delayed(0, 1, delay, 260));
            t.BeginAnimation(TranslateTransform.YProperty, Delayed(18, 0, delay, 420));
            i++;
        }
    }

    /// <summary>
    /// 待ち時間の間は from のまま止めておき、その後イーズアウトで to へ動かす。
    /// (BeginTime で遅らせると、待っている間は元の値が一瞬見えてしまうため)
    /// </summary>
    private static DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan delay, int ms)
    {
        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero) a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(ms)), EaseOut));
        a.Freeze();
        return a;
    }

    /// <summary>カード(GlassCardStyle か SurfaceBrush 背景の角丸 Border)を見える順に集める。</summary>
    private static void Collect(DependencyObject root, List<FrameworkElement> found)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { Visibility: not Visibility.Visible }) continue;
            if (child is Border b && IsCard(b))
            {
                found.Add(b);
                continue;        // カードの中のカードは動かさない
            }
            Collect(child, found);
        }
    }

    private static Style? _glass;
    private static System.Windows.Media.Brush? _surface;

    private static bool IsCard(Border b)
    {
        _glass ??= System.Windows.Application.Current.TryFindResource("GlassCardStyle") as Style;
        _surface ??= System.Windows.Application.Current.TryFindResource("SurfaceBrush") as System.Windows.Media.Brush;
        if (b.Style != null && ReferenceEquals(b.Style, _glass)) return true;
        return ReferenceEquals(b.Background, _surface) && b.CornerRadius.TopLeft >= 8 && b.ActualHeight >= 60;
    }

    /// <summary>マウスを乗せると少し持ち上がる(1回だけ付ける)。</summary>
    public static void AttachHover(FrameworkElement card)
    {
        if (card.RenderTransform is not TransformGroup { Children.Count: 2 } || card.GetValue(HoverAttachedProperty) is not true)
        {
            card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            card.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
        }
        if (card.GetValue(HoverAttachedProperty) is true) return;
        card.SetValue(HoverAttachedProperty, true);

        card.MouseEnter += (_, _) => Lift(card, true);
        card.MouseLeave += (_, _) => Lift(card, false);
    }

    private static void Lift(FrameworkElement card, bool up)
    {
        if (card.RenderTransform is not TransformGroup g || g.Children[0] is not ScaleTransform s) return;
        // 大きなカードほど拡大率を控えめにする(ぼやけて見えないように)
        var amount = card.ActualWidth > 500 ? 0.006 : 0.014;
        var anim = new DoubleAnimation(up ? 1 + amount : 1, TimeSpan.FromMilliseconds(up ? 220 : 300)) { EasingFunction = EaseOut };
        s.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        s.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
