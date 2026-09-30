using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AyuTranslate.Core
{
    public enum ScriptKind
    {
        Unknown = 0,
        Han,
        Latin,
        Cyrillic,
        Arabic,
        Hebrew,
        Hiragana,
        Katakana,
        Hangul,
        Thai,
        Devanagari,
        Greek,
    }

    /// <summary>文本清洗、语言/字符集判定、缓存键生成。</summary>
    public static class TextUtil
    {
        // ---------- 清洗 ----------

        /// <summary>去掉零宽字符、双向控制符（Telegram 会在消息前塞 \u200E）。</summary>
        public static string StripInvisible(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c == '\u200B' || c == '\u200C' || c == '\u200D' || c == '\u200E' ||
                    c == '\u200F' || c == '\u202A' || c == '\u202B' || c == '\u202C' ||
                    c == '\u202D' || c == '\u202E' || c == '\u2060' || c == '\uFEFF' ||
                    c == '\u2066' || c == '\u2067' || c == '\u2068' || c == '\u2069')
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Windows OCR 会在 CJK 字之间插空格（"你 好 世 界"），这里把它合回去；
        /// 但保留 CJK 与拉丁/数字之间的空格。
        /// </summary>
        public static string CollapseCjkSpaces(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == ' ' && sb.Length > 0 && i + 1 < s.Length)
                {
                    char prev = sb[sb.Length - 1];
                    char next = s[i + 1];
                    if (IsCjk(prev) && IsCjk(next)) continue;
                    if (IsCjk(prev) && IsFullWidthPunctuation(next)) continue;
                    if (IsFullWidthPunctuation(prev) && IsCjk(next)) continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static bool IsCjk(char c)
        {
            return (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 统一表意
                   (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
                   (c >= 0xF900 && c <= 0xFAFF) ||   // 兼容表意
                   (c >= 0x3040 && c <= 0x30FF) ||   // 假名
                   (c >= 0xAC00 && c <= 0xD7AF) ||   // 谚文
                   (c >= 0x3000 && c <= 0x303F);     // CJK 标点
        }

        public static bool IsFullWidthPunctuation(char c)
        {
            return c == '，' || c == '。' || c == '！' || c == '？' || c == '：' || c == '；' ||
                   c == '、' || c == '“' || c == '”' || c == '‘' || c == '’' || c == '（' ||
                   c == '）' || c == '《' || c == '》' || c == '【' || c == '】' || c == '—' ||
                   c == '…' || c == '·';
        }

        /// <summary>把多行文本规范化：统一换行、去尾部空格、压缩空行。</summary>
        public static string NormalizeWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = s.Split('\n');
            var sb = new StringBuilder();
            bool lastBlank = false;
            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                bool blank = line.Trim().Length == 0;
                if (blank && lastBlank) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(line);
                lastBlank = blank;
            }
            return sb.ToString().Trim('\n', ' ');
        }

        /// <summary>完整清洗流程（OCR 结果用）。</summary>
        public static string CleanOcr(string s)
        {
            s = StripInvisible(s);
            s = CollapseCjkSpaces(s);
            s = NormalizeWhitespace(s);
            return s;
        }

        // ---------- 有效性判断 ----------

        /// <summary>是否只是纯符号 / 纯数字 / 单个字符，不值得翻译。</summary>
        public static bool IsNoise(string s, int minLength)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            string t = s.Trim();
            if (t.Length < Math.Max(1, minLength)) return true;

            int letterOrDigit = 0;
            int letter = 0;
            int cjk = 0;
            foreach (char c in t)
            {
                if (char.IsLetterOrDigit(c)) letterOrDigit++;
                if (char.IsLetter(c)) letter++;
                if (IsCjk(c)) cjk++;
            }
            if (letterOrDigit == 0) return true;

            // 只有一个字母（比如 "A"、"k"）通常是头像/时间等噪声
            if (letter <= 1 && cjk <= 1 && t.Length <= 2) return true;

            // 大部分是数字和标点
            if (letter * 3 < t.Length && cjk == 0) return true;

            return false;
        }

        /// <summary>是否看起来像时间戳 / 未读数等 UI 噪声。</summary>
        public static bool LooksLikeTimestampOrCounter(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = s.Trim();
            if (t.Length > 12) return false;
            // 14:08 / 上午 5:04 / 2026-09-16 星期三
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,2}:\d{2}(\s*[APap][Mm])?$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{1,2}$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d+$")) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(t, @"^\d{4}-\d{2}-\d{2}")) return true;
            return false;
        }

        // ---------- 字符集判定 ----------

        public static ScriptKind DominantScript(string s, out double ratio)
        {
            ratio = 0;
            if (string.IsNullOrWhiteSpace(s)) return ScriptKind.Unknown;

            var counts = new Dictionary<ScriptKind, int>();
            int letters = 0;
            foreach (char c in s)
            {
                var k = ScriptOf(c);
                if (k == ScriptKind.Unknown) continue;
                letters++;
                counts.TryGetValue(k, out int n);
                counts[k] = n + 1;
            }
            if (letters == 0) return ScriptKind.Unknown;

            ScriptKind best = ScriptKind.Unknown;
            int bestN = 0;
            foreach (var kv in counts)
            {
                if (kv.Value > bestN)
                {
                    bestN = kv.Value;
                    best = kv.Key;
                }
            }
            ratio = (double)bestN / letters;
            return best;
        }

        public static ScriptKind ScriptOf(char c)
        {
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF))
                return ScriptKind.Han;
            if (c >= 0x3040 && c <= 0x309F) return ScriptKind.Hiragana;
            if (c >= 0x30A0 && c <= 0x30FF) return ScriptKind.Katakana;
            if (c >= 0xAC00 && c <= 0xD7AF) return ScriptKind.Hangul;
            if ((c >= 0x0041 && c <= 0x005A) || (c >= 0x0061 && c <= 0x007A) ||
                (c >= 0x00C0 && c <= 0x024F) || (c >= 0x1E00 && c <= 0x1EFF))
                return ScriptKind.Latin;
            if (c >= 0x0400 && c <= 0x04FF) return ScriptKind.Cyrillic;
            if (c >= 0x0600 && c <= 0x06FF) return ScriptKind.Arabic;
            if (c >= 0x0590 && c <= 0x05FF) return ScriptKind.Hebrew;
            if (c >= 0x0E00 && c <= 0x0E7F) return ScriptKind.Thai;
            if (c >= 0x0900 && c <= 0x097F) return ScriptKind.Devanagari;
            if ((c >= 0x0370 && c <= 0x03FF) || (c >= 0x1F00 && c <= 0x1FFF)) return ScriptKind.Greek;
            return ScriptKind.Unknown;
        }

        /// <summary>判断文本是否已经是目标语言（用于跳过）。</summary>
        public static bool IsAlreadyTargetLanguage(string text, string targetLanguage)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;
            var target = LanguageMap.ScriptOfLanguage(targetLanguage);
            var got = DominantScript(text, out double ratio);
            if (got == ScriptKind.Unknown) return true;

            // 日文里汉字 + 假名混排，汉字比例会很高，需要特判
            if (target == ScriptKind.Han && HasKana(text)) return false;
            if (target == ScriptKind.Hiragana || target == ScriptKind.Katakana)
                return HasKana(text);

            return got == target && ratio >= 0.55;
        }

        public static bool HasKana(string s)
        {
            foreach (char c in s)
            {
                if ((c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)) return true;
            }
            return false;
        }

        // ---------- 缓存键 ----------

        public static string CacheKey(string text, string source, string target)
        {
            string norm = NormalizeWhitespace(StripInvisible(text));
            return (target ?? "") + "\u0001" + (source ?? "auto") + "\u0001" + norm;
        }

        /// <summary>
        /// 「宽松」缓存键：去掉所有空白与标点、统一大小写、并抹掉 OCR 常见的
        /// 易混字符差异（0/O、1/l/I、|/I 等）。
        ///
        /// 屏幕 OCR 每次结果都会有细微抖动（末尾多一个 "：" 、"0" 变成 "F"…），
        /// 严格键会让同一句话反复请求接口。用宽松键可以显著减少重复请求，
        /// 对翻译质量几乎没有影响（标点差异不影响语义）。
        /// </summary>
        public static string LooseCacheKey(string text, string source, string target)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var sb = new StringBuilder(text.Length);
            foreach (char raw in StripInvisible(text))
            {
                char c = char.ToLowerInvariant(raw);

                // 丢掉空白与各类标点/符号
                if (char.IsWhiteSpace(c)) continue;
                if (char.IsPunctuation(c)) continue;
                if (char.IsSymbol(c)) continue;

                // 抹平 OCR 易混字符
                switch (c)
                {
                    case '0': c = 'o'; break;
                    case '1': c = 'l'; break;
                    case '|': c = 'l'; break;
                    case '5': c = 's'; break;
                    case '2': c = 'z'; break;
                    case '8': c = 'b'; break;
                    case '6': c = 'g'; break;
                    case '9': c = 'g'; break;
                }

                sb.Append(c);
            }

            string loose = sb.ToString();

            // 太短的话宽松键区分度不够，直接退化为严格键
            if (loose.Length < 4)
                return CacheKey(text, source, target);

            return (target ?? "") + "\u0001" + (source ?? "auto") + "\u0001~" + loose;
        }

        /// <summary>两个块是否「基本是同一句话」，用于逐帧复用上一次的译文。</summary>
        public static double Similarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1.0;

            string x = NormForCompare(a);
            string y = NormForCompare(b);
            if (x.Length == 0 && y.Length == 0) return 1.0;
            if (x.Length == 0 || y.Length == 0) return 0.0;
            if (string.Equals(x, y, StringComparison.Ordinal)) return 1.0;

            // 短的用编辑距离，长的用 2-gram 集合，兼顾准确与性能
            if (x.Length <= 64 && y.Length <= 64)
            {
                int dist = Levenshtein(x, y);
                int max = Math.Max(x.Length, y.Length);
                return 1.0 - (double)dist / max;
            }

            var ga = Bigrams(x);
            var gb = Bigrams(y);
            if (ga.Count == 0 || gb.Count == 0) return 0.0;
            int inter = 0;
            foreach (var g in ga) if (gb.Contains(g)) inter++;
            return 2.0 * inter / (ga.Count + gb.Count);
        }

        private static string NormForCompare(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char raw in StripInvisible(s))
            {
                char c = char.ToLowerInvariant(raw);
                if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
                switch (c)
                {
                    case '0': c = 'o'; break;
                    case '1': c = 'l'; break;
                    case '|': c = 'l'; break;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static HashSet<string> Bigrams(string s)
        {
            var set = new HashSet<string>();
            for (int i = 0; i + 1 < s.Length; i++)
            {
                set.Add(s.Substring(i, 2));
            }
            if (set.Count == 0) set.Add(s);
            return set;
        }

        private static int Levenshtein(string a, string b)
        {
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                }
                var tmp = prev;
                prev = cur;
                cur = tmp;
            }
            return prev[b.Length];
        }

        /// <summary>粗略估算字符串显示宽度（全角算 2）。</summary>
        public static int DisplayWidth(string s)
        {
            int w = 0;
            foreach (char c in s)
            {
                if (IsCjk(c) || IsFullWidthPunctuation(c)) w += 2;
                else w += 1;
            }
            return w;
        }

        public static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        /// <summary>把翻译结果清理干净（去掉模型可能加的解释、引号）。</summary>
        public static string CleanTranslationOutput(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim();

            // 去掉常见的包裹引号
            if (s.Length >= 2)
            {
                char a = s[0], b = s[s.Length - 1];
                bool quoted = (a == '"' && b == '"') || (a == '\'' && b == '\'') ||
                              (a == '“' && b == '”') || (a == '「' && b == '」') || (a == '『' && b == '』');
                if (quoted) s = s.Substring(1, s.Length - 2).Trim();
            }

            // 去掉 "翻译：" 之类前缀
            foreach (var prefix in new[] { "翻译：", "翻译:", "译文：", "译文:", "Translation:", "translation:" })
            {
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(prefix.Length).Trim();
                    break;
                }
            }

            // 去掉代码块围栏
            if (s.StartsWith("```") && s.EndsWith("```") && s.Length > 6)
            {
                s = s.Substring(3, s.Length - 6).Trim();
            }

            return s;
        }
    }
}
