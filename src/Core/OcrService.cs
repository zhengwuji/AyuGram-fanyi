using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AyuTranslate.Core
{
    /// <summary>一行 OCR 结果（原图坐标系，未缩放）。</summary>
    public sealed class OcrLine
    {
        public string Text = "";
        public double X;
        public double Y;
        public double Width;
        public double Height;
        /// <summary>该行使用的识别语言标签。</summary>
        public string Language = "";

        public double CenterY => Y + Height / 2.0;
        public double Right => X + Width;
        public double Bottom => Y + Height;

        public override string ToString() =>
            $"[{X:F0},{Y:F0} {Width:F0}x{Height:F0}] {Text}";
    }

    /// <summary>基于 Windows.Media.Ocr 的本地 OCR 封装（多语言并行识别 + 结果合并）。</summary>
    public sealed class OcrService : IDisposable
    {
        private readonly List<OcrEngine> _engines = new List<OcrEngine>();
        private readonly List<string> _tags = new List<string>();
        private bool _disposed;

        /// <summary>当前实际启用的 OCR 语言标签。</summary>
        public IReadOnlyList<string> ActiveLanguages => _tags;

        public bool HasEngine => _engines.Count > 0;

        public OcrService(IEnumerable<string> preferredLanguages)
        {
            var wanted = (preferredLanguages ?? Enumerable.Empty<string>()).ToList();

            // 优先按配置的顺序创建引擎
            foreach (var tag in wanted)
            {
                var e = TryCreate(tag);
                if (e != null) Add(e, tag);
            }

            // 补一个用户配置语言（兜底，保证至少能识别点什么）
            if (_engines.Count == 0)
            {
                try
                {
                    var e = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (e != null) Add(e, e.RecognizerLanguage?.LanguageTag ?? "auto");
                }
                catch (Exception ex)
                {
                    Log.Warn("创建默认 OCR 引擎失败：" + ex.Message);
                }
            }

            if (_engines.Count == 0)
            {
                Log.Error("没有可用的 Windows OCR 引擎。请到「设置 → 时间和语言 → 语言和区域」为需要的语言安装“光学字符识别”功能。");
            }
            else
            {
                Log.Info("OCR 引擎已就绪：" + string.Join(", ", _tags));
            }
        }

        private void Add(OcrEngine e, string tag)
        {
            if (_tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) return;
            _engines.Add(e);
            _tags.Add(tag);
        }

        private static OcrEngine TryCreate(string tag)
        {
            try
            {
                var lang = new Language(tag);
                var e = OcrEngine.TryCreateFromLanguage(lang);
                if (e == null) Log.Debug("OCR 语言不可用：" + tag);
                return e;
            }
            catch (Exception ex)
            {
                Log.Debug("OCR 语言创建异常 " + tag + "：" + ex.Message);
                return null;
            }
        }

        /// <summary>列出系统已安装的 OCR 语言。</summary>
        public static List<string> AvailableLanguages()
        {
            var list = new List<string>();
            try
            {
                foreach (var l in OcrEngine.AvailableRecognizerLanguages)
                {
                    list.Add(l.LanguageTag);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("枚举 OCR 语言失败：" + ex.Message);
            }
            return list;
        }

        /// <summary>按指定倍数放大位图，提高小字号识别率。</summary>
        public static Bitmap Scale(Bitmap src, double scale)
        {
            if (src == null) return null;
            if (Math.Abs(scale - 1.0) < 0.01) return src;

            int w = Math.Max(1, (int)Math.Round(src.Width * scale));
            int h = Math.Max(1, (int)Math.Round(src.Height * scale));

            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return dst;
        }

        /// <summary>
        /// 把 Bitmap 转成 SoftwareBitmap（Bgra8）。
        /// 32bpp 位图的 stride 恒等于 width*4，因此直接从像素缓冲拷贝，
        /// 不再走「BMP 编码 → 流 → 解码器」那一圈。
        /// </summary>
        private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int bytes = data.Stride * bmp.Height;
                var buffer = new byte[bytes];
                Marshal.Copy(data.Scan0, buffer, 0, bytes);
                return SoftwareBitmap.CreateCopyFromBuffer(
                    buffer.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height,
                    BitmapAlphaMode.Premultiplied);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>
        /// 识别一张位图。返回的坐标已经除回原始（未放大）坐标系。
        /// </summary>
        /// <param name="bmp">已放大的位图。</param>
        /// <param name="scale">放大倍数（用于坐标还原）。</param>
        /// <param name="ct">取消令牌。</param>
        public async Task<List<OcrLine>> RecognizeAsync(Bitmap bmp, double scale, CancellationToken ct)
        {
            var result = new List<OcrLine>();
            if (bmp == null || _engines.Count == 0 || _disposed) return result;

            SoftwareBitmap sb;
            try
            {
                sb = ToSoftwareBitmap(bmp);
            }
            catch (Exception ex)
            {
                Log.Exception("位图转换失败", ex);
                return result;
            }

            if (sb == null) return result;

            var tasks = new List<Task<List<OcrLine>>>();
            for (int i = 0; i < _engines.Count; i++)
            {
                var engine = _engines[i];
                var tag = _tags[i];
                tasks.Add(Task.Run(async () =>
                {
                    var lines = new List<OcrLine>();
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        // SoftwareBitmap 是 agile 对象且创建后只读，可被多个引擎并发共享
                        var ocr = await engine.RecognizeAsync(sb).AsTask(ct).ConfigureAwait(false);

                        foreach (var line in ocr.Lines)
                        {
                            string text = line.Text;
                            if (string.IsNullOrWhiteSpace(text)) continue;

                            double minX = double.MaxValue, minY = double.MaxValue;
                            double maxX = double.MinValue, maxY = double.MinValue;
                            foreach (var word in line.Words)
                            {
                                var r = word.BoundingRect;
                                if (r.X < minX) minX = r.X;
                                if (r.Y < minY) minY = r.Y;
                                if (r.X + r.Width > maxX) maxX = r.X + r.Width;
                                if (r.Y + r.Height > maxY) maxY = r.Y + r.Height;
                            }
                            if (minX == double.MaxValue)
                            {
                                minX = minY = 0;
                                maxX = maxY = 0;
                            }

                            lines.Add(new OcrLine
                            {
                                Text = TextUtil.CleanOcr(text),
                                X = minX / scale,
                                Y = minY / scale,
                                Width = (maxX - minX) / scale,
                                Height = (maxY - minY) / scale,
                                Language = tag,
                            });
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        Log.Debug("OCR(" + tag + ") 失败：" + ex.Message);
                    }
                    return lines;
                }, ct));
            }

            try
            {
                var all = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var list in all) result.AddRange(list);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Debug("OCR 并行识别失败：" + ex.Message);
            }
            finally
            {
                sb.Dispose();
            }

            return result;
        }

        public void Dispose()
        {
            _disposed = true;
            _engines.Clear();
            _tags.Clear();
        }
    }
}
