using System;
using System.IO;
using System.Linq;
using System.Text;
using AyuTranslate.Core;

namespace AyuTranslate.Proxy
{
    /// <summary>
    /// 把 AyuGram.exe 里内置「Google 翻译」的接口地址改写到本地代理。
    ///
    /// 原理：AyuGram 的 ayu/features/translator/implementations/google.cpp 里
    /// 把接口地址写成了字符串常量：
    ///     constexpr auto kGoogleTranslateUrl = "https://translate-pa.googleapis.com/v1/translateHtml";
    /// 该字符串在二进制中唯一存在，且没有长度校验，因此可以原地替换成
    /// 一个**不更长**的地址（用 \0 补齐尾部）。
    ///
    /// 安全性：
    ///   - 修改前自动备份为 AyuGram.exe.ayutranslate.bak
    ///   - 支持一键还原
    ///   - 只在字符串唯一匹配时才动手，匹配到 0 处或多处都直接放弃
    /// </summary>
    public static class BinaryPatcher
    {
        /// <summary>AyuGram 源码里硬编码的原始地址。</summary>
        public const string OriginalUrl = "https://translate-pa.googleapis.com/v1/translateHtml";

        public const string BackupSuffix = ".ayutranslate.bak";

        /// <summary>本地代理默认监听的端口。</summary>
        public const int DefaultPort = 8766;

        public static string BuildProxyUrl(int port) => $"http://127.0.0.1:{port}/v1/translateHtml";

        /// <summary>是否够短，能塞进原字符串的位置。</summary>
        public static bool FitsInPlace(string url) =>
            !string.IsNullOrEmpty(url) && url.Length <= OriginalUrl.Length;

        public sealed class PatchStatus
        {
            public bool FileExists;
            public bool Patched;
            public bool BackupExists;
            public int OriginalOccurrences;
            public int ProxyOccurrences;
            public string CurrentUrl;
            public string Message;
            public long FileSize;
        }

        /// <summary>检查当前补丁状态。</summary>
        public static PatchStatus Check(string exePath)
        {
            var st = new PatchStatus();

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                st.Message = "找不到 AyuGram.exe：" + exePath;
                return st;
            }

            st.FileExists = true;
            st.FileSize = new FileInfo(exePath).Length;
            st.BackupExists = File.Exists(exePath + BackupSuffix);

            byte[] data;
            try
            {
                data = File.ReadAllBytes(exePath);
            }
            catch (Exception ex)
            {
                st.Message = "读取文件失败：" + ex.Message;
                return st;
            }

            st.OriginalOccurrences = CountOccurrences(data, Encoding.ASCII.GetBytes(OriginalUrl));
            st.ProxyOccurrences = CountOccurrences(data, Encoding.ASCII.GetBytes("http://127.0.0.1:"));

            if (st.OriginalOccurrences == 1)
            {
                st.Patched = false;
                st.CurrentUrl = OriginalUrl;
                st.Message = st.BackupExists
                    ? "未打补丁（存在备份文件，可能是之前还原过）"
                    : "未打补丁，使用官方 Google 翻译接口";
            }
            else if (st.OriginalOccurrences == 0 && st.ProxyOccurrences > 0)
            {
                st.Patched = true;
                st.CurrentUrl = ReadPatchedUrl(data);
                st.Message = "已打补丁 → " + st.CurrentUrl;
            }
            else
            {
                st.Message = $"无法确定状态：原始地址出现 {st.OriginalOccurrences} 次" +
                             "（可能是 AyuGram 版本不同或已被其他工具修改）。为安全起见不会修改。";
            }

            return st;
        }

        /// <summary>
        /// 写入补丁：把原始地址替换成本地代理地址。
        /// </summary>
        /// <param name="exePath">AyuGram.exe 路径。</param>
        /// <param name="port">本地代理端口。</param>
        /// <param name="keepBackup">是否保留备份（强烈建议 true）。</param>
        public static PatchStatus Apply(string exePath, int port, bool keepBackup = true)
        {
            var st = new PatchStatus();

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                st.Message = "找不到 AyuGram.exe：" + exePath;
                return st;
            }

            // 确保 AyuGram 没在运行，否则文件被占用
            if (IsFileLocked(exePath))
            {
                st.Message = "AyuGram.exe 正在运行，请先完全退出 AyuGram 再打补丁。";
                return st;
            }

            string proxyUrl = BuildProxyUrl(port);
            if (!FitsInPlace(proxyUrl))
            {
                st.Message = $"代理地址太长（{proxyUrl.Length} > {OriginalUrl.Length} 字节），无法原地替换。";
                return st;
            }

            byte[] data = File.ReadAllBytes(exePath);
            var needle = Encoding.ASCII.GetBytes(OriginalUrl);
            var offsets = FindAll(data, needle);

