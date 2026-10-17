#!/usr/bin/env python3
"""一次性验证编排：授权点击闭环 → 状态面板 → 暂停/恢复拦截。"""
import ctypes
import json
import subprocess
import sys
import threading
import time
import urllib.request

u = ctypes.windll.user32
u.SetProcessDPIAware()


def click(x, y):
    u.SetCursorPos(x, y)
    time.sleep(0.12)
    u.mouse_event(2, 0, 0, 0, 0)   # down
    time.sleep(0.04)
    u.mouse_event(4, 0, 0, 0, 0)   # up
    print(f"  click ({x},{y})")


def shot(name):
    subprocess.run([sys.executable, "tools/shot.py", name, "770", "0", "400", "200"],
                   cwd="D:/workbuuddy/heath/agent-auth-island", capture_output=True)
    print(f"  shot {name}")


def authorize(tool, detail, timeout_ms):
    body = json.dumps({"tool": tool, "detail": detail, "timeoutMs": timeout_ms}).encode()
    req = urllib.request.Request(
        "http://127.0.0.1:18765/authorize", data=body,
        headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout_ms / 1000 + 5) as r:
            return r.read().decode()
    except Exception as e:
        return f"ERR {e}"


print("[1] 发授权请求把胶囊从隐藏态叫回来")
res = {}
t = threading.Thread(target=lambda: res.update(r=authorize("Bash", "echo hello", 20000)), daemon=True)
t.start()
time.sleep(1.6)
shot("step1-alert.png")

print("[2] 点「允许」")
click(860, 160)
t.join(timeout=6)
print("  响应:", res.get("r"))
time.sleep(1.9)          # 结果态播完回落到待机

print("[3] 点胶囊打开状态面板")
click(960, 33)
time.sleep(1.0)
shot("step2-panel.png")

print("[4] 点「暂停拦截」，立刻发一个请求（应直接 pass，不弹卡）")
click(854, 128)
time.sleep(0.8)
shot("step3-paused.png")
print("  暂停后请求:", authorize("Bash", "echo should-pass", 5000))

print("[5] 点「恢复拦截」并关面板")
click(854, 128)
time.sleep(0.5)
click(960, 86)
time.sleep(0.6)
shot("step4-idle.png")
print("done")
