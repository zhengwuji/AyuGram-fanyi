using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AyuTranslate.Core;

namespace AyuTranslate.UI
{
    /// <summary>
    /// 覆盖层绘制器：把 OverlayItem 直接画到一个 WPF 元素上。
    /// 用手绘而不是控件树，几百个条目也不会卡。
    /// </summary>
    public sealed class OverlayRenderer : FrameworkElement
    {
        private List<OverlayItem> _items = new List<OverlayItem>();
        private double _dpiScale = 1.0;
        private AppConfig _cfg;

        private Brush _background;
        private Brush _foreground;
        private Brush _originalForeground;
        private Brush _failedBackground;
        private Brush _pendingBackground;
        private Typeface _typeface;
        private Pen _border;

        public bool DrawDebugBounds { get; set; }

        public OverlayRenderer()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
            RenderOptions.SetClearTypeHint(this, ClearTypeHint.Enabled);
        }

        public void ApplyConfig(AppConfig cfg)
        {
            _cfg = cfg;

            var bgColor = HotkeyUtil.ParseColor(cfg.OverlayBackground, Color.FromArgb(0xD9, 0, 0, 0));
            var fgColor = HotkeyUtil.ParseColor(cfg.OverlayForeground, Colors.White);
            var origColor = HotkeyUtil.ParseColor(cfg.OverlayOriginalForeground, Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));

            _background = Freeze(new SolidColorBrush(bgColor));
            _foreground = Freeze(new SolidColorBrush(fgColor));
            _originalForeground = Freeze(new SolidColorBrush(origColor));
            _failedBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0x6E, 0x1B, 0x1B)));
            _pendingBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xB3, 0x1B, 0x3A, 0x6E)));
            _border = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1.0));

            string family = string.IsNullOrWhiteSpace(cfg.OverlayFontFamily) ? "Microsoft YaHei UI" : cfg.OverlayFontFamily;
            _typeface = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

            InvalidateVisual();
        }

        private static T Freeze<T>(T f) where T : Freezable
        {
            if (f.CanFreeze) f.Freeze();
            return f;
        }

        public void UpdateItems(List<OverlayItem> items, double dpiScale)
        {
            _items = items ?? new List<OverlayItem>();
            _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;
            InvalidateVisual();
        }

        public int ItemCount => _items.Count;

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            if (_cfg == null || _items == null || _items.Count == 0) return;

            double scale = _dpiScale;
            double maxFontSize = _cfg.OverlayFontSize;
            double minFontSize = Math.Max(8.0, maxFontSize * 0.62);
            int maxLines = Math.Max(1, _cfg.OverlayMaxLines);
            int drawn = 0;

            // 按 y 排序，允许后画的盖住先画的
            foreach (var item in _items)
            {
                if (drawn >= Math.Max(1, _cfg.OverlayMaxItems)) break;

                // 物理像素 -> WPF DIP
                var r = item.Bounds;
                var rect = new Rect(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
                if (rect.Width < 8 || rect.Height < 6) continue;

                var bg = item.Failed ? _failedBackground : (item.Pending ? _pendingBackground : _background);
                dc.DrawRoundedRectangle(bg, DrawDebugBounds ? _border : null, rect, 3, 3);

                double paddingX = 4;
                double paddingY = _cfg.OverlayPaddingY / scale;
                double availWidth = Math.Max(4, rect.Width - paddingX * 2);
                double availHeight = Math.Max(4, rect.Height - paddingY * 2);

                string text = item.Translation ?? "";
                if (item.Failed && string.IsNullOrWhiteSpace(text)) text = "⚠ 翻译失败";

                var ft = BuildText(text, availWidth, maxFontSize, ref maxLines);

                // 自适应缩小字号，保证尽量装下
                int guard = 0;
                while (ft.Height > availHeight && maxFontSize > minFontSize && guard++ < 8)
                {
                    maxFontSize = Math.Max(minFontSize, maxFontSize - 1.0);
                    ft = BuildText(text, availWidth, maxFontSize, ref maxLines);
                }

                // 仍然装不下就裁剪 + 省略号
                bool clipped = ft.Height > availHeight + 0.5;
                if (clipped)
                {
                    text = FitWithEllipsis(text, availWidth, maxFontSize, availHeight);
                    ft = BuildText(text, availWidth, maxFontSize, ref maxLines);
                }

                dc.PushClip(new RectangleGeometry(new Rect(
                    rect.X + paddingX - 2, rect.Y + paddingY - 1,
                    availWidth + 4, Math.Max(availHeight, ft.Height) + 2)));

                double textY = rect.Y + paddingY;
                if (ft.Height < availHeight)
                {
                    // 垂直居中对齐原文
                    textY = rect.Y + (rect.Height - ft.Height) / 2.0;
                }
                dc.DrawText(ft, new Point(rect.X + paddingX, textY));

                dc.Pop();

                if (_cfg.ShowOriginalText && !string.IsNullOrWhiteSpace(item.Original) && item.Original != item.Translation)
                {
                    var orig = new FormattedText(
                        TextUtil.Truncate(item.Original.Replace("\n", " ⏎ "), 60),
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        _typeface,
                        Math.Max(7, maxFontSize * 0.7),
                        _originalForeground,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    orig.MaxTextWidth = availWidth;
                    orig.MaxLineCount = 1;
                    orig.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(orig, new Point(rect.X + paddingX, rect.Bottom - orig.Height - 1));
                }

                drawn++;
            }
        }

        private FormattedText BuildText(string text, double maxWidth, double fontSize, ref int maxLines)
        {
            var ft = new FormattedText(
                text ?? "",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                _typeface,
                Math.Max(6, fontSize),
                _foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            ft.MaxTextWidth = Math.Max(4, maxWidth);
            if (maxLines > 0)
            {
                ft.MaxLineCount = maxLines;
                ft.Trimming = TextTrimming.CharacterEllipsis;
            }
            ft.LineHeight = Math.Max(6, fontSize) * 1.28;
            return ft;
        }

        /// <summary>逐字符收缩，直到内容高度能装进可用高度。</summary>
        private string FitWithEllipsis(string text, double width, double fontSize, double maxHeight)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var ft = BuildText(text, width, fontSize, ref _unusedLines);
            if (ft.Height <= maxHeight) return text;

            int lo = 0, hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                string candidate = text.Substring(0, mid) + "…";
                var probe = BuildText(candidate, width, fontSize, ref _unusedLines);
                if (probe.Height <= maxHeight) lo = mid;
                else hi = mid - 1;
            }

            if (lo <= 0) return "…";
            return text.Substring(0, lo).TrimEnd() + "…";
        }

        private int _unusedLines = 0;
    }
}
