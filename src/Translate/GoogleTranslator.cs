using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// Google 非官方单条接口 translate_a/single（免密钥）。
    /// 国内网络通常不可用，仅作为兜底选项。
    /// </summary>
    public sealed class GoogleTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;

        public GoogleTranslator(AppConfig cfg) { _cfg = cfg; }

        public override string Name => "Google(非官方)";
        public override bool IsConfigured => true;

        public override async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            string baseUrl = string.IsNullOrWhiteSpace(_cfg.ApiEndpoint)
                ? "https://translate.googleapis.com/translate_a/single"
                : _cfg.ApiEndpoint;

            string url = baseUrl +
                         (baseUrl.Contains("?") ? "&" : "?") +
                         "client=gtx&dt=t&sl=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : request.SourceLanguage) +
                         "&tl=" + Uri.EscapeDataString(LanguageMap.ToGoogle(request.TargetLanguage)) +
                         "&q=" + Uri.EscapeDataString(request.Text ?? "");

            var (ok, body, err) = await Http.SendWithRetryAsync(
                () =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Get, url);
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
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                    {
                        var first = root[0];
                        if (first.ValueKind == JsonValueKind.Array)
                        {
                            var sb = new System.Text.StringBuilder();
                            foreach (var seg in first.EnumerateArray())
                            {
                                if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 &&
                                    seg[0].ValueKind == JsonValueKind.String)
                                {
                                    sb.Append(seg[0].GetString());
                                }
                            }
                            string text = TextUtil.CleanTranslationOutput(sb.ToString());
                            if (string.IsNullOrWhiteSpace(text))
                                return TranslationResult.Fail("Google 返回空内容", Name);
                            return TranslationResult.Ok(text, Name, TextUtil.Truncate(body, 300));
                        }
                    }
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
