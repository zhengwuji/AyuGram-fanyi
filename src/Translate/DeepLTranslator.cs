using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// DeepL API（v2/translate）。
    /// 免费版密钥以 ":fx" 结尾，接口域名是 api-free.deepl.com。
    /// </summary>
    public sealed class DeepLTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;

        public DeepLTranslator(AppConfig cfg) { _cfg = cfg; }

        public override string Name => "DeepL";
        public override bool IsConfigured => !string.IsNullOrWhiteSpace(_cfg.ApiKey) && !string.IsNullOrWhiteSpace(Endpoint);
        public override string ConfigurationHint => "请填写 DeepL 密钥，并把接口地址设为 api-free.deepl.com 或 api.deepl.com。";

        private string Endpoint
        {
            get
            {
                string e = _cfg.ApiEndpoint;
                if (string.IsNullOrWhiteSpace(e) || e.IndexOf("api-free.deepl.com", StringComparison.OrdinalIgnoreCase) < 0
                    && e.IndexOf("api.deepl.com", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // 按密钥后缀推断域名
                    string host = (_cfg.ApiKey ?? "").TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
                        ? "https://api-free.deepl.com/v2/translate"
                        : "https://api.deepl.com/v2/translate";
                    return host;
                }
                return e;
            }
        }

        public override async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            if (!IsConfigured) return TranslationResult.Fail(ConfigurationHint, Name);

            var pairs = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("text", request.Text ?? ""),
                new KeyValuePair<string, string>("target_lang", LanguageMap.ToDeepL(request.TargetLanguage)),
                new KeyValuePair<string, string>("preserve_formatting", "1"),
            };
            if (!string.IsNullOrWhiteSpace(request.SourceLanguage) &&
                !string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            {
                pairs.Add(new KeyValuePair<string, string>("source_lang", LanguageMap.ToDeepL(request.SourceLanguage)));
            }

            var content = new FormUrlEncodedContent(pairs);
            string url = Endpoint;

            var (ok, body, err) = await Http.SendWithRetryAsync(
                () =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    r.Headers.TryAddWithoutValidation("Authorization", "DeepL-Auth-Key " + _cfg.ApiKey.Trim());
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
                    if (root.TryGetProperty("translations", out var arr) && arr.ValueKind == JsonValueKind.Array &&
                        arr.GetArrayLength() > 0)
                    {
                        var t = arr[0].GetProperty("text").GetString();
                        t = TextUtil.CleanTranslationOutput(t);
                        if (string.IsNullOrWhiteSpace(t)) return TranslationResult.Fail("DeepL 返回空内容", Name);
                        return TranslationResult.Ok(t, Name, TextUtil.Truncate(body, 300));
                    }
                    if (root.TryGetProperty("message", out var m))
                        return TranslationResult.Fail("DeepL 错误：" + m.GetString(), Name);
                }
            }
            catch (JsonException ex)
            {
                return TranslationResult.Fail("DeepL 响应解析失败：" + ex.Message, Name, TextUtil.Truncate(body, 400));
            }

            return TranslationResult.Fail("DeepL 响应结构无法识别", Name, TextUtil.Truncate(body, 400));
        }
    }
}
