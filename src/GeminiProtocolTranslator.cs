using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CLIProxyAPI_GUI
{
    public static class GeminiProtocolTranslator
    {
        // Antigravity 上游 generateContent 的 model 字段只接受 fetchAvailableModels 返回的完整键名。
        // 3.5/3.6/3.7 系列按 -low/-medium/-high 变体命名，裸名(如 gemini-3.7-flash, gemini-3.6-flash)与旧名
        // 会被上游返回 404 Requested entity was not found，因此需要统一映射到合法键。
        private static readonly Dictionary<string, string> ModelAliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // 3.7 系列
            ["gemini-3.7-flash"] = "gemini-3.7-flash-high",
            ["gemini-3.7-flash-high"] = "gemini-3.7-flash-high",
            ["gemini-3.7-flash-medium"] = "gemini-3.7-flash-medium",
            ["gemini-3.7-flash-low"] = "gemini-3.7-flash-low",
            ["gemini-3.7-flash-lite"] = "gemini-3.7-flash-low",
            ["gemini-3.7-flash-thinking"] = "gemini-3.7-flash-thinking",
            ["gemini-3.7-pro"] = "gemini-3.7-pro-high",
            ["gemini-3.7-pro-high"] = "gemini-3.7-pro-high",
            ["gemini-3.7-pro-low"] = "gemini-3.7-pro-low",
            // 3.6 系列
            ["gemini-3.6-flash"] = "gemini-3.6-flash-high",           // 上游 defaultAgentModelId
            ["gemini-3.6-flash-high"] = "gemini-3.6-flash-high",
            ["gemini-3.6-flash-medium"] = "gemini-3.6-flash-medium",
            ["gemini-3.6-flash-low"] = "gemini-3.6-flash-low",
            ["gemini-3.6-flash-lite"] = "gemini-3.6-flash-low",
            ["gemini-3.6-flash-pro"] = "gemini-3.6-flash-high",
            // 3.5 系列（上游无 -high 键，最高变体为 -low）
            ["gemini-3.5-flash"] = "gemini-3.5-flash-low",
            ["gemini-3.5-flash-high"] = "gemini-3.5-flash-low",
            ["gemini-3.5-flash-medium"] = "gemini-3.5-flash-low",
            ["gemini-3.5-flash-low"] = "gemini-3.5-flash-low",
            ["gemini-3.5-flash-lite"] = "gemini-3.5-flash-extra-low",
            ["gemini-3.5-flash-extra-low"] = "gemini-3.5-flash-extra-low",
            // 3.x 系列
            ["gemini-3-flash"] = "gemini-3-flash",
            ["gemini-3-flash-preview"] = "gemini-3-flash",
            ["gemini-3-pro"] = "gemini-3.1-pro-high",
            ["gemini-3-pro-preview"] = "gemini-3.1-pro-high",
            ["gemini-3.1-pro"] = "gemini-3.1-pro-high",
            ["gemini-3.1-pro-preview"] = "gemini-3.1-pro-high",
            ["gemini-3.1-pro-high"] = "gemini-3.1-pro-high",
            ["gemini-3.1-pro-low"] = "gemini-3.1-pro-low",
            ["gemini-3.1-flash"] = "gemini-3.1-flash-lite",
            ["gemini-3.1-flash-lite"] = "gemini-3.1-flash-lite",
            ["gemini-3.1-flash-image"] = "gemini-3.1-flash-image",
            // latest 别名
            ["gemini-flash-latest"] = "gemini-3.7-flash-high",
            ["gemini-pro-latest"] = "gemini-3.7-pro-high",
        };

        // 上游 fetchAvailableModels 字典中实测可用的完整键名（generateContent 直接接受这些）
        private static readonly HashSet<string> KnownModelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gemini-3.7-flash-high", "gemini-3.7-flash-medium", "gemini-3.7-flash-low", "gemini-3.7-flash-thinking",
            "gemini-3.7-pro-high", "gemini-3.7-pro-low", "gemini-3.7-pro", "gemini-3.7-flash",
            "gemini-3.6-flash-high", "gemini-3.6-flash-medium", "gemini-3.6-flash-low", "gemini-3.6-flash-tiered",
            "gemini-3.5-flash-low", "gemini-3.5-flash-extra-low",
            "gemini-3.1-pro-high", "gemini-3.1-pro-low",
            "gemini-3.1-flash-lite", "gemini-3.1-flash-image",
            "gemini-3-flash", "gemini-3-flash-agent", "gemini-pro-agent",
            "gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-flash-thinking", "gemini-2.5-pro",
            "tab_flash_lite_preview", "tab_jump_flash_lite_preview",
            "gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-1.5-pro", "gemini-1.5-flash",
        };

        public static string MapModel(string inputModel)
        {
            if (string.IsNullOrWhiteSpace(inputModel)) return "gemini-3.5-flash-low";
            string m = inputModel.Trim();

            // 如果客户端传入带提供商前缀的模型 (如 "测试:gemini-3.5-flash-lite" 或 "google:gemini-3.1-pro")，自动剥离冒号前的部分
            if (m.Contains(":"))
            {
                var parts = m.Split(':');
                m = parts[parts.Length - 1].Trim();
            }

            string lower = m.ToLowerInvariant();

            // 第三方非 Gemini 别名 (如 gpt-4o, claude 等) 映射到可用模型
            if (lower.StartsWith("gpt-4") || lower.Contains("claude-3-5") || lower.Contains("claude-sonnet"))
                return "gemini-3.1-pro-high";
            if (lower.StartsWith("gpt-3") || lower.Contains("claude-haiku"))
                return "gemini-3.5-flash-low";

            // 已是上游合法键名 -> 原样返回（防止把 tiered 等合法变体误归一）
            if (KnownModelKeys.Contains(lower))
                return m;

            // 精确别名表：客户端名 -> 上游合法键名
            if (ModelAliasMap.TryGetValue(lower, out var mapped))
                return mapped;

            // 兜底规则：未知的 gemini-3.x 变体按系列归一（上游只认字典键）
            if (lower.StartsWith("gemini-"))
            {
                if (lower.Contains("3.7-flash"))
                {
                    return lower.Contains("thinking") ? "gemini-3.7-flash-thinking"
                         : lower.Contains("lite") || lower.Contains("low") ? "gemini-3.7-flash-low"
                         : lower.Contains("medium") ? "gemini-3.7-flash-medium"
                         : "gemini-3.7-flash-high";
                }
                if (lower.Contains("3.7-pro"))
                {
                    return lower.Contains("low") ? "gemini-3.7-pro-low" : "gemini-3.7-pro-high";
                }
                if (lower.Contains("3.6-flash"))
                {
                    return lower.Contains("lite") ? "gemini-3.6-flash-low"
                         : lower.Contains("low") ? "gemini-3.6-flash-low"
                         : lower.Contains("medium") ? "gemini-3.6-flash-medium"
                         : "gemini-3.6-flash-high";
                }
                if (lower.Contains("3.5-flash"))
                {
                    return lower.Contains("lite") || lower.Contains("extra-low") ? "gemini-3.5-flash-extra-low"
                         : "gemini-3.5-flash-low";
                }
                if (lower.Contains("3-flash"))
                    return "gemini-3-flash";
                if (lower.Contains("3-pro") || lower.Contains("3.1-pro"))
                    return "gemini-3.1-pro-high";
                if (lower.Contains("3.1-flash"))
                    return lower.Contains("image") ? "gemini-3.1-flash-image" : "gemini-3.1-flash-lite";
            }

            // 其余模型名（2.x 及更早系列、claude 原生名等）原样传递
            return m;
        }

        public static async Task<(string json, bool isRateLimit)> FetchOpenAIModelListJsonAsync(HttpClient httpClient, string accessToken, string projectId)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var modelSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(accessToken))
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, LocalProxyServer.GoogleCloudCodeBaseUrl + "/v1internal:fetchAvailableModels");
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                    req.Headers.TryAddWithoutValidation("User-Agent", LocalProxyServer.AntigravityUserAgent);
                    
                    var reqBodyDict = new Dictionary<string, object>();
                    if (!string.IsNullOrEmpty(projectId)) reqBodyDict["project"] = projectId;
                    req.Content = new StringContent(JsonSerializer.Serialize(reqBodyDict), Encoding.UTF8, "application/json");

                    using var resp = await httpClient.SendAsync(req);
                    string jsonContent = await resp.Content.ReadAsStringAsync();

                    if ((int)resp.StatusCode == 429 ||
                        jsonContent.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
                        jsonContent.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("", true);
                    }

                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(jsonContent);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("models", out var pModels))
                        {
                            // 上游实际返回对象字典（键=模型名），遍历属性键提取纯正上游模型 ID
                            if (pModels.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var prop in pModels.EnumerateObject())
                                {
                                    string name = prop.Name;
                                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                                        name = name.Substring(7);
                                    if (!string.IsNullOrWhiteSpace(name))
                                        modelSet.Add(name);
                                }
                            }
                            else if (pModels.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in pModels.EnumerateArray())
                                {
                                    string name = "";
                                    if (item.TryGetProperty("name", out var pN)) name = pN.GetString() ?? "";
                                    else if (item.TryGetProperty("id", out var pId)) name = pId.GetString() ?? "";

                                    if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                                        name = name.Substring(7);

                                    if (!string.IsNullOrWhiteSpace(name))
                                        modelSet.Add(name);
                                }
                            }
                        }

                        if (root.TryGetProperty("webSearchModelIds", out var pWeb) && pWeb.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in pWeb.EnumerateArray())
                            {
                                string name = item.GetString() ?? "";
                                if (name.StartsWith("models/", StringComparison.OrdinalIgnoreCase))
                                    name = name.Substring(7);

                                if (!string.IsNullOrWhiteSpace(name))
                                    modelSet.Add(name);
                            }
                        }
                    }
                }
                catch { }
            }

            var dataList = modelSet.Select(m => new
            {
                id = m,
                @object = "model",
                created = now,
                owned_by = "google"
            }).ToArray();

            var res = new
            {
                @object = "list",
                data = dataList
            };

            return (JsonSerializer.Serialize(res), false);
        }

        public static (string geminiJson, bool isStream, string targetModel) ConvertOpenAIToGeminiRequest(string openAiJson, string projectId = "")
        {
            using var doc = JsonDocument.Parse(openAiJson);
            var root = doc.RootElement;

            string rawModel = root.TryGetProperty("model", out var pM) ? pM.GetString() ?? "" : "";
            string targetModel = MapModel(rawModel);

            bool isStream = root.TryGetProperty("stream", out var pStr) && pStr.ValueKind == JsonValueKind.True && pStr.GetBoolean();

            var contentsList = new List<object>();
            object? systemInstruction = null;

            if (root.TryGetProperty("messages", out var pMsg) && pMsg.ValueKind == JsonValueKind.Array)
            {
                var systemParts = new List<object>();

                // 同请求内 tool_call_id -> 函数名 映射（tool 消息缺少 name 时用于还原 functionResponse.name）
                var toolCallIdToName = new Dictionary<string, string>();

                foreach (var msg in pMsg.EnumerateArray())
                {
                    string role = msg.TryGetProperty("role", out var pRole) ? pRole.GetString() ?? "user" : "user";
                    var parts = ParseOpenAIMessageParts(msg);

                    if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
                    {
                        systemParts.AddRange(parts);
                    }
                    else if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
                    {
                        // OpenAI tool 消息 -> Gemini functionResponse part
                        // name 优先取消息自带 name；缺失时用同请求内 assistant.tool_calls 的 id->name 映射还原；
                        // 均缺失时回退 tool_call_id（上游实测可接受 call_id 作为 name）
                        string toolName = msg.TryGetProperty("name", out var pTName) ? pTName.GetString() ?? "" : "";
                        string toolCallId = msg.TryGetProperty("tool_call_id", out var pTCid) ? pTCid.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(toolName))
                        {
                            if (!string.IsNullOrEmpty(toolCallId) && toolCallIdToName.TryGetValue(toolCallId, out var mappedName))
                                toolName = mappedName;
                            else
                                toolName = toolCallId;
                        }
                        string toolText = "";
                        if (msg.TryGetProperty("content", out var pTContent))
                        {
                            if (pTContent.ValueKind == JsonValueKind.String) toolText = pTContent.GetString() ?? "";
                            else if (pTContent.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var item in pTContent.EnumerateArray())
                                    if (item.TryGetProperty("text", out var pTT)) toolText += pTT.GetString();
                            }
                        }
                        if (!string.IsNullOrEmpty(toolName))
                        {
                            contentsList.Add(new
                            {
                                role = "user",
                                parts = new[]
                                {
                                    new
                                    {
                                        functionResponse = new
                                        {
                                            name = toolName,
                                            response = new { result = string.IsNullOrEmpty(toolText) ? "(empty tool result)" : toolText }
                                        }
                                    }
                                }
                            });
                        }
                    }
                    else if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        // assistant 消息: 文本 parts + tool_calls -> functionCall parts
                        var asstParts = new List<object>(parts);

                        if (msg.TryGetProperty("tool_calls", out var pTCalls) && pTCalls.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in pTCalls.EnumerateArray())
                            {
                                string fnName = "";
                                if (tc.TryGetProperty("function", out var pFn))
                                {
                                    if (pFn.TryGetProperty("name", out var pFnName)) fnName = pFnName.GetString() ?? "";
                                    if (pFn.TryGetProperty("arguments", out var pFnArgs))
                                    {
                                        string argsStr = pFnArgs.ValueKind == JsonValueKind.String ? pFnArgs.GetString() ?? "" : pFnArgs.GetRawText();
                                        if (!string.IsNullOrEmpty(fnName))
                                        {
                                            JsonNode? argsNode = null;
                                            try { argsNode = JsonNode.Parse(string.IsNullOrEmpty(argsStr) ? "{}" : argsStr); } catch { }
                                            // 记录 tool_call_id -> 函数名 映射，供后续 tool 消息还原 name
                                            string toolCallId = tc.TryGetProperty("id", out var pTCid2) ? pTCid2.GetString() ?? "" : "";
                                            if (!string.IsNullOrEmpty(toolCallId))
                                                toolCallIdToName[toolCallId] = fnName;
                                            // 从 call_id 还原 thought_signature（代理下发的 "call_ts_" 前缀编码），
                                            // 上游 Antigravity 模型强制要求 functionCall 回传时附带
                                            string? sig = TryExtractThoughtSignature(toolCallId);
                                            var fnCallObj = new JsonObject
                                            {
                                                ["name"] = fnName,
                                                ["args"] = argsNode ?? new JsonObject()
                                            };
                                            var partObj = new JsonObject { ["functionCall"] = fnCallObj };
                                            if (!string.IsNullOrEmpty(sig)) partObj["thoughtSignature"] = sig;
                                            asstParts.Add(partObj);
                                        }
                                    }
                                }
                            }
                        }

                        contentsList.Add(new
                        {
                            role = "model",
                            parts = asstParts.ToArray()
                        });
                    }
                    else
                    {
                        string geminiRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                        contentsList.Add(new
                        {
                            role = geminiRole,
                            parts = parts
                        });
                    }
                }

                if (systemParts.Count > 0)
                {
                    systemInstruction = new
                    {
                        parts = systemParts
                    };
                }
            }
            else
            {
                // Responses API / Completions API 兼容 (input, instructions, prompt)
                if (root.TryGetProperty("instructions", out var pInst) && pInst.ValueKind == JsonValueKind.String)
                {
                    systemInstruction = new { parts = new[] { new { text = pInst.GetString() ?? "" } } };
                }

                if (root.TryGetProperty("input", out var pInput))
                {
                    if (pInput.ValueKind == JsonValueKind.String)
                    {
                        contentsList.Add(new { role = "user", parts = new[] { new { text = pInput.GetString() ?? "" } } });
                    }
                    else if (pInput.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in pInput.EnumerateArray())
                        {
                            string role = item.TryGetProperty("role", out var pRole) ? pRole.GetString() ?? "user" : "user";
                            string itemType = item.TryGetProperty("type", out var pItemType) ? pItemType.GetString() ?? "" : "";
                            // Responses 顶层 function_call item 没有 role 字段，必须映射为 model 角色
                            // （否则 Gemini 侧 functionCall part 出现在 user 角色下会被上游拒绝）
                            if (itemType.Equals("function_call", StringComparison.OrdinalIgnoreCase) &&
                                (string.IsNullOrEmpty(role) || role.Equals("user", StringComparison.OrdinalIgnoreCase)))
                            {
                                role = "assistant";
                            }
                            var parts = ParseOpenAIMessageParts(item);
                            // 无法识别的 item（无 content 且非 function_call/function_call_output）跳过，避免空 parts 消息被上游拒绝
                            if (parts.Count == 0) continue;
                            string geminiRole = role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
                            contentsList.Add(new { role = geminiRole, parts = parts });
                        }
                    }
                }
                else if (root.TryGetProperty("prompt", out var pPrompt) && pPrompt.ValueKind == JsonValueKind.String)
                {
                    contentsList.Add(new { role = "user", parts = new[] { new { text = pPrompt.GetString() ?? "" } } });
                }
            }

            var genConfig = new Dictionary<string, object>();
            if (root.TryGetProperty("temperature", out var pTemp) && pTemp.ValueKind == JsonValueKind.Number)
                genConfig["temperature"] = pTemp.GetDouble();
            if (root.TryGetProperty("top_p", out var pTopP) && pTopP.ValueKind == JsonValueKind.Number)
                genConfig["topP"] = pTopP.GetDouble();
            if (root.TryGetProperty("max_tokens", out var pMaxT) && pMaxT.ValueKind == JsonValueKind.Number)
                genConfig["maxOutputTokens"] = pMaxT.GetInt32();
            else if (root.TryGetProperty("max_completion_tokens", out var pMaxCT) && pMaxCT.ValueKind == JsonValueKind.Number)
                genConfig["maxOutputTokens"] = pMaxCT.GetInt32();

            // 工具定义: OpenAI tools -> Gemini functionDeclarations
            // 兼容两种工具格式:
            //   Chat Completions: {"type":"function","function":{"name","description","parameters"}}
            //   AI SDK Responses: {"type":"function","name","description","parameters"} (扁平)
            var geminiTools = new List<object>();
            if (root.TryGetProperty("tools", out var pTools) && pTools.ValueKind == JsonValueKind.Array)
            {
                var fnDecls = new List<object>();
                foreach (var tool in pTools.EnumerateArray())
                {
                    if (tool.ValueKind != JsonValueKind.Object) continue;
                    bool isFunction = tool.TryGetProperty("type", out var pTType) &&
                        pTType.GetString()?.Equals("function", StringComparison.OrdinalIgnoreCase) == true;
                    if (!isFunction) continue;

                    JsonElement fn;
                    if (tool.TryGetProperty("function", out var pFn) && pFn.ValueKind == JsonValueKind.Object)
                        fn = pFn; // Chat Completions 格式
                    else
                        fn = tool; // AI SDK 扁平格式
                    if (fn.ValueKind != JsonValueKind.Object) continue;

                    string fnName = fn.TryGetProperty("name", out var pFnN) ? pFnN.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(fnName)) continue;
                    string fnDesc = fn.TryGetProperty("description", out var pFnD) ? pFnD.GetString() ?? "" : "";
                    JsonElement? fnParams = fn.TryGetProperty("parameters", out var pFnP) ? pFnP : (JsonElement?)null;
                    var decl = new Dictionary<string, object>
                    {
                        ["name"] = fnName
                    };
                    if (!string.IsNullOrEmpty(fnDesc)) decl["description"] = fnDesc;
                    if (fnParams.HasValue && fnParams.Value.ValueKind == JsonValueKind.Object)
                        decl["parameters"] = SanitizeGeminiSchema(fnParams.Value) ?? new Dictionary<string, object>();
                    else
                        decl["parameters"] = new { type = "object", properties = new object { } };
                    fnDecls.Add(decl);
                }
                if (fnDecls.Count > 0)
                {
                    geminiTools.Add(new { functionDeclarations = fnDecls });
                }
            }

            // tool_choice -> Gemini functionCallingConfig
            Dictionary<string, object>? toolConfig = null;
            if (root.TryGetProperty("tool_choice", out var pToolChoice))
            {
                string mode = "AUTO";
                List<string>? allowedNames = null;
                if (pToolChoice.ValueKind == JsonValueKind.String)
                {
                    string tc = pToolChoice.GetString() ?? "";
                    if (tc.Equals("none", StringComparison.OrdinalIgnoreCase)) mode = "NONE";
                    else if (tc.Equals("required", StringComparison.OrdinalIgnoreCase)) mode = "ANY";
                }
                else if (pToolChoice.ValueKind == JsonValueKind.Object)
                {
                    if (pToolChoice.TryGetProperty("type", out var pTcType) && pTcType.GetString()?.Equals("function", StringComparison.OrdinalIgnoreCase) == true
                        && pToolChoice.TryGetProperty("name", out var pTcName))
                    {
                        mode = "ANY";
                        allowedNames = new List<string> { pTcName.GetString() ?? "" };
                    }
                }
                if (mode != "AUTO" || allowedNames != null)
                {
                    toolConfig = new Dictionary<string, object>
                    {
                        ["functionCallingConfig"] = new Dictionary<string, object>
                        {
                            ["mode"] = mode
                        }
                    };
                    if (allowedNames != null)
                        ((Dictionary<string, object>)toolConfig["functionCallingConfig"])["allowedFunctionNames"] = allowedNames;
                }
            }

            // 保证 contents 非空且第一个 turn 为 user 角色（Antigravity 要求 user 轮次起手，否则 400）
            if (contentsList.Count == 0)
            {
                contentsList.Add(new { role = "user", parts = new object[] { new { text = "" } } });
            }
            else
            {
                // 检查第一项角色，若为 model（例如 assistant 起手或历史不规范），插入空 user 轮次
                var firstItem = contentsList[0];
                var roleProp = firstItem.GetType().GetProperty("role")?.GetValue(firstItem)?.ToString();
                if (roleProp != null && roleProp.Equals("model", StringComparison.OrdinalIgnoreCase))
                {
                    contentsList.Insert(0, new { role = "user", parts = new object[] { new { text = "" } } });
                }
            }

            var innerRequest = new Dictionary<string, object>();
            // 对齐最新 CPA：使用负整数 session id（如 -1234567890）或 session uuid
            innerRequest["sessionId"] = "-" + Math.Abs(Random.Shared.NextInt64(1000000000000000L, 9999999999999999L));
            if (systemInstruction != null) innerRequest["systemInstruction"] = systemInstruction;
            innerRequest["contents"] = contentsList;
            if (genConfig.Count > 0) innerRequest["generationConfig"] = genConfig;
            if (geminiTools.Count > 0) innerRequest["tools"] = geminiTools;
            if (toolConfig != null) innerRequest["toolConfig"] = toolConfig;

            bool isImageModel = targetModel.Contains("image", StringComparison.OrdinalIgnoreCase);
            string requestType = isImageModel ? "image_gen" : "agent";
            string requestId = isImageModel
                ? $"image_gen/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/{Guid.NewGuid():D}/12"
                : "agent-" + Guid.NewGuid().ToString("D");

            var outerWrapper = new Dictionary<string, object>
            {
                ["model"] = targetModel,
                ["userAgent"] = "antigravity",
                ["requestType"] = requestType,
                ["requestId"] = requestId,
                ["request"] = innerRequest
            };
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                outerWrapper["project"] = projectId;
            }

            return (JsonSerializer.Serialize(outerWrapper), isStream, targetModel);
        }

        /// <summary>
        /// Gemini Schema 允许的字段白名单（generativelanguage API FunctionDeclaration.parameters）。
        /// AI SDK / OpenAI 的 JSON Schema 常含 Gemini 不支持的字段（如 propertyNames、anyOf、$schema 等），
        /// 上游 proto 严格校验会直接 400 "Unknown name ... Cannot find field"，因此转换时递归清洗。
        /// 经真实上游逐字段探测收敛：examples、const、$ref、propertyNames、patternProperties、definitions、
        /// $defs、$schema、$id、dependencies、uniqueItems、multipleOf、allOf/anyOf/oneOf/not/contains、
        /// minContains/maxContains、propertyOrdering、deprecated、readOnly/writeOnly、title/default 均不被
        /// 原生 Gemini API 接受，故只保留以下确证支持的字段（与 CLIProxyAPI internal/util/gemini_schema.go
        /// 的黑名单互相印证）。
        /// </summary>
        private static readonly HashSet<string> GeminiSchemaAllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "type", "format", "description", "nullable", "enum", "items", "properties", "required",
            "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum",
            "minProperties", "maxProperties", "pattern", "additionalProperties"
        };

        /// <summary>
        /// 递归清洗 JSON Schema 为 Gemini 兼容结构（仅保留白名单字段）。
        /// 注意: "properties" 的键是用户属性名（任意），必须全部保留，只有值需要递归清洗。
        /// 若类型为 array 且缺少 items，补齐默认 items: { type: "string" } 避免上游 400。
        /// </summary>
        private static object? SanitizeGeminiSchema(JsonElement schema)
        {
            switch (schema.ValueKind)
            {
                case JsonValueKind.Object:
                    var result = new Dictionary<string, object?>();
                    foreach (var prop in schema.EnumerateObject())
                    {
                        if (!GeminiSchemaAllowedKeys.Contains(prop.Name)) continue;
                        if (prop.NameEquals("properties"))
                        {
                            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                            var propsDict = new Dictionary<string, object?>();
                            foreach (var p in prop.Value.EnumerateObject())
                                propsDict[p.Name] = SanitizeGeminiSchema(p.Value);
                            if (propsDict.Count > 0)
                                result["properties"] = propsDict;
                        }
                        else if (prop.NameEquals("additionalProperties"))
                        {
                            // Gemini 的 additionalProperties 仅支持 bool；AI SDK record 类型会生成对象形式的
                            // additionalProperties（属性约束），上游不支持，直接移除
                            if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                                result["additionalProperties"] = prop.Value.ValueKind == JsonValueKind.True;
                        }
                        else
                        {
                            result[prop.Name] = SanitizeGeminiSchema(prop.Value);
                        }
                    }
                    // 清洗后至少保留 type，避免空 schema 被上游拒绝
                    if (result.Count == 0)
                        result["type"] = "object";

                    // 对齐 CPA fix(b5cde4b)：如果 type 为 array 且没有 items，补默认 items 避免上游报错
                    if (result.TryGetValue("type", out var typeVal) && typeVal is string typeStr &&
                        typeStr.Equals("array", StringComparison.OrdinalIgnoreCase) && !result.ContainsKey("items"))
                    {
                        result["items"] = new Dictionary<string, object?> { ["type"] = "string" };
                    }

                    return result;
                case JsonValueKind.Array:
                    var list = new List<object?>();
                    foreach (var item in schema.EnumerateArray())
                        list.Add(SanitizeGeminiSchema(item));
                    return list;
                case JsonValueKind.String:
                    return schema.GetString();
                case JsonValueKind.Number:
                    return schema.TryGetInt32(out int i) ? (object)i : schema.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }

        /// <summary>
        /// 从 Gemini part 中读取 thought_signature（上游字段名为 camelCase thoughtSignature，兼容 snake_case 变体）。
        /// 该字段是 functionCall part 的兄弟字段，用于后续请求回传验证（缺失时上游报 400）。
        /// </summary>
        public static string? GetThoughtSignature(JsonElement part)
        {
            if (part.ValueKind != JsonValueKind.Object) return null;
            if (part.TryGetProperty("thoughtSignature", out var pTs) && pTs.ValueKind == JsonValueKind.String)
            {
                string sig = pTs.GetString() ?? "";
                return string.IsNullOrEmpty(sig) ? null : sig;
            }
            if (part.TryGetProperty("thought_signature", out var pTs2) && pTs2.ValueKind == JsonValueKind.String)
            {
                string sig = pTs2.GetString() ?? "";
                return string.IsNullOrEmpty(sig) ? null : sig;
            }
            return null;
        }

        /// <summary>
        /// 构造携带 thought_signature 的 OpenAI 兼容 call_id。
        /// 编码约定: "call_ts_" + base64url(thoughtSignature)，客户端原样回传后可由 TryExtractThoughtSignature 还原。
        /// 无签名时生成普通随机 call_id（兼容不需要签名的上游模型）。
        /// </summary>
        public static string BuildCallId(string? thoughtSignature)
        {
            if (!string.IsNullOrEmpty(thoughtSignature))
                return "call_ts_" + Base64UrlEncode(thoughtSignature);
            return "call_" + Guid.NewGuid().ToString("N").Substring(0, 24);
        }

        /// <summary>
        /// 从 call_id 中提取 thought_signature（仅识别 "call_ts_" 前缀的编码格式，解码失败返回 null）。
        /// </summary>
        public static string? TryExtractThoughtSignature(string? callId)
        {
            const string prefix = "call_ts_";
            if (string.IsNullOrEmpty(callId) || !callId.StartsWith(prefix, StringComparison.Ordinal))
                return null;
            try
            {
                string payload = callId.Substring(prefix.Length);
                payload = payload.Replace('-', '+').Replace('_', '/');
                switch (payload.Length % 4)
                {
                    case 2: payload += "=="; break;
                    case 3: payload += "="; break;
                    case 1: return null; // 非法长度
                }
                return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            }
            catch { return null; }
        }

        private static string Base64UrlEncode(string value)
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static List<object> ParseOpenAIMessageParts(JsonElement msg)
        {
            var parts = new List<object>();

            if (!msg.TryGetProperty("content", out var pContent))
            {
                // Responses API 顶层 item：function_call / function_call_output 没有 content 键，
                // 仅通过 type 标识（历史工具交互消息，之前会被静默丢弃导致第二轮丢失上下文）
                if (msg.TryGetProperty("type", out var pTopType))
                {
                    string topType = pTopType.GetString() ?? "";
                    if (topType.Equals("function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFunctionCallPart(parts, msg);
                    }
                    else if (topType.Equals("function_call_output", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFunctionCallOutputPart(parts, msg);
                    }
                }
                return parts;
            }

            if (pContent.ValueKind == JsonValueKind.String)
            {
                string text = pContent.GetString() ?? "";
                if (!string.IsNullOrEmpty(text))
                    parts.Add(new { text = text });
            }
            else if (pContent.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pContent.EnumerateArray())
                {
                    string type = item.TryGetProperty("type", out var pType) ? pType.GetString() ?? "" : "";
                    // AI SDK / OpenAI Responses 格式的文本类型: text / input_text / output_text
                    if (type.Equals("text", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("input_text", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("output_text", StringComparison.OrdinalIgnoreCase))
                    {
                        string text = item.TryGetProperty("text", out var pText) ? pText.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(text))
                            parts.Add(new { text = text });
                    }
                    // Responses 格式的 function_call (assistant 历史) -> Gemini functionCall part
                    else if (type.Equals("function_call", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFunctionCallPart(parts, item);
                    }
                    // Responses 格式的 function_call_output (工具结果) -> Gemini functionResponse part
                    else if (type.Equals("function_call_output", StringComparison.OrdinalIgnoreCase))
                    {
                        AddFunctionCallOutputPart(parts, item);
                    }
                    else if (type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("input_image", StringComparison.OrdinalIgnoreCase))
                    {
                        // image_url: {url: "data:...;base64,..."} 或 input_image: {image_url: "data:..."}
                        string urlStr = "";
                        if (item.TryGetProperty("image_url", out var pImgUrl))
                        {
                            if (pImgUrl.ValueKind == JsonValueKind.String)
                                urlStr = pImgUrl.GetString() ?? "";
                            else if (pImgUrl.TryGetProperty("url", out var pUrl))
                                urlStr = pUrl.GetString() ?? "";
                        }
                        if (!string.IsNullOrEmpty(urlStr))
                        {
                            var match = Regex.Match(urlStr, @"^data:(image\/[a-zA-Z0-9\+\-\.]+);base64,(.+)");
                            if (match.Success)
                            {
                                string mimeType = match.Groups[1].Value;
                                string base64Data = match.Groups[2].Value;
                                parts.Add(new
                                {
                                    inlineData = new
                                    {
                                        mimeType = mimeType,
                                        data = base64Data
                                    }
                                });
                            }
                        }
                    }
                }
            }

            if (parts.Count == 0)
            {
                parts.Add(new { text = "" });
            }
            return parts;
        }

        /// <summary>
        /// Responses 格式 function_call item -> Gemini functionCall part（附带 thought_signature 透传）
        /// </summary>
        private static void AddFunctionCallPart(List<object> parts, JsonElement item)
        {
            string fnName = item.TryGetProperty("name", out var pFnName) ? pFnName.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(fnName)) return;
            string argsStr = item.TryGetProperty("arguments", out var pFnArgs) ? pFnArgs.GetString() ?? "" : "";
            JsonNode? argsNode = null;
            try { argsNode = JsonNode.Parse(string.IsNullOrEmpty(argsStr) ? "{}" : argsStr); } catch { }

            // 从 call_id / id 中还原 thought_signature（代理响应时为 "call_ts_" 前缀编码）
            string callId = item.TryGetProperty("call_id", out var pCid) ? pCid.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(callId) && item.TryGetProperty("id", out var pId))
                callId = pId.GetString() ?? "";
            string? sig = TryExtractThoughtSignature(callId);

            var fnCallObj = new JsonObject
            {
                ["name"] = fnName,
                ["args"] = argsNode ?? new JsonObject()
            };
            var partObj = new JsonObject { ["functionCall"] = fnCallObj };
            if (!string.IsNullOrEmpty(sig)) partObj["thoughtSignature"] = sig;
            parts.Add(partObj);
        }

        /// <summary>
        /// Responses 格式 function_call_output item -> Gemini functionResponse part。
        /// name 优先取 item.name；缺失时回退到 call_id（上游实测可接受，见用户会话 400 分析），最后兜底 tool_result。
        /// </summary>
        private static void AddFunctionCallOutputPart(List<object> parts, JsonElement item)
        {
            string fnName = item.TryGetProperty("name", out var pFName) ? pFName.GetString() ?? "" : "";
            string callId = item.TryGetProperty("call_id", out var pCallId) ? pCallId.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(fnName))
                fnName = string.IsNullOrEmpty(callId) ? "tool_result" : callId;

            string outStr = "";
            if (item.TryGetProperty("output", out var pOut))
            {
                if (pOut.ValueKind == JsonValueKind.String) outStr = pOut.GetString() ?? "";
                else if (pOut.ValueKind == JsonValueKind.Array)
                {
                    // 兼容 output 数组形式（如 [{type:"text",text:"..."}]）
                    var sb = new StringBuilder();
                    foreach (var oi in pOut.EnumerateArray())
                    {
                        if (oi.TryGetProperty("text", out var pOT)) sb.Append(pOT.GetString());
                        else if (oi.ValueKind == JsonValueKind.String) sb.Append(oi.GetString());
                    }
                    outStr = sb.ToString();
                }
                else outStr = pOut.GetRawText();
            }

            parts.Add(new
            {
                functionResponse = new
                {
                    name = fnName,
                    response = new { result = string.IsNullOrEmpty(outStr) ? "(empty tool result)" : outStr }
                }
            });
        }

        public static string ConvertGeminiToOpenAIResponse(string geminiJson, string requestedModel)
        {
            string reqId = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string contentText = "";
            string finishReason = "stop";
            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            var toolCalls = new List<object>();

            try
            {
                using var doc = JsonDocument.Parse(geminiJson);
                var root = doc.RootElement;
                var targetRoot = root;
                if (root.TryGetProperty("response", out var pResp) && pResp.ValueKind == JsonValueKind.Object)
                {
                    targetRoot = pResp;
                }

                if (targetRoot.TryGetProperty("candidates", out var pCand) && pCand.ValueKind == JsonValueKind.Array && pCand.GetArrayLength() > 0)
                {
                    var firstCand = pCand[0];
                    if (firstCand.TryGetProperty("content", out var pCnt) && pCnt.TryGetProperty("parts", out var pParts) && pParts.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in pParts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var pTxt))
                                sb.Append(pTxt.GetString());
                            else if (part.TryGetProperty("functionCall", out var pFnCall) && pFnCall.ValueKind == JsonValueKind.Object)
                            {
                                string fnName = pFnCall.TryGetProperty("name", out var pFnN) ? pFnN.GetString() ?? "" : "";
                                string argsStr = "";
                                if (pFnCall.TryGetProperty("args", out var pFnA))
                                {
                                    argsStr = pFnA.ValueKind == JsonValueKind.String ? pFnA.GetString() ?? "" : pFnA.GetRawText();
                                }
                                // call_id 携带 thought_signature（"call_ts_" 编码），供客户端原样回传以满足上游校验
                                string callId = BuildCallId(GetThoughtSignature(part));
                                toolCalls.Add(new
                                {
                                    id = callId,
                                    type = "function",
                                    function = new
                                    {
                                        name = fnName,
                                        arguments = argsStr
                                    }
                                });
                            }
                        }
                        contentText = sb.ToString();
                    }

                    if (firstCand.TryGetProperty("finishReason", out var pFR))
                    {
                        string fr = pFR.GetString() ?? "";
                        finishReason = MapFinishReason(fr);
                    }
                    if (toolCalls.Count > 0 && finishReason == "stop")
                        finishReason = "tool_calls";
                }

                if (root.TryGetProperty("usageMetadata", out var pUsage))
                {
                    if (pUsage.TryGetProperty("promptTokenCount", out var pPT)) promptTokens = pPT.GetInt32();
                    if (pUsage.TryGetProperty("candidatesTokenCount", out var pCT)) completionTokens = pCT.GetInt32();
                    if (pUsage.TryGetProperty("totalTokenCount", out var pTT)) totalTokens = pTT.GetInt32();
                }
            }
            catch { }

            var res = new
            {
                id = reqId,
                @object = "chat.completion",
                created = created,
                model = requestedModel,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = contentText,
                            tool_calls = toolCalls.Count > 0 ? toolCalls.ToArray() : null
                        },
                        finish_reason = finishReason
                    }
                },
                usage = new
                {
                    prompt_tokens = promptTokens,
                    completion_tokens = completionTokens,
                    total_tokens = totalTokens
                }
            };

            return JsonSerializer.Serialize(res);
        }

        public static string BuildOpenAISseChunk(string reqId, long created, string requestedModel, string contentChunk, string? finishReason)
        {
            object choiceObj;
            if (finishReason != null)
            {
                choiceObj = new
                {
                    index = 0,
                    delta = new { },
                    finish_reason = finishReason
                };
            }
            else
            {
                choiceObj = new
                {
                    index = 0,
                    delta = new { content = contentChunk },
                    finish_reason = (string?)null
                };
            }

            var chunkObj = new
            {
                id = reqId,
                @object = "chat.completion.chunk",
                created = created,
                model = requestedModel,
                choices = new[] { choiceObj }
            };

            return "data: " + JsonSerializer.Serialize(chunkObj) + "\n\n";
        }

        /// <summary>
        /// 构造 OpenAI 流式 tool_calls 增量 chunk
        /// </summary>
        public static string BuildOpenAIToolCallSseChunk(string reqId, long created, string requestedModel, string callId, string fnName, string argsJson)
        {
            var chunkObj = new
            {
                id = reqId,
                @object = "chat.completion.chunk",
                created = created,
                model = requestedModel,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new
                        {
                            tool_calls = new[]
                            {
                                new
                                {
                                    index = 0,
                                    id = callId,
                                    type = "function",
                                    function = new { name = fnName, arguments = argsJson }
                                }
                            }
                        },
                        finish_reason = (string?)null
                    }
                }
            };

            return "data: " + JsonSerializer.Serialize(chunkObj) + "\n\n";
        }

        // ============ OpenAI Responses API (/v1/responses) 转换 ============

        /// <summary>
        /// 把 Gemini generateContent 的非流式响应转换为 OpenAI Responses API JSON 对象
        /// </summary>
        public static string ConvertGeminiToResponsesResponse(string geminiJson, string requestedModel)
        {
            string respId = "resp_" + Guid.NewGuid().ToString("N").Substring(0, 24);
            string msgId = "msg_" + Guid.NewGuid().ToString("N").Substring(0, 24);
            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string contentText = "";
            int promptTokens = 0, completionTokens = 0, totalTokens = 0;
            var functionCalls = new List<object>();

            try
            {
                using var doc = JsonDocument.Parse(geminiJson);
                var root = doc.RootElement;
                var targetRoot = root;
                if (root.TryGetProperty("response", out var pResp) && pResp.ValueKind == JsonValueKind.Object)
                {
                    targetRoot = pResp;
                }

                if (targetRoot.TryGetProperty("candidates", out var pCand) && pCand.ValueKind == JsonValueKind.Array && pCand.GetArrayLength() > 0)
                {
                    var firstCand = pCand[0];
                    if (firstCand.TryGetProperty("content", out var pCnt) && pCnt.TryGetProperty("parts", out var pParts) && pParts.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in pParts.EnumerateArray())
                        {
                            if (part.TryGetProperty("text", out var pTxt))
                                sb.Append(pTxt.GetString());
                            else if (part.TryGetProperty("functionCall", out var pFnCall) && pFnCall.ValueKind == JsonValueKind.Object)
                            {
                                string fnName = pFnCall.TryGetProperty("name", out var pFnN) ? pFnN.GetString() ?? "" : "";
                                string argsStr = "";
                                if (pFnCall.TryGetProperty("args", out var pFnA))
                                    argsStr = pFnA.ValueKind == JsonValueKind.String ? pFnA.GetString() ?? "" : pFnA.GetRawText();
                                // call_id 携带 thought_signature（"call_ts_" 编码），供客户端原样回传以满足上游校验
                                string callId = BuildCallId(GetThoughtSignature(part));
                                functionCalls.Add(new
                                {
                                    type = "function_call",
                                    id = callId,
                                    call_id = callId,
                                    name = fnName,
                                    arguments = argsStr,
                                    status = "completed"
                                });
                            }
                        }
                        contentText = sb.ToString();
                    }
                }

                if (root.TryGetProperty("usageMetadata", out var pUsage))
                {
                    if (pUsage.TryGetProperty("promptTokenCount", out var pPT)) promptTokens = pPT.GetInt32();
                    if (pUsage.TryGetProperty("candidatesTokenCount", out var pCT)) completionTokens = pCT.GetInt32();
                    if (pUsage.TryGetProperty("totalTokenCount", out var pTT)) totalTokens = pTT.GetInt32();
                }
            }
            catch { }

            var outputList = new List<object>();
            if (!string.IsNullOrEmpty(contentText))
            {
                outputList.Add(new
                {
                    type = "message",
                    id = msgId,
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new { type = "output_text", text = contentText, annotations = new object[] { } }
                    }
                });
            }
            foreach (var fc in functionCalls)
            {
                outputList.Add(fc);
            }
            // 既无文本也无工具调用时给一个空消息，避免客户端认为无内容
            if (outputList.Count == 0)
            {
                outputList.Add(new
                {
                    type = "message",
                    id = msgId,
                    status = "completed",
                    role = "assistant",
                    content = new object[] { }
                });
            }

            var res = new
            {
                id = respId,
                @object = "response",
                created_at = created,
                status = "completed",
                model = requestedModel,
                output = outputList.ToArray(),
                usage = new
                {
                    input_tokens = promptTokens,
                    output_tokens = completionTokens,
                    total_tokens = totalTokens,
                    input_tokens_details = new { cached_tokens = 0 },
                    output_tokens_details = new { reasoning_tokens = 0 }
                }
            };

            return JsonSerializer.Serialize(res);
        }

        /// <summary>
        /// 构造 Responses API 的 SSE 事件消息（event: 行 + data: 行）
        /// </summary>
        public static string BuildResponsesSse(string eventName, object payload)
        {
            return "event: " + eventName + "\ndata: " + JsonSerializer.Serialize(payload) + "\n\n";
        }

        /// <summary>
        /// 构造 Responses API 流式开始事件 (response.created)
        /// </summary>
        public static string BuildResponsesSseCreated(string respId, string itemId, long created, string model)
        {
            return BuildResponsesSse("response.created", new
            {
                type = "response.created",
                response = new
                {
                    id = respId,
                    @object = "response",
                    created_at = created,
                    status = "in_progress",
                    model = model,
                    output = new object[] { }
                }
            });
        }

        /// <summary>
        /// 构造 Responses API 流式输出项添加事件 (response.output_item.added)
        /// </summary>
        public static string BuildResponsesSseItemAdded(string respId, string itemId)
        {
            return BuildResponsesSse("response.output_item.added", new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new
                {
                    id = itemId,
                    type = "message",
                    status = "in_progress",
                    role = "assistant",
                    content = new object[] { }
                },
                response_id = respId
            });
        }

        /// <summary>
        /// 构造 Responses API 流式内容部分添加事件 (response.content_part.added)
        /// </summary>
        public static string BuildResponsesSsePartAdded(string respId, string itemId)
        {
            return BuildResponsesSse("response.content_part.added", new
            {
                type = "response.content_part.added",
                item_id = itemId,
                output_index = 0,
                content_index = 0,
                part = new
                {
                    type = "output_text",
                    text = "",
                    annotations = new object[] { }
                },
                response_id = respId
            });
        }

        /// <summary>
        /// 构造 Responses API 流式文本增量事件 (response.output_text.delta)
        /// </summary>
        public static string BuildResponsesSseDelta(string itemId, string delta)
        {
            return BuildResponsesSse("response.output_text.delta", new
            {
                type = "response.output_text.delta",
                item_id = itemId,
                output_index = 0,
                content_index = 0,
                delta = delta
            });
        }

        /// <summary>
        /// 构造 Responses API 流式函数调用输出项添加事件 (response.output_item.added, type=function_call)
        /// </summary>
        public static string BuildResponsesSseFunctionCallAdded(string respId, string callId, string fnName)
        {
            return BuildResponsesSse("response.output_item.added", new
            {
                type = "response.output_item.added",
                output_index = 0,
                item = new
                {
                    id = callId,
                    type = "function_call",
                    status = "in_progress",
                    call_id = callId,
                    name = fnName,
                    arguments = ""
                },
                response_id = respId
            });
        }

        /// <summary>
        /// 构造 Responses API 流式函数参数增量事件 (response.function_call_arguments.delta)
        /// </summary>
        public static string BuildResponsesSseFunctionCallArgsDelta(string callId, string argsChunk)
        {
            return BuildResponsesSse("response.function_call_arguments.delta", new
            {
                type = "response.function_call_arguments.delta",
                item_id = callId,
                output_index = 0,
                delta = argsChunk
            });
        }

        /// <summary>
        /// 构造 Responses API 流式函数参数完成事件 (response.function_call_arguments.done + response.output_item.done)
        /// </summary>
        public static string BuildResponsesSseFunctionCallDone(string callId, string fullArgs)
        {
            var argsDone = BuildResponsesSse("response.function_call_arguments.done", new
            {
                type = "response.function_call_arguments.done",
                item_id = callId,
                output_index = 0,
                arguments = fullArgs
            });

            var itemDone = BuildResponsesSse("response.output_item.done", new
            {
                type = "response.output_item.done",
                output_index = 0,
                item = new
                {
                    id = callId,
                    type = "function_call",
                    status = "completed",
                    call_id = callId,
                    name = "",
                    arguments = fullArgs
                },
                response_id = ""
            });

            return argsDone + itemDone;
        }

        /// <summary>
        /// 构造 Responses API 流式完成事件 (response.output_text.done + response.completed)
        /// extraOutputItems 用于在 completed.response.output 中附带 function_call 等额外输出项
        /// </summary>
        public static string BuildResponsesSseCompleted(string respId, string itemId, long created, string model, string fullText, int promptTokens, int completionTokens, int totalTokens, object[]? extraOutputItems = null)
        {
            var doneEvent = BuildResponsesSse("response.output_text.done", new
            {
                type = "response.output_text.done",
                item_id = itemId,
                output_index = 0,
                content_index = 0,
                text = fullText
            });

            var outputList = new List<object>();
            if (!string.IsNullOrEmpty(fullText))
            {
                outputList.Add(new
                {
                    type = "message",
                    id = itemId,
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new { type = "output_text", text = fullText, annotations = new object[] { } }
                    }
                });
            }
            if (extraOutputItems != null)
            {
                foreach (var item in extraOutputItems)
                {
                    outputList.Add(item);
                }
            }
            if (outputList.Count == 0)
            {
                outputList.Add(new
                {
                    type = "message",
                    id = itemId,
                    status = "completed",
                    role = "assistant",
                    content = new object[] { }
                });
            }

            var completedPayload = new
            {
                type = "response.completed",
                response = new
                {
                    id = respId,
                    @object = "response",
                    created_at = created,
                    status = "completed",
                    model = model,
                    output = outputList.ToArray(),
                    usage = new
                    {
                        input_tokens = promptTokens,
                        output_tokens = completionTokens,
                        total_tokens = totalTokens,
                        input_tokens_details = new { cached_tokens = 0 },
                        output_tokens_details = new { reasoning_tokens = 0 }
                    }
                }
            };
            var completedEvent = BuildResponsesSse("response.completed", completedPayload);

            return doneEvent + completedEvent;
        }

        /// <summary>
        /// 构造 Responses API 流式失败/不完整事件 (response.incomplete)，用于异常结束
        /// </summary>
        public static string BuildResponsesSseIncomplete(string respId, long created, string model)
        {
            return BuildResponsesSse("response.incomplete", new
            {
                type = "response.incomplete",
                response = new
                {
                    id = respId,
                    @object = "response",
                    created_at = created,
                    status = "incomplete",
                    model = model,
                    output = new object[] { }
                }
            });
        }

        private static string MapFinishReason(string geminiReason)
        {
            if (string.Equals(geminiReason, "STOP", StringComparison.OrdinalIgnoreCase)) return "stop";
            if (string.Equals(geminiReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)) return "length";
            if (string.Equals(geminiReason, "SAFETY", StringComparison.OrdinalIgnoreCase)) return "content_filter";
            return "stop";
        }
    }
}
