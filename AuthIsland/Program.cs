using System.Linq;
using System.Windows;
using AuthIsland.Cli;

namespace AuthIsland;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 钩子模式：完全不碰 WPF，几十毫秒就退出。
        // 这条路径会在每次工具调用前被拉起来一次，绝不能带 UI 的开销。
        var hook = ValueOf(args, "--hook");
        if (hook is not null) return HookRunner.Run(hook);

        if (args.Contains("--install"))
        {
            var (ok, message) = Installer.Install();
            HookRunner.Trace($"install: {ok} {message.Replace("\r\n", " ")}");
            return ok ? 0 : 1;
        }

        if (args.Contains("--uninstall"))
        {
            var (ok, _) = Installer.Uninstall();
            return ok ? 0 : 1;
        }

        if (args.Contains("--health"))
        {
            return IslandClient.IsAlive() ? 0 : 1;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            return i + 1 < args.Length ? args[i + 1] : "";
        }
        return null;
    }
}
