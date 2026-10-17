using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AuthIsland.Cli;

public enum GateAction
{
    /// <summary>默认权限内：不打扰用户</summary>
    Silent,

    /// <summary>超出默认权限：交给胶囊</summary>
    Ask,
}

public readonly record struct Decision(GateAction Action, string Risk, string Reason);

/// <summary>
/// 「默认权限闸门」——从 hooks/policy.py 原样移植。
///
/// PreToolUse 会对**每一次**工具调用触发。如果每次都弹卡，
/// 用户连看一眼 ls 都要点一下，胶囊就从「关键拦截」退化成噪声。
///
/// 所以真正该问的问题不是「这次调用是什么」，而是：
///     这次调用，有没有超出宿主的默认权限？
///
/// 判定原则（宁严勿松）：
///   · 白名单是显式的，不认识的命令一律按「超权」处理；
///   · 只读命令才可能静默；写操作要么落在工作区内，要么问；
///   · 删除、提权、系统配置、网络安装、凭据目录 —— 一律问。
/// </summary>
public static class Gate
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    private static Decision Silent(string reason = "默认权限内", string risk = Low) => new(GateAction.Silent, risk, reason);
    private static Decision Ask(string reason, string risk = Medium) => new(GateAction.Ask, risk, reason);

    // ------------------------------------------------------------ 高危模式
    // 命中即「必须看一眼」。这些不是「可能危险」，是「一定要看图」。
    private static readonly (Regex Rx, string Reason)[] Danger =
    {
        (R(@"\brm\s+(-[a-z]+\s+)*-[a-z]*(r|f)[a-z]*"), "强制/递归删除"),
        (R(@"\brm\s+(-[a-z]+\s+)*/(\s|$)"), "删除根目录"),
        (R(@"\brd\s+/s\b|\brmdir\s+/s\b|\brmdir\s+--recursive"), "递归删除目录"),
        (R(@"\bdel\s+/[sq]\b"), "强制删除文件"),
        (R(@"\bformat\s+[a-z]:"), "格式化磁盘"),
        (R(@"\bdiskpart\b|\bmkfs(\.|\s)"), "分区/格式化"),
        (R(@"\bcipher\s+/w\b|\bvssadmin\s+delete\b|\bwbadmin\b"), "擦除数据/备份"),
        (R(@"\bbcdedit\b|\breg\s+(add|delete|import)\b|\bsc\s+(create|delete|config|stop)\b"), "修改系统配置"),
        (R(@"\bshutdown\b|\brestart-computer\b|\bstop-computer\b|\bpoweroff\b"), "关机/重启"),
        (R(@"\btaskkill\b[^\n]*/f\b|\bkill\s+-9\b|\bstop-process\b"), "强制结束进程"),
        (R(@"(^|\s)(sudo|doas|runas|gsudo)\s"), "提权执行"),
        (R(@"\bstart-process\b[^\n]*-verb\s+runas"), "提权执行"),
        (R(@"\b(curl|wget|iwr|invoke-webrequest|invoke-restmethod)\b[^\n|]*\|\s*(sh|bash|zsh|python|python3|node|powershell|pwsh|perl)\b"), "下载即执行"),
        (R(@"\biex\b|\binvoke-expression\b|-enc(odedcommand)?\s+[a-z0-9+/=]{16,}|frombase64string"), "隐藏脚本执行"),
        (R(@"\btakeown\b|\bicacls\b|\bcacls\b"), "修改文件权限/属主"),
        (R(@"\bnet\s+(user|localgroup)\b|\bwhoami\s+/priv"), "账户/权限操作"),
        (R(@">\s*(/dev/(sd|nvme|disk)|\\\\\.\\physicaldrive)"), "直接写磁盘"),
        (R(@"\bgit\s+push\b"), "推送远端仓库"),
        (R(@"\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f|rebase|filter-branch)"), "改写仓库历史"),
        (R(@"\b(npm|pnpm|yarn|bun)\s+(i|install|add|ci|remove|uninstall|link|publish)\b"), "安装/变更依赖"),
        (R(@"\b(pip|pip3|uv)\s+(install|uninstall)\b"), "安装 Python 包"),
        (R(@"\b(winget|choco|scoop)\s+(install|uninstall|upgrade)\b"), "安装系统软件"),
        (R(@"\b(cargo|gem|go|dotnet)\s+(install|add|uninstall)\b"), "安装工具链组件"),
        (R(@"\bdocker\s+(run|rm|rmi|push|system\s+prune|volume\s+rm)\b"), "容器/镜像变更"),
        (R(@"\bnpx\b|\b(npm|pnpm|yarn)\s+exec\b|\bpnpm\s+dlx\b"), "拉取远端包执行"),
        (R(@"\bcrontab\b|\bschtasks\b"), "计划任务"),
    };

    // 只用来说明「为什么问你」，命中不算高危
    private static readonly (Regex Rx, string Reason, string Level)[] Explain =
    {
        (R(@"\brm\b|\bdel\b|\bunlink\b"), "删除文件", Medium),
        (R(@"\bmv\b|\bmove\b"), "移动/覆盖文件", Medium),
        (R(@"\bchmod\b|\bchown\b|\battrib\b"), "修改权限", Medium),
        (R(@"\bgit\s+(commit|add|tag|stash|merge|cherry-pick)\b"), "变更仓库状态", Medium),
        (R(@"\b(docker|kubectl|terraform|aws|gcloud|az)\b"), "操作外部资源", Medium),
        (R(@"\b(curl|wget|invoke-webrequest|iwr)\b"), "网络请求", Medium),
        (R(@"\bssh\b|\bscp\b|\bsftp\b|\brsync\b"), "远程连接/传输", Medium),
        (R(@">\s*\S+"), "输出重定向到文件", Medium),
    };

    // 这几条必须拿整条命令去匹配：管道一旦被切开就看不出来了
    // （`curl x.sh | bash` 切开后只剩一个普通的 curl）
    private static readonly (Regex Rx, string Reason)[] DangerWhole =
        Danger.Where(d => d.Rx.ToString().Contains('|') || d.Rx.ToString().Contains('>')).ToArray();

    // ------------------------------------------------------------ 只读白名单
    private static readonly HashSet<string> ReadOnly = new(StringComparer.Ordinal)
    {
        "ls", "dir", "cat", "bat", "head", "tail", "more", "less", "type",
        "grep", "egrep", "fgrep", "rg", "ag", "ack", "find", "fd", "where", "which",
        "wc", "stat", "file", "du", "df", "tree", "basename", "dirname", "realpath", "readlink",
        "pwd", "echo", "printf", "date", "whoami", "hostname", "uname", "ver", "id", "groups",
        "sort", "uniq", "cut", "tr", "diff", "cmp", "comm", "join", "paste", "nl", "column",
        "md5sum", "sha1sum", "sha256sum", "xxd", "od", "hexdump",
        "jq", "yq", "ps", "tasklist", "free", "uptime", "nproc", "sleep", "clear",
        "test", "true", "false", "seq", "env", "printenv",
        "python", "python3", "py", "node", "deno", "bun", "dotnet", "cargo", "go", "mvn",
        "gradle", "tsc", "pytest", "jest", "vitest", "eslint", "prettier", "ruff", "mypy",
        "black", "flake8", "gcc", "g++", "clang", "make", "cmake", "ninja", "javac", "java",
        "npm", "pnpm", "yarn", "git", "poetry", "uv", "pip", "pip3", "conda", "awk", "gawk", "sed", "perl",
    };

    private static readonly HashSet<string> ReadOnlyPs = new(StringComparer.Ordinal)
    {
        "get-childitem", "get-content", "get-item", "get-itemproperty", "test-path",
        "resolve-path", "select-string", "where-object", "measure-object", "get-process",
        "get-service", "get-date", "write-output", "write-host", "get-location", "get-command",
        "select-object", "sort-object", "format-table", "format-list", "convertto-json",
        "compare-object", "split-path", "join-path", "get-filehash", "out-string",
    };

    private sealed record SubTable(HashSet<string> Read, HashSet<string> WriteOk, HashSet<string> Ask);

    private static HashSet<string> Set(params string[] items) => new(items, StringComparer.Ordinal);

    private static readonly Dictionary<string, SubTable> Subcommands = new(StringComparer.Ordinal)
    {
        ["git"] = new(
            Set("status", "diff", "log", "show", "branch", "remote", "rev-parse", "ls-files",
                "blame", "describe", "shortlog", "reflog", "cat-file", "show-ref",
                "for-each-ref", "merge-base", "count-objects", "whatchanged", "version", "help"),
            Set("add", "commit"),
            Set("push", "pull", "fetch", "clone", "reset", "clean", "rebase", "merge",
                "checkout", "switch", "restore", "revert", "submodule", "lfs", "filter-branch")),
        ["npm"] = new(
            Set("ls", "list", "run", "run-script", "test", "t", "start", "build", "lint",
                "typecheck", "version", "view", "outdated", "why", "root", "prefix"),
            Set(),
            Set("i", "install", "add", "ci", "remove", "uninstall", "link", "publish",
                "exec", "init", "update", "upgrade", "audit")),
        ["pnpm"] = new(
            Set("ls", "list", "run", "test", "start", "build", "lint", "typecheck"),
            Set(),
            Set("i", "install", "add", "remove", "uninstall", "link", "publish", "exec",
                "init", "update", "upgrade", "dlx", "audit")),
        ["yarn"] = new(
            Set("run", "test", "start", "build", "lint", "typecheck", "info", "why",
                "list", "versions", "cache"),
            Set(),
            Set("add", "remove", "install", "upgrade", "publish", "dlx", "link", "init")),
        ["cargo"] = new(
            Set("build", "check", "test", "clippy", "fmt", "doc", "run", "bench", "tree",
                "metadata", "locate-project", "verify-project", "version"),
            Set(),
            Set("install", "uninstall", "publish", "add", "remove", "update", "fix", "new")),
        ["go"] = new(
            Set("build", "test", "vet", "fmt", "list", "version", "env"),
            Set(),
            Set("install", "get", "clean", "run", "work", "generate")),
        ["dotnet"] = new(
            Set("build", "test", "run", "publish", "restore", "clean", "format",
                "msbuild", "pack", "list", "new", "sln", "watch"),
            Set(),
            Set("tool", "add", "remove", "workload", "install", "update")),
        ["pip"] = new(Set("list", "show", "freeze", "check", "download"), Set(),
            Set("install", "uninstall", "upgrade", "cache")),
        ["pip3"] = new(Set("list", "show", "freeze", "check", "download"), Set(),
            Set("install", "uninstall", "upgrade", "cache")),
        ["uv"] = new(Set("run", "sync", "lock", "tree", "pip-list", "pip-freeze", "version"), Set(),
            Set("pip", "add", "remove", "tool", "publish", "init", "venv")),
        ["poetry"] = new(Set("run", "show", "check", "lock", "env", "version", "build"), Set(),
            Set("add", "remove", "install", "update", "publish", "new")),
        ["conda"] = new(Set("list", "info", "search"), Set(),
            Set("install", "remove", "create", "update", "env")),
    };

    // 明确会改文件的命令：要求所有目标都落在工作区内
    private static readonly HashSet<string> WriteCmds = Set(
        "mkdir", "md", "touch", "cp", "copy", "xcopy", "robocopy", "mv", "move",
        "ln", "patch", "tar", "unzip", "zip");

    // 明确的破坏性命令：永远问（哪怕在工作区内）
    private static readonly HashSet<string> Destructive = Set(
        "rm", "del", "erase", "rmdir", "rd", "truncate", "shred", "srm");

    // 凭据 / 系统目录：读写都问
    private static readonly Regex[] Sensitive =
    {
        R(@"[/\\]\.ssh([/\\]|$)"),
        R(@"[/\\]\.aws([/\\]|$)"),
        R(@"[/\\]\.gnupg([/\\]|$)"),
        R(@"[/\\]\.kube([/\\]|$)"),
        R(@"[/\\]\.docker[/\\]config\.json$"),
        R(@"[/\\]\.config[/\\]gcloud"),
        R(@"[/\\]\.netrc$"),
        R(@"[/\\]\.git-credentials$"),
        R(@"[/\\]\.npmrc$"),
        R(@"[/\\]\.pypirc$"),
        R(@"[/\\]\.env(\.|$)"),
        R(@"id_rsa|id_ed25519|\.pem$|\.pfx$|\.p12$"),
        R(@"[/\\]appdata[/\\]"),
        R(@"[/\\]windows([/\\]|$)"),
        R(@"[/\\]program files"),
        R(@"[/\\]\.workbuddy[/\\]settings(\.local)?\.json$"),
        R(@"credentials\.json$"),
    };

    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ------------------------------------------------------------ 路径工具

    private static string Norm(string p) => p.Replace('\\', '/');

    private static bool IsSensitive(string text)
    {
        var p = Norm(text);
        return Sensitive.Any(rx => rx.IsMatch(p));
    }

    private static string? Resolve(string path, string cwd)
    {
        try
        {
            var raw = path.Trim().Trim('"').Trim('\'');
            if (raw.Length == 0) return null;
            if (raw == "~" || raw.StartsWith("~/") || raw.StartsWith("~\\"))
            {
                raw = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + raw[1..];
            }
            if (!Path.IsPathRooted(raw) && cwd.Length > 0) raw = Path.Combine(cwd, raw);
            return Path.GetFullPath(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>path 是否落在 root（工作区根）里；相对路径按 base 展开。</summary>
    private static bool Inside(string path, string baseDir, string root = "")
    {
        if (root.Length == 0) root = baseDir;
        var target = Resolve(path, baseDir);
        var rootPath = Resolve(root, root);
        if (target is null || rootPath is null) return false;

        target = target.TrimEnd('\\', '/');
        rootPath = rootPath.TrimEnd('\\', '/');
        if (string.Equals(target, rootPath, StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(rootPath + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePath(string tok)
    {
        var t = tok.Trim('"').Trim('\'');
        if (t.Length == 0 || t.StartsWith('-')) return false;
        if (t is "." or "..") return true;
        return t.Contains('/') || t.Contains('\\') || Regex.IsMatch(t, @"^[a-zA-Z]:") || t.StartsWith('~');
    }

    // ------------------------------------------------------------ 词法

    /// <summary>按 && || ; | & 换行切分，但不切引号里的内容，也不切 >> 重定向。</summary>
    public static List<string> SplitSegments(string cmd)
    {
        var segs = new List<string>();
        var buf = new StringBuilder();
        char quote = '\0';
        var i = 0;

        while (i < cmd.Length)
        {
            var ch = cmd[i];

            if (quote != '\0')
            {
                buf.Append(ch);
                if (ch == '\\' && quote == '"' && i + 1 < cmd.Length)
                {
                    buf.Append(cmd[i + 1]);
                    i += 2;
                    continue;
                }
                if (ch == quote) quote = '\0';
                i++;
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                buf.Append(ch);
                i++;
                continue;
            }

            if (i + 1 < cmd.Length && cmd[i] == '>' && cmd[i + 1] == '>')
            {
                buf.Append(">>");
                i += 2;
                continue;
            }

            if (i + 1 < cmd.Length && ((cmd[i] == '&' && cmd[i + 1] == '&') || (cmd[i] == '|' && cmd[i + 1] == '|')))
            {
                segs.Add(buf.ToString());
                buf.Clear();
                i += 2;
                continue;
            }

            if (ch is ';' or '|' or '\n' or '&')
            {
                segs.Add(buf.ToString());
                buf.Clear();
                i++;
                continue;
            }

            buf.Append(ch);
            i++;
        }

        segs.Add(buf.ToString());
        return segs.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    /// <summary>类 POSIX 切词：引号成组、双引号内允许反斜杠转义。</summary>
    private static List<string> Tokens(string seg)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        var has = false;

        for (var i = 0; i < seg.Length; i++)
        {
            var c = seg[i];

            if (quote != '\0')
            {
                if (c == '\\' && quote == '"' && i + 1 < seg.Length)
                {
                    sb.Append(seg[i + 1]);
                    i++;
                    has = true;
                    continue;
                }
                if (c == quote)
                {
                    quote = '\0';
                    has = true;
                    continue;
                }
                sb.Append(c);
                has = true;
                continue;
            }

            if (c is '\'' or '"')
            {
                quote = c;
                has = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (has)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                    has = false;
                }
                continue;
            }

            sb.Append(c);
            has = true;
        }

        if (has) list.Add(sb.ToString());
        return list;
    }

    private static readonly Regex EnvAssign = new(@"^[A-Za-z_][A-Za-z0-9_]*=", RegexOptions.Compiled);

    private static (string Head, List<string> Args) Head(List<string> tokens)
    {
        var i = 0;
        while (i < tokens.Count && EnvAssign.IsMatch(tokens[i])) i++;
        if (i >= tokens.Count) return ("", new List<string>());

        var name = Norm(tokens[i]).Split('/').Last().ToLowerInvariant();
        name = Regex.Replace(name, @"\.(exe|cmd|bat|ps1)$", "");
        return (name, tokens.GetRange(i + 1, tokens.Count - i - 1));
    }

    /// <summary>找出输出重定向、-o、--output 之类的落盘目标。</summary>
    private static List<string> WriteTargets(List<string> args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var tok = args[i];
            if (tok is ">" or ">>" or "1>" or "2>" && i + 1 < args.Count)
            {
                list.Add(args[i + 1]);
            }
            else if (Regex.IsMatch(tok, @"^[12]?>>?") && tok.Length > 1)
            {
                list.Add(Regex.Replace(tok, @"^[12]?>>?", ""));
            }
            else if (tok is "-o" or "--output" or "--out" or "--outfile" or "--log-file" or "-O" && i + 1 < args.Count)
            {
                list.Add(args[i + 1]);
            }
            else if (tok.StartsWith("--output=", StringComparison.Ordinal) || tok.StartsWith("--out=", StringComparison.Ordinal))
            {
                list.Add(tok.Split('=', 2)[1]);
            }
        }

        return list.Where(t => t.Length > 0 && !t.StartsWith('&')).ToList();
    }

    // ------------------------------------------------------------ 单段判定

    private static Decision EvalSegment(string seg, string cwd, string root = "")
    {
        if (root.Length == 0) root = cwd;

        foreach (var (rx, reason) in Danger)
        {
            if (rx.IsMatch(seg)) return Ask(reason, High);
        }

        if (seg.StartsWith('(') || seg.StartsWith('{') || seg.StartsWith('`')) return Ask("嵌套子命令", Medium);

        var (head, args) = Head(Tokens(seg));
        if (head.Length == 0) return Silent("无副作用");

        if (Destructive.Contains(head))
        {
            var recursive = args.Any(a => a.StartsWith('-') && a.Contains('r'));
            return Ask($"删除操作（{head}）", recursive ? High : Medium);
        }

        foreach (var t in WriteTargets(args))
        {
            if (t.Equals("/dev/null", StringComparison.OrdinalIgnoreCase) || t.Equals("nul", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsSensitive(t)) return Ask("写凭据/系统目录", High);
            if (!Inside(t, cwd, root)) return Ask("写工作区外文件", Medium);
        }

        if (Subcommands.TryGetValue(head, out var table)) return EvalSubcommand(head, args, table, cwd, root);

        if (WriteCmds.Contains(head))
        {
            var paths = args.Where(a => !a.StartsWith('-')).ToList();
            if (paths.Count == 0) return Ask($"{head} 缺少明确目标", Medium);
            foreach (var p in paths)
            {
                if (IsSensitive(p)) return Ask("涉及凭据/系统目录", High);
                if (!Inside(p, cwd, root)) return Ask("写工作区外文件", Medium);
            }
            return Silent($"工作区内 {head}");
        }

        if (ReadOnly.Contains(head))
        {
            if (head == "find" && args.Any(a => a is "-delete" or "-exec" or "-execdir" or "-ok" or "-fprint"))
                return Ask("find 带删除/执行参数", High);

            if (head is "awk" or "gawk" && args.Any(a => a.Contains("system(")))
                return Ask("awk 内联执行", Medium);

            if (head is "python" or "python3" or "py" or "node" or "deno" or "bun")
                return EvalInterpreter(head, args, cwd, root);

            if (head is "sed" or "perl")
            {
                if (args.Any(a => a.StartsWith("-i", StringComparison.Ordinal) || a.StartsWith("--in-place", StringComparison.Ordinal)))
                {
                    foreach (var p in args.Where(a => !a.StartsWith('-') && LooksLikePath(a)))
                    {
                        if (IsSensitive(p)) return Ask("就地改写凭据/系统文件", High);
                        if (!Inside(p, cwd, root)) return Ask("就地改写工作区外文件", Medium);
                    }
                    return Silent("工作区内就地改写");
                }
                return Silent("sed 只读输出");
            }

            foreach (var p in args)
            {
                if (LooksLikePath(p) && IsSensitive(p)) return Ask("读取凭据目录", High);
            }
            return Silent("只读命令");
        }

        if (ReadOnlyPs.Contains(head))
        {
            foreach (var p in args)
            {
                if (LooksLikePath(p) && IsSensitive(p)) return Ask("读取凭据目录", High);
            }
            return Silent("只读 cmdlet");
        }

        foreach (var (rx, reason, level) in Explain)
        {
            if (rx.IsMatch(seg)) return Ask(reason, level);
        }

        return Ask($"未登记的 {head} 命令", Medium);
    }

    private static Decision EvalSubcommand(string head, List<string> args, SubTable table, string cwd, string root)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "";
        if (sub.Length == 0) return Silent("查看用法");

        if (table.Ask.Contains(sub))
            return Ask($"{head} {sub} 会改变外部状态", head == "git" && sub == "push" ? High : Medium);

        if (table.WriteOk.Contains(sub)) return Silent($"本地 {head} {sub}");

        if (table.Read.Contains(sub))
        {
            if (head is "npm" or "pnpm" or "yarn" && sub is "run" or "run-script") return Silent("运行项目脚本");
            if (head is "uv" or "poetry" && sub == "run") return Silent("运行项目脚本");

            foreach (var a in args)
            {
                if (LooksLikePath(a) && IsSensitive(a)) return Ask("涉及凭据/系统目录", High);
            }
            foreach (var t in WriteTargets(args))
            {
                if (t.Equals("/dev/null", StringComparison.OrdinalIgnoreCase) || t.Equals("nul", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Inside(t, cwd, root)) return Ask("写工作区外文件", Medium);
            }
            return Silent($"{head} {sub}");
        }

        return Ask($"未登记的 {head} {sub}", Medium);
    }

    /// <summary>解释器：能读到工作区内的脚本就算默认权限，内联代码不算。</summary>
    private static Decision EvalInterpreter(string head, List<string> args, string cwd, string root)
    {
        if (args.Count == 0) return Ask($"{head} 交互式执行", Medium);

        if (args.Any(a => a is "-c" or "-e" or "-p" or "--eval" or "--print" or "-")) return Ask("内联脚本执行", Medium);

        var mIdx = args.IndexOf("-m");
        if (mIdx >= 0)
        {
            var module = mIdx + 1 < args.Count ? args[mIdx + 1] : "";
            if (module is "pytest" or "unittest" or "compileall" or "json.tool") return Silent($"运行 {module}");
            return Ask($"{head} -m {module}", Medium);
        }

        var scripts = args.Where(a => !a.StartsWith('-')).ToList();
        if (scripts.Count == 0) return Ask($"{head} 无脚本参数", Medium);

        var script = scripts[0];
        if (IsSensitive(script)) return Ask("执行凭据目录下的脚本", High);
        if (!Inside(script, cwd, root)) return Ask("执行工作区外的脚本", Medium);
        return Silent("运行项目内脚本");
    }

    // ------------------------------------------------------------ 其它工具

    private static Decision EvalWrite(string tool, Dictionary<string, string> input, string cwd)
    {
        var path = "";
        foreach (var key in new[] { "file_path", "path", "notebook_path", "target_file" })
        {
            if (input.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                path = v.Trim();
                break;
            }
        }

        if (path.Length == 0) return Ask("没有明确目标路径", Medium);
        if (IsSensitive(path)) return Ask("写凭据/系统配置", High);
        if (Inside(path, cwd)) return Silent("工作区内写入");
        return Ask("写工作区外文件", Medium);
    }

    private static Decision EvalRead(Dictionary<string, string> input)
    {
        foreach (var key in new[] { "file_path", "path", "notebook_path" })
        {
            if (input.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                if (IsSensitive(v.Trim())) return Ask("读取凭据目录", High);
                break;
            }
        }
        return Silent("读取文件");
    }

    // ------------------------------------------------------------ 本地覆盖

    private sealed class Overrides
    {
        public List<string> AlwaysAsk { get; set; } = new();
        public List<string> AlwaysSilent { get; set; } = new();
        public string SilentEmits { get; set; } = "allow";
    }

    private static Overrides LoadOverrides()
    {
        var result = new Overrides();
        try
        {
            var path = Path.Combine(AppPaths.Home, "policy.local.json");
            if (!File.Exists(path)) return result;

            var text = File.ReadAllText(path, Encoding.UTF8);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("alwaysAsk", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
                result.AlwaysAsk = a.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();

            if (root.TryGetProperty("alwaysSilent", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.Array)
                result.AlwaysSilent = s.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList();

            if (root.TryGetProperty("silentEmits", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String)
                result.SilentEmits = m.GetString() ?? "allow";
        }
        catch
        {
            // 配置坏了就当没有，绝不能因为读配置失败而卡住工具调用
        }
        return result;
    }

    private static readonly Overrides Local = LoadOverrides();

    private static bool OverrideHit(List<string> patterns, string text)
    {
        foreach (var p in patterns)
        {
            try
            {
                if (Regex.IsMatch(text, p, RegexOptions.IgnoreCase)) return true;
            }
            catch
            {
                // 单条正则写错不影响其它规则
            }
        }
        return false;
    }

    /// <summary>静默调用怎么交给宿主：allow = 钩子直接放行；pass = 交回宿主原生流程。</summary>
    public static string SilentEmits => Local.SilentEmits.Equals("pass", StringComparison.OrdinalIgnoreCase) ? "pass" : "allow";

    // ------------------------------------------------------------ 入口

    public static Decision Evaluate(string tool, Dictionary<string, string> input, string cwd)
    {
        if (Environment.GetEnvironmentVariable("AUTH_ISLAND_GATE")?.ToLowerInvariant() is "off" or "0" or "false")
            return Ask("闸门已关闭（全量送胶囊）", Low);

        var name = (tool ?? "").Trim();
        var lowered = name.ToLowerInvariant();

        if (lowered is "write" or "edit" or "multiedit" or "notebookedit" or "str_replace_editor" or "apply_patch")
            return EvalWrite(name, input, cwd);

        if (lowered == "read") return EvalRead(input);

        if (lowered is "glob" or "grep" or "ls" or "list_files" or "websearch" or "webfetch" or "todoread" or "todowrite")
            return Silent("只读工具");

        var command = "";
        foreach (var key in new[] { "command", "cmd", "script" })
        {
            if (input.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                command = v.Trim();
                break;
            }
        }

        if (command.Length == 0) return Ask($"{name switch { "" => "未知工具", _ => name }} 无法归类", Medium);

        if (OverrideHit(Local.AlwaysAsk, command)) return Ask("命中本地排除规则", High);
        if (OverrideHit(Local.AlwaysSilent, command)) return Silent("命中本地放行规则");

        if (command.Contains("$(") || command.Contains('`')) return Ask("命令替换（内部不可见）", Medium);
        if (command.Contains("<<")) return Ask("内联 heredoc 脚本", Medium);

        foreach (var (rx, reason) in DangerWhole)
        {
            if (rx.IsMatch(command)) return Ask(reason, High);
        }

        var segments = SplitSegments(command);
        if (segments.Count == 0) return Ask("无法解析命令", Medium);

        var root = cwd;   // 工作区根：写操作必须落在这里面
        var baseDir = cwd; // 当前目录：受 cd 影响，用来展开相对路径
        var last = Silent("只读命令");

        foreach (var seg in segments)
        {
            var (head, args) = Head(Tokens(seg));

            // cd 只是改变上下文，本身没有副作用；但它影响后面几段的路径判定，
            // 所以顺着链改 base —— 否则 `cd C:/other && echo x > y` 会被误判成工作区内写入。
            if (head is "cd" or "pushd" or "chdir")
            {
                var target = args.Count > 0 ? args[0] : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                baseDir = Resolve(target, baseDir) ?? baseDir;
                last = Silent("切换目录");
                continue;
            }

            if (head is "popd" or "exit" or "true" or ":")
            {
                last = Silent("无副作用");
                continue;
            }

            var result = EvalSegment(seg, baseDir, root);
            if (result.Action == GateAction.Ask) return result;
            last = result;
        }

        return Silent(segments.Count == 1 ? last.Reason : $"全部在默认权限内（{last.Reason}）");
    }
}
