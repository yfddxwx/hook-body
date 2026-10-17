# AuthIsland 交接文档

> **这份文档的目的**：让一个**全新的会话 / 全新的 agent**（没有任何上下文）在 5 分钟内接手这个项目。
> 读完它不需要再去翻历史对话。所有路径、参数、已完成/未完成状态都以此文为准。

- 最后更新：2026-10-17（第五轮：hooks 真机跑通，定位并修掉静默失败根因）
- 项目根目录：`D:\workbuuddy\heath\agent-auth-island\`
- 当前状态：**已完整跑通**。hooks 合并在真实 `~/.workbuddy/settings.json`，真机实测：工具调用 → 弹卡 → 点击 → 回传 `allow` → 命令执行。已实现授权胶囊 + 毛玻璃 + 无事件退场 + 状态面板/托盘 + 断连感知 + 任务完成通知（点击直达会话）。Codex / Antigravity 适配器已写好但未在真机验证（本机未装这两个软件）
- **根因备忘（最重要的一条）**：hook 命令原来写的是裸 `python`，钩子进程的 PATH 会先命中
  `WindowsApps\python.exe`（微软商店存根）——它不报错、不输出、立刻退出，表现就是"钩子完全没反应，
  且没有任何日志"。改成 python 绝对路径后立刻正常。**任何"hooks 好像没生效"的问题，先看这条。**
- **配置备份**：`%USERPROFILE%\.workbuddy\settings.json.bak-20261017-141313`

---

## 一、30 秒速览

**这是什么**：一个 Windows 原生桌面浮层（WPF / .NET 8），常驻屏幕顶部、形如苹果灵动岛（黑色胶囊）。当 AI Agent 要执行需要授权的操作时，胶囊会"生长"成一张卡片，显示**哪个工具、要执行什么命令**，用户点「允许 / 拒绝」，决定直接回传给 Agent。

**当前形态（第七轮之后）**：**一个 exe 就是一切**——双击 = 安装向导；`--hook xxx` = 钩子（不加载 WPF）；`--install` / `--uninstall` = 静默装/卸。授权判定（闸门）已从 Python 移植成 C#（`Cli/Gate.cs`），用户机器**零依赖**（不需要 Python/Node）。分发物：`release/AuthIsland.exe`（69MB 单文件自包含）。

**为什么做**：Agent 的授权弹窗通常在应用内部，容易错过、也容易被"确认疲劳"糊弄。胶囊把授权提到屏幕最顶层，且强制展示具体命令内容。

**核心链路**：

```
Agent 要执行工具
   → 宿主 Hook（同一 exe 的 --hook 模式，几十毫秒）
   → POST 127.0.0.1:18765/authorize {tool, detail, risk, reason}
   → 连接挂起，胶囊形变展开
   → 用户点「允许」
   → hook 收到 {"decision":"allow"}
   → 按宿主协议输出决策，Agent 继续执行
```

**一句话记住设计原则**：胶囊是**可选的加速器，不是单点依赖**。它没开 / 崩了 / 超时，Agent 必须能退回自己的原生审批流程（fail-open）。唯一例外是 Antigravity——它是 fail-closed，适配器必须永远 `exit 0` 并兜底输出 `ask`。

---

## 二、机器环境（已确认，别重复探测）

| 项 | 值 |
|---|---|
| 系统 | Windows 11 25H2（build 26200），支持 Mica / Acrylic |
| .NET SDK | **系统未安装**，用便携版：`D:\workbuuddy\heath\.tools\dotnet\dotnet.exe`（未加 PATH） |
| .NET Framework | 4.8 在位（但本工程用的是 .NET 8，不依赖它） |
| Visual Studio | 未安装，也不需要（纯 `dotnet build` 即可） |
| Python | `python` → 3.13.14 可用（hook 脚本依赖它，必须留在 PATH） |
| 开发环境安装方式 | 官方 `dotnet-install.ps1` 会卡住，最终是**直连下载 zip 解压**：`https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.zip` |

**当前未安装的软件**（所以相关接入未经验证）：ChatGPT 桌面版 / Codex CLI、Google Antigravity。

---

## 三、已完成 vs 未完成

### 已验证 ✅

| 项 | 证据 |
|---|---|
| 编译通过 | 0 警告 0 错误（`dotnet build -c Release`） |
| 待机胶囊渲染 | 截图 `shot-idle.png`，顶部黑色胶囊显示正常 |
| 自包含版可独立运行 | 截图 `shot-publish.png`、`shot-final-idle.png` |
| 超时 fail-open | 60s 无人点击 → hook 收到 `{"decision":"pass"}` → 正确交回原生流程 |
| 三个适配器输出协议 | 假 payload 实测：WorkBuddy 静默降级、Codex 输出 `decision.behavior`、Antigravity 输出 `decision` |
| 端到端点击闭环 | 用户在胶囊上真实点击「允许」→ hook 收到 allow（`tools/choreo.py` 可复现） |
| **点击闭环截图** | `shot-glass-alert.png`（授权卡） |
| **毛玻璃质感** | 双层磨砂颗粒 + 雾面渐变 + 边缘高光 + 独立投影层，见 `shot-glass-alert.png` / `shot-glass-idle-zoom.png` |
| **无事件三档退场** | 15s → 休眠小点（`shot-dormant.png`）；3min → 整颗隐藏（`shot-hidden2.png`，`IsWindowVisible=0` 已确认）；请求自动唤醒 |
| **状态面板** | 点胶囊打开：服务地址、最近请求时间、暂停/隐藏/退出（`shot-panel.png`） |
| **暂停拦截** | 面板/托盘/右键三处开关；暂停后 `/authorize` 立即返回 `pass`（实测） |
| **断连感知** | 客户端进程被杀后 ~1s 卡片自动收起（socket EOF → watchdog） |
| **任务完成通知** | 模拟 Stop payload → 完成卡弹出（`shot-done.png`），6s 自动收，hover 暂停计时 |
| **点击直达会话** | 用当前会话真实 session_id 实测：点卡片 → `workbuddy://chat/<id>` → WorkBuddy 切到该任务（`shot-after-click.png`） |

