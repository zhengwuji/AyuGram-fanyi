using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

namespace AyuTranslate.Core
{
    /// <summary>由若干 OCR 行聚合而成的一段文本（大致对应一条聊天消息）。</summary>
    public sealed class TextBlock
    {
        public List<OcrLine> Lines = new List<OcrLine>();
        public string Text = "";
        public double X;
        public double Y;
        public double Width;
        public double Height;
        /// <summary>文本整体偏右（自己发的消息）。</summary>
        public bool RightAligned;
        /// <summary>块的矩形（含少量内边距）。</summary>
        public RectangleF Bounds => new RectangleF((float)X, (float)Y, (float)Width, (float)Height);
        public string Key => Text;

        public override string ToString() =>
            $"[{X:F0},{Y:F0} {Width:F0}x{Height:F0}] {TextUtil.Truncate(Text.Replace("\n", " / "), 70)}";
    }

    /// <summary>把 OCR 行合并成消息块。</summary>
    public static class BlockDetector
    {
        /// <summary>一个块最多容纳多少行，防止把整屏内容粘成一块。</summary>
        private const int MaxLinesPerBlock = 12;

        public static List<TextBlock> GroupLines(IEnumerable<OcrLine> lines, AppConfig cfg)
        {
            var input = (lines ?? Enumerable.Empty<OcrLine>())
                .Where(l => l != null && !string.IsNullOrWhiteSpace(l.Text))
                .Where(l => l.Height >= cfg.MinLineHeight)
                .Where(l => !TextUtil.IsNoise(l.Text, cfg.MinTextLength))
                .OrderBy(l => l.Y)
                .ThenBy(l => l.X)
                .ToList();

            var blocks = new List<TextBlock>();
            var lastLineOf = new Dictionary<TextBlock, OcrLine>();
            var heightOf = new Dictionary<TextBlock, double>();

            foreach (var line in input)
            {
                TextBlock best = null;
                double bestScore = double.MaxValue;

                // 只与最后生成的若干块比较：符合从上到下的阅读顺序，同时避免 O(n^2)
                int start = Math.Max(0, blocks.Count - 10);
                for (int i = blocks.Count - 1; i >= start; i--)
                {
                    var b = blocks[i];
                    if (b.Lines.Count >= MaxLinesPerBlock) continue;

                    var last = lastLineOf[b];

                    // 必须是「这一行的上方」，且间距不超过允许值
                    double gap = line.Y - last.Bottom;
                    double allowed = MaxMergeGap(cfg, line.Height, last.Height);
                    if (gap > allowed) continue;
                    if (gap < -(Math.Min(line.Height, last.Height) * 0.6)) continue;

                    // 字号要接近，否则可能是图标/标题混排
                    double hMax = Math.Max(line.Height, last.Height);
                    if (hMax > 0 && Math.Abs(line.Height - last.Height) / hMax > 0.55) continue;

                    double overlap = HorizontalOverlapRatio(line, last);
                    if (overlap < cfg.LineMergeOverlap) continue;

                    // 间距越小、重叠越多，越可能是同一条消息
                    double score = gap * 2.0 - overlap * 12.0 + Math.Abs(line.Height - last.Height);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = b;
                    }
                }

                if (best != null)
                {
                    best.Lines.Add(line);
                    lastLineOf[best] = line;
                    Recompute(best);
                }
                else
                {
                    var nb = new TextBlock();
                    nb.Lines.Add(line);
                    lastLineOf[nb] = line;
                    Recompute(nb);
                    blocks.Add(nb);
                }
            }

            // 合并后再次过滤（合并后的文本可能才够长）
            blocks = blocks
                .Where(b => !TextUtil.IsNoise(b.Text, cfg.MinTextLength))
                .Where(b => !TextUtil.LooksLikeTimestampOrCounter(b.Text))
                .OrderBy(b => b.Y)
                .ToList();

            foreach (var b in blocks)
            {
                b.Lines.Sort((p, q) => p.Y.CompareTo(q.Y));
            }

            return blocks;
        }

