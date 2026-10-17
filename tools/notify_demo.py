#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""手动推一条「任务完成」通知，单测完成卡的展示与点击跳转。"""

import json
import os
import sys
import urllib.request

PORT = int(os.environ.get("AUTH_ISLAND_PORT", "18765"))
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

body = {
    "sessionId": sys.argv[1] if len(sys.argv) > 1 else "",
    "summary": sys.argv[2] if len(sys.argv) > 2 else "已把三分支合并，测试全绿",
    "cwd": os.getcwd(),
}

req = urllib.request.Request(
    f"http://127.0.0.1:{PORT}/notify",
    data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
    headers={"Content-Type": "application/json; charset=utf-8"},
    method="POST",
)

with OPENER.open(req, timeout=5) as resp:
    print(resp.read().decode("utf-8") or "(empty)", "->", body["summary"])
