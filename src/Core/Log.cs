using System;
using System.IO;
using System.Text;

namespace AyuTranslate.Core
{
    /// <summary>极简日志。写入 %APPDATA%\AyuTranslate\logs\。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _directory;
        private static bool _verbose;
        private static StreamWriter _writer;
        private static string _writerFile;

        public static bool Verbose
        {
            get => _verbose;
            set => _verbose = value;
        }

        public static string FilePath
        {
            get
            {
                lock (Gate)
                {
                    EnsureWriter();
                    return _writerFile;
                }
            }
        }

        public static void Init(string directory, bool verbose)
        {
            lock (Gate)
            {
                _verbose = verbose;
                _directory = directory;
            }
        }

        public static void Info(string message) => Write("INFO ", message);

        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message) => Write("ERROR", message);

        public static void Debug(string message)
        {
            if (_verbose) Write("DEBUG", message);
        }

        public static void Exception(string context, Exception ex)
        {
            var sb = new StringBuilder();
            sb.Append(context).Append(" -> ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 4)
            {
                sb.Append(" | inner: ").Append(inner.GetType().Name).Append(": ").Append(inner.Message);
                inner = inner.InnerException;
            }
            if (ex.StackTrace != null)
                sb.Append(Environment.NewLine).Append(ex.StackTrace);
            Write("ERROR", sb.ToString());
        }

        private static string PathForNow()
        {
            string dir = string.IsNullOrWhiteSpace(_directory) ? AppConfig.DefaultLogDirectory : _directory;
            return Path.Combine(dir, "ayutranslate-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }

        /// <summary>
        /// 持有常开的 StreamWriter（替代每行 AppendAllText 的开关文件），
        /// 日期变化时自动滚动到新文件 —— 托盘常驻进程会跨天运行。
        /// </summary>
        private static void EnsureWriter()
        {
            string path = PathForNow();
            if (_writer != null && string.Equals(_writerFile, path, StringComparison.OrdinalIgnoreCase)) return;

            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _writerFile = null;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
                _writerFile = path;
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }

        private static void Write(string level, string message)
        {
            string line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                DateTime.Now, level, message);
            lock (Gate)
            {
                try
                {
                    EnsureWriter();
                    _writer?.WriteLine(line);
                }
                catch
                {
                    // 忽略
                }
            }
            System.Diagnostics.Debug.WriteLine(line);
        }
    }
}
