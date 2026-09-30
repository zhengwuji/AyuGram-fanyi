using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AyuTranslate.Translate
{
    public sealed class TranslationRequest
    {
        public string Text;
        public string SourceLanguage = "auto";
        public string TargetLanguage = "zh-CN";
    }

    public sealed class TranslationResult
    {
        public bool Success;
        public string Text = "";
        public string Error = "";
        public string ProviderName = "";
        /// <summary>服务端返回的原始响应（调试用，可能被截断）。</summary>
        public string RawResponse = "";

        public static TranslationResult Ok(string text, string provider, string raw = null) =>
            new TranslationResult { Success = true, Text = text ?? "", ProviderName = provider, RawResponse = raw };

        public static TranslationResult Fail(string error, string provider, string raw = null) =>
            new TranslationResult
            {
                Success = false,
                Error = error ?? "未知错误",
                ProviderName = provider,
                RawResponse = raw ?? "",
            };
    }

    /// <summary>翻译后端统一接口。</summary>
    public interface ITranslator : IDisposable
    {
        string Name { get; }

        /// <summary>是否配置完整，可以发起请求。</summary>
        bool IsConfigured { get; }

        /// <summary>配置缺失时的说明。</summary>
        string ConfigurationHint { get; }

        Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);

        /// <summary>批量翻译（默认逐个调用；OpenAI 兼容后端会覆写为单请求多段）。</summary>
        Task<List<TranslationResult>> TranslateBatchAsync(IReadOnlyList<TranslationRequest> requests, CancellationToken ct);
    }

    /// <summary>批量翻译的默认实现。</summary>
    public abstract class TranslatorBase : ITranslator
    {
        public abstract string Name { get; }

        public virtual bool IsConfigured => true;

        public virtual string ConfigurationHint => "";

        public abstract Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);

        public virtual async Task<List<TranslationResult>> TranslateBatchAsync(
            IReadOnlyList<TranslationRequest> requests, CancellationToken ct)
        {
            var list = new List<TranslationResult>(requests.Count);
            foreach (var r in requests)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    list.Add(await TranslateAsync(r, ct).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    list.Add(TranslationResult.Fail(ex.Message, Name));
                }
            }
            return list;
        }

        public virtual void Dispose() { }
    }
}
