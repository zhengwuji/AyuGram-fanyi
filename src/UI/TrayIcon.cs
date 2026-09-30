using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using AyuTranslate.Core;

namespace AyuTranslate.UI
{
    /// <summary>系统托盘图标与菜单。</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly AppController _controller;
        private readonly OverlayWindow _overlay;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _autoItem;
        private readonly ToolStripMenuItem _overlayItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _providerItem;

        private SettingsHost _settings;

        public TrayIcon(AppController controller, OverlayWindow overlay)
        {
            _controller = controller;
            _overlay = overlay;
            _settings = new SettingsHost(controller, overlay);

            _menu = new ContextMenuStrip { ShowImageMargin = false };

            _statusItem = new ToolStripMenuItem("状态：启动中…") { Enabled = false };
            _menu.Items.Add(_statusItem);
            _menu.Items.Add(new ToolStripSeparator());

            _autoItem = new ToolStripMenuItem("自动翻译", null, (s, e) => _controller.ToggleAutoMode())
            {
                CheckOnClick = false,
            };
            _menu.Items.Add(_autoItem);

            _overlayItem = new ToolStripMenuItem("显示覆盖层", null, (s, e) => _controller.ToggleOverlay());
            _menu.Items.Add(_overlayItem);

            _menu.Items.Add(new ToolStripMenuItem("立即翻译一次 (热键)", null, (s, e) =>
            {
                _ = _controller.TranslateOnceAsync(manual: true);
            }));

            _menu.Items.Add(new ToolStripSeparator());

            _providerItem = new ToolStripMenuItem("当前接口：-") { Enabled = false };
            _menu.Items.Add(_providerItem);

            _menu.Items.Add(new ToolStripMenuItem("重新连接 AyuGram 窗口", null, (s, e) =>
            {
                if (_controller.LocateTarget(true)) Notify("AyuTranslate", "已连接目标窗口。");
                else Notify("AyuTranslate", "未找到 AyuGram 窗口。");
            }));

            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(new ToolStripMenuItem("设置…", null, (s, e) => ShowSettings()));
            _menu.Items.Add(new ToolStripMenuItem("打开日志", null, (s, e) => OpenPath(Log.FilePath)));
            _menu.Items.Add(new ToolStripMenuItem("打开配置", null, (s, e) => OpenPath(AppConfig.DefaultConfigPath)));
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("退出", null, (s, e) => ExitApp()));

            _menu.Opening += (s, e) => RefreshMenu();

            _icon = new NotifyIcon
            {
                Icon = BuildIcon(),
                Text = "AyuTranslate — 实时翻译覆盖层",
                Visible = true,
                ContextMenuStrip = _menu,
            };
            _icon.DoubleClick += (s, e) => ShowSettings();

            _controller.AutoModeChanged += on => RefreshMenu();
            _controller.TargetChanged += () => RefreshMenu();
            _controller.StatusChanged += msg => SetStatus(msg);
        }

        public void ShowStatus(string status)
        {
            SetStatus(status);
        }

        private void ShowSettings()
        {
            _settings.Show();
        }

        public void SetStatus(string status)
        {
            try
            {
                string shortText = TextUtil.Truncate(status ?? "", 100);
                _statusItem.Text = "状态：" + shortText;

                // NotifyIcon.Text 上限 63 字符
                _icon.Text = TextUtil.Truncate("AyuTranslate — " + (status ?? ""), 62);
            }
            catch { }
        }

        private void RefreshMenu()
        {
            try
            {
                bool auto = _controller.AutoMode;
                _autoItem.Text = auto ? "自动翻译：已开启 (热键 Ctrl+Alt+A)" : "自动翻译：已关闭 (热键 Ctrl+Alt+A)";
                _autoItem.Checked = auto;

                bool visible = _controller.OverlayVisible;
                _overlayItem.Text = visible ? "隐藏覆盖层 (热键 Ctrl+Alt+H)" : "显示覆盖层 (热键 Ctrl+Alt+H)";
                _overlayItem.Checked = visible;

                var tr = _controller.Pipeline?.Translator;
                string provider = tr?.Name ?? "-";
                _providerItem.Text = "当前接口：" + TextUtil.Truncate(provider, 60);

                var stats = _controller.Pipeline?.Stats;
                if (stats != null)
                {
                    _statusItem.Text = "状态：" + TextUtil.Truncate(stats.Summary, 100);
                }
            }
            catch { }
        }

        public void Notify(string title, string message)
        {
            try
            {
                _icon.BalloonTipTitle = title ?? "AyuTranslate";
                _icon.BalloonTipText = TextUtil.Truncate(message ?? "", 240);
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(3500);
            }
            catch { }
        }

        private static void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("打开路径失败：" + ex.Message);
            }
        }

        private void ExitApp()
        {
            try
            {
                _icon.Visible = false;
                _controller.Dispose();
                System.Windows.Application.Current?.Shutdown();
            }
            catch (Exception ex)
            {
                Log.Exception("退出失败", ex);
            }
        }

        /// <summary>生成一个简单的托盘图标（不依赖外部资源文件）。</summary>
        private static Icon BuildIcon()
        {
            try
            {
                // 优先用程序目录下的 icon.ico
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string ico = Path.Combine(exeDir, "icon.ico");
                if (File.Exists(ico))
                {
                    return new Icon(ico);
                }

                using (var bmp = new Bitmap(32, 32))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);

                        using (var bg = new SolidBrush(Color.FromArgb(255, 0x2A, 0x6E, 0xC8)))
                        {
                            g.FillEllipse(bg, 1, 1, 30, 30);
                        }

                        using (var pen = new Pen(Color.White, 2.4f))
                        {
                            g.DrawString("译", new Font("Microsoft YaHei UI", 14, FontStyle.Bold, GraphicsUnit.Pixel),
                                Brushes.White, new PointF(5, 6));
                        }
                    }

                    IntPtr h = bmp.GetHicon();
                    using (var tmp = Icon.FromHandle(h))
                    {
                        // 克隆一份，脱离 GDI 句柄生命周期
                        return (Icon)tmp.Clone();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("生成托盘图标失败：" + ex.Message);
                return SystemIcons.Application;
            }
        }

        public void Dispose()
        {
            try
            {
                _icon.Visible = false;
                _icon.Dispose();
                _menu.Dispose();
            }
            catch { }
        }
    }
}
