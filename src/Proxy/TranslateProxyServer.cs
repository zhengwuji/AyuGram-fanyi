using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;
using AyuTranslate.Translate;

namespace AyuTranslate.Proxy
{
    /// <summary>
    /// 本地翻译代理：冒充 AyuGram 内置「Google 翻译」所用的接口。
    ///
    /// AyuGram 的 GoogleTranslator 会向
    ///     https://translate-pa.googleapis.com/v1/translateHtml
    /// 发送如下请求（见源码 implementations/google.cpp）：
    ///
    ///     POST /v1/translateHtml
    ///     Content-Type: application/json+protobuf
    ///     { "X-Goog-Api-Key": "AIza..." }
    ///     请求体：[[["原文"], "auto", "zh-CN"], "wt_lib"]
    ///
    /// 期望响应是一个 JSON 数组，客户端会递归取出其中所有字符串并拼接：
    ///     [["译文"]]
    ///
    /// 本代理实现同一个协议，但把翻译转发到用户配置的任意 AI 接口。
    /// 配合把 AyuGram.exe 里的该 URL 改写为 http://127.0.0.1:PORT/v1/translateHtml，
    /// 就能让 AyuGram 的**原生翻译界面**使用自定义 AI —— 渲染完全原生，
    /// 译文原地替换原文，不依赖 OCR，也不画覆盖层。
    /// </summary>
    public sealed class TranslateProxyServer : IDisposable
    {
        /// <summary>客户端默认请求的路径。</summary>
        public const string TranslatePath = "/v1/translateHtml";

        /// <summary>AyuGram 原本使用的谷歌翻译接口（AI 失效时的兜底目标）。</summary>
        public const string GoogleOriginalUrl = "https://translate-pa.googleapis.com/v1/translateHtml";

        /// <summary>AyuGram 源码里内置的公开谷歌 API Key（与客户端请求时发送的相同）。</summary>
        public const string GoogleApiKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";

        private AppConfig _cfg;
        private readonly int _port;
        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private Task _acceptLoop;
        private volatile bool _running;

        /// <summary>共享翻译后端（无共享可变状态，可多请求并发使用）。</summary>
        private ITranslator _translator;

        /// <summary>AI 并发上限：AyuGram 整聊翻译会同时发来大量请求，
        /// 不加闸会把网关打到限流，反而全军覆没。</summary>
        private readonly SemaphoreSlim _aiGate = new SemaphoreSlim(3, 3);

        /// <summary>单次 AI 调用的时间预算。AyuGram 客户端 15 秒就放弃等待，留出余量。</summary>
        private const int AiTimeBudgetMs = 12000;

        /// <summary>代理层翻译缓存：重复点击翻译 / 回看旧消息时直接秒回，不再打网关。</summary>
        private readonly TranslationCache _cache = new TranslationCache(4096);

        public TranslateProxyServer(AppConfig cfg, int port)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _port = port;
        }

        public int Port => _port;
        public bool IsRunning => _running;

        /// <summary>配置热更新后刷新翻译后端（模型 / 密钥等变更即时生效）。</summary>
        public void UpdateConfig(AppConfig cfg)
        {
            if (cfg == null) return;
            _cfg = cfg;
            var old = _translator;
            _translator = null;
            try { old?.Dispose(); } catch { }
            Log.Info("代理已刷新翻译后端：" + _cfg.Provider + " / " + _cfg.Model);
        }

        public long RequestCount;
        public long SuccessCount;
        public long FailCount;
        public DateTime LastRequestUtc = DateTime.MinValue;
        public string LastError = "";

        /// <summary>每完成一次翻译都会触发，用于日志与界面显示。</summary>
        public event Action<string> RequestLogged;

