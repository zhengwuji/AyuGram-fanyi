using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// 任意 OpenAI 兼容 /chat/completions 接口。
    /// 适用于 OpenAI、DeepSeek、Moonshot、通义、硅基流动、Ollama、vLLM、LM Studio、OneAPI/NewAPI 网关等。
    /// </summary>
    public sealed class OpenAiCompatibleTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;

        public OpenAiCompatibleTranslator(AppConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        public override string Name => "OpenAI 兼容 (" + DescribeEndpoint() + ")";

        public override bool IsConfigured =>
            !string.IsNullOrWhiteSpace(EffectiveEndpoint()) && !string.IsNullOrWhiteSpace(_cfg.Model);

        public override string ConfigurationHint =>
            IsConfigured ? "" : "请填写「API 地址」与「模型名」。本地 Ollama 可填 http://127.0.0.1:11434/v1";

        private string DescribeEndpoint()
        {
            string url = EffectiveEndpoint();
            if (string.IsNullOrWhiteSpace(url)) return "未配置";
            try
            {
                var uri = new Uri(url);
                return uri.Host + uri.AbsolutePath;
            }
            catch { return url; }
        }

        private string EffectiveEndpoint()
        {
            if (!string.IsNullOrWhiteSpace(_cfg.ApiEndpoint)) return _cfg.ApiEndpoint.Trim();
            if (string.IsNullOrWhiteSpace(_cfg.ApiBaseUrl)) return "";
            return AppConfig.CombineUrl(_cfg.ApiBaseUrl, "chat/completions");
        }

        private string BuildSystemPrompt(string target)
        {
            string targetName = LanguageMap.PromptNameOf(target);
            string prompt = _cfg.SystemPrompt ?? "";
            if (prompt.IndexOf("{target}", StringComparison.OrdinalIgnoreCase) >= 0)
                prompt = Regex.Replace(prompt, @"\{target\}", targetName, RegexOptions.IgnoreCase);
            else
                prompt = prompt + " Target language: " + targetName + ".";
            return prompt;
        }

        /// <summary>批量请求用的系统提示词：强调逐段对应、禁止任何额外输出。</summary>
        private static string BuildBatchSystemPrompt()
        {
            return "You are a batch translation engine. " +
                   "You always answer strictly in the ###<n>###<translation> format, " +
                   "one line per input segment, with no extra commentary. " +
                   "Never explain, never add notes, never repeat the source text. " +
                   "If a segment is already in the target language or is unreadable, " +
                   "repeat it back verbatim.";
        }

        /// <summary>
        /// 抑制推理型模型的「思考」开销。
        ///
        /// 实测（本地网关 + DeepSeek 系模型）：模型把思考过程写在独立的
        /// reasoning_content 字段里，正文仍正常。但思考本身会消耗 completion token —
        /// 面对乱码 OCR 文本时从几百暴涨到 5400+，单次请求 3 秒变 47 秒。
        /// 这里先请求关闭思考；服务商不支持时下面的 max_tokens 会兜住。
        /// </summary>
        private static void ApplyAntiReasoningGuards(Dictionary<string, object> payload)
        {
            payload["reasoning_effort"] = "none";
            payload["enable_thinking"] = false;
            payload["thinking"] = new Dictionary<string, object> { ["type"] = "disabled" };
        }

        /// <summary>
        /// 估算 max_tokens。
        ///
        /// 关键点：max_tokens 限制的是「思考 + 正文」的总量，不是正文长度。
        /// 所以预算必须按输入规模放大，否则推理型模型会把额度耗在思考上，
        /// 导致 finish_reason=length、content 为空。
        /// </summary>
        private static int EstimateMaxTokens(int inputChars, bool batch, int segmentCount)
        {
            int segments = Math.Max(1, segmentCount);
            int avgChars = Math.Max(1, inputChars / segments);

            // 每段正文的 token 预算：中文约占字符数的 1.5 倍，再留余量
            int perSegmentBody = Math.Max(120, (int)(avgChars * 3.5));

            // 推理开销按正文的 4 倍估（实测乱码文本推理可达正文的十倍以上，
            // 但给太多预算等于不设限，4 倍是速度与成功率的折中）
            int reasoningFactor = batch ? 4 : 5;

            int budget = batch
                ? perSegmentBody * segments * reasoningFactor / 2
                : perSegmentBody * reasoningFactor;

            return Math.Max(512, Math.Min(budget, 16384));
        }

        public override async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            if (!IsConfigured) return TranslationResult.Fail(ConfigurationHint, Name);

            var payload = new Dictionary<string, object>
            {
                ["model"] = _cfg.Model,
                ["temperature"] = _cfg.Temperature,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new { role = "system", content = BuildSystemPrompt(request.TargetLanguage) },
                    new { role = "user", content = request.Text ?? "" },
                },
            };
            ApplyAntiReasoningGuards(payload);
            payload["max_tokens"] = EstimateMaxTokens((request.Text ?? "").Length, false, 1);

            string url = EffectiveEndpoint();

            var (httpOk, content, httpErr) = await SendAndExtractAsync(url, payload, ct).ConfigureAwait(false);
            if (!httpOk)
            {
                // HTTP 层失败（401/404/429/超时…）：如实上报，放大 max_tokens 重试毫无意义
                return TranslationResult.Fail(httpErr, Name);
            }
            if (content == null)
            {
                // 可能是推理吃光了 max_tokens 导致正文为空，放大额度重试一次
                payload["max_tokens"] = Math.Min(16384, ((int)payload["max_tokens"]) * 3);
                Log.Debug("单条翻译首次无正文，放大 max_tokens 重试：" + payload["max_tokens"]);
                (httpOk, content, httpErr) = await SendAndExtractAsync(url, payload, ct).ConfigureAwait(false);
                if (!httpOk) return TranslationResult.Fail(httpErr, Name);
                if (content == null)
                    return TranslationResult.Fail("模型未返回正文（可能推理耗尽输出额度）", Name);
            }

            return TranslationResult.Ok(TextUtil.CleanTranslationOutput(content), Name);
        }

        /// <summary>
        /// 发一次请求并取出正文。
        /// HttpOk=false 时 Error 是 HTTP 层错误；HttpOk=true 且 Content=null 表示拿到了响应但没解析出正文。
        /// </summary>
        private async Task<(bool HttpOk, string Content, string Error)> SendAndExtractAsync(
            string url, Dictionary<string, object> payload, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(payload, JsonOpts);

            var (ok, body, err) = await Http.SendWithRetryAsync(
                () =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = Http.JsonContent(json) };
                    if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey.Trim());
                    Http.ApplyExtraHeaders(req, _cfg.ExtraHeaders);
                    return req;
                },
                _cfg.TimeoutSeconds, _cfg.MaxRetries, ct).ConfigureAwait(false);

            if (!ok)
            {
                Log.Debug("请求失败：" + err);
                return (false, null, err ?? "请求失败");
            }

            string content = TryExtractContent(body, out string extractError);
            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Debug("未取到正文：" + (extractError ?? "内容为空"));
                return (true, null, extractError ?? "内容为空");
            }
            return (true, content, null);
        }

        /// <summary>
        /// 批量翻译：一次请求把多段文本一起发过去，用编号分隔，模型按编号返回。
        /// 这样能显著减少请求数，也更容易让模型保持上下文。
        /// </summary>
        public override async Task<List<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> requests, CancellationToken ct)
        {
            var results = new List<TranslationResult>(requests.Count);
            if (requests.Count == 0) return results;

            // 只有一段就直接走单条
            if (requests.Count == 1)
            {
                results.Add(await TranslateAsync(requests[0], ct).ConfigureAwait(false));
                return results;
            }

            if (!IsConfigured)
            {
                foreach (var _ in requests) results.Add(TranslationResult.Fail(ConfigurationHint, Name));
                return results;
            }

            string target = requests[0].TargetLanguage;
            int totalChars = 0;
            var sb = new StringBuilder();
            sb.AppendLine("Translate each numbered segment below into " + LanguageMap.PromptNameOf(target) + ".");
            sb.AppendLine("Keep exactly the same numbering. Output ONLY the translated segments, one per line, in the form:");
            sb.AppendLine("###<n>###<translation>");
            sb.AppendLine("Do not merge or split segments. Preserve line breaks inside a segment as \\n.");
            sb.AppendLine("Segments may be screen-OCR output with noise; translate them as-is without commenting on them.");
            sb.AppendLine();

            for (int i = 0; i < requests.Count; i++)
            {
                string t = requests[i].Text ?? "";
                totalChars += t.Length;
                sb.Append("###").Append(i + 1).Append("###");
                sb.Append(t.Replace("\r\n", "\n").Replace("\n", "\\n"));
                sb.AppendLine();
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = _cfg.Model,
                ["temperature"] = _cfg.Temperature,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new { role = "system", content = BuildBatchSystemPrompt() },
                    new { role = "user", content = sb.ToString() },
                },
            };
            ApplyAntiReasoningGuards(payload);
            payload["max_tokens"] = EstimateMaxTokens(totalChars, true, requests.Count);

            string url = EffectiveEndpoint();

            var (httpOk, content, httpErr) = await SendAndExtractAsync(url, payload, ct).ConfigureAwait(false);
            if (!httpOk)
            {
                // HTTP 层失败（401/429/超时…）：逐条重发只会把同样的失败重复 N 次，整体如实报错
                Log.Debug("批量翻译 HTTP 失败：" + httpErr);
                for (int i = 0; i < requests.Count; i++)
                    results.Add(TranslationResult.Fail(httpErr, Name));
                return results;
            }

            // 推理型模型可能把输出额度耗在思考上，导致正文被截断成空串或缺段。
            // 这时放大 max_tokens 重发一次，仍不行才退化为逐条。
            if (content == null || ParseBatch(content, requests.Count) == null)
            {
                int bumped = Math.Min(16384, ((int)payload["max_tokens"]) * 3);
                if (bumped > (int)payload["max_tokens"])
                {
                    Log.Debug($"批量首次不可用，放大 max_tokens 到 {bumped} 重试");
                    payload["max_tokens"] = bumped;
                    var (ok2, retry, err2) = await SendAndExtractAsync(url, payload, ct).ConfigureAwait(false);
                    if (!ok2)
                    {
                        for (int i = 0; i < requests.Count; i++)
                            results.Add(TranslationResult.Fail(err2, Name));
                        return results;
                    }
                    if (retry != null) content = retry;
                }
            }

            if (content == null)
            {
                Log.Debug("批量翻译失败，退化为逐条");
                return await base.TranslateBatchAsync(requests, ct).ConfigureAwait(false);
            }

            var parsed = ParseBatch(content, requests.Count);
            if (parsed == null || parsed.Count != requests.Count)
            {
                Log.Debug("批量响应无法解析（" + (parsed?.Count ?? 0) + "/" + requests.Count + "），退化为逐条");
                return await base.TranslateBatchAsync(requests, ct).ConfigureAwait(false);
            }

            for (int i = 0; i < requests.Count; i++)
            {
                string t = TextUtil.CleanTranslationOutput(parsed[i] ?? "");
                if (string.IsNullOrWhiteSpace(t))
                {
                    // 单段失败就退化为单条请求
                    var single = await TranslateAsync(requests[i], ct).ConfigureAwait(false);
                    results.Add(single);
                }
                else
                {
                    results.Add(TranslationResult.Ok(t, Name));
                }
            }
            return results;
        }

        internal static List<string> ParseBatch(string content, int expected)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            var map = new Dictionary<int, string>();
            var matches = Regex.Matches(content, @"###\s*(\d+)\s*###", RegexOptions.Multiline);
            for (int i = 0; i < matches.Count; i++)
            {
                int idx = int.Parse(matches[i].Groups[1].Value);
                int start = matches[i].Index + matches[i].Length;
                int end = (i + 1 < matches.Count) ? matches[i + 1].Index : content.Length;
                string val = content.Substring(start, end - start).Trim();
                val = val.Replace("\\n", "\n");
                if (!map.ContainsKey(idx)) map[idx] = val;
            }

            if (map.Count == 0) return null;

            var list = new List<string>(expected);
            for (int i = 1; i <= expected; i++)
            {
                list.Add(map.TryGetValue(i, out var v) ? v : null);
            }
            return list;
        }

        internal static string TryExtractContent(string body, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(body))
            {
                error = "响应为空";
                return null;
            }

            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;

                    // 标准 OpenAI
                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                    {
                        var c0 = choices[0];
                        if (c0.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var contentEl))
                        {
                            string text = ReadContentValue(contentEl);
                            if (!string.IsNullOrWhiteSpace(text)) return text;
                        }
                        if (c0.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                            return textEl.GetString();
                    }

                    // 一些网关会包一层
                    if (root.TryGetProperty("data", out var data))
                    {
                        if (data.ValueKind == JsonValueKind.String) return data.GetString();
                        if (data.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
                            return dc.GetString();
                        if (data.TryGetProperty("translation", out var dt) && dt.ValueKind == JsonValueKind.String)
                            return dt.GetString();
                    }

                    if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String)
                        return resp.GetString();

                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        error = "服务端错误：" + (errorEl.ValueKind == JsonValueKind.String
                            ? errorEl.GetString()
                            : TextUtil.Truncate(errorEl.ToString(), 300));
                        return null;
                    }
                }
            }
            catch (JsonException ex)
            {
                error = "响应不是合法 JSON：" + ex.Message;
                return null;
            }

            error = "响应结构无法识别";
            return null;
        }

        private static string ReadContentValue(JsonElement contentEl)
        {
            if (contentEl.ValueKind == JsonValueKind.String) return contentEl.GetString();

            // 新版 OpenAI 会返回 content 数组（多模态）
            if (contentEl.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in contentEl.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String) sb.Append(part.GetString());
                    else if (part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                }
                return sb.ToString();
            }
            return null;
        }

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
    }
}
