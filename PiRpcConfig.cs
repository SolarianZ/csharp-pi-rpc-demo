using System.Text;
using System.Text.Json;

namespace CsharpPiRpcDemo;

/// <summary>
/// 这个程序的全部配置。没有 settings.json、没有 models.json、没有 auth.json ——
/// 所有 provider、模型、API key、系统提示词都写在这里，由 C# 在运行时注入 pi 进程。
///
/// =====================================================================
/// 【这份配置会连带改变 pi 的哪些行为】
///
/// 本 demo 表面上只做了两件事：① 把 pi 的配置来源换到隔离目录；
/// ② 通过 bootstrap 扩展注入 provider 和系统提示词。
/// 但这两件事除"配置来源"之外，还会连带改变 pi 的若干行为。总览如下，
/// 字段级的细节见各属性注释，flag 级的细节见 Program.cs 里 piArgs 的注释。
///
/// 1) 配置来源改道（PI_CODING_AGENT_DIR）—— 不只是"换个地方读配置"：
///    用户 settings.json 里的所有偏好都会失效并回落内置默认，包括 defaultTools、
///    thinkingLevel、压缩(compaction)阈值、自动重试、cacheWarming 等；
///    用户的 extensions / skills / prompts / themes / mcp.json / trust.json
///    也全部不再加载。这是隔离的代价，属于本 demo 的既定意图。
///
/// 2) 显式 flag（见 Program.cs 的 piArgs）—— 每一项都有行为副作用：
///      --no-context-files → 不再注入 AGENTS.md / CLAUDE.md，提示词少一段
///      --no-approve       → 项目 .pi/ 全部不加载
///      --no-extensions    → 关闭扩展发现与内置扩展，可能真的少一个工具
///      --no-session       → 对话记录完全不落盘（sessionFile=null，不创建 sessions/
///                            目录），内容仅存于进程内存，无法 continue / resume
///
/// 3) 系统提示词被「整段替换」—— 这是最实质的一条，见 SystemPrompt 属性注释。
///
/// 【实测确认未被改变的部分】
///    · 工具的 API 声明本身（tools 数组由 API 的 tools 字段传递，与提示词文本无关）
///    · thinkingLevel / autoCompaction / steeringMode / followUpMode 的默认值
///    · RPC 协议本身、会话事件流、prompt 的 disposition 语义
/// =====================================================================
/// </summary>
public sealed record PiRpcConfig
{
    // ────────────────────────────────────────────────────────────────
    // 1. pi 可执行文件
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 直接调用 PATH 里的 pi。
    /// 行为影响：这会让"用户同时也装了 pi"的场景变得不确定 —— 无法保证选中哪个 pi，
    /// 版本和行为都可能与预期不同。生产环境请改用 <see cref="PiCliPath"/>。
    /// </summary>
    public string PiCommand { get; init; } = "pi";

    /// <summary>
    /// 生产环境请把它设成你自己打包的 pi 的 dist/cli.js 绝对路径。
    /// 否则会选中用户全局安装的 pi，版本和隔离都不可控。
    /// 设置后改用 `node &lt;cliPath&gt;` 启动。
    ///
    /// 补充：官方 TypeScript 的 RpcClient 默认 cliPath 是相对路径 "dist/cli.js"，
    /// 且用 spawn("node", [cliPath, ...]) 启动，所以不设置它同样会选错 pi。
    /// </summary>
    public string? PiCliPath { get; init; } = null;

    // ────────────────────────────────────────────────────────────────
    // 2. Provider 与模型（常规情况下这些本该写在 &lt;agent-dir&gt;/models.json 里）
    //
    //    这里改为由 bootstrap 扩展在运行时调用 pi.registerProvider() 注入，
    //    效果等价于 models.json 里的 providers.&lt;id&gt; 段。
    //    之所以必须用扩展，是因为 RPC 协议没有任何"设置 provider"的命令
    //    （只有 prompt / get_state / set_model 等 33 个），而 set_model 只能在
    //    已经可用的模型里切换；命令行也没有 baseUrl 参数。
    // ────────────────────────────────────────────────────────────────

    /// <summary>Provider 的 ID，用于 registerProvider 和 --provider 参数。</summary>
    public string ProviderId { get; init; } = "csdemo";

    /// <summary>仅用于展示，不影响请求内容。</summary>
    public string ProviderName { get; init; } = "C# RPC Demo Provider";

    /// <summary>
    /// API 端点。常规做法是写在 models.json 的 baseUrl。
    /// 行为影响：无（只是把端点换成了注入值）。
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.deepseek.com";

