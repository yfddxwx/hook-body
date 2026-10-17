#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Antigravity (2.0 / CLI / IDE) —— PreToolUse hook。

注意：Antigravity 是 **fail-closed**。钩子崩溃、超时、非零退出，
工具调用会被直接拦掉（和 Codex / WorkBuddy 的 fail-open 相反）。
所以这个脚本必须：
  1. 永远 exit 0
  2. 任何异常都输出 {"decision": "ask"} —— 让 Antigravity 弹它自己的审批卡

输出的决策词汇比另外两家更丰富：
  allow / deny / ask / force_ask / deny_unless_prior_grant
我们只用 allow / deny / ask 三个。

配置见 integrations/antigravity.hooks.json
（全局 ~/.gemini/config/hooks.json，CLI / IDE / App 共用）。
"""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from island_core import ask_island, read_payload, summarize  # noqa: E402
from policy import SILENT, evaluate  # noqa: E402


def extract(payload: dict):
    """Antigravity 各版本的 payload 字段不完全一致，这里做兼容。"""
    tool = payload.get("tool_name") or payload.get("toolName")

    tool_input = payload.get("tool_input") or payload.get("toolInput")

    call = payload.get("toolCall") or payload.get("tool_call") or {}
    if not tool and isinstance(call, dict):
        tool = call.get("name") or call.get("toolName")
        args = call.get("args") or {}
        if isinstance(args, dict):
            tool = tool or args.get("ToolName") or args.get("toolName")

    if not tool_input and isinstance(call, dict):
        tool_input = call.get("args") or {}

    cwd = payload.get("cwd") or payload.get("workspace") or ""
    return tool or "unknown", tool_input or {}, cwd


def emit(decision: str, reason: str) -> None:
    print(json.dumps({"decision": decision, "reason": reason}, ensure_ascii=False))


def main() -> int:
    try:
        payload = read_payload()

        # 空载荷 = 探测，不是真实调用：直接放行，别让 fail-closed 的宿主卡住
        if not payload:
            emit("allow", "AuthIsland：校验探测（无真实调用），直接放行")
            return 0

        tool, tool_input, cwd = extract(payload)

        # 和 WorkBuddy 一样：默认权限内的调用直接放行，不进胶囊
        gate = evaluate(tool, tool_input, cwd)
        if gate.action == SILENT:
            emit("allow", f"AuthIsland：默认权限内（{gate.reason}）")
            return 0

        decision = ask_island(tool, summarize(tool, tool_input), cwd,
                              risk=gate.risk, reason=gate.reason)

        if decision == "allow":
            emit("allow", "由 AuthIsland 授权胶囊允许")
        elif decision == "deny":
            emit("deny", "由 AuthIsland 授权胶囊拒绝")
        else:
            # 胶囊不在 / 超时：交回 Antigravity 原生审批
            emit("ask", "AuthIsland 不可用，转原生审批")
    except Exception:
        # fail-closed 的宿主：任何异常都必须给出一个安全且不阻断的答案
        try:
            emit("ask", "AuthIsland hook 异常，转原生审批")
        except Exception:
            pass

    return 0


if __name__ == "__main__":
    sys.exit(main())
