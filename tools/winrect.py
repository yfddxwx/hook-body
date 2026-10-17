#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印 AuthIsland 主窗口的物理像素矩形，给自动化点击定位用。"""
import ctypes
from ctypes import wintypes

u = ctypes.windll.user32
u.SetProcessDPIAware()

hwnd = u.FindWindowW(None, "AuthIsland")
if not hwnd:
    print("NOT_FOUND")
    raise SystemExit(1)

rect = wintypes.RECT()
u.GetWindowRect(hwnd, ctypes.byref(rect))
print(f"{rect.left} {rect.top} {rect.right} {rect.bottom}")