### 未验证 / 待办 ⚠️

1. **hooks 在真实 WorkBuddy 进程里已跑通** ✅ —— 2026-10-17 14:33 实测：`_hook.log` 记录到
   `tool_name=Bash` 的真实 payload，胶囊弹卡，点击后 hook 输出 `permissionDecision: allow`。
   payload 实际字段：`session_id / transcript_path / cwd / hook_event_name / tool_name / tool_input /
   call_id / tool_use_id / agent_type / permission_mode / client / version / generation_id / model`。
2. **ChatGPT 桌面版是否读取用户级 `~/.codex/hooks.json`**——GUI 可能要求单独信任。这是目前最大不确定项。
3. **Antigravity payload 字段形态**——各版本可能是 `tool_name` 或 `toolCall.args.ToolName`，适配器已做多路兼容但没实跑。
4. **Antigravity matcher 正则实际匹配范围**——别把 `view_file` 这类只读工具也拦了。
5. **Stop 深链在"任务 id ≠ session id"时的行为**——实测用的 session_id 与任务一致；若未来 WorkBuddy 改用独立任务 id，深链会落到 home（已有降级，不会报错）。
6. 多屏跟随鼠标所在屏（当前只定位主屏顶部）。
7. 开机自启（可加启动目录 / 计划任务）。
8. `"始终允许"` 规则记忆（同一命令重复授权时免打扰）。
9. 毛玻璃是合成质感，不是 DWM Acrylic 真背景模糊（技术原因见决策 9）；如果坚持要真模糊，需改 DirectComposition 方案，成本高。

---

## 四、文件地图

