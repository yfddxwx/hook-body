#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""手动触发一次授权请求，用来单独验证胶囊动效。"""

import json
import sys
import urllib.request

PORT = 18765

payload = {
    "tool": sys.argv[1] if len(sys.argv) > 1 else "Bash",
    "detail": sys.argv[2] if len(sys.argv) > 2 else "npm install -g @tencent/wecom-cli",
    "cwd": "D:/workbuuddy/heath",
    "risk": sys.argv[3] if len(sys.argv) > 3 else "medium",
    "reason": sys.argv[4] if len(sys.argv) > 4 else "安装/变更依赖",
    "timeoutMs": 60000,
}

body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
req = urllib.request.Request(
    f"http://127.0.0.1:{PORT}/authorize",
    data=body,
    headers={"Content-Type": "application/json; charset=utf-8"},
    method="POST",
)

print(f"→ {payload['tool']}: {payload['detail']}")
print("等待你在胶囊上点击…")

try:
    _opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with _opener.open(req, timeout=90) as resp:
        print("←", resp.read().decode("utf-8"))
except Exception as ex:
    print("连不上胶囊：", ex)
