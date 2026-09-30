using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using AyuTranslate.Core;
using AyuTranslate.UI;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace AyuTranslate
{
    /// <summary>程序入口：单实例 + 托盘 + 覆盖层 + 设置窗口。</summary>
    public sealed class Program
    {
        private static Mutex _singleInstance;

        [STAThread]
        public static void Main(string[] args)
        {
            // 无界面自检模式：AyuTranslate.exe --selftest [--provider offline] [--preview out.png]
            // 无界面模式：任何一个自检开关都进入命令行分支，不启动 GUI。
            if (args != null && SelfTest.HasAnyModeFlag(args))
            {
                Environment.Exit(SelfTest.Run(args));
                return;
            }

            // 只有 --help 之类时给出用法，避免误启动
            if (args != null && args.Any(a =>
                    a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/?", StringComparison.OrdinalIgnoreCase)))
            {
                Environment.Exit(SelfTest.Run(new[] { "--help" }));
                return;
            }

            bool createdNew;
            _singleInstance = new Mutex(true, @"Global\AyuTranslate_SingleInstance_9E2C1A", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("AyuTranslate 已经在运行了（请查看系统托盘图标）。",
                    "AyuTranslate", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var app = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Exception("未处理的界面异常", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Exception("未处理的异常", e.ExceptionObject as Exception ?? new Exception("unknown"));
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Log.Exception("未观察的任务异常", e.Exception);
                e.SetObserved();
            };

            var cfg = AppConfig.Load();
            Log.Init(cfg.LogDirectory, cfg.DebugLog);
            Log.Info("=== AyuTranslate 启动 ===");
            Log.Info("配置文件：" + AppConfig.DefaultConfigPath);

            var overlay = new OverlayWindow();
            overlay.ApplyConfig(cfg);

            var controller = new AppController(app.Dispatcher, overlay, cfg);
            var tray = new TrayIcon(controller, overlay);
            var settingsHost = new SettingsHost(controller, overlay);

            controller.SettingsRequested += () => settingsHost.Show();
            controller.StatusChanged += msg => tray.SetStatus(msg);
            controller.ErrorRaised += msg => tray.Notify("AyuTranslate", msg);

            // 首帧窗口必须创建出来，否则注册热键时拿不到句柄
            overlay.Show();
            overlay.Hide();
            controller.Start();

            tray.ShowStatus(controller.Status());
            controller.SetAutoMode(true);

            app.Run();

            controller.Dispose();
            tray.Dispose();
            _singleInstance.Dispose();
        }
    }
}