```
agent-auth-island/
├─ HANDOFF.md                     ← 本文档（接手先读这个）
├─ README.md                      ← 面向使用者的说明（安装、视觉规格、协议）
├─ run.cmd                        ← 双击启动：自动找 publish/ → bin/Release/ → dotnet run
├─ build.ps1                      ← 构建脚本（需 .NET 8 SDK）
├─ settings.snippet.json          ← WorkBuddy 的 hooks 配置片段（合并进 settings.json）
├─ .gitignore
│
├─ AuthIsland/                    ← WPF 工程主体
│  ├─ AuthIsland.csproj           ← net8.0-windows, WinExe, x64, UseWPF+UseWindowsForms（托盘）；StartupObject=Program
│  ├─ app.manifest                ← DPI awareness（PerMonitorV2）+ Win10/11 兼容声明
│  ├─ Program.cs                  ← 手写 Main：--hook/--install/--uninstall 分流，钩子路径**完全不加载 WPF**
│  ├─ App.xaml(.cs)               ← 启动入口、全局样式（PanelButton 等）、异常兜底；未安装先出 SetupWindow
│  ├─ SetupWindow.xaml(.cs)       ← 一键安装向导（双击 exe 看到的窗口）
│  ├─ MainWindow.xaml             ← 胶囊视觉：玻璃分层 + 五层内容（Idle/Alert/Result/Panel/DormantDot）
│  ├─ MainWindow.xaml.cs          ← 状态机（Idle/Dormant/Alert/Result/Panel）、弹簧驱动、穿透、休眠
│  ├─ Animation/SpringValue.cs    ← 逐帧弹簧积分（核心手感来源）
│  ├─ Visual/FrostTexture.cs      ← 磨砂颗粒纹理（双层互质周期灰度噪声 → OpacityMask）
│  ├─ Cli/Gate.cs                 ← 「默认权限闸门」C# 版（从 hooks/policy.py 移植，判定 1:1）
│  ├─ Cli/HookRunner.cs           ← 钩子模式：四宿主协议 + stdin「读到完整 JSON 即返回」+ 滚动日志
│  ├─ Cli/IslandClient.cs         ← 裸 TcpClient 手写 HTTP（绕开系统代理；预检 350ms）
│  ├─ Cli/Installer.cs            ← 一键安装/卸载：复制自身、幂等写 hooks（先备份）、HKCU Run 自启
│  ├─ Cli/AppPaths.cs             ← 安装位置 %LOCALAPPDATA%\AuthIsland 与日志路径
│  ├─ Services/AuthServer.cs      ← TcpListener 手写极简 HTTP：挂起授权、完成通知、暂停、断连 watchdog
│  ├─ Services/DoneNotice.cs      ← 完成通知模型（sessionId / summary / cwd）
│  ├─ Services/TrayIcon.cs        ← NotifyIcon 托盘（图标运行时绘制，无需 .ico 资源）
│  ├─ Services/AuthRequest.cs     ← 请求模型 + TaskCompletionSource
│  └─ Interop/NativeMethods.cs    ← SetWindowLong / WM_NCHITTEST 等
│
├─ hooks/                         ← [旧版 Python 钩子，已被 exe 内置版取代；保留作参考/回退]
│  ├─ policy.py                   ← 默认权限闸门（C# 版的移植母本，含逐条理由的注释）
│  ├─ island_core.py              ← 统一 ask_island() → allow / deny / pass
│  ├─ pre_tool_use.py             ← WorkBuddy（PreToolUse；先过闸门再决定弹不弹）
│  ├─ stop_notify.py              ← WorkBuddy（Stop → /notify，完成通知；全程静默）
│  ├─ codex_permission_request.py ← Codex / ChatGPT 桌面版（PermissionRequest）
│  ├─ antigravity_pre_tool_use.py ← Antigravity（PreToolUse，fail-closed 兜底）
│  └─ demo.py                     ← 手动发一次授权请求，单测动效用
│
├─ integrations/                  ← 另两家的配置片段
│  ├─ codex.hooks.json
│  └─ antigravity.hooks.json
│
├─ docs/integration-feasibility.md ← 多客户端接入可行性评估（能力矩阵、风险）
├─ preview/island-preview.html    ← 浏览器动效调参页（拖滑块调弹簧，定完再改 C#）
├─ tools/
│  ├─ shot.py                     ← GDI 截图（系统截图被安全策略拦截后的替代）
│  ├─ click.py                    ← 模拟真实鼠标点击
│  ├─ choreo.py                   ← 一键交互验证（授权→面板→暂停→恢复）
│  ├─ winrect.py                  ← 查询胶囊窗口矩形
│  ├─ gate_test.py                ← 用 exe 跑闸门回归（33 命令 + 5 文具用例）
│  └─ policy_test.py              ← Python 版闸门回归（旧版对照）
├─ release/                       ← 单文件分发版：AuthIsland.exe（69MB 自包含+压缩，发给用户的就是它）
├─ publish/                       ← 多文件自包含发布产物（hook 启动比单文件快 ~150ms，本地调试用）
├─ shot-*.png                     ← 各状态验证截图
└─ AuthIsland/bin/Release/net8.0-windows/ ← 常规构建产物
```

---

## 五、关键技术决策（别推翻，除非有明确理由）

### 1. 为什么是 WPF 而不是 Electron / Tauri

要求"Windows + 原生 + 灵动岛级丝滑"。WPF 的硬件加速合成 + 逐帧控制足够，且没有 WebView 的启动延迟和内存开销。Tauri 的窗口动画受 WebView 帧率影响，做不出弹簧连续形变。

### 2. 形变动画不用 `DoubleAnimation`，用逐帧弹簧积分

`SpringValue.cs`，半隐式欧拉，默认 `stiffness = 320, damping = 26`。

**关键差异**：目标值可在动画中途被改写而**速度连续**。Storyboard 打断重开会"重新起跑"，连续来两条授权请求时会有明显顿挫——这正是灵动岛没有的。想调手感就改这两个数，或先用 `preview/island-preview.html` 试。

### 3. 三个形态（DIP）

| 状态 | 宽 × 高 | 圆角 | 内容 |
|---|---|---|---|
| Idle 待机 | 124 × 34 | 17 | 呼吸绿点 + "WorkBuddy" |
| Alert 授权 | 452 × 176 | 26 | 工具名徽章、命令预览、拒绝/允许、倒计时 |
| Result 结果 | 236 × 62 | 31 | 勾/叉 + "已允许/已拒绝"，1.15s 后回落 |

常量在 `MainWindow.xaml.cs` 第 18–20 行。

### 4. 浮层不挡桌面：`WM_NCHITTEST` 返回 `HTTRANSPARENT`

