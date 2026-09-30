using System;
using System.Collections.Generic;

namespace AyuTranslate.Core
{
    /// <summary>界面语言代码 &lt;-&gt; 各服务商语言代码 &lt;-&gt; 字符集 的映射。</summary>
    public static class LanguageMap
    {
        public sealed class LangInfo
        {
            public string UiCode;
            public string Display;
            public ScriptKind Script;
            /// <summary>OpenAI 提示词里用的自然语言名。</summary>
            public string PromptName;
            /// <summary>DeepL 目标语言代码（null 表示不支持）。</summary>
            public string DeepL;
            /// <summary>LibreTranslate 代码。</summary>
            public string Libre;
            /// <summary>Google 代码。</summary>
            public string Google;
            /// <summary>Windows OCR 语言标签候选。</summary>
            public string[] Ocr;

            public override string ToString() => Display + "  (" + UiCode + ")";
        }

        private static readonly List<LangInfo> All = new List<LangInfo>
        {
            new LangInfo { UiCode="zh-CN", Display="简体中文", Script=ScriptKind.Han, PromptName="Simplified Chinese",
                DeepL="ZH-HANS", Libre="zh", Google="zh-CN", Ocr=new[]{"zh-Hans-CN","zh-Hans"} },
            new LangInfo { UiCode="zh-TW", Display="繁体中文", Script=ScriptKind.Han, PromptName="Traditional Chinese",
                DeepL="ZH-HANT", Libre="zt", Google="zh-TW", Ocr=new[]{"zh-Hant-TW","zh-Hant"} },
            new LangInfo { UiCode="en", Display="English 英语", Script=ScriptKind.Latin, PromptName="English",
                DeepL="EN-US", Libre="en", Google="en", Ocr=new[]{"en-US","en-GB","en"} },
            new LangInfo { UiCode="ja", Display="日本語 日语", Script=ScriptKind.Hiragana, PromptName="Japanese",
                DeepL="JA", Libre="ja", Google="ja", Ocr=new[]{"ja-JP","ja"} },
            new LangInfo { UiCode="ko", Display="한국어 韩语", Script=ScriptKind.Hangul, PromptName="Korean",
                DeepL="KO", Libre="ko", Google="ko", Ocr=new[]{"ko-KR","ko"} },
            new LangInfo { UiCode="ru", Display="Русский 俄语", Script=ScriptKind.Cyrillic, PromptName="Russian",
                DeepL="RU", Libre="ru", Google="ru", Ocr=new[]{"ru-RU","ru"} },
            new LangInfo { UiCode="fr", Display="Français 法语", Script=ScriptKind.Latin, PromptName="French",
                DeepL="FR", Libre="fr", Google="fr", Ocr=new[]{"fr-FR","fr"} },
            new LangInfo { UiCode="de", Display="Deutsch 德语", Script=ScriptKind.Latin, PromptName="German",
                DeepL="DE", Libre="de", Google="de", Ocr=new[]{"de-DE","de"} },
            new LangInfo { UiCode="es", Display="Español 西班牙语", Script=ScriptKind.Latin, PromptName="Spanish",
                DeepL="ES", Libre="es", Google="es", Ocr=new[]{"es-ES","es"} },
            new LangInfo { UiCode="pt", Display="Português 葡萄牙语", Script=ScriptKind.Latin, PromptName="Portuguese",
                DeepL="PT-BR", Libre="pt", Google="pt", Ocr=new[]{"pt-BR","pt"} },
            new LangInfo { UiCode="it", Display="Italiano 意大利语", Script=ScriptKind.Latin, PromptName="Italian",
                DeepL="IT", Libre="it", Google="it", Ocr=new[]{"it-IT","it"} },
            new LangInfo { UiCode="ar", Display="العربية 阿拉伯语", Script=ScriptKind.Arabic, PromptName="Arabic",
                DeepL="AR", Libre="ar", Google="ar", Ocr=new[]{"ar-SA","ar"} },
            new LangInfo { UiCode="tr", Display="Türkçe 土耳其语", Script=ScriptKind.Latin, PromptName="Turkish",
                DeepL="TR", Libre="tr", Google="tr", Ocr=new[]{"tr-TR","tr"} },
            new LangInfo { UiCode="th", Display="ไทย 泰语", Script=ScriptKind.Thai, PromptName="Thai",
                DeepL=null, Libre="th", Google="th", Ocr=new[]{"th-TH","th"} },
            new LangInfo { UiCode="vi", Display="Tiếng Việt 越南语", Script=ScriptKind.Latin, PromptName="Vietnamese",
                DeepL=null, Libre="vi", Google="vi", Ocr=new[]{"vi-VN","vi"} },
            new LangInfo { UiCode="id", Display="Bahasa Indonesia 印尼语", Script=ScriptKind.Latin, PromptName="Indonesian",
                DeepL="ID", Libre="id", Google="id", Ocr=new[]{"id-ID","id"} },
            new LangInfo { UiCode="uk", Display="Українська 乌克兰语", Script=ScriptKind.Cyrillic, PromptName="Ukrainian",
                DeepL="UK", Libre="uk", Google="uk", Ocr=new[]{"uk-UA","uk"} },
            new LangInfo { UiCode="pl", Display="Polski 波兰语", Script=ScriptKind.Latin, PromptName="Polish",
                DeepL="PL", Libre="pl", Google="pl", Ocr=new[]{"pl-PL","pl"} },
            new LangInfo { UiCode="nl", Display="Nederlands 荷兰语", Script=ScriptKind.Latin, PromptName="Dutch",
                DeepL="NL", Libre="nl", Google="nl", Ocr=new[]{"nl-NL","nl"} },
            new LangInfo { UiCode="hi", Display="हिन्दी 印地语", Script=ScriptKind.Devanagari, PromptName="Hindi",
                DeepL=null, Libre="hi", Google="hi", Ocr=new[]{"hi-IN","hi"} },
            new LangInfo { UiCode="fa", Display="فارسی 波斯语", Script=ScriptKind.Arabic, PromptName="Persian",
                DeepL=null, Libre="fa", Google="fa", Ocr=new[]{"fa-IR","fa"} },
            new LangInfo { UiCode="he", Display="עברית 希伯来语", Script=ScriptKind.Hebrew, PromptName="Hebrew",
                DeepL=null, Libre="he", Google="he", Ocr=new[]{"he-IL","he"} },
        };

