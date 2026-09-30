using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AyuTranslate.Core;

namespace AyuTranslate.UI
{
    /// <summary>
    /// 覆盖在 AyuGram 聊天区上的透明置顶窗口。
    /// 默认鼠标穿透（点击会落到 AyuGram 上），不抢焦点、不显示在任务栏。
    /// </summary>
    public sealed class OverlayWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        public OverlayRenderer Renderer { get; }

        private IntPtr _hwnd;
        private bool _clickThrough = true;
        private bool _attached;
        private bool _userHidden;
        private TargetWindow _target;
        private AppConfig _cfg;

        public OverlayWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Width = 1;
            Height = 1;
            Left = -10000;
            Top = -10000;

            Renderer = new OverlayRenderer();
            Content = Renderer;

            SourceInitialized += OnSourceInitialized;
            Loaded += (s, e) => ApplyClickThrough();
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            _hwnd = helper.Handle;

            // 不抢焦点、不进 Alt+Tab、不显示在任务栏
            int ex = GetWindowLong32(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            SetWindowLong32(_hwnd, GWL_EXSTYLE, ex);

            ApplyClickThrough();
            Log.Debug("覆盖层窗口句柄 = 0x" + _hwnd.ToInt64().ToString("X"));
        }

        public IntPtr Handle => _hwnd;

        /// <summary>是否在界面线程上。</summary>
        private bool OnUiThread => Dispatcher == null || Dispatcher.CheckAccess();

        /// <summary>把调用切到界面线程。WPF 的 DependencyObject 有线程亲和性。</summary>
        private void RunOnUi(Action action)
        {
            if (action == null) return;
            if (OnUiThread)
            {
                action();
                return;
            }
            try
            {
                Dispatcher?.BeginInvoke(action);
            }
            catch (Exception ex)
            {
                Log.Debug("切换到界面线程失败：" + ex.Message);
            }
        }

        public void ApplyConfig(AppConfig cfg)
        {
            _cfg = cfg;
            Renderer.ApplyConfig(cfg);
            _clickThrough = cfg.OverlayClickThrough;
            ApplyClickThrough();
        }

        private void ApplyClickThrough()
        {
            if (_hwnd == IntPtr.Zero) return;
            try
            {
                int ex = GetWindowLong32(_hwnd, GWL_EXSTYLE);
                ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                if (_clickThrough) ex |= WS_EX_TRANSPARENT;
                else ex &= ~WS_EX_TRANSPARENT;
                SetWindowLong32(_hwnd, GWL_EXSTYLE, ex);
            }
            catch (Exception ex)
            {
                Log.Debug("设置鼠标穿透失败：" + ex.Message);
            }
        }

        /// <summary>把覆盖层对齐到目标窗口客户区。</summary>
        public void AttachTo(TargetWindow tw)
        {
            if (tw == null) return;
            _target = tw;
            _attached = true;
            RunOnUi(FollowTarget);
        }

        public void Detach()
        {
            _attached = false;
            _target = null;
            RunOnUi(HideNative);
        }

        /// <summary>
        /// 跟随目标窗口的位置与尺寸（窗口移动/缩放后调用）。
        /// 必须在界面线程执行：SetWindowPos 之外的 WPF 属性赋值有线程亲和性。
        /// </summary>
        public void FollowTarget()
        {
            if (!_attached || _target == null || _hwnd == IntPtr.Zero) return;

            if (!OnUiThread)
            {
                RunOnUi(FollowTarget);
                return;
            }

            if (!_target.IsWindowAlive())
            {
                _attached = false;
                _target = null;
                HideNative();
                return;
            }

            WindowLocator.Refresh(_target);
            if (!_target.IsUsable)
            {
                HideNative();
                return;
            }

            var origin = _target.ClientOrigin;
            int w = Math.Max(1, _target.ClientRect.Width);
            int h = Math.Max(1, _target.ClientRect.Height);

            // 用物理像素精确定位，保证与目标客户区像素级对齐
            bool moved = SetWindowPos(_hwnd, HWND_TOPMOST, origin.X, origin.Y, w, h,
                SWP_NOACTIVATE);

            if (!moved)
            {
                double scale = GetDpiScale();
                Left = origin.X / scale;
                Top = origin.Y / scale;
                Width = w / scale;
                Height = h / scale;
            }

            double s = GetDpiScale();
            Renderer.Width = w / s;
            Renderer.Height = h / s;
            UpdateLayout();
        }