    /// <summary>
    /// API key。常规做法是放在 &lt;agent-dir&gt;/auth.json 或 models.json 的 apiKey。
    ///
    /// 实测凭据优先级：--api-key &gt; auth.json &gt; models.json 的 apiKey。
    /// 注意 models.json 里定义的自定义 provider **不读环境变量**（只有内置 provider 读，
    /// 例如 DEEPSEEK_API_KEY），所以自定义 provider 的 key 必须显式注入。
    /// </summary>
    public string ApiKey { get; init; } = "sk-REPLACE-WITH-YOUR-OWN-KEY";

    /// <summary>协议适配。openai-completions 表示走 OpenAI Chat Completions 兼容协议。</summary>
    public string Api { get; init; } = "openai-completions";

    /// <summary>
    /// 模型 ID。
    ///
    /// 行为影响（值得注意）：模型定义由扩展在运行时给出，因此 pi 不会去读
    /// models-store.json 的模型目录缓存 —— 全新的空 agent dir 里本来也一个模型都没有
    /// （模型清单来自该缓存文件，不是写死在代码里）。
    /// 代价是失去了内置目录里那份模型的 compat 元数据：例如 pi 自带目录中的
    /// deepseek-flash 带有 requiresReasoningContentOnAssistantMessages、thinkingFormat
    /// 等兼容开关，而这里注入的模型不带这些字段，推理/思考相关的处理可能与内置定义不同。
    /// 简单对话已实测正常；若你依赖这些兼容行为，应改用 models.json 或补齐模型字段。
    /// </summary>
    public string ModelId { get; init; } = "deepseek-flash";

    /// <summary>模型显示名。</summary>
    public string ModelName { get; init; } = "DeepSeek Flash (injected by C#)";

    /// <summary>是否声明为推理模型。声明后 pi 才会按推理模型处理思考等级等参数。</summary>
    public bool Reasoning { get; init; } = false;

    /// <summary>上下文窗口大小，影响压缩阈值等。注入值即 pi 的认知，不再来自模型目录。</summary>
    public int ContextWindow { get; init; } = 128_000;

    /// <summary>单次回复的最大 token 数。</summary>
    public int MaxTokens { get; init; } = 8_192;

    // ────────────────────────────────────────────────────────────────
    // 3. 系统提示词（常规情况下本该写在 &lt;agent-dir&gt;/SYSTEM.md 或 --system-prompt 里）
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 系统提示词。
    ///
    /// ⚠️ 这是本 demo 对 pi 行为改动最大的一处。注入方式见 BootstrapScript：
    /// 它在 before_agent_start 里返回 { systemPrompt }，而 pi 会把它当作
    /// 「强制整体替换」(forceSystemPrompt)。pi 源码 dist/core/system-prompt.js：
    ///
    ///     export function buildSystemPromptState(input) {
    ///         if (input.forceSystemPrompt !== undefined)
    ///             return { content: input.forceSystemPrompt };   // 只有 content，没有 sections
    ///         return { content: "", sections: buildSystemPromptSections(input) };
    ///     }
    ///
    /// 也就是说，pi 正常的那套有序结构化提示词会被整体丢弃，只剩下面这一段字符串。
    /// 正常结构包含 9 段（buildSystemPromptSections 的组装顺序）：
    ///     1. preamble         角色设定（"You are an expert coding assistant operating inside pi…"）
    ///                          —— 若设置了 SYSTEM.md / --system-prompt，
    ///                             则走 customPrompt 分支替换此段
    ///     2. tools            工具清单及用途
    ///     3. rules            各工具贡献的 guideline，含 edit 的 oldText 精确匹配规则、
    ///                         多处编辑须合并为一次调用、write 仅用于新建/整体重写等
    ///     4. docs             pi 文档与示例的路径指引
    ///     5. addendum         APPEND_SYSTEM.md / --append-system-prompt 的内容
    ///     6. project_context  AGENTS.md / CLAUDE.md 的内容
    ///     7. skills           skills 列表
    ///     8. cwd              工作目录
    ///     9. 自定义 sections
    /// 注意 2/3/4 段只在**没有** customPrompt 时才生成。
    ///
    /// 实测（同一环境、同一模型、同一提问）：
    ///     默认 pi                            → system prompt 2809 字符，结构完整
    ///     本 demo 全部 flag 但不覆盖提示词   → 2809 字符（说明 flag 本身不动提示词结构）
    ///     本 demo 覆盖提示词                 → 23 字符，只剩下面这段字符串
    ///
    /// 后果：
    ///   · 模型看不到工具用途说明和 &lt;rules&gt;，尤其是 edit 的 oldText 匹配规则，
    ///     更容易把编辑参数写错；
    ///   · 模型不知道工作目录（原来的 &lt;cwd&gt; 段没了）；
    ///   · 问及 pi 自身时没有 &lt;docs&gt; 路径指引，会去乱找文件；
    ///   · 失去 pi「结构化 sections + transcript delta」的增量维护机制，
    ///     变成每轮重发一个固定串。
    ///
    /// 但请注意：**工具本身并没有被禁用**。tools 数组是通过 API 的 tools 字段传的，
    /// 与提示词文本无关；实测三种配置的 tools 都仍是 read/bash/edit/write 四个。
    /// 模型依然能调用工具，只是失去了文本层面的使用指引。
    ///
    /// 这是本 demo 的有意选择：让 app 完全掌控人格，不让 pi 的默认 coding-assistant
    /// 设定和文档指引泄漏进来。如果你希望保留 pi 的 agent 能力，有两种**不改逻辑**的替代：
    ///   a) 换成非破坏式注入：在 before_agent_start 里修改可变的
    ///      event.systemPromptOptions（例如 appendSystemPrompt / promptGuidelines /
    ///      sections），不要返回 systemPrompt。实测这样 &lt;tools&gt;/&lt;rules&gt;/&lt;docs&gt;/&lt;cwd&gt;
    ///      全部保留，自定义内容也会注入（提示词长度 2876，各段齐全）。
    ///   b) 退一档改用 --append-system-prompt（走 appendSystemPrompt，只追加 &lt;addendum&gt;）。
    /// 另外 SYSTEM.md / --system-prompt 走 customPrompt 分支，比本方案温和一档：
    /// 它替换 preamble 并跳过 tools/rules/docs，但保留 addendum/project_context/skills/cwd。
    /// </summary>
    public string SystemPrompt { get; init; } =
        "你是由 C# 控制台程序通过 pi RPC 驱动的助手。" +
        "请用中文回答，保持简短，并在回答开头加上 [C#-DEMO] 标记。";

