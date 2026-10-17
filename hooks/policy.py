#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AuthIsland 的「默认权限闸门」。

PreToolUse 是**每一次**工具调用都会触发的。如果把每次调用都送到胶囊，
用户连看一眼 `ls` 都要点一下，胶囊就从「关键拦截」退化成噪声，
最后的结果一定是用户把它关掉。

所以真正该问的问题不是「这次调用是什么」，而是：
    这次调用，有没有超出宿主的默认权限？

没超出的：静默放行，胶囊连亮都不亮。
超出了：才送到胶囊，并且带上「为什么找你」——让用户知道该看什么。

判定原则（宁严勿松）：
  · 白名单是显式的。不认识的命令一律按「超权」处理。
  · 只读命令才可能静默；写操作要么落在工作区内，要么问你。
  · 删除、提权、系统配置、网络安装、凭据目录 —— 一律问。

可调项：同目录下 policy.local.json
  {"alwaysAsk": ["^docker\\s+"], "alwaysSilent": ["^git\\s+status\\b"]}
环境变量 AUTH_ISLAND_GATE=off 可整体关闭闸门（退回「每次调用都问」）。
"""

import json
import os
import re
import shlex
from pathlib import Path
from typing import NamedTuple

SILENT = "silent"   # 默认权限内：不打扰
ASK = "ask"         # 超出默认权限：交给胶囊

LOW, MEDIUM, HIGH = "low", "medium", "high"


class Decision(NamedTuple):
    action: str
    risk: str
    reason: str


def silent(reason="默认权限内", risk=LOW):
    return Decision(SILENT, risk, reason)


def ask(reason, risk=MEDIUM):
    return Decision(ASK, risk, reason)


# ---------------------------------------------------------------- 高危模式
# 命中即 ASK + HIGH。这些不是「可能危险」，是「必须看一眼」。
_DANGER = [
    (r"\brm\s+(-[a-z]+\s+)*-[a-z]*(r|f)[a-z]*", "强制/递归删除"),
    (r"\brm\s+(-[a-z]+\s+)*/(\s|$)", "删除根目录"),
    (r"\brd\s+/s\b|\brmdir\s+/s\b|\brmdir\s+--recursive", "递归删除目录"),
    (r"\bdel\s+/[sq]\b", "强制删除文件"),
    (r"\bformat\s+[a-z]:", "格式化磁盘"),
    (r"\bdiskpart\b|\bmkfs(\.|\s)", "分区/格式化"),
    (r"\bcipher\s+/w\b|\bvssadmin\s+delete\b|\bwbadmin\b", "擦除数据/备份"),
    (r"\bbcdedit\b|\breg\s+(add|delete|import)\b|\bsc\s+(create|delete|config|stop)\b", "修改系统配置"),
    (r"\bshutdown\b|\brestart-computer\b|\bstop-computer\b|\bpoweroff\b", "关机/重启"),
    (r"\btaskkill\b[^\n]*/f\b|\bkill\s+-9\b|\bstop-process\b", "强制结束进程"),
    (r"(^|\s)(sudo|doas|runas|gsudo)\s", "提权执行"),
    (r"\bstart-process\b[^\n]*-verb\s+runas", "提权执行"),
    (r"\b(curl|wget|iwr|invoke-webrequest|invoke-restmethod)\b[^\n|]*\|\s*(sh|bash|zsh|python|python3|node|powershell|pwsh|perl)\b", "下载即执行"),
    (r"\biex\b|\binvoke-expression\b|-enc(odedcommand)?\s+[a-z0-9+/=]{16,}|frombase64string", "隐藏脚本执行"),
    (r"\btakeown\b|\bicacls\b|\bcacls\b", "修改文件权限/属主"),
    (r"\bnet\s+(user|localgroup)\b|\bwhoami\s+/priv", "账户/权限操作"),
    (r">\s*(/dev/(sd|nvme|disk)|\\\\\.\\physicaldrive)", "直接写磁盘"),
    (r"\bgit\s+push\b", "推送远端仓库"),
    (r"\bgit\s+(reset\s+--hard|clean\s+-[a-z]*f|rebase|filter-branch)", "改写仓库历史"),
    (r"\b(npm|pnpm|yarn|bun)\s+(i|install|add|ci|remove|uninstall|link|publish)\b", "安装/变更依赖"),
    (r"\b(pip|pip3|uv)\s+(install|uninstall)\b", "安装 Python 包"),
    (r"\b(winget|choco|scoop)\s+(install|uninstall|upgrade)\b", "安装系统软件"),
    (r"\b(cargo|gem|go|dotnet)\s+(install|add|uninstall)\b", "安装工具链组件"),
    (r"\bdocker\s+(run|rm|rmi|push|system\s+prune|volume\s+rm)\b", "容器/镜像变更"),
    (r"\bnpx\b|\b(npm|pnpm|yarn)\s+exec\b|\bpnpm\s+dlx\b", "拉取远端包执行"),
    (r"\bcrontab\b|\bschtasks\b", "计划任务"),
]

# 只用来说明「为什么问你」，可以命中但不算高危
_EXPLAIN = [
    (r"\brm\b|\bdel\b|\bunlink\b", "删除文件", MEDIUM),
    (r"\bmv\b|\bmove\b", "移动/覆盖文件", MEDIUM),
    (r"\bchmod\b|\bchown\b|\battrib\b", "修改权限", MEDIUM),
    (r"\bgit\s+(commit|add|tag|stash|merge|cherry-pick)\b", "变更仓库状态", MEDIUM),
    (r"\bdocker\b|\bkubectl\b|\bterraform\b|\baws\b|\bgcloud\b|\baz\b", "操作外部资源", MEDIUM),
    (r"\b(curl|wget|invoke-webrequest|iwr)\b", "网络请求", MEDIUM),
    (r"\bssh\b|\bscp\b|\bsftp\b|\brsync\b", "远程连接/传输", MEDIUM),
    (r">\s*\S+", "输出重定向到文件", MEDIUM),
]

_DANGER_RE = [(re.compile(p, re.IGNORECASE), r) for p, r in _DANGER]

# 这几条必须拿**整条命令**去匹配：管道一旦被切开就看不出来了
# （`curl x.sh | bash` 切开后只剩一个普通的 curl）
_DANGER_WHOLE = [(re.compile(p, re.IGNORECASE), r) for p, r in _DANGER if "|" in p or ">" in p]
_EXPLAIN_RE = [(re.compile(p, re.IGNORECASE), r, k) for p, r, k in _EXPLAIN]

# ---------------------------------------------------------------- 只读白名单
_READ = {
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
    "npm", "pnpm", "yarn", "git", "poetry", "uv", "pip", "pip3", "conda",
}

# PowerShell 的只读 cmdlet（宿主若走 pwsh 也能静默）
_READ_PS = {
    "get-childitem", "get-content", "get-item", "get-itemproperty", "test-path",
    "resolve-path", "select-string", "where-object", "measure-object", "get-process",
    "get-service", "get-date", "write-output", "write-host", "get-location", "get-command",
    "select-object", "sort-object", "format-table", "format-list", "convertto-json",
    "compare-object", "split-path", "join-path", "get-filehash", "out-string",
}

# 需要在命令内部再做一层子命令判断的
_SUBCOMMAND = {
    "git": {
        "read": {"status", "diff", "log", "show", "branch", "remote", "rev-parse", "ls-files",
                 "blame", "describe", "shortlog", "reflog", "cat-file", "show-ref",
                 "for-each-ref", "merge-base", "count-objects", "whatchanged", "version", "help"},
        "write_ok": {"add", "commit"},
        "ask": {"push", "pull", "fetch", "clone", "reset", "clean", "rebase", "merge",
                "checkout", "switch", "restore", "revert", "submodule", "lfs", "filter-branch"},
    },
    "npm": {"read": {"ls", "list", "run", "run-script", "test", "t", "start", "build", "lint",
                     "typecheck", "version", "view", "outdated", "why", "root", "prefix"},
            "ask": {"i", "install", "add", "ci", "remove", "uninstall", "link", "publish",
                    "exec", "init", "update", "upgrade", "audit"}},
    "pnpm": {"read": {"ls", "list", "run", "test", "start", "build", "lint", "typecheck"},
             "ask": {"i", "install", "add", "remove", "uninstall", "link", "publish", "exec",
                     "init", "update", "upgrade", "dlx", "audit"}},
    "yarn": {"read": {"run", "test", "start", "build", "lint", "typecheck", "info", "why",
                      "list", "versions", "cache"},
             "ask": {"add", "remove", "install", "upgrade", "publish", "dlx", "link", "init"}},
    "cargo": {"read": {"build", "check", "test", "clippy", "fmt", "doc", "run", "bench", "tree",
                       "metadata", "locate-project", "verify-project", "version"},
              "ask": {"install", "uninstall", "publish", "add", "remove", "update", "fix", "new"}},
    "go": {"read": {"build", "test", "vet", "fmt", "list", "version", "env", "mod-tidy"},
           "ask": {"install", "get", "clean", "run", "work", "generate"}},
    "dotnet": {"read": {"build", "test", "run", "publish", "restore", "clean", "format",
                        "msbuild", "pack", "list", "nuget", "new", "sln", "watch"},
               "ask": {"tool", "add", "remove", "workload", "install", "update"}},
    "pip": {"read": {"list", "show", "freeze", "check", "download"},
            "ask": {"install", "uninstall", "upgrade", "cache"}},
    "pip3": {"read": {"list", "show", "freeze", "check", "download"},
             "ask": {"install", "uninstall", "upgrade", "cache"}},
    "uv": {"read": {"run", "sync", "lock", "tree", "pip-list", "pip-freeze", "version"},
           "ask": {"pip", "add", "remove", "tool", "publish", "init", "venv"}},
    "poetry": {"read": {"run", "show", "check", "lock", "env", "version", "build"},
               "ask": {"add", "remove", "install", "update", "publish", "new"}},
    "conda": {"read": {"list", "info", "search"},
              "ask": {"install", "remove", "create", "update", "env"}},
}

# 明确会改文件的命令：要求所有目标都落在工作区内，否则问
_WRITE = {
    "mkdir", "md", "touch", "cp", "copy", "xcopy", "robocopy", "mv", "move",
    "ln", "sed", "perl", "patch", "tar", "unzip", "zip",
}

# 明确的破坏性命令：永远问（哪怕在工作区内）
_DESTRUCTIVE = {"rm", "del", "erase", "rmdir", "rd", "truncate", "shred", "srm"}

# 凭据 / 系统目录：读写都问
_SENSITIVE = [re.compile(p, re.IGNORECASE) for p in (
    r"[/\\]\.ssh([/\\]|$)",
    r"[/\\]\.aws([/\\]|$)",
    r"[/\\]\.gnupg([/\\]|$)",
    r"[/\\]\.kube([/\\]|$)",
    r"[/\\]\.docker[/\\]config\.json$",
    r"[/\\]\.config[/\\]gcloud",
    r"[/\\]\.netrc$",
    r"[/\\]\.git-credentials$",
    r"[/\\]\.npmrc$",
    r"[/\\]\.pypirc$",
    r"[/\\]\.env(\.|$)",
    r"id_rsa|id_ed25519|\.pem$|\.pfx$|\.p12$",
    r"[/\\]appdata[/\\]",
    r"[/\\]windows([/\\]|$)",
    r"[/\\]program files",
    r"[/\\]\.workbuddy[/\\]settings(\.local)?\.json$",
    r"credentials\.json$",
)]


# ---------------------------------------------------------------- 工具函数

def _norm(path: str) -> str:
    return path.replace("\\", "/")


def _is_sensitive(text: str) -> bool:
    p = _norm(text)
    return any(rx.search(p) for rx in _SENSITIVE)


def _resolve(path: str, cwd: str) -> Path | None:
    try:
        raw = os.path.expanduser(path.strip().strip('"').strip("'"))
        p = Path(raw)
        if not p.is_absolute() and cwd:
            p = Path(cwd) / p
        return p.resolve()
    except Exception:
        return None


def _inside(path: str, base: str, root: str = "") -> bool:
    """path 是否落在 root（工作区根）里。相对路径按 base 展开。"""
    root = root or base
    target = _resolve(path, base)
    rootp = _resolve(root, root)
    if target is None or rootp is None:
        return False
    try:
        target.relative_to(rootp)
        return True
    except ValueError:
        return False


def _looks_like_path(tok: str) -> bool:
    t = tok.strip('"').strip("'")
    if not t or t.startswith("-"):
        return False
    if t in (".", ".."):
        return True
    return ("/" in t) or ("\\" in t) or bool(re.match(r"^[a-zA-Z]:", t)) or t.startswith("~")


def split_segments(cmd: str) -> list[str]:
    """按 && || ; | & 换行 切分，但不切引号里的内容，也不切 >> 重定向。"""
    segs, buf = [], []
    quote = ""
    i, n = 0, len(cmd)
    while i < n:
        ch = cmd[i]
        if quote:
            buf.append(ch)
            if ch == "\\" and quote == '"' and i + 1 < n:
                buf.append(cmd[i + 1])
                i += 2
                continue
            if ch == quote:
                quote = ""
            i += 1
            continue
        if ch in ("'", '"'):
            quote = ch
            buf.append(ch)
            i += 1
            continue
        if cmd.startswith(">>", i):
            buf.append(">>")
            i += 2
            continue
        if cmd.startswith("&&", i) or cmd.startswith("||", i):
            segs.append("".join(buf))
            buf = []
            i += 2
            continue
        if ch in ";|\n&":
            segs.append("".join(buf))
            buf = []
            i += 1
            continue
        buf.append(ch)
        i += 1
    segs.append("".join(buf))
    return [s.strip() for s in segs if s.strip()]


def _tokens(seg: str) -> list[str]:
    try:
        return shlex.split(seg, posix=True)
    except ValueError:
        # 引号没闭合之类：退化成简单切分，后面通常落不到白名单而被 ask
        return seg.split()


def _head(tokens: list[str]) -> tuple[str, list[str]]:
    """跳过前导环境变量赋值，返回 (命令名, 剩余参数)。"""
    i = 0
    while i < len(tokens) and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", tokens[i]):
        i += 1
    if i >= len(tokens):
        return "", []
    head = os.path.basename(_norm(tokens[i])).lower()
    head = re.sub(r"\.(exe|cmd|bat|ps1)$", "", head)
    return head, tokens[i + 1:]


def _write_targets(args: list[str]) -> list[str]:
    """找出输出重定向、-o、--output 之类的落盘目标。"""
    out = []
    for idx, tok in enumerate(args):
        if tok in (">", ">>", "1>", "2>") and idx + 1 < len(args):
            out.append(args[idx + 1])
        elif re.match(r"^[12]?>>?", tok) and len(tok) > 1:
            out.append(re.sub(r"^[12]?>>?", "", tok))
        elif tok in ("-o", "--output", "--out", "--outfile", "--log-file", "-O") and idx + 1 < len(args):
            out.append(args[idx + 1])
        elif tok.startswith("--output=") or tok.startswith("--out="):
            out.append(tok.split("=", 1)[1])
    return [t for t in out if t and not t.startswith("&")]


# ---------------------------------------------------------------- 单段判定

def _eval_segment(seg: str, cwd: str, root: str = "") -> Decision:
    root = root or cwd
    for rx, reason in _DANGER_RE:
        if rx.search(seg):
            return ask(reason, HIGH)

    if seg.startswith("(") or seg.startswith("{") or seg.startswith("`"):
        return ask("嵌套子命令", MEDIUM)

    tokens = _tokens(seg)
    head, args = _head(tokens)
    if not head:
        return silent("无副作用")

    if head in _DESTRUCTIVE:
        return ask(f"删除操作（{head}）", HIGH if any(a.startswith("-") and "r" in a for a in args) else MEDIUM)

    targets = _write_targets(args)
    for t in targets:
        if t.lower() in ("/dev/null", "nul"):
            continue
        if _is_sensitive(t):
            return ask("写凭据/系统目录", HIGH)
        if not _inside(t, cwd, root):
            return ask("写工作区外文件", MEDIUM)

    if head in _SUBCOMMAND:
        return _eval_subcommand(head, args, cwd, root)

    if head in _WRITE:
        paths = [a for a in args if not a.startswith("-")]
        if not paths:
            return ask(f"{head} 缺少明确目标", MEDIUM)
        for p in paths:
            if _is_sensitive(p):
                return ask("涉及凭据/系统目录", HIGH)
            if not _inside(p, cwd, root):
                return ask("写工作区外文件", MEDIUM)
        return silent(f"工作区内 {head}")

    if head in _READ:
        if head == "find" and any(a in ("-delete", "-exec", "-execdir", "-ok", "-fprint") for a in args):
            return ask("find 带删除/执行参数", HIGH)
        if head in ("awk", "gawk") and any("system(" in a for a in args):
            return ask("awk 内联执行", MEDIUM)
        if head in ("python", "python3", "py", "node", "deno", "bun"):
            return _eval_interpreter(head, args, cwd, root)
        if head in ("sed", "perl"):
            if any(a.startswith("-i") or a.startswith("--in-place") for a in args):
                for p in [a for a in args if not a.startswith("-") and _looks_like_path(a)]:
                    if _is_sensitive(p):
                        return ask("就地改写凭据/系统文件", HIGH)
                    if not _inside(p, cwd, root):
                        return ask("就地改写工作区外文件", MEDIUM)
                return silent("工作区内就地改写")
            return silent("sed 只读输出")
        for p in args:
            if _looks_like_path(p) and _is_sensitive(p):
                return ask("读取凭据目录", HIGH)
        return silent("只读命令")

    if head in _READ_PS:
        for p in args:
            if _looks_like_path(p) and _is_sensitive(p):
                return ask("读取凭据目录", HIGH)
        return silent("只读 cmdlet")

    for rx, reason, level in _EXPLAIN_RE:
        if rx.search(seg):
            return ask(reason, level)

    return ask(f"未登记的 {head} 命令", MEDIUM)


def _eval_subcommand(head: str, args: list[str], cwd: str, root: str = "") -> Decision:
    table = _SUBCOMMAND[head]
    sub = ""
    for a in args:
        if not a.startswith("-"):
            sub = a.lower()
            break

    if not sub:
        return silent("查看用法")

    if sub in table.get("ask", ()):
        return ask(f"{head} {sub} 会改变外部状态", HIGH if (head == "git" and sub == "push") else MEDIUM)

    if sub in table.get("write_ok", ()):
        return silent(f"本地 {head} {sub}")

    if sub in table.get("read", ()):
        if head in ("npm", "pnpm", "yarn") and sub in ("run", "run-script", "dlx"):
            return silent("运行项目脚本")
        if head in ("uv", "poetry") and sub == "run":
            return silent("运行项目脚本")
        for a in args:
            if _looks_like_path(a) and _is_sensitive(a):
                return ask("涉及凭据/系统目录", HIGH)
        for t in _write_targets(args):
            if t.lower() not in ("/dev/null", "nul") and not _inside(t, cwd, root):
                return ask("写工作区外文件", MEDIUM)
        return silent(f"{head} {sub}")

    return ask(f"未登记的 {head} {sub}", MEDIUM)


def _eval_interpreter(head: str, args: list[str], cwd: str, root: str = "") -> Decision:
    """解释器：能读到工作区内的脚本就算默认权限，内联代码不算。"""
    if not args:
        return ask(f"{head} 交互式执行", MEDIUM)

    if any(a in ("-c", "-e", "-p", "--eval", "--print", "-") for a in args):
        return ask("内联脚本执行", MEDIUM)

    if "-m" in args:
        idx = args.index("-m")
        module = args[idx + 1] if idx + 1 < len(args) else ""
        if module in ("pytest", "unittest", "compileall", "json.tool"):
            return silent(f"运行 {module}")
        return ask(f"{head} -m {module}", MEDIUM)

    scripts = [a for a in args if not a.startswith("-")]
    if not scripts:
        return ask(f"{head} 无脚本参数", MEDIUM)

    script = scripts[0]
    if _is_sensitive(script):
        return ask("执行凭据目录下的脚本", HIGH)
    if not _inside(script, cwd, root):
        return ask("执行工作区外的脚本", MEDIUM)
    return silent("运行项目内脚本")


# ---------------------------------------------------------------- 其它工具

def _eval_write(tool: str, tool_input: dict, cwd: str) -> Decision:
    path = ""
    for key in ("file_path", "path", "notebook_path", "target_file"):
        value = tool_input.get(key)
        if isinstance(value, str) and value.strip():
            path = value.strip()
            break

    if not path:
        return ask("没有明确目标路径", MEDIUM)
    if _is_sensitive(path):
        return ask("写凭据/系统配置", HIGH)
    if _inside(path, cwd):
        return silent("工作区内写入")
    return ask("写工作区外文件", MEDIUM)


def _eval_read(tool_input: dict) -> Decision:
    path = ""
    for key in ("file_path", "path", "notebook_path"):
        value = tool_input.get(key)
        if isinstance(value, str) and value.strip():
            path = value.strip()
            break
    if path and _is_sensitive(path):
        return ask("读取凭据目录", HIGH)
    return silent("读取文件")


# ---------------------------------------------------------------- 覆盖配置

def _load_overrides() -> dict:
    path = Path(__file__).resolve().parent / "policy.local.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except Exception:
        return {}


_OVERRIDES = _load_overrides()


def _override_hit(patterns, text) -> bool:
    if not isinstance(patterns, list):
        return False
    for p in patterns:
        try:
            if re.search(p, text, re.IGNORECASE):
                return True
        except re.error:
            continue
    return False


# ---------------------------------------------------------------- 入口

def evaluate(tool: str, tool_input: dict, cwd: str = "") -> Decision:
    """
    返回 Decision(action='silent'|'ask', risk='low'|'medium'|'high', reason='给用户看的一句话')。
    """
    if os.environ.get("AUTH_ISLAND_GATE", "").lower() in ("off", "0", "false"):
        return ask("闸门已关闭（全量送胶囊）", LOW)

    tool_input = tool_input if isinstance(tool_input, dict) else {}
    name = (tool or "").strip()
    lowered = name.lower()

    if lowered in ("write", "edit", "multiedit", "notebookedit", "str_replace_editor", "apply_patch"):
        return _eval_write(name, tool_input, cwd)

    if lowered == "read":
        return _eval_read(tool_input)

    if lowered in ("glob", "grep", "ls", "list_files", "websearch", "webfetch", "todoread", "todowrite"):
        return silent("只读工具")

    command = ""
    for key in ("command", "cmd", "script"):
        value = tool_input.get(key)
        if isinstance(value, str) and value.strip():
            command = value.strip()
            break

    if not command:
        # 不认识又没命令：交给人判断，但别当真高危
        return ask(f"{name or '未知工具'} 无法归类", MEDIUM)

    if _override_hit(_OVERRIDES.get("alwaysAsk"), command):
        return ask("命中本地排除规则", HIGH)
    if _override_hit(_OVERRIDES.get("alwaysSilent"), command):
        return silent("命中本地放行规则")

    if "$(" in command or "`" in command:
        return ask("命令替换（内部不可见）", MEDIUM)
    if "<<" in command:
        return ask("内联 heredoc 脚本", MEDIUM)

    for rx, reason in _DANGER_WHOLE:
        if rx.search(command):
            return ask(reason, HIGH)

    segments = split_segments(command)
    if not segments:
        return ask("无法解析命令", MEDIUM)

    root = cwd          # 工作区根：写操作必须落在这里面
    base = cwd          # 当前目录：受 cd 影响，用来展开相对路径

    last = silent("只读命令")
    for seg in segments:
        head, args = _head(_tokens(seg))

        # cd 只是改变上下文，本身没有副作用；但它会影响后面几段的路径判定，
        # 所以这里顺着链改 base —— 否则 `cd C:/other && echo x > y` 会被误判成工作区内写入。
        if head in ("cd", "pushd", "chdir"):
            target = args[0] if args else os.path.expanduser("~")
            base = str(_resolve(target, base) or base)
            last = silent("切换目录")
            continue
        if head in ("popd", "exit", "true", ":"):
            last = silent("无副作用")
            continue

        result = _eval_segment(seg, base, root)
        if result.action == ASK:
            return result
        last = result

    return silent(last.reason if len(segments) == 1 else f"全部在默认权限内（{last.reason}）")


def silent_emits() -> str:
    """
    静默调用怎么交给宿主：
      "allow"（默认）—— 钩子直接放行，宿主和自己都不会再弹任何东西。
      "pass"        —— 不表态，退回宿主原生流程（宿主仍可能自己弹一次）。
    想完全尊重宿主自己的权限设置就改成 "pass"。
    """
    mode = str(_OVERRIDES.get("silentEmits") or "allow").lower()
    return "pass" if mode == "pass" else "allow"
