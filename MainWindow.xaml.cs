using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace NekoNoTe;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private FrameworkElement _activePanel;

    /// <summary>サイドバーのボタンと、開くパネルの組み合わせ</summary>
    private (ToggleButton Button, FrameworkElement Panel)[] Pages =>
        new (ToggleButton, FrameworkElement)[]
        {
            (RobloxNavButton, RobloxPanel),
            (UpdateNavButton, UpdatePanel),
        };

    public MainWindow()
    {
        InitializeComponent();
        _activePanel = RobloxPanel;
        UpdatePanel.UpdateAvailableChanged += available =>
            UpdateNavActivity.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await UpdatePanel.CheckSilentlyAsync();
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        var target = Pages.First(p => ReferenceEquals(p.Button, clicked)).Panel;

        // ToggleButton は選択済みでも押すと一度 OFF になるため、同じタブなら選択を戻して何もしない
        if (ReferenceEquals(_activePanel, target))
        {
            clicked.IsChecked = true;
            return;
        }

        foreach (var (button, _) in Pages) button.IsChecked = ReferenceEquals(button, clicked);
        _activePanel = target;
        CrossFadeToPanel(target);
    }

    /// <summary>タブ切り替え時、旧パネルをフェードアウトしてから新パネルをフェードインする</summary>
    private void CrossFadeToPanel(FrameworkElement target)
    {
        const int fadeOutMs = 110;
        const int fadeInMs = 180;

        foreach (var (_, panel) in Pages)
        {
            if (ReferenceEquals(panel, target) || panel.Visibility != Visibility.Visible) continue;
            var fadeOut = new DoubleAnimation(panel.Opacity, 0, TimeSpan.FromMilliseconds(fadeOutMs));
            fadeOut.Completed += (_, _) => panel.Visibility = Visibility.Collapsed;
            panel.BeginAnimation(OpacityProperty, fadeOut);
        }

        target.Opacity = 0;
        target.Visibility = Visibility.Visible;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(fadeInMs))
        {
            BeginTime = TimeSpan.FromMilliseconds(fadeOutMs - 30),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        target.UpdateLayout();
        NekoNoTe.Behaviors.Motion.PanelIn(target, TimeSpan.FromMilliseconds(fadeOutMs - 30));
    }
}
