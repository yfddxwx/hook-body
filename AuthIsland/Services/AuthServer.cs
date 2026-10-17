using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AuthIsland.Services;

/// <summary>
/// 本地授权服务。hook 发一个 POST /authorize 进来，这个连接会一直挂着，
/// 直到用户点了胶囊上的按钮，才把 allow / deny 写回去。
///
/// 用裸 TcpListener 而不是 HttpListener：HttpListener 绑定前缀需要 URL ACL，
/// 普通用户权限下容易 503，这里手写最小 HTTP 就完全没有部署门槛。
/// </summary>
public sealed class AuthServer : IDisposable
{
    public const int DefaultPort = 18765;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public event Action<AuthRequest>? RequestReceived;
    public event Action<AuthRequest>? RequestExpired;

    /// <summary>任务跑完的通知（Stop hook 推过来）。这个端点不挂起，收到就回。</summary>
    public event Action<DoneNotice>? NotifyReceived;

    public int Port { get; }

    /// <summary>暂停拦截：所有请求直接返回 pass，交回宿主自己的审批流程。</summary>
    public bool Paused { get; set; }

    public bool IsRunning { get; private set; }

    public AuthServer(int port = DefaultPort)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var stream = client.GetStream();

                var (method, path, body) = await ReadHttpAsync(stream, _cts.Token);

                if (method == "POST" && path.StartsWith("/authorize", StringComparison.Ordinal))
                {
                    await HandleAuthorizeAsync(stream, body);
                }
                else if (method == "POST" && path.StartsWith("/notify", StringComparison.Ordinal))
                {
                    await HandleNotifyAsync(stream, body);
                }
                else if (path.StartsWith("/health", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 200, "{\"ok\":true}");
                }
                else if (method == "POST" && path.StartsWith("/shutdown", StringComparison.Ordinal))
                {
                    await WriteJsonAsync(stream, 200, "{\"ok\":true}");
                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                        System.Windows.Application.Current.Shutdown());
                }
                else
                {
                    await WriteJsonAsync(stream, 404, "{\"error\":\"not found\"}");
                }
            }
            catch
            {
                // 连接异常直接丢弃，不影响下一次请求
            }
        }
    }

    private async Task HandleAuthorizeAsync(NetworkStream stream, string body)
    {
        AuthRequest? req = null;
        try
        {
            req = JsonSerializer.Deserialize<AuthRequest>(body, JsonOpts);
        }
        catch
        {
            // 解析失败按 pass 处理，交回 WorkBuddy 自己的权限流程
        }

        if (req is null || string.IsNullOrWhiteSpace(req.Tool))
        {
            await WriteJsonAsync(stream, 400, "{\"decision\":\"pass\"}");
            return;
        }

        // 用户暂停了拦截：不弹卡片，让宿主自己弹它原生的审批
        if (Paused)
        {
            await WriteJsonAsync(stream, 200, "{\"decision\":\"pass\"}");
            return;
        }

        if (req.TimeoutMs <= 0) req.TimeoutMs = 60_000;
        req.Deadline = DateTime.UtcNow.AddMilliseconds(req.TimeoutMs);

        RequestReceived?.Invoke(req);

        var timeout = Task.Delay(req.TimeoutMs, _cts.Token);

        // 盯着 socket：Agent 被关掉（比如 WorkBuddy 整个退出）时连接会断，
        // 这里能立刻感知，而不是让用户对着一张再也点不动的卡片等满 60 秒。
        var clientGone = WaitForClientCloseAsync(stream, _cts.Token);

        var finished = await Task.WhenAny(req.Completion.Task, timeout, clientGone);

        if (finished == clientGone)
        {
            req.Completion.TrySetResult(new AuthDecision
            {
                Decision = "pass",
                Reason = "客户端已断开"
            });
            RequestExpired?.Invoke(req);
            return; // 连接没了，别再写响应
        }

        string decision;
        if (finished == req.Completion.Task && req.Completion.Task.IsCompletedSuccessfully)
        {
            decision = req.Completion.Task.Result.Decision;
        }
        else
        {
            RequestExpired?.Invoke(req);
            decision = "pass"; // 超时：交回原生权限流程，而不是替用户拍板
        }

        await WriteJsonAsync(stream, 200, $"{{\"decision\":\"{decision}\"}}");
    }

    /// <summary>
    /// 任务完成通知。立刻回包，绝不让 Stop hook 等 —— 它是在一轮对话结束时同步跑的。
    /// </summary>
    private async Task HandleNotifyAsync(NetworkStream stream, string body)
    {
        try
        {
            var notice = JsonSerializer.Deserialize<DoneNotice>(body, JsonOpts);
            if (notice is not null &&
                !string.IsNullOrWhiteSpace(notice.SessionId + notice.Summary))
            {
                NotifyReceived?.Invoke(notice);
            }
        }
        catch
        {
            // 解析失败也不能让 hook 报错
        }

        await WriteJsonAsync(stream, 200, "{\"ok\":true}");
    }

    /// <summary>
    /// 等待对端关闭连接。请求体已经读完，这里再读只会等到 EOF 或异常。
    /// 正常点击的场景下这个任务会一直挂着，直到 HandleClientAsync 释放 socket 才自然结束。
    /// </summary>
    private static async Task<bool> WaitForClientCloseAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(0, 1), ct);
                if (n <= 0) return true;
            }
        }
        catch
        {
            // 取消 / 连接重置 / 客户端进程消失，都算"对端没了"
            return true;
        }

        return false;
    }

    // ---------- 极简 HTTP ----------

    private static async Task<(string Method, string Path, string Body)> ReadHttpAsync(
        NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int headerEnd = -1;

        while (ms.Length < 1_048_576)
        {
            int n = await stream.ReadAsync(buffer, ct);
            if (n <= 0) break;
            ms.Write(buffer, 0, n);

            if (headerEnd < 0) headerEnd = FindHeaderEnd(ms);

            if (headerEnd >= 0)
            {
                var head = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
                int contentLength = ParseContentLength(head);
                if (ms.Length - headerEnd >= contentLength) break;
            }
        }

        if (headerEnd < 0) return ("", "", "");

        var headText = Encoding.ASCII.GetString(ms.GetBuffer(), 0, headerEnd);
        var bodyText = Encoding.UTF8.GetString(
            ms.GetBuffer(), headerEnd, (int)ms.Length - headerEnd);

        var lines = headText.Split("\r\n");
        var parts = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();

        var method = parts.Length > 0 ? parts[0] : "";
        var path = parts.Length > 1 ? parts[1] : "";

        return (method, path, bodyText);
    }

    private static int FindHeaderEnd(MemoryStream ms)
    {
        var buf = ms.GetBuffer();
        int len = (int)ms.Length;
        for (int i = 3; i < len; i++)
        {
            if (buf[i - 3] == 13 && buf[i - 2] == 10 && buf[i - 1] == 13 && buf[i] == 10)
                return i + 1;
        }
        return -1;
    }

    private static int ParseContentLength(string head)
    {
        foreach (var line in head.Split("\r\n"))
        {
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            if (!line[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            return int.TryParse(line[(idx + 1)..].Trim(), out var v) ? v : 0;
        }
        return 0;
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int code, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head =
            $"HTTP/1.1 {code} {(code == 200 ? "OK" : "ERR")}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
