using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AyuTranslate.Core;
using AyuTranslate.Translate;

namespace AyuTranslate.UI
{
    public partial class SettingsWindow : Window
    {
        private readonly AppController _controller;
        private AppConfig _working;
        private bool _loading;
        private bool _regionAuto = true;

        public SettingsWindow(AppController controller)
        {
            _controller = controller;
            _working = controller.Config.Clone();

            InitializeComponent();

            _loading = true;
            PopulateChoices();
            LoadFromConfig(_working);
            _loading = false;

            RefreshPreview();
            RefreshOcrAvailable();
            RefreshPatchStatus();
            RefreshProxyStatus();

            // 所有输入控件变更都刷新预览
            foreach (var box in FindVisualChildren<TextBox>(this))
            {
                box.TextChanged += (s, e) => { if (!_loading) RefreshPreview(); };
            }
            foreach (var cb in FindVisualChildren<CheckBox>(this))
            {
                cb.Checked += (s, e) => { if (!_loading) RefreshPreview(); };
                cb.Unchecked += (s, e) => { if (!_loading) RefreshPreview(); };
            }
        }

        // ---------------------------------------------------------------- 初始化

        private void PopulateChoices()
        {
            ProviderBox.ItemsSource = Enum.GetValues(typeof(ProviderKind))
                .Cast<ProviderKind>()
                .Select(p => TranslatorFactory.DisplayName(p))
                .ToList();
            ProviderBox.SelectedIndex = (int)_working.Provider;

            TargetLangBox.ItemsSource = LanguageMap.Languages.Select(l => l.Display + "  (" + l.UiCode + ")").ToList();
            SourceLangBox.ItemsSource = new[] { "自动检测 (auto)" }
                .Concat(LanguageMap.Languages.Select(l => l.Display + "  (" + l.UiCode + ")"))
                .ToList();

            InputMethodBox.ItemsSource = new[]
            {
                "剪贴板 + Ctrl+V（推荐，兼容性最好）",
                "直接发送字符消息（不动剪贴板）",
            };
            InputSendModeBox.ItemsSource = new[]
            {
                "仅填入输入框（不发送，推荐）",
                "填入后自动按回车发送",
            };
        }