窗口固定 560×300 覆盖屏幕顶部，但胶囊之外的区域鼠标全部穿透到底层窗口。所以它常驻也不影响正常操作——这是"能不能常开"的前提。

### 5. 点击不抢焦点

`WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW + ShowActivated=false`：点胶囊时正在打字的窗口不失焦，任务栏和 Alt+Tab 里也看不到它。

### 6. 授权服务是手写 HTTP，不是 ASP.NET

用 `TcpListener` 手撸最小 HTTP（`AuthServer.cs`），好处是**零额外依赖、单文件发布体积可控**，且"连接挂起到用户点击"这个语义用原生 socket 表达最直接。

### 7. 一个核心 + 三个薄适配器

三家宿主的钩子协议不同，但语义收敛到同一个动作。所以：

```
island_core.ask_island(tool, detail, cwd) → "allow" | "deny" | "pass"
```

- `pass` 的语义是「我不决定」，由宿主走自己的原生审批。
- 任何异常（胶囊没开、端口不通、超时、返回垃圾）**一律归到 `pass`**（除 Antigravity 适配器转为 `ask`）。
- 差异全部收在适配器里，核心不动。

### 8. 三家宿主的关键差异（这是最容易踩的坑）

| 宿主 | 用哪个事件 | 输出 | 钩子失败时 |
|---|---|---|---|
| WorkBuddy | **`PermissionRequest`**（只在宿主判 ask 时触发） | `hookSpecificOutput.decision.behavior: allow/deny` | **fail-open**（返回空 → 宿主弹自己的原生审批） |
| Codex / ChatGPT 桌面版 | **`PermissionRequest`**（不是 PreToolUse！） | `decision.behavior: allow/deny` | **fail-open**（放行） |
| Antigravity | `PreToolUse`（每次调用都触发，闸门判颗粒度） | `decision: allow/deny/ask` | **fail-closed**（阻断！） |

**为什么 WorkBuddy / Codex 都用 `PermissionRequest`**：这类事件在宿主**自己的权限系统判定为 ask（正要弹审批）时**才触发——拦截颗粒度天然等于宿主的规则（WorkBuddy 是 `settings.json` 里的沙箱/权限规则，Codex 是它的审批策略），胶囊不用猜、也不用自己维护一套白名单。钩子返回 `allow/deny` 时宿主直接采纳，原生弹窗不会出现；返回空/超时则宿主照常弹自己的窗。WorkBuddy 的输出格式是 `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`（注意多了层 hookSpecificOutput）。

> 2026-10-17 修正：WorkBuddy 最初挂的是 `PreToolUse`（每次调用都触发，靠本地闸门判颗粒度）。
> 用户指出颗粒度应以宿主为准后，改为只挂 `PermissionRequest`。从 CLI 反编译确认：
> `checkPermission` 的 `default:`（ask）分支里才调 `dispatchPermissionRequestHook`，
> allow/deny 分支根本不经过钩子。

**Antigravity 为什么特殊**：它是 fail-closed，钩子崩溃/超时/非零退出会直接阻断工具调用。所以适配器必须永远 `exit 0`，任何异常都输出 `{"decision":"ask"}` 交回它自己的审批卡。**如果照搬 WorkBuddy 的写法，胶囊一关它什么命令都跑不了。**

### 9. 毛玻璃是"合成玻璃"，不是 DWM Acrylic

真 Acrylic（`SetWindowCompositionAttribute` / `DWMWA_SYSTEMBACKDROP_TYPE`）在本场景全都不行：
分层窗口（`AllowsTransparency`）上模糊区域是**整个矩形**（会盖住胶囊外的屏幕）；
非分层窗口要靠 `SetWindowRgn` 裁圆角（锯齿、无投影）；且胶囊逐帧改尺寸时 Acrylic 会严重闪屏。

所以采用合成方案：近黑半透底 → 斜向雾面渐变 → 两层互质周期（83/127px）灰度噪声做
`OpacityMask` 的磨砂颗粒 → 上亮下暗的内描边 → **独立图层的柔化投影**。
投影不挂在含文字的容器上（Effect 会让 ClearType 失效、文字发糊）。

### 10. 无事件三档退场：Idle → Dormant → Hidden

用户明确要求"没有事件时不影响屏幕观看"。梯度：
15s（`AUTH_ISLAND_DORMANT_SEC`）→ 缩成 54×18 暗点、55% 透明度；
3min（`AUTH_ISLAND_HIDE_SEC`，0 关闭）→ 窗口 `Hide()`，只剩托盘图标。
任何 `/authorize` 请求都会把它叫回来（从休眠尺寸"长"出来再变卡，弹簧中途改目标，正好是灵动岛手感）。
隐藏后**没有**常驻热区——发现方式是托盘图标，别再加"透明热区"方案（分层窗口全透明像素不参与命中测试，行不通）。

### 11. 胶囊永远"有反应"，Agent 死了卡片自动收

