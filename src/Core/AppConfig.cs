using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AyuTranslate.Core
{
    /// <summary>翻译服务类型。</summary>
    public enum ProviderKind
    {
        /// <summary>任意 OpenAI 兼容 /chat/completions 接口（OpenAI、DeepSeek、Ollama、vLLM、OneAPI、LM Studio…）。</summary>
        OpenAICompatible = 0,

        /// <summary>任意自定义 HTTP 接口，请求体 / 响应字段可用模板自定义。</summary>
        CustomHttp = 1,

        /// <summary>DeepL API（免费版 / 专业版）。</summary>
        DeepL = 2,

        /// <summary>自建 LibreTranslate 实例。</summary>
        LibreTranslate = 3,

        /// <summary>Google 非官方 translate_a 接口（免密钥，可能被墙）。</summary>
        GoogleUnofficial = 4,

        /// <summary>本地离线词典 / 直通，不做网络请求。</summary>
        Offline = 5,
    }

    /// <summary>输入框行为。</summary>
    public enum InputSendMode
    {
        /// <summary>只写入输入框，由用户自己按回车（最安全）。</summary>
        FillOnly = 0,

        /// <summary>写入输入框后自动回车发送。</summary>
        FillAndSend = 1,
    }

    /// <summary>向目标窗口写入文本的方式。</summary>
    public enum InputMethod
    {
        /// <summary>剪贴板 + Ctrl+V（对 Qt 最稳，推荐）。</summary>
        ClipboardPaste = 0,

        /// <summary>直接投递 WM_CHAR（不需要剪贴板，但部分控件不接收）。</summary>
        SendMessageChars = 1,
    }

    /// <summary>覆盖层的绘制风格。</summary>
    public enum OverlayStyle
    {
        /// <summary>
        /// 原生样式：采样原文所在气泡的背景色与文字色，用同样的颜色盖住原文。
        /// 视觉上接近 Telegram 自带的「原地翻译」。
        /// </summary>
        Native = 0,

        /// <summary>半透明黑框：可读性最强，但能明显看出是覆盖层。</summary>
        Box = 1,

        /// <summary>半透明框 + 下方一行灰色原文对照。</summary>
        Bilingual = 2,
    }

    public sealed class RegionConfig    {
        /// <summary>相对目标窗口客户区的矩形（像素）。</summary>
        public int X { get; set; } = 0;
        public int Y { get; set; } = 0;
        public int Width { get; set; } = 0;
        public int Height { get; set; } = 0;

        /// <summary>为 true 时使用 Auto 自动推断聊天区域。</summary>
        public bool Auto { get; set; } = true;

        public bool IsUsable => !Auto && Width > 40 && Height > 40;

        public RegionConfig Clone() => new RegionConfig { X = X, Y = Y, Width = Width, Height = Height, Auto = Auto };
    }

    public sealed class AppConfig
    {
        // ===== 目标窗口 =====
        /// <summary>
        /// AyuGram.exe 的完整路径，用于自动定位进程。留空时按进程名
        /// （AyuGram / Telegram 关键字）兜底查找。
        /// </summary>
        public string TargetExecutable { get; set; } = "";

        /// <summary>窗口类名匹配关键字（Qt 版本号会变，所以用前缀匹配）。</summary>
        public string TargetWindowClassPrefix { get; set; } = "Qt5";

        /// <summary>固定绑定的窗口句柄；0 表示自动查找。窗口句柄在进程重启后会失效。</summary>
        public long TargetWindowHandle { get; set; } = 0;

        /// <summary>为 true 时自动挑面积最大的可见顶层窗口。</summary>
        public bool AutoPickLargestWindow { get; set; } = true;

        // ===== 翻译服务 =====
        public ProviderKind Provider { get; set; } = ProviderKind.OpenAICompatible;

        /// <summary>自定义 AI 地址（OpenAI 兼容的 base url，例如 https://api.deepseek.com/v1）。</summary>
        public string ApiBaseUrl { get; set; } = "https://api.deepseek.com/v1";

        /// <summary>完整接口地址；CustomHttp 时使用。</summary>
        public string ApiEndpoint { get; set; } = "";

        // ---- API 密钥：内存中为明文，写盘时用 DPAPI（当前用户）加密 ----
        // JSON 里的 "ApiKey" 字段存的是 ApiKeyStorage（"enc:v1:" 前缀 + Base64 密文）；
        // 旧版本配置里的明文密钥在读取时照常兼容，下次保存时自动转为密文。
        private string _apiKeyPlain = "";

        /// <summary>API 密钥（运行时使用，明文）。</summary>
        [JsonIgnore]
        public string ApiKey
        {
            get => _apiKeyPlain;
            set => _apiKeyPlain = value ?? "";
        }

        /// <summary>密钥的持久化形态。序列化时加密，反序列化时解密。</summary>
        [JsonPropertyName("ApiKey")]
        public string ApiKeyStorage
        {
            get => EncodeApiKey(_apiKeyPlain);
            set => _apiKeyPlain = DecodeApiKey(value ?? "");
        }

        private const string ApiKeyCipherPrefix = "enc:v1:";

        private static string EncodeApiKey(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (plain.StartsWith(ApiKeyCipherPrefix, StringComparison.Ordinal)) return plain; // 已是密文（Clone 往返）
            try
            {
                byte[] enc = ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return ApiKeyCipherPrefix + Convert.ToBase64String(enc);
            }
            catch (Exception ex)
            {
                // DPAPI 失败时退回明文存储，保证功能可用（与旧行为一致）
                Log.Warn("API 密钥加密失败，退回明文保存：" + ex.Message);
                return plain;
            }
        }

        private static string DecodeApiKey(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith(ApiKeyCipherPrefix, StringComparison.Ordinal)) return stored; // 旧版明文
            try
            {
                byte[] enc = Convert.FromBase64String(stored.Substring(ApiKeyCipherPrefix.Length));
                return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser));
            }
            catch (Exception ex)
            {
                Log.Warn("API 密钥解密失败，已按空密钥处理：" + ex.Message);
                return "";
            }
        }

        /// <summary>模型名，例如 deepseek-chat / gpt-4o-mini / qwen2.5:7b。</summary>
        public string Model { get; set; } = "deepseek-chat";

        /// <summary>额外请求头，格式 Key: Value，每行一个。</summary>
        public string ExtraHeaders { get; set; } = "";

        /// <summary>CustomHttp 的请求体模板；{text} 会被替换，{target} / {source} 也可用。</summary>
        public string RequestTemplate { get; set; } = "{\"text\":\"{text}\",\"target_lang\":\"{target}\"}";

        /// <summary>CustomHttp 的响应取值路径，例如 "data.translation" 或 "choices.0.message.content"。</summary>
        public string ResponsePath { get; set; } = "data.translation";

        public int TimeoutSeconds { get; set; } = 60;

        public int MaxRetries { get; set; } = 2;

        /// <summary>批量翻译一次最多打包多少条消息（仅对支持批量的后端生效）。</summary>
        public int MaxBatchItems { get; set; } = 12;

        /// <summary>单次批量请求的最大字符数，长文本会单独成批以避免超出上下文。</summary>
        public int MaxBatchChars { get; set; } = 1800;

        // ===== 语言 =====
        /// <summary>目标语言，界面语言代码（zh-CN / en / ja …）。</summary>
        public string TargetLanguage { get; set; } = "zh-CN";

        /// <summary>源语言，"auto" 表示自动检测。</summary>
        public string SourceLanguage { get; set; } = "auto";

        /// <summary>已经是目标语言的消息不再翻译（按字符脚本比例判断）。</summary>
        public bool SkipTargetLanguage { get; set; } = true;

        // ===== 提示词 =====
        public string SystemPrompt { get; set; } =
            "You are a professional real-time chat translator. Translate the user's message into {target}. " +
            "Rules: output ONLY the translation, no explanations, no quotes, no romanization. " +
            "Preserve emoji, @mentions, URLs, code, and line breaks. " +
            "Keep the original casual/internet tone; translate slang idiomatically. " +
            "If the text is already in {target}, return it unchanged.";

        public double Temperature { get; set; } = 0.2;

        // ===== 覆盖层外观 =====

        /// <summary>绘制风格：原生样式 / 半透明黑框 / 双语对照。</summary>
        public OverlayStyle OverlayStyle { get; set; } = OverlayStyle.Native;

        /// <summary>
        /// 原生样式下，背景色相对采样值的额外压暗/提亮（0 表示完全用采样色）。
        /// 需要让覆盖区域和周围气泡有一点点区分时可微调。
        /// </summary>
        public int NativeBackgroundAdjust { get; set; } = 0;

        /// <summary>原生样式下，文字相对采样背景色的对比度目标。</summary>
        public bool NativeAutoTextColor { get; set; } = true;

        /// <summary>覆盖层背景色，ARGB 十六进制，例如 #E6000000（Box 样式使用）。</summary>
        public string OverlayBackground { get; set; } = "#D9000000";

        /// <summary>覆盖层文字颜色。</summary>
        public string OverlayForeground { get; set; } = "#FFFFFFFF";

        /// <summary>原文颜色（保留原文显示时使用）。</summary>
        public string OverlayOriginalForeground { get; set; } = "#99FFFFFF";

        public double OverlayFontSize { get; set; } = 15;

        public string OverlayFontFamily { get; set; } = "Microsoft YaHei UI";

        /// <summary>译文上下留白，用于覆盖住原文。</summary>
        public int OverlayPaddingY { get; set; } = 3;

        /// <summary>译文比原区域向外扩展的像素，保证盖住原文。</summary>
        public int OverlayBleed { get; set; } = 4;

        /// <summary>覆盖层不透明度 0.1 - 1.0。</summary>
        public double OverlayOpacity { get; set; } = 1.0;

        /// <summary>是否在译文下保留灰色原文。</summary>
        public bool ShowOriginalText { get; set; } = false;

        /// <summary>鼠标穿透（true 时点击会落到 AyuGram 上）。</summary>
        public bool OverlayClickThrough { get; set; } = true;

        /// <summary>单次最多绘制的覆盖条目数（防止界面卡顿）。</summary>
        public int OverlayMaxItems { get; set; } = 400;

        /// <summary>译文最长显示行数（超出截断，避免盖住下面的消息）。</summary>
        public int OverlayMaxLines { get; set; } = 6;

        // ===== 识别 =====
        /// <summary>OCR 语言（Windows OCR 可用语言，例如 zh-Hans-CN / en-US / ja）。</summary>
        public List<string> OcrLanguages { get; set; } = new List<string> { "zh-Hans-CN", "en-US" };

        /// <summary>放大倍数，提高小字号识别率。</summary>
        public double OcrScale { get; set; } = 2.0;

        /// <summary>
        /// 自适应缩放：上一帧文字已经足够大（行高中位数 ≥ 28px）时跳过放大，
        /// 省掉一次全区域的高质量插值。默认关闭。
        /// </summary>
        public bool AdaptiveOcrScale { get; set; } = false;

        /// <summary>相邻文本行合并成一条消息的最大垂直间距（像素，原图坐标）。</summary>
        public int LineMergeGap { get; set; } = 10;

        /// <summary>同一条消息内相邻行的最小水平重叠比例。</summary>
        public double LineMergeOverlap { get; set; } = 0.25;

        /// <summary>忽略高度小于该值（原图像素）的文本行。</summary>
        public int MinLineHeight { get; set; } = 8;

        /// <summary>忽略长度小于该值的文本。</summary>
        public int MinTextLength { get; set; } = 2;

        // ===== 运行 =====

        /// <summary>
        /// 覆盖层翻译总开关（截图 + OCR 识别那条链路）。
        /// 默认关闭：只用「原生翻译接管」时不需要它，关掉可以省下
        /// 周期性抓图 / OCR 的 CPU 开销。需要覆盖层时在设置里勾选开启。
        /// 关闭后自动翻译、手动翻译热键、覆盖层显示全部停用；
        /// 自检模式（--selftest / --overlaytest）不受影响。
        /// </summary>
        public bool OverlayEnabled { get; set; } = false;

        public bool StartWithOverlayVisible { get; set; } = true;

        /// <summary>自动模式轮询间隔（毫秒）。</summary>
        public int PollIntervalMs { get; set; } = 700;

        /// <summary>
        /// 帧跳过：抓到的画面与上一帧一致（采样差异 &lt; 1%）时不再跑 OCR / 翻译，
        /// 直接沿用上一次的覆盖条目。自动模式下大多数帧都是静止画面，
        /// 关闭后每帧都会全量 OCR，CPU 与接口调用量明显上升。
        /// </summary>
        public bool SkipUnchangedFrames { get; set; } = true;

        /// <summary>两次翻译请求之间的最小间隔，避免触发限流。</summary>
        public int MinRequestIntervalMs { get; set; } = 350;

        /// <summary>仅在目标窗口处于前台时翻译。</summary>
        public bool OnlyWhenTargetFocused { get; set; } = true;

        /// <summary>目标窗口失去前台时自动隐藏覆盖层，避免挡住其他程序。</summary>
        public bool HideOverlayWhenUnfocused { get; set; } = true;

        /// <summary>手动翻译快捷键（全局）。</summary>
        public string HotkeyTranslate { get; set; } = "Ctrl+Alt+T";

        /// <summary>切换覆盖层快捷键。</summary>
        public string HotkeyToggleOverlay { get; set; } = "Ctrl+Alt+H";

        /// <summary>切换自动模式快捷键。</summary>
        public string HotkeyToggleAuto { get; set; } = "Ctrl+Alt+A";

        /// <summary>打开设置快捷键。</summary>
        public string HotkeySettings { get; set; } = "Ctrl+Alt+S";

        // ===== 原生翻译接管（推荐用法）=====
        //
        // AyuGram 自带的翻译界面是原生的（译文原地替换原文）。
        // 它内置的「Google 翻译」后端会把请求发到
        //     https://translate-pa.googleapis.com/v1/translateHtml
        // 我们把这个地址改写成本地代理，就能让原生界面直接使用自定义 AI。

        /// <summary>是否启用本地翻译代理。</summary>
        public bool ProxyEnabled { get; set; } = false;

        /// <summary>
        /// AI 翻译失效时（报错 / 超时 / 未配置），自动回退到 AyuGram 原本的
        /// 免费谷歌翻译接口，保证聊天始终能出译文而不是停在原文。
        /// </summary>
        public bool ProxyFallbackToGoogle { get; set; } = true;

        /// <summary>代理监听端口。</summary>
        public int ProxyPort { get; set; } = 8766;

        /// <summary>打开主界面时自动启动代理。</summary>
        public bool ProxyAutoStart { get; set; } = false;

        // ===== 输入框 =====
        public InputSendMode InputSendMode { get; set; } = InputSendMode.FillOnly;

        /// <summary>写入方式。</summary>
        public InputMethod InputMethod { get; set; } = InputMethod.ClipboardPaste;

        /// <summary>发送快捷键（通常是 Enter）。</summary>
        public string InputSendKey { get; set; } = "Enter";

        /// <summary>输入框相对客户区的位置（Auto 为 true 时自动推断）。</summary>
        public RegionConfig InputBoxRegion { get; set; } = new RegionConfig { Auto = true };

        // ===== 区域 =====
        public RegionConfig ChatRegion { get; set; } = new RegionConfig { Auto = true };

        /// <summary>翻译时是否包含侧边栏（关闭可避免把会话列表也翻译了）。</summary>
        public bool IncludeSidebar { get; set; } = false;

        // ===== 缓存 =====
        public int CacheMaxEntries { get; set; } = 4000;

        public bool CacheEnabled { get; set; } = true;

        /// <summary>
        /// 位置复用阈值：上一帧同位置的文本相似度超过该值就直接沿用译文。
        /// 用来吸收 OCR 抖动，避免同一句话反复请求接口。设 1.0 可关闭该行为。
        /// </summary>
        public double ReuseSimilarity { get; set; } = 0.82;

        // ===== 日志 =====
        public bool DebugLog { get; set; } = false;

        public string LogDirectory { get; set; } = "";

        // ---------------------------------------------------------------

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            PropertyNamingPolicy = null,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public static string ConfigDirectory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AyuTranslate");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string DefaultConfigPath => Path.Combine(ConfigDirectory, "config.json");

        public static string DefaultLogDirectory
        {
            get
            {
                string dir = Path.Combine(ConfigDirectory, "logs");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static AppConfig Load(string path = null)
        {
            path = path ?? DefaultConfigPath;
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path);
                    var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
                    if (cfg != null)
                    {
                        cfg.Normalize();
                        return cfg;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("读取配置失败，使用默认值：" + ex.Message);
            }

            var fresh = new AppConfig();
            fresh.Normalize();
            return fresh;
        }

        public void Save(string path = null)
        {
            path = path ?? DefaultConfigPath;
            try
            {
                Normalize();
                string json = JsonSerializer.Serialize(this, JsonOpts);
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败：" + ex.Message);
            }
        }

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(TargetLanguage)) TargetLanguage = "zh-CN";
            if (string.IsNullOrWhiteSpace(SourceLanguage)) SourceLanguage = "auto";
            if (string.IsNullOrWhiteSpace(OverlayFontFamily)) OverlayFontFamily = "Microsoft YaHei UI";
            if (OcrLanguages == null || OcrLanguages.Count == 0)
                OcrLanguages = new List<string> { "zh-Hans-CN", "en-US" };
            if (OcrScale < 1.0) OcrScale = 1.0;
            if (OcrScale > 4.0) OcrScale = 4.0;
            if (MaxBatchItems < 1) MaxBatchItems = 1;
            if (MaxBatchItems > 32) MaxBatchItems = 32;
            if (MaxBatchChars < 200) MaxBatchChars = 200;
            if (MaxBatchChars > 8000) MaxBatchChars = 8000;
            if (PollIntervalMs < 150) PollIntervalMs = 150;
            if (PollIntervalMs > 10000) PollIntervalMs = 10000;
            if (MinRequestIntervalMs < 0) MinRequestIntervalMs = 0;
            if (OverlayFontSize < 8) OverlayFontSize = 8;
            if (OverlayFontSize > 48) OverlayFontSize = 48;
            if (OverlayOpacity < 0.1) OverlayOpacity = 0.1;
            if (OverlayOpacity > 1.0) OverlayOpacity = 1.0;
            if (Temperature < 0) Temperature = 0;
            if (Temperature > 2) Temperature = 2;
            if (TimeoutSeconds < 5) TimeoutSeconds = 5;
            if (MaxRetries < 0) MaxRetries = 0;
            if (LineMergeGap < 0) LineMergeGap = 0;
            if (MinLineHeight < 1) MinLineHeight = 1;
            if (MinTextLength < 1) MinTextLength = 1;
            if (ReuseSimilarity < 0.5) ReuseSimilarity = 0.5;
            if (ReuseSimilarity > 1.0) ReuseSimilarity = 1.0;
            if (ProxyPort < 1024 || ProxyPort > 65535) ProxyPort = 8766;
            if (ChatRegion == null) ChatRegion = new RegionConfig();
            if (InputBoxRegion == null) InputBoxRegion = new RegionConfig();
            if (string.IsNullOrWhiteSpace(LogDirectory)) LogDirectory = DefaultLogDirectory;

            // 旧版本会把「按 ApiBaseUrl 派生的完整地址」持久化到 ApiEndpoint，
            // 导致之后修改 base url 时不生效（endpoint 优先级更高）。
            // 迁移：ApiEndpoint 恰好等于派生值 / 各后端默认值时清空，恢复「留空自动」。
            ApiEndpoint = ClearIfDerived(ApiEndpoint, CombineUrl(ApiBaseUrl, "chat/completions"));
            ApiEndpoint = ClearIfDerived(ApiEndpoint, "https://api-free.deepl.com/v2/translate");
            ApiEndpoint = ClearIfDerived(ApiEndpoint, "https://api.deepl.com/v2/translate");
            ApiEndpoint = ClearIfDerived(ApiEndpoint, "http://localhost:5000/translate");
            ApiEndpoint = ClearIfDerived(ApiEndpoint, "http://127.0.0.1:5000/translate");
            ApiEndpoint = ClearIfDerived(ApiEndpoint, "https://translate.googleapis.com/translate_a/single");
        }

        /// <summary>endpoint 与派生/默认值完全一致时返回空串（表示自动），否则原样返回（trim 过）。</summary>
        private static string ClearIfDerived(string endpoint, string derived)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return "";
            return string.Equals(endpoint.Trim(), derived, StringComparison.OrdinalIgnoreCase) ? "" : endpoint.Trim();
        }

        public static string CombineUrl(string baseUrl, string relative)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return relative;
            string b = baseUrl.TrimEnd('/');
            if (b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return b;
            return b + "/" + relative.TrimStart('/');
        }

        public AppConfig Clone()
        {
            string json = JsonSerializer.Serialize(this, JsonOpts);
            var c = JsonSerializer.Deserialize<AppConfig>(json, JsonOpts);
            c.Normalize();
            return c;
        }
    }
}