        private void LoadFromConfig(AppConfig c)
        {
            var idx = LanguageMap.Languages.Select((l, i) => new { l, i })
                .FirstOrDefault(x => x.l.UiCode == c.TargetLanguage);
            TargetLangBox.SelectedIndex = idx?.i ?? 0;

            if (string.Equals(c.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
            {
                SourceLangBox.SelectedIndex = 0;
            }
            else
            {
                var s = LanguageMap.Languages.Select((l, i) => new { l, i })
                    .FirstOrDefault(x => x.l.UiCode == c.SourceLanguage);
                SourceLangBox.SelectedIndex = s != null ? s.i + 1 : 0;
            }

            BaseUrlBox.Text = c.ApiBaseUrl ?? "";
            EndpointBox.Text = c.ApiEndpoint ?? "";
            ApiKeyBox.Password = c.ApiKey ?? "";
            ModelBox.Text = c.Model ?? "";
            PromptBox.Text = c.SystemPrompt ?? "";
            HeadersBox.Text = c.ExtraHeaders ?? "";
            TimeoutBox.Text = c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            RetriesBox.Text = c.MaxRetries.ToString(CultureInfo.InvariantCulture);
            TemperatureBox.Text = c.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
            RequestTemplateBox.Text = c.RequestTemplate ?? "";
            ResponsePathBox.Text = c.ResponsePath ?? "";

            RegionAutoBox.IsChecked = c.ChatRegion?.Auto ?? true;
            _regionAuto = RegionAutoBox.IsChecked == true;
            RegionXBox.Text = (c.ChatRegion?.X ?? 0).ToString(CultureInfo.InvariantCulture);
            RegionYBox.Text = (c.ChatRegion?.Y ?? 0).ToString(CultureInfo.InvariantCulture);
            RegionWBox.Text = (c.ChatRegion?.Width ?? 0).ToString(CultureInfo.InvariantCulture);
            RegionHBox.Text = (c.ChatRegion?.Height ?? 0).ToString(CultureInfo.InvariantCulture);
            IncludeSidebarBox.IsChecked = c.IncludeSidebar;

            OcrLangBox.Text = string.Join(", ", c.OcrLanguages ?? new System.Collections.Generic.List<string>());
            OcrScaleBox.Text = c.OcrScale.ToString("0.##", CultureInfo.InvariantCulture);
            MergeGapBox.Text = c.LineMergeGap.ToString(CultureInfo.InvariantCulture);
            MinLineHeightBox.Text = c.MinLineHeight.ToString(CultureInfo.InvariantCulture);
            MinTextLenBox.Text = c.MinTextLength.ToString(CultureInfo.InvariantCulture);

            SkipTargetLangBox.IsChecked = c.SkipTargetLanguage;
            CacheEnabledBox.IsChecked = c.CacheEnabled;
            CacheSizeBox.Text = c.CacheMaxEntries.ToString(CultureInfo.InvariantCulture);
            OverlayBgBox.Text = c.OverlayBackground ?? "";
            OverlayFgBox.Text = c.OverlayForeground ?? "";
            OverlayOrigFgBox.Text = c.OverlayOriginalForeground ?? "";
            FontSizeBox.Text = c.OverlayFontSize.ToString("0.##", CultureInfo.InvariantCulture);
            PadYBox.Text = c.OverlayPaddingY.ToString(CultureInfo.InvariantCulture);
            BleedBox.Text = c.OverlayBleed.ToString(CultureInfo.InvariantCulture);
            OpacityBox.Text = c.OverlayOpacity.ToString("0.##", CultureInfo.InvariantCulture);
            FontFamilyBox.Text = c.OverlayFontFamily ?? "";
            MaxLinesBox.Text = c.OverlayMaxLines.ToString(CultureInfo.InvariantCulture);
            MaxItemsBox.Text = c.OverlayMaxItems.ToString(CultureInfo.InvariantCulture);
            ShowOriginalBox.IsChecked = c.ShowOriginalText;
            ClickThroughBox.IsChecked = c.OverlayClickThrough;

            PollIntervalBox.Text = c.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
            MinRequestIntervalBox.Text = c.MinRequestIntervalMs.ToString(CultureInfo.InvariantCulture);
            OnlyFocusedBox.IsChecked = c.OnlyWhenTargetFocused;
            HideWhenUnfocusedBox.IsChecked = c.HideOverlayWhenUnfocused;
            StartVisibleBox.IsChecked = c.StartWithOverlayVisible;
            DebugLogBox.IsChecked = c.DebugLog;
            TargetExeBox.Text = c.TargetExecutable ?? "";

            HotkeyTranslateBox.Text = c.HotkeyTranslate ?? "";
            HotkeyToggleOverlayBox.Text = c.HotkeyToggleOverlay ?? "";
            HotkeyToggleAutoBox.Text = c.HotkeyToggleAuto ?? "";
            HotkeySettingsBox.Text = c.HotkeySettings ?? "";

            InputMethodBox.SelectedIndex = (int)c.InputMethod;
            InputSendModeBox.SelectedIndex = (int)c.InputSendMode;

            ProxyAutoStartBox.IsChecked = c.ProxyAutoStart;
            ProxyFallbackBox.IsChecked = c.ProxyFallbackToGoogle;
            OverlayEnabledBox.IsChecked = c.OverlayEnabled;
            ProxyPortBox.Text = c.ProxyPort.ToString(CultureInfo.InvariantCulture);
            PatchExeBox.Text = string.IsNullOrWhiteSpace(c.TargetExecutable)
                ? "C:\\Program Files\\AyuGram\\AyuGram.exe"
                : c.TargetExecutable;

            UpdateRegionEnabled();
        }

        private void UpdateRegionEnabled()
        {
            bool auto = RegionAutoBox.IsChecked == true;
            foreach (var b in new[] { RegionXBox, RegionYBox, RegionWBox, RegionHBox })
            {
                b.IsEnabled = !auto;
                b.Opacity = auto ? 0.45 : 1.0;
            }
        }

        private void OnRegionAutoChanged(object sender, RoutedEventArgs e)
        {
            _regionAuto = RegionAutoBox.IsChecked == true;
            if (!_loading) UpdateRegionEnabled();
        }

        private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var kind = (ProviderKind)Math.Max(0, ProviderBox.SelectedIndex);

            // 切换接口类型时给几个典型默认值，省得手填
            switch (kind)
            {
                case ProviderKind.OpenAICompatible:
                    if (string.IsNullOrWhiteSpace(BaseUrlBox.Text) || BaseUrlBox.Text.Contains("deepl") ||
                        BaseUrlBox.Text.Contains("localhost:5000") || BaseUrlBox.Text.Contains("translate"))
                    {
                        BaseUrlBox.Text = "https://api.deepseek.com/v1";
                        ModelBox.Text = "deepseek-chat";                    }
                    EndpointBox.Text = AppConfig.CombineUrl(BaseUrlBox.Text, "chat/completions");
                    break;
                case ProviderKind.DeepL:
                    EndpointBox.Text = "https://api-free.deepl.com/v2/translate";
                    break;
                case ProviderKind.LibreTranslate:
                    EndpointBox.Text = "http://localhost:5000/translate";
                    break;
                case ProviderKind.GoogleUnofficial:
                    EndpointBox.Text = "https://translate.googleapis.com/translate_a/single";
                    break;
                case ProviderKind.CustomHttp:
                    EndpointBox.Text = "https://your-endpoint.example.com/translate";
                    if (string.IsNullOrWhiteSpace(RequestTemplateBox.Text))
                        RequestTemplateBox.Text = "{\"text\":\"{text}\",\"target_lang\":\"{target}\"}";
                    if (string.IsNullOrWhiteSpace(ResponsePathBox.Text))
                        ResponsePathBox.Text = "data.translation";
                    break;
                case ProviderKind.Offline:
                    break;
            }
        }

        private void RefreshOcrAvailable()
        {
            try
            {
                var available = OcrService.AvailableLanguages();
                OcrAvailableText.Text = available.Count == 0
                    ? "⚠ 系统未安装任何 OCR 语言包，识别无法工作。"
                    : "本机可用的 OCR 语言：" + string.Join(", ", available);
            }
            catch (Exception ex)
            {
                OcrAvailableText.Text = "枚举 OCR 语言失败：" + ex.Message;
            }
        }

        // ---------------------------------------------------------------- 预览

        private void RefreshPreview()
        {
            try
            {
                PreviewCanvas.Children.Clear();

                var bg = HotkeyUtil.ParseColor(OverlayBgBox.Text, System.Windows.Media.Color.FromArgb(0xD9, 0, 0, 0));
                var fg = HotkeyUtil.ParseColor(OverlayFgBox.Text, Colors.White);

                double fontSize = ParseDouble(FontSizeBox.Text, 15);
                double padY = ParseDouble(PadYBox.Text, 3);
                double bleed = ParseDouble(BleedBox.Text, 4);

                double w = Math.Max(120, PreviewCanvas.ActualWidth - 20);
                if (w < 120) w = 640;

                string sample = "Translated preview text — 覆盖层预览效果 / 这是译文示例";

                var card = new Border
                {
                    Background = new SolidColorBrush(bg),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, padY, 6, padY),
                    Margin = new Thickness(12, 10, 12, 10),
                    Width = Math.Max(120, w - 24),
                };

                var ft = new System.Windows.Controls.TextBlock
                {
                    Text = sample,
                    Foreground = new SolidColorBrush(fg),
                    FontFamily = new FontFamily(string.IsNullOrWhiteSpace(FontFamilyBox.Text)
                        ? "Microsoft YaHei UI"
                        : FontFamilyBox.Text),
                    FontSize = Math.Max(8, fontSize),
                    TextWrapping = TextWrapping.Wrap,
                };
                card.Child = ft;

                PreviewCanvas.Width = double.NaN;
                PreviewCanvas.Children.Add(card);
            }
            catch
            {
                // 预览失败无所谓
            }
        }