两个互补的修复：
- 点待机/休眠胶囊 → 打开**状态面板**（服务地址、最近请求时间、暂停/隐藏/退出）。此前点胶囊没有任何反馈，这是用户报的第一个问题。
- `AuthServer` 里起一个 socket watchdog（`WaitForClientCloseAsync`）：请求体读完后继续读，读到 EOF/异常说明 Agent 进程没了 → 立刻 `pass` 并收卡。否则 WorkBuddy 被关掉后，用户要对一张点不动的卡干等 60 秒。

"暂停拦截"（面板 / 托盘 / 右键菜单三处开关）置位后，`/authorize` 直接返回 `pass`，交回宿主原生审批——胶囊退到纯旁观位。

### 12. 任务完成通知：Stop hook → /notify → 深链

逆向 WorkBuddy 安装目录确认的三件事（`D:\Programs\WorkBuddy\resources\app.asar` 与
`app.asar.unpacked\cli\dist\codebuddy-lite-wb.mjs`）：
- CLI 支持 `Stop` 钩子，payload 含 `session_id`、`cwd`、`last_assistant_message`、`stop_hook_active`；
- 桌面端深链格式就是 `workbuddy://chat/<encodeURIComponent(sessionId)>`（它自己的系统通知就是这么跳的），另有 `workbuddy://home`；
- 协议已在 `HKCU\Software\Classes\workbuddy` 注册。

设计约束：
- `/notify` **立即回包**，Stop hook 在一轮对话结束时同步执行，绝不能等胶囊；
- hook 脚本**永远不输出任何内容**（Stop 的 stdout 会被宿主当作"是否阻止停止"的指令）；
- WorkBuddy 在前台时不弹（`GetForegroundWindow` 的进程名判断），`AUTH_ISLAND_NOTIFY_ALWAYS=1` 可强制；
- 卡片 6s 自动收（hover 暂停），授权卡优先，不叠加。

---

## 六、协议契约

`POST http://127.0.0.1:18765/authorize`

```json
{
  "id": "s1-1234",
  "tool": "Bash",
  "detail": "npm install -g @tencent/wecom-cli",
  "cwd": "D:/workbuuddy/heath",
  "timeoutMs": 60000
}
```

**响应**（连接会一直挂到用户点击或超时）：

```json
{ "decision": "allow" }   // 或 "deny" / "pass"
```

其它端点：

| 端点 | 用途 |
|---|---|
| `GET /health` | 探活，用于 hook 快速判断胶囊是否在线 |
| `POST /shutdown` | 优雅退出 |

端口可用环境变量 `AUTH_ISLAND_PORT` 覆盖（**hook 和服务端都要设**，否则连不上）。

Hook 侧可调环境变量：

| 变量 | 默认 | 说明 |
|---|---|---|
| `AUTH_ISLAND_PORT` | `18765` | 服务端口 |
| `AUTH_ISLAND_TIMEOUT_MS` | `60000` | 等用户点击的超时；超时即 `pass` |
| `AUTH_ISLAND_SESSION` | `s` | 请求 id 前缀，便于区分会话 |
| `AUTH_ISLAND_DORMANT_SEC` | `15` | 无事件多少秒后休眠成小点；0 = 永不休眠 |
| `AUTH_ISLAND_HIDE_SEC` | `180` | 无事件多少秒后整颗隐藏（只留托盘）；0 = 不隐藏 |
| `AUTH_ISLAND_NOTIFY_ALWAYS` | `0` | `1` = WorkBuddy 在前台时也弹完成通知 |
| `AUTH_ISLAND_NOTIFY_TIMEOUT` | `1.5` | hook 侧（秒）：/notify 最长等待 |
| `AUTH_ISLAND_NOTIFY_CHARS` | `90` | hook 侧：摘要最长字符数 |

---

## 七、怎么跑 / 怎么建 / 怎么测

### 启动胶囊

```powershell
cd D:\workbuuddy\heath\agent-auth-island
.\run.cmd                        # 自动找可用的 exe（推荐）
.\publish\AuthIsland.exe         # 自包含版，无需装 .NET（162MB）
```

### 构建

```powershell
# 需要便携版 SDK（不在 PATH 里，用绝对路径）
$env:DOTNET_CLI_TELEMETRY_OPTOUT="1"
& "D:\workbuuddy\heath\.tools\dotnet\dotnet.exe" build `
  "D:\workbuuddy\heath\agent-auth-island\AuthIsland\AuthIsland.csproj" -c Release --nologo
