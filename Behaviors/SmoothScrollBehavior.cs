using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NekoNoTe.Behaviors;

/// <summary>
/// ScrollViewer(または内部にScrollViewerを持つListViewなど)のマウスホイール操作に、
/// チャットアプリのような慣性(モメンタム)スクロールを付ける添付ビヘイビア。
/// ホイール1回ごとに速度を加算し、毎フレーム摩擦で減速させながら位置を更新する。
/// </summary>
public static class SmoothScrollBehavior
{
    private const double WheelImpulse = 2200; // ホイール1ノッチで加算する速度(px/秒)
    private const double MaxVelocity = 7000;
    private const double FrictionPerSecond = 11.0; // 速度の指数減衰係数(大きいほど早く止まる)
    private const double StopThreshold = 6; // これ未満の速度(px/秒)になったら停止扱い

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SmoothScrollBehavior), new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(MomentumState), typeof(SmoothScrollBehavior), new PropertyMetadata(null));

    private sealed class MomentumState
    {
        public double Velocity;
        public long LastTimestamp;
        public EventHandler? RenderingHandler;
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
        else
        {
            element.PreviewMouseWheel -= OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // コンボボックスの一覧などポップアップの中のホイールは、そのポップアップに任せる
        // (ポップアップは別の画面ツリーなので、外側の ScrollViewer の子孫にならない)
        if (sender is Visual host && e.OriginalSource is Visual src && !host.IsAncestorOf(src))
        {
            return;
        }

        // 一覧が開いている間は、後ろのページをスクロールさせない(一覧の外でホイールを回した時)
        if (Mouse.Captured is System.Windows.Controls.ComboBox { IsDropDownOpen: true } openCombo && sender is Visual page && page.IsAncestorOf(openCombo))
        {
            e.Handled = true;
            return;
        }

        var scrollViewer = FindScrollViewer((DependencyObject)sender);
        if (scrollViewer is null || scrollViewer.ScrollableHeight <= 0)
        {
            return;
        }

        e.Handled = true;

        var state = GetOrCreateState(scrollViewer);
        state.Velocity = Math.Clamp(state.Velocity - Math.Sign(e.Delta) * WheelImpulse, -MaxVelocity, MaxVelocity);
        EnsureRunning(scrollViewer, state);
    }

    private static MomentumState GetOrCreateState(ScrollViewer scrollViewer)
    {
        if (scrollViewer.GetValue(StateProperty) is not MomentumState state)
        {
            state = new MomentumState();
            scrollViewer.SetValue(StateProperty, state);
        }
        return state;
    }

    private static void EnsureRunning(ScrollViewer scrollViewer, MomentumState state)
    {
        if (state.RenderingHandler is not null)
        {
            return;
        }

        state.LastTimestamp = Stopwatch.GetTimestamp();
        EventHandler handler = null!;
        handler = (_, _) => OnRendering(scrollViewer, state, handler);
        state.RenderingHandler = handler;
        CompositionTarget.Rendering += handler;
    }

    private static void OnRendering(ScrollViewer scrollViewer, MomentumState state, EventHandler handler)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - state.LastTimestamp) / (double)Stopwatch.Frequency;
        state.LastTimestamp = now;

        // 指数減衰(摩擦): velocity(t) = velocity(0) * e^(-k*t)
        state.Velocity *= Math.Exp(-FrictionPerSecond * dt);

        var next = scrollViewer.VerticalOffset + state.Velocity * dt;
        var clamped = Math.Clamp(next, 0, scrollViewer.ScrollableHeight);
        if (clamped != next)
        {
            // 端に到達したら反発させず止める。
            state.Velocity = 0;
        }
        scrollViewer.ScrollToVerticalOffset(clamped);

        if (Math.Abs(state.Velocity) < StopThreshold)
        {
            CompositionTarget.Rendering -= handler;
            state.RenderingHandler = null;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
        {
            return sv;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
