using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;

namespace AyuTranslate.Core
{
    /// <summary>采样结果：一块文字所在的气泡背景色与合适的文字色。</summary>
    public struct SampledColors
    {
        public Color Background;
        public Color Foreground;

        /// <summary>采样是否可信（周围像素太杂时为 false，调用方应回退到默认样式）。</summary>
        public bool Reliable;

        public static SampledColors Default => new SampledColors
        {
            Background = Color.FromArgb(255, 255, 255, 255),
            Foreground = Color.FromArgb(255, 0, 0, 0),
            Reliable = false,
        };
    }

    /// <summary>
    /// 从截图里采样文字块周围的背景色与文字色。
    ///
    /// 目的：让覆盖层用「原文所在气泡的颜色」去盖住原文，
    /// 视觉上就像 Telegram 自带的原地翻译，而不是贴一块黑框。
    /// </summary>
    public static class ColorSampler
    {
        /// <summary>
        /// 采样一个文字块的背景色与文字色。
        /// </summary>
        /// <param name="bmp">聊天区域截图（坐标与 rect 同一坐标系）。</param>
        /// <param name="rect">文字块矩形。</param>
        /// <param name="inset">向内收缩的像素，避开气泡边界。</param>
        public static SampledColors Sample(Bitmap bmp, Rectangle rect, int inset = 2)
        {
            var result = SampledColors.Default;
            if (bmp == null) return result;

            var bounds = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var r = Rectangle.Intersect(rect, bounds);
            if (r.Width < 4 || r.Height < 4) return result;

            // 向内收缩，避免把气泡边框、相邻气泡混进来
            var inner = Rectangle.Inflate(r, -inset, -inset);
            inner = Rectangle.Intersect(inner, bounds);
            if (inner.Width < 2 || inner.Height < 2) inner = r;

            var points = new List<Color>(512);

            try
            {
                var data = bmp.LockBits(inner, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    int stepX = Math.Max(1, inner.Width / 48);
                    int stepY = Math.Max(1, inner.Height / 32);

                    unsafe
                    {
                        byte* basePtr = (byte*)data.Scan0;
                        for (int y = 0; y < inner.Height; y += stepY)
                        {
                            byte* row = basePtr + y * stride;
                            for (int x = 0; x < inner.Width; x += stepX)
                            {
                                int o = x * 4;
                                points.Add(Color.FromArgb(255, row[o + 2], row[o + 1], row[o]));
                            }
                        }
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch
            {
                return result;
            }

            if (points.Count == 0) return result;

            // ---- 背景色 = 出现最多的颜色（量化到 8 级，避免抗锯齿干扰）----
            var histogram = new Dictionary<int, int>();
            foreach (var c in points)
            {
                int key = Quantize(c);
                histogram.TryGetValue(key, out int n);
                histogram[key] = n + 1;
            }

            int bestKey = 0, bestCount = 0;
            foreach (var kv in histogram)
            {
                if (kv.Value > bestCount)
                {
                    bestCount = kv.Value;
                    bestKey = kv.Key;
                }
            }

            var background = Dequantize(bestKey);

            // 背景色占比太低 → 说明这块区域很花（渐变背景、图片），采样不可信
            double dominance = (double)bestCount / points.Count;
            if (dominance < 0.28) return result;

            // ---- 文字色：取与背景色差异最大的一小撮像素的平均值 ----
            var textPixels = points
                .Where(c => ColorDistance(c, background) > 90)
                .ToList();

            Color foreground;
            if (textPixels.Count >= 3 && textPixels.Count < points.Count * 0.6)
            {
                int ar = 0, ag = 0, ab = 0;
                foreach (var c in textPixels) { ar += c.R; ag += c.G; ab += c.B; }
                foreground = Color.FromArgb(255, ar / textPixels.Count, ag / textPixels.Count, ab / textPixels.Count);
            }
            else
            {
                // 没采到明显文字，就按背景亮度反推一个高对比色
                foreground = IsDark(background)
                    ? Color.FromArgb(255, 240, 240, 240)
                    : Color.FromArgb(255, 32, 32, 32);
            }

            // 保证对比度足够，否则译文会看不清
            if (ColorDistance(foreground, background) < 110)
            {
                foreground = IsDark(background)
                    ? Color.FromArgb(255, 245, 245, 245)
                    : Color.FromArgb(255, 20, 20, 20);
            }

            result.Background = background;
            result.Foreground = foreground;
            result.Reliable = true;
            return result;
        }

        /// <summary>把颜色量化成 8 级/通道，消掉抗锯齿带来的细微差异。</summary>
        private static int Quantize(Color c)
        {
            int r = (c.R >> 5) & 0x07;
            int g = (c.G >> 5) & 0x07;
            int b = (c.B >> 5) & 0x07;
            return (r << 6) | (g << 3) | b;
        }

        private static Color Dequantize(int key)
        {
            int r = ((key >> 6) & 0x07) * 36 + 18;
            int g = ((key >> 3) & 0x07) * 36 + 18;
            int b = (key & 0x07) * 36 + 18;
            return Color.FromArgb(255, Math.Min(255, r), Math.Min(255, g), Math.Min(255, b));
        }

        public static double ColorDistance(Color a, Color b)
        {
            int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
            // 加权欧氏距离，更贴近人眼感受
            return Math.Sqrt(dr * dr * 0.30 + dg * dg * 0.59 + db * db * 0.11);
        }

        public static bool IsDark(Color c)
        {
            double lum = (c.R * 0.299 + c.G * 0.587 + c.B * 0.114);
            return lum < 128;
        }

        /// <summary>按采样结果调整背景色（配置里的微调量）。</summary>
        public static Color AdjustBackground(Color c, int adjust)
        {
            if (adjust == 0) return c;
            int r = Math.Max(0, Math.Min(255, c.R + adjust));
            int g = Math.Max(0, Math.Min(255, c.G + adjust));
            int b = Math.Max(0, Math.Min(255, c.B + adjust));
            return Color.FromArgb(255, r, g, b);
        }
    }
}