```

### 发布自包含版

```powershell
# 多文件版（本地调试用；hook 启动比单文件快 ~150ms）
& "D:\workbuuddy\heath\.tools\dotnet\dotnet.exe" publish `
  "D:\workbuuddy\heath\agent-auth-island\AuthIsland\AuthIsland.csproj" `
  -c Release -r win-x64 --self-contained true `
  -o "D:\workbuuddy\heath\agent-auth-island\publish" --nologo

# 单文件分发版（发给用户的就是它；实测 17s 可出包，此前"卡死"是没挂 DebugType=none）
& "D:\workbuuddy\heath\.tools\dotnet\dotnet.exe" publish `
  "D:\workbuuddy\heath\agent-auth-island\AuthIsland\AuthIsland.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none `
  -o "D:\workbuuddy\heath\agent-auth-island\release" --nologo
```

### 测一次授权（不接任何 Agent）

```powershell
# 前台会一直挂着等你点击，建议放后台跑
python "D:\workbuuddy\heath\agent-auth-island\hooks\demo.py" "Bash" "npm install -g @tencent/wecom-cli"
```

demo.py 还支持风险标签参数（tool / detail / risk / reason）：

```powershell
python hooks\demo.py Bash "sudo rm -rf /data" high "提权执行 + 强制/递归删除"
```

### 默认权限闸门（2026-10-17 加入，别删）

用户反馈"不要什么命令都弹出"，于是加了 `hooks/policy.py`：
先判定这次调用是否超出**默认权限**，没超出就静默放行（连胶囊都不亮），
超出了才送 `/authorize`，并带上 `risk`（low/medium/high）和 `reason`
（给用户看的一句话）。胶囊把 reason 渲染成标题旁的风险标签（高危红/需确认黄）。

- 判定原则：白名单是显式的，不认识的命令一律问；删除/提权/系统配置/
  网络安装/凭据目录一律问；写操作必须落在工作区（hook payload 的 `cwd`）内。
- `cd` 只影响后续段落的相对路径基准（base），写入的容身判定始终对着工作区根（root）。
- 静默放行的输出由 `policy.local.json` 的 `silentEmits` 控制：
  `"allow"`（默认，钩子直接放行）或 `"pass"`（退回宿主原生流程）。
- WorkBuddy 的 Codex 适配器**不过闸门**：PermissionRequest 只在宿主正要弹
  审批时触发，本身就是超权，不能再被我们的白名单放宽。
- 回归测试：`python tools/policy_test.py`（改 policy 后必跑，40+ 正反用例）。
- 已实测：`ls`/`cat`/`git status` 等静默（`_hook.log` 里 `gate=silent`）；
  `rm -rf` 等弹卡并显示红色风险标签；用户点「拒绝」后 WorkBuddy 真实拦截该工具调用。
- 已知保守行为：`$VAR` 开头的命令（变量无法解析）、`python -c`、heredoc、
  `$()` 都会问——这是有意的，宁严勿松。

或直接：

```powershell
curl.exe -X POST http://127.0.0.1:18765/authorize `
  -H "Content-Type: application/json" `
  -d '{\"tool\":\"Bash\",\"detail\":\"rm -rf build/\",\"timeoutMs\":60000}'
```

### 测三个适配器的输出协议

```bash
cd "D:/workbuuddy/heath/agent-auth-island"
echo '{"tool_name":"Bash","tool_input":{"command":"ls -la"}}' \
  | AUTH_ISLAND_TIMEOUT_MS=3000 python hooks/pre_tool_use.py

echo '{"tool_name":"Bash","tool_input":{"command":"rm -rf build/"}}' \
  | AUTH_ISLAND_TIMEOUT_MS=3000 python hooks/codex_permission_request.py

echo '{"tool_name":"run_command","tool_input":{"command":"npm install"}}' \
  | AUTH_ISLAND_TIMEOUT_MS=3000 python hooks/antigravity_pre_tool_use.py
```

### 自动化验证整套交互（授权点击 → 状态面板 → 暂停/恢复）

```bash
cd "D:/workbuuddy/heath/agent-auth-island" && python tools/choreo.py
```
会真实移动鼠标点击（别在打字时跑），每步截图到项目根目录。

### 截图验证（系统截图被安全策略拦截时的唯一可行方式）

```powershell
python "D:\workbuuddy\heath\agent-auth-island\tools\shot.py" "D:\workbuuddy\heath\agent-auth-island\shot.png" 400 0 1120 380
```
参数：`输出路径 x y width height`。用的是 Python ctypes 直调 GDI32 `BitBlt`。

---

## 八、接入各宿主

### WorkBuddy（已完成接入）

1. ~~合并 hooks~~ —— **已于 2026-10-17 切换到 `PermissionRequest`**（此前是 `PreToolUse`+闸门）。
   `%USERPROFILE%\.workbuddy\settings.json` 现在含 `PermissionRequest` + `Stop` 两段
   （由 `AuthIsland.exe --install` 自动写入）。要还原：胶囊右键退出，或 `AuthIsland.exe --uninstall`。