    // ────────────────────────────────────────────────────────────────
    // 4. 运行时行为（这两个超时只影响 C# 侧的等待，不改变 pi 行为）
    // ────────────────────────────────────────────────────────────────

    /// <summary>单条命令的响应等待上限（get_state 等）。</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 一次提问的等待上限，等的是 agent_settled。
    /// 注意 agent_end 只是单次底层 run 结束，重试/压缩/队列仍可能继续，
    /// 所以这里等的是 agent_settled 而不是 agent_end。
    /// </summary>
    public TimeSpan PromptTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>占位符检测：仅用于在没填 key 时给出友好提示，不影响 pi。</summary>
    public bool HasPlaceholderKey =>
        ApiKey.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 生成 pi 的 bootstrap 扩展。这是整个"零配置文件"方案的关键：
/// pi 的 RPC 协议没有任何"传配置"的命令，但扩展可以在运行时
/// registerProvider() 注入 provider/模型/API key，并用 before_agent_start 覆盖系统提示词。
///
/// 扩展必须是一个文件，所以我们在运行时把它写到临时目录；
/// 但它的**内容**全部来自 C# 常量，磁盘上不存在任何用户配置文件。
///
/// ─────────────────────────────────────────────────────────────────────
/// 【这段生成的代码会让 pi 发生哪些行为变化】
///
/// A) pi.registerProvider(id, cfg)
///    · 效果：等价于在 &lt;agent-dir&gt;/models.json 里写一个 providers.&lt;id&gt; 段，
///      把 baseUrl / apiKey / 模型定义带进 pi。
///    · 行为变化：pi 不再需要（也不读取）models.json 和 auth.json；
///      模型目录缓存 models-store.json 也用不上。
///    · 副作用：模型的 compat 元数据没了（见 PiRpcConfig.ModelId 注释）。
///    · 重要：扩展注册的 provider **不会自动成为默认模型**。实测只给 --extension
///      而不传 --provider/--model 时，请求根本不会到达该 provider。所以必须配合
///      Program.cs 里的 --provider / --model 一起用。
///
/// B) pi.on("before_agent_start", () =&gt; ({ systemPrompt }))
///    · 效果：pi 把返回值里的 systemPrompt 当作 forceSystemPrompt —— 整体替换。
///    · 行为变化：**pi 正常的 9 段结构化提示词被全部丢弃**（preamble / tools /
///      rules / docs / addendum / project_context / skills / cwd / 自定义段），
///      只剩 C# 给的这一串。详见 PiRpcConfig.SystemPrompt 注释里的实测数据。
///    · 但工具没有被禁用：tools 仍通过 API 的 tools 字段传递。
///    · 非破坏式替代（不改这里的逻辑，仅备查）：不要返回 systemPrompt，改用
///        event.systemPromptOptions.appendSystemPrompt = "...";
///        event.systemPromptOptions.promptGuidelines = ["..."];
///        event.systemPromptOptions.sections["my_tag"] = "...";
///      systemPromptOptions 是可变的（类型注释：Mutable prompt sections），
///      实测这样结构与自定义内容可以兼得。
///
/// 【扩展的加载时机与权限】
/// 扩展跑在 pi 进程内，拥有与 pi 相同的操作系统权限，能读取提示词、工具调用、
/// 文件和凭据。只加载可信来源的扩展。另外 pi 会等待异步 factory 完成后才继续启动，
/// 所以不要在这个 factory 里启动进程/定时器；长生命周期资源应放在 session_start，
/// 并在 session_shutdown 里做幂等清理。本 demo 的 factory 只做同步注册，是安全的。
/// </summary>
public static class BootstrapScript
{
    /// <summary>
    /// JSON 序列化选项。WriteIndented 让生成出来的 JS 可读
    /// （可以 dotnet run -- --selftest --show-bootstrap 查看）。
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 把 PiRpcConfig 渲染成一段 ESM 模块（pi 用 jiti 加载，.js/.ts 都支持）。
    ///
    /// 这里刻意用 JsonSerializer 生成 provider 对象而不是手写字符串拼接，
    /// 避免 API key / 提示词里的引号、换行、反斜杠造成转义错误。
    /// </summary>
    public static string Build(PiRpcConfig cfg)
    {
        // 形状与 models.json 的 providers.<id> 一致（见 ProviderConfigInput）：
        // name / baseUrl / apiKey / api / models[]。
        // models[] 里每一项的必填字段是 id / name / input / cost，
        // 加上 chat 模型的 reasoning / contextWindow / maxTokens。
        var provider = new
        {
            name = cfg.ProviderName,
            baseUrl = cfg.BaseUrl,
            apiKey = cfg.ApiKey,
            api = cfg.Api,
            models = new object[]
            {
                new
                {
                    id = cfg.ModelId,
                    name = cfg.ModelName,
                    reasoning = cfg.Reasoning,
                    input = new[] { "text" },
                    // cost 用于 pi 的用量估算；这里是自定义端点，填 0 即可。
                    cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0 },
                    contextWindow = cfg.ContextWindow,
                    maxTokens = cfg.MaxTokens,
                },
            },
        };

