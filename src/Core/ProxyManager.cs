using System;
using System.Collections.Generic;
using System.Linq;
using AyuTranslate.Proxy;

namespace AyuTranslate.Core
{
    /// <summary>
    /// 管理「原生翻译接管」：本地代理的生命周期 + 目标程序补丁状态。
    ///
    /// 用法（推荐路径）：
    ///   1. 给 AyuGram.exe 打补丁，把内置 Google 翻译的接口地址指向本代理
    ///   2. 在 AyuGram 里把翻译服务选成 Google
    ///   3. 启动代理
    /// 之后 AyuGram 的翻译就是原生的界面 + 你自己的 AI。
    /// </summary>
    public sealed class ProxyManager : IDisposable
    {
        private static readonly Lazy<ProxyManager> Lazy =
            new Lazy<ProxyManager>(() => new ProxyManager());

        public static ProxyManager Instance => Lazy.Value;

        private TranslateProxyServer _server;
        private AppConfig _cfg;
        private readonly List<string> _recentLog = new List<string>();

        private ProxyManager() { }

        public bool IsRunning => _server != null && _server.IsRunning;
        public int Port => _server?.Port ?? (_cfg?.ProxyPort ?? BinaryPatcher.DefaultPort);

        public TranslateProxyServer Server => _server;

        public long RequestCount => _server?.RequestCount ?? 0;
        public long SuccessCount => _server?.SuccessCount ?? 0;
        public long FailCount => _server?.FailCount ?? 0;
        public string LastError => _server?.LastError ?? "";

        public IReadOnlyList<string> RecentLog => _recentLog;

        /// <summary>最近几次翻译的日志（最新的在前）。</summary>
        public void ClearLog() => _recentLog.Clear();

        public bool Start(AppConfig cfg)
        {
            if (IsRunning) return true;
            if (cfg == null) return false;

            _cfg = cfg;

            try
            {
                _server = new TranslateProxyServer(cfg, cfg.ProxyPort);
                _server.RequestLogged += OnRequestLogged;
                _server.Start();
                Log.Info("原生翻译代理已启动，端口 " + cfg.ProxyPort);
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("启动翻译代理失败", ex);
                _server = null;
                return false;
            }
        }

        private void OnRequestLogged(string line)
        {
            lock (_recentLog)
            {
                _recentLog.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                while (_recentLog.Count > 50) _recentLog.RemoveAt(_recentLog.Count - 1);
            }
        }

        /// <summary>配置热更新后把新配置同步给运行中的代理（模型 / 密钥变更即时生效）。</summary>
        public void UpdateConfig(AppConfig cfg)
        {
            if (cfg == null || _server == null) return;
            try { _server.UpdateConfig(cfg); } catch (Exception ex)
            {
                Log.Debug("代理刷新配置失败：" + ex.Message);
            }
        }

        public void Stop()
        {
            if (_server == null) return;
            try
            {
                _server.RequestLogged -= OnRequestLogged;
                _server.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug("停止代理失败：" + ex.Message);
            }
            _server = null;
            Log.Info("原生翻译代理已停止。");
        }

        public void Restart(AppConfig cfg)
        {
            Stop();
            Start(cfg);
        }

        public string StatusText()
        {
            if (!IsRunning) return "未运行";
            return $"运行中 · 127.0.0.1:{Port} · 请求 {RequestCount}（成功 {SuccessCount} / 失败 {FailCount}）";
        }

        // ------------------------------------------------------------- 补丁

        public BinaryPatcher.PatchStatus CheckPatch(string exePath) => BinaryPatcher.Check(exePath);

        public BinaryPatcher.PatchStatus ApplyPatch(string exePath, int port) =>
            BinaryPatcher.Apply(exePath, port, keepBackup: true);

        public BinaryPatcher.PatchStatus RestorePatch(string exePath) => BinaryPatcher.Restore(exePath);

        /// <summary>目标程序是否正在运行（打补丁前必须关闭）。</summary>
        public static bool IsTargetRunning(AppConfig cfg)
        {
            try
            {
                string exeName = System.IO.Path.GetFileNameWithoutExtension(cfg?.TargetExecutable ?? "AyuGram");
                if (string.IsNullOrWhiteSpace(exeName)) exeName = "AyuGram";
                return System.Diagnostics.Process.GetProcessesByName(exeName).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose() => Stop();
    }
}
