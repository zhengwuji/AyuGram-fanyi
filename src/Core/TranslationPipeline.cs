using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Translate;
namespace AyuTranslate.Core
{
    /// <summary>一个待绘制的覆盖条目。</summary>
    public sealed class OverlayItem
    {
        /// <summary>矩形，坐标相对于目标窗口客户区。</summary>
        public Rectangle Bounds;

        public string Translation = "";
        public string Original = "";
        public bool Pending;
        public bool Failed;
        public string Error = "";

        public override string ToString() =>
            $"{Bounds} -> {TextUtil.Truncate(Translation.Replace("\n", " / "), 50)}";
    }

    public sealed class PipelineStats
    {
        public int Captures;
        public int FramesSkipped;
        public int OcrRuns;
        public int BlocksFound;
        public int BlocksDeduplicated;
        public int OverlayItems;
        public int TranslationsRequested;
        public int TranslationsOk;
        public int TranslationsFailed;
        public int CacheHits;
        public int PositionReuse;
        public double LastCaptureMs;
        public double LastOcrMs;
        public double LastTranslateMs;
        public double LastTotalMs;
        public string LastError = "";
        public DateTime LastRunUtc = DateTime.MinValue;

        public string Summary =>
            $"抓图 {Captures} 次（跳帧 {FramesSkipped}）/ OCR {OcrRuns} 次 / 文本块 {BlocksFound}(去重 {BlocksDeduplicated}) / 覆盖 {OverlayItems} / " +
            $"翻译 {TranslationsOk}成功 {TranslationsFailed}失败 / 缓存命中 {CacheHits} / 位置复用 {PositionReuse}";
    }

