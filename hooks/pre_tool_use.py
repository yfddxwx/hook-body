#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
WorkBuddy PreToolUse hook。

这个钩子对**每一次**工具调用都会触发，所以这里有两级：
  1. 闸门（policy）：这次调用有没有超出默认权限？
  2. 超了才送去胶囊（island_core），没超就直接放行，胶囊不亮。

输出格式（与 Claude Code 同源）：
  {"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "allow|deny", ...}}
不输出任何内容 = 交回 WorkBuddy 自己的权限流程（fail-open）。
"""

import json
import sys
from datetime import datetime
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from island_core import ask_island, read_payload, summarize  # noqa: E402
from policy import SILENT, evaluate, silent_emits  # noqa: E402

_LOG = Path(__file__).resolve().parent / "_hook.log"


def trace(message: str) -> None:
    """调试用：确认宿主到底有没有调用钩子、闸门怎么判的。写失败一律忽略。"""
    try:
        if _LOG.exists() and _LOG.stat().st_size > 256 * 1024:
            _LOG.unlink()
        with _LOG.open("a", encoding="utf-8") as f:
            f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S} {message}\n")
    except Exception:
        pass


def respond(decision: str, reason: str) -> None:
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": decision,
            "permissionDecisionReason": reason,
        }
    }, ensure_ascii=False))


def main() -> int:
    payload = read_payload()

    # 空载荷 = 宿主的「运行校验」探测（不是真实调用）。
    # 直接回 allow，让校验立刻通过；否则闸门会把它当未知调用弹卡挂 60 秒。
    if not payload:
        trace("empty payload (probe) -> allow")
        respond("allow", "AuthIsland：校验探测（无真实调用），直接放行")
        return 0

    tool = payload.get("tool_name") or payload.get("toolName") or "unknown"
    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    cwd = payload.get("cwd") or payload.get("workspace") or ""

    # 闸门：默认权限内的调用到此为止，不惊动胶囊
    gate = evaluate(tool, tool_input, cwd)
    trace(f"tool={tool} gate={gate.action}/{gate.risk}/{gate.reason}")

    if gate.action == SILENT:
        mode = silent_emits()
        if mode == "allow":
            respond("allow", f"AuthIsland：默认权限内（{gate.reason}）")
        return 0

    detail = summarize(tool, tool_input)
    trace(f"  detail={detail[:90]!r}")

    decision = ask_island(tool, detail, cwd, risk=gate.risk, reason=gate.reason)
    trace(f"  decision={decision}")

    if decision == "pass":
        return 0

    respond(decision, f"由 AuthIsland 授权胶囊处理：{gate.reason}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
