using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CLIProxyAPI_GUI
{
    public class LocalProxyServer
    {
        /// <summary>
        /// Google Cloud Code (Antigravity) 上游端点。对齐官方 CPA 默认值使用 daily 通道：
        /// daily 与 prod 两通道的配额/限流独立计量，混用 prod 通道会在账号额度充足时误报 429。
        /// </summary>
        public const string GoogleCloudCodeBaseUrl = "https://daily-cloudcode-pa.googleapis.com";

        // ===== Antigravity Hub 动态 User-Agent（对齐 CPA internal/misc/antigravity_version.go）=====
        private const string AntigravityFallbackVersion = "2.9.1";
        private const string AntigravityHubPlatform = "darwin/arm64";
        private const string AntigravityHubManifestUrl = "https://antigravity-hub-auto-updater-974169037036.us-central1.run.app/manifest/latest-arm64-mac.yml";
        private static readonly TimeSpan AntigravityVersionCacheTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan AntigravityVersionRefreshInterval = TimeSpan.FromHours(3);

        private static volatile string _antigravityVersion = AntigravityFallbackVersion;
        private static readonly HttpClient ManifestHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static int _manifestLoopStarted;

        /// <summary>静态日志回调（由实例在启动时注册，供版本后台刷新线程输出日志）</summary>
        internal static Action<string>? StaticLogCallback { get; set; }

        /// <summary>动态 Antigravity Hub User-Agent：manifest 拉取最新版本，6 小时缓存，失败回退 2.9.1</summary>
        public static string AntigravityUserAgent => $"antigravity/hub/{_antigravityVersion} {AntigravityHubPlatform}";

        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly HttpClient _httpClient;

        public int Port { get; set; } = 8317;
        public string ApiKey { get; set; } = "";
        public bool IsRunning => _listener?.IsListening ?? false;

        public Action<string>? LogCallback { get; set; }

        /// <summary>
        /// 获取可用于代理转发的有效 Gemini 账号凭证。传入已尝试（或需排除）的邮箱集合，返回未处于冷却期的可用账号。
        /// </summary>
        public Func<IReadOnlySet<string>?, Task<(string accessToken, string email, string projectId)?>>? GetGeminiTokenAsync { get; set; }

        /// <summary>
        /// 账号触发 429 RESOURCE_EXHAUSTED 时的回调通知（参数：账号邮箱, 建议冷却时长）
        /// </summary>
        public Action<string, TimeSpan>? OnAccountRateLimited { get; set; }

        public LocalProxyServer()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        }

        private void Log(string message)
        {
            LogCallback?.Invoke($"[本地代理 API] {message}");
        }

        private static void StaticLog(string message)
        {
            try { StaticLogCallback?.Invoke($"[本地代理 API] {message}"); } catch { }
        }

        /// <summary>启动 Antigravity Hub 版本后台刷新循环（进程内仅启动一次，每 3 小时刷新，缓存有效期 6 小时）</summary>
        private static void EnsureAntigravityVersionRefreshLoop()
        {
            if (Interlocked.CompareExchange(ref _manifestLoopStarted, 1, 0) != 0) return;
            Task.Run(AntigravityVersionRefreshLoopAsync);
        }

        private static async Task AntigravityVersionRefreshLoopAsync()
        {
            while (true)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, AntigravityHubManifestUrl);
                    req.Headers.TryAddWithoutValidation("User-Agent", "electron-builder");
                    req.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
                    using var resp = await ManifestHttpClient.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        string body = await resp.Content.ReadAsStringAsync();
                        string? version = ParseManifestVersion(body);
                        if (version != null && version != _antigravityVersion)
                        {
                            _antigravityVersion = version;
                            StaticLog($"[UA 版本] 已从 Antigravity Hub manifest 拉取最新版本 {version}，User-Agent 更新为 {AntigravityUserAgent}");
                        }
                        else if (version != null)
                        {
                            StaticLog($"[UA 版本] Antigravity Hub manifest 版本仍为 {version}，无需更新");
                        }
                        else
                        {
                            StaticLog("[UA 版本] manifest 响应中未找到有效 version 字段，保持当前版本");
                        }
                    }
                    else
                    {
                        StaticLog($"[UA 版本] manifest 拉取失败 (HTTP {(int)resp.StatusCode})，保持版本 {_antigravityVersion}");
                    }
                }
                catch (Exception ex)
                {
                    StaticLog($"[UA 版本] manifest 拉取异常: {ex.Message}，保持版本 {_antigravityVersion}");
                }
                await Task.Delay(AntigravityVersionRefreshInterval);
            }
        }

        /// <summary>解析 electron-builder 更新 manifest（YAML）中的 version 字段，并校验为 x.y.z 纯数字版本号</summary>
        private static string? ParseManifestVersion(string manifestBody)
        {
            if (string.IsNullOrEmpty(manifestBody)) return null;
            foreach (string rawLine in manifestBody.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("version:", StringComparison.OrdinalIgnoreCase)) continue;
                string version = line.Substring("version:".Length).Trim().Trim('"', '\'');
                if (string.IsNullOrEmpty(version)) return null;

                string[] parts = version.Split('.');
                if (parts.Length != 3) return null;
                foreach (string part in parts)
                {
                    if (part.Length == 0) return null;
                    foreach (char ch in part)
                        if (ch < '0' || ch > '9') return null;
                }
                return version;
            }
            return null;
        }

        public bool Start()
        {
            if (IsRunning) return true;

            try
            {
                StaticLogCallback ??= LogCallback;
                EnsureAntigravityVersionRefreshLoop();
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Prefixes.Add($"http://localhost:{Port}/");
                _listener.Start();

                _cts = new CancellationTokenSource();

                Task.Run(() => ListenLoopAsync(_cts.Token));

                Log($"服务已成功启动！监听地址: http://127.0.0.1:{Port}/v1");
                return true;
            }
            catch (Exception ex)
            {
                Log($"启动失败 (端口 {Port}): {ex.Message}");
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                if (_listener != null && _listener.IsListening)
                {
                    _listener.Stop();
                    _listener.Close();
                }
            }
            catch { }
            finally
            {
                _listener = null;
                _cts = null;
                Log("服务已停止");
            }
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener != null && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    _ = ProcessRequestAsync(context);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        Log($"接收连接异常: {ex.Message}");
                }
            }
        }

        /// <summary>HTTP (HttpListener) 请求入口：适配为 ProxyRequest/ProxyResponse 后走共享业务核心</summary>
        private async Task ProcessRequestAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var resp = context.Response;

            var proxReq = new ProxyRequest
            {
                HttpMethod = req.HttpMethod,
                RawUrl = req.RawUrl ?? "",
                AbsolutePath = req.Url?.AbsolutePath ?? "/",
                QueryString = req.Url?.Query.TrimStart('?') ?? "",
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                HasEntityBody = req.HasEntityBody,
                ContentType = req.ContentType ?? "",
                ContentEncoding = req.ContentEncoding ?? Encoding.UTF8,
                InputStream = req.InputStream,
            };
            if (req.Headers != null)
            {
                foreach (string? k in req.Headers.AllKeys)
                    if (k != null) proxReq.Headers[k] = req.Headers[k] ?? "";
            }
            if (req.QueryString != null)
            {
                foreach (string? k in req.QueryString.AllKeys)
                    if (k != null) proxReq.Query[k] = req.QueryString[k] ?? "";
            }

            var syncStream = new HttpListenerSyncStream(resp);
            var proxResp = new ProxyResponse
            {
                OutputStream = syncStream,
                CloseAsyncAction = () =>
                {
                    try
                    {
                        syncStream.SyncNow();
                        context.Response.Close();
                    }
                    catch { }
                    return Task.CompletedTask;
                },
            };
            syncStream.Attach(proxResp);

            await ProcessRequestCoreAsync(proxReq, proxResp);
        }

        /// <summary>共享业务核心：HTTP 与 HTTPS 两条传输通道共用的请求分发/处理逻辑</summary>
        private async Task ProcessRequestCoreAsync(ProxyRequest req, ProxyResponse resp)
        {
            string rawPath = req.AbsolutePath;

            // CORS 支持
            resp.Headers["Access-Control-Allow-Origin"] = "*";
            resp.Headers["Access-Control-Allow-Headers"] = "*";
            resp.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS, PUT, DELETE";

            if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                resp.StatusCode = 200;
                await resp.CloseAsync();
                return;
            }

            // 身份校验 (API Key)
            if (!string.IsNullOrWhiteSpace(ApiKey))
            {
                string authHeader = req.Headers.TryGetValue("Authorization", out var ah) ? ah : "";
                string queryKey = (req.Query.TryGetValue("key", out var qk) ? qk : null)
                                  ?? (req.Query.TryGetValue("api_key", out var qak) ? qak : "");
                string token = "";

                if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    token = authHeader.Substring(7).Trim();
                else if (!string.IsNullOrEmpty(queryKey))
                    token = queryKey.Trim();

                if (!string.Equals(token, ApiKey.Trim(), StringComparison.Ordinal))
                {
                    Log($"鉴权失败: 客户端传入 Key [{token}] 与设置的 API Key 不匹配");
                    resp.StatusCode = (int)HttpStatusCode.Unauthorized;
                    byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\": {\"message\": \"Invalid API Key\", \"type\": \"invalid_request_error\"}}");
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.ContentLength64 = errBytes.Length;
                    await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    await resp.CloseAsync();
                    return;
                }
            }

            string path = rawPath.TrimEnd('/').ToLowerInvariant();
            if (string.IsNullOrEmpty(path)) path = "/";

            try
            {
                // 健康检查
                if (path == "/" || path == "/health" || path == "/v1/health" || path.EndsWith("/health"))
                {
                    resp.StatusCode = 200;
                    byte[] msg = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"service\":\"Haodo Gemini Local Proxy API\"}");
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.ContentLength64 = msg.Length;
                    await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                    await resp.CloseAsync();
                    return;
                }

                // OpenAI 向量嵌入 /v1/embeddings 占位适配（防止第三方客户端探测报错）
                if (path == "/v1/embeddings" || path == "/embeddings" || path.EndsWith("/embeddings"))
                {
                    resp.StatusCode = 200;
                    string dummyEmbed = "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.0]}],\"model\":\"text-embedding-3-small\"}";
                    byte[] msg = Encoding.UTF8.GetBytes(dummyEmbed);
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.ContentLength64 = msg.Length;
                    await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                    await resp.CloseAsync();
                    return;
                }

                // 缓存请求体以支持多账号故障转移重试
                string requestBodyText = "";
                byte[]? requestBodyBytes = null;
                if (req.HasEntityBody && req.InputStream != null)
                {
                    using var ms = new MemoryStream();
                    await req.InputStream.CopyToAsync(ms);
                    requestBodyBytes = ms.ToArray();
                    requestBodyText = (req.ContentEncoding ?? Encoding.UTF8).GetString(requestBodyBytes);
                }

                bool isResponsesPath = path == "/v1/responses" || path == "/responses" || path.EndsWith("/responses");
                bool isChatCompletionsPath = path == "/v1/chat/completions" || path == "/chat/completions" || path.EndsWith("/chat/completions") ||
                                            path == "/v1/completions" || path == "/completions" || path.EndsWith("/completions");
                bool isModelsPath = path == "/v1/models" || path == "/models" || path.EndsWith("/models");
                bool isNativeGeminiPath = rawPath.StartsWith("/v1beta/models/", StringComparison.OrdinalIgnoreCase);

                if (isChatCompletionsPath || isResponsesPath || isModelsPath || isNativeGeminiPath)
                {
                    var attemptedEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var retryState = new RateLimitRetryState();

                    while (true)
                    {
                        (string accessToken, string email, string projectId)? tokenResult = null;
                        if (GetGeminiTokenAsync != null)
                        {
                            tokenResult = await GetGeminiTokenAsync(attemptedEmails);
                        }

                        if (tokenResult == null)
                        {
                            if (attemptedEmails.Count > 0)
                            {
                                Log($"[本地代理 429] 尝试过的所有有效账号 ({attemptedEmails.Count} 个) 均已触发配额耗尽 (RESOURCE_EXHAUSTED) 或处于冷却期中");
                                resp.StatusCode = (int)HttpStatusCode.TooManyRequests;
                                byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\": {\"message\": \"All Gemini accounts in Haodo have exhausted their quota / rate limit (429 RESOURCE_EXHAUSTED). Please wait for cooldown or add more accounts.\", \"type\": \"insufficient_quota\", \"code\": 429}}");
                                resp.ContentType = "application/json; charset=utf-8";
                                resp.ContentLength64 = errBytes.Length;
                                await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                                await resp.CloseAsync();
                                return;
                            }
                            else
                            {
                                Log("请求拒绝: 未找到有效的已登录 Gemini 账号或 Token 刷新失败");
                                resp.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                                byte[] errBytes = Encoding.UTF8.GetBytes("{\"error\": {\"message\": \"No valid logged-in Gemini account found in Haodo\", \"type\": \"service_unavailable\"}}");
                                resp.ContentType = "application/json; charset=utf-8";
                                resp.ContentLength64 = errBytes.Length;
                                await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                                await resp.CloseAsync();
                                return;
                            }
                        }

                        string accessToken = tokenResult.Value.accessToken;
                        string accountEmail = tokenResult.Value.email;
                        string projectId = tokenResult.Value.projectId;
                        attemptedEmails.Add(accountEmail);

                        // 1. 模型列表接口
                        if (isModelsPath)
                        {
                            var (modelJson, isRateLimit) = await TryFetchOpenAIModelListJsonAsync(accessToken, projectId);
                            if (isRateLimit)
                            {
                                ApplyAccountRateLimit(accountEmail, TimeSpan.FromSeconds(60), attemptedEmails, retryState);
                                continue;
                            }

                            resp.StatusCode = 200;
                            byte[] msg = Encoding.UTF8.GetBytes(modelJson);
                            resp.ContentType = "application/json; charset=utf-8";
                            resp.ContentLength64 = msg.Length;
                            await resp.OutputStream.WriteAsync(msg, 0, msg.Length);
                            await resp.CloseAsync();
                            return;
                        }

                        // 2. 原生 Gemini 接口转发
                        if (isNativeGeminiPath)
                        {
                            TimeSpan? nativeRateLimit = await HandleNativeGeminiForwardAsync(req, resp, rawPath, accessToken, requestBodyBytes);
                            if (nativeRateLimit.HasValue)
                            {
                                ApplyAccountRateLimit(accountEmail, nativeRateLimit.Value, attemptedEmails, retryState);
                                continue;
                            }
                            return;
                        }

                        // 3. OpenAI 聊天 / Responses 补全接口
                        var (geminiBody, isStream, targetModel) = GeminiProtocolTranslator.ConvertOpenAIToGeminiRequest(requestBodyText, projectId);
                        Log($"[请求] {accountEmail} (Project: {projectId}) -> 模型: {targetModel} | 流式: {isStream} | 路由: {(isResponsesPath ? "responses" : "chat")}");

                        TimeSpan? rateLimitCooldown;
                        if (isResponsesPath)
                        {
                            rateLimitCooldown = isStream
                                ? await HandleResponsesStreamAsync(resp, targetModel, geminiBody, accessToken, accountEmail)
                                : await HandleResponsesNonStreamAsync(resp, targetModel, geminiBody, accessToken, accountEmail);
                        }
                        else if (isStream)
                        {
                            rateLimitCooldown = await HandleStreamChatCompletionAsync(resp, targetModel, geminiBody, accessToken, accountEmail);
                        }
                        else
                        {
                            rateLimitCooldown = await HandleNonStreamChatCompletionAsync(resp, targetModel, geminiBody, accessToken, accountEmail);
                        }

                        if (rateLimitCooldown.HasValue)
                        {
                            ApplyAccountRateLimit(accountEmail, rateLimitCooldown.Value, attemptedEmails, retryState);
                            continue; // 429：按分类冷却后自动故障转移重试
                        }

                        return;
                    }
                }

                // 未匹配到的路由 (标准 JSON 404，不给空响应)
                Log($"未匹配的路由 (404): {rawPath}");
                resp.StatusCode = 404;
                byte[] err404 = Encoding.UTF8.GetBytes($"{{\"error\": {{\"message\": \"Path not found: {rawPath}\", \"type\": \"invalid_request_error\"}}}}");
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = err404.Length;
                await resp.OutputStream.WriteAsync(err404, 0, err404.Length);
                await resp.CloseAsync();
            }
            catch (Exception ex)
            {
                Log($"处理请求错误 ({rawPath}): {ex.Message}");
                try
                {
                    resp.StatusCode = 500;
                    byte[] errBytes = Encoding.UTF8.GetBytes($"{{\"error\": {{\"message\": \"{ex.Message}\"}}}}");
                    resp.ContentType = "application/json; charset=utf-8";
                    await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    await resp.CloseAsync();
                }
                catch { }
            }
        }

        /// <summary>单次请求内的 429 重试状态（限制同账号瞬时重试次数，避免死循环）</summary>
        private sealed class RateLimitRetryState
        {
            public int InstantRetries;
        }

        /// <summary>
        /// 对齐官方 CPA 的 429 分类决策（antigravity_executor）：解析响应中的 retryDelay 与 ErrorInfo reason，
        /// 返回建议冷却时长。TimeSpan.Zero 表示瞬时软限流（retryDelay &lt; 3s），可同账号立即重试。
        /// </summary>
        private static TimeSpan ClassifyRateLimitCooldown(string? responseContent)
        {
            TimeSpan? retryDelay = null;
            string? reason = null;
            try
            {
                if (!string.IsNullOrEmpty(responseContent))
                {
                    using var doc = JsonDocument.Parse(responseContent);
                    retryDelay = FindRetryDelayRecursive(doc.RootElement);
                    reason = FindErrorInfoReasonRecursive(doc.RootElement);
                }
            }
            catch { }

            if (retryDelay.HasValue)
            {
                if (retryDelay.Value < TimeSpan.FromSeconds(3))
                    return TimeSpan.Zero; // 瞬时软限流：同账号立即重试
                if (retryDelay.Value < TimeSpan.FromMinutes(5))
                    return ClampCooldown(retryDelay.Value, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5)); // 短时限流：按 retryDelay 冷却后换号
                return ClampCooldown(retryDelay.Value, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60));     // 配额耗尽：长冷却
            }

            if (!string.IsNullOrEmpty(reason))
            {
                if (reason.Contains("SOFT", StringComparison.OrdinalIgnoreCase))
                    return TimeSpan.Zero;
                if (reason.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase))
                    return TimeSpan.FromSeconds(60);
            }
            return TimeSpan.FromMinutes(30); // 无法识别：按配额耗尽保守处理
        }

        private static TimeSpan ClampCooldown(TimeSpan value, TimeSpan min, TimeSpan max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static TimeSpan? FindRetryDelayRecursive(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("retryDelay") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        if (TryParseGoogleDuration(prop.Value.GetString(), out var delay)) return delay;
                    }
                    var found = FindRetryDelayRecursive(prop.Value);
                    if (found.HasValue) return found;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindRetryDelayRecursive(item);
                    if (found.HasValue) return found;
                }
            }
            return null;
        }

        private static string? FindErrorInfoReasonRecursive(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                bool isErrorInfo = false;
                string? reason = null;
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.NameEquals("@type") && prop.Value.ValueKind == JsonValueKind.String &&
                        (prop.Value.GetString() ?? "").EndsWith("google.rpc.ErrorInfo", StringComparison.OrdinalIgnoreCase))
                    {
                        isErrorInfo = true;
                    }
                    else if (prop.NameEquals("reason") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        reason = prop.Value.GetString();
                    }
                    else
                    {
                        var found = FindErrorInfoReasonRecursive(prop.Value);
                        if (found != null) return found;
                    }
                }
                if (isErrorInfo && !string.IsNullOrEmpty(reason)) return reason;
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    var found = FindErrorInfoReasonRecursive(item);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>解析 Google 时长格式（如 "32s"、"1.5s"、"2m"、"1h"）</summary>
        private static bool TryParseGoogleDuration(string? text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim().ToLowerInvariant();
            double multiplier;
            if (s.EndsWith("s")) multiplier = 1;
            else if (s.EndsWith("m")) multiplier = 60;
            else if (s.EndsWith("h")) multiplier = 3600;
            else return false;
            if (!double.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0)
                return false;
            duration = TimeSpan.FromSeconds(value * multiplier);
            return true;
        }

        /// <summary>
        /// 按 429 分类结果对账号应用冷却：Zero 为瞬时软限流（首次同账号立即重试）；
        /// 否则通知上层冷却并切换账号（故障转移）。
        /// </summary>
        private void ApplyAccountRateLimit(string accountEmail, TimeSpan cooldown, HashSet<string> attemptedEmails, RateLimitRetryState retryState)
        {
            if (cooldown <= TimeSpan.Zero)
            {
                if (retryState.InstantRetries < 1)
                {
                    retryState.InstantRetries++;
                    attemptedEmails.Remove(accountEmail);
                    Log($"[本地代理 429] 账号 {accountEmail} 触发瞬时软限流 (retryDelay < 3s)，同账号立即重试...");
                    return;
                }
                cooldown = TimeSpan.FromSeconds(20); // 已重试过一次仍瞬时受限，改为短冷却换号
            }

            string kind = cooldown >= TimeSpan.FromMinutes(5) ? "配额耗尽 (quota_exhausted)" : "短时限流 (rate_limited)";
            Log($"[本地代理 429] 账号 {accountEmail} {kind}，冷却 {FormatCooldown(cooldown)}；自动故障转移切换账号重试...");
            try
            {
                OnAccountRateLimited?.Invoke(accountEmail, cooldown);
            }
            catch { }
        }

        private static string FormatCooldown(TimeSpan cooldown)
        {
            if (cooldown >= TimeSpan.FromMinutes(1))
                return $"{cooldown.TotalMinutes:0.#} 分钟";
            return $"{cooldown.TotalSeconds:0.#} 秒";
        }

        private static bool IsRateLimitResponse(HttpStatusCode statusCode, string? responseContent)
        {
            if ((int)statusCode == 429) return true;
            if (!string.IsNullOrEmpty(responseContent))
            {
                if (responseContent.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
                    responseContent.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase) ||
                    responseContent.Contains("RATE_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
                    responseContent.Contains("rateLimitExceeded", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private async Task<(string json, bool isRateLimit)> TryFetchOpenAIModelListJsonAsync(string accessToken, string projectId)
        {
            return await GeminiProtocolTranslator.FetchOpenAIModelListJsonAsync(_httpClient, accessToken, projectId);
        }

        private async Task<TimeSpan?> HandleResponsesNonStreamAsync(ProxyResponse resp, string targetModel, string geminiBody, string accessToken, string accountEmail)
        {
            string url = GoogleCloudCodeBaseUrl + "/v1internal:generateContent";
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            httpReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
            httpReq.Content = new StringContent(geminiBody, Encoding.UTF8, "application/json");

            var googleResp = await _httpClient.SendAsync(httpReq);
            if (googleResp.StatusCode == HttpStatusCode.NotFound)
            {
                string? retryModel = GetModelRetryCandidate(targetModel);
                if (retryModel != null)
                {
                    Log($"模型 {targetModel} 404，尝试候选键 {retryModel}");
                    googleResp.Dispose();
                    var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
                    retryReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                    retryReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                    retryReq.Content = new StringContent(ReplaceGeminiBodyModel(geminiBody, retryModel), Encoding.UTF8, "application/json");
                    googleResp = await _httpClient.SendAsync(retryReq);
                    targetModel = retryModel;
                }
            }

            using (googleResp)
            {
                string respContent = await googleResp.Content.ReadAsStringAsync();

                if (IsRateLimitResponse(googleResp.StatusCode, respContent))
                {
                    return ClassifyRateLimitCooldown(respContent);
                }

                if (!googleResp.IsSuccessStatusCode)
                {
                    Log($"Google 返回错误 {(int)googleResp.StatusCode}: {respContent}");
                    resp.StatusCode = (int)googleResp.StatusCode;
                    byte[] errBytes = Encoding.UTF8.GetBytes(respContent);
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.ContentLength64 = errBytes.Length;
                    await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    await resp.CloseAsync();
                    return null;
                }

                string openAiResp = GeminiProtocolTranslator.ConvertGeminiToResponsesResponse(respContent, targetModel);
                byte[] outBytes = Encoding.UTF8.GetBytes(openAiResp);
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = outBytes.Length;
                await resp.OutputStream.WriteAsync(outBytes, 0, outBytes.Length);
                await resp.CloseAsync();
                return null;
            }
        }

        private async Task<TimeSpan?> HandleResponsesStreamAsync(ProxyResponse resp, string targetModel, string geminiBody, string accessToken, string accountEmail)
        {
            string url = GoogleCloudCodeBaseUrl + "/v1internal:streamGenerateContent?alt=sse";

            string respId = "resp_" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string itemId = "msg_" + Guid.NewGuid().ToString("N").Substring(0, 24);
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fullText = new StringBuilder();
            var completedExtraOutput = new List<object>();
            string currentModel = targetModel;
            bool startedEvents = false;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
                httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                httpReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                httpReq.Content = new StringContent(geminiBody, Encoding.UTF8, "application/json");

                var googleResp = await _httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead);
                if (googleResp.StatusCode == HttpStatusCode.NotFound)
                {
                    string? retryModel = GetModelRetryCandidate(currentModel);
                    if (retryModel != null)
                    {
                        Log($"模型 {currentModel} 流式 404，尝试候选键 {retryModel}");
                        googleResp.Dispose();
                        var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
                        retryReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                        retryReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                        retryReq.Content = new StringContent(ReplaceGeminiBodyModel(geminiBody, retryModel), Encoding.UTF8, "application/json");
                        googleResp = await _httpClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead);
                        currentModel = retryModel;
                    }
                }

                using (googleResp)
                {
                    if (!googleResp.IsSuccessStatusCode)
                    {
                        string errText = await googleResp.Content.ReadAsStringAsync();
                        if (IsRateLimitResponse(googleResp.StatusCode, errText))
                        {
                            return ClassifyRateLimitCooldown(errText);
                        }

                        // 尚未向客户端写出任何字节时回传上游错误；否则（空响应重试后仍失败等场景）直接关流，
                        // 避免留下既不写内容也不关闭的悬挂响应
                        bool anyWritten = (resp.OutputStream as HttpListenerSyncStream)?.HeadersSynced ?? true;
                        if (!anyWritten)
                        {
                            Log($"Google 流式返回错误 {(int)googleResp.StatusCode}: {errText}");
                            resp.StatusCode = (int)googleResp.StatusCode;
                            byte[] errBytes = Encoding.UTF8.GetBytes(errText);
                            resp.ContentType = "application/json; charset=utf-8";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                        }
                        await resp.CloseAsync();
                        return null;
                    }

                    if (!startedEvents)
                    {
                        resp.StatusCode = 200;
                        resp.ContentType = "text/event-stream; charset=utf-8";
                        resp.Headers["Cache-Control"] = "no-cache";
                        resp.Headers["Connection"] = "keep-alive";
                    }

                    try
                    {
                        using var stream = await googleResp.Content.ReadAsStreamAsync();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        int chunkCount = 0;
                        int chunksWithCandidates = 0;
                        int chunksWithText = 0;
                        int chunksWithFunctionCall = 0;

                        while (true)
                        {
                            string? line = await reader.ReadLineAsync();
                            if (line == null) break;

                            if (line.StartsWith("data: "))
                            {
                                string dataJson = line.Substring(6).Trim();
                                if (string.IsNullOrEmpty(dataJson)) continue;
                                chunkCount++;

                                try
                                {
                                    using var doc = JsonDocument.Parse(dataJson);
                                    var root = doc.RootElement;
                                    var targetRoot = root;
                                    if (root.TryGetProperty("response", out var pResp) && pResp.ValueKind == JsonValueKind.Object)
                                        targetRoot = pResp;

                                    if (targetRoot.TryGetProperty("candidates", out var pCand) && pCand.ValueKind == JsonValueKind.Array && pCand.GetArrayLength() > 0)
                                    {
                                        var cand = pCand[0];
                                        chunksWithCandidates++;
                                        string textChunk = "";
                                        List<(string name, string args, string? sig)>? fnCalls = null;

                                        if (cand.TryGetProperty("content", out var pCnt) && pCnt.TryGetProperty("parts", out var pParts) && pParts.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var part in pParts.EnumerateArray())
                                            {
                                                if (part.TryGetProperty("text", out var pT))
                                                {
                                                    string t = pT.GetString() ?? "";
                                                    if (!string.IsNullOrEmpty(t))
                                                    {
                                                        textChunk += t;
                                                        chunksWithText++;
                                                    }
                                                }
                                                else if (part.TryGetProperty("functionCall", out var pFnCall) && pFnCall.ValueKind == JsonValueKind.Object)
                                                {
                                                    chunksWithFunctionCall++;
                                                    string fnName = pFnCall.TryGetProperty("name", out var pFnN) ? pFnN.GetString() ?? "" : "";
                                                    string argsStr = "";
                                                    if (pFnCall.TryGetProperty("args", out var pFnA))
                                                        argsStr = pFnA.ValueKind == JsonValueKind.String ? pFnA.GetString() ?? "" : pFnA.GetRawText();
                                                    (fnCalls ??= new List<(string, string, string?)>()).Add((fnName, argsStr, GeminiProtocolTranslator.GetThoughtSignature(part)));
                                                }
                                            }
                                        }

                                        if (!string.IsNullOrEmpty(textChunk) || fnCalls != null)
                                        {
                                            if (!startedEvents)
                                            {
                                                startedEvents = true;
                                                byte[] createdBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSseCreated(respId, itemId, created, currentModel));
                                                await resp.OutputStream.WriteAsync(createdBytes, 0, createdBytes.Length);
                                                byte[] itemBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSseItemAdded(respId, itemId));
                                                await resp.OutputStream.WriteAsync(itemBytes, 0, itemBytes.Length);
                                                byte[] partBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSsePartAdded(respId, itemId));
                                                await resp.OutputStream.WriteAsync(partBytes, 0, partBytes.Length);
                                                await resp.OutputStream.FlushAsync();
                                            }
                                        }

                                        if (!string.IsNullOrEmpty(textChunk))
                                        {
                                            fullText.Append(textChunk);
                                            string deltaSse = GeminiProtocolTranslator.BuildResponsesSseDelta(itemId, textChunk);
                                            byte[] deltaBytes = Encoding.UTF8.GetBytes(deltaSse);
                                            await resp.OutputStream.WriteAsync(deltaBytes, 0, deltaBytes.Length);
                                            await resp.OutputStream.FlushAsync();
                                        }

                                        if (fnCalls != null)
                                        {
                                            foreach (var (fnName, argsJson, fnSig) in fnCalls)
                                            {
                                                string callId = GeminiProtocolTranslator.BuildCallId(fnSig);
                                                string addSse = GeminiProtocolTranslator.BuildResponsesSseFunctionCallAdded(respId, callId, fnName);
                                                byte[] addBytes = Encoding.UTF8.GetBytes(addSse);
                                                await resp.OutputStream.WriteAsync(addBytes, 0, addBytes.Length);
                                                string argsDeltaSse = GeminiProtocolTranslator.BuildResponsesSseFunctionCallArgsDelta(callId, argsJson);
                                                byte[] argsDeltaBytes = Encoding.UTF8.GetBytes(argsDeltaSse);
                                                await resp.OutputStream.WriteAsync(argsDeltaBytes, 0, argsDeltaBytes.Length);
                                                await resp.OutputStream.FlushAsync();
                                                string fnDoneSse = GeminiProtocolTranslator.BuildResponsesSseFunctionCallDone(callId, argsJson);
                                                byte[] fnDoneBytes = Encoding.UTF8.GetBytes(fnDoneSse);
                                                await resp.OutputStream.WriteAsync(fnDoneBytes, 0, fnDoneBytes.Length);
                                                await resp.OutputStream.FlushAsync();
                                                completedExtraOutput.Add(new
                                                {
                                                    type = "function_call",
                                                    id = callId,
                                                    call_id = callId,
                                                    name = fnName,
                                                    arguments = argsJson,
                                                    status = "completed"
                                                });
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        Log($"[Responses流] 上游 chunk 总数: {chunkCount}, 含文本: {chunksWithText}, 含函数调用: {chunksWithFunctionCall}, 累计文本长度: {fullText.Length}");
                        bool isEmptyResponse = chunksWithCandidates > 0 && chunksWithText == 0 && chunksWithFunctionCall == 0;

                        if (isEmptyResponse && attempt == 0 && geminiBody.Contains("\"tools\""))
                        {
                            Log("[Responses流] 上游空响应（无文本无函数调用），移除 tools 后重试");
                            geminiBody = RemoveToolsFromGeminiBody(geminiBody);
                            continue;
                        }

                        if (!startedEvents)
                        {
                            startedEvents = true;
                            byte[] createdBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSseCreated(respId, itemId, created, currentModel));
                            await resp.OutputStream.WriteAsync(createdBytes, 0, createdBytes.Length);
                            byte[] itemBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSseItemAdded(respId, itemId));
                            await resp.OutputStream.WriteAsync(itemBytes, 0, itemBytes.Length);
                            byte[] partBytes = Encoding.UTF8.GetBytes(GeminiProtocolTranslator.BuildResponsesSsePartAdded(respId, itemId));
                            await resp.OutputStream.WriteAsync(partBytes, 0, partBytes.Length);
                            await resp.OutputStream.FlushAsync();
                        }
                        string doneSse = GeminiProtocolTranslator.BuildResponsesSseCompleted(respId, itemId, created, currentModel, fullText.ToString(), 0, 0, 0, completedExtraOutput.Count > 0 ? completedExtraOutput.ToArray() : null);
                        byte[] doneBytes = Encoding.UTF8.GetBytes(doneSse);
                        await resp.OutputStream.WriteAsync(doneBytes, 0, doneBytes.Length);
                        await resp.OutputStream.FlushAsync();
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log($"Responses 流式处理异常: {ex.Message}");
                        string incSse = GeminiProtocolTranslator.BuildResponsesSseIncomplete(respId, created, currentModel);
                        byte[] incBytes = Encoding.UTF8.GetBytes(incSse);
                        try
                        {
                            await resp.OutputStream.WriteAsync(incBytes, 0, incBytes.Length);
                            await resp.OutputStream.FlushAsync();
                        }
                        catch { }
                        break;
                    }
                }
            }

            await resp.CloseAsync();
            return null;
        }

        private async Task<TimeSpan?> HandleNonStreamChatCompletionAsync(ProxyResponse resp, string targetModel, string geminiBody, string accessToken, string accountEmail)
        {
            string url = GoogleCloudCodeBaseUrl + "/v1internal:generateContent";
            using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
            httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            httpReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
            httpReq.Content = new StringContent(geminiBody, Encoding.UTF8, "application/json");

            var googleResp = await _httpClient.SendAsync(httpReq);

            if (googleResp.StatusCode == HttpStatusCode.NotFound)
            {
                string? retryModel = GetModelRetryCandidate(targetModel);
                if (retryModel != null)
                {
                    Log($"模型 {targetModel} 404，尝试候选键 {retryModel}");
                    googleResp.Dispose();
                    var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
                    retryReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                    retryReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                    retryReq.Content = new StringContent(ReplaceGeminiBodyModel(geminiBody, retryModel), Encoding.UTF8, "application/json");
                    googleResp = await _httpClient.SendAsync(retryReq);
                    targetModel = retryModel;
                }
            }

            using (googleResp)
            {
                string respContent = await googleResp.Content.ReadAsStringAsync();

                if (IsRateLimitResponse(googleResp.StatusCode, respContent))
                {
                    return ClassifyRateLimitCooldown(respContent);
                }

                if (!googleResp.IsSuccessStatusCode)
                {
                    Log($"Google 返回错误 {(int)googleResp.StatusCode}: {respContent}");
                    resp.StatusCode = (int)googleResp.StatusCode;
                    byte[] errBytes = Encoding.UTF8.GetBytes(respContent);
                    resp.ContentType = "application/json; charset=utf-8";
                    resp.ContentLength64 = errBytes.Length;
                    await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                    await resp.CloseAsync();
                    return null;
                }

                string openAiResp = GeminiProtocolTranslator.ConvertGeminiToOpenAIResponse(respContent, targetModel);
                byte[] outBytes = Encoding.UTF8.GetBytes(openAiResp);
                resp.StatusCode = 200;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = outBytes.Length;
                await resp.OutputStream.WriteAsync(outBytes, 0, outBytes.Length);
                await resp.CloseAsync();
                return null;
            }
        }

        private async Task<TimeSpan?> HandleStreamChatCompletionAsync(ProxyResponse resp, string targetModel, string geminiBody, string accessToken, string accountEmail)
        {
            string url = GoogleCloudCodeBaseUrl + "/v1internal:streamGenerateContent?alt=sse";

            string reqId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string currentModel = targetModel;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
                httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                httpReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                httpReq.Content = new StringContent(geminiBody, Encoding.UTF8, "application/json");

                var googleResp = await _httpClient.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead);

                if (googleResp.StatusCode == HttpStatusCode.NotFound)
                {
                    string? retryModel = GetModelRetryCandidate(currentModel);
                    if (retryModel != null)
                    {
                        Log($"模型 {currentModel} 流式 404，尝试候选键 {retryModel}");
                        googleResp.Dispose();
                        var retryReq = new HttpRequestMessage(HttpMethod.Post, url);
                        retryReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                        retryReq.Headers.TryAddWithoutValidation("User-Agent", AntigravityUserAgent);
                        retryReq.Content = new StringContent(ReplaceGeminiBodyModel(geminiBody, retryModel), Encoding.UTF8, "application/json");
                        googleResp = await _httpClient.SendAsync(retryReq, HttpCompletionOption.ResponseHeadersRead);
                        currentModel = retryModel;
                    }
                }

                using (googleResp)
                {
                    if (!googleResp.IsSuccessStatusCode)
                    {
                        string errText = await googleResp.Content.ReadAsStringAsync();
                        if (IsRateLimitResponse(googleResp.StatusCode, errText))
                        {
                            return ClassifyRateLimitCooldown(errText);
                        }

                        // 尚未向客户端写出任何字节时回传上游错误；否则（空响应重试后仍失败等场景）直接关流，
                        // 避免留下既不写内容也不关闭的悬挂响应
                        bool anyWritten = (resp.OutputStream as HttpListenerSyncStream)?.HeadersSynced ?? true;
                        if (!anyWritten)
                        {
                            Log($"Google 流式返回错误 {(int)googleResp.StatusCode}: {errText}");
                            resp.StatusCode = (int)googleResp.StatusCode;
                            byte[] errBytes = Encoding.UTF8.GetBytes(errText);
                            resp.ContentType = "application/json; charset=utf-8";
                            resp.ContentLength64 = errBytes.Length;
                            await resp.OutputStream.WriteAsync(errBytes, 0, errBytes.Length);
                        }
                        await resp.CloseAsync();
                        return null;
                    }

                    if (attempt == 0)
                    {
                        resp.StatusCode = 200;
                        resp.ContentType = "text/event-stream; charset=utf-8";
                        resp.Headers.Add("Cache-Control", "no-cache");
                        resp.Headers.Add("Connection", "keep-alive");
                    }

                    using var stream = await googleResp.Content.ReadAsStreamAsync();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    int totalChunks = 0;
                    int chunksWithCandidates = 0;
                    int chunksWithText = 0;
                    int chunksWithFunctionCall = 0;

                    while (true)
                    {
                        string? line = await reader.ReadLineAsync();
                        if (line == null) break;

                        if (line.StartsWith("data: "))
                        {
                            string dataJson = line.Substring(6).Trim();
                            if (string.IsNullOrEmpty(dataJson)) continue;
                            totalChunks++;

                            try
                            {
                                using var doc = JsonDocument.Parse(dataJson);
                                var root = doc.RootElement;
                                var targetRoot = root;
                                if (root.TryGetProperty("response", out var pResp) && pResp.ValueKind == JsonValueKind.Object)
                                    targetRoot = pResp;

                                if (targetRoot.TryGetProperty("candidates", out var pCand) && pCand.ValueKind == JsonValueKind.Array && pCand.GetArrayLength() > 0)
                                {
                                    var cand = pCand[0];
                                    string textChunk = "";
                                    List<(string name, string args, string? sig)>? fnCalls = null;
                                    if (cand.TryGetProperty("content", out var pCnt) && pCnt.TryGetProperty("parts", out var pParts) && pParts.ValueKind == JsonValueKind.Array)
                                    {
                                        foreach (var part in pParts.EnumerateArray())
                                        {
                                            if (part.TryGetProperty("text", out var pT))
                                                textChunk += pT.GetString();
                                            else if (part.TryGetProperty("functionCall", out var pFnCall) && pFnCall.ValueKind == JsonValueKind.Object)
                                            {
                                                chunksWithFunctionCall++;
                                                string fnName = pFnCall.TryGetProperty("name", out var pFnN) ? pFnN.GetString() ?? "" : "";
                                                string argsStr = "";
                                                if (pFnCall.TryGetProperty("args", out var pFnA))
                                                    argsStr = pFnA.ValueKind == JsonValueKind.String ? pFnA.GetString() ?? "" : pFnA.GetRawText();
                                                (fnCalls ??= new List<(string, string, string?)>()).Add((fnName, argsStr, GeminiProtocolTranslator.GetThoughtSignature(part)));
                                            }
                                        }
                                    }

                                    chunksWithCandidates++;
                                    if (!string.IsNullOrEmpty(textChunk)) chunksWithText++;

                                    if (!string.IsNullOrEmpty(textChunk))
                                    {
                                        string chunkSse = GeminiProtocolTranslator.BuildOpenAISseChunk(reqId, created, currentModel, textChunk, null);
                                        byte[] chunkBytes = Encoding.UTF8.GetBytes(chunkSse);
                                        await resp.OutputStream.WriteAsync(chunkBytes, 0, chunkBytes.Length);
                                        await resp.OutputStream.FlushAsync();
                                    }

                                    if (fnCalls != null)
                                    {
                                        foreach (var (fnName, argsJson, fnSig) in fnCalls)
                                        {
                                            string callId = GeminiProtocolTranslator.BuildCallId(fnSig);
                                            string toolSse = GeminiProtocolTranslator.BuildOpenAIToolCallSseChunk(reqId, created, currentModel, callId, fnName, argsJson);
                                            byte[] toolBytes = Encoding.UTF8.GetBytes(toolSse);
                                            await resp.OutputStream.WriteAsync(toolBytes, 0, toolBytes.Length);
                                            await resp.OutputStream.FlushAsync();
                                        }
                                    }

                                    string? finishReason = null;
                                    if (cand.TryGetProperty("finishReason", out var pFR))
                                    {
                                        string fr = pFR.GetString() ?? "";
                                        if (!string.IsNullOrEmpty(fr) && !fr.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                                            finishReason = fr.ToLowerInvariant();
                                    }
                                    if (fnCalls != null && (finishReason == null || finishReason == "stop"))
                                        finishReason = "tool_calls";

                                    if (finishReason != null)
                                    {
                                        string finishSse = GeminiProtocolTranslator.BuildOpenAISseChunk(reqId, created, currentModel, "", finishReason);
                                        byte[] finishBytes = Encoding.UTF8.GetBytes(finishSse);
                                        await resp.OutputStream.WriteAsync(finishBytes, 0, finishBytes.Length);
                                        await resp.OutputStream.FlushAsync();
                                    }
                                }
                            }
                            catch { }
                        }
                    }

                    Log($"[chat流] 上游 chunk 总数: {totalChunks}, 含文本: {chunksWithText}, 含函数调用: {chunksWithFunctionCall}");
                    bool isEmptyResponse = chunksWithCandidates > 0 && chunksWithText == 0 && chunksWithFunctionCall == 0;

                    if (isEmptyResponse && attempt == 0 && geminiBody.Contains("\"tools\""))
                    {
                        Log("[chat流] 上游空响应（无文本无函数调用），移除 tools 后重试");
                        geminiBody = RemoveToolsFromGeminiBody(geminiBody);
                        continue;
                    }

                    byte[] doneBytes = Encoding.UTF8.GetBytes("data: [DONE]\n\n");
                    await resp.OutputStream.WriteAsync(doneBytes, 0, doneBytes.Length);
                    await resp.OutputStream.FlushAsync();
                    break;
                }
            }

            await resp.CloseAsync();
            return null;
        }

        private static string? GetModelRetryCandidate(string targetModel)
        {
            if (string.IsNullOrWhiteSpace(targetModel)) return null;
            string lower = targetModel.ToLowerInvariant();
            if (!lower.StartsWith("gemini-")) return null;

            if (lower.Contains("3.7-flash"))
            {
                if (lower.Contains("gemini-3.7-flash-high") || lower.Contains("gemini-3.7-flash-medium") || lower.Contains("gemini-3.7-flash-low") || lower.Contains("gemini-3.7-flash-thinking"))
                    return null;
                return "gemini-3.7-flash-high";
            }
            if (lower.Contains("3.7-pro"))
            {
                if (lower.Contains("gemini-3.7-pro-high") || lower.Contains("gemini-3.7-pro-low"))
                    return null;
                return "gemini-3.7-pro-high";
            }
            if (lower.Contains("3.6-flash"))
            {
                if (lower.Contains("gemini-3.6-flash-high") || lower.Contains("gemini-3.6-flash-medium") || lower.Contains("gemini-3.6-flash-low") || lower.Contains("gemini-3.6-flash-tiered"))
                    return null;
                return "gemini-3.6-flash-high";
            }
            if (lower.Contains("3.5-flash"))
            {
                if (lower.Contains("gemini-3.5-flash-low") || lower.Contains("gemini-3.5-flash-extra-low"))
                    return null;
                return "gemini-3.5-flash-low";
            }
            if (lower.Contains("3-flash"))
            {
                if (lower.Contains("gemini-3-flash")) return null;
                return "gemini-3-flash";
            }
            if (lower.Contains("3-pro") || lower.Contains("3.1-pro"))
            {
                if (lower.Contains("gemini-3.1-pro-high") || lower.Contains("gemini-3.1-pro-low"))
                    return null;
                return "gemini-3.1-pro-high";
            }
            if (lower.Contains("3.1-flash"))
            {
                if (lower.Contains("gemini-3.1-flash-lite") || lower.Contains("gemini-3.1-flash-image"))
                    return null;
                return "gemini-3.1-flash-lite";
            }
            return null;
        }

        private static string ReplaceGeminiBodyModel(string geminiBody, string newModel)
        {
            try
            {
                using var doc = JsonDocument.Parse(geminiBody);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return geminiBody;

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("model")) writer.WriteString("model", newModel);
                        else prop.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch
            {
                return geminiBody;
            }
        }

        private static string RemoveToolsFromGeminiBody(string geminiBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(geminiBody);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return geminiBody;

                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.NameEquals("request") && prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            writer.WritePropertyName("request");
                            writer.WriteStartObject();
                            foreach (var rp in prop.Value.EnumerateObject())
                            {
                                if (rp.NameEquals("tools") || rp.NameEquals("toolConfig"))
                                    continue;
                                rp.WriteTo(writer);
                            }
                            writer.WriteEndObject();
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch
            {
                return geminiBody;
            }
        }

        private async Task<TimeSpan?> HandleNativeGeminiForwardAsync(ProxyRequest req, ProxyResponse resp, string rawPath, string accessToken, byte[]? bodyBytes)
        {
            string querySuffix = string.IsNullOrEmpty(req.QueryString) ? "" : "?" + req.QueryString;
            string targetUrl = "https://generativelanguage.googleapis.com" + rawPath + querySuffix;
            using var httpReq = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUrl);
            httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");

            if (bodyBytes != null && bodyBytes.Length > 0)
            {
                httpReq.Content = new ByteArrayContent(bodyBytes);
                if (!string.IsNullOrEmpty(req.ContentType))
                    httpReq.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
            }

            var googleResp = await _httpClient.SendAsync(httpReq);
            using (googleResp)
            {
                if (googleResp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    string errText = await googleResp.Content.ReadAsStringAsync();
                    return ClassifyRateLimitCooldown(errText);
                }

                resp.StatusCode = (int)googleResp.StatusCode;
                if (googleResp.Content.Headers.ContentType != null)
                    resp.ContentType = googleResp.Content.Headers.ContentType.ToString();

                byte[] outBytes = await googleResp.Content.ReadAsByteArrayAsync();
                resp.ContentLength64 = outBytes.Length;
                await resp.OutputStream.WriteAsync(outBytes, 0, outBytes.Length);
                await resp.CloseAsync();
                return null;
            }
        }
    }

    public class ProxyRequest
    {
        public string HttpMethod { get; set; } = "GET";
        public string RawUrl { get; set; } = "";
        public string AbsolutePath { get; set; } = "/";
        public string QueryString { get; set; } = "";
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Query { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool HasEntityBody { get; set; }
        public string ContentType { get; set; } = "";
        public Encoding ContentEncoding { get; set; } = Encoding.UTF8;
        public Stream? InputStream { get; set; }
    }

    public class ProxyResponse
    {
        public int StatusCode { get; set; } = 200;
        public string ContentType { get; set; } = "application/json; charset=utf-8";
        public long ContentLength64 { get; set; } = -1;
        public bool SendChunked { get; set; } = false;
        public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Stream OutputStream { get; set; } = Stream.Null;
        public Func<Task>? CloseAsyncAction { get; set; }

        public async Task CloseAsync()
        {
            if (CloseAsyncAction != null)
            {
                await CloseAsyncAction();
            }
        }
    }

    public class HttpListenerSyncStream : Stream
    {
        private readonly HttpListenerResponse _response;
        private ProxyResponse? _proxyResponse;
        private bool _headersSynced = false;

        public HttpListenerSyncStream(HttpListenerResponse response)
        {
            _response = response;
        }

        public void Attach(ProxyResponse proxyResponse)
        {
            _proxyResponse = proxyResponse;
        }

        /// <summary>响应头是否已同步（即是否已有任何字节写出到客户端）</summary>
        internal bool HeadersSynced => _headersSynced;

        public void SyncNow()
        {
            if (_headersSynced || _proxyResponse == null) return;
            _headersSynced = true;

            try
            {
                _response.StatusCode = _proxyResponse.StatusCode;
                if (!string.IsNullOrEmpty(_proxyResponse.ContentType))
                    _response.ContentType = _proxyResponse.ContentType;
                if (_proxyResponse.ContentLength64 >= 0)
                {
                    _response.ContentLength64 = _proxyResponse.ContentLength64;
                }
                else
                {
                    // 长度未知（SSE 流式响应）：必须启用 chunked 传输编码。
                    // 否则 http/sys 按 Content-Length: 0 处理，首次写入即抛
                    // "Bytes to be written to the stream exceed the Content-Length bytes size specified"，
                    // 客户端只能收到 200 + text/event-stream + 0 字节空响应体。
                    _response.SendChunked = true;
                }

                foreach (var kvp in _proxyResponse.Headers)
                {
                    if (string.Equals(kvp.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(kvp.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        _response.Headers[kvp.Key] = kvp.Value;
                    }
                    catch { }
                }
            }
            catch { }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
            SyncNow();
            _response.OutputStream.Flush();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            SyncNow();
            return _response.OutputStream.FlushAsync(cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            SyncNow();
            _response.OutputStream.Write(buffer, offset, count);
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            SyncNow();
            await _response.OutputStream.WriteAsync(buffer, offset, count, cancellationToken);
        }
    }
}
