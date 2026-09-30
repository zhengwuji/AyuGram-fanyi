using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;

namespace AyuTranslate.Translate
{
    /// <summary>
    /// 从 OpenAI 兼容接口的 /models 拉取可用模型列表。
    /// 用来在设置界面里给出真实可选模型，避免手填错名字
    /// （填错的典型表现是网关回 403 / MODEL_NOT_IN_PLAN / Model not recognized）。
    /// </summary>
    public static class ModelListService
    {
        public sealed class ModelInfo
        {
            public string Id;
            public string OwnedBy;
            public long ContextWindow;

            public override string ToString() =>
                string.IsNullOrWhiteSpace(OwnedBy) ? Id : Id + "   (" + OwnedBy + ")";
        }

        /// <summary>根据配置推断 /models 地址。</summary>
        public static string ResolveModelsUrl(AppConfig cfg)
        {
            string baseUrl = cfg?.ApiBaseUrl ?? "";
            string endpoint = cfg?.ApiEndpoint ?? "";

            // 优先从完整 endpoint 反推（去掉 /chat/completions）
            string source = !string.IsNullOrWhiteSpace(endpoint) ? endpoint : baseUrl;
            if (string.IsNullOrWhiteSpace(source)) return null;

            string s = source.TrimEnd('/');
            const string chatSuffix = "/chat/completions";
            if (s.EndsWith(chatSuffix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - chatSuffix.Length);

            // 有些网关的模型列表不在 /v1 下，这里保持原样拼 /models
            return s.TrimEnd('/') + "/models";
        }

        public static async Task<List<ModelInfo>> FetchAsync(AppConfig cfg, CancellationToken ct)
        {
            var result = new List<ModelInfo>();
            string url = ResolveModelsUrl(cfg);
            if (string.IsNullOrWhiteSpace(url)) return result;

            try
            {
                var (ok, body, err) = await Http.SendWithRetryAsync(
                    () =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, url);
                        if (!string.IsNullOrWhiteSpace(cfg.ApiKey))
                            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cfg.ApiKey.Trim());
                        Http.ApplyExtraHeaders(req, cfg.ExtraHeaders);
                        return req;
                    },
                    20, 1, ct).ConfigureAwait(false);

                if (!ok)
                {
                    Log.Debug("获取模型列表失败：" + err);
                    return result;
                }

                using (var doc = JsonDocument.Parse(body))
                {
                    JsonElement arr;
                    var root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Array)
                        arr = root;
                    else if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                        arr = data;
                    else if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                        arr = models;
                    else
                        return result;

                    foreach (var item in arr.EnumerateArray())
                    {
                        var info = new ModelInfo();

                        if (item.ValueKind == JsonValueKind.String)
                        {
                            info.Id = item.GetString();
                        }
                        else if (item.ValueKind == JsonValueKind.Object)
                        {
                            if (item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                                info.Id = id.GetString();
                            else if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                                info.Id = name.GetString();

                            if (item.TryGetProperty("owned_by", out var owned) && owned.ValueKind == JsonValueKind.String)
                                info.OwnedBy = owned.GetString();

                            if (item.TryGetProperty("context_window", out var cw) &&
                                cw.ValueKind == JsonValueKind.Number)
                            {
                                info.ContextWindow = cw.GetInt64();
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(info.Id)) result.Add(info);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("解析模型列表失败：" + ex.Message);
            }

            return result
                .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
