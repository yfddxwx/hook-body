using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace AuthIsland.Cli;

/// <summary>
/// 胶囊客户端。刻意不用 HttpClient：
///   · HttpClient 默认继承系统代理，本机回环请求会被错误地丢给代理（白等甚至失败）；
///   · 每次工具调用都要起一个进程，HttpClient 的初始化开销不划算。
/// 裸 TcpClient 只做一件事——连本机、发一行 HTTP——毫秒级且可控。
/// </summary>
public static class IslandClient
{
    public const int DefaultPort = 18765;

    private const int ProbeMs = 350;

    public static int Port
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("AUTH_ISLAND_PORT");
            return int.TryParse(raw, out var p) && p > 0 ? p : DefaultPort;
        }
    }

    /// <summary>胶囊是否在监听。探测失败就当它没开，直接 pass（安全降级）。</summary>
    public static bool IsAlive()
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", Port);
            return task.Wait(ProbeMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 胶囊不在就把它拉起来（仅限已安装到本机的情况）。
    ///
    /// 为什么要这一步：胶囊被任务管理器结束、被安全软件清掉、或开机时自启被拦，
    /// 用户唯一的感受就是"怎么不弹了"，且完全不知道该怎么办。
    /// 让下一次钩子顺手把它拉起来，是唯一不需要用户动手的补救方式。
    /// </summary>
    public static bool EnsureRunning(int waitMs = 2500)
    {
        if (IsAlive()) return true;
        if (!File.Exists(AppPaths.TargetExe)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.TargetExe)
            {
                UseShellExecute = true,
                WorkingDirectory = AppPaths.Home,
            });
        }
        catch
        {
            return false;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < waitMs)
        {
            Thread.Sleep(120);
            if (IsAlive()) return true;
        }
        return false;
    }

    /// <summary>POST 一段 JSON，返回响应正文；任何异常返回 null。</summary>
    public static string? Post(string path, string json, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", Port);
            if (!connect.Wait(ProbeMs) || !client.Connected) return null;

            client.NoDelay = true;
            using var stream = client.GetStream();
            stream.ReadTimeout = Math.Max(1000, timeoutMs);
            stream.WriteTimeout = 5000;

            var body = Encoding.UTF8.GetBytes(json);
            var head =
                $"POST {path} HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n";

            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();

            var buffer = new byte[4096];
            var sb = new StringBuilder();
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (sb.Length > 64 * 1024) break;
            }

            var text = sb.ToString();
            var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return split >= 0 ? text[(split + 4)..] : text;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 问用户。返回 "allow" | "deny" | "pass"。
    /// "pass" 的语义是「我不决定」，由宿主退回自己的原生审批流程。
    /// </summary>
    public static string Ask(string tool, string detail, string cwd, string risk, string reason, int timeoutMs)
    {
        if (!EnsureRunning(3000)) return "pass";

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = $"{Environment.ProcessId}-{DateTime.UtcNow.Ticks}",
            ["tool"] = string.IsNullOrWhiteSpace(tool) ? "unknown" : tool,
            ["detail"] = detail ?? "",
            ["cwd"] = cwd ?? "",
            ["timeoutMs"] = timeoutMs,
            ["risk"] = string.IsNullOrWhiteSpace(risk) ? "normal" : risk,
            ["reason"] = reason ?? "",
        });

        var response = Post("/authorize", payload, timeoutMs + 5000);
        if (response is null) return "pass";

        try
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("decision", out var d))
            {
                var value = d.GetString() ?? "";
                return value is "allow" or "deny" ? value : "pass";
            }
        }
        catch
        {
            // 响应不是 JSON：按 pass 处理
        }
        return "pass";
    }

    public static void Notify(string sessionId, string summary, string cwd, int timeoutMs)
    {
        if (!EnsureRunning(1000)) return;

        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId ?? "",
            ["summary"] = summary ?? "",
            ["cwd"] = cwd ?? "",
        });

        Post("/notify", payload, timeoutMs);
    }

    /// <summary>
    /// 报到：告诉胶囊"宿主起来了"。用在 SessionStart / 登录成功这类钩子上，
    /// 让离线态比进程轮询早一点消掉（轮询最多慢 1 秒，这个几乎即时）。
    /// 失败完全忽略——它只是加速，不是必需。
    /// </summary>
    public static void Hello(int timeoutMs = 600)
    {
        if (!EnsureRunning(1500)) return;
        Post("/hello", "{}", timeoutMs);
    }
}