        private static double ParseDouble(string s, double fallback)
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }

        private static int ParseInt(string s, int fallback)
        {
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;
        }

        // ---------------------------------------------------------------- 保存

        private void Collect(AppConfig c)
        {
            c.Provider = (ProviderKind)Math.Max(0, ProviderBox.SelectedIndex);
            c.ApiBaseUrl = BaseUrlBox.Text.Trim();
            c.ApiEndpoint = EndpointBox.Text.Trim();
            c.ApiKey = ApiKeyBox.Password ?? "";
            c.Model = ModelBox.Text.Trim();
            c.SystemPrompt = PromptBox.Text;
            c.ExtraHeaders = HeadersBox.Text;
            c.TimeoutSeconds = ParseInt(TimeoutBox.Text, 60);
            c.MaxRetries = ParseInt(RetriesBox.Text, 2);
            c.Temperature = ParseDouble(TemperatureBox.Text, 0.2);
            c.RequestTemplate = RequestTemplateBox.Text;
            c.ResponsePath = ResponsePathBox.Text.Trim();

            c.TargetLanguage = SelectedLangCode(TargetLangBox, "zh-CN");
            c.SourceLanguage = SourceLangBox.SelectedIndex <= 0
                ? "auto"
                : SelectedLangCode(SourceLangBox, "auto", offset: 1);

            c.ChatRegion = new RegionConfig
            {
                Auto = RegionAutoBox.IsChecked == true,
                X = ParseInt(RegionXBox.Text, 0),
                Y = ParseInt(RegionYBox.Text, 0),
                Width = ParseInt(RegionWBox.Text, 0),
                Height = ParseInt(RegionHBox.Text, 0),
            };
            c.IncludeSidebar = IncludeSidebarBox.IsChecked == true;

            c.OcrLanguages = (OcrLangBox.Text ?? "")
                .Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (c.OcrLanguages.Count == 0) c.OcrLanguages.Add("zh-Hans-CN");

            c.OcrScale = ParseDouble(OcrScaleBox.Text, 2.0);
            c.LineMergeGap = ParseInt(MergeGapBox.Text, 10);
            c.MinLineHeight = ParseInt(MinLineHeightBox.Text, 8);
            c.MinTextLength = ParseInt(MinTextLenBox.Text, 2);

            c.SkipTargetLanguage = SkipTargetLangBox.IsChecked == true;
            c.CacheEnabled = CacheEnabledBox.IsChecked == true;
            c.CacheMaxEntries = ParseInt(CacheSizeBox.Text, 4000);

            c.OverlayBackground = OverlayBgBox.Text.Trim();
            c.OverlayForeground = OverlayFgBox.Text.Trim();
            c.OverlayOriginalForeground = OverlayOrigFgBox.Text.Trim();
            c.OverlayFontSize = ParseDouble(FontSizeBox.Text, 15);
            c.OverlayPaddingY = ParseInt(PadYBox.Text, 3);
            c.OverlayBleed = ParseInt(BleedBox.Text, 4);
            c.OverlayOpacity = ParseDouble(OpacityBox.Text, 1.0);
            c.OverlayFontFamily = FontFamilyBox.Text.Trim();
            c.OverlayMaxLines = ParseInt(MaxLinesBox.Text, 6);
            c.OverlayMaxItems = ParseInt(MaxItemsBox.Text, 400);
            c.ShowOriginalText = ShowOriginalBox.IsChecked == true;
            c.OverlayClickThrough = ClickThroughBox.IsChecked == true;

            c.PollIntervalMs = ParseInt(PollIntervalBox.Text, 700);
            c.MinRequestIntervalMs = ParseInt(MinRequestIntervalBox.Text, 350);
            c.OnlyWhenTargetFocused = OnlyFocusedBox.IsChecked == true;
            c.HideOverlayWhenUnfocused = HideWhenUnfocusedBox.IsChecked == true;
            c.StartWithOverlayVisible = StartVisibleBox.IsChecked == true;
            c.DebugLog = DebugLogBox.IsChecked == true;
            c.TargetExecutable = TargetExeBox.Text.Trim();

            c.HotkeyTranslate = HotkeyTranslateBox.Text.Trim();
            c.HotkeyToggleOverlay = HotkeyToggleOverlayBox.Text.Trim();
            c.HotkeyToggleAuto = HotkeyToggleAutoBox.Text.Trim();
            c.HotkeySettings = HotkeySettingsBox.Text.Trim();

            c.InputMethod = (InputMethod)Math.Max(0, InputMethodBox.SelectedIndex);
            c.InputSendMode = (InputSendMode)Math.Max(0, InputSendModeBox.SelectedIndex);

            c.ProxyPort = ParseInt(ProxyPortBox.Text, c.ProxyPort);
            c.ProxyAutoStart = ProxyAutoStartBox.IsChecked == true;
            c.ProxyFallbackToGoogle = ProxyFallbackBox.IsChecked == true;
            c.OverlayEnabled = OverlayEnabledBox.IsChecked == true;
            c.ProxyEnabled = ProxyManager.Instance.IsRunning;
            if (!string.IsNullOrWhiteSpace(PatchExeBox.Text))
                c.TargetExecutable = PatchExeBox.Text.Trim();

            c.Normalize();
        }