        var providerJson = JsonSerializer.Serialize(provider, JsonOpts);
        var systemPromptJson = JsonSerializer.Serialize(cfg.SystemPrompt);
        var providerIdJson = JsonSerializer.Serialize(cfg.ProviderId);

        // $$""" ... """ 里的 {{ }} 是 C# 插值，单个 { } 原样输出（JS 语法）。
        return $$"""
            // 本文件由 C# 在运行时生成，内容全部来自 PiRpcConfig 中的常量。
            // 它不是一个"配置文件"：它是注入配置的可执行代码。
            const provider = {{providerJson}};
            const systemPrompt = {{systemPromptJson}};

            export default function (pi) {
              // ① 注入 provider（baseUrl / apiKey / 模型定义）。
              //    等价于 models.json 的 providers.<id> 段；注册后 pi 不再需要读
              //    models.json / auth.json，也不再依赖模型目录缓存 models-store.json。
              //    注意：注册的 provider 不会自动成为默认模型，必须配合命令行
              //    的 --provider / --model（见 Program.cs），否则请求不会发到它。
              pi.registerProvider({{providerIdJson}}, provider);

              // ② 覆盖系统提示词。
              //    pi 会把这里的返回值当作 forceSystemPrompt —— **整体替换**。
              //    于是 pi 正常的 9 段结构化提示词（preamble / tools / rules /
              //    docs / addendum / project_context / skills / cwd / 自定义段）
              //    全部被丢弃，模型只看到 C# 给的这段字符串。
              //    工具本身不受影响：tools 是通过 API 的 tools 字段传的。
              //
              //    若想保留 pi 的默认结构，可改为修改可变的事件字段而不返回
              //    systemPrompt，例如：
              //        pi.on("before_agent_start", (event) => {
              //          event.systemPromptOptions.appendSystemPrompt = "...";
              //          event.systemPromptOptions.promptGuidelines = ["..."];
              //        });
              pi.on("before_agent_start", () => ({ systemPrompt }));
            }
            """;
    }

    /// <summary>
    /// 以 UTF-8 无 BOM 写入。无 BOM 很重要：pi 按行读取并逐行 JSON 解析，
    /// 但这里的 .js 是给 jiti 加载的，BOM 可能导致解析异常。
    /// </summary>
    public static void WriteTo(string path, PiRpcConfig cfg)
    {
        File.WriteAllText(path, Build(cfg), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
