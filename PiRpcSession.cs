using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsharpPiRpcDemo;

/// <summary>
/// pi RPC 模式的子进程封装。
///
/// 【这个类不改变 pi 的任何行为】
/// 它只负责"怎么跟 pi 说话"：启动进程、按 JSONL 分帧、按 id 关联命令与响应、
/// 把事件转成 C# 回调。所有会改变 pi 行为的因素都在别处：
///   · 启动参数（--no-* / --extension / --provider / --model）→ Program.cs 的 piArgs
///   · 环境变量（PI_CODING_AGENT_DIR / PI_OFFLINE）→ Program.cs 的 env
///   · 扩展内部的 pi.registerProvider / before_agent_start → PiRpcConfig.BootstrapScript
/// 所以调试"行为异常"时，先看那三处，而不是这里。
///
/// 协议要点：
///  - 严格 JSONL：一行一个完整 JSON 对象，以 \n 结尾。
///  - stdin 发命令，stdout 收 response 和事件；stderr 是日志，不是协议数据。
///  - 命令带 id，response 回带同一个 id，必须按 id 关联（命令是并发处理的）。
///  - prompt 返回成功只代表"已受理"，要等 agent_settled 才知道 pi 不会再自动继续。
/// </summary>
public sealed class PiRpcSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly StreamReader _stderr;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly StringBuilder _stderrBuffer = new();
    private int _nextId;

    /// <summary>收到非 response 的记录（会话事件）时触发。</summary>
    public event Action<JsonNode>? EventReceived;

    public string StderrText
    {
        get { lock (_stderrBuffer) return _stderrBuffer.ToString(); }
    }

    private PiRpcSession(Process process)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _stderr = process.StandardError;
        _ = Task.Run(PumpStdoutAsync);
        _ = Task.Run(PumpStderrAsync);
    }

    public static PiRpcSession Start(
        PiRpcConfig cfg,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env,
        string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        if (!string.IsNullOrWhiteSpace(cfg.PiCliPath))
        {
            // 推荐用于生产：用 node 跑你自己打包的 pi，绝不依赖 PATH。
            psi.FileName = "node";
            psi.ArgumentList.Add(cfg.PiCliPath!);
        }
        else
        {
            psi.FileName = cfg.PiCommand;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
        {
            if (value is null)
            {
                psi.Environment.Remove(key);
            }
            else
            {
                psi.Environment[key] = value;
            }
        }

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"无法启动：{psi.FileName}");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"找不到可执行文件 '{psi.FileName}'。请确认 pi 已安装并在 PATH 中，" +
                $"或设置 PiRpcConfig.PiCliPath 指向你打包的 dist/cli.js。", ex);
        }

        return new PiRpcSession(process);
    }

    /// <summary>发一条命令并等待对应 id 的 response。</summary>
    public async Task<JsonNode> SendAsync(JsonObject command, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _nextId).ToString();
        command["id"] = id;

        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        await _stdin.WriteAsync(command.ToJsonString() + "\n").ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);

        return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
    }

    /// <summary>
    /// 发 prompt、流式回调事件、等到 agent_settled 才返回。
    /// </summary>
    public async Task PromptAsync(string message, TimeSpan timeout, Action<JsonNode>? onEvent = null)
    {
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(JsonNode evt)
        {
            onEvent?.Invoke(evt);
            if (Str(evt, "type") == "agent_settled")
            {
                settled.TrySetResult();
            }
        }

        EventReceived += Handler;
        try
        {
            var response = await SendAsync(
                new JsonObject { ["type"] = "prompt", ["message"] = message },
                timeout).ConfigureAwait(false);

            if (response["success"]?.GetValue<bool>() != true)
            {
                throw new InvalidOperationException("prompt 被拒绝：" + response["error"]);
            }

            var disposition = response["data"] is JsonNode data ? Str(data, "disposition") : null;
            Console.WriteLine($"   [prompt 受理] disposition={disposition}");

            // disposition=handled 表示没有启动 run（被扩展命令消费），此时不会有 agent_settled。
            if (disposition == "handled")
            {
                return;
            }

            await settled.Task.WaitAsync(timeout).ConfigureAwait(false);
        }
        finally
        {
            EventReceived -= Handler;
        }
    }

    internal static string? Str(JsonNode? node, string key)
    {
        if (node?[key] is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }
        return null;
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            while (true)
            {
                // StreamReader.ReadLine 只按 \r\n / \n / \r 切分，
                // 不会把 U+2028 / U+2029 当换行 —— 正好符合 JSONL 分帧要求。
                // （Node.js 的 readline 会，所以文档特意警告不要用它。）
                var line = await _stdout.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.Length == 0)
                {
                    continue;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (node is null)
                {
                    continue;
                }

                if (Str(node, "type") == "response")
                {
                    var id = Str(node, "id");
                    if (id is not null && _pending.TryRemove(id, out var tcs))
                    {
                        tcs.TrySetResult(node);
                        continue;
                    }
                }

                EventReceived?.Invoke(node);
            }
        }
        catch (Exception)
        {
            // stdout 关闭，进程退出中。
        }
        finally
        {
            var error = new InvalidOperationException(
                "pi 进程已退出，仍有命令未收到响应。stderr：" + Environment.NewLine + StderrText);
            foreach (var kvp in _pending)
            {
                kvp.Value.TrySetException(error);
            }
            _pending.Clear();
        }
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            while (true)
            {
                var line = await _stderr.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                lock (_stderrBuffer)
                {
                    _stderrBuffer.AppendLine(line);
                }
            }
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 关闭子进程 stdin = 请求 pi 有序退出。
        try
        {
            _stdin.Close();
        }
        catch (Exception)
        {
            // 忽略。
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
