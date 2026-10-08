# C# 调用 pi RPC —— 零配置文件 demo

用 C# 启动 `pi --mode rpc` 子进程，**所有配置（provider、baseUrl、API key、模型、系统提示词）全部硬编码在 C# 代码里**，pi 不读用户 `~/.pi/agent` 下的任何配置文件。

实测环境：pi **1.0.3**、.NET SDK **10.0.102**（`net10.0`）。

## 快速开始

```bash
cd ~/projects/csharp-pi-rpc-demo

# 1) 先跑自检：不需要任何 API key，不联网
dotnet run -- --selftest

# 2) 换真实 provider：编辑 PiRpcConfig.cs 里的 ApiKey / BaseUrl / ModelId，然后
dotnet run

# 想看生成的 bootstrap.js：
dotnet run -- --selftest --show-bootstrap
```

自检会拉起一个本地假 provider（`FakeOpenAiProvider`），端到端跑通
`C# → pi RPC 子进程 → bootstrap 扩展注入的 provider`，并把 pi 实际发出的请求打印出来：

```
pi 当前模型: csdemo/deepseek-flash

[用户] 用一句话说明：这条消息经过了哪些环节才到达你这里？
   [prompt 受理] disposition=started
[pi  ] 本地假 provider 收到了请求，链路已打通。

--- 假 provider 侧观察到的真实请求 ---
  Authorization : Bearer sk-selftest-local
  model         : deepseek-flash
  system prompt : 你是由 C# 控制台程序通过 pi RPC 驱动的助手。…

自检结果: 通过 ✓
```

真实 DeepSeek 端点也实测通过（`[C#-DEMO]` 前缀证明系统提示词确实来自 C#）。

## 关键机制

### 1. RPC 协议没有"传配置"的命令

pi 的 RPC 命令只有 `prompt` / `get_state` / `set_model` / `compact` 等 33 个，**没有** `set_system_prompt`、`register_provider`。`set_model` 只能在**已经可用**的模型里切换。

所以配置必须在**进程启动时**就位。三条通道：

| 通道 | 覆盖范围 |
|---|---|
| CLI 参数 | provider/model 选择、api key、系统提示词、工具白/黑名单、资源路径 |
| 环境变量 | 配置目录、会话目录、离线模式 |
| **扩展（本 demo 用的）** | **全部**：provider 的 baseUrl、凭据、模型定义、系统提示词 |

### 2. `PI_CODING_AGENT_DIR` —— 隔离配置目录

pi 解析配置目录的逻辑（`dist/config.js`）：

```js
export function getAgentDir() {
  const envDir = process.env[ENV_AGENT_DIR];        // PI_CODING_AGENT_DIR
  if (envDir) return expandTildePath(envDir);
  return join(homedir(), CONFIG_DIR_NAME, "agent"); // ~/.pi/agent
}
```

本 demo 把它指向临时目录，于是 `settings.json` / `models.json` / `auth.json` /
`models-store.json` / `extensions/` / `skills/` / `SYSTEM.md` / `AGENTS.md` / `trust.json`
全部改道，`~/.pi/agent` 一个都不读。

> ⚠️ 该目录**必须可写**：pi 要在里面写 `settings.json.lock`、`auth.json`、`models-store.json`。
> 只读目录会静默降级成"settings 无效"并打印 `EPERM`。

### 3. bootstrap 扩展 —— 运行时注入 provider

`BootstrapScript.Build()` 把 C# 常量序列化成一段 JS，运行时写到临时目录：

```js
const provider = { name, baseUrl, apiKey, api, models: [...] };
const systemPrompt = "…";

export default function (pi) {
  pi.registerProvider("csdemo", provider);                      // 注入 provider
  pi.on("before_agent_start", () => ({ systemPrompt }));        // 整段替换系统提示词
}
```

扩展**必须是一个文件**，但它的**内容**全部来自 `PiRpcConfig`，磁盘上不存在任何用户配置文件。

`before_agent_start` 返回的 `systemPrompt` 是**整段替换**，连 pi 默认附加的 `<cwd>` 段都不会有（实测）。

### 4. 必须显式传 `--provider` / `--model`

**扩展注册的 provider 不会自动成为默认模型。** 实测：只写 `--extension` 而不传
`--provider/--model`，请求不会到达注入的 provider。所以两者都要给：

```bash
pi --mode rpc --no-session \
   --no-extensions --no-context-files --no-approve \
   --extension <bootstrap.js> \
   --provider csdemo --model deepseek-flash
```

## 文件说明

