using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace CsharpPiRpcDemo;

/// <summary>
/// 自检用的本地假 provider：一个最小可用的 OpenAI 兼容 /v1/chat/completions 端点。
///
/// 它的存在是为了让这个 demo **不需要任何真实 API key** 就能端到端验证
/// "C# → pi RPC 子进程 → bootstrap 扩展注入的 provider" 整条链路，
/// 并顺便抓取 pi 实际发出的 Authorization / model / system prompt，
/// 用来证明配置确实来自 C# 代码。
///
/// 【它不改变 pi 的行为】
/// 它只是把同一套启动参数里的 baseUrl 换成了本地地址，让请求能打到我们自己这里。
/// 因此自检通过即可证明：启动参数、扩展注入、RPC 交互、事件流处理都是对的。
/// 但它**不能**验证模型侧的真实兼容性 —— 除了不校验 key、不做真实推理之外，
/// 它也不会返回工具调用(tool_calls)、推理内容(reasoning_content)等真实 provider
/// 会返回的结构，更不会触发工具执行路径。所以真实 provider 仍需单独验证。
/// </summary>
public sealed class FakeOpenAiProvider : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private int _requestCount;

    private FakeOpenAiProvider(int port) => _port = port;

    public string BaseUrl => $"http://127.0.0.1:{_port}/v1";
    public string? SeenAuthorization { get; private set; }
    public string? SeenModel { get; private set; }
    public string? SeenSystemPrompt { get; private set; }
    public int RequestCount => Volatile.Read(ref _requestCount);

    public static FakeOpenAiProvider Start()
    {
        var port = GetFreePort();
        var provider = new FakeOpenAiProvider(port);
        provider._listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        provider._listener.Start();
        _ = Task.Run(provider.LoopAsync);
        return provider;
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // listener 已关闭
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            Interlocked.Increment(ref _requestCount);
            SeenAuthorization = ctx.Request.Headers["Authorization"];

            var doc = JsonNode.Parse(body) as JsonObject;
            SeenModel = doc?["model"]?.GetValue<string>();
            if (doc?["messages"] is JsonArray messages)
            {
                foreach (var message in messages)
                {
                    var role = message?["role"]?.GetValue<string>();
                    if (role is "system" or "developer")
                    {
                        SeenSystemPrompt = message?["content"]?.ToString();
                    }
                }
            }

            var wantsStream = body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase);
            if (wantsStream)
            {
                await WriteStreamAsync(ctx).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(ctx).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 自检期间忽略。
        }
        finally
        {
            try
            {
                ctx.Response.Close();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
    }

    private const string Reply = "本地假 provider 收到了请求，链路已打通。";

    private async Task WriteStreamAsync(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        var output = ctx.Response.OutputStream;

        async Task WriteAsync(string text)
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes(text)).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }

        var model = SeenModel ?? "selftest";
        var first = new JsonObject
        {
            ["id"] = "chatcmpl-selftest",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply },
                    ["finish_reason"] = null,
                },
            },
        };
        await WriteAsync("data: " + first.ToJsonString() + "\n\n").ConfigureAwait(false);

        var last = new JsonObject
        {
            ["id"] = "chatcmpl-selftest",
            ["object"] = "chat.completion.chunk",
            ["created"] = 0,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = new JsonObject(),
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 1,
                ["completion_tokens"] = 1,
                ["total_tokens"] = 2,
            },
        };
        await WriteAsync("data: " + last.ToJsonString() + "\n\n").ConfigureAwait(false);
        await WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
    }

    private async Task WriteJsonAsync(HttpListenerContext ctx)
    {
        var payload = new JsonObject
        {
            ["id"] = "chatcmpl-selftest",
            ["object"] = "chat.completion",
            ["created"] = 0,
            ["model"] = SeenModel ?? "selftest",
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = Reply },
                    ["finish_reason"] = "stop",
                },
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 1,
                ["completion_tokens"] = 1,
                ["total_tokens"] = 2,
            },
        };

        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }
}
