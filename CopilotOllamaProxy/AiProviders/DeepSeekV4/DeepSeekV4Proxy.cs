using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CopilotOllamaProxy.AiProviders.DeepSeekV4;

public class DeepSeekV4Proxy
{
    private readonly string BASE_URL;
    private readonly string MODEL;
    private readonly string ExpectedRequestedMODEL;
    private readonly string API_KEY;
    private readonly JsonSerializerOptions JsonOpts;
    private readonly ILogger<DeepSeekV4Proxy> _logger;

    // ─── State ───────────────────────────────────────────────────────────
    ConcurrentDictionary<string, string> ReasoningCache = new(StringComparer.Ordinal);
    long _assistantMsgCounter = 0;


    public static Task CreateAndeRun(WebApplicationBuilder builder, string baseUrl, string apiKey, DeepSeekV4ModelSettings modelSettings, JsonSerializerOptions jsonOpts, CancellationToken cancellationToken = default)
    {

        builder.WebHost.UseUrls($"http://0.0.0.0:{modelSettings.Port}");

        // Clear default logging providers and force console logger with ANSI colors
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(opts =>
        {
            opts.ColorBehavior = Microsoft.Extensions.Logging.Console.LoggerColorBehavior.Enabled;
            opts.SingleLine = false;
            opts.TimestampFormat = null;
        });

        var app = builder.Build();

        var logger = app.Services.GetRequiredService<ILogger<DeepSeekV4Proxy>>();
        var proxy = new DeepSeekV4Proxy(app, baseUrl, apiKey, modelSettings, jsonOpts, logger);




        // ─── Start ───────────────────────────────────────────────────────────
        var version = "  Version: 2026.05.09";
        var model = $"  Model:   {(modelSettings.CopilotId != modelSettings.UnderlyingId ? $"{modelSettings.CopilotId} -> {modelSettings.UnderlyingId}" : modelSettings.CopilotId)}";
        var url = $"  URL:     http://localhost:{modelSettings.Port}/v1";
        Console.WriteLine($"╔{new string('═', 70)}╗");
        Console.WriteLine($"║{"  DeepSeek Copilot Proxy (Ultra)",-70}║");
        Console.WriteLine($"╠{new string('═', 70)}╣");
        Console.WriteLine($"║{version,-70}║");
        Console.WriteLine($"║{model,-70}║");
        Console.WriteLine($"║{url,-70}║");
        Console.WriteLine($"╚{new string('═', 70)}╝");
        return app.RunAsync(cancellationToken);
    }

    private DeepSeekV4Proxy(WebApplication app, string baseUrl, string apiKey, DeepSeekV4ModelSettings modelSettings, JsonSerializerOptions jsonOpts, ILogger<DeepSeekV4Proxy> logger)
    {
        BASE_URL = baseUrl;
        MODEL = modelSettings.UnderlyingId;
        ExpectedRequestedMODEL = modelSettings.CopilotId;
        API_KEY = apiKey;
        JsonOpts = jsonOpts;
        _logger = logger;


        // Max-performance HTTP handler
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            MaxConnectionsPerServer = 256,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            PreAuthenticate = false
        };

        var httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(5),
            BaseAddress = new Uri(BASE_URL)
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", API_KEY);
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // ─── GET /v1/models ──────────────────────────────────────────────────
        app.MapGet("/v1/models", () =>
        Results.Json(new
        {
            @object = "list",
            data = new string[]
            {
                //new { id = MODEL, @object = "model", created = 1700000000, owned_by = "deepseek" },
                //new { id = "deepseek-chat", @object = "model", created = 1700000000, owned_by = "deepseek" }
        }
        }, JsonOpts));

        // ─── GET /health ─────────────────────────────────────────────────────
        app.MapGet("/health", () => Results.Ok(new { status = "ok", model = MODEL }));

        // ─── POST /v1/chat/completions ──────────────────────────────────────
        app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;