        public void Start()
        {
            if (_running) return;

            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _running = true;

            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log.Info($"翻译代理已启动：http://127.0.0.1:{_port}{TranslatePath}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            Log.Info("翻译代理已停止。");
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
                catch (Exception ex)
                {
                    Log.Debug("accept 异常：" + ex.Message);
                    continue;
                }

                var c = client;
                _ = Task.Run(() => HandleClientAsync(c, ct), ct);
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;

                    using (var stream = client.GetStream())
                    {
                        var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                        if (request == null) return;

                        string path = request.Path;
                        int query = path.IndexOf('?');
                        if (query >= 0) path = path.Substring(0, query);

                        if (request.Method == "GET")
                        {
                            // 健康检查 / 调试入口
                            if (path == "/" || path == "/health" || path == "/status")
                            {
                                await WriteJsonAsync(stream, 200,
                                    "{\"ok\":true,\"service\":\"AyuTranslate proxy\",\"requests\":" +
                                    Interlocked.Read(ref RequestCount) + "}").ConfigureAwait(false);
                                return;
                            }
                            await WriteJsonAsync(stream, 404, "{\"error\":\"not found\"}").ConfigureAwait(false);
                            return;
                        }

                        if (request.Method != "POST")
                        {
                            await WriteJsonAsync(stream, 405, "{\"error\":\"method not allowed\"}").ConfigureAwait(false);
                            return;
                        }

                        Interlocked.Increment(ref RequestCount);
                        LastRequestUtc = DateTime.UtcNow;

                        // 兼容多种路径，方便别的客户端接入
                        if (!IsTranslatePath(path))
                        {
                            await WriteJsonAsync(stream, 404, "{\"error\":\"unknown path: " + Escape(path) + "\"}")
                                .ConfigureAwait(false);
                            return;
                        }

                        var parsed = ParseGoogleRequest(request.Body);
                        if (string.IsNullOrWhiteSpace(parsed.Text))
                        {
                            // 解析失败：谷歌认识自家协议，直接把原始请求转发过去试试
                            string relayed = _cfg.ProxyFallbackToGoogle
                                ? await TranslateViaGoogleAsync(request.Body, parsed, ct).ConfigureAwait(false)
                                : null;
                            if (relayed != null)
                            {
                                Interlocked.Increment(ref SuccessCount);
                                await WriteJsonAsync(stream, 200, BuildGoogleResponse(relayed)).ConfigureAwait(false);
                                return;
                            }

                            Interlocked.Increment(ref FailCount);
                            LastError = "无法从请求体解析出原文";
                            Log.Warn("代理：无法解析请求体：" + Truncate(request.Body, 300));
                            await WriteJsonAsync(stream, 200, "[[\"\"]]").ConfigureAwait(false);
                            return;
                        }

                        string translation = await TranslateAsync(parsed, ct).ConfigureAwait(false);

                        if (translation == null && _cfg.ProxyFallbackToGoogle)
                        {
                            // AI 翻译失效（报错 / 超时 / 未配置）：回退到 AyuGram 原本的免费谷歌翻译，
                            // 保证聊天始终能出译文，而不是停在原文
                            translation = await TranslateViaGoogleAsync(request.Body, parsed, ct).ConfigureAwait(false);
                            if (translation != null)
                            {
                                Interlocked.Increment(ref SuccessCount);
                                await WriteJsonAsync(stream, 200, BuildGoogleResponse(translation)).ConfigureAwait(false);
                                return;
                            }
                        }

                        if (translation == null)
                        {
                            Interlocked.Increment(ref FailCount);
                            // 返回空串而不是报错，避免 AyuGram 弹出错误提示
                            await WriteJsonAsync(stream, 200, "[[\"\"]]").ConfigureAwait(false);
                            return;
                        }

                        Interlocked.Increment(ref SuccessCount);
                        string response = BuildGoogleResponse(translation);
                        await WriteJsonAsync(stream, 200, response).ConfigureAwait(false);

                        RequestLogged?.Invoke($"{parsed.From}→{parsed.To}  " +
                                              $"{Truncate(parsed.Text, 50)}  ⇒  {Truncate(translation, 50)}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug("代理处理请求异常：" + ex.Message);
                }
            }
        }

        private static bool IsTranslatePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p = path.TrimEnd('/').ToLowerInvariant();
            return p.EndsWith("/translatetml", StringComparison.OrdinalIgnoreCase)
                   || p.Contains("translatehtml")
                   || p.EndsWith("/translate")
                   || p.EndsWith("/v1/translate");
        }

