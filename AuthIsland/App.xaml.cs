using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using AuthIsland.Cli;

namespace AuthIsland;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 胶囊常驻，任何未处理异常都不应该让浮层消失
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            TryLog(args.Exception);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TryLog(args.ExceptionObject as Exception);

        // 还没装过：先给一个「一键安装」的向导，而不是让人对着胶囊发愣
        var portable = e.Args.Contains("--portable", System.StringComparer.OrdinalIgnoreCase);
        if (!AppPaths.IsInstalled && !portable)
        {
            var setup = new SetupWindow();
            MainWindow = setup;
            setup.Show();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void TryLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            AppPaths.EnsureHome();
            File.AppendAllText(
                AppPaths.ErrorLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 日志失败也不能影响主流程
        }
    }
}
