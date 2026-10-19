using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AuthIsland.Services;

/// <summary>
/// 判断宿主（WorkBuddy / Codex / Antigravity 等）还在不在。
///
/// 为什么用进程探测而不是靠钩子报"我回来了"：
///   · 宿主被强杀 / 崩溃 / 直接从任务管理器结束，没有任何钩子会触发；
///   · 宿主启动时也未必有稳定可挂的钩子（Codex 的钩子要手动 trust）。
/// 而进程表是唯一"关了就一定变"的事实来源，所以以它为准，钩子只做加速。
///
/// WorkBuddy 是多进程（Electron 派系，本机实测 9 个进程同挂在一个主进程下），
/// 所以这里按**进程名**统计。代价是：一个进程都不在 ≠ 一定是关掉了 UI，
/// 但反过来（还有进程在）一定是活着 —— 这个方向的误判只会让我们"少报离线"，可接受。
/// </summary>
public static class HostWatcher
{
    /// <summary>认为"宿主在场"的进程名（不含 .exe，不区分大小写）。可用环境变量追加。</summary>
    private static readonly string[] DefaultNames =
    {
        "WorkBuddy", "CodeBuddy",
        "Codex", "ChatGPT",
        "Antigravity", "Gemini",
    };

    private static string[] Names
    {
        get
        {
            var extra = Environment.GetEnvironmentVariable("AUTH_ISLAND_HOST_PROCESS") ?? "";
            var parts = extra.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => s.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase))
                             .Where(s => s.Length > 0)
                             .ToArray();

            // AUTH_ISLAND_HOST_ONLY=1：只认显式指定的进程名，忽略内置清单。
            // 用途一：只用 Codex 的人不想被一个碰巧叫 ChatGPT 的进程骗成"在线"。
            // 用途二：调试离线态时指向一个不存在的进程名即可，不用真去杀宿主。
            if (Environment.GetEnvironmentVariable("AUTH_ISLAND_HOST_ONLY") == "1")
                return parts.Length == 0 ? new[] { "__authisland_none__" } : parts;

            return parts.Length == 0 ? DefaultNames : DefaultNames.Concat(parts).Distinct().ToArray();
        }
    }

    public static bool AnyRunning()
    {
        foreach (var name in Names)
        {
            try
            {
                var list = Process.GetProcessesByName(name);
                if (list.Length == 0) continue;

                foreach (var p in list) p.Dispose();
                return true;
            }
            catch
            {
                // 查询失败当作"可能有"，宁可少报离线
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 把正在运行的宿主窗口提到前台。没有宿主时返回 false。
    ///
    /// 只对 WorkBuddy 这类 Electron 应用尝试：优先匹配主窗口（有标题、无 owner），
    /// 找到就直接 SetForegroundWindow。失败也不抛，调用方当"没找到"处理。
    /// </summary>
    public static bool BringToFront()
    {
        foreach (var name in Names)
        {
            int[] ids;
            try
            {
                var list = Process.GetProcessesByName(name);
                ids = list.Select(p => p.Id).ToArray();
                foreach (var p in list) p.Dispose();
            }
            catch
            {
                continue;
            }

            foreach (var pid in ids)
            {
                var hwnd = FindMainWindow(pid);
                if (hwnd == IntPtr.Zero) continue;

                try
                {
                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                    return SetForegroundWindow(hwnd);
                }
                catch
                {
                    // 继续试下一个
                }
            }
        }
        return false;
    }

    private static IntPtr FindMainWindow(int pid)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)pid) return true;

            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;   // 排除子窗口/工具窗

            var len = GetWindowTextLength(hwnd);
            if (len <= 0) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    private const int SW_RESTORE = 9;
    private const uint GW_OWNER = 4;

    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
}
