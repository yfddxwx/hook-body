using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AuthIsland.Cli;

/// <summary>
/// 钩子模式。和胶囊 UI 共用同一个 exe，但走这条路径时**完全不加载 WPF**，
/// 启动只要几十毫秒——因为这个进程会在每次工具调用前被拉起来一次。
///
/// 命令行：
///   AuthIsland.exe --hook permission    WorkBuddy  PermissionRequest（宿主判定需要授权时才触发）
///   AuthIsland.exe --hook codex         Codex / ChatGPT 桌面版  PermissionRequest
///   AuthIsland.exe --hook antigravity   Antigravity  PreToolUse（fail-closed，需要本地闸门）
///   AuthIsland.exe --hook stop          WorkBuddy  Stop（任务完成通知）
///   AuthIsland.exe --hook pretooluse    旧接口，仅给「每次调用都要过闸门」的宿主用
/// </summary>
public static class HookRunner
{
    private static readonly int TimeoutMs = ReadInt("AUTH_ISLAND_TIMEOUT_MS", 60_000);

    /// <summary>
    /// PermissionRequest 的等待上限：用户可能离开一会儿。
    /// 必须小于钩子配置里的 timeout（600s），否则宿主先超时、原生弹窗和胶囊会打架。
    /// </summary>
    private static readonly int PermissionTimeoutMs = ReadInt("AUTH_ISLAND_PERMISSION_TIMEOUT_MS", 540_000);
    private static readonly int NotifyTimeoutMs = ReadInt("AUTH_ISLAND_NOTIFY_TIMEOUT_MS", 1500);
    private static readonly int NotifyChars = ReadInt("AUTH_ISLAND_NOTIFY_CHARS", 90);

    // 等宿主把 payload 写完的上限。正常是一次 read 就到，不会真等满。
    private static readonly int StdinBudgetMs = ReadInt("AUTH_ISLAND_STDIN_MS", 800);

    public static int Run(string mode)
    {
        Trace($"enter mode={mode} redirected={Console.IsInputRedirected}");
        try
        {
            switch (mode)
            {
                case "permission":
                case "permissionrequest":
                case "perm":
                    return PermissionRequest();
                case "pretooluse":
                case "pre":
                    return PreToolUse(hostIsWorkBuddy: true);
                case "codex":
                    return Codex();
                case "antigravity":
                    return Antigravity();
                case "stop":
                    return Stop();
                default:
                    Trace($"unknown hook mode: {mode}");
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Trace($"hook crashed: {ex.Message}");
            // Antigravity 的钩子必须永远给出一个决定，否则它会卡住工具调用
            if (mode == "antigravity") EmitJson("{\"decision\":\"ask\"}");
            return 0;
        }
    }

    // ---------------------------------------------------------------- WorkBuddy

    /// <summary>
    /// WorkBuddy 的 PermissionRequest：
    ///
    /// 宿主在 ask 分支里才会调用这个钩子 —— 也就是说，
    /// **「这次调用要不要授权」是 WorkBuddy 自己算出来的**，我们一个规则都不判。
    /// 钩子只做两件事：把请求搬到屏幕最上层的胶囊，把用户的选择原样回给宿主。
    /// 返回 allow/deny 时，宿主会直接 finish，它自己的审批弹窗根本不会出现；
    /// 我们返回空（胶囊没开 / 超时 / 用户没点），宿主就会退回原生弹窗 —— 天然 fail-open。
    /// </summary>
    private static int PermissionRequest()
    {
        var payload = ReadPayload();
        if (payload is null)
        {
            Trace("[PERM] empty payload -> 交回原生审批");
            return 0;
        }

        var (tool, input, cwd) = ReadTool(payload.Value);
        var detail = Summarize(tool, input);

        var decision = IslandClient.Ask(tool, detail, cwd, "normal", "", PermissionTimeoutMs);
        Trace($"[PERM] tool={tool} cwd={cwd} decision={decision} detail={Truncate(detail, 80)}");

        if (decision == "pass") return 0;

        object inner = decision == "allow"
            ? new Dictionary<string, object?> { ["behavior"] = "allow" }
            : new Dictionary<string, object?>
            {
                ["behavior"] = "deny",
                ["message"] = "已由 AuthIsland 授权胶囊拒绝",
            };

        EmitJson(ToJson(new Dictionary<string, object?>
        {
            ["hookSpecificOutput"] = new Dictionary<string, object?>
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = inner,
            },
        }));
        return 0;
    }

