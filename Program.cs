using System.Text;
using System.Text.Json.Nodes;

namespace CsharpPiRpcDemo;

/// <summary>
/// 组装整个 demo。执行顺序：
///   1) 建一个属于本程序的隔离 agent dir（pi 的配置目录改道到这里）
///   2) 把 PiRpcConfig 里的常量渲染成 bootstrap 扩展文件
///   3) 用一组显式 flag 启动 `pi --mode rpc` 子进程
///   4) get_state 确认 pi 真的采用了注入的 provider/model
///   5) prompt 提问，流式收文本，等 agent_settled
///   6) 自检模式下核对假 provider 侧看到的请求，反证配置确实来自 C#
///
/// 【与"改变 pi 行为"有关的三处，按影响从大到小】
///   · bootstrap 扩展里的 before_agent_start → 系统提示词被整段替换（见 PiRpcConfig.SystemPrompt）
///   · PI_CODING_AGENT_DIR → 配置来源整体改道（见下方第 1 步注释）
///   · 一组 --no-* flag → 逐项关闭若干能力（见下方 piArgs 注释）
/// 除此之外，本文件不做任何会改变 pi 行为的操作：事件订阅、get_state、prompt
/// 都只是读取和驱动，不会修改 pi 的内部逻辑。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var selfTest = args.Contains("--selftest", StringComparer.OrdinalIgnoreCase);
        var showBootstrap = args.Contains("--show-bootstrap", StringComparer.OrdinalIgnoreCase);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== C# 调用 pi RPC：零配置文件 demo ===");
        Console.WriteLine();

        // 自检模式会拉起一个本地假 provider，于是不需要任何真实 API key。
        // 它只替换 provider 的 baseUrl/apiKey/显示名，**不改变 pi 的任何行为逻辑**
        // —— 走的是完全相同的启动参数、相同的 bootstrap 注入路径，
        // 因此自检通过就说明整条链路（含扩展注入）是通的。
        using var fakeProvider = selfTest ? FakeOpenAiProvider.Start() : null;

        var config = new PiRpcConfig();
        if (fakeProvider is not null)
        {
            config = config with
            {
                BaseUrl = fakeProvider.BaseUrl,
                ApiKey = "sk-selftest-local",
                ProviderName = "Self-test fake provider",
                ModelName = "Self-test model",
            };
        }

        if (fakeProvider is null && config.HasPlaceholderKey)
        {
            Console.Error.WriteLine("PiRpcConfig.ApiKey 还是占位符。");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  方式一：编辑 PiRpcConfig.cs，把 ApiKey / BaseUrl / ModelId 换成你自己的。");
            Console.Error.WriteLine("  方式二：直接跑自检，不需要任何 key：");
            Console.Error.WriteLine();
            Console.Error.WriteLine("      dotnet run -- --selftest");
            return 1;
        }

        // ── 1. 属于本程序的隔离 agent dir ────────────────────────────────
        //
        // pi 解析配置目录的逻辑（dist/config.js）：
        //     export function getAgentDir() {
        //         const envDir = process.env[ENV_AGENT_DIR];        // PI_CODING_AGENT_DIR
        //         if (envDir) return expandTildePath(envDir);
        //         return join(homedir(), CONFIG_DIR_NAME, "agent"); // ~/.pi/agent
        //     }
        // 环境变量是第一优先级，所以下面的 PI_CODING_AGENT_DIR 一旦设置，
        // pi 就完全不碰用户的 ~/.pi/agent。
        //
        // 【它改道的范围 —— 不只是 settings.json】
        //   settings.json / models.json / auth.json / models-store.json /
        //   keybindings.json / mcp.json / trust.json /
        //   extensions/ / skills/ / prompts/ / themes/ /
        //   SYSTEM.md / APPEND_SYSTEM.md / AGENTS.md
        // 全部改到新目录；因为新目录里这些文件都不存在，等于全部失效。
        //
        // 【由此产生的行为变化（这是隔离的代价，属于既定意图）】
        //   · 用户 settings.json 里的偏好全部回落内置默认，包括 defaultTools、
        //     thinkingLevel、压缩阈值、自动重试、cacheWarming 等；
        //   · 用户安装的 extensions / skills / prompts / themes 不再加载；
        //   · 用户 mcp.json 里的 MCP 服务器不再连接；
        //   · 用户 trust.json 里的项目信任决定不参与，项目信任永远走默认路径；
        //   · pi 不再读取用户级 AGENTS.md（跨工作目录生效的那份用户指令）。
        //
        // 【目录必须可写】
        //   pi 会往这里写 settings.json.lock、auth.json、models-store.json。
        //   只读目录会导致 "EPERM ... settings.json.lock" 警告，
        //   并且 settings 会被静默判定为无效。
        //
        // 这里还额外做了：每次启动前清空目录，保证 demo 行为可复现。
        var root = Path.Combine(Path.GetTempPath(), "csharp-pi-rpc-demo");
        var agentDir = Path.Combine(root, "agent");
        var workDir = Path.Combine(root, "work");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(workDir);

        // ── 2. 把 C# 常量生成为 bootstrap 扩展 ──────────────────────────
        //
        // 这一步是"零配置文件"能成立的原因：pi 的 RPC 协议没有任何传配置的命令
        // （33 个命令里没有 set_system_prompt / register_provider），
        // 命令行也没有 baseUrl 之类的参数，所以只能靠扩展在运行时注入。
        //
        // 唯一的磁盘产物是这个 bootstrap.js —— 但它是**可执行代码**而不是配置文件，
        // 内容由 C# 常量序列化而来；生成它的注释见 PiRpcConfig.BootstrapScript。
        var bootstrapPath = Path.Combine(root, "bootstrap.js");
        BootstrapScript.WriteTo(bootstrapPath, config);

        Console.WriteLine("配置（全部来自 PiRpcConfig.cs，磁盘上没有任何配置文件）：");
        Console.WriteLine($"  agent dir : {agentDir}");
        Console.WriteLine($"              （与 ~/.pi/agent 完全隔离）");
        Console.WriteLine($"  bootstrap : {bootstrapPath}");
        Console.WriteLine($"  provider  : {config.ProviderId} -> {config.BaseUrl}");
        Console.WriteLine($"  model     : {config.ModelId}");
        Console.WriteLine($"  api key   : {Mask(config.ApiKey)}");
        if (selfTest)
        {
            Console.WriteLine("  模式      : --selftest（本地假 provider，不联网、不消耗额度）");
        }
        Console.WriteLine();

        if (showBootstrap)
        {
            Console.WriteLine("--- 生成的 bootstrap.js ---");
            Console.WriteLine(BootstrapScript.Build(config));
            Console.WriteLine("--------------------------");
            Console.WriteLine();
        }

        // ── 3. 启动 pi RPC 子进程 ───────────────────────────────────────
        //
        // piArgs 是"改变 pi 行为"最集中的地方。每一项的意图与副作用如下：
        //
        //  --mode rpc
        //      意图：以 RPC 模式运行（stdin/stdout 上的 JSONL 协议）。
        //      行为变化：无。这只是选择输入输出协议，不改变 agent 逻辑。
        //      注意：如果用官方的 RpcClient，它会自动加上这个参数，不要重复传。
        //
        //  --no-session
        //      意图：让每次调用都是全新、无状态的会话。
        //      行为变化：**对话记录完全不落盘**。实测一轮完整问答（totalMessages=3）后：
        //                  · get_session_stats.sessionFile 返回 null；
        //                  · 进程退出后磁盘上没有 .jsonl，连 sessions/ 目录都不会被创建；
        //                  · agent dir 里只剩 pi 自己的 auth.json(2B) 和
        //                    models-store.json(2B)，都不含对话内容；
        //                  · 内容只存在 pi 进程内存里，进程退出即丢失。
        //                因此无法 --continue / --resume，也没有历史上下文。
        //      相关变量：PI_SESSION_FILE / PI_SESSION_ID 在临时会话下**未设置**
        //                （官方文档原话："unset for ephemeral sessions"）。
        //                注意这两个变量只注入 LLM 可调用的 bash / powershell 工具，
        //                **不注入** RPC 的 bash 命令，所以无法通过 RPC bash 观测到
        //                （实测两种配置下都打印为空）。
        //      对照：去掉本参数后会话写到
        //                <agent-dir>/sessions/<cwd 转义名>/<ISO时间戳>_<uuid>.jsonl
        //            实测内容是按 cwd 分组的 JSONL 条目树：
        //                session 头 / model_change / thinking_level_change / message…
        //            每条带 id + parentId，构成可分支的树（fork / clone 的基础）。
        //            注意 system 提示词以**结构化 sections** 记录，而不是一段纯文本。
        //      适合"每次都从头开始"的 app；需要多轮对话就不要加它，
        //      改为用 --session-dir / PI_CODING_AGENT_SESSION_DIR 指定存储位置。
        //      （即便去掉本参数，因为 PI_CODING_AGENT_DIR 已改道，会话也只会落在
        //        隔离目录下的 sessions/，不会碰用户的 ~/.pi/agent/sessions/。）
        //
        //  --no-extensions
        //      意图：封闭运行环境，只保留我们自己用 --extension 显式加载的 bootstrap 扩展。
        //      行为变化：关闭**扩展发现**以及**内置扩展**（codemode / tool_search / MCP）。
        //                显式 --extension 传入的路径仍然会加载（已实测）。
        //      实测证据：若 agent 目录的 settings.json 用 defaultTools 启用了 codemode，
        //                不带此参数时 tools 有 5 个（多一个 codemode），
        //                带上此参数后只剩 read/bash/edit/write 四个 —— 工具会真的少一个。
        //                另外 MCP 服务器也会因此不再连接。
        //      若你的 app 需要 MCP 或 codemode，请去掉这一项。
        //
        //  --no-context-files
        //      意图：不让工作目录里的指令文件影响本 app 的模型行为。
        //      行为变化：不加载 cwd 及其祖先目录的 AGENTS.md / CLAUDE.md，
        //                系统提示词里不再有 <project_context> 段。
        //      实测证据：cwd 放一个 AGENTS.md 时，不加此参数 system prompt 为 3021 字符
        //                且包含 <project_context> 与文件内容；加上后为 2810 字符且不含。
        //      注意：上下文文件**不受项目信任机制约束**，只要存在就会被加载，
        //            所以如果 app 要密封行为，这个参数是必需的。
        //
        //  --no-approve
        //      意图：确保项目级 .pi/ 一律不参与，让行为可预测。
        //      行为变化：跳过 .pi/settings.json、.pi/mcp.json、.pi/extensions、
        //                .pi/skills、.pi/prompts、.pi/themes、
        //                .pi/SYSTEM.md、.pi/APPEND_SYSTEM.md。
        //      说明：RPC 模式本来就无法弹出项目信任提示，而 defaultProjectTrust
        //            默认是 "ask"，所以不加这个参数时这些资源通常也会被跳过；
        //            显式指定是为了消除"用户 trust.json 里恰好有决定"之类的不确定性。
        //
        //  --extension <bootstrapPath>
        //      意图：注入 provider 定义和系统提示词（见 PiRpcConfig.BootstrapScript）。
        //      行为变化：注入 provider（等价于 models.json 的一段）+
        //                **整段替换系统提示词**（影响最大的一处）。
        //
        //  --provider / --model
        //      意图：指定要用的 provider 和模型。
        //      为什么必须显式给：扩展注册的 provider **不会自动成为默认模型**。
        //      实测证据：只传 --extension 而不传这两个参数时，请求不会到达注入的 provider
        //                （没有任何请求打到自检用的假端点）。
        var piArgs = new List<string>
        {
            "--mode", "rpc",
            "--no-session",
            "--no-extensions",     // 关掉扩展发现与内置扩展；显式 --extension 仍然生效
            "--no-context-files",  // 不读 cwd 及祖先目录的 AGENTS.md / CLAUDE.md
            "--no-approve",        // 项目 .pi/ 一律不加载
            "--extension", bootstrapPath,
            // 必须显式指定：扩展注册的 provider 不会自动成为默认模型。
            "--provider", config.ProviderId,
            "--model", config.ModelId,
        };

        // 环境变量：只影响 pi 进程自身的配置解析与联网行为，不改变 agent 逻辑。
        //
        //  PI_CODING_AGENT_DIR      核心开关，把配置目录整体搬走（详见第 1 步注释）。
        //  PI_OFFLINE=1              禁用启动时的自动联网：包括模型目录刷新和版本检查。
        //                            副作用：全新空目录在离线状态下永远拿不到内置模型清单
        //                            （模型清单来自 models-store.json 缓存，不是写死在代码里）。
        //                            本 demo 靠 bootstrap 的 registerProvider 显式定义模型，
        //                            所以不受影响。
        //  PI_SKIP_VERSION_CHECK=1   不向 pi.dev 发起版本检查请求。与 PI_OFFLINE 部分重叠，
        //                            这里保留是为了语义清晰。
        //
        // 注意：PiRpcSession 把这张表**合并**进继承来的环境变量，不是替换，
        //       所以父进程的 PATH、HOME、代理变量等都会照常传递给 pi。
        var env = new Dictionary<string, string?>
        {
            ["PI_CODING_AGENT_DIR"] = agentDir,  // 核心开关：把配置目录整个搬走
            ["PI_OFFLINE"] = "1",                // 不联网刷新模型目录
            ["PI_SKIP_VERSION_CHECK"] = "1",     // 不做版本检查请求
        };

        await using var session = PiRpcSession.Start(config, piArgs, env, workDir);

        var answer = new StringBuilder();
        var answerHeaderPrinted = false;

        // 事件订阅只是**被动读取** pi 的事件流，不改变 pi 的任何行为。
        // 想改变行为必须在扩展里做（pi.on(...) 的 handler 才能 transform / block）。
        session.EventReceived += evt =>
        {
            var type = PiRpcSession.Str(evt, "type");

            // message_update 里承载流式增量；delta 是本次新增的文本片段。
            if (type == "message_update" &&
                evt["assistantMessageEvent"] is JsonNode inner &&
                PiRpcSession.Str(inner, "type") == "text_delta" &&
                PiRpcSession.Str(inner, "delta") is { } delta)
            {
                if (!answerHeaderPrinted)
                {
                    answerHeaderPrinted = true;
                    Console.Write("[pi  ] ");
                }
                answer.Append(delta);
                Console.Write(delta);
            }
            // 扩展 handler 抛错时 pi 通过 extension_error 上报，而不是让进程崩掉。
            // 这是 RPC 客户端唯一能察觉"扩展出问题"的通道。
            // 注意：本 demo 的 bootstrap 若注册 provider 失败（比如 JSON 写错），
            // 就会走这里，而不是让 pi 退出。
            else if (type == "extension_error")
            {
                Console.Error.WriteLine($"[扩展报错] {evt["error"]}");
            }
        };

        try
        {
            // ── 4. 先确认 pi 实际采用了哪个 provider/model ───────────────
            // 只读查询，不改变 pi 行为；用来证明 --provider/--model 确实生效
            // （因为扩展注册的 provider 不会自动成为默认模型）。
            var state = await session.SendAsync(
                new JsonObject { ["type"] = "get_state" }, config.StartupTimeout);

            var model = state["data"]?["model"];
            Console.WriteLine(
                $"pi 当前模型: {PiRpcSession.Str(model, "provider")}/{PiRpcSession.Str(model, "id")}");
            Console.WriteLine();

            // ── 5. 提问，流式接收，等 agent_settled ──────────────────────
            const string userPrompt = "用一句话说明：这条消息经过了哪些环节才到达你这里？";
            Console.WriteLine($"[用户] {userPrompt}");

            await session.PromptAsync(userPrompt, config.PromptTimeout);
            if (!answerHeaderPrinted)
            {
                Console.Write("[pi  ] (没有收到文本输出)");
            }
            Console.WriteLine();
            Console.WriteLine();

            // ── 6. 自检：打印假 provider 侧观察到的证据 ──────────────────
            // 这三项是从"pi 实际发出的 HTTP 请求"里抓的，不是从我们自己的变量里读的，
            // 所以能反证：Authorization 来自 C# 的 ApiKey、model 来自 C# 的 ModelId、
            // system prompt 来自 C# 的 SystemPrompt —— 全部绕过了 models.json /
            // auth.json / SYSTEM.md。
            if (fakeProvider is not null)
            {
                Console.WriteLine("--- 假 provider 侧观察到的真实请求 ---");
                Console.WriteLine($"  Authorization : {fakeProvider.SeenAuthorization}");
                Console.WriteLine($"  model         : {fakeProvider.SeenModel}");
                Console.WriteLine($"  system prompt : {Clip(fakeProvider.SeenSystemPrompt, 120)}");
                Console.WriteLine();
                Console.WriteLine("这三项全部来自 C# 代码里的 PiRpcConfig 常量，");
                Console.WriteLine("而不是任何 models.json / auth.json / SYSTEM.md。");
                Console.WriteLine();

                var ok = fakeProvider.RequestCount > 0
                         && fakeProvider.SeenAuthorization == "Bearer " + config.ApiKey
                         && fakeProvider.SeenModel == config.ModelId
                         && fakeProvider.SeenSystemPrompt == config.SystemPrompt;

                Console.WriteLine(ok ? "自检结果: 通过 ✓" : "自检结果: 未通过 ✗");
                if (!ok)
                {
                    return 2;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("调用失败: " + ex.Message);
            var stderr = session.StderrText.Trim();
            if (stderr.Length > 0)
            {
                Console.Error.WriteLine("--- pi stderr ---");
                Console.Error.WriteLine(stderr);
            }
            return 3;
        }
        finally
        {
            // DisposeAsync 会关闭子进程 stdin，请求 pi 有序退出。
        }

        var remainingStderr = session.StderrText.Trim();
        if (remainingStderr.Length > 0)
        {
            Console.WriteLine("--- pi stderr（诊断信息，不是协议数据）---");
            Console.WriteLine(remainingStderr);
        }

        Console.WriteLine();
        Console.WriteLine("完成。");
        return 0;
    }

    private static string Mask(string key)
    {
        if (key.Length <= 10)
        {
            return new string('*', key.Length);
        }
        return key[..6] + new string('*', 6) + key[^4..];
    }

    private static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(无)";
        }
        var flat = text.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..max] + "…";
    }
}