    /// <summary>
    /// 主流水线：抓图 → OCR → 分块 → 翻译 → 产出覆盖条目。
    /// </summary>
    public sealed class TranslationPipeline : IDisposable
    {
        private AppConfig _cfg;
        private OcrService _ocr;
        private ITranslator _translator;
        private readonly TranslationCache _cache;
        private readonly SemaphoreSlim _runGate = new SemaphoreSlim(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;
        private bool _disposed;

        /// <summary>保护 LastCapture 调试快照的锁（不要 lock(this)）。</summary>
        private readonly object _bitmapLock = new object();

        /// <summary>上一帧的聊天区图像（帧跳过用），由流水线持有并在下一帧替换时释放。</summary>
        private Bitmap _lastFrameBitmap;
        private Rectangle _lastFrameRegion;

        /// <summary>上一帧识别行的中位高度（自适应缩放用），原图坐标。</summary>
        private double _lastMedianLineHeight;
        private bool _scaleSkipped;

        public PipelineStats Stats { get; } = new PipelineStats();

        /// <summary>上一次成功抓取的聊天区图像（调试用）。</summary>
        public Bitmap LastCapture { get; private set; }

        /// <summary>上一次识别出的文本块。</summary>
        public List<TextBlock> LastBlocks { get; private set; } = new List<TextBlock>();

        /// <summary>上一次使用的区域（客户区坐标）。</summary>
        public Rectangle LastRegion { get; private set; }

        public TranslationCache Cache => _cache;

        /// <summary>错误事件（用于界面提示）。</summary>
        public event Action<string> ErrorReported;

        public TranslationPipeline(AppConfig cfg)
        {
            _cfg = cfg;
            _cache = new TranslationCache(cfg.CacheMaxEntries);
            RebuildComponents();
        }

        public AppConfig Config => _cfg;

        /// <summary>配置变更后重建 OCR / 翻译器。</summary>
        public void RebuildComponents()
        {
            try { _translator?.Dispose(); } catch { }
            try { _ocr?.Dispose(); } catch { }

            _translator = TranslatorFactory.Create(_cfg);
            _ocr = new OcrService(_cfg.OcrLanguages);

            if (_cfg.CacheEnabled == false) _cache.Clear();

            Log.Info("翻译后端：" + _translator.Name + "，已配置=" + _translator.IsConfigured);
        }

        public void UpdateConfig(AppConfig cfg)
        {
            _cfg = cfg;
            RebuildComponents();
        }

        public ITranslator Translator => _translator;
        public OcrService Ocr => _ocr;

        /// <summary>
        /// 执行一轮：抓取 + 识别 + 翻译。
        /// <paramref name="allowFrameSkip"/> 为 false 时强制完整执行（手动翻译用，
        /// 否则画面没变时会直接沿用上次结果，重试不了失败的分块）。
        /// </summary>
        public async Task<List<OverlayItem>> RunOnceAsync(TargetWindow tw, CancellationToken ct,
            bool translate = true, bool allowFrameSkip = true)
        {
            var items = new List<OverlayItem>();
            if (tw == null || !tw.IsWindowAlive()) return items;
            if (!await _runGate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                Log.Debug("上一轮尚未结束，跳过本次");
                return items;
            }

            var swTotal = Stopwatch.StartNew();
            try
            {
                WindowLocator.Refresh(tw);
                if (!tw.IsUsable)
                {
                    string reason = tw.UnusableReason();
                    Stats.LastError = "窗口不可用：" + reason;
                    Log.Warn(Stats.LastError);
                    ErrorReported?.Invoke(Stats.LastError);
                    return items;
                }

                // ---- 1) 抓图 ----
                var autoRegion = BlockDetector.AutoDetectChatRegion(tw.ClientWidth, tw.ClientHeight, _cfg);
                var region = BlockDetector.ResolveRegion(_cfg.ChatRegion, autoRegion, tw.ClientWidth, tw.ClientHeight);
                LastRegion = region;

                var fullWindow = ScreenCapture.Capture(tw, CaptureBackend.PrintWindow, clientOnly: true);
                Stats.Captures++;
                if (fullWindow == null)
                {
                    Stats.LastError = "抓图失败（PrintWindow 返回空）。窗口可能已最小化或被系统屏蔽。";
                    ErrorReported?.Invoke(Stats.LastError);
                    return items;
                }

                // 全白/全黑 = 窗口没有真正渲染（被 cloaked、还在加载、或渲染器异常）
                if (IsEffectivelyBlank(fullWindow))
                {
                    Stats.LastError = tw.IsCloaked
                        ? "窗口不在当前虚拟桌面上（被系统屏蔽），无法抓图。请切换回 AyuGram 所在桌面。"
                        : "抓到的图像是空白，AyuGram 可能还在加载或渲染异常。";
                    Log.Warn(Stats.LastError);
                    ErrorReported?.Invoke(Stats.LastError);
                    fullWindow.Dispose();
                    return items;
                }

                var regionBitmap = ScreenCapture.Crop(fullWindow, region.X, region.Y, region.Width, region.Height);
                fullWindow.Dispose();
                if (regionBitmap == null) return items;

                // 调试快照只在 DebugLog 开启时保留，避免每帧多克隆一整张区域图
                if (Log.Verbose)
                {
                    lock (_bitmapLock)
                    {
                        LastCapture?.Dispose();
                        LastCapture = (Bitmap)regionBitmap.Clone();
                    }
                }

                // ---- 帧跳过：画面与上一帧一致时直接沿用上次的覆盖条目 ----
                // 采样差异 < 1% 视为一致（光标闪烁、输入中提示这类微动不会过线，
                // 新消息 / 滚动一定会过线）。OCR 是单帧最贵的环节，静止画面下全部省掉。
                if (translate && allowFrameSkip && _cfg.SkipUnchangedFrames &&
                    FrameMatchesPrevious(regionBitmap, region))
                {
                    regionBitmap.Dispose();
                    Stats.FramesSkipped++;
                    Stats.LastTotalMs = swTotal.Elapsed.TotalMilliseconds;
                    Stats.LastRunUtc = DateTime.UtcNow;
                    return new List<OverlayItem>(_lastOverlayItems);
                }

                // 本帧成为新的缓存帧；此后由帧缓存持有，后续路径不再释放它
                var frame = regionBitmap;
                SwapFrameBitmap(frame, region);

                var swCap = Stopwatch.StartNew();

                // ---- 2) OCR ----
                var swOcr = Stopwatch.StartNew();
                double effScale = EffectiveOcrScale();
                _scaleSkipped = effScale <= 1.01;
                var scaled = OcrService.Scale(frame, effScale);
                List<OcrLine> lines;
                try
                {
                    lines = await _ocr.RecognizeAsync(scaled, effScale, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Scale 在倍数≈1 时会原样返回 frame 引用，此时不能 Dispose
                    if (!ReferenceEquals(scaled, frame)) scaled.Dispose();
                }
                _lastMedianLineHeight = MedianLineHeight(lines);
                Stats.OcrRuns++;
                Stats.LastOcrMs = swOcr.Elapsed.TotalMilliseconds;
                swCap.Stop();
                Stats.LastCaptureMs = swCap.Elapsed.TotalMilliseconds - Stats.LastOcrMs;

                // ---- 3) 分块 ----
                var blocks = BlockDetector.GroupLines(lines, _cfg);
                blocks = blocks.Where(b => IsInsideChatArea(b, region, tw.ClientWidth)).ToList();

                // 多语言 OCR 会对同一行产出重复块；必须在翻译之前去重，
                // 否则同一句话会被重复发往接口，白花一倍费用。
                int beforeDedup = blocks.Count;
                blocks = BlockDetector.DeduplicateBlocks(blocks);
                Stats.BlocksDeduplicated = beforeDedup - blocks.Count;
                if (Stats.BlocksDeduplicated > 0)
                {
                    Log.Debug($"重复块去重：{beforeDedup} → {blocks.Count}");
                }

                LastBlocks = blocks;
                Stats.BlocksFound = blocks.Count;

                if (blocks.Count == 0)
                {
                    Stats.LastTotalMs = swTotal.Elapsed.TotalMilliseconds;
                    Stats.LastRunUtc = DateTime.UtcNow;
                    return items;
                }

                // ---- 4) 过滤 / 翻译 ----
                var todo = new List<TextBlock>();
                var resolved = new Dictionary<TextBlock, string>();
                foreach (var b in blocks)
                {
                    string text = b.Text.Trim();
                    if (text.Length == 0) continue;
                    if (_cfg.SkipTargetLanguage && TextUtil.IsAlreadyTargetLanguage(text, _cfg.TargetLanguage))
                    {
                        Log.Debug("跳过（已是目标语言）：" + TextUtil.Truncate(text, 40));
                        continue;
                    }

                    // 先查严格键，再查宽松键。宽松键能吸收 OCR 的抖动，
                    // 避免同一句话因为末尾多了个标点就重新请求接口。
                    if (_cfg.CacheEnabled)
                    {
                        string strict = TextUtil.CacheKey(text, _cfg.SourceLanguage, _cfg.TargetLanguage);
                        if (_cache.TryGet(strict, out string hit))
                        {
                            Stats.CacheHits++;
                            resolved[b] = hit;
                            continue;
                        }

                        string loose = TextUtil.LooseCacheKey(text, _cfg.SourceLanguage, _cfg.TargetLanguage);
                        if (loose != null && !string.Equals(loose, strict, StringComparison.Ordinal) &&
                            _cache.TryGet(loose, out string hit2))
                        {
                            Stats.CacheHits++;
                            resolved[b] = hit2;
                            // 顺手把严格键也补上，下次走快路径
                            _cache.Set(strict, hit2);
                            continue;
                        }
                    }

                    todo.Add(b);
                }

                var translated = new Dictionary<TextBlock, string>(resolved);

                // 位置复用：上一帧同一个位置、内容又几乎一样的块，
                // 直接沿用上次译文。OCR 抖动导致缓存未命中时这一步能兜住。
                for (int k = todo.Count - 1; k >= 0; k--)
                {
                    var b = todo[k];
                    var prev = FindPreviousAtSamePlace(b);
                    if (prev == null) continue;

                    if (TextUtil.Similarity(prev.Original, b.Text) < _cfg.ReuseSimilarity)
                        continue;

                    if (string.IsNullOrWhiteSpace(prev.Translation)) continue;

                    Stats.PositionReuse++;
                    translated[b] = prev.Translation;
                    StoreCache(b.Text, prev.Translation);
                    todo.RemoveAt(k);
                }

                if (translate && todo.Count > 0 && _translator != null)
                {
                    var swTr = Stopwatch.StartNew();
                    var results = await TranslateBlocksAsync(todo, ct).ConfigureAwait(false);
                    Stats.LastTranslateMs = swTr.Elapsed.TotalMilliseconds;
                    swTr.Stop();

                    Stats.TranslationsRequested += todo.Count;

                    for (int i = 0; i < todo.Count; i++)
                    {
                        var r = results[i];
                        if (r != null && r.Success && !string.IsNullOrWhiteSpace(r.Text))
                        {
                            Stats.TranslationsOk++;
                            translated[todo[i]] = r.Text;
                            if (_cfg.CacheEnabled) StoreCache(todo[i].Text, r.Text);
                        }
                        else
                        {
                            Stats.TranslationsFailed++;
                            string err = r?.Error ?? "未返回结果";
                            Stats.LastError = err;
                            Log.Debug("翻译失败：" + err);
                        }
                    }
                }

                // ---- 5) 生成覆盖条目 ----
                foreach (var b in blocks)
                {
                    if (!translated.TryGetValue(b, out string text) || string.IsNullOrWhiteSpace(text)) continue;

                    var rect = ExpandRect(b, region, tw, _cfg);
                    items.Add(new OverlayItem
                    {
                        Bounds = rect,
                        Translation = text,
                        Original = b.Text,
                    });
                }

                items = DeduplicateItems(items);
                Stats.OverlayItems = items.Count;
                _lastOverlayItems = items;
                _lastRegion = region;

                Stats.LastTotalMs = swTotal.Elapsed.TotalMilliseconds;
                Stats.LastRunUtc = DateTime.UtcNow;
                return items;
            }
            catch (OperationCanceledException)
            {
                return items;
            }
            catch (Exception ex)
            {
                Stats.LastError = ex.Message;
                Log.Exception("流水线异常", ex);
                ErrorReported?.Invoke("翻译流水线异常：" + ex.Message);
                return items;
            }
            finally
            {
                swTotal.Stop();
                _runGate.Release();
            }
        }

        /// <summary>当前帧与上一帧是否一致（区域相同且采样差异 &lt; 1%）。</summary>
        private bool FrameMatchesPrevious(Bitmap frame, Rectangle region)
        {
            var prev = _lastFrameBitmap;
            if (prev == null) return false;
            if (_lastFrameRegion != region) return false;
            return ScreenCapture.DiffRatio(prev, frame) < 0.01;
        }

        /// <summary>用新帧替换缓存帧，并释放旧帧的内存。</summary>
        private void SwapFrameBitmap(Bitmap frame, Rectangle region)
        {
            var old = _lastFrameBitmap;
            _lastFrameBitmap = frame;
            _lastFrameRegion = region;
            old?.Dispose();
        }

        /// <summary>
        /// 自适应 OCR 缩放：上一帧文字已经够大（行高中位数 ≥ 28px）就跳过放大，
        /// 省一次全区域高质量插值。带迟滞（恢复阈值 14px），避免在阈值附近逐帧抖动。
        /// </summary>
        private double EffectiveOcrScale()
        {
            if (!_cfg.AdaptiveOcrScale || _cfg.OcrScale <= 1.01) return _cfg.OcrScale;
            if (_scaleSkipped)
            {
                if (_lastMedianLineHeight >= 14) return 1.0;
            }
            else if (_lastMedianLineHeight >= 28)
            {
                return 1.0;
            }
            return _cfg.OcrScale;
        }

        private static double MedianLineHeight(List<OcrLine> lines)
        {
            if (lines == null || lines.Count == 0) return 0;
            var hs = lines.Select(l => l.Height).OrderBy(h => h).ToList();
            return hs[hs.Count / 2];
        }

        /// <summary>上一帧里，落在同一屏幕位置的覆盖条目。</summary>
        private OverlayItem FindPreviousAtSamePlace(TextBlock b)
        {
            if (_lastOverlayItems == null || _lastOverlayItems.Count == 0) return null;

            OverlayItem best = null;
            double bestDist = double.MaxValue;

            foreach (var it in _lastOverlayItems)
            {
                // 块的坐标是「区域坐标」，覆盖条目是「客户区坐标」，需要补上区域偏移
                double itemY = it.Bounds.Y - _lastRegion.Y;
                double itemX = it.Bounds.X - _lastRegion.X;

                double dy = Math.Abs(itemY - b.Y);
                double dx = Math.Abs(itemX - b.X);

                // 同一行、同一列附近
                if (dy > Math.Max(6, b.Height * 0.5)) continue;
                if (dx > Math.Max(12, b.Width * 0.25)) continue;

                double dist = dy * 3 + dx;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = it;
                }
            }

            return best;
        }

        private List<OverlayItem> _lastOverlayItems = new List<OverlayItem>();
        private Rectangle _lastRegion;

        /// <summary>
        /// 判断图像是否「基本空白」。
        /// 采样统计颜色种类与方差：全白 / 全黑 / 纯色都会被判为空白。
        /// </summary>
        internal static bool IsEffectivelyBlank(Bitmap bmp)
        {
            if (bmp == null || bmp.Width < 8 || bmp.Height < 8) return true;

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                long sum = 0, sumSq = 0;
                int n = 0;
                var seen = new HashSet<int>();
                unsafe
                {
                    byte* p = (byte*)data.Scan0;
                    int step = Math.Max(4, bmp.Width / 80);
                    for (int y = 0; y < bmp.Height; y += step)
                    {
                        byte* row = p + y * data.Stride;
                        for (int x = 0; x < bmp.Width; x += step)
                        {
                            int o = x * 4;
                            int lum = (row[o] * 30 + row[o + 1] * 59 + row[o + 2] * 11) / 100;
                            sum += lum;
                            sumSq += (long)lum * lum;
                            n++;
                            if (seen.Count < 32) seen.Add(row[o] << 16 | row[o + 1] << 8 | row[o + 2]);
                        }
                    }
                }

                if (n == 0) return true;
                double mean = (double)sum / n;
                double variance = (double)sumSq / n - mean * mean;

                // 颜色种类很少（<3）或方差极小 → 判定为空白
                if (seen.Count < 3) return true;
                if (variance < 8.0) return true;
                return false;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>同时写入严格键与宽松键。</summary>
        private void StoreCache(string text, string translation)        {
            _cache.Set(TextUtil.CacheKey(text, _cfg.SourceLanguage, _cfg.TargetLanguage), translation);
            string loose = TextUtil.LooseCacheKey(text, _cfg.SourceLanguage, _cfg.TargetLanguage);
            if (loose != null) _cache.Set(loose, translation);
        }

        /// <summary>带节流与批量合并的翻译调用。</summary>
        private async Task<List<TranslationResult>> TranslateBlocksAsync(List<TextBlock> blocks, CancellationToken ct)
        {
            var all = new List<TranslationResult>(blocks.Count);

            // 按批切分（长文本单独成批，避免超出上下文）；条数与字符数可配置
            int maxBatchChars = Math.Max(200, _cfg.MaxBatchChars);
            int maxBatchItems = Math.Max(1, _cfg.MaxBatchItems);

            int i = 0;
            while (i < blocks.Count)
            {
                int chars = 0;
                var batch = new List<TextBlock>();
                while (i < blocks.Count && batch.Count < maxBatchItems)
                {
                    int len = blocks[i].Text.Length;
                    if (batch.Count > 0 && chars + len > maxBatchChars) break;
                    batch.Add(blocks[i]);
                    chars += len;
                    i++;
                }

                await ThrottleAsync(ct).ConfigureAwait(false);

                var reqs = batch.Select(b => new TranslationRequest
                {
                    Text = b.Text,
                    SourceLanguage = _cfg.SourceLanguage,
                    TargetLanguage = _cfg.TargetLanguage,
                }).ToList();

                List<TranslationResult> res;
                try
                {
                    res = await _translator.TranslateBatchAsync(reqs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Exception("批量翻译异常", ex);
                    res = reqs.Select(_ => TranslationResult.Fail(ex.Message, _translator.Name)).ToList();
                }

                if (res == null || res.Count != batch.Count)
                {
                    res = new List<TranslationResult>();
                    for (int k = 0; k < batch.Count; k++)
                        res.Add(TranslationResult.Fail("返回数量不匹配", _translator.Name));
                }

                all.AddRange(res);
            }

            return all;
        }

        private async Task ThrottleAsync(CancellationToken ct)
        {
            int min = _cfg.MinRequestIntervalMs;
            if (min <= 0) return;
            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            if (elapsed.TotalMilliseconds < min)
            {
                await Task.Delay(min - (int)elapsed.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            _lastRequestUtc = DateTime.UtcNow;
        }

        /// <summary>判断块是否落在聊天内容区（过滤掉会话列表等）。</summary>
        private bool IsInsideChatArea(TextBlock b, Rectangle region, int clientWidth)
        {
            if (b.Y + b.Height < region.Top - 4) return false;
            if (b.Y > region.Bottom + 4) return false;
            if (b.X < region.Left - 8) return false;

            if (!_cfg.IncludeSidebar)
            {
                // 明显在左侧会话列表的块丢掉
                if (b.X + b.Width < region.Left + 4) return false;
            }
            return true;
        }

        /// <summary>把 OCR 块的位置换算成客户区坐标，并向外扩张以盖住原文。</summary>
        private Rectangle ExpandRect(TextBlock b, Rectangle region, TargetWindow tw, AppConfig cfg)
        {
            int bleed = Math.Max(0, cfg.OverlayBleed);
            int padY = Math.Max(0, cfg.OverlayPaddingY);

            int x = region.X + (int)Math.Floor(b.X) - bleed;
            int y = region.Y + (int)Math.Floor(b.Y) - bleed - padY;
            int w = (int)Math.Ceiling(b.Width) + bleed * 2;
            int h = (int)Math.Ceiling(b.Height) + bleed * 2 + padY * 2;

            // 同一批块可能互相重叠（比如头像/昵称和正文挨得很近），
            // 这里保证最小尺寸，避免出现细长条把相邻行切坏。
            w = Math.Max(w, 40);
            h = Math.Max(h, 20);

            var r = new Rectangle(x, y, w, h);

            // 夹在客户区内
            var bounds = new Rectangle(0, 0, tw.ClientWidth, tw.ClientHeight);
            r.Intersect(bounds);
            return r;
        }

        /// <summary>
        /// 去掉互相包含/高度重叠的块，只保留信息量最大的那个。
        /// OCR 的多语言并行识别会产生重复行，这里顺手做去重。
        /// </summary>
        private static List<OverlayItem> DeduplicateItems(List<OverlayItem> items)
        {
            if (items.Count <= 1) return items;

            var ordered = items
                .OrderByDescending(i => (double)(i.Translation?.Length ?? 0))
                .ToList();

            var kept = new List<OverlayItem>();
            foreach (var candidate in ordered)
            {
                bool duplicate = false;
                foreach (var k in kept)
                {
                    if (IntersectionRatio(candidate.Bounds, k.Bounds) > 0.6)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate) kept.Add(candidate);
            }

            return kept.OrderBy(i => i.Bounds.Y).ThenBy(i => i.Bounds.X).ToList();
        }

        private static double IntersectionRatio(Rectangle a, Rectangle b)
        {
            var inter = Rectangle.Intersect(a, b);
            if (inter.IsEmpty) return 0;
            double areaA = (double)a.Width * a.Height;
            double areaB = (double)b.Width * b.Height;
            double smaller = Math.Min(areaA, areaB);
            if (smaller <= 0) return 0;
            return inter.Width * inter.Height / smaller;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _translator?.Dispose(); } catch { }
            try { _ocr?.Dispose(); } catch { }
            try { _runGate.Dispose(); } catch { }
            lock (_bitmapLock)
            {
                LastCapture?.Dispose();
                LastCapture = null;
            }
            _lastFrameBitmap?.Dispose();
            _lastFrameBitmap = null;
        }
    }
}