    private static int PreToolUse(bool hostIsWorkBuddy)
    {
        var payload = ReadPayload();

        // 空载荷 = 宿主的「运行校验」探测（不是真实调用）。
        // 直接回 allow，否则闸门会把它当未知调用弹卡挂 60 秒。
        if (payload is null)
        {
            Trace("empty payload (probe) -> allow");
            EmitJson("{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\"," +
                     "\"permissionDecision\":\"allow\",\"permissionDecisionReason\":\"AuthIsland: probe\"}}");
            return 0;
        }

        var (tool, input, cwd) = ReadTool(payload.Value);
        var gate = Gate.Evaluate(tool, input, cwd);
        Trace($"tool={tool} gate={gate.Action}/{gate.Risk}/{gate.Reason}");

        if (gate.Action == GateAction.Silent)
        {
            if (Gate.SilentEmits == "allow")
            {
                EmitJson(ToJson(new Dictionary<string, object?>
                {
                    ["hookSpecificOutput"] = new Dictionary<string, object?>
                    {
                        ["hookEventName"] = "PreToolUse",
                        ["permissionDecision"] = "allow",
                        ["permissionDecisionReason"] = $"AuthIsland：默认权限内（{gate.Reason}）",
                    },
                }));
            }
            return 0;
        }

        var detail = Summarize(tool, input);
        Trace($"  detail={Truncate(detail, 90)}");

        var decision = IslandClient.Ask(tool, detail, cwd, gate.Risk, gate.Reason, TimeoutMs);
        Trace($"  decision={decision}");

        // pass = 不表态，让 WorkBuddy 走它自己的权限流程
        if (decision == "pass") return 0;

        EmitJson(ToJson(new Dictionary<string, object?>
        {
            ["hookSpecificOutput"] = new Dictionary<string, object?>
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision,
                ["permissionDecisionReason"] = $"由 AuthIsland 授权胶囊处理：{gate.Reason}",
            },
        }));
        return 0;
    }

    // ---------------------------------------------------------------- Codex

    private static int Codex()
    {
        var payload = ReadPayload();
        if (payload is null) return 0;

        var (tool, input, cwd) = ReadTool(payload.Value);

        // Codex 有时会给出人类可读的审批原因，优先展示它
        var detail = input.TryGetValue("description", out var desc) && !string.IsNullOrWhiteSpace(desc)
            ? desc.Trim()
            : Summarize(tool, input);

        // 这个钩子只在 Codex 正要弹审批时才触发，本身就已经是「超权」，
        // 所以这里不做闸门判断，否则等于用我们的白名单去放宽 Codex 的策略。
        var decision = IslandClient.Ask(tool, detail, cwd, Gate.Medium, "Codex 请求审批", TimeoutMs);
        if (decision == "pass") return 0;

        object inner = decision == "allow"
            ? new Dictionary<string, object?> { ["behavior"] = "allow" }
            : new Dictionary<string, object?>
            {
                ["behavior"] = "deny",
                ["message"] = "由 AuthIsland 拒绝",
            };

        EmitJson(ToJson(new Dictionary<string, object?>
        {
            ["hookSpecificOutput"] = new Dictionary<string, object?>
            {
                ["hookEventName"] = "PermissionRequest",
                ["decision"] = inner,
            },
        }));
        return 0;
    }

    // ---------------------------------------------------------------- Antigravity

    private static int Antigravity()
    {
        // fail-closed：任何异常都必须给出决定，否则 Antigravity 会卡在等钩子上
        var payload = ReadPayload();
        if (payload is null)
        {
            EmitJson("{\"decision\":\"ask\"}");
            return 0;
        }

        try
        {
            var (tool, input, cwd) = ReadTool(payload.Value);
            var gate = Gate.Evaluate(tool, input, cwd);

            if (gate.Action == GateAction.Silent)
            {
                if (Gate.SilentEmits == "allow") EmitJson("{\"decision\":\"allow\"}");
                else EmitJson("{\"decision\":\"ask\"}");
                return 0;
            }

            var decision = IslandClient.Ask(tool, Summarize(tool, input), cwd, gate.Risk, gate.Reason, TimeoutMs);
            EmitJson(decision switch
            {
                "allow" => "{\"decision\":\"allow\"}",
                "deny" => "{\"decision\":\"deny\"}",
                _ => "{\"decision\":\"ask\"}",
            });
        }
        catch
        {
            EmitJson("{\"decision\":\"ask\"}");
        }
        return 0;
    }

    // ---------------------------------------------------------------- Stop

    private static int Stop()
    {
        // 硬约束：永远 exit 0、永远不往 stdout 写任何东西。
        // Stop 钩子的 stdout 会被宿主当成「是否阻止 agent 停止」的指令。
        var payload = ReadPayload();
        if (payload is null) return 0;
        var root = payload.Value;

        var sessionId = GetString(root, "session_id", "sessionId") ?? "";
        var summary = GetString(root, "last_assistant_message", "summary") ?? "";
        var cwd = GetString(root, "cwd") ?? "";

        Trace($"[STOP] session={sessionId}");

        IslandClient.Notify(sessionId, Squeeze(summary), cwd, NotifyTimeoutMs);
        return 0;
    }

    // ---------------------------------------------------------------- 工具

    private static (string Tool, Dictionary<string, string> Input, string Cwd) ReadTool(JsonElement payload)
    {
        var tool = GetString(payload, "tool_name", "toolName", "tool") ?? "unknown";
        var cwd = GetString(payload, "cwd", "workspace", "workingDirectory") ?? "";

        var input = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "tool_input", "toolInput", "input", "arguments" })
        {
            if (!payload.TryGetProperty(name, out var node)) continue;
            if (node.ValueKind != JsonValueKind.Object) continue;

            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    input[prop.Name] = prop.Value.GetString() ?? "";
            }
            break;
        }

        return (tool, input, cwd);
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            if (!obj.TryGetProperty(name, out var node)) continue;
            if (node.ValueKind == JsonValueKind.String)
            {
                var value = node.GetString();
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
        }
        return null;
    }

    private static readonly string[] DetailKeys =
        { "command", "cmd", "file_path", "path", "url", "query", "pattern", "prompt", "description" };

    /// <summary>把工具参数压成一行给人看的话。</summary>
    private static string Summarize(string tool, Dictionary<string, string> input)
    {
        foreach (var key in DetailKeys)
        {
            if (input.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return Truncate(value.Trim(), 400);
        }

        if (input.Count == 0) return tool;
        return Truncate(string.Join(", ", input.Select(kv => $"{kv.Key}={kv.Value}")), 400);
    }

    /// <summary>把最后一条回复压成一行摘要：去代码块、去 Markdown 记号、折叠空白。</summary>
    private static string Squeeze(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var t = Regex.Replace(text, "```.*?```", " ", RegexOptions.Singleline);
        t = Regex.Replace(t, @"!\[[^\]]*\]\([^)]*\)", " ");
        t = Regex.Replace(t, @"\[([^\]]*)\]\([^)]*\)", "$1");
        t = Regex.Replace(t, @"[*_`#>|~]", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();

        return t.Length > NotifyChars ? t[..NotifyChars] + "…" : t;
    }

    private static string Truncate(string text, int max) => text.Length > max ? text[..max] : text;

    /// <summary>
    /// 读宿主喂进来的 JSON。
    ///
    /// 这里刻意**不等 EOF**：宿主的管道写端往往被子进程继承，
    /// 于是 read-to-end 会一直挂着（实测 Python 的 subprocess 就是如此），
    /// 每次工具调用卡满超时。改成「括号配对到完整对象就立刻返回」，
    /// 正常情况一次 read 就能拿到全部内容，耗时接近 0。
    /// </summary>
    private static JsonElement? ReadPayload()
    {
        try
        {
            if (!Console.IsInputRedirected) return null;

            var buffer = new List<byte>(4096);
            var gate = new object();
            var eof = false;
            var stdin = Console.OpenStandardInput();

            var reader = new Thread(() =>
            {
                try
                {
                    var chunk = new byte[8192];
                    while (true)
                    {
                        var read = stdin.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;
                        lock (gate)
                        {
                            buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                            Monitor.PulseAll(gate);
                        }
                    }
                }
                catch
                {
                    // 宿主提前关管道：当作结束
                }
                lock (gate)
                {
                    eof = true;
                    Monitor.PulseAll(gate);
                }
            })
            {
                IsBackground = true,
                Name = "hook-stdin",
            };
            reader.Start();

            var deadline = Environment.TickCount64 + StdinBudgetMs;

            lock (gate)
            {
                while (true)
                {
                    var snapshot = buffer.ToArray();
                    if (TryExtractJson(snapshot, eof, out var json)) return ParseJson(json);
                    if (eof) return null;

                    var left = deadline - Environment.TickCount64;
                    if (left <= 0) return null;
                    Monitor.Wait(gate, (int)Math.Min(left, 120));
                }
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>在字节层面做括号配对（UTF-8 的多字节高位不会撞上 ASCII 括号），找到完整对象就返回。</summary>
    private static bool TryExtractJson(byte[] data, bool eof, out string json)
    {
        json = "";
        var start = Array.IndexOf(data, (byte)'{');
        if (start < 0) return false;

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < data.Length; i++)
        {
            var c = (char)data[i];

            if (inString)
            {
                if (escape) { escape = false; continue; }
                if (c == '\\') { escape = true; continue; }
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        json = Encoding.UTF8.GetString(data, start, i - start + 1);
                        return true;
                    }
                    break;
            }
        }

        // 没配对上但有 EOF：可能是残缺内容，交给调用方当空载荷
        return false;
    }

    private static JsonElement? ParseJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            // 必须 Clone：doc 释放后 element 不能再用
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static void EmitJson(string json)
    {
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch
        {
            // 宿主没接 stdout：丢弃即可
        }
    }

    private static string ToJson(Dictionary<string, object?> value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static int ReadInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    /// <summary>诊断日志：确认宿主到底有没有调用钩子、闸门怎么判的。</summary>
    public static void Trace(string message)
    {
        try
        {
            AppPaths.EnsureHome();
            var file = new FileInfo(AppPaths.HookLog);
            if (file.Exists && file.Length > 256 * 1024) file.Delete();
            File.AppendAllText(AppPaths.HookLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\r\n", Encoding.UTF8);
        }
        catch
        {
            // 日志写不进去绝不能影响判定
        }
    }
}
