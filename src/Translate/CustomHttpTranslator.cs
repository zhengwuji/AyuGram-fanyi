using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// 通用「自定义 HTTP 接口」翻译器。
    ///
    /// 请求体模板里可用占位符：
    ///   {text}        原文（自动做 JSON 字符串转义）
    ///   {text_raw}    原文（不做转义，用于表单/纯文本接口）
    ///   {target}      目标语言界面代码，例如 zh-CN
    ///   {source}      源语言界面代码，auto 表示自动
    ///   {target_name} 目标语言英文名，例如 Simplified Chinese
    ///
    /// 响应取值用「点路径」描述，例如：
    ///   data.translation
    ///   choices.0.message.content
    ///   translations.0.text
    ///   result.text
    /// </summary>
    public sealed class CustomHttpTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;

        public CustomHttpTranslator(AppConfig cfg)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        }

        public override string Name => "自定义 HTTP";
        public override bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiEndpoint);
        public override string ConfigurationHint => "请填写「自定义接口地址」与「请求体模板」。";

        public override async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            if (!IsConfigured) return TranslationResult.Fail(ConfigurationHint, Name);

            string text = request.Text ?? "";
            string template = string.IsNullOrWhiteSpace(_cfg.RequestTemplate)
                ? "{\"text\":\"{text}\",\"target_lang\":\"{target}\"}"
                : _cfg.RequestTemplate;

            string body = template
                .Replace("{text}", JsonEscape(text))
                .Replace("{text_raw}", text)
                .Replace("{target}", request.TargetLanguage ?? "zh-CN")
                .Replace("{source}", request.SourceLanguage ?? "auto")
                .Replace("{target_name}", LanguageMap.PromptNameOf(request.TargetLanguage))
                .Replace("{source_name}", LanguageMap.PromptNameOf(request.SourceLanguage));
            // 常见别名
            body = body.Replace("{target_lang}", request.TargetLanguage ?? "zh-CN")
                       .Replace("{targetLang}", request.TargetLanguage ?? "zh-CN")
                       .Replace("{q}", JsonEscape(text));

            string url = _cfg.ApiEndpoint.Trim();
            // 支持把文本直接放进 URL（GET 风格接口）
            if (url.Contains("{text}") || url.Contains("{text_raw}") || url.Contains("{q}"))
            {
                url = url.Replace("{text}", Uri.EscapeDataString(text))
                         .Replace("{text_raw}", Uri.EscapeDataString(text))
                         .Replace("{q}", Uri.EscapeDataString(text))
                         .Replace("{target}", Uri.EscapeDataString(request.TargetLanguage ?? "zh-CN"))
                         .Replace("{source}", Uri.EscapeDataString(request.SourceLanguage ?? "auto"));

                var (okGet, bodyGet, errGet) = await Http.SendWithRetryAsync(
                    () =>
                    {
                        var r = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
                            r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey.Trim());
                        Http.ApplyExtraHeaders(r, _cfg.ExtraHeaders);
                        return r;
                    },
                    _cfg.TimeoutSeconds, _cfg.MaxRetries, ct).ConfigureAwait(false);

                return BuildResult(okGet, bodyGet, errGet);
            }

            var (ok, respBody, err) = await Http.SendWithRetryAsync(
                () =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = Http.JsonContent(body) };
                    if (!string.IsNullOrWhiteSpace(_cfg.ApiKey))
                        r.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _cfg.ApiKey.Trim());
                    Http.ApplyExtraHeaders(r, _cfg.ExtraHeaders);
                    return r;
                },
                _cfg.TimeoutSeconds, _cfg.MaxRetries, ct).ConfigureAwait(false);

            return BuildResult(ok, respBody, err);
        }

        private TranslationResult BuildResult(bool ok, string body, string err)
        {
            if (!ok) return TranslationResult.Fail(err, Name);

            string value = ExtractByPath(body, _cfg.ResponsePath);
            if (string.IsNullOrWhiteSpace(value))
            {
                return TranslationResult.Fail(
                    "按路径 \"" + (_cfg.ResponsePath ?? "") + "\" 取值失败，请检查「响应取值路径」设置",
                    Name, TextUtil.Truncate(body, 800));
            }

            value = TextUtil.CleanTranslationOutput(value);
            if (string.IsNullOrWhiteSpace(value))
                return TranslationResult.Fail("接口返回空内容", Name, TextUtil.Truncate(body, 800));

            return TranslationResult.Ok(value, Name, TextUtil.Truncate(body, 400));
        }

        /// <summary>按 "a.b.0.c" 这样的点路径从 JSON 里取值。</summary>
        internal static string ExtractByPath(string json, string path)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    JsonElement cur = doc.RootElement;
                    foreach (var rawSeg in path.Split('.'))
                    {
                        string seg = rawSeg.Trim();
                        if (seg.Length == 0) continue;

                        if (cur.ValueKind == JsonValueKind.Array)
                        {
                            if (!int.TryParse(seg, out int idx)) return null;
                            if (idx < 0 || idx >= cur.GetArrayLength()) return null;
                            cur = cur[idx];
                        }
                        else if (cur.ValueKind == JsonValueKind.Object)
                        {
                            if (!cur.TryGetProperty(seg, out var next))
                            {
                                // 尝试大小写不敏感匹配
                                bool found = false;
                                foreach (var p in cur.EnumerateObject())
                                {
                                    if (string.Equals(p.Name, seg, StringComparison.OrdinalIgnoreCase))
                                    {
                                        next = p.Value;
                                        found = true;
                                        break;
                                    }
                                }
                                if (!found) return null;
                            }
                            cur = next;
                        }
                        else
                        {
                            return null;
                        }
                    }

                    if (cur.ValueKind == JsonValueKind.String) return cur.GetString();
                    if (cur.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var e in cur.EnumerateArray())
                        {
                            if (e.ValueKind == JsonValueKind.String) sb.Append(e.GetString());
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                    return cur.ToString();
                }
            }
            catch (JsonException ex)
            {
                Log.Debug("响应 JSON 解析失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>把一个字符串转义成 JSON 字符串字面量内部的形式（不含首尾引号）。</summary>
        internal static string JsonEscape(string s)
        {
            if (s == null) return "";
            string quoted = JsonSerializer.Serialize(s);
            if (quoted.Length >= 2 && quoted[0] == '"' && quoted[quoted.Length - 1] == '"')
                return quoted.Substring(1, quoted.Length - 2);
            return quoted;
        }
    }
}
