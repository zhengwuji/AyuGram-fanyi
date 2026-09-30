using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace AyuTranslate.Core
{
    /// <summary>目标窗口信息快照。</summary>
    public sealed class TargetWindow
    {
        public IntPtr Handle;
        public int ProcessId;
        public string ClassName = "";
        public string Title = "";
        public string ProcessPath = "";
        public Native.RECT WindowRect;
        public Native.RECT ClientRect;
        /// <summary>客户区左上角在屏幕上的坐标。</summary>
        public Native.POINT ClientOrigin;
        public bool IsForeground;
        public bool IsMinimized;
        public bool IsVisible;
        public bool IsDisabled;
        public bool HasThickFrame;
        public bool HasCaption;
        /// <summary>被 DWM 屏蔽：不在当前虚拟桌面上，抓图会得到空白。</summary>
        public bool IsCloaked;
        public uint Dpi = 96;

        public int ClientWidth => ClientRect.Width;
        public int ClientHeight => ClientRect.Height;

        /// <summary>客户区宽度 / 实际渲染宽度 的比例（DPI 缩放）。</summary>
        public double DpiScale => Dpi <= 0 ? 1.0 : Dpi / 96.0;

        /// <summary>
        /// 窗口是否可抓图。
        /// 注意 cloaked 的窗口 IsWindowVisible 仍为 true，但抓图必然空白，
        /// 所以这里要排除掉。
        /// </summary>
        public bool IsUsable => Handle != IntPtr.Zero && IsWindowAlive()
                                && !IsMinimized && IsVisible && !IsCloaked;

        /// <summary>如果不可用，给出人类可读的原因。</summary>
        public string UnusableReason()
        {
            if (Handle == IntPtr.Zero) return "窗口句柄无效";
            if (!IsWindowAlive()) return "窗口已关闭";
            if (IsMinimized) return "窗口已最小化";
            if (IsCloaked) return "窗口不在当前虚拟桌面上（被系统屏蔽），请切换到 AyuGram 所在的桌面";
            if (!IsVisible) return "窗口处于隐藏状态，请先显示 AyuGram";
            return "";
        }

        public bool IsWindowAlive() => Native.IsWindow(Handle);

        public override string ToString() =>
            $"pid={ProcessId} hwnd=0x{Handle.ToInt64():X} client={ClientWidth}x{ClientHeight} title=\"{Title}\"";
    }

    /// <summary>定位 AyuGram（或任意 Telegram Desktop 系）窗口。</summary>
    public static class WindowLocator
    {
        /// <summary>已知的 Telegram Desktop 系窗口类名（Qt5/Qt6 版本号会变，用前缀匹配）。</summary>
        private static readonly string[] ClassPrefixes = { "Qt5", "Qt6" };

        /// <summary>这些窗口类不是聊天主窗口，必须排除。</summary>
        private static readonly string[] ExcludedClassParts =
        {
            "WindowShadow", "TrayIcon", "IME", "Hook", "SoPY_", "Sogou", "MSCTF",
        };

        /// <summary>这些标题是辅助窗口（设置、媒体查看器等），不是聊天主窗口。</summary>
        private static readonly string[] ExcludedTitles =
        {
            "AyuGramDesktop", "媒体查看器", "Media viewer", "Media Viewer", "TelegramDesktop",
        };

        private const int GWL_STYLE = -16;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_DISABLED = 0x08000000;

        public static TargetWindow Find(AppConfig cfg)
        {
            var candidates = new List<TargetWindow>();

            // 0) 配置里显式指定了窗口句柄就直接用
            if (cfg.TargetWindowHandle != 0)
            {
                var pinned = Describe(new IntPtr(cfg.TargetWindowHandle), 0);
                if (pinned != null && pinned.IsWindowAlive())
                {
                    pinned.ProcessId = Native.GetWindowProcessId(pinned.Handle);
                    Log.Info("使用配置中固定的窗口句柄：" + pinned);
                    return pinned;
                }
                Log.Warn("配置中固定的窗口句柄已失效，回退到自动查找。");
            }

            // 1) 优先按可执行文件路径找进程
            var pids = new HashSet<int>();
            try
            {
                if (!string.IsNullOrWhiteSpace(cfg.TargetExecutable))
                {
                    string exeName = Path.GetFileNameWithoutExtension(cfg.TargetExecutable);
                    if (!string.IsNullOrWhiteSpace(exeName))
                    {
                        foreach (var p in Process.GetProcessesByName(exeName))
                        {
                            pids.Add(p.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("按进程名查找失败：" + ex.Message);
            }

            // 2) 找不到就退回到全部进程里按窗口类名/标题猜
            if (pids.Count == 0)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        string name = p.ProcessName;
                        if (name.IndexOf("AyuGram", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("Telegram", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            pids.Add(p.Id);
                        }
                    }
                    catch { }
                }
            }

            foreach (int pid in pids)
            {
                foreach (var h in Native.GetTopLevelWindowsOfProcess(pid))
                {
                    var tw = Describe(h, pid);
                    if (tw == null) continue;
                    if (IsPlausibleMainWindow(tw, cfg)) candidates.Add(tw);
                }
            }

            if (candidates.Count == 0) return null;

            // 选择策略：
            //   可见且未最小化 > 有 WS_THICKFRAME（主窗口可缩放） > 客户区面积大
            // 媒体查看器/设置弹窗没有 WS_THICKFRAME，因此不会被误选。
            var ordered = candidates
                .OrderByDescending(c => c.IsVisible && !c.IsMinimized)
                .ThenByDescending(c => c.HasThickFrame)
                .ThenByDescending(c => (long)c.ClientWidth * c.ClientHeight)
                .ToList();

            if (!cfg.AutoPickLargestWindow)
            {
                var visible = ordered.FirstOrDefault(c => c.IsVisible);
                return visible ?? ordered[0];
            }

            return ordered[0];
        }

        private static bool IsPlausibleMainWindow(TargetWindow tw, AppConfig cfg)
        {
            if (tw.ClientWidth < 200 || tw.ClientHeight < 200) return false;
            if (tw.IsDisabled) return false;

            string cls = tw.ClassName ?? "";
            foreach (var bad in ExcludedClassParts)
            {
                if (cls.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            bool classOk = ClassPrefixes.Any(p => cls.StartsWith(p, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(cfg.TargetWindowClassPrefix))
            {
                classOk = cls.StartsWith(cfg.TargetWindowClassPrefix, StringComparison.OrdinalIgnoreCase)
                          || ClassPrefixes.Any(p => cls.StartsWith(p, StringComparison.Ordinal));
            }
            if (!classOk) return false;

            // 排除媒体查看器、设置对话框等辅助窗口
            foreach (var bad in ExcludedTitles)
            {
                if (string.Equals(tw.Title, bad, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        private static TargetWindow Describe(IntPtr h, int pid)
        {
            try
            {
                if (!Native.IsWindow(h)) return null;

                if (pid == 0) pid = Native.GetWindowProcessId(h);

                int style = Native.GetWindowLongW(h, GWL_STYLE);

                var tw = new TargetWindow
                {
                    Handle = h,
                    ProcessId = pid,
                    ClassName = Native.GetClassName(h),
                    Title = Native.GetWindowText(h),
                    IsVisible = Native.IsWindowVisible(h),
                    IsMinimized = Native.IsIconic(h),
                    IsForeground = Native.GetForegroundWindow() == h,
                    HasThickFrame = (style & WS_THICKFRAME) != 0,
                    HasCaption = (style & WS_CAPTION) == WS_CAPTION,
                    IsDisabled = (style & WS_DISABLED) != 0,
                    IsCloaked = Native.IsWindowCloaked(h),
                };

                Native.GetWindowRect(h, out var wr);
                tw.WindowRect = wr;
                Native.GetClientRect(h, out var cr);
                tw.ClientRect = cr;
                tw.ClientOrigin = Native.GetClientOrigin(h);
                try { tw.Dpi = Native.GetDpiForWindow(h); } catch { tw.Dpi = 96; }
                if (tw.Dpi == 0) tw.Dpi = 96;

                try
                {
                    tw.ProcessPath = Process.GetProcessById(pid).MainModule?.FileName ?? "";
                }
                catch { tw.ProcessPath = ""; }

                return tw;
            }
            catch (Exception ex)
            {
                Log.Debug("Describe 窗口失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>重新读取窗口几何（窗口移动/缩放后调用）。</summary>
        public static void Refresh(TargetWindow tw)
        {
            if (tw == null || !tw.IsWindowAlive()) return;
            Native.GetWindowRect(tw.Handle, out var wr);
            tw.WindowRect = wr;
            Native.GetClientRect(tw.Handle, out var cr);
            tw.ClientRect = cr;
            tw.ClientOrigin = Native.GetClientOrigin(tw.Handle);
            tw.IsVisible = Native.IsWindowVisible(tw.Handle);
            tw.IsMinimized = Native.IsIconic(tw.Handle);
            tw.IsForeground = Native.GetForegroundWindow() == tw.Handle;
            tw.IsCloaked = Native.IsWindowCloaked(tw.Handle);
            try
            {
                tw.Dpi = Native.GetDpiForWindow(tw.Handle);
                if (tw.Dpi == 0) tw.Dpi = 96;
            }
            catch { }
        }

        /// <summary>判断某个屏幕点是否落在目标窗口上（用来判断焦点/悬停）。</summary>
        public static bool IsScreenPointOnWindow(TargetWindow tw, int x, int y)
        {
            if (tw == null) return false;
            var wr = tw.WindowRect;
            return x >= wr.Left && x < wr.Right && y >= wr.Top && y < wr.Bottom;
        }

        /// <summary>目标窗口是否是当前前台窗口（或其子窗口）。</summary>
        public static bool IsForeground(TargetWindow tw)
        {
            if (tw == null || !tw.IsWindowAlive()) return false;
            var fg = Native.GetForegroundWindow();
            if (fg == tw.Handle) return true;
            var root = Native.GetAncestor(fg, Native.GA_ROOT);
            if (root == tw.Handle) return true;
            // 弹出菜单 / 输入法窗口也属于同一进程
            return Native.GetWindowProcessId(fg) == tw.ProcessId;
        }
    }
}
