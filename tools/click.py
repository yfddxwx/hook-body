#!/usr/bin/env python3
"""模拟一次真实鼠标点击（移动 + 按下 + 抬起），用于自动化验证胶囊交互。"""
import ctypes
import sys
import time

u = ctypes.windll.user32
u.SetProcessDPIAware()

x, y = int(sys.argv[1]), int(sys.argv[2])

ctypes.windll.user32.SetCursorPos(x, y)
time.sleep(0.15)
u.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
time.sleep(0.05)
u.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
print(f"clicked ({x},{y})")
