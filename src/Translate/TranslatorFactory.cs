using System;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>按配置创建翻译后端。</summary>
    public static class TranslatorFactory
    {
        public static ITranslator Create(AppConfig cfg)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            switch (cfg.Provider)
            {
                case ProviderKind.OpenAICompatible:
                    return new OpenAiCompatibleTranslator(cfg);

                case ProviderKind.CustomHttp:
                    return new CustomHttpTranslator(cfg);

                case ProviderKind.DeepL:
                    return new DeepLTranslator(cfg);

                case ProviderKind.LibreTranslate:
                    return new LibreTranslateTranslator(cfg);

                case ProviderKind.GoogleUnofficial:
                    return new GoogleTranslator(cfg);

                case ProviderKind.Offline:
                    return new OfflineTranslator(cfg);

                default:
                    return new OpenAiCompatibleTranslator(cfg);
            }
        }

        public static string DisplayName(ProviderKind kind)
        {
            switch (kind)
            {
                case ProviderKind.OpenAICompatible: return "OpenAI 兼容接口（推荐：DeepSeek / Ollama / OneAPI / vLLM）";
                case ProviderKind.CustomHttp: return "自定义 HTTP 接口（自己写请求模板与取值路径）";
                case ProviderKind.DeepL: return "DeepL API";
                case ProviderKind.LibreTranslate: return "LibreTranslate（自建）";
                case ProviderKind.GoogleUnofficial: return "Google 非官方接口（免密钥，需能访问谷歌）";
                case ProviderKind.Offline: return "离线测试（不联网，用于验证识别与覆盖）";
                default: return kind.ToString();
            }
        }
    }
}
