using System.Linq;
using System.Threading;
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

        // 单实例：钩子会在胶囊不在时自动把它拉起来，再加上开机自启，
        // 很容易出现两个胶囊同时抢 18765。第二个直接静默退出。
        try
        {
            _single = new Mutex(true, @"Local\AuthIsland.SingleInstance", out var created);
            if (!created) return 0;
        }
        catch
        {
            // 拿不到互斥体也不阻断启动（极端情况下宁可多开一个，也不要没有胶囊）
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    // 进程活着期间必须一直持有，回收就失去互斥
    private static Mutex? _single;

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