        /// <summary>刷新覆盖内容（可在任意线程调用）。</summary>
        public void Update(List<OverlayItem> items)
        {
            if (!_attached) return;

            if (!OnUiThread)
            {
                var copy = items == null ? new List<OverlayItem>() : new List<OverlayItem>(items);
                RunOnUi(() => Update(copy));
                return;
            }

            FollowTarget();

            double scale = GetDpiScale();
            Renderer.UpdateItems(items, scale);
            Renderer.Opacity = _cfg?.OverlayOpacity ?? 1.0;

            if (_userHidden) return;
            if (_suspendedByUnfocus) return;

            if (!IsVisible)
            {
                try { Show(); }
                catch (Exception ex) { Log.Debug("显示覆盖层失败：" + ex.Message); }
            }

            // 保证仍然置顶（AyuGram 可能刚被激活）
            try
            {
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { }
        }

        public void Clear()
        {
            RunOnUi(() => Renderer.UpdateItems(new List<OverlayItem>(), GetDpiScale()));
        }

        /// <summary>供托盘/热键使用：隐藏时不取消绑定。</summary>
        public void HideOverlay()
        {
            _userHidden = true;
            RunOnUi(HideNative);
        }

        /// <summary>供托盘/热键使用：重新显示。</summary>
        public void ShowOverlay()
        {
            _userHidden = false;
            _suspendedByUnfocus = false;
            RunOnUi(() =>
            {
                if (!_attached || _target == null) return;

                FollowTarget();
                try
                {
                    if (!IsVisible) Show();
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch (Exception ex)
                {
                    Log.Debug("显示覆盖层失败：" + ex.Message);
                }
            });
        }

        /// <summary>目标窗口切到后台时临时收起（保留用户意图状态）。</summary>
        public void SuspendForUnfocus()
        {
            _suspendedByUnfocus = true;
            RunOnUi(HideNative);
        }

        /// <summary>目标窗口回到前台后恢复显示。</summary>
        public void ResumeFromUnfocus(List<OverlayItem> items)
        {
            if (!_suspendedByUnfocus) return;

            if (!OnUiThread)
            {
                var copy = items == null ? new List<OverlayItem>() : new List<OverlayItem>(items);
                RunOnUi(() => ResumeFromUnfocus(copy));
                return;
            }

            _suspendedByUnfocus = false;

            if (_userHidden || !_attached || _target == null) return;

            FollowTarget();
            double scale = GetDpiScale();
            Renderer.UpdateItems(items ?? new List<OverlayItem>(), scale);
            Renderer.Opacity = _cfg?.OverlayOpacity ?? 1.0;

            try
            {
                if (!IsVisible) Show();
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch (Exception ex)
            {
                Log.Debug("恢复覆盖层失败：" + ex.Message);
            }
        }

        /// <summary>是否因为目标窗口失去焦点而处于挂起状态。</summary>
        public bool SuspendedByUnfocus => _suspendedByUnfocus;

        private bool _suspendedByUnfocus;

        /// <summary>真正的可见性（隐藏时也返回用户意图状态）。</summary>
        public bool UserHidden => _userHidden;

        public bool OverlayVisible => IsVisible && !_userHidden && !_suspendedByUnfocus;

        private void HideNative()
        {
            try
            {
                if (IsVisible) Hide();
            }
            catch (Exception ex)
            {
                Log.Debug("隐藏覆盖层失败：" + ex.Message);
            }
        }

        /// <summary>安全读取可见性（跨线程直接读 IsVisible 不会抛异常，但这里保持统一入口）。</summary>
        private bool IsVisibleSafe
        {
            get
            {
                try { return IsVisible; }
                catch { return false; }
            }
        }

        private double GetDpiScale()
        {
            try
            {
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                    return src.CompositionTarget.TransformToDevice.M11;
            }
            catch { }
            return 1.0;
        }

        /// <summary>防止覆盖层被点击激活。</summary>
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
        }
    }
}