2. **重启不是必须的**——实测 `settings.json` 改动会热加载，改完下一次工具调用即生效。但行为异常时，
   完全重启（关窗 ≠ 退出，要托盘退出）仍然是最稳的兜底。
3. 会话权限模式必须是「询问 / 默认」。`dontAsk` / `bypassPermissions` 模式下**根本不会产生权限请求**，hook 也就不会被触发。

安装器写入的配置（无 matcher = 所有工具都适用；反正只有宿主判 ask 时才会触发）：

```json
{
  "hooks": {
    "PermissionRequest": [
      { "hooks": [{ "type": "command", "command": "\"C:\\Users\\<你>\\AppData\\Local\\AuthIsland\\AuthIsland.exe\" --hook permission", "timeout": 600 }] }
    ],
    "Stop": [
      { "hooks": [{ "type": "command", "command": "\"...\\AuthIsland.exe\" --hook stop", "timeout": 5 }] }
    ]
  }
}
```

> `timeout: 600` 是宿主等钩子的上限（秒）；钩子内部等用户点击 540s
> （`AUTH_ISLAND_PERMISSION_TIMEOUT_MS`），留 60s 余量给返回空后的原生流程。

### Codex / ChatGPT 桌面版（已写配置，未真机验证）

```powershell
# 1) 合并 integrations/codex.hooks.json 到 ~/.codex/hooks.json
# 2) ~/.codex/config.toml 加：
#    [features]
#    codex_hooks = true
# 3) Codex 里执行 /hooks，review 并 trust
# 4) 重启 Codex
```

### Antigravity（已写配置，未真机验证）

```powershell
# 1) 合并 integrations/antigravity.hooks.json 到 ~/.gemini/config/hooks.json
#    （全局配置，CLI / IDE / App 共用）
# 2) Settings > Customizations > Hooks 确认已加载
# 3) 重启 Antigravity
```

⚠️ 别加 `--dangerously-skip-permissions`，它会完全绕过钩子层。

---

## 九、踩过的坑（省得重新踩）

| 坑 | 现象 | 解决 |
|---|---|---|
| **hook 配置启动时缓存** | 改完 `settings.json` 没反应 | 必须完全退出进程再启动，关窗口不算 |
| **权限模式** | hook 从不触发 | `dontAsk` / `bypassPermissions` 下不产生权限请求，切回「询问」 |
| **Antigravity fail-closed** | 胶囊一关，Antigravity 所有命令都跑不了 | 适配器永远 `exit 0`，异常时输出 `{"decision":"ask"}` |
| **端口被占** | 服务起不来，整个窗口不显示 | `MainWindow` 里已加兜底：端口失败降级为仅 UI 模式，不崩 |
| **系统截图被拦** | `CopyFromScreen` 返回全黑 | 用 `tools/shot.py`（GDI BitBlt） |
| **屏幕锁定** | 截图全黑、`GetForegroundWindow()` 为 0 | 先解锁；锁屏状态下无法验证点击闭环 |
| **dotnet-install.ps1 卡住** | 后台几十分钟无进度 | 弃用脚本，直连下载 zip 后 `tar -xf` 解压 |
| **单文件发布（PublishSingleFile）** | 极慢，曾卡死被中断 | 用普通自包含发布，体积换速度 |
| **hook 依赖 `python` 在 PATH** | hook 静默失败 → fail-open 放行 | ⚠️**实测踩中**：钩子进程 PATH 先命中 `WindowsApps\python.exe` 商店存根，不报错不输出直接退出。命令里必须写 python 绝对路径 |
| **"hooks 不生效"先查日志** | 以为配置没加载，其实命令 spawn 失败 | 两个 hook 都写 `hooks/_hook.log`：无新行=没 spawn；有 invoked 无 decision=在等胶囊；pass=胶囊没开/暂停 |
| **以为 hooks 只在启动时加载** | 白白多重启一次 | 实测 `settings.json` 改动会**热加载**，下一次工具调用即生效。但改完行为仍异常时，重启是最稳的兜底 |
| **UseWindowsForms 注入全局 using** | `Application` / `Brush` 二义性编译错 | csproj 里 `<Using Remove="System.Windows.Forms" />` 等 |
| **Effect 挂在文字容器上** | ClearType 失效、文字发糊 | 投影单独一层 Border，只含黑色形状 |
| **ContextMenu 里的 x:Name** | 生成字段访问不到（独立 namescope） | 用 `Tag` 标记 + 遍历 `Items` 同步 |
| **分层窗口全透明像素不参与命中** | 想做"隐形热区唤回"行不通 | 隐藏态的唤回走托盘，别做透明热区 |
| **发布时 exe 正在运行** | MSB3027 文件被锁，重试 10 次后失败 | 先 `taskkill AuthIsland.exe` 再 publish |
| **Stop hook 输出 stdout** | 可能被宿主解析为"阻止 agent 停止" | stop_notify.py 全程静默，只 exit 0 |
| **6s 自动收起 vs 自动化点击** | 脚本点晚了卡片已退场，点到桌面 | 编排脚本里 notify 后 1s 内点击 |
| **`HTTP_PROXY` 会把回环请求也代理掉** | hook 连 `127.0.0.1:18765` 变慢/失败 | `build_opener(ProxyHandler({}))` 显式直连，三个脚本都改了 |
| **Windows 连未监听端口要等 ~2s** | 胶囊没开时每次工具调用白等 2.44s | 先做 0.35s TCP 预检（`is_alive()`），不通立刻 `pass`；实测降到 0.75s |
| **WinExe + 管道 stdin 读不到 EOF** | `ReadToEnd` 永远挂着，每次工具调用卡满超时 | ⚠️**实测踩中**：宿主管道的写端被子进程继承，EOF 永远不来（Python `subprocess` 100% 复现）。改为「字节层面括号配对，凑成完整 JSON 立即返回」（`HookRunner.ReadPayload`），一次 read 就够 |
| **框架依赖 exe 缺运行时** | 不报错退出，而是**弹"安装 .NET"对话框挂死** | 分发必须自包含；开发机跑框架依赖版要 `DOTNET_ROOT` |
| **手写 Main 与 WPF 生成的 Main 冲突** | CS0017 多入口 | csproj 设 `<StartupObject>AuthIsland.Program</StartupObject>`；钩子分支放 Main 最前，避免加载 WPF 程序集 |
| **单文件发布其实不慢** | 之前判了死刑 | 补上 `-p:DebugType=none` 后 17s 出包；真正的坑是没这个参数 |
| **hook 配置里有旧版条目** | 同一次调用弹两次卡 | 安装器先删再插（幂等）；匹配词要同时认 `AuthIsland` / `agent-auth-island` / `pre_tool_use.py` |
| **`json.dump` 重写 settings.json** | 内容正确但整文件重排版 | 改动前 `cp` 备份；合并后必须 `json.load` 校验一遍 |

