#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
AuthIsland 客户端核心：把一次工具调用送去胶囊，等用户点。

三个宿主（WorkBuddy / Codex / Antigravity）的钩子协议各不相同，
但都收敛到同一个动作：拿 tool + detail 去问，回来一个 allow / deny / pass。
"""

import json
import os
import socket
import sys
import urllib.request

PORT = int(os.environ.get("AUTH_ISLAND_PORT", "18765"))
DEFAULT_TIMEOUT_MS = int(os.environ.get("AUTH_ISLAND_TIMEOUT_MS", "60000"))

# 本机回环地址绝不该经过系统代理。
# 很多环境会设 HTTP_PROXY/HTTPS_PROXY（本机就是 127.0.0.1:54701），
# urllib 默认读这些变量，会把发往 127.0.0.1:18765 的请求也丢给代理：
# 轻则每次工具调用白等 2 秒（代理探测超时），重则代理回 502 让钩子整个失效。
# 这里显式清空代理，直连本机。
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

PROBE_TIMEOUT = float(os.environ.get("AUTH_ISLAND_PROBE_SEC", "0.35"))


def is_alive() -> bool:
    """
    胶囊是否在监听。探测失败就当它没开、直接 pass。

    为什么不直接发请求靠异常判断：Windows 上连一个没人监听的本地端口，
    要等约 2 秒才拿到 ConnectionRefused（SYN 被丢弃后重传），
    期间钩子一直挂着——胶囊没开时每次工具调用都白等两秒。
    正常监听的端口 connect 是毫秒级，所以 0.35 秒的预检足够，
    换来的最坏代价只是漏判（漏判 = pass = 交回宿主原生流程，是安全的）。
    """
    try:
        with socket.create_connection(("127.0.0.1", PORT), timeout=PROBE_TIMEOUT):
            return True
    except OSError:
        return False

_DETAIL_KEYS = ("command", "cmd", "file_path", "path", "url", "query", "pattern", "prompt")


def summarize(tool, tool_input) -> str:
    """把工具参数压成一行给人看的话。"""
    if not isinstance(tool_input, dict):
        return str(tool_input)[:400]

    for key in _DETAIL_KEYS:
        value = tool_input.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()[:400]

    try:
        return json.dumps(tool_input, ensure_ascii=False)[:400]
    except Exception:
        return str(tool_input)[:400]


def ask_island(tool: str, detail: str, cwd: str = "", timeout_ms: int = None,
               risk: str = "normal", reason: str = "") -> str:
    """
    返回 'allow' | 'deny' | 'pass'。

    'pass' 的语义是「我不决定」，由宿主退回自己的原生审批流程。
    任何异常（胶囊没开、端口不通、超时、返回垃圾）都归到 pass。

    risk / reason 是闸门的产物：告诉用户「为什么这次值得看一眼」，
    胶囊会把它渲染成标题旁的一枚风险标签。
    """
    timeout_ms = timeout_ms or DEFAULT_TIMEOUT_MS

    if not is_alive():
        return "pass"

    payload = {
        "id": f"{os.environ.get('AUTH_ISLAND_SESSION', 's')}-{os.getpid()}",
        "tool": tool or "unknown",
        "detail": detail or "",
        "cwd": cwd or "",
        "timeoutMs": timeout_ms,
        "risk": risk or "normal",
        "reason": reason or "",
    }

    try:
        request = urllib.request.Request(
            f"http://127.0.0.1:{PORT}/authorize",
            data=json.dumps(payload, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json; charset=utf-8"},
            method="POST",
        )
        with _OPENER.open(request, timeout=timeout_ms / 1000.0 + 5) as resp:
            data = json.loads(resp.read().decode("utf-8"))
            decision = data.get("decision", "pass")
    except Exception:
        return "pass"

    return decision if decision in ("allow", "deny") else "pass"


def read_payload() -> dict:
    """读 stdin 的 JSON；任何异常返回空 dict，由调用方决定降级策略。"""
    try:
        raw = sys.stdin.read()
        payload = json.loads(raw) if raw.strip() else {}
        return payload if isinstance(payload, dict) else {}
    except Exception:
        return {}
