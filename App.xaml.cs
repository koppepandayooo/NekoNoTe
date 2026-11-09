using System.Threading;
using System.Windows;

namespace NekoNoTe;

public partial class App : System.Windows.Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, "NekoNoTe.SingleInstance", out var first);
        if (!first)
        {
            System.Windows.MessageBox.Show("NekoNoTe はもう起動しています。", "NekoNoTe");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
