using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace AuthIsland.Cli;

/// <summary>
/// 一键安装/卸载。
///
/// 目标：用户双击一次，之后什么都不用配。
///   · 程序复制到 %LOCALAPPDATA%\AuthIsland（不需要管理员权限）
///   · 自动写 WorkBuddy 的 hooks 配置（绝对路径，不依赖 python / node）
///   · 注册开机自启，胶囊常驻
///   · 立刻拉起胶囊
/// </summary>
public static class Installer
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AuthIsland";
    private const string Marker = ".installed";

    public static (bool Ok, string Message) Install(string? sourceExe = null)
    {
        try
        {
            var current = sourceExe ?? Environment.ProcessPath ?? "";
            if (current.Length == 0) return (false, "找不到当前程序路径");

            AppPaths.EnsureHome();

            // 已经在目标位置就跳过复制
            var samePath = string.Equals(
                Path.GetFullPath(current), Path.GetFullPath(AppPaths.TargetExe), StringComparison.OrdinalIgnoreCase);

            if (!samePath)
            {
                StopCapsule(killAll: true);
                CopyPayload(Path.GetDirectoryName(current)!, AppPaths.Home);
            }

            if (!File.Exists(AppPaths.TargetExe)) return (false, "复制程序失败（文件被占用？）");

            var (hooksOk, hooksMessage) = WriteHooks();

            RegisterAutoStart();

            File.WriteAllText(Path.Combine(AppPaths.Home, Marker),
                $"installed at {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n", Encoding.UTF8);

            StartCapsule();

            return hooksOk
                ? (true, $"安装完成。\n\n{hooksMessage}\n\n胶囊已在屏幕顶部就位：只有 WorkBuddy 自己判定需要授权时，才会把请求同步到胶囊；其余操作它内部就处理完了，不会打扰你。")
                : (false, $"程序已装好，但写入 WorkBuddy 配置失败：\n{hooksMessage}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Uninstall()
    {
        try
        {
            RemoveHooks();
            UnregisterAutoStart();
            StopCapsule(killAll: true);

            try { File.Delete(Path.Combine(AppPaths.Home, Marker)); } catch { }

            // 自己正跑在这个目录里，交给系统在自己退出后删
            ScheduleSelfDelete();
            return (true, "已卸载。WorkBuddy 中原有的权限设置已恢复。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 复制

    private static void CopyPayload(string from, string to)
    {
        var multiFile = File.Exists(Path.Combine(from, "AuthIsland.deps.json"));

        foreach (var file in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(file);
            var copy = name.StartsWith("AuthIsland", StringComparison.OrdinalIgnoreCase)
                       || (multiFile && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                         || name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
            if (!copy) continue;

            try
            {
                File.Copy(file, Path.Combine(to, name), overwrite: true);
            }
            catch (IOException)
            {
                // 目标被占用：跳过，通常是因为旧版本还在跑
            }
        }
    }

    // ---------------------------------------------------------------- hooks 配置

    private static string HookCommand(string mode) => $"\"{AppPaths.TargetExe}\" --hook {mode}";

    private static (bool Ok, string Message) WriteHooks()
    {
        var path = AppPaths.SettingsJson;
        var dir = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(dir);

            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : JsonNode.Parse(text) as JsonObject ?? new JsonObject();

                // 动别人的配置之前先留个备份
                File.Copy(path, $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            }
            else
            {
                root = new JsonObject();
            }

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            // 幂等：先清掉旧版 AuthIsland 条目，避免重复弹两次
            RemoveOurEntries(hooks);

            // 只挂 PermissionRequest：WorkBuddy 自己的权限系统判定「需要授权」时才会调它。
            // 不给 matcher = 所有工具都适用；但真正触发的只有宿主本来就要弹窗的那些。
            AppendHook(hooks, "PermissionRequest", null, HookCommand("permission"), 600);

            // 宿主起来了：立刻告诉胶囊（比进程轮询早最多 1 秒）。
            // SessionStart 在会话开/恢复时触发；auth_success 在登录成功时触发，覆盖冷启动。
            AppendHook(hooks, "SessionStart", null, HookCommand("hello"), 5);
            AppendHook(hooks, "Notification", "auth_success", HookCommand("hello"), 5);

            AppendHook(hooks, "Stop", null, HookCommand("stop"), 5);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            File.WriteAllText(path, root.ToJsonString(options), new UTF8Encoding(false));
            return (true, $"已接入 WorkBuddy（{path}）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static void AppendHook(JsonObject hooks, string eventName, string? matcher, string command, int timeout)
    {
        if (hooks[eventName] is not JsonArray list)
        {
            list = new JsonArray();
            hooks[eventName] = list;
        }

        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = command,
            ["timeout"] = timeout,
        };

        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray(hook),
        };
        if (matcher is not null) entry["matcher"] = matcher;

        list.Add(entry);
    }

    private static void RemoveHooks()
    {
        var path = AppPaths.SettingsJson;
        if (!File.Exists(path)) return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject;
            if (root?["hooks"] is not JsonObject hooks) return;

            RemoveOurEntries(hooks);

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, root.ToJsonString(options), new UTF8Encoding(false));
        }
        catch
        {
            // 配置坏了也不要卡住卸载
        }
    }

    private static void RemoveOurEntries(JsonObject hooks)
    {
        foreach (var key in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[key] is not JsonArray list) continue;

            foreach (var node in list.ToList())
            {
                if (node is not JsonObject entry) continue;
                if (entry["hooks"] is not JsonArray inner) continue;

                // 认出「我们的条目」：新版是 AuthIsland.exe，旧版是 ...\agent-auth-island\hooks\*.py
                var ours = inner.Any(h =>
                {
                    var cmd = (h is JsonObject o ? o["command"]?.ToString() : null) ?? "";
                    return cmd.Contains("authisland", StringComparison.OrdinalIgnoreCase)
                           || cmd.Contains("auth-island", StringComparison.OrdinalIgnoreCase)
                           || cmd.Contains("pre_tool_use.py", StringComparison.OrdinalIgnoreCase)
                           || cmd.Contains("stop_notify.py", StringComparison.OrdinalIgnoreCase);
                });

                if (ours) list.Remove(node);
            }

            if (list.Count == 0) hooks.Remove(key);
        }
    }

    // ---------------------------------------------------------------- 自启

    private static void RegisterAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(RunValue, $"\"{AppPaths.TargetExe}\"");
        }
        catch
        {
            // 注册表被策略锁住也不影响手动启动
        }
    }

    private static void UnregisterAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------------------------------------------------------- 进程

    public static void StopCapsule(bool killAll)
    {
        try
        {
            var self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName("AuthIsland"))
            {
                if (p.Id == self) continue;
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch
        {
            // 忽略
        }
    }

    public static void StartCapsule()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppPaths.TargetExe,
                UseShellExecute = true,
                WorkingDirectory = AppPaths.Home,
            });
        }
        catch
        {
            // 起不来时用户仍可双击 exe
        }
    }

    private static void ScheduleSelfDelete()
    {
        try
        {
            var args = $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{AppPaths.Home}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch
        {
            // 删不掉就留着，用户能自己删
        }
    }
}
