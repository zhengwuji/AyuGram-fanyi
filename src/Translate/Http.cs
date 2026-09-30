using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>共享 HttpClient（避免 socket 耗尽），带重试。</summary>
    internal static class Http
    {
        private static HttpClient _client;
        private static readonly object Gate = new object();

        public static HttpClient Client
        {
            get
            {
                if (_client == null)
                {
                    lock (Gate)
                    {
                        if (_client == null)
                        {
                            var handler = new HttpClientHandler
                            {
                                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                                UseProxy = true,
                                AllowAutoRedirect = true,
                            };
                            _client = new HttpClient(handler)
                            {
                                Timeout = Timeout.InfiniteTimeSpan, // 单请求用 CancellationToken 控制
                            };
                            _client.DefaultRequestHeaders.UserAgent.ParseAdd(
                                "AyuTranslate/1.0 (+https://github.com/AyuGram/AyuGramDesktop)");
                        }
                    }
                }
                return _client;
            }
        }

        /// <summary>解析 "Key: Value" 多行额外请求头。</summary>
        public static void ApplyExtraHeaders(HttpRequestMessage req, string extraHeaders)
        {
            if (string.IsNullOrWhiteSpace(extraHeaders)) return;
            foreach (var raw in extraHeaders.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int idx = line.IndexOf(':');
                if (idx <= 0) continue;
                string key = line.Substring(0, idx).Trim();
                string val = line.Substring(idx + 1).Trim();
                if (key.Length == 0) continue;
                try
                {
                    req.Headers.TryAddWithoutValidation(key, val);
                }
                catch { }
            }
        }

        public static StringContent JsonContent(string json)
        {
            return new StringContent(json, Encoding.UTF8, "application/json");
        }

        /// <summary>
        /// 发送请求并把响应体读成字符串（不抛异常，错误都塞进结果元组）。
        /// StatusCode 在没有得到 HTTP 响应（超时 / 连接失败）时为 0。
        /// </summary>
        public static async Task<(bool Ok, int StatusCode, string Body, string Error)> SendAsync(
            HttpRequestMessage req, int timeoutSeconds, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
                try
                {
                    using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, cts.Token)
                               .ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            string shortBody = TextUtil.Truncate(body ?? "", 500);
                            return (false, (int)resp.StatusCode, body,
                                $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {shortBody}");
                        }
                        return (true, (int)resp.StatusCode, body, null);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (false, 0, null, $"请求超时（{timeoutSeconds}s）");
                }
                catch (Exception ex)
                {
                    return (false, 0, null, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        /// <summary>带重试的请求。</summary>
        public static async Task<(bool Ok, string Body, string Error)> SendWithRetryAsync(
            Func<HttpRequestMessage> factory, int timeoutSeconds, int maxRetries, CancellationToken ct)
        {
            string lastError = null;
            for (int attempt = 0; attempt <= Math.Max(0, maxRetries); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                using (var req = factory())
                {
                    var (ok, status, body, err) = await SendAsync(req, timeoutSeconds, ct).ConfigureAwait(false);
                    if (ok) return (true, body, null);
                    lastError = err;

                    // 4xx 不重试（除 429 限流和 408 请求超时）；0 = 没拿到响应（连接/超时），可重试
                    bool noRetry = status >= 400 && status < 500 && status != 429 && status != 408;
                    if (noRetry) break;
                }
                if (attempt < maxRetries)
                {
                    int delay = 400 * (attempt + 1);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }
            return (false, null, lastError ?? "请求失败");
        }
    }
}