        // ----------------------------------------------------------- 协议解析

        internal sealed class ParsedRequest
        {
            public string Text;
            public string From = "auto";
            public string To = "zh-CN";
        }

        /// <summary>
        /// 解析 Google translateHtml 的请求体。
        /// 结构：[[ [ "原文" ], "from", "to" ], "wt_lib"]
        /// 同时兼容更宽松的形状，避免格式微调后失效。
        /// </summary>
        internal static ParsedRequest ParseGoogleRequest(string body)
        {
            var result = new ParsedRequest();
            if (string.IsNullOrWhiteSpace(body)) return result;

            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    JsonElement payload = root;

                    // 最外层是 [payload, "wt_lib"]
                    if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                        payload = root[0];

                    if (payload.ValueKind != JsonValueKind.Array)
                    {
                        // 极简格式：直接给一个字符串
                        if (payload.ValueKind == JsonValueKind.String) result.Text = payload.GetString();
                        return result;
                    }

                    var arr = payload;
                    if (arr.GetArrayLength() > 0)
                    {
                        var textNode = arr[0];
                        result.Text = ExtractText(textNode);
                    }
                    if (arr.GetArrayLength() > 1 && arr[1].ValueKind == JsonValueKind.String)
                        result.From = arr[1].GetString() ?? "auto";
                    if (arr.GetArrayLength() > 2 && arr[2].ValueKind == JsonValueKind.String)
                        result.To = arr[2].GetString() ?? "zh-CN";
                }
            }
            catch (JsonException ex)
            {
                Log.Debug("代理：请求体不是合法 JSON（" + ex.Message + "）");
            }

            return result;
        }

        private static string ExtractText(JsonElement node)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    return node.GetString();

                case JsonValueKind.Array:
                    // ["原文"] 或 ["原文", ...]
                    foreach (var item in node.EnumerateArray())
                    {
                        string s = ExtractText(item);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                    return null;

                case JsonValueKind.Object:
                    if (node.TryGetProperty("text", out var t)) return ExtractText(t);
                    if (node.TryGetProperty("q", out var q)) return ExtractText(q);
                    return null;
            }
            return null;
        }

        /// <summary>
        /// 组装 Google 风格的响应。
        /// 客户端会做 HTML 解码（QTextDocument），所以这里要把译文做 HTML 转义，
        /// 并把换行写成 &lt;br&gt;，否则译文里的 &lt; &gt; &amp; 会被当成标签吃掉。
        /// </summary>
        internal static string BuildGoogleResponse(string translation)
        {
            string html = ToHtml(translation);
            string json = JsonSerializer.Serialize(new object[]
            {
                new object[] { html },
            });
            return json;
        }

        /// <summary>把纯文本转成客户端能安全解码的 HTML 片段。</summary>
        internal static string ToHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length + 16);
            foreach (char c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\n': sb.Append("<br>"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>把客户端发来的 <c>&lt;br&gt;</c> 还原成换行。</summary>
        internal static string FromHtml(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return text
                .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase)
                .Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase);
        }

        // ----------------------------------------------------------- 翻译

        private async Task<string> TranslateAsync(ParsedRequest req, CancellationToken ct)
        {
            try
            {
                if (_translator == null) _translator = TranslatorFactory.Create(_cfg);
                if (!_translator.IsConfigured)
                {
                    LastError = _translator.ConfigurationHint;
                    Log.Warn("代理：翻译后端未配置 - " + LastError);
                    return null;
                }

                var request = new TranslationRequest
                {
                    Text = FromHtml(req.Text),
                    SourceLanguage = string.IsNullOrWhiteSpace(req.From) ? "auto" : req.From,
                    TargetLanguage = NormalizeLang(req.To),
                };

                // 缓存命中直接返回（AyuGram 重复翻译同一段文本很常见）
                string cacheKey = request.SourceLanguage + "\u0001" + request.TargetLanguage + "\u0001" + request.Text;
                if (_cache.TryGet(cacheKey, out string cached))
                {
                    Interlocked.Increment(ref SuccessCount);
                    Log.Debug("代理：缓存命中");
                    RequestLogged?.Invoke($"{request.SourceLanguage}→{request.TargetLanguage}  " +
                                          $"{Truncate(request.Text, 50)}  ⇒  {Truncate(cached, 50)}（缓存）");
                    return cached;
                }

                TranslationResult result;
                await _aiGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        // 开启谷歌兜底时给 AI 少一点时间，给兜底留出余量（AyuGram 总共只等 15 秒）
                        int budget = _cfg.ProxyFallbackToGoogle ? 10000 : AiTimeBudgetMs;
                        cts.CancelAfter(budget);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        result = await _translator.TranslateAsync(request, cts.Token).ConfigureAwait(false);
                        Log.Debug($"代理：AI 用时 {sw.ElapsedMilliseconds} ms");
                    }
                }
                finally
                {
                    _aiGate.Release();
                }

                if (result != null && result.Success && !string.IsNullOrWhiteSpace(result.Text))
                {
                    _cache.Set(cacheKey, result.Text);
                    return result.Text;
                }

                LastError = result?.Error ?? "无结果";
                Log.Warn("代理：翻译失败 - " + LastError);
                return null;
            }
            catch (OperationCanceledException)
            {
                LastError = ct.IsCancellationRequested ? "客户端已取消" : "AI 调用超出时间预算";
                Log.Warn("代理：翻译失败 - " + LastError);
                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Exception("代理翻译异常", ex);
                return null;
            }
        }

        /// <summary>
        /// 谷歌兜底：把 AyuGram 发来的原始请求体原样转发给真正的谷歌翻译接口，
        /// 取回译文（同时写入缓存，重复文本不会再走一遍）。
        /// </summary>
        private async Task<string> TranslateViaGoogleAsync(string rawBody, ParsedRequest parsed, CancellationToken ct)
        {
            try
            {
                var (ok, body, err) = await Http.SendWithRetryAsync(
                    () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Post, GoogleOriginalUrl)
                        {
                            Content = new StringContent(rawBody, Encoding.UTF8, "application/json+protobuf"),
                        };
                        req.Headers.TryAddWithoutValidation("X-Goog-Api-Key", GoogleApiKey);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");
                        return req;
                    },
                    timeoutSeconds: 4, maxRetries: 0, ct).ConfigureAwait(false);

                if (!ok)
                {
                    LastError = "谷歌兜底失败：" + err;
                    Log.Warn("代理：" + LastError);
                    return null;
                }

                string text = ExtractGoogleResponseText(body);
                if (string.IsNullOrWhiteSpace(text))
                {
                    LastError = "谷歌兜底返回空译文";
                    Log.Warn("代理：" + LastError);
                    return null;
                }

                Log.Info("代理：AI 翻译失效，已回退到谷歌免费翻译。");
                string cacheKey = (parsed.From ?? "auto") + "\u0001" + NormalizeLang(parsed.To) + "\u0001" + FromHtml(parsed.Text);
                _cache.Set(cacheKey, text);
                RequestLogged?.Invoke($"{parsed.From}→{NormalizeLang(parsed.To)}  " +
                                      $"{Truncate(parsed.Text, 50)}  ⇒  {Truncate(text, 50)}（谷歌兜底）");
                return text;
            }
            catch (OperationCanceledException)
            {
                LastError = "谷歌兜底超时";
                Log.Warn("代理：" + LastError);
                return null;
            }
            catch (Exception ex)
            {
                LastError = "谷歌兜底异常：" + ex.Message;
                Log.Warn("代理：" + LastError);
                return null;
            }
        }

        /// <summary>从谷歌响应里提取译文（递归收集字符串后用空格拼接，与客户端解析方式一致）。</summary>
        internal static string ExtractGoogleResponseText(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using (var doc = JsonDocument.Parse(body))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
                    return CollectAllStrings(root[0]);
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string CollectAllStrings(JsonElement node)
        {
            var sb = new StringBuilder();
            CollectInto(node, sb);
            return sb.ToString().Trim();
        }

        private static void CollectInto(JsonElement node, StringBuilder sb)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.String:
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(node.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray()) CollectInto(item, sb);
                    break;
                case JsonValueKind.Object:
                    if (node.TryGetProperty("text", out var t)) CollectInto(t, sb);
                    if (node.TryGetProperty("trans", out var tr)) CollectInto(tr, sb);
                    break;
            }
        }

        /// <summary>把 AyuGram 传来的语言代码（如 zh-CN / zh）规范成界面代码。</summary>
        private static string NormalizeLang(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "zh-CN";
            string l = lang.Trim();
            if (l.Length == 2)
            {
                switch (l.ToLowerInvariant())
                {
                    case "zh": return "zh-CN";
                    case "en": return "en";
                }
            }
            return l;
        }

        // ----------------------------------------------------------- HTTP 细节

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public string Body;
        }

        private static async Task<HttpRequest> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            var raw = new MemoryStream();

            // 读到头部结束（\r\n\r\n）
            int headerEnd = -1;
            while (headerEnd < 0)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (n <= 0) return null;
                raw.Write(buffer, 0, n);
                headerEnd = IndexOfHeaderEnd(raw.ToArray());
                if (raw.Length > 1024 * 1024) return null;
            }

            var all = raw.ToArray();
            string headerText = Encoding.UTF8.GetString(all, 0, headerEnd);

            var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            var requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var req = new HttpRequest
            {
                Method = requestLine[0].ToUpperInvariant(),
                Path = requestLine[1],
            };

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim();
                string val = lines[i].Substring(colon + 1).Trim();
                req.Headers[key] = val;
            }

            int contentLength = 0;
            if (req.Headers.TryGetValue("Content-Length", out string cl))
                int.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);

            var bodyBytes = new List<byte>();
            int bodyStart = headerEnd + 4;
            if (all.Length > bodyStart) bodyBytes.AddRange(SubArray(all, bodyStart, all.Length - bodyStart));

            while (bodyBytes.Count < contentLength)
            {
                int n = await stream.ReadAsync(buffer, 0, Math.Min(buffer.Length, contentLength - bodyBytes.Count), ct)
                    .ConfigureAwait(false);
                if (n <= 0) break;
                bodyBytes.AddRange(SubArray(buffer, 0, n));
            }

            req.Body = bodyBytes.Count == 0
                ? ""
                : Encoding.UTF8.GetString(bodyBytes.ToArray(), 0, Math.Min(bodyBytes.Count, contentLength > 0 ? contentLength : bodyBytes.Count));

            return req;
        }

        private static byte[] SubArray(byte[] src, int start, int count)
        {
            var dst = new byte[Math.Min(count, src.Length - start)];
            Array.Copy(src, start, dst, 0, dst.Length);
            return dst;
        }

        private static int IndexOfHeaderEnd(byte[] data)
        {
            for (int i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            return -1;
        }

        private static async Task WriteJsonAsync(NetworkStream stream, int status, string json)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(json);
            string head =
                "HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "\r\n";

            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, 0, headBytes.Length).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        private static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        public void Dispose()
        {
            Stop();
            try { _translator?.Dispose(); } catch { }
            try { _aiGate?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }
        }
    }
}
