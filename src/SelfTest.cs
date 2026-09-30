using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AyuTranslate.Core;
using AyuTranslate.Translate;

namespace AyuTranslate
{
    /// <summary>
    /// 无界面自检：验证「定位窗口 → 抓图 → OCR → 分块 → 翻译」整条链路，
    /// 并把模拟覆盖层的结果渲染成 PNG，方便在没有 GUI 的情况下确认效果。
    ///
    /// 用法：
    ///   AyuTranslate.exe --selftest
    ///   AyuTranslate.exe --selftest --provider offline --out preview.png
    ///   AyuTranslate.exe --selftest --dump          (输出抓图与文本块)
    /// </summary>
    internal static class SelfTest
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        private const int ATTACH_PARENT_PROCESS = -1;

        /// <summary>WinExe 没有控制台，自检时挂到父进程控制台或新建一个。</summary>
        private static void EnsureConsole()
        {
            try
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    AllocConsole();
                }
                var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                Console.SetOut(new TeeWriter(stdout, null));
                var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetError(stderr);
            }
            catch
            {
                try { Console.SetOut(new TeeWriter(Console.Out, null)); } catch { }
            }
        }

        private static readonly StringBuilder _reportBuffer = new StringBuilder();

        /// <summary>同时写控制台与内存缓冲的写入器。</summary>
        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter _inner;
            public TeeWriter(TextWriter inner, StringBuilder ignored) { _inner = inner; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { _inner.Write(value); _reportBuffer.Append(value); }
            public override void Write(string value) { _inner.Write(value); _reportBuffer.Append(value); }
            public override void WriteLine(string value) { _inner.WriteLine(value); _reportBuffer.AppendLine(value); }
            public override void WriteLine() { _inner.WriteLine(); _reportBuffer.AppendLine(); }
            public override void Flush() { _inner.Flush(); }
        }

        /// <summary>命令行里是否带有任一自检开关。</summary>
        public static bool HasAnyModeFlag(string[] args)
        {
            if (args == null) return false;
            return args.Any(a =>
                a.Equals("--selftest", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--translate-test", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--overlaytest", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--proxy", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--patch", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--unpatch", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--patch-status", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--setup-native", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--revert-native", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/?", StringComparison.OrdinalIgnoreCase));
        }

        public static int Run(string[] args)
        {
            EnsureConsole();

            if (args.Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("/?", StringComparison.OrdinalIgnoreCase)))
            {
                PrintUsage();
                return 0;
            }

            int code;
            if (args.Any(a => a.Equals("--setup-native", StringComparison.OrdinalIgnoreCase)))
                code = RunSetupNative(args, revert: false);
            else if (args.Any(a => a.Equals("--revert-native", StringComparison.OrdinalIgnoreCase)))
                code = RunSetupNative(args, revert: true);
            else if (args.Any(a => a.Equals("--patch-status", StringComparison.OrdinalIgnoreCase)))
                code = RunPatch(args, restore: false, statusOnly: true);
            else if (args.Any(a => a.Equals("--patch", StringComparison.OrdinalIgnoreCase)))
                code = RunPatch(args, restore: false);
            else if (args.Any(a => a.Equals("--unpatch", StringComparison.OrdinalIgnoreCase)))
                code = RunPatch(args, restore: true);
            else if (args.Any(a => a.Equals("--proxy", StringComparison.OrdinalIgnoreCase)))
                code = RunProxy(args);
            else if (args.Any(a => a.Equals("--translate-test", StringComparison.OrdinalIgnoreCase)))
                code = RunTranslateTest(args);
            else if (args.Any(a => a.Equals("--overlaytest", StringComparison.OrdinalIgnoreCase)))
                code = RunOverlayTest(args);
            else
                code = RunCore(args);

            try
            {
                string reportPath = GetArg(args, "--report");
                if (string.IsNullOrWhiteSpace(reportPath))
                    reportPath = Path.Combine(AppConfig.DefaultLogDirectory, "selftest-report.txt");

                File.WriteAllText(reportPath, _reportBuffer.ToString(), Encoding.UTF8);
                Console.WriteLine();
                Console.WriteLine("报告已写入：" + reportPath);
            }
            catch { }

            return code;
        }

        private const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        // ================================================================
        //  一键安装 / 卸载原生翻译接管
        // ================================================================

        /// <summary>
        /// 一键完成原生接管的三件事：
        ///   1. 改写 AyuGram.exe 里 Google 翻译的接口地址
        ///   2. 把 tdata/ayu_settings.json 里的 translationProvider 设为 google
        ///   3. 提示如何启动代理
        /// 必须先把 AyuGram 完全退出（含托盘图标）。
        /// </summary>
        private static int RunSetupNative(string[] args, bool revert)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            int port = ParsePort(args);

            string exe = GetArg(args, "--exe");
            if (string.IsNullOrWhiteSpace(exe)) exe = cfg.TargetExecutable;

            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine(revert ? "=== 卸载原生翻译接管 ===" : "=== 安装原生翻译接管 ===");
            Console.WriteLine("目标程序 : " + exe);
            Console.WriteLine("代理端口 : " + port);
            Console.WriteLine();

            if (ProxyManager.IsTargetRunning(cfg))
            {
                Console.WriteLine("[FAIL] AyuGram 正在运行。请先完全退出（包括托盘图标），然后重试。");
                return 2;
            }

            // ---------- 1) 二进制补丁 ----------
            if (revert)
            {
                var rst = Proxy.BinaryPatcher.Restore(exe);
                Console.WriteLine("· 还原二进制 : " + rst.Message);
            }
            else
            {
                var pst = Proxy.BinaryPatcher.Apply(exe, port, keepBackup: true);
                Console.WriteLine("· 写入补丁   : " + pst.Message);
                if (!pst.Patched)
                {
                    Console.WriteLine();
                    Console.WriteLine("[FAIL] 补丁未写入，后续设置未修改。");
                    return 3;
                }
            }

            // ---------- 2) AyuGram 设置 ----------
            string settingsPath = FindAyuSettings(exe);
            if (settingsPath == null)
            {
                Console.WriteLine("· 设置文件   : 未找到 tdata/ayu_settings.json（可跳过，手动在界面里选 Google）");
            }
            else
            {
                bool ok = TrySetTranslationProvider(settingsPath, revert ? "telegram" : "google", out string msg);
                Console.WriteLine("· 翻译服务   : " + msg);
                if (!ok)
                {
                    Console.WriteLine("              （可手动在 AyuGram → 设置 → AyuGram 选项 → 翻译服务 里选择）");
                }
            }

            Console.WriteLine();
            if (revert)
            {
                Console.WriteLine("已卸载。AyuGram 将恢复使用官方翻译接口。");
                Console.WriteLine("备份文件仍保留：AyuGram.exe.ayutranslate.bak");
            }
            else
            {
                Console.WriteLine("安装完成。接下来：");
                Console.WriteLine($"  1. 启动代理：  {AppContext.BaseDirectory}AyuTranslate.exe --proxy --port {port}");
                Console.WriteLine("  2. 启动 AyuGram");
                Console.WriteLine("  3. 在任意消息上点「翻译」，译文即由你自己的 AI 生成");
                Console.WriteLine();
                Console.WriteLine("提示：也可以直接运行 AyuTranslate.exe，在设置 →「原生翻译接管」里开关。");
            }

            return 0;
        }

        /// <summary>在 AyuGram 目录下找 tdata/ayu_settings.json。</summary>
        private static string FindAyuSettings(string exePath)
        {
            try
            {
                string dir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(dir)) return null;

                string p = Path.Combine(dir, "tdata", "ayu_settings.json");
                return File.Exists(p) ? p : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 修改 ayu_settings.json 里的 translationProvider。
        /// 使用 JsonNode 做外科式修改，保留文件里的其它所有设置。
        /// </summary>
        private static bool TrySetTranslationProvider(string path, string provider, out string message)
        {
            message = "";
            try
            {
                string json = File.ReadAllText(path);
                var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                if (node == null)
                {
                    message = "设置文件解析失败";
                    return false;
                }

                string current = node["translationProvider"]?.GetValue<string>() ?? "(未设置)";
                if (string.Equals(current, provider, StringComparison.OrdinalIgnoreCase))
                {
                    message = $"已是 {provider}，无需修改";
                    return true;
                }

                // 备份一次
                string backup = path + ".ayutranslate.bak";
                if (!File.Exists(backup)) File.Copy(path, backup, overwrite: false);

                node["translationProvider"] = provider;

                var opts = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, node.ToJsonString(opts), new UTF8Encoding(false));
                File.Replace(tmp, path, null);

                message = $"{current} → {provider}";
                return true;
            }
            catch (Exception ex)
            {
                message = "修改失败：" + ex.Message;
                return false;
            }
        }

        // ================================================================
        //  原生翻译接管：打补丁 / 还原 / 查看状态
        // ================================================================

        private static int RunPatch(string[] args, bool restore, bool statusOnly = false)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            int port = ParsePort(args);

            string exe = GetArg(args, "--exe");
            if (string.IsNullOrWhiteSpace(exe)) exe = cfg.TargetExecutable;

            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("目标文件 : " + exe);
            Console.WriteLine("代理端口 : " + port);
            Console.WriteLine("原始地址 : " + Proxy.BinaryPatcher.OriginalUrl);
            Console.WriteLine("代理地址 : " + Proxy.BinaryPatcher.BuildProxyUrl(port));
            Console.WriteLine();

            if (statusOnly)
            {
                var st = Proxy.BinaryPatcher.Check(exe);
                PrintPatchStatus(st);
                return st.Patched ? 0 : 1;
            }

            if (restore)
            {
                var st = Proxy.BinaryPatcher.Restore(exe);
                Console.WriteLine(st.Message);
                if (st.Message.StartsWith("已从备份还原"))
                {
                    Console.WriteLine();
                    Console.WriteLine("提示：备份文件仍保留在 AyuGram.exe.ayutranslate.bak，");
                    Console.WriteLine("      确认 AyuGram 能正常启动后可以手动删除。");
                    return 0;
                }
                return 2;
            }

            var applied = Proxy.BinaryPatcher.Apply(exe, port, keepBackup: true);
            Console.WriteLine(applied.Message);
            Console.WriteLine();

            if (applied.Patched)
            {
                Console.WriteLine("下一步：");
                Console.WriteLine("  1. 启动 AyuGram");
                Console.WriteLine("  2. 打开  设置 → AyuGram 选项 → 翻译服务  ，选择「Google」");
                Console.WriteLine("  3. 保持本代理运行：" + AppContext.BaseDirectory + "AyuTranslate.exe --proxy");
                Console.WriteLine("  4. 在任意消息上点「翻译」，译文就会由你自己的 AI 生成（原生界面显示）");
                return 0;
            }
            return 3;
        }

        private static void PrintPatchStatus(Proxy.BinaryPatcher.PatchStatus st)
        {
            Console.WriteLine("文件存在   : " + (st.FileExists ? "是" : "否"));
            if (st.FileExists)
            {
                Console.WriteLine("文件大小   : " + st.FileSize.ToString("N0") + " 字节");
                Console.WriteLine("备份存在   : " + (st.BackupExists ? "是" : "否"));
                Console.WriteLine("原始地址   : 出现 " + st.OriginalOccurrences + " 次");
                Console.WriteLine("已打补丁   : " + (st.Patched ? "是" : "否"));
                if (!string.IsNullOrWhiteSpace(st.CurrentUrl))
                    Console.WriteLine("当前地址   : " + st.CurrentUrl);
            }
            Console.WriteLine();
            Console.WriteLine(st.Message);
        }

        // ================================================================
        //  本地翻译代理（前台运行）
        // ================================================================

        private static int RunProxy(string[] args)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            ApplyOverrides(cfg, args);

            Log.Init(cfg.LogDirectory, true);
            Console.OutputEncoding = Encoding.UTF8;

            int port = ParsePort(args);

            using (var server = new Proxy.TranslateProxyServer(cfg, port))
            {
                var translator = TranslatorFactory.Create(cfg);
                bool configured = translator.IsConfigured;
                string name = translator.Name;
                string hint = translator.ConfigurationHint;
                translator.Dispose();

                Console.WriteLine("AyuTranslate 本地翻译代理");
                Console.WriteLine("========================================");
                Console.WriteLine("监听地址 : http://127.0.0.1:" + port + Proxy.TranslateProxyServer.TranslatePath);
                Console.WriteLine("翻译后端 : " + name);
                Console.WriteLine("模型     : " + cfg.Model);
                Console.WriteLine("目标语言 : " + cfg.TargetLanguage);
                Console.WriteLine("========================================");

                if (!configured)
                {
                    Console.WriteLine();
                    Console.WriteLine("[FAIL] 翻译后端未配置：" + hint);
                    Console.WriteLine("       请先在设置界面填好接口地址与模型。");
                    return 3;
                }

                server.RequestLogged += line => Console.WriteLine("  " + line);

                try
                {
                    server.Start();
                }
                catch (Exception ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("[FAIL] 无法监听端口 " + port + "：" + ex.Message);
                    Console.WriteLine("       换个端口：--port 8767");
                    return 4;
                }

                Console.WriteLine();
                Console.WriteLine("代理已就绪。请确认 AyuGram 已完成设置：");
                Console.WriteLine("  1. 已执行过 --patch（把接口地址指向本代理）");
                Console.WriteLine("  2. 设置 → AyuGram 选项 → 翻译服务 选择「Google」");
                Console.WriteLine();
                Console.WriteLine("按 Ctrl+C 退出。");
                Console.WriteLine();

                var quit = new ManualResetEventSlim(false);
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    quit.Set();
                };

                while (!quit.Wait(1000))
                {
                    // 每秒打印一次统计，方便观察是否真的有请求进来
                    if (server.RequestCount > 0)
                    {
                        Console.Title = $"AyuTranslate 代理  请求 {server.RequestCount}  成功 {server.SuccessCount}  失败 {server.FailCount}";
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"共处理 {server.RequestCount} 个请求：成功 {server.SuccessCount}，失败 {server.FailCount}");
                if (server.FailCount > 0 && !string.IsNullOrWhiteSpace(server.LastError))
                    Console.WriteLine("最近错误：" + server.LastError);
            }

            return 0;
        }

        private static int ParsePort(string[] args)
        {
            string p = GetArg(args, "--port");
            if (!string.IsNullOrWhiteSpace(p) && int.TryParse(p, out int v) && v > 0 && v < 65536) return v;
            return Proxy.BinaryPatcher.DefaultPort;
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"AyuTranslate — AyuGram 实时翻译覆盖层

用法:
  AyuTranslate.exe                       正常启动（托盘图标 + 覆盖层）
  AyuTranslate.exe --help                显示本帮助

原生翻译接管（推荐，效果与 AyuGram 自带翻译完全一致）:
  --setup-native      一键安装：改写接口地址 + 把翻译服务设为 Google（需先退出 AyuGram）
  --revert-native     一键卸载：还原二进制 + 翻译服务改回 Telegram
  --patch             只改写 AyuGram 内置 Google 翻译的接口地址
  --unpatch           只从备份还原二进制
  --patch-status      查看当前补丁状态
  --proxy             前台运行本地翻译代理（Ctrl+C 退出）
  --port <端口>       指定代理端口（默认 8766）

自检模式（不启动界面）:
  --selftest          完整链路：定位窗口 → 抓图 → OCR → 分块 → 翻译 → 生成预览图
  --translate-test    只测翻译接口（不需要 AyuGram 窗口），并报告耗时
  --overlaytest       真的显示覆盖层并截图验证

常用参数:
  --provider <名字>   OpenAICompatible | CustomHttp | DeepL | LibreTranslate | GoogleUnofficial | Offline
  --url <地址>        API 地址（OpenAI 兼容填 base url，程序自动补 /chat/completions）
  --model <名字>      模型名
  --key <密钥>        API 密钥
  --target <代码>     目标语言，如 zh-CN / en / ja
  --exe <路径>        AyuGram.exe 路径
  --template <模板>   自定义请求体模板（{text} {target} {source}）
  --response-path <路径>  自定义响应取值路径，如 data.translation
  --headers <头>      额外请求头，多条用 | 分隔
  --sidebar           把左侧会话列表也纳入识别范围
  --dump              输出区域截图与全部文本块
  --out <文件>        预览图输出路径
  --config <文件>     使用指定配置文件
  --report <文件>     自检报告输出路径

示例:
  AyuTranslate.exe --translate-test --provider OpenAICompatible ^
      --url https://api.deepseek.com/v1 --model deepseek-chat --key sk-xxx

  AyuTranslate.exe --selftest --provider offline --dump

原生翻译接管完整流程:
  AyuTranslate.exe --patch --exe ""C:\Program Files\AyuGram\AyuGram.exe"" --port 8766
  AyuTranslate.exe --proxy --port 8766
  (然后在 AyuGram 里把翻译服务选成 Google)
");
        }

        /// <summary>
        /// 只测翻译接口，不需要 AyuGram 窗口。
        /// 用来在配置阶段快速确认「地址 / 密钥 / 模型 / 响应解析」是否正确，
        /// 并把耗时和 token 消耗打出来，便于判断接口是否够快。
        /// </summary>
        private static int RunTranslateTest(string[] args)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            ApplyOverrides(cfg, args);

            Log.Init(cfg.LogDirectory, false);
            Console.OutputEncoding = Encoding.UTF8;

            using (var translator = TranslatorFactory.Create(cfg))
            {
                Console.WriteLine("接口类型  : " + translator.Name);
                Console.WriteLine("接口地址  : " + (string.IsNullOrWhiteSpace(cfg.ApiEndpoint)
                    ? "(空，按 API 地址自动拼接)" : cfg.ApiEndpoint));
                Console.WriteLine("模型      : " + cfg.Model);
                Console.WriteLine("目标语言  : " + cfg.TargetLanguage);
                Console.WriteLine("密钥      : " + (string.IsNullOrWhiteSpace(cfg.ApiKey)
                    ? "(未设置)"
                    : cfg.ApiKey.Substring(0, Math.Min(8, cfg.ApiKey.Length)) + "…"));
                Console.WriteLine();

                if (!translator.IsConfigured)
                {
                    Console.WriteLine("[FAIL] 配置不完整：" + translator.ConfigurationHint);
                    return 3;
                }

                // ---- 单条 ----
                var singleReq = new TranslationRequest
                {
                    Text = "Good morning! The server is under maintenance, please try again later.",
                    SourceLanguage = cfg.SourceLanguage,
                    TargetLanguage = cfg.TargetLanguage,
                };

                Console.WriteLine("---- 单条翻译 ----");
                var sw = Stopwatch.StartNew();
                TranslationResult r1;
                try
                {
                    r1 = translator.TranslateAsync(singleReq, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] 单条请求异常：" + ex.Message);
                    return 4;
                }
                sw.Stop();

                if (r1.Success)
                {
                    Console.WriteLine($"[ OK ] {sw.ElapsedMilliseconds} ms");
                    Console.WriteLine("       原文：" + singleReq.Text);
                    Console.WriteLine("       译文：" + r1.Text);
                }
                else
                {
                    Console.WriteLine($"[FAIL] {sw.ElapsedMilliseconds} ms -> {r1.Error}");
                }
                Console.WriteLine();

                // ---- 批量（模拟真实使用：8 条短消息一次发过去）----
                var batchTexts = new[]
                {
                    "Hello, how are you today?",
                    "The invoice has been paid.",
                    "Please check your billing settings.",
                    "We will deploy the new version tonight.",
                    "The latency looks a bit high today.",
                    "Thanks for your patience.",
                    "Can you share the API document?",
                    "Let me know if you need more quota.",
                };
                var batchReq = batchTexts.Select(t => new TranslationRequest
                {
                    Text = t,
                    SourceLanguage = cfg.SourceLanguage,
                    TargetLanguage = cfg.TargetLanguage,
                }).ToList();

                Console.WriteLine($"---- 批量翻译（{batchReq.Count} 条一次性发出）----");
                var sw2 = Stopwatch.StartNew();
                List<TranslationResult> rs;
                try
                {
                    rs = translator.TranslateBatchAsync(batchReq, CancellationToken.None).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] 批量请求异常：" + ex.Message);
                    return 5;
                }
                sw2.Stop();

                int ok = 0;
                for (int i = 0; i < batchReq.Count; i++)
                {
                    var r = i < rs.Count ? rs[i] : null;
                    if (r != null && r.Success && !string.IsNullOrWhiteSpace(r.Text))
                    {
                        ok++;
                        Console.WriteLine($"  ✔ {TextUtil.Truncate(batchReq[i].Text, 44)}");
                        Console.WriteLine($"    → {TextUtil.Truncate(r.Text.Replace("\n", " / "), 70)}");
                    }
                    else
                    {
                        Console.WriteLine($"  ✘ {TextUtil.Truncate(batchReq[i].Text, 44)}");
                        Console.WriteLine($"    → {r?.Error ?? "无结果"}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"       {(ok == batchReq.Count ? "[ OK ]" : "[WARN]")} 批量耗时 {sw2.ElapsedMilliseconds} ms，成功 {ok}/{batchReq.Count}");

                if (ok == 0) return 6;

                // 速度提示
                long perItem = sw2.ElapsedMilliseconds / Math.Max(1, batchReq.Count);
                Console.WriteLine($"       平均每条 {perItem} ms" +
                                  (perItem > 3000 ? "  ← 偏慢，建议换更快的模型或调大批量条数" : ""));
                return 0;
            }
        }

        /// <summary>
        /// 真正的端到端验证：启动覆盖层窗口、跑一轮流水线、把覆盖层实拍下来。
        /// 会真的在屏幕上显示覆盖层约 1 秒。
        /// </summary>
        private static int RunOverlayTest(string[] args)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            ApplyOverrides(cfg, args);
            cfg.Provider = ProviderKind.Offline;   // 不联网
            cfg.IncludeSidebar = true;             // 保证屏幕上一定有可识别文字
            cfg.OnlyWhenTargetFocused = false;
            cfg.HideOverlayWhenUnfocused = false;  // 测试时强制显示
            cfg.Normalize();

            Log.Init(cfg.LogDirectory, true);

            string outPath = GetArg(args, "--out") ??
                             Path.Combine(AppContext.BaseDirectory, "selftest-overlay.png");

            int exit = 0;

            var app = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown,
            };

            UI.OverlayWindow overlay = null;
            AppController controller = null;

            app.Startup += async (s, e) =>
            {
                try
                {
                    var tw = WindowLocator.Find(cfg);
                    if (tw == null)
                    {
                        Console.WriteLine("[FAIL] 未找到目标窗口。");
                        exit = 2;
                        app.Shutdown();
                        return;
                    }
                    Console.WriteLine("[ OK ] 目标窗口：" + tw);

                    EnsureWindowVisible(tw);

                    overlay = new UI.OverlayWindow();
                    overlay.ApplyConfig(cfg);
                    overlay.Show();          // 先创建句柄
                    overlay.Hide();

                    controller = new AppController(app.Dispatcher, overlay, cfg);
                    controller.LocateTarget(false);

                    // 顺带验证设置窗口的 XAML 能否正常构造（构造失败会抛异常）
                    try
                    {
                        var sw = new UI.SettingsWindow(controller);
                        Console.WriteLine("[ OK ] 设置窗口构造成功（" + sw.Title + "）");
                        sw.Close();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[FAIL] 设置窗口构造失败：" + ex.Message);
                        exit = 7;
                    }

                    var items = await controller.Pipeline.RunOnceAsync(controller.Target, CancellationToken.None);

                    Console.WriteLine($"[ OK ] 覆盖条目 = {items.Count}");
                    foreach (var it in items.Take(30))
                    {
                        Console.WriteLine($"      {it.Bounds}  {TextUtil.Truncate(it.Translation.Replace("\n", " "), 60)}");
                    }

                    if (items.Count == 0)
                    {
                        Console.WriteLine("[WARN] 没有生成覆盖条目。");
                        exit = 5;
                    }

                    overlay.AttachTo(controller.Target);
                    overlay.Update(items);

                    await Task.Delay(1400);   // 等 WPF 渲染

                    // 方式一：真实屏幕截图（只有 AyuGram 在最前面时才拍得到内容）
                    var full = ScreenCapture.CaptureScreen(controller.Target, clientOnly: true);
                    if (full != null)
                    {
                        full.Save(outPath, ImageFormat.Png);
                        Console.WriteLine("[ OK ] 覆盖层实拍（屏幕）已保存：" + outPath);
                        full.Dispose();
                    }

                    // 方式二：把覆盖条目合成到 PrintWindow 内容上。
                    // 即使 AyuGram 被别的窗口挡住，也能准确看到最终观感。
                    var content = ScreenCapture.Capture(controller.Target, CaptureBackend.PrintWindow, clientOnly: true);
                    if (content != null)
                    {
                        string compositePath = Path.Combine(
                            Path.GetDirectoryName(outPath) ?? ".",
                            Path.GetFileNameWithoutExtension(outPath) + "-composite.png");
                        Compositor.Render(content, items, cfg, compositePath);
                        content.Dispose();
                        Console.WriteLine("[ OK ] 覆盖层合成图已保存：" + compositePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FAIL] 覆盖层测试异常：" + ex);
                    Log.Exception("覆盖层测试异常", ex);
                    exit = 9;
                }
                finally
                {
                    try { overlay?.Hide(); } catch { }
                    try { controller?.Dispose(); } catch { }
                    app.Shutdown();
                }
            };

            app.Run();

            Console.WriteLine(exit == 0 ? "覆盖层测试完成。" : "覆盖层测试完成：有警告。");
            return exit;
        }

        /// <summary>确保目标窗口可见（隐藏/最小化时 PrintWindow 会返回全黑）。</summary>
        private static void EnsureWindowVisible(TargetWindow tw)
        {
            try
            {
                if (!tw.IsVisible || tw.IsMinimized)
                {
                    Console.WriteLine("[INFO] 目标窗口当前不可见，尝试恢复显示…");
                    ShowWindow(tw.Handle, SW_RESTORE);
                    Thread.Sleep(1000);
                    WindowLocator.Refresh(tw);
                }
                if (!tw.IsVisible)
                {
                    Console.WriteLine("[WARN] 目标窗口仍不可见，抓图结果可能是黑色。");
                }
            }
            catch (Exception ex)
            {
                Log.Debug("恢复窗口失败：" + ex.Message);
            }
        }

        private static int RunCore(string[] args)
        {
            var cfg = AppConfig.Load(GetArg(args, "--config"));
            ApplyOverrides(cfg, args);

            Log.Init(cfg.LogDirectory, true);
            Log.Info("=== 自检模式启动 ===");
            Log.Info("配置：" + AppConfig.DefaultConfigPath);

            string outPath = GetArg(args, "--out") ??
                             Path.Combine(AppContext.BaseDirectory, "selftest-preview.png");
            bool dump = args.Any(a => a.Equals("--dump", StringComparison.OrdinalIgnoreCase));

            Console.OutputEncoding = Encoding.UTF8;

            int exit = 0;
            try
            {
                // ---- 1) 定位窗口 ----
                var tw = WindowLocator.Find(cfg);
                if (tw == null)
                {
                    Console.WriteLine("[FAIL] 未找到目标窗口。请先启动 AyuGram。");
                    Console.WriteLine("       期望的进程：" + cfg.TargetExecutable);
                    return 2;
                }
                Console.WriteLine("[ OK ] 目标窗口：" + tw);
                Console.WriteLine("       客户区原点 = " + tw.ClientOrigin.X + "," + tw.ClientOrigin.Y +
                                  "   DPI = " + tw.Dpi);

                // ---- 2) 抓图 ----
                var sw = Stopwatch.StartNew();
                var full = ScreenCapture.Capture(tw, CaptureBackend.PrintWindow, clientOnly: true);
                sw.Stop();
                if (full == null)
                {
                    Console.WriteLine("[FAIL] PrintWindow 抓图失败。");
                    return 3;
                }
                Console.WriteLine($"[ OK ] 抓图 {full.Width}x{full.Height} 用时 {sw.ElapsedMilliseconds} ms");

                // ---- 3) 区域 ----
                var auto = BlockDetector.AutoDetectChatRegion(tw.ClientWidth, tw.ClientHeight, cfg);
                var region = BlockDetector.ResolveRegion(cfg.ChatRegion, auto, tw.ClientWidth, tw.ClientHeight);
                Console.WriteLine("[ OK ] 聊天区域（客户区坐标）：" + region);
                if (cfg.ChatRegion != null && cfg.ChatRegion.IsUsable)
                    Console.WriteLine("       来源：手动配置");
                else
                    Console.WriteLine("       来源：自动推断");

                var regionBmp = ScreenCapture.Crop(full, region.X, region.Y, region.Width, region.Height);
                if (dump)
                {
                    string p = Path.ChangeExtension(outPath, null) + "-region.png";
                    regionBmp.Save(p, ImageFormat.Png);
                    Console.WriteLine("       已保存区域截图：" + p);
                }

                // ---- 4) OCR ----
                var langs = OcrService.AvailableLanguages();
                Console.WriteLine(langs.Count == 0
                    ? "[WARN] 系统没有可用的 OCR 语言包！"
                    : "[ OK ] 系统 OCR 语言：" + string.Join(", ", langs));

                using (var ocr = new OcrService(cfg.OcrLanguages))
                {
                    Console.WriteLine("       启用引擎：" + string.Join(", ", ocr.ActiveLanguages));
                    if (!ocr.HasEngine)
                    {
                        Console.WriteLine("[FAIL] 没有可用的 OCR 引擎。");
                        return 4;
                    }

                    var swOcr = Stopwatch.StartNew();
                    var scaled = OcrService.Scale(regionBmp, cfg.OcrScale);
                    var lines = ocr.RecognizeAsync(scaled, cfg.OcrScale, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    swOcr.Stop();
                    if (!ReferenceEquals(scaled, regionBmp)) scaled.Dispose();

                    Console.WriteLine($"[ OK ] OCR 原始行数 = {lines.Count}，用时 {swOcr.ElapsedMilliseconds} ms");

                    // ---- 5) 分块 ----
                    var blocks = BlockDetector.GroupLines(lines, cfg)
                        .Where(b => b.X >= -8 && b.Y >= -8 && b.X < region.Width + 8 && b.Y < region.Height + 8)
                        .ToList();

                    Console.WriteLine($"[ OK ] 合并后文本块 = {blocks.Count}");
                    Console.WriteLine();

                    foreach (var b in blocks)
                    {
                        Console.WriteLine($"  [{b.X,5:F0},{b.Y,5:F0} {b.Width,5:F0}x{b.Height,4:F0}] {TextUtil.Truncate(b.Text.Replace("\n", " / "), 90)}");
                    }
                    Console.WriteLine();

                    if (blocks.Count == 0)
                    {
                        Console.WriteLine("[WARN] 没有识别到文本块。可能原因：");
                        Console.WriteLine("       - 聊天区域设置不正确（试试调整 ChatRegion）");
                        Console.WriteLine("       - 聊天窗口没有打开会话（右侧是空白背景）");
                        Console.WriteLine("       - OCR 语言与消息语言不匹配");
                        exit = 5;
                    }

                    // ---- 6) 翻译 ----
                    var translator = TranslatorFactory.Create(cfg);
                    Console.WriteLine("[ OK ] 翻译后端：" + translator.Name + "，已配置 = " + translator.IsConfigured);

                    var results = new List<string>();
                    if (translator.IsConfigured && blocks.Count > 0)
                    {
                        var todo = blocks
                            .Where(b => !TextUtil.IsNoise(b.Text, cfg.MinTextLength))
                            .Where(b => !(cfg.SkipTargetLanguage && TextUtil.IsAlreadyTargetLanguage(b.Text, cfg.TargetLanguage)))
                            .Take(8)
                            .ToList();

                        Console.WriteLine($"       待翻译 {todo.Count} 条（最多取 8 条用于自检）");

                        var swTr = Stopwatch.StartNew();
                        var reqs = todo.Select(b => new TranslationRequest
                        {
                            Text = b.Text,
                            SourceLanguage = cfg.SourceLanguage,
                            TargetLanguage = cfg.TargetLanguage,
                        }).ToList();
                        var res = translator.TranslateBatchAsync(reqs, CancellationToken.None)
                            .GetAwaiter().GetResult();
                        swTr.Stop();

                        int okCount = 0;
                        for (int i = 0; i < todo.Count; i++)
                        {
                            var r = i < res.Count ? res[i] : null;
                            if (r != null && r.Success)
                            {
                                okCount++;
                                Console.WriteLine($"  ✔ {TextUtil.Truncate(todo[i].Text.Replace("\n", " "), 40)}");
                                Console.WriteLine($"    → {TextUtil.Truncate(r.Text.Replace("\n", " "), 70)}");
                                results.Add(r.Text);
                            }
                            else
                            {
                                Console.WriteLine($"  ✘ {TextUtil.Truncate(todo[i].Text.Replace("\n", " "), 40)}");
                                Console.WriteLine($"    → 失败：{r?.Error ?? "无结果"}");
                                results.Add("");
                            }
                        }
                        Console.WriteLine($"[ OK ] 翻译成功 {okCount}/{todo.Count}，用时 {swTr.ElapsedMilliseconds} ms");
                    }
                    else if (!translator.IsConfigured)
                    {
                        Console.WriteLine("[WARN] 翻译后端未配置：" + translator.ConfigurationHint);
                    }

                    // ---- 7) 渲染模拟覆盖层 ----
                    RenderPreview(regionBmp, blocks, cfg, outPath);
                    Console.WriteLine("[ OK ] 覆盖层预览已保存：" + outPath);
                    Console.WriteLine("       打开这张图即可核对译文位置是否精确覆盖原文。");

                    translator.Dispose();
                }

                full.Dispose();
                regionBmp.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] 自检异常：" + ex);
                Log.Exception("自检异常", ex);
                return 9;
            }

            Console.WriteLine();
            Console.WriteLine(exit == 0 ? "自检完成：链路正常。" : "自检完成：有警告，见上文。");
            return exit;
        }

        /// <summary>把译文按位置画到原图上，模拟真实覆盖层效果。</summary>
        private static void RenderPreview(Bitmap region, List<TextBlock> blocks, AppConfig cfg, string outPath)
        {
            using (var canvas = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.DrawImageUnscaled(region, 0, 0);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                    var bgColor = HotkeyUtil.ParseColor(cfg.OverlayBackground, System.Windows.Media.Color.FromArgb(0xD9, 0, 0, 0));
                    var fgColor = HotkeyUtil.ParseColor(cfg.OverlayForeground, System.Windows.Media.Colors.White);
                    var bg = Color.FromArgb(bgColor.A, bgColor.R, bgColor.G, bgColor.B);
                    var fg = Color.FromArgb(fgColor.A, fgColor.R, fgColor.G, fgColor.B);

                    using (var brush = new SolidBrush(bg))
                    using (var textBrush = new SolidBrush(fg))
                    {
                        int i = 0;
                        foreach (var b in blocks)
                        {
                            if (i++ > 60) break;

                            var rect = new Rectangle(
                                (int)Math.Floor(b.X) - cfg.OverlayBleed,
                                (int)Math.Floor(b.Y) - cfg.OverlayBleed - cfg.OverlayPaddingY,
                                (int)Math.Ceiling(b.Width) + cfg.OverlayBleed * 2,
                                (int)Math.Ceiling(b.Height) + cfg.OverlayBleed * 2 + cfg.OverlayPaddingY * 2);

                            rect.Intersect(new Rectangle(0, 0, canvas.Width, canvas.Height));
                            if (rect.Width < 8 || rect.Height < 6) continue;

                            g.FillRectangle(brush, rect);

                            // 自检时用原文占位，避免真的消耗接口额度
                            string text = "〔译文〕" + TextUtil.Truncate(b.Text, 90);
                            using (var font = new Font(cfg.OverlayFontFamily, (float)cfg.OverlayFontSize,
                                       FontStyle.Regular, GraphicsUnit.Pixel))
                            {
                                var size = g.MeasureString(text, font, Math.Max(10, rect.Width - 6));
                                var rectF = new RectangleF(rect.X + 3, rect.Y + cfg.OverlayPaddingY,
                                    Math.Max(10, rect.Width - 6), Math.Max(10, rect.Height));
                                g.DrawString(text, font, textBrush, rectF);
                            }

                            using (var pen = new Pen(Color.FromArgb(90, 0xFF, 0x60, 0x60), 1))
                            {
                                g.DrawRectangle(pen, rect);
                            }
                        }
                    }
                }

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
                canvas.Save(outPath, ImageFormat.Png);
            }
        }

        private static void ApplyOverrides(AppConfig cfg, string[] args)
        {
            string provider = GetArg(args, "--provider");
            if (!string.IsNullOrWhiteSpace(provider) && Enum.TryParse(provider, true, out ProviderKind kind))
                cfg.Provider = kind;

            string target = GetArg(args, "--target");
            if (!string.IsNullOrWhiteSpace(target)) cfg.TargetLanguage = target;

            string model = GetArg(args, "--model");
            if (!string.IsNullOrWhiteSpace(model)) cfg.Model = model;

            string url = GetArg(args, "--url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                cfg.ApiBaseUrl = url;
                cfg.ApiEndpoint = url.Contains("/chat/completions") ? url : AppConfig.CombineUrl(url, "chat/completions");
            }

            string key = GetArg(args, "--key");
            if (!string.IsNullOrWhiteSpace(key)) cfg.ApiKey = key;

            string exe = GetArg(args, "--exe");
            if (!string.IsNullOrWhiteSpace(exe)) cfg.TargetExecutable = exe;

            string template = GetArg(args, "--template");
            if (!string.IsNullOrWhiteSpace(template)) cfg.RequestTemplate = template;

            string path = GetArg(args, "--response-path");
            if (!string.IsNullOrWhiteSpace(path)) cfg.ResponsePath = path;

            string headers = GetArg(args, "--headers");
            if (!string.IsNullOrWhiteSpace(headers)) cfg.ExtraHeaders = headers.Replace("|", "\n");

            if (args.Any(a => a.Equals("--sidebar", StringComparison.OrdinalIgnoreCase)))
                cfg.IncludeSidebar = true;

            cfg.Normalize();
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }
    }
}
