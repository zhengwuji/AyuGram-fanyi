using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using AyuTranslate.Core;

namespace AyuTranslate
{
    /// <summary>
    /// 用 GDI+ 把覆盖条目合成到背景图上，复刻真实覆盖层的视觉效果。
    /// 主要给自检用：即使目标窗口被别的窗口挡住，也能看到最终观感。
    /// </summary>
    internal static class Compositor
    {
        public static void Render(Bitmap background, List<OverlayItem> items, AppConfig cfg, string outPath)
        {
            if (background == null) return;
            items = items ?? new List<OverlayItem>();

            using (var canvas = new Bitmap(background.Width, background.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.DrawImageUnscaled(background, 0, 0);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                    var bgColor = HotkeyUtil.ParseColor(cfg.OverlayBackground, System.Windows.Media.Color.FromArgb(0xD9, 0, 0, 0));
                    var fgColor = HotkeyUtil.ParseColor(cfg.OverlayForeground, System.Windows.Media.Colors.White);
                    var bg = Color.FromArgb(bgColor.A, bgColor.R, bgColor.G, bgColor.B);
                    var fg = Color.FromArgb(fgColor.A, fgColor.R, fgColor.G, fgColor.B);

                    float fontSize = (float)Math.Max(8, cfg.OverlayFontSize);
                    using (var font = new Font(
                        string.IsNullOrWhiteSpace(cfg.OverlayFontFamily) ? "Microsoft YaHei UI" : cfg.OverlayFontFamily,
                        fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var bgBrush = new SolidBrush(bg))
                    using (var fgBrush = new SolidBrush(fg))
                    using (var border = new Pen(Color.FromArgb(70, 0x66, 0xCC, 0xFF), 1))
                    {
                        foreach (var item in items.Take(Math.Max(1, cfg.OverlayMaxItems)))
                        {
                            var r = item.Bounds;
                            r.Intersect(new Rectangle(0, 0, canvas.Width, canvas.Height));
                            if (r.Width < 8 || r.Height < 6) continue;

                            g.FillRectangle(bgBrush, r);

                            var textRect = new RectangleF(
                                r.X + 4, r.Y + 1,
                                Math.Max(8, r.Width - 8),
                                Math.Max(8, r.Height - 2));

                            using (var sf = new StringFormat
                            {
                                Trimming = StringTrimming.EllipsisCharacter,
                                FormatFlags = StringFormatFlags.LineLimit,
                            })
                            {
                                g.DrawString(item.Translation ?? "", font, fgBrush, textRect, sf);
                            }

                            g.DrawRectangle(border, r);
                        }
                    }
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                canvas.Save(outPath, ImageFormat.Png);
            }
        }
    }
}