        private static readonly Dictionary<string, LangInfo> ByCode =
            new Dictionary<string, LangInfo>(StringComparer.OrdinalIgnoreCase);

        static LanguageMap()
        {
            foreach (var l in All)
            {
                ByCode[l.UiCode] = l;
                // 常见别名
                if (l.UiCode == "zh-CN") { ByCode["zh"] = l; ByCode["zh-Hans"] = l; ByCode["chs"] = l; }
                if (l.UiCode == "zh-TW") { ByCode["zh-Hant"] = l; ByCode["cht"] = l; }
                if (l.UiCode == "en") { ByCode["en-US"] = l; ByCode["en-GB"] = l; }
                if (l.UiCode == "ja") { ByCode["jp"] = l; ByCode["ja-JP"] = l; }
                if (l.UiCode == "ko") { ByCode["ko-KR"] = l; }
            }
        }

        public static IReadOnlyList<LangInfo> Languages => All;

        public static LangInfo Get(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return ByCode["en"];
            if (ByCode.TryGetValue(code.Trim(), out var info)) return info;
            return ByCode["en"];
        }

        public static ScriptKind ScriptOfLanguage(string code) => Get(code).Script;

        public static string PromptNameOf(string code) => Get(code).PromptName;

        /// <summary>从任意语言标识猜测字符集（OCR 语言标签用）。</summary>
        public static ScriptKind ScriptOfOcrTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return ScriptKind.Unknown;
            string t = tag.ToLowerInvariant();
            if (t.StartsWith("zh")) return ScriptKind.Han;
            if (t.StartsWith("ja")) return ScriptKind.Hiragana;
            if (t.StartsWith("ko")) return ScriptKind.Hangul;
            if (t.StartsWith("ru") || t.StartsWith("uk") || t.StartsWith("bg")) return ScriptKind.Cyrillic;
            if (t.StartsWith("ar") || t.StartsWith("fa") || t.StartsWith("ur")) return ScriptKind.Arabic;
            if (t.StartsWith("he")) return ScriptKind.Hebrew;
            if (t.StartsWith("th")) return ScriptKind.Thai;
            if (t.StartsWith("hi")) return ScriptKind.Devanagari;
            return ScriptKind.Latin;
        }

        /// <summary>把界面语言代码映射到 DeepL 代码。</summary>
        public static string ToDeepL(string code)
        {
            var info = Get(code);
            return info.DeepL ?? info.UiCode.ToUpperInvariant();
        }

        public static string ToLibre(string code) => Get(code).Libre;

        public static string ToGoogle(string code) => Get(code).Google;

        /// <summary>把界面语言代码映射成 Windows OCR 语言标签。</summary>
        public static string[] ToOcrTags(string code)
        {
            var info = Get(code);
            if (info.Ocr != null) return info.Ocr;
            return new[] { code };
        }
    }
}