            if (offsets.Count == 0)
            {
                var cur = Check(exePath);
                st.Message = cur.Patched
                    ? "已经是打过补丁的状态，无需重复操作。"
                    : "在文件中找不到原始地址，可能版本不同，未做修改。";
                return st;
            }

            if (offsets.Count > 1)
            {
                st.Message = $"原始地址出现 {offsets.Count} 次（预期 1 次），为避免误改已放弃。";
                return st;
            }

            int offset = offsets[0];

            // 备份
            string backup = exePath + BackupSuffix;
            if (keepBackup && !File.Exists(backup))
            {
                try
                {
                    File.Copy(exePath, backup, overwrite: false);
                }
                catch (Exception ex)
                {
                    st.Message = "创建备份失败，已放弃修改：" + ex.Message;
                    return st;
                }
            }

            try
            {
                var replacement = Encoding.ASCII.GetBytes(proxyUrl);

                // 写入新地址
                Array.Copy(replacement, 0, data, offset, replacement.Length);

                // 用 \0 补齐剩余部分，保证 C 字符串在这里结束
                for (int i = offset + replacement.Length; i < offset + needle.Length; i++)
                {
                    data[i] = 0;
                }

                WriteAtomically(exePath, data);
                st.Patched = true;
                st.BackupExists = File.Exists(backup);
                st.CurrentUrl = proxyUrl;
                st.Message = $"补丁已写入：{OriginalUrl}  →  {proxyUrl}";
            }
            catch (Exception ex)
            {
                st.Message = "写入补丁失败：" + ex.Message;
            }

            return st;
        }

        /// <summary>
        /// 原子写回：先写同目录临时文件，再 File.Replace 覆盖。
        /// 直接覆盖上百 MB 的 exe 一旦中途崩溃 / 断电会把客户端写坏，
        /// File.Replace 保证要么旧内容、要么新内容，不会出现半截文件。
        /// </summary>
        private static void WriteAtomically(string exePath, byte[] data)
        {
            string tmp = exePath + ".ayutranslate.tmp";
            try
            {
                File.WriteAllBytes(tmp, data);
                File.Replace(tmp, exePath, null);
            }
            catch (PlatformNotSupportedException)
            {
                // 个别文件系统不支持 Replace，退回直接写（保持旧行为）
                File.WriteAllBytes(exePath, data);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>从备份还原。</summary>
        public static PatchStatus Restore(string exePath)
        {
            var st = new PatchStatus();
            string backup = exePath + BackupSuffix;

            if (!File.Exists(backup))
            {
                st.Message = "没有找到备份文件，无法还原：" + backup;
                return st;
            }

            if (IsFileLocked(exePath))
            {
                st.Message = "AyuGram.exe 正在运行，请先完全退出 AyuGram 再还原。";
                return st;
            }

            try
            {
                // 同样走临时文件 + Replace，避免还原过程被打断时损坏 exe
                string tmp = exePath + ".ayutranslate.tmp";
                File.Copy(backup, tmp, overwrite: true);
                File.Replace(tmp, exePath, null);

                st.OriginalOccurrences = 1;
                st.BackupExists = true;
                st.Patched = false;
                st.CurrentUrl = OriginalUrl;
                st.Message = "已从备份还原为原始文件。";
            }
            catch (Exception ex)
            {
                st.Message = "还原失败：" + ex.Message;
            }

            return st;
        }

        /// <summary>删除备份文件（还原之后清理）。</summary>
        public static void RemoveBackup(string exePath)
        {
            try
            {
                string backup = exePath + BackupSuffix;
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (Exception ex)
            {
                Log.Debug("删除备份失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------- 工具

        private static string ReadPatchedUrl(byte[] data)
        {
            var prefix = Encoding.ASCII.GetBytes("http://127.0.0.1:");
            var offsets = FindAll(data, prefix);
            if (offsets.Count == 0) return "(未知)";

            int start = offsets[0];
            int end = start;
            while (end < data.Length && data[end] != 0 && end - start < 256) end++;
            return Encoding.ASCII.GetString(data, start, end - start);
        }

        private static int CountOccurrences(byte[] haystack, byte[] needle) => FindAll(haystack, needle).Count;

        private static System.Collections.Generic.List<int> FindAll(byte[] haystack, byte[] needle)
        {
            var result = new System.Collections.Generic.List<int>();
            if (needle.Length == 0 || haystack.Length < needle.Length) return result;

            int limit = haystack.Length - needle.Length;
            for (int i = 0; i <= limit; i++)
            {
                if (haystack[i] != needle[0]) continue;
                bool match = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match)
                {
                    result.Add(i);
                    i += needle.Length - 1;
                }
            }
            return result;
        }

        private static bool IsFileLocked(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
