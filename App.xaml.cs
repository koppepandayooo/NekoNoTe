using System.Diagnostics;
using System.Threading;
using System.Windows;
using NekoNoTe.Roblox;

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
        NekoNoTe.Behaviors.Motion.Init();
        RobloxPanel.ApplyStartupSettings();
        new MainWindow().Show();
    }

    /// <summary>インストーラーを裏で動かして自分は終わる(入れ終わるとインストーラーが NekoNoTe を起動する)</summary>
    public void RunInstallerAndExit(string installer)
    {
        Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS") { UseShellExecute = true });
        Shutdown();
    }
}
