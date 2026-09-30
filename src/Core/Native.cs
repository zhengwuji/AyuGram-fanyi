using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AyuTranslate.Core
{
    /// <summary>user32 / gdi32 / dwmapi 原生互操作。</summary>
    public static class Native
    {
        // ---------- 窗口枚举 ----------
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLengthW(IntPtr hWnd);

        // ---------- 窗口几何 / 状态 ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width => Right - Left;
            public int Height => Bottom - Top;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        // ---------- 消息 ----------
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetMessageExtraInfo();

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        // ---------- 截图 ----------
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
            IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        // ---------- DWM ----------
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        /// <summary>DWMWA_CLOAKED</summary>
        public const int DWMWA_CLOAKED = 14;

        /// <summary>
        /// 窗口是否被 DWM 屏蔽（cloaked）。
        ///
        /// 被屏蔽时窗口虽然 IsWindowVisible=true，但它其实不在当前虚拟桌面上
        /// （或在「任务视图」里），此时 PrintWindow 会返回全白/全黑。
        /// 这种情况必须显式检测出来，否则会误判为「抓图失败」。
        /// </summary>
        public static bool IsWindowCloaked(IntPtr hWnd)
        {
            try
            {
                int cloaked = 0;
                int hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
                if (hr != 0) return false;
                return cloaked != 0;
            }
            catch
            {
                return false;
            }
        }

        // ---------- 常量 ----------
        public const int GWL_EXSTYLE = -20;
        public const int GWL_STYLE = -16;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const uint GW_OWNER = 4;
        public const uint GA_ROOT = 2;
        public const uint GA_ROOTOWNER = 3;

        public const uint PW_RENDERFULLCONTENT = 0x00000002;

        public const uint WM_GETOBJECT = 0x003D;
        public const uint WM_MOUSEWHEEL = 0x020A;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;

        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const uint SRCCOPY = 0x00CC0020;
        public const uint CAPTUREBLT = 0x40000000;

        public static int MakeLParam(int loWord, int hiWord)
        {
            return (hiWord << 16) | (loWord & 0xFFFF);
        }

        public static string GetClassName(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetWindowText(IntPtr hWnd)
        {
            int len = GetWindowTextLengthW(hWnd);
            var sb = new StringBuilder(Math.Max(len + 2, 512));
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static int GetExStyle(IntPtr hWnd) => GetWindowLongW(hWnd, GWL_EXSTYLE);

        public static void SetExStyle(IntPtr hWnd, int style) => SetWindowLongW(hWnd, GWL_EXSTYLE, style);

        /// <summary>把屏幕坐标转换成目标窗口的客户区坐标。</summary>
        public static POINT ScreenToWindowClient(IntPtr hWnd, int screenX, int screenY)
        {
            var p = new POINT { X = screenX, Y = screenY };
            ScreenToClient(hWnd, ref p);
            return p;
        }

        /// <summary>客户区原点在屏幕上的位置。</summary>
        public static POINT GetClientOrigin(IntPtr hWnd)
        {
            var p = new POINT { X = 0, Y = 0 };
            ClientToScreen(hWnd, ref p);
            return p;
        }

        /// <summary>枚举某个进程的全部顶层窗口。</summary>
        public static List<IntPtr> GetTopLevelWindowsOfProcess(int processId)
        {
            var result = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)processId) result.Add(h);
                return true;
            }, IntPtr.Zero);
            return result;
        }

        public static int GetWindowProcessId(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return (int)pid;
        }
    }
}