        private static string SelectedLangCode(ComboBox box, string fallback, int offset = 0)
        {
            int i = box.SelectedIndex - offset;
            if (i < 0 || i >= LanguageMap.Languages.Count) return fallback;
            return LanguageMap.Languages[i].UiCode;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _controller.Config.Clone();
                Collect(cfg);

                Log.Verbose = cfg.DebugLog;
                Log.Init(cfg.LogDirectory, cfg.DebugLog);

                _controller.ApplyConfig(cfg, reloadHotkeys: true);
                _controller.SaveConfig();

                _working = cfg.Clone();
                StatusText.Text = "✔ 已保存并生效。配置文件：" + AppConfig.DefaultConfigPath;
            }
            catch (Exception ex)
            {
                Log.Exception("保存设置失败", ex);
                StatusText.Text = "✘ 保存失败：" + ex.Message;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void OnTestTranslator(object sender, RoutedEventArgs e)
        {
            _ = TestTranslatorAsync();
        }

        /// <summary>从 /models 拉取可用模型，填进下拉框。</summary>
        private void OnFetchModels(object sender, RoutedEventArgs e)
        {
            _ = FetchModelsAsync();
        }

        private async System.Threading.Tasks.Task FetchModelsAsync()
        {
            try
            {
                StatusText.Text = "正在获取模型列表…";

                var cfg = _controller.Config.Clone();
                Collect(cfg);

                string url = ModelListService.ResolveModelsUrl(cfg);
                if (string.IsNullOrWhiteSpace(url))
                {
                    StatusText.Text = "✘ 请先填写「API 地址」。";
                    return;
                }

                var models = await ModelListService.FetchAsync(cfg, System.Threading.CancellationToken.None);

                if (models.Count == 0)
                {
                    StatusText.Text = $"✘ 没有取到模型列表（{url}）。该服务可能不支持 /models，" +
                                      "请手动填写模型名。";
                    return;
                }

                string current = ModelBox.Text;
                ModelBox.ItemsSource = models.Select(m => m.Id).ToList();

                // 尽量保留用户已填的模型名
                if (!string.IsNullOrWhiteSpace(current)) ModelBox.Text = current;

                StatusText.Text = $"✔ 取到 {models.Count} 个模型：{url}";
                Log.Info($"模型列表（{models.Count}）：" + string.Join(", ", models.Take(30).Select(m => m.Id)));
            }
            catch (Exception ex)
            {
                Log.Exception("获取模型列表失败", ex);
                StatusText.Text = "✘ 获取模型列表异常：" + ex.Message;
            }
        }

        private async System.Threading.Tasks.Task TestTranslatorAsync()
        {
            try
            {
                StatusText.Text = "正在测试翻译接口…";

                var cfg = _controller.Config.Clone();
                Collect(cfg);

                // 临时用界面上的配置测，不影响正在跑的实例
                using (var translator = TranslatorFactory.Create(cfg))
                {
                    if (!translator.IsConfigured)
                    {
                        StatusText.Text = "✘ " + (string.IsNullOrWhiteSpace(translator.ConfigurationHint)
                            ? "配置不完整"
                            : translator.ConfigurationHint);
                        return;
                    }

                    var req = new TranslationRequest
                    {
                        Text = "Hello, how are you today? This is a connectivity test.",
                        SourceLanguage = cfg.SourceLanguage,
                        TargetLanguage = cfg.TargetLanguage,
                    };

                    var sw = Stopwatch.StartNew();
                    var result = await translator.TranslateAsync(req, System.Threading.CancellationToken.None);
                    sw.Stop();

                    if (result.Success)
                    {
                        StatusText.Text = $"✔ [{translator.Name}] {sw.ElapsedMilliseconds} ms → {result.Text}";
                    }
                    else
                    {
                        StatusText.Text = $"✘ [{translator.Name}] 失败：{result.Error}";
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("测试翻译接口失败", ex);
                StatusText.Text = "✘ 测试异常：" + ex.Message;
            }
        }

        private void OnTestOcr(object sender, RoutedEventArgs e)
        {
            _ = TestOcrAsync();
        }

        private async System.Threading.Tasks.Task TestOcrAsync()
        {
            try
            {
                StatusText.Text = "正在抓取 AyuGram 并识别…";

                var cfg = _controller.Config.Clone();
                Collect(cfg);
                _controller.ApplyConfig(cfg, reloadHotkeys: false);

                var blocks = await _controller.TestOcrAsync();

                if (blocks == null || blocks.Count == 0)
                {
                    StatusText.Text = "✘ 没有识别到文本。请确认 AyuGram 窗口已打开、且没有最小化；" +
                                      "或调整“聊天区域”后重试。";
                    return;
                }

                string sample = string.Join("  |  ",
                    blocks.Take(5).Select(b => TextUtil.Truncate(b.Text.Replace("\n", " "), 24)));

                StatusText.Text = $"✔ 识别到 {blocks.Count} 个文本块。示例：{sample}";
                Log.Info("OCR 测试结果：" + blocks.Count + " 块");
                foreach (var b in blocks.Take(40)) Log.Debug("  " + b);
            }
            catch (Exception ex)
            {
                Log.Exception("测试 OCR 失败", ex);
                StatusText.Text = "✘ OCR 测试异常：" + ex.Message;
            }
        }

        private void OnOpenConfigDir(object sender, RoutedEventArgs e) => OpenShell(AppConfig.ConfigDirectory);

        // ================================================================
        //  原生翻译接管
        // ================================================================

        private void OnApplyPatch(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _controller.Config;
                string exe = string.IsNullOrWhiteSpace(PatchExeBox.Text)
                    ? cfg.TargetExecutable
                    : PatchExeBox.Text.Trim();

                if (ProxyManager.IsTargetRunning(cfg))
                {
                    StatusText.Text = "✘ AyuGram 正在运行，请先完全退出（含托盘图标）再打补丁。";
                    return;
                }

                int port = ParseInt(ProxyPortBox.Text, cfg.ProxyPort);

                var st = ProxyManager.Instance.ApplyPatch(exe, port);
                StatusText.Text = (st.Patched ? "✔ " : "✘ ") + st.Message;
                RefreshPatchStatus();
            }
            catch (Exception ex)
            {
                Log.Exception("打补丁失败", ex);
                StatusText.Text = "✘ 打补丁异常：" + ex.Message;
            }
        }

        private void OnRestorePatch(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _controller.Config;
                string exe = string.IsNullOrWhiteSpace(PatchExeBox.Text)
                    ? cfg.TargetExecutable
                    : PatchExeBox.Text.Trim();

                if (ProxyManager.IsTargetRunning(cfg))
                {
                    StatusText.Text = "✘ AyuGram 正在运行，请先完全退出再还原。";
                    return;
                }

                var st = ProxyManager.Instance.RestorePatch(exe);
                bool ok = st.Message.StartsWith("已从备份还原");
                StatusText.Text = (ok ? "✔ " : "✘ ") + st.Message;
                RefreshPatchStatus();
            }
            catch (Exception ex)
            {
                Log.Exception("还原失败", ex);
                StatusText.Text = "✘ 还原异常：" + ex.Message;
            }
        }

        private void OnRefreshPatchStatus(object sender, RoutedEventArgs e) => RefreshPatchStatus();

        private void RefreshPatchStatus()
        {
            try
            {
                string exe = PatchExeBox?.Text?.Trim();
                if (string.IsNullOrWhiteSpace(exe)) exe = _controller.Config.TargetExecutable;

                var st = ProxyManager.Instance.CheckPatch(exe);

                if (!st.FileExists)
                {
                    PatchStatusText.Text = "✘ " + st.Message;
                    PatchStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
                    return;
                }

                if (st.Patched)
                {
                    PatchStatusText.Text =
                        $"✔ 已接管：{st.CurrentUrl}\n" +
                        "AyuGram 的 Google 翻译会走本地代理。记得在 AyuGram 里把翻译服务选成 Google。";
                    PatchStatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
                }
                else if (st.OriginalOccurrences == 1)
                {
                    PatchStatusText.Text =
                        "○ 未接管：当前使用官方 Google 翻译接口。\n" +
                        "点「打补丁」即可改为使用你自己的 AI。";
                    PatchStatusText.Foreground = System.Windows.Media.Brushes.Gainsboro;
                }
                else
                {
                    PatchStatusText.Text = "⚠ " + st.Message;
                    PatchStatusText.Foreground = System.Windows.Media.Brushes.Orange;
                }
            }
            catch (Exception ex)
            {
                PatchStatusText.Text = "检测失败：" + ex.Message;
                PatchStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            }
        }

        private void OnOpenTargetDir(object sender, RoutedEventArgs e)
        {
            try
            {
                string exe = PatchExeBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(exe)) exe = _controller.Config.TargetExecutable;
                string dir = System.IO.Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir)) OpenShell(dir);
            }
            catch (Exception ex)
            {
                StatusText.Text = "打开目录失败：" + ex.Message;
            }
        }

        private void OnStartProxy(object sender, RoutedEventArgs e)
        {
            try
            {
                var cfg = _controller.Config.Clone();
                Collect(cfg);
                cfg.ProxyPort = ParseInt(ProxyPortBox.Text, cfg.ProxyPort);

                var pm = ProxyManager.Instance;
                if (pm.IsRunning) pm.Restart(cfg);
                else pm.Start(cfg);

                // 端口/开关记进配置
                _controller.Config.ProxyPort = cfg.ProxyPort;
                _controller.Config.ProxyEnabled = pm.IsRunning;
                _controller.Config.ProxyAutoStart = ProxyAutoStartBox.IsChecked == true;
                _controller.SaveConfig();

                StatusText.Text = pm.IsRunning
                    ? $"✔ 代理已启动：http://127.0.0.1:{pm.Port}/v1/translateHtml"
                    : "✘ 代理启动失败，请检查端口是否被占用（可换一个端口）。";
                RefreshProxyStatus();
            }
            catch (Exception ex)
            {
                Log.Exception("启动代理失败", ex);
                StatusText.Text = "✘ 启动代理异常：" + ex.Message;
            }
        }

        private void OnStopProxy(object sender, RoutedEventArgs e)
        {
            try
            {
                ProxyManager.Instance.Stop();
                _controller.Config.ProxyEnabled = false;
                _controller.SaveConfig();
                StatusText.Text = "已停止代理。AyuGram 的翻译将无法工作（除非还原补丁）。";
                RefreshProxyStatus();
            }
            catch (Exception ex)
            {
                StatusText.Text = "停止代理异常：" + ex.Message;
            }
        }

        private void OnRefreshProxyLog(object sender, RoutedEventArgs e) => RefreshProxyLog();

        private void OnClearProxyLog(object sender, RoutedEventArgs e)
        {
            ProxyManager.Instance.ClearLog();
            RefreshProxyLog();
        }

        private void RefreshProxyLog()
        {
            try
            {
                var log = ProxyManager.Instance.RecentLog;
                ProxyLogBox.Text = log.Count == 0
                    ? "（暂无翻译记录。在 AyuGram 里点一条消息的「翻译」后回来刷新。）"
                    : string.Join(Environment.NewLine, log);
            }
            catch { }
        }

        private void RefreshProxyStatus()
        {
            try
            {
                var pm = ProxyManager.Instance;
                ProxyStatusText.Text = pm.StatusText();
                ProxyStatusText.Foreground = pm.IsRunning
                    ? System.Windows.Media.Brushes.LightGreen
                    : System.Windows.Media.Brushes.Gainsboro;
                RefreshProxyLog();
            }
            catch { }
        }

        /// <summary>测试代理是否能正确翻译（自己发一个 AyuGram 格式的请求过去）。</summary>
        private async void OnTestProxy(object sender, RoutedEventArgs e)
        {
            try
            {
                var pm = ProxyManager.Instance;
                if (!pm.IsRunning)
                {
                    StatusText.Text = "✘ 代理尚未启动，请先点「启动代理」。";
                    return;
                }

                StatusText.Text = "正在通过代理测试翻译…";

                int port = pm.Port;
                string body = "[[[\"Hello, this is a proxy test from AyuTranslate.\"],\"auto\",\"zh-CN\"],\"wt_lib\"]";

                string response = await System.Threading.Tasks.Task.Run(async () =>
                {
                    using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(120) })
                    {
                        var content = new System.Net.Http.StringContent(
                            body, System.Text.Encoding.UTF8, "application/json+protobuf");
                        using (var resp = await http.PostAsync(
                                   $"http://127.0.0.1:{port}/v1/translateHtml", content))
                        {
                            return await resp.Content.ReadAsStringAsync();
                        }
                    }
                });

                // 按 AyuGram 的方式解析：取 root[0] 里所有字符串
                string text = "(无法解析)";
                try
                {
                    using (var doc = System.Text.Json.JsonDocument.Parse(response))
                    {
                        var root = doc.RootElement;
                        if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() > 0)
                        {
                            var parts = new List<string>();
                            CollectStrings(root[0], parts);
                            text = string.Join(" ", parts);
                        }
                    }
                }
                catch { }

                StatusText.Text = $"✔ 代理工作正常，译文：{text}";
                RefreshProxyStatus();
            }
            catch (Exception ex)
            {
                Log.Exception("测试代理失败", ex);
                StatusText.Text = "✘ 代理测试失败：" + ex.Message;
            }
        }

        private static void CollectStrings(System.Text.Json.JsonElement node, List<string> into)
        {
            switch (node.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    into.Add(node.GetString());
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray()) CollectStrings(item, into);
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    if (node.TryGetProperty("text", out var t)) CollectStrings(t, into);
                    if (node.TryGetProperty("trans", out var tr)) CollectStrings(tr, into);
                    break;
            }
        }


        private void OnOpenLogDir(object sender, RoutedEventArgs e) => OpenShell(AppConfig.DefaultLogDirectory);

        private void OnOpenConfigFile(object sender, RoutedEventArgs e)
        {
            string p = AppConfig.DefaultConfigPath;
            if (File.Exists(p)) OpenShell(p);
            else StatusText.Text = "配置文件还不存在，先点一次“保存”。";
        }

        private static void OpenShell(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log.Warn("打开路径失败 " + path + "：" + ex.Message);
            }
        }

        // ---------------------------------------------------------------- 工具

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
            where T : DependencyObject
        {
            if (root == null) yield break;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T t) yield return t;
                foreach (var d in FindVisualChildren<T>(child)) yield return d;
            }
        }
    }
}
