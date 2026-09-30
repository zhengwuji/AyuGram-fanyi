using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AyuTranslate.UI;

namespace AyuTranslate.Core
{
    /// <summary>
    /// 总控：定位窗口 → 定时抓取翻译 → 更新覆盖层；管理热键与状态。
    /// </summary>
    public sealed class AppController : IDisposable
    {
        private AppConfig _cfg;
        private readonly Dispatcher _dispatcher;
        private readonly OverlayWindow _overlay;
        private TranslationPipeline _pipeline;
        private HotkeyManager _hotkeys;
        private DispatcherTimer _timer;
        private DispatcherTimer _followTimer;
        private CancellationTokenSource _cts;

        private bool _autoMode;
        private bool _busy;
        private TargetWindow _target;
        private DateTime _overlayAttachRetryUtc = DateTime.MinValue;
        private DateTime _lastConfigReloadUtc = DateTime.MinValue;
        private DateTime _settingsOpenedUtc = DateTime.MinValue;
        private List<OverlayItem> _lastItems = new List<OverlayItem>();

        public AppConfig Config => _cfg;
        public TranslationPipeline Pipeline => _pipeline;
        public TargetWindow Target => _target;

        public event Action<bool> AutoModeChanged;
        public event Action<string> StatusChanged;
        public event Action<string> ErrorRaised;
        public event Action TargetChanged;

        public bool AutoMode => _autoMode;
        public bool OverlayVisible => _overlay.OverlayVisible;
        public bool HotkeysAvailable => _hotkeys != null && _hotkeys.Registered.Count > 0;
        public IReadOnlyList<OverlayItem> LastItems => _lastItems;

