using System;
using System.Windows;
using System.Windows.Threading;
using AyuTranslate.Core;

namespace AyuTranslate.UI
{
    /// <summary>
    /// 设置窗口的宿主：保证同一时间只有一个实例，
    /// 关闭时只隐藏（不销毁），下次打开即时响应。
    /// </summary>
    public sealed class SettingsHost
    {
        private readonly AppController _controller;
        private readonly OverlayWindow _overlay;
        private SettingsWindow _window;

        public SettingsHost(AppController controller, OverlayWindow overlay)
        {
            _controller = controller;
            _overlay = overlay;
        }

        public void Show()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Show));
                return;
            }

            try
            {
                if (_window == null)
                {
                    _window = new SettingsWindow(_controller);
                    _window.Closed += (s, e) =>
                    {
                        // 关闭后重建，保证下次打开读到最新配置
                        _window = null;
                    };
                }

                if (_window.WindowState == WindowState.Minimized)
                    _window.WindowState = WindowState.Normal;

                _window.Show();
                _window.Activate();
                _window.Topmost = true;
                _window.Topmost = false;
                _window.Focus();
            }
            catch (Exception ex)
            {
                Log.Exception("打开设置窗口失败", ex);
            }
        }
    }
}
