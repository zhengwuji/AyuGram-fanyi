using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// 离线测试翻译器：不联网，只给原文加标记。
    /// 用来验证「抓图 → OCR → 覆盖层定位」这条链路是否正常。
    /// </summary>
    public sealed class OfflineTranslator : TranslatorBase
    {
        private readonly AppConfig _cfg;
        private static readonly Dictionary<string, string> Demo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["hello"] = "你好",
            ["hi"] = "嗨",
            ["thanks"] = "谢谢",
            ["thank you"] = "谢谢你",
            ["good morning"] = "早上好",
            ["how are you"] = "你好吗",
        };

        public OfflineTranslator(AppConfig cfg) { _cfg = cfg; }

        public override string Name => "离线测试(不联网)";
        public override bool IsConfigured => true;

        public override Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            string text = (request.Text ?? "").Trim();
            string tag = LanguageMap.Get(request.TargetLanguage).Display;

            if (Demo.TryGetValue(text, out string hit))
            {
                return Task.FromResult(TranslationResult.Ok("〔" + tag + "〕" + hit, Name));
            }

            // 用目标语言标签包裹原文，方便肉眼确认覆盖层位置与宽度是否合适
            string preview = TextUtil.Truncate(text, 120);
            return Task.FromResult(TranslationResult.Ok("〔" + tag + "〕" + preview, Name));
        }
    }
}
