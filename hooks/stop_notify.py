#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
WorkBuddy Stop hook —— 任务跑完后告诉胶囊"完成了"，由胶囊弹出通知。

三个硬约束：
  1. 永远 exit 0、永远不输出任何内容。Stop hook 的 stdout 会被宿主当作
     "是否要阻止 agent 停止"的指令，多打一行字就可能改变 agent 行为。
  2. 超时必须短（默认 1.5s）。这是在一轮对话结束时同步执行的，
     胶囊没开也不能让用户等。
  3. 任何异常都静默吞掉 —— 通知丢一条无所谓，卡住 agent 不行。

配置见 settings.snippet.json 的 Stop 段。
"""

import json
import os
import re
import socket
import sys
import urllib.request
from datetime import datetime
from pathlib import Path

_LOG = Path(__file__).resolve().parent / "_hook.log"


def trace(message: str) -> None:
    """调试用：确认宿主是否真的调用了 Stop 钩子。"""
    try:
        with _LOG.open("a", encoding="utf-8") as f:
            f.write(f"{datetime.now():%Y-%m-%d %H:%M:%S} [STOP] {message}\n")
    except Exception:
        pass

PORT = int(os.environ.get("AUTH_ISLAND_PORT", "18765"))
TIMEOUT = float(os.environ.get("AUTH_ISLAND_NOTIFY_TIMEOUT", "1.5"))
MAX_LEN = int(os.environ.get("AUTH_ISLAND_NOTIFY_CHARS", "90"))

# 回环地址绕过系统代理，理由同 island_core.py：本机设了 HTTP_PROXY 时，
# urllib 会把 127.0.0.1 的请求也交给代理，白等一两秒甚至直接失败。
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

PROBE_TIMEOUT = float(os.environ.get("AUTH_ISLAND_PROBE_SEC", "0.35"))


def is_alive() -> bool:
    """先探一下端口：胶囊没开就立刻返回，别让每轮对话结束都多等 1.5 秒。"""
    try:
        with socket.create_connection(("127.0.0.1", PORT), timeout=PROBE_TIMEOUT):
            return True
    except OSError:
        return False


def squeeze(text) -> str:
    """把最后一条回复压成一行摘要：去代码块、去 Markdown 记号、折叠空白。"""
    if not isinstance(text, str) or not text.strip():
        return ""

    t = re.sub(r"```.*?```", " ", text, flags=re.S)   # 代码块整段丢掉
    t = re.sub(r"!\[[^\]]*\]\([^)]*\)", " ", t)        # 图片
    t = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", t)     # 链接只留文字
    t = re.sub(r"[*_`#>|~]", "", t)                    # 行内记号
    t = re.sub(r"\s+", " ", t).strip()

    return t[:MAX_LEN] + "…" if len(t) > MAX_LEN else t


def main() -> int:
    trace("invoked")
    try:
        raw = sys.stdin.read()
        payload = json.loads(raw) if raw.strip() else {}
    except Exception:
        trace("payload parse failed")
        return 0

    if not isinstance(payload, dict):
        return 0

    trace(f"payload keys={list(payload.keys())} session={payload.get('session_id','')}")

    if not is_alive():
        trace("capsule not reachable -> skip")
        return 0

    body = {
        "sessionId": payload.get("session_id") or payload.get("sessionId") or "",
        "summary": squeeze(payload.get("last_assistant_message") or payload.get("summary")),
        "cwd": payload.get("cwd") or "",
    }

    try:
        request = urllib.request.Request(
            f"http://127.0.0.1:{PORT}/notify",
            data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
            headers={"Content-Type": "application/json; charset=utf-8"},
            method="POST",
        )
        with _OPENER.open(request, timeout=TIMEOUT) as resp:
            resp.read()
    except Exception:
        # 胶囊没开 / 超时 / 端口不通：静默放弃
        pass

    return 0


if __name__ == "__main__":
    sys.exit(main())