            try
            {
                // Read and parse request
                using var bodyReader = new StreamReader(ctx.Request.Body, Encoding.UTF8, false, 1024);
                var rawBody = await bodyReader.ReadToEndAsync(ct);

                using var doc = JsonDocument.Parse(rawBody);
                var root = doc.RootElement;
                var isStream = root.TryGetProperty("stream", out var sp) && sp.GetBoolean();

                // Inject cached reasoning_content and override model
                var modified = ModifyRequest(doc);
                var bodyText = modified ?? rawBody;

                // For non-streaming: direct proxy via HttpClient
                if (!isStream)
                {
                    try
                    {
                        using var content = new StringContent(bodyText, Encoding.UTF8, "application/json");
                        using var response = await httpClient.SendAsync(
                            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = content },
                            ct);

                        var respBody = await response.Content.ReadAsStringAsync(ct);

                        if (response.IsSuccessStatusCode)
                            CacheReasoningFromResponse(respBody);

                        ctx.Response.StatusCode = (int)response.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(respBody, ct);
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception during non-streaming HTTP call to upstream API");
                        ctx.Response.StatusCode = 500;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                message = $"Proxy error: {ex.Message}",
                                type = "proxy_exception",
                                details = ex.GetType().Name
                            }
                        }, JsonOpts), ct);
                        return;
                    }
                }

                // ── Streaming ──
                try
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.CacheControl = "no-cache";
                    ctx.Response.Headers["X-Accel-Buffering"] = "no";

                    using var reqContent = new StringContent(bodyText, Encoding.UTF8, "application/json");
                    using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                    {
                        Content = reqContent
                    };
                    upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                    using var upstreamResp = await httpClient.SendAsync(
                        upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

                    if (!upstreamResp.IsSuccessStatusCode)
                    {
                        var errBody = await upstreamResp.Content.ReadAsStringAsync(ct);
                        ctx.Response.StatusCode = (int)upstreamResp.StatusCode;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(errBody, ct);
                        return;
                    }

                    await StreamAndCache(upstreamResp, ctx.Response, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception during streaming HTTP call to upstream API");

                    if (!ctx.Response.HasStarted)
                    {
                        ctx.Response.StatusCode = 500;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                message = $"Proxy error: {ex.Message}",
                                type = "proxy_exception",
                                details = ex.GetType().Name
                            }
                        }, JsonOpts), ct);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in /v1/chat/completions endpoint");

                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            message = $"Internal error: {ex.Message}",
                            type = "internal_exception",
                            details = ex.GetType().Name
                        }
                    }, JsonOpts), ct);
                }
            }
        });

        // ─── Ollama /api/tags ────────────────────────────────────────────────
        app.MapGet("/api/tags", () =>
            Results.Json(new
            {
                models = new string[]
                {
                    //new
                    //{
                    //    name = MODEL, model = MODEL,
                    //    modified_at = DateTime.UtcNow.ToString("o"), size = 0L,
                    //    digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                    //    details = new { parent_model = "", format = "api", family = "deepseek",
                    //        families = Array.Empty<string>(), parameter_size = "", quantization_level = "" }
                    //}
                }
            }, JsonOpts));

        // ─── Ollama /api/chat ────────────────────────────────────────────────
        app.MapPost("/api/chat", async (HttpContext ctx) =>
        {
            var ct = ctx.RequestAborted;

            try
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var isStream = root.TryGetProperty("stream", out var sp) && sp.GetBoolean();

                // Convert Ollama messages to OpenAI format
                var messages = new List<object>();
                if (root.TryGetProperty("messages", out var omsgs))
                {
                    foreach (var msg in omsgs.EnumerateArray())
                    {
                        var role = msg.GetProperty("role").GetString()!;
                        var text = msg.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

                        object content;
                        if (msg.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
                        {
                            var parts = new List<object> { new { type = "text", text } };
                            foreach (var img in imgs.EnumerateArray())
                            {
                                var url = img.GetString()!;
                                if (!url.StartsWith("data:") && !url.StartsWith("http"))
                                    url = $"data:image/png;base64,{url}";
                                parts.Add(new { type = "image_url", image_url = new { url } });
                            }
                            content = parts;
                        }
                        else content = text;

                        messages.Add(new { role, content });
                    }
                }

                var reqObj = new Dictionary<string, object?>
                {
                    ["model"] = MODEL,
                    ["messages"] = messages,
                    ["stream"] = isStream,
                    ["max_tokens"] = 8192
                };
                if (root.TryGetProperty("tools", out var tools))
                    reqObj["tools"] = tools;

                var reqJson = JsonSerializer.Serialize(reqObj, JsonOpts);
                using var reqContent = new StringContent(reqJson, Encoding.UTF8, "application/json");

                if (!isStream)
                {
                    try
                    {
                        using var resp = await httpClient.SendAsync(
                            new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = reqContent }, ct);
                        var respBody = await resp.Content.ReadAsStringAsync(ct);

                        if (!resp.IsSuccessStatusCode)
                        {
                            ctx.Response.StatusCode = (int)resp.StatusCode;
                            await ctx.Response.WriteAsync(respBody, ct);
                            return;
                        }

                        CacheReasoningFromResponse(respBody);

                        using var odoc = JsonDocument.Parse(respBody);
                        var msg = odoc.RootElement.GetProperty("choices")[0].GetProperty("message");
                        var ollamaResp = new Dictionary<string, object?>
                        {
                            ["model"] = MODEL,
                            ["created_at"] = DateTime.UtcNow.ToString("o"),
                            ["message"] = new Dictionary<string, object?>
                            {
                                ["role"] = "assistant",
                                ["content"] = msg.GetProperty("content").GetString() ?? ""
                            },
                            ["done"] = true,
                            ["done_reason"] = "stop"
                        };
                        if (msg.TryGetProperty("tool_calls", out var tcs))
                            ((Dictionary<string, object?>)ollamaResp["message"]!)["tool_calls"] = tcs;

                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(ollamaResp, JsonOpts), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception during Ollama non-streaming HTTP call to upstream API");
                        ctx.Response.StatusCode = 500;
                        ctx.Response.ContentType = "application/json";
                        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                        {
                            error = new
                            {
                                message = $"Proxy error: {ex.Message}",
                                type = "proxy_exception",
                                details = ex.GetType().Name
                            }
                        }, JsonOpts), ct);
                        return;
                    }
                }
                else
                {
                    try
                    {
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "text/event-stream";
                        ctx.Response.Headers["X-Accel-Buffering"] = "no";

                        using var upstreamReq = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                        {
                            Content = reqContent
                        };
                        upstreamReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

                        using var upstreamResp = await httpClient.SendAsync(
                            upstreamReq, HttpCompletionOption.ResponseHeadersRead, ct);

                        if (!upstreamResp.IsSuccessStatusCode)
                            return;

                        await StreamAndCache(upstreamResp, ctx.Response, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Exception during Ollama streaming HTTP call to upstream API");

                        if (!ctx.Response.HasStarted)
                        {
                            ctx.Response.StatusCode = 500;
                            ctx.Response.ContentType = "application/json";
                            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                            {
                                error = new
                                {
                                    message = $"Proxy error: {ex.Message}",
                                    type = "proxy_exception",
                                    details = ex.GetType().Name
                                }
                            }, JsonOpts), ct);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected exception in /api/chat endpoint");

                if (!ctx.Response.HasStarted)
                {
                    ctx.Response.StatusCode = 500;
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = new
                        {
                            message = $"Internal error: {ex.Message}",
                            type = "internal_exception",
                            details = ex.GetType().Name
                        }
                    }, JsonOpts), ct);
                }
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // Local functions (capture ReasoningCache, _assistantMsgCounter, httpClient)
    // ══════════════════════════════════════════════════════════════════════

    string? ModifyRequest(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (!root.TryGetProperty("messages", out var msgs))
            return null;

        int idx = 0;
        bool modified = false;
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms);

        w.WriteStartObject();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.NameEquals("model"))
            {
                var requestedModel = prop.Value.GetString();
                if (requestedModel != ExpectedRequestedMODEL) throw new InvalidOperationException($"Invalid model requested: {requestedModel}. Expected: {ExpectedRequestedMODEL}");
                w.WriteString("model", MODEL);
                modified = requestedModel != MODEL;
                continue;
            }
            //else if (prop.NameEquals("max_completion_tokens"))
            //{
            //    var maxTokens = prop.Value.GetInt32();
            //    w.WriteNumber("max_tokens", maxTokens);
            //    modified = true;
            //    continue;
            //}
            else if (!prop.NameEquals("messages"))
            {
                prop.WriteTo(w);
                continue;
            }

            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (var msg in msgs.EnumerateArray())
            {
                var role = msg.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role == "assistant")
                {
                    bool hasTc = msg.TryGetProperty("tool_calls", out var tcArr) && tcArr.GetArrayLength() > 0;
                    string? key = null;

                    if (hasTc)
                    {
                        var ids = new List<string>();
                        foreach (var tc in tcArr.EnumerateArray())
                            if (tc.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String)
                                ids.Add(idE.GetString()!);
                        if (ids.Count > 0) key = $"toolcall:{string.Join("|", ids)}";
                    }
                    else
                    {
                        key = $"assistant:{idx++}";
                    }

                    if (key != null && ReasoningCache.TryGetValue(key, out var rc))
                    {
                        bool needsInject = !msg.TryGetProperty("reasoning_content", out var exRc)
                            || exRc.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(exRc.GetString());

                        if (needsInject)
                        {
                            w.WriteStartObject();
                            foreach (var mp in msg.EnumerateObject())
                                mp.WriteTo(w);
                            w.WriteString("reasoning_content", rc);
                            w.WriteEndObject();
                            modified = true;
                            continue;
                        }
                    }
                }
                msg.WriteTo(w);
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.Flush();

        return modified ? Encoding.UTF8.GetString(ms.ToArray()) : null;
    }

    void CacheReasoningFromResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return;

            var msg = choices[0].TryGetProperty("message", out var m) ? m : choices[0].TryGetProperty("delta", out var d) ? d : default;
            if (msg.ValueKind == JsonValueKind.Undefined) return;
            if (!msg.TryGetProperty("reasoning_content", out var rc) || string.IsNullOrEmpty(rc.GetString())) return;

            string key;
            if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.GetArrayLength() > 0)
            {
                var ids = new List<string>();
                foreach (var tc in tcs.EnumerateArray())
                    if (tc.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String)
                        ids.Add(idE.GetString()!);
                key = $"toolcall:{string.Join("|", ids)}";
            }
            else
            {
                key = $"assistant:{Interlocked.Increment(ref _assistantMsgCounter) - 1}";
            }

            var reasoning = rc.GetString();

            ReasoningCache[key] = reasoning!;

            if (!string.IsNullOrEmpty(reasoning))
            {
                _logger.LogInformation($"Caching reasoning_content for key '{key}' (length: {reasoning.Length})");
                _logger.LogInformation(@$"*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*
*?*?* reasoning_content:                        *?*?*
*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*
{reasoning}
*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*");
            }
        }
        catch { /* cache errors are non-critical */ }
    }

    async Task StreamAndCache(HttpResponseMessage upstream, HttpResponse downstream, CancellationToken ct)
    {
        using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(upstreamStream);
        await using var writer = new StreamWriter(downstream.Body, leaveOpen: true) { NewLine = "\n" };

        var sb = new StringBuilder(4096);
        List<string>? tcIds = null;
        bool hasTc = false;
        int? asstIdx = null;

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("data:"))
            {
                var json = line.Substring(5).TrimStart();
                if (json.Length > 0 && json != "[DONE]")
                {
                    try
                    {
                        using var chunk = JsonDocument.Parse(json);
                        var cr = chunk.RootElement;
                        if (cr.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                        {
                            var delta = choices[0].TryGetProperty("delta", out var d) ? d
                                : choices[0].TryGetProperty("message", out var mm) ? mm : default;

                            if (delta.ValueKind != JsonValueKind.Undefined)
                            {
                                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                                {
                                    var rct = rc.GetString();
                                    if (!string.IsNullOrEmpty(rct)) sb.Append(rct);
                                }
                                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                                {
                                    hasTc = true;
                                    foreach (var tc in tcs.EnumerateArray())
                                    {
                                        if (tc.TryGetProperty("id", out var idE) && idE.ValueKind == JsonValueKind.String)
                                        {
                                            tcIds ??= new List<string>();
                                            var id = idE.GetString()!;
                                            if (!tcIds.Contains(id)) tcIds.Add(id);
                                        }
                                    }
                                }
                                if (choices[0].TryGetProperty("finish_reason", out var fr) && fr.ValueKind != JsonValueKind.Null)
                                {
                                    var reasoning = sb.ToString();
                                    if (!string.IsNullOrEmpty(reasoning))
                                    {
                                        string key;
                                        if (hasTc && tcIds != null && tcIds.Count > 0)
                                            key = $"toolcall:{string.Join("|", tcIds)}";
                                        else
                                            key = $"assistant:{asstIdx ?? (int)(Interlocked.Increment(ref _assistantMsgCounter) - 1)}";
                                        ReasoningCache[key] = reasoning;

                                        _logger.LogInformation($"Caching reasoning_content for key '{key}' (length: {reasoning.Length})");
                                        _logger.LogInformation(@$"*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*
*?*?* reasoning_content:                        *?*?*
*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*
{reasoning}
*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*?*");
                                    }
                                }
                            }
                        }
                    }
                    catch { /* parse errors are non-critical */ }

                    // Pass-through all data lines unmodified
                    await writer.WriteAsync("data: ");
                    await writer.WriteAsync(json);
                    await writer.WriteLineAsync();
                }
                else
                {
                    await writer.WriteLineAsync(line);
                }
            }
            else
            {
                await writer.WriteLineAsync(line);
            }

            await writer.FlushAsync(ct);
        }
    }
}