        public AppController(Dispatcher dispatcher, OverlayWindow overlay, AppConfig cfg)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));

            _pipeline = new TranslationPipeline(_cfg);
            _pipeline.ErrorReported += msg => RaiseError(msg);

            _overlay.ApplyConfig(_cfg);

            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(_cfg.PollIntervalMs) };
            _timer.Tick += async (s, e) => await TickAsync().ConfigureAwait(true);

            _followTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _followTimer.Tick += (s, e) => FollowTick();
        }

        // --------------------------------------------------------------- 生命周期

        public void Start()
        {
            RegisterHotkeys();
            _timer.Interval = TimeSpan.FromMilliseconds(_cfg.PollIntervalMs);
            _timer.Start();
            _followTimer.Start();

            SetStatus("已启动，等待 AyuGram 窗口…");
            _autoMode = false;

            // 原生翻译接管：按配置自动启动本地代理
            if (_cfg.ProxyAutoStart || _cfg.ProxyEnabled)
            {
                if (ProxyManager.Instance.Start(_cfg))
                {
                    Log.Info($"已按配置自动启动原生翻译代理（端口 {_cfg.ProxyPort}）。");
                }
                else
                {
                    Log.Warn("自动启动原生翻译代理失败（端口可能被占用）。");
                }
            }

            // 首次定位。窗口操作必须回到界面线程，否则会碰到 WPF 的线程亲和性检查。
            LocateTarget(true);
        }

        public void Stop()
        {
            _timer?.Stop();
            _followTimer?.Stop();
            CancelCurrent();
            _hotkeys?.Dispose();
            _hotkeys = null;
            try { ProxyManager.Instance.Stop(); } catch { }
        }

        private void RegisterHotkeys()
        {
            _hotkeys?.Dispose();
            _hotkeys = new HotkeyManager(_overlay.Handle);
            _hotkeys.HotkeyPressed += OnHotkey;

            _hotkeys.Register(_cfg.HotkeyTranslate, "translate-once");
            _hotkeys.Register(_cfg.HotkeyToggleOverlay, "toggle-overlay");
            _hotkeys.Register(_cfg.HotkeyToggleAuto, "toggle-auto");
            _hotkeys.Register(_cfg.HotkeySettings, "settings");

            if (_hotkeys.Registered.Count == 0)
            {
                RaiseError("所有全局热键都注册失败，请到设置里改成未被占用的组合。");
            }
        }

        private void OnHotkey(string action)
        {
            switch (action)
            {
                case "translate-once":
                    _ = TranslateOnceAsync(manual: true);
                    break;
                case "toggle-overlay":
                    ToggleOverlay();
                    break;
                case "toggle-auto":
                    ToggleAutoMode();
                    break;
                case "settings":
                    OpenSettings();
                    break;
            }
        }

        // --------------------------------------------------------------- 目标窗口

        /// <summary>定位目标窗口。返回是否成功。</summary>
        public bool LocateTarget(bool announce = false)
        {
            try
            {
                var tw = WindowLocator.Find(_cfg);
                if (tw == null)
                {
                    if (announce)
                    {
                        SetStatus("未找到 AyuGram 窗口，请确认 AyuGram 已启动。");
                    }
                    if (_target != null)
                    {
                        _target = null;
                        _overlay.Detach();
                        TargetChanged?.Invoke();
                    }
                    return false;
                }

                bool changed = _target == null || _target.Handle != tw.Handle;
                _target = tw;

                if (changed)
                {
                    Log.Info("已找到目标窗口：" + tw);
                    _overlay.AttachTo(tw);
                    SetStatus("已连接：" + TextUtil.Truncate(string.IsNullOrWhiteSpace(tw.Title) ? "AyuGram" : tw.Title, 40));
                    TargetChanged?.Invoke();
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("定位目标窗口失败", ex);
                return false;
            }
        }

        private void FollowTick()
        {
            try
            {
                if (_target == null || !_target.IsWindowAlive())
                {
                    if (DateTime.UtcNow - _overlayAttachRetryUtc > TimeSpan.FromSeconds(2))
                    {
                        _overlayAttachRetryUtc = DateTime.UtcNow;
                        LocateTarget(false);
                    }
                    return;
                }

                bool focused = WindowLocator.IsForeground(_target);

                // 目标窗口切到后台时收起覆盖层，否则会挡住别的程序
                if (_cfg.HideOverlayWhenUnfocused && !focused)
                {
                    if (_overlay.OverlayVisible)
                    {
                        _overlay.SuspendForUnfocus();
                    }
                    return;
                }

                if (_overlay.SuspendedByUnfocus)
                {
                    _overlay.ResumeFromUnfocus(_lastItems);
                }

                if (_overlay.OverlayVisible || _lastItems.Count > 0)
                {
                    _overlay.FollowTarget();
                }
            }
            catch (Exception ex)
            {
                Log.Debug("跟随窗口失败：" + ex.Message);
            }
        }

        // --------------------------------------------------------------- 主循环

        private async Task TickAsync()
        {
            // 自动热重载配置（设置窗口保存后立即生效）
            TryReloadConfig();

            if (!_autoMode || _busy) return;

            if (_target == null || !_target.IsWindowAlive())
            {
                LocateTarget(false);
                if (_target == null) return;
            }

            if (_cfg.OnlyWhenTargetFocused && !WindowLocator.IsForeground(_target))
            {
                return;
            }

            await TranslateOnceAsync(manual: false).ConfigureAwait(true);
        }

        private void TryReloadConfig()
        {
            if ((DateTime.UtcNow - _lastConfigReloadUtc).TotalMilliseconds < 1500)
            {
                // 设置窗口打开时也要及时响应
                if ((DateTime.UtcNow - _settingsOpenedUtc) > TimeSpan.FromSeconds(30)) return;
            }
            _lastConfigReloadUtc = DateTime.UtcNow;

            try
            {
                var path = AppConfig.DefaultConfigPath;
                if (!System.IO.File.Exists(path)) return;
                var stamp = System.IO.File.GetLastWriteTimeUtc(path);
                if (stamp <= _cfgLoadedUtc) return;

                var fresh = AppConfig.Load(path);
                _cfgLoadedUtc = stamp;

                // 热键或轮询间隔变化需要重建定时器
                bool needHotkeyReload = fresh.HotkeyTranslate != _cfg.HotkeyTranslate ||
                                        fresh.HotkeyToggleOverlay != _cfg.HotkeyToggleOverlay ||
                                        fresh.HotkeyToggleAuto != _cfg.HotkeyToggleAuto ||
                                        fresh.HotkeySettings != _cfg.HotkeySettings;

                ApplyConfig(fresh, reloadHotkeys: needHotkeyReload);
                Log.Info("检测到配置文件变更，已热重载。");
            }
            catch (Exception ex)
            {
                Log.Debug("热重载配置失败：" + ex.Message);
            }
        }

        private DateTime _cfgLoadedUtc = DateTime.MinValue;

        /// <summary>应用新配置。</summary>
        public void ApplyConfig(AppConfig cfg, bool reloadHotkeys = true)
        {
            _cfg = cfg;
            _overlay.ApplyConfig(cfg);
            _pipeline.UpdateConfig(cfg);
            ProxyManager.Instance.UpdateConfig(cfg);
            _timer.Interval = TimeSpan.FromMilliseconds(cfg.PollIntervalMs);

            try
            {
                var path = AppConfig.DefaultConfigPath;
                if (System.IO.File.Exists(path))
                    _cfgLoadedUtc = System.IO.File.GetLastWriteTimeUtc(path);
            }
            catch { }

            if (reloadHotkeys) RegisterHotkeys();
        }

        public void SaveConfig()
        {
            _cfg.Save();
            try
            {
                _cfgLoadedUtc = System.IO.File.GetLastWriteTimeUtc(AppConfig.DefaultConfigPath);
            }
            catch { }
        }

        // --------------------------------------------------------------- 翻译

        private void CancelCurrent()
        {
            try { _cts?.Cancel(); } catch { }
        }

        public async Task TranslateOnceAsync(bool manual)
        {
            if (_busy) return;
            if (_target == null || !_target.IsWindowAlive())
            {
                if (!LocateTarget(true))
                {
                    if (manual) RaiseError("未找到 AyuGram 窗口。请先启动 AyuGram。");
                    return;
                }
            }

            _busy = true;
            CancelCurrent();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                var sw = Stopwatch.StartNew();
                // 手动触发时绕过帧跳过：即使画面没变，也要重试上次失败的分块
                var items = await _pipeline.RunOnceAsync(_target, ct,
                    translate: true, allowFrameSkip: !manual).ConfigureAwait(true);
                sw.Stop();

                if (ct.IsCancellationRequested) return;

                _lastItems = items ?? new List<OverlayItem>();
                _overlay.AttachTo(_target);
                _overlay.Update(_lastItems);

                if (_lastItems.Count == 0)
                {
                    SetStatus(_pipeline.Stats.LastError is string err && err.Length > 0
                        ? "无译文：" + TextUtil.Truncate(err, 60)
                        : "未识别到可翻译文本（检查区域设置）");
                }
                else
                {
                    SetStatus($"已翻译 {_lastItems.Count} 条 · {sw.ElapsedMilliseconds} ms" +
                              $"（OCR {_pipeline.Stats.LastOcrMs:F0}ms / 翻译 {_pipeline.Stats.LastTranslateMs:F0}ms）");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Exception("翻译一次失败", ex);
                RaiseError("翻译失败：" + ex.Message);
            }
            finally
            {
                _busy = false;
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }

        /// <summary>只做截图 + OCR（不翻译），用于校准区域。</summary>
        public async Task<List<TextBlock>> TestOcrAsync()
        {
            if (_target == null || !_target.IsWindowAlive())
            {
                LocateTarget(true);
                if (_target == null) return new List<TextBlock>();
            }

            var items = await _pipeline.RunOnceAsync(_target, CancellationToken.None, translate: false).ConfigureAwait(true);
            return _pipeline.LastBlocks ?? new List<TextBlock>();
        }

        /// <summary>测试翻译后端连通性。</summary>
        public async Task<Translate.TranslationResult> TestTranslatorAsync(string sampleText)
        {
            var translator = _pipeline.Translator;
            if (translator == null) return Translate.TranslationResult.Fail("翻译后端未初始化", "n/a");
            if (!translator.IsConfigured) return Translate.TranslationResult.Fail(translator.ConfigurationHint, translator.Name);

            var req = new Translate.TranslationRequest
            {
                Text = string.IsNullOrWhiteSpace(sampleText) ? "Hello, how are you today?" : sampleText,
                SourceLanguage = _cfg.SourceLanguage,
                TargetLanguage = _cfg.TargetLanguage,
            };
            return await translator.TranslateAsync(req, CancellationToken.None).ConfigureAwait(true);
        }

        // --------------------------------------------------------------- 开关

        public void ToggleOverlay()
        {
            if (_overlay.OverlayVisible)
            {
                _overlay.HideOverlay();
                SetStatus("覆盖层已隐藏");
            }
            else
            {
                if (_target != null && _target.IsWindowAlive()) _overlay.AttachTo(_target);
                _overlay.ShowOverlay();
                _overlay.Update(_lastItems);
                SetStatus("覆盖层已显示");
            }
        }

        public void ToggleAutoMode()
        {
            SetAutoMode(!_autoMode);
        }

        public void SetAutoMode(bool on)
        {
            _autoMode = on;
            if (!on) CancelCurrent();
            SetStatus(on ? "自动翻译：开启" : "自动翻译：关闭");
            AutoModeChanged?.Invoke(on);
        }

        public void OpenSettings()
        {
            _settingsOpenedUtc = DateTime.UtcNow;
            SettingsRequested?.Invoke();
        }

        public event Action SettingsRequested;

        // --------------------------------------------------------------- 输入框

        /// <summary>把译文写回 AyuGram 输入框。</summary>
        public async Task<bool> SendToInputBoxAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (_target == null || !_target.IsWindowAlive())
            {
                if (!LocateTarget(true)) return false;
            }

            return await Task.Run(() => InputInjector.Inject(_target, _cfg, text)).ConfigureAwait(true);
        }

        // --------------------------------------------------------------- 状态

        private void SetStatus(string message)
        {
            StatusChanged?.Invoke(message);
        }

        /// <summary>当前状态摘要（托盘菜单初始化时用）。</summary>
        public string Status()
        {
            if (_target == null) return "未连接 AyuGram 窗口";
            string title = string.IsNullOrWhiteSpace(_target.Title) ? "AyuGram" : _target.Title;
            var tr = _pipeline?.Translator;
            return $"已连接 {TextUtil.Truncate(title, 28)} · 接口 {tr?.Name ?? "-"} · 自动 {(_autoMode ? "开" : "关")}";
        }

        private void RaiseError(string message)
        {
            Log.Warn(message);
            ErrorRaised?.Invoke(message);
        }

        public void Dispose()
        {
            Stop();
            _pipeline?.Dispose();
            _pipeline = null;
            CancelCurrent();
        }
    }
}
