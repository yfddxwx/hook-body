# 多客户端接入可行性评估

> 目标：同一个 AuthIsland 胶囊，同时服务 WorkBuddy、Antigravity、ChatGPT（Codex）。
> 评估时间：2026-10-17。本机未安装 Antigravity 与 ChatGPT，结论基于官方文档，标注了需实测的部分。

## 一句话结论

**可行，而且比预期顺利。** 三家都提供了「工具调用前可阻塞、可返回决策」的钩子；
WorkBuddy 已完成，Codex 的钩子输出格式几乎与它一致，Antigravity 的语义甚至更强（原生支持 `ask`）。

唯一接不了的是 **ChatGPT 网页版 / 移动端**——它不在本地执行工具，没有可拦截的调用。

## 能力矩阵

| 宿主 | 钩子位置 | 推荐事件 | 决策词汇 | 钩子失败时 | 可行性 | 新增工作量 |
|---|---|---|---|---|---|---|
| **WorkBuddy** | `~/.workbuddy/settings.json` | `PreToolUse` | allow / deny / ask | fail-open | ✅ 已完成 | 0 |
| **Codex CLI / ChatGPT 桌面版 Codex** | `~/.codex/hooks.json` 或 `config.toml [hooks]` | **`PermissionRequest`** | allow / deny（PreToolUse 仅保证 deny） | fail-open | ✅ 高 | 小：换输出格式 + 配置 |
| **Antigravity 2.0 / CLI / IDE** | `~/.gemini/config/hooks.json` 或 `.agents/hooks.json` | `PreToolUse` | allow / deny / **ask** / force_ask | **fail-closed** | ✅ 高 | 小：换输出格式 + 防阻断 |
| **ChatGPT 网页 / 移动端** | — | — | — | — | ❌ 不适用 | — |

## 逐家说明

### 1. Codex / ChatGPT 桌面版

Codex 现在有一套完整的生命周期钩子：`PreToolUse`、`PermissionRequest`、`PostToolUse`、
`SessionStart`、`Stop` 等，配置在 `~/.codex/hooks.json`，或 `config.toml` 的内联 `[hooks]` 表。
需要在 `config.toml` 打开 `[features] codex_hooks = true`，并在 `/hooks` 里 review & trust 之后才生效。

**为什么选 `PermissionRequest` 而不是 `PreToolUse`：**

- `PreToolUse` 只保证支持 `deny`；`permissionDecision: "allow"` 只在带 `updatedInput`（改写参数）时才被接受，
  `"ask"` 明确标注为 *parsed but not supported yet*。用它做审批会很别扭。
- `PermissionRequest` 恰恰是在「Codex 正要弹审批」时触发，
  返回 `{"decision":{"behavior":"allow"}}` 会**直接跳过它自己的审批弹窗**；返回 `deny` 则阻断。
  这正是胶囊要的效果。
- 它**只在需要审批时才跑**，不需要审批的命令不会打扰你。

ChatGPT 桌面版（2026-07 起 Codex 已并入，Work 模式底层复用 Codex 执行能力）理论上读同一份
`~/.codex/` 配置，**但桌面版是否加载用户 hooks 需要实测**。ChatGPT 云端 Work 任务不跑本地工具，天然不适用。

### 2. Antigravity

Antigravity 2.0 / CLI / IDE 共用一份 `~/.gemini/config/hooks.json`，
支持 `PreToolUse`、`PostToolUse`、`PreInvocation`、`PostInvocation`、`Stop`，matcher 是工具名正则。

它的 `PreToolUse` 决策词汇是目前最丰富的：

```
allow | deny | ask | force_ask | deny_unless_prior_grant
```

`ask` / `force_ask` 是**原生审批卡**，`force_ask` 还会忽略用户之前勾的「始终允许」——
说明 Antigravity 自己就把「把决定权交回外部」当成一等公民。已经有人用这个机制把审批转发到手机端并同步阻塞等待，
和 AuthIsland 的模式完全一致。

**但有一个必须处理的差异：Antigravity 是 fail-closed。**
钩子崩溃、超时、非零退出，工具调用会被**直接拦掉**（Codex / WorkBuddy 是放行）。
所以 Antigravity 的适配脚本必须永远 `exit 0`，任何异常都输出 `{"decision":"ask"}` 交回原生审批。

另外：`--dangerously-skip-permissions` 会**完全绕过钩子层**，这一点要比其他两家更小心。

### 3. WorkBuddy

已完成。`PreToolUse` + `hookSpecificOutput.permissionDecision`，fail-open。

## 统一架构

三家协议不同，但都收敛成同一个动作。所以拆成「一个核心 + 三个薄适配器」：

```
hooks/
├─ island_core.py                  # 统一：POST /authorize，返回 allow | deny | pass
├─ pre_tool_use.py                 # WorkBuddy   → permissionDecision
├─ codex_permission_request.py     # Codex       → decision.behavior
└─ antigravity_pre_tool_use.py     # Antigravity → decision: allow/deny/ask

integrations/
├─ codex.hooks.json
└─ antigravity.hooks.json
```

`pass` 是核心返回的第三种状态，含义是「我不决定」——
由适配器决定怎么优雅降级（Codex/WorkBuddy 静默退出，Antigravity 输出 `ask`）。

## fail-open vs fail-closed（最关键的差异）

| 宿主 | 钩子异常 | 胶囊没开 | 我们的策略 |
|---|---|---|---|
| WorkBuddy | 放行 | 走原生权限 | 不输出，静默退出 |
| Codex | 放行 | 走原生审批 | 不输出，静默退出 |
| Antigravity | **阻断** | **阻断** | 永远 exit 0，输出 `ask` 转原生审批 |

如果照搬 WorkBuddy 的写法直接给 Antigravity 用，胶囊一关，Antigravity 就**什么命令都跑不了**。
这是接入时最容易踩的坑。

## 未验证项（本机未安装，装上后需逐条确认）

1. Codex `PermissionRequest` 的实际 stdin 字段（是否含 `tool_input.description`）。
2. ChatGPT 桌面版是否读取用户级 `~/.codex/hooks.json`（以及要不要在 GUI 里单独信任）。
3. Antigravity 各版本 payload 的字段形态（`tool_name` vs `toolCall.args.ToolName`），适配器已做多路兼容但需实跑。
4. Antigravity matcher 正则的实际匹配范围（是否会匹配到 `view_file` 这类只读工具）。
5. 三家在多重并发请求时的超时策略是否一致。

## 落地步骤

```powershell
# Codex
# 1) 合并 integrations/codex.hooks.json 到 ~/.codex/hooks.json
# 2) config.toml 加 [features] codex_hooks = true
# 3) Codex 里执行 /hooks，review 并 trust
# 4) 重启 Codex

# Antigravity
# 1) 合并 integrations/antigravity.hooks.json 到 ~/.gemini/config/hooks.json
# 2) Settings > Customizations > Hooks 确认已加载
# 3) 重启 Antigravity
```

## 风险

- **胶囊是单点**：它挂了会让三家的审批一起失效。Antigravity 侧尤其危险，已用 `ask` 兜底。
- **授权疲劳**：三个 agent 同时跑，弹窗会排队。建议只在 `run_command` / 写文件 / MCP 这类真正有副作用的工具上挂。
- **信任机制**：Codex 要求显式 trust，脚本改动后 hash 变了要重新信任。
- **语义边界**：钩子是护栏不是铁墙。deny 了 Bash 的 `rm`，agent 可能换 Python 删文件——真正的硬边界仍要靠各家的 sandbox。
