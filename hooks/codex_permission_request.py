#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Codex / ChatGPT 桌面版 Codex —— PermissionRequest hook。

为什么用 PermissionRequest 而不是 PreToolUse：
  Codex 的 PreToolUse 只保证支持 deny（allow 仅用于改写输入），
  而 PermissionRequest 才是在「Codex 正要弹审批」时触发，
  并且返回 allow 会直接跳过它自己的审批弹窗 —— 正好是我们要的效果。

输出：
  允许  {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}
  拒绝  {"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"deny","message":"..."}}}
  不决定 → 不输出，Codex 走原生审批（fail-open）。

配置见 integrations/codex.hooks.json，且需要 [features] codex_hooks = true。
"""

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from island_core import ask_island, read_payload, summarize  # noqa: E402


def main() -> int:
    payload = read_payload()

    tool = payload.get("tool_name") or payload.get("toolName") or "unknown"
    tool_input = payload.get("tool_input") or payload.get("toolInput") or {}
    cwd = payload.get("cwd") or ""

    # Codex 有时会给出人类可读的审批原因，优先展示它
    description = None
    if isinstance(tool_input, dict):
        description = tool_input.get("description")

    detail = description.strip() if isinstance(description, str) and description.strip() \
        else summarize(tool, tool_input)

    # 注意：这个钩子只在 Codex **正要弹审批**时才触发，本身就已经是「超权」，
    # 所以这里不做闸门判断，否则等于用我们的白名单去放宽 Codex 的策略。
    decision = ask_island(tool, detail, cwd, risk="medium", reason="Codex 请求审批")

    if decision == "pass":
        return 0

    if decision == "allow":
        out = {"decision": {"behavior": "allow"}}
    else:
        out = {"decision": {"behavior": "deny", "message": "由 AuthIsland 拒绝"}}

    print(json.dumps(
        {"hookSpecificOutput": {"hookEventName": "PermissionRequest", **out}},
        ensure_ascii=False,
    ))
    return 0


if __name__ == "__main__":
    sys.exit(main())