---

## 十、下一步建议（按优先级）

1. **验证「拒绝」真的能拦下命令**——「允许」已实测通过；`deny` 路径还没在真机上走过一次。
   顺手确认点「拒绝」后 agent 收到的是拒绝理由，而不是一直等到超时。
2. **装 Codex，验证 `PermissionRequest` 真实起作用**——重点确认返回 `allow` 是否真的跳过了它自己的弹窗。
2. **确认 ChatGPT 桌面版是否吃 `~/.codex/hooks.json`**——这是最大不确定项。
3. **Antigravity 实跑**——打印真实 payload 看字段形态，校准 `extract()` 和 matcher 正则。
4. 多屏跟随鼠标所在屏（`MonitorFromPoint` + `GetCursorPos`）。
5. `"始终允许"` 规则记忆（命令前缀指纹 → 本地规则文件，命中则直接 `allow`）。
6. 开机自启（`HKCU\...\Run` 注册表项或启动目录快捷方式）。
7. 如果一定要真·背景模糊：DirectComposition / WinUI Composition Backdrop 方案，成本高，先别动。

---

## 十一、给新会话 / 新 Agent 的开场提示词

> 如果要换个会话或者换个 agent 继续，**把下面这段直接粘过去**：

```
项目：D:\workbuuddy\heath\agent-auth-island\

这是一个 Windows 原生（WPF / .NET 8）的"授权灵动岛"桌面浮层：Agent 需要授权时，
屏幕顶部的黑色胶囊会形变成卡片，显示「哪个工具、要执行什么命令」，用户点允许/拒绝，
决定通过 PreToolUse 类钩子回传给 Agent。

请先完整阅读 D:\workbuuddy\heath\agent-auth-island\HANDOFF.md，再读 README.md，
不要重复探测环境，也不要推翻已有的技术决策（尤其是"逐帧弹簧而不是 DoubleAnimation"
和"一个核心 + 三个薄适配器"）。当前 state 和待办在 HANDOFF.md 的第三、十节。

环境要点：
- .NET SDK 是便携版，在 D:\workbuuddy\heath\.tools\dotnet\dotnet.exe，不在 PATH，要用绝对路径
- hook 脚本用系统 `python`（3.13），必须在 PATH
- 授权服务在 127.0.0.1:18765，端口可用 AUTH_ISLAND_PORT 覆盖
- 系统截图工具被安全策略拦截，要截图用 tools/shot.py

我的下一步需求是：<在这里写你要做什么>
```

---

## 十二、一句话总结

> 胶囊负责"把授权推到人眼前并拿到一个明确的 yes/no"，hook 负责"把这个决定翻译成每个宿主能懂的话"。
> 两者之间只有一个协议：`POST /authorize` → `{"decision": "allow" | "deny" | "pass"}`。
> **任何新宿主接入，只需要再写一个 30 行的适配器，核心不用动。**