        /// <summary>
        /// 允许合并的最大垂直间距：取配置值与「行高的一半」中的较大者。
        /// 这样同一条消息内的换行会被合并，而相邻消息（间距更大）不会。
        /// </summary>
        private static double MaxMergeGap(AppConfig cfg, double h1, double h2)
        {
            double adaptive = Math.Min(h1, h2) * 0.55;
            return Math.Max(cfg.LineMergeGap, adaptive);
        }

        private static double HorizontalOverlapRatio(OcrLine a, OcrLine b)
        {
            double overlap = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
            if (overlap <= 0) return 0;
            double denom = Math.Min(a.Width, b.Width);
            if (denom <= 0) return 0;
            return overlap / denom;
        }

        private static void Recompute(TextBlock b)
        {
            if (b.Lines.Count == 0) return;

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            foreach (var l in b.Lines)
            {
                if (l.X < minX) minX = l.X;
                if (l.Y < minY) minY = l.Y;
                if (l.Right > maxX) maxX = l.Right;
                if (l.Bottom > maxY) maxY = l.Bottom;
            }
            b.X = minX;
            b.Y = minY;
            b.Width = maxX - minX;
            b.Height = maxY - minY;

            var ordered = b.Lines.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
            var sb = new StringBuilder();
            OcrLine prev = null;
            foreach (var l in ordered)
            {
                if (prev != null)
                {
                    double gap = l.Y - prev.Bottom;
                    bool sameParagraph = gap <= Math.Min(l.Height, prev.Height) * 0.55;

                    if (!sameParagraph)
                    {
                        // 明显的段间距，保留换行
                        sb.Append('\n');
                    }
                    else
                    {
                        // 同一段内换行：CJK 之间不补空格，拉丁文之间补一个空格
                        char lastCh = sb.Length > 0 ? sb[sb.Length - 1] : '\0';
                        char firstCh = l.Text.Length > 0 ? l.Text[0] : '\0';
                        bool cjkJoin = TextUtil.IsCjk(lastCh) && TextUtil.IsCjk(firstCh);
                        if (!cjkJoin) sb.Append(' ');
                    }
                }
                sb.Append(l.Text);
                prev = l;
            }

            string text = sb.ToString();
            // 压缩 CJK 之间多余的空白，但保留换行
            text = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]{2,}", " ");
            b.Text = text.Trim();
        }

        /// <summary>
        /// 去掉互为重复的文本块。
        ///
        /// 程序会并行跑多个 OCR 引擎（例如 zh-Hans-CN + en-US），同一行文字
        /// 会被识别出多份略有差异的结果。这些重复块如果不去掉，
        /// 会把同一句话重复发给翻译接口，白花一倍费用。
        /// </summary>
        public static List<TextBlock> DeduplicateBlocks(List<TextBlock> blocks)
        {
            if (blocks == null) return new List<TextBlock>();
            if (blocks.Count <= 1) return blocks;

            var kept = new List<TextBlock>();

            // 质量高的先入队，这样重复项会被更准的那份吸收掉
            foreach (var b in blocks.OrderByDescending(x => TextQuality(x.Text)).ThenBy(x => x.Y))
            {
                bool duplicate = false;
                foreach (var k in kept)
                {
                    // 位置基本重合（同一行同一列）
                    if (Math.Abs(k.Y - b.Y) > Math.Max(4, b.Height * 0.5)) continue;
                    if (Math.Abs(k.X - b.X) > Math.Max(8, b.Width * 0.25)) continue;

                    if (TextUtil.Similarity(k.Text, b.Text) >= 0.80)
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate) kept.Add(b);
            }

            return kept.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
        }

        /// <summary>
        /// 粗略的文本质量打分，用于在重复块里挑一个更好的。
        ///
        /// 针对 OCR 的典型错误：把形近的小写字母认成大写
        /// （"of" → "Of"、"I'm" → "rm"）。因此拉丁文本里
        /// 小写多、大写少的版本通常更准。
        /// </summary>
        private static int TextQuality(string s)
        {
            if (string.IsNullOrEmpty(s)) return int.MinValue;

            int lower = 0, upper = 0, letters = 0, cjk = 0, digits = 0;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) letters++;
                if (char.IsLower(c)) lower++;
                if (char.IsUpper(c)) upper++;
                if (char.IsDigit(c)) digits++;
                if (TextUtil.IsCjk(c)) cjk++;
            }

            // 自然语言里大写字母占比很低，多出来的多半是误识别
            return (lower - upper) * 2 + letters + cjk * 3 + digits;
        }

        /// <summary>
        /// 自动推断聊天区域（排除左侧边栏与底部输入框）。
        /// 返回客户区坐标系下的矩形。
        /// </summary>
        public static Rectangle AutoDetectChatRegion(int clientWidth, int clientHeight, AppConfig cfg)
        {
            if (clientWidth < 200 || clientHeight < 200)
                return new Rectangle(0, 0, Math.Max(1, clientWidth), Math.Max(1, clientHeight));

            int leftInset;
            if (cfg.IncludeSidebar)
            {
                leftInset = 0;
            }
            else
            {
                // AyuGram / Telegram Desktop：图标栏 ~68px + 会话列表 ~272px
                // 宽窗口时列表会变宽，这里按比例估算并夹在合理区间
                double ratio = clientWidth >= 1200 ? 0.30 : 0.34;
                leftInset = (int)Math.Round(clientWidth * ratio);
                leftInset = Math.Max(72, Math.Min(leftInset, clientWidth / 2));
            }

            int rightInset = 0;
            int topInset = 0;
            int bottomInset = (int)Math.Round(Math.Min(110, clientHeight * 0.12));

            int w = clientWidth - leftInset - rightInset;
            int h = clientHeight - topInset - bottomInset;
            if (w < 120) { leftInset = 0; w = clientWidth; }
            if (h < 120) { bottomInset = 0; h = clientHeight; }

            return new Rectangle(leftInset, topInset, w, h);
        }

        /// <summary>
        /// 自动推断输入框区域（聊天区底部）。
        /// </summary>
        public static Rectangle AutoDetectInputRegion(int clientWidth, int clientHeight, AppConfig cfg)
        {
            var chat = AutoDetectChatRegion(clientWidth, clientHeight, cfg);
            int h = (int)Math.Round(Math.Min(72, clientHeight * 0.08));
            h = Math.Max(36, h);
            int y = clientHeight - h;
            return new Rectangle(chat.X, y, chat.Width, h);
        }

        /// <summary>按配置解析出实际使用的区域矩形。</summary>
        public static Rectangle ResolveRegion(RegionConfig rc, Rectangle auto, int clientWidth, int clientHeight)
        {
            if (rc != null && rc.IsUsable)
            {
                var r = new Rectangle(rc.X, rc.Y, rc.Width, rc.Height);
                r.Intersect(new Rectangle(0, 0, clientWidth, clientHeight));
                if (r.Width > 20 && r.Height > 20) return r;
            }
            return auto;
        }

        /// <summary>
        /// 用 OCR 结果二次修正聊天区域：把明显落在侧边栏的块裁掉。
        /// </summary>
        public static Rectangle RefineByBlocks(Rectangle region, List<TextBlock> blocks, int clientWidth)
        {
            if (blocks == null || blocks.Count < 4) return region;

            // 找最大的水平间隙：左边是会话列表，右边是聊天内容
            var ordered = blocks.OrderBy(b => b.X).ToList();
            double bestGap = 0;
            double splitX = region.Left;
            for (int i = 1; i < ordered.Count; i++)
            {
                double gapStart = ordered[i - 1].X + ordered[i - 1].Width;
                double gapEnd = ordered[i].X;
                double gap = gapEnd - gapStart;
                if (gap > bestGap && gapStart > clientWidth * 0.10 && gapEnd < clientWidth * 0.75)
                {
                    bestGap = gap;
                    splitX = gapEnd;
                }
            }

            if (bestGap >= 24)
            {
                int newLeft = (int)Math.Round(splitX) - 6;
                if (newLeft > region.Left && newLeft < region.Right - 100)
                {
                    return new Rectangle(newLeft, region.Top, region.Right - newLeft, region.Height);
                }
            }

            return region;
        }
    }
}
