using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AyuTranslate.Core
{
    public enum CaptureBackend
    {
        /// <summary>PrintWindow + PW_RENDERFULLCONTENT，不要求窗口在最前面。</summary>
        PrintWindow = 0,

        /// <summary>从屏幕 DC BitBlt。要求窗口可见且未被遮挡；不采集分层窗口（即不采集本程序覆盖层）。</summary>
        ScreenBitBlt = 1,
    }

    /// <summary>目标窗口位图抓取。</summary>
    public static class ScreenCapture
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        private static readonly object Gate = new object();

        /// <summary>
        /// 抓取目标窗口客户区。
        /// </summary>
        /// <param name="tw">目标窗口。</param>
        /// <param name="backend">抓取方式。</param>
        /// <param name="clientOnly">true 表示只抓客户区；false 抓整窗口。</param>
        public static Bitmap Capture(TargetWindow tw, CaptureBackend backend = CaptureBackend.PrintWindow, bool clientOnly = false)
        {
            if (tw == null || !tw.IsWindowAlive()) return null;

            if (backend == CaptureBackend.PrintWindow)
                return CapturePrintWindow(tw, clientOnly);

            return CaptureScreen(tw, clientOnly);
        }

        /// <summary>PrintWindow 方式：窗口被遮挡、在后台也能拿到内容。</summary>
        public static Bitmap CapturePrintWindow(TargetWindow tw, bool clientOnly)
        {
            int w, h;

            if (clientOnly)
            {
                w = tw.ClientRect.Width;
                h = tw.ClientRect.Height;
            }
            else
            {
                w = tw.WindowRect.Width;
                h = tw.WindowRect.Height;
            }

            if (w <= 0 || h <= 0) return null;

            lock (Gate)
            {
                IntPtr hdcScreen = IntPtr.Zero;
                IntPtr hdcMem = IntPtr.Zero;
                IntPtr hBmp = IntPtr.Zero;
                IntPtr hOld = IntPtr.Zero;
                try
                {
                    hdcScreen = Native.GetDC(IntPtr.Zero);
                    if (hdcScreen == IntPtr.Zero) return null;

                    hdcMem = CreateCompatibleDC(hdcScreen);
                    if (hdcMem == IntPtr.Zero) return null;

                    hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
                    if (hBmp == IntPtr.Zero) return null;

                    hOld = SelectObject(hdcMem, hBmp);

                    bool ok = Native.PrintWindow(tw.Handle, hdcMem, Native.PW_RENDERFULLCONTENT);
                    if (!ok)
                        ok = Native.PrintWindow(tw.Handle, hdcMem, 0);
                    if (!ok) return null;

                    // FromHbitmap 得到的是 32bppRgb（alpha 位无效），必须转成 Argb，
                    // 否则下游按 Argb 读出的颜色 alpha=0。转换与裁剪合并成一次 DrawImage，
                    // 避免先整窗拷贝一遍再裁剪拷贝一遍。
                    var raw = Image.FromHbitmap(hBmp);
                    Bitmap result;
                    try
                    {
                        Rectangle srcRect;
                        if (clientOnly)
                        {
                            // PrintWindow 画的是整个窗口，裁到客户区
                            int dx = tw.ClientOrigin.X - tw.WindowRect.Left;
                            int dy = tw.ClientOrigin.Y - tw.WindowRect.Top;
                            srcRect = new Rectangle(dx, dy, w, h);
                            srcRect.Intersect(new Rectangle(0, 0, raw.Width, raw.Height));
                            if (srcRect.Width <= 0 || srcRect.Height <= 0)
                                srcRect = new Rectangle(0, 0, raw.Width, raw.Height);
                        }
                        else
                        {
                            srcRect = new Rectangle(0, 0, raw.Width, raw.Height);
                        }

                        result = new Bitmap(srcRect.Width, srcRect.Height, PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(result))
                        {
                            g.DrawImage(raw, new Rectangle(0, 0, srcRect.Width, srcRect.Height),
                                srcRect, GraphicsUnit.Pixel);
                        }
                    }
                    finally
                    {
                        raw.Dispose();
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    Log.Debug("CapturePrintWindow 失败：" + ex.Message);
                    return null;
                }
                finally
                {
                    if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                    if (hdcScreen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, hdcScreen);
                }
            }
        }

        /// <summary>从屏幕抓取窗口区域（不使用 CAPTUREBLT，因此不会抓到本程序的分层覆盖层）。</summary>
        public static Bitmap CaptureScreen(TargetWindow tw, bool clientOnly)
        {
            int x, y, w, h;
            if (clientOnly)
            {
                x = tw.ClientOrigin.X;
                y = tw.ClientOrigin.Y;
                w = tw.ClientRect.Width;
                h = tw.ClientRect.Height;
            }
            else
            {
                x = tw.WindowRect.Left;
                y = tw.WindowRect.Top;
                w = tw.WindowRect.Width;
                h = tw.WindowRect.Height;
            }
            return CaptureScreenRect(x, y, w, h);
        }

        /// <summary>抓取任意屏幕矩形。</summary>
        public static Bitmap CaptureScreenRect(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;

            lock (Gate)
            {
                IntPtr hdcScreen = IntPtr.Zero;
                IntPtr hdcMem = IntPtr.Zero;
                IntPtr hBmp = IntPtr.Zero;
                IntPtr hOld = IntPtr.Zero;
                try
                {
                    hdcScreen = Native.GetDC(IntPtr.Zero);
                    if (hdcScreen == IntPtr.Zero) return null;

                    hdcMem = CreateCompatibleDC(hdcScreen);
                    hBmp = CreateCompatibleBitmap(hdcScreen, w, h);
                    if (hdcMem == IntPtr.Zero || hBmp == IntPtr.Zero) return null;

                    hOld = SelectObject(hdcMem, hBmp);
                    bool ok = Native.BitBlt(hdcMem, 0, 0, w, h, hdcScreen, x, y, Native.SRCCOPY);
                    if (!ok) return null;

                    var bmp = Image.FromHbitmap(hBmp);
                    var result = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(result))
                    {
                        g.DrawImageUnscaled(bmp, 0, 0);
                    }
                    bmp.Dispose();
                    return result;
                }
                catch (Exception ex)
                {
                    Log.Debug("CaptureScreenRect 失败：" + ex.Message);
                    return null;
                }
                finally
                {
                    if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) SelectObject(hdcMem, hOld);
                    if (hBmp != IntPtr.Zero) DeleteObject(hBmp);
                    if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
                    if (hdcScreen != IntPtr.Zero) Native.ReleaseDC(IntPtr.Zero, hdcScreen);
                }
            }
        }

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        public static Bitmap Crop(Bitmap src, int x, int y, int w, int h)
        {
            if (src == null) return null;
            var rect = new Rectangle(x, y, w, h);
            var bounds = new Rectangle(0, 0, src.Width, src.Height);
            rect.Intersect(bounds);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                rect = bounds;
            }
            var dst = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.DrawImage(src, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
            }
            return dst;
        }

        /// <summary>
        /// 对比两次抓取的差异比例（0-1），用来判断内容是否变化。
        /// </summary>
        public static double DiffRatio(Bitmap a, Bitmap b, int step = 6)
        {
            if (a == null || b == null) return 1.0;
            if (a.Width != b.Width || a.Height != b.Height) return 1.0;

            var da = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var db = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                long total = 0, diff = 0;
                unsafe
                {
                    byte* pa = (byte*)da.Scan0;
                    byte* pb = (byte*)db.Scan0;
                    int strideA = da.Stride;
                    int strideB = db.Stride;

                    for (int y = 0; y < a.Height; y += step)
                    {
                        byte* ra = pa + y * strideA;
                        byte* rb = pb + y * strideB;
                        for (int x = 0; x < a.Width; x += step)
                        {
                            int off = x * 4;
                            int d = Math.Abs(ra[off] - rb[off]) + Math.Abs(ra[off + 1] - rb[off + 1]) + Math.Abs(ra[off + 2] - rb[off + 2]);
                            total++;
                            if (d > 36) diff++;
                        }
                    }
                }
                if (total == 0) return 0;
                return (double)diff / total;
            }
            finally
            {
                a.UnlockBits(da);
                b.UnlockBits(db);
            }
        }
    }
}
