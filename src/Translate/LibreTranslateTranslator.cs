using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>自建 LibreTranslate 实例（/translate）。</summary>
    public sealed class LibreTranslateTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;

        public LibreTranslateTranslator(AppConfig cfg) { _cfg = cfg; }

        public override string Name => "LibreTranslate";

        /// <summary>留空时使用常见的本地默认地址（与旧行为一致）。</summary>
        private string Endpoint =>
            string.IsNullOrWhiteSpace(_cfg.ApiEndpoint)
                ? "http://127.0.0.1:5000/translate"
                : _cfg.ApiEndpoint.Trim();

        public override bool IsConfigured => true;

        public override string ConfigurationHint => "自建 LibreTranslate 地址留空时默认 http://127.0.0.1:5000/translate";

        public override async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            if (!IsConfigured) return TranslationResult.Fail(ConfigurationHint, Name);

            string source = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : request.SourceLanguage;
            var payload = new Dictionary<string, object>
            {
                ["q"] = request.Text ?? "",
                ["source"] = source,
                ["target"] = LanguageMap.ToLibre(request.TargetLanguage),
                ["format"] = "text",
            };
            if (!string.IsNullOrWhiteSpace(_cfg.ApiKey)) payload["api_key"] = _cfg.ApiKey.Trim();

            string json = JsonSerializer.Serialize(payload);
            string url = Endpoint;

            var (ok, body, err) = await Http.SendWithRetryAsync(
                () =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = Http.JsonContent(json) };
                    Http.ApplyExtraHeaders(r, _cfg.ExtraHeaders);
                    return r;
                },
                _cfg.TimeoutSeconds, _cfg.MaxRetries, ct).ConfigureAwait(false);

            if (!ok) return TranslationResult.Fail(err, Name);

            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("translatedText", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        string text = TextUtil.CleanTranslationOutput(t.GetString());
                        if (string.IsNullOrWhiteSpace(text)) return TranslationResult.Fail("LibreTranslate 返回空内容", Name);
                        return TranslationResult.Ok(text, Name, TextUtil.Truncate(body, 300));
                    }
                    if (root.TryGetProperty("error", out var e))
                        return TranslationResult.Fail("LibreTranslate 错误：" + e, Name);
                }
            }
            catch (JsonException ex)
            {
                return TranslationResult.Fail("响应解析失败：" + ex.Message, Name, TextUtil.Truncate(body, 400));
            }

            return TranslationResult.Fail("响应结构无法识别", Name, TextUtil.Truncate(body, 400));
        }
    }
}