| 文件 | 作用 |
|---|---|
| `PiRpcConfig.cs` | **全部配置**（`PiRpcConfig` record）+ bootstrap 脚本生成器 |
| `PiRpcSession.cs` | pi RPC 子进程封装：JSONL 分帧、按 id 关联、事件流、`agent_settled` |
| `FakeOpenAiProvider.cs` | 自检用的本地假 provider（含 SSE 流式） |
| `Program.cs` | 组装：建隔离目录 → 生成 bootstrap → 启动 pi → 提问 → 打印证据 |

## RPC 协议要点（本项目已实现）

- **严格 JSONL**：一行一个完整 JSON，`\n` 结尾。
- **按 id 关联**：命令带 `id`，`response` 回带同一个 `id`；命令是并发处理的，**不要依赖返回顺序**。
- **分帧陷阱**：只按 `\n` 切分。Node.js 的 `readline` 会把 `U+2028`/`U+2029` 也当换行，而它们在 JSON 字符串里合法——所以官方文档警告不要用它。
  .NET 的 `StreamReader.ReadLine()` 只按 `\r\n`/`\n`/`\r` 切分，**正好安全**。
- **`prompt` 返回 ≠ 干完了**：`data.disposition` 为 `started`/`queued`/`handled`。要等 `agent_settled` 才能确认 pi 不会再自动继续。`handled` 表示没启动 run，不要等。
- **stderr 不是协议数据**，是日志，要单独读走以免管道阻塞。
- **退出**：关闭子进程 stdin 就是请求有序退出。

## 行为变化在哪里被注释说明

本 demo 除了改变配置来源，还会连带改变 pi 的若干行为。这些改动**没有对应的开关文档**，
所以全部写在代码注释里，按影响从大到小：

| 位置 | 说明了什么 |
|---|---|
| `PiRpcConfig.cs` 类头注释 | 行为变化总览（配置改道 / 显式 flag / 提示词替换三块） |
| `PiRpcConfig.cs` → `SystemPrompt` | **改动最大的一处**：`forceSystemPrompt` 如何丢掉 pi 的 9 段结构化提示词，含实测数据与两种非破坏式替代 |
| `PiRpcConfig.cs` → `ModelId` | 运行时注入的模型缺失内置目录的 `compat` 元数据，推理/思考处理可能与内置定义不同 |
| `PiRpcConfig.cs` → `BootstrapScript` 类注释 | `registerProvider` 与 `before_agent_start` 各自的意图、副作用与实测证据；生成的 JS 里也有同样说明 |
| `Program.cs` → `piArgs` | 每个 `--no-*` flag 的意图、行为变化与实测证据 |
| `Program.cs` → `env` | `PI_CODING_AGENT_DIR` / `PI_OFFLINE` / `PI_SKIP_VERSION_CHECK` 的作用与副作用 |
| `Program.cs` 类头注释 | 全流程，以及哪三处会改变 pi 行为、哪些操作只是被动读取 |
| `PiRpcSession.cs` 类注释 | 明确这个类是协议中立的，不改变 pi 行为；调试行为异常应先看上面几处 |

一句话概括：**flags 与配置改道影响的是"能用哪些能力"，而提示词替换影响的是"模型的判断依据"。**
实测三种配置下 `tools` 数组都是 `read/bash/edit/write` 四个（工具靠 API 的 `tools` 字段传递，
与提示词文本无关），但 system prompt 会从 2809 字符塌缩到 23 字符。

## 生产环境注意事项

1. **钉死 pi 路径**。`PiRpcConfig.PiCliPath` 指向你自己打包的 `dist/cli.js`，改用 `node <cliPath>` 启动。

   > 如果用官方的 `RpcClient`（TypeScript），它的默认 `cliPath` 是相对路径 `"dist/cli.js"`，
   > 不设置就可能选中用户全局安装的 `pi`，隔离和版本控制全部失效。

2. **堵住两个不在 `PI_CODING_AGENT_DIR` 管辖内的入口**：
   - **`AGENTS.md` / `CLAUDE.md`**：不受项目信任机制约束，**永远加载**，会注入 system prompt → 用 `--no-context-files`。
   - **项目 `.pi/`**：RPC 无法弹信任提示，默认 `defaultProjectTrust: "ask"` 会跳过，但为了行为确定 → 用 `--no-approve`。

3. **`--no-extensions`**：关掉扩展发现和内置扩展（codemode / tool_search / MCP），显式 `--extension` 仍然加载（已实测）。要保留 MCP 之类的内置扩展就去掉这个参数。

4. **并发**：同一个 agent dir 起多个 pi 进程会争 `settings.json.lock`。多实例请各用独立子目录。

5. **模型目录**：全新的空 agent dir 里**没有任何模型**（模型清单来自 `models-store.json` 缓存）。本 demo 靠 `registerProvider` 显式定义模型，所以不依赖缓存；`PI_OFFLINE=1` 也不会影响它。
