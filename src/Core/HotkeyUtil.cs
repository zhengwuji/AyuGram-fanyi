using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AyuTranslate.Core
{
    /// <summary>把热键字符串解析成 WPF 的 ModifierKeys + Key。</summary>
    public static class HotkeyUtil
    {
        public sealed class Hotkey
        {
            public ModifierKeys Modifiers = ModifierKeys.None;
            public Key Key = Key.None;
            public bool IsValid => Key != Key.None;

            public override string ToString()
            {
                var parts = new List<string>();
                if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
                if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
                if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
                if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
                parts.Add(Key.ToString());
                return string.Join("+", parts);
            }
        }

        public static Hotkey Parse(string spec)
        {
            var hk = new Hotkey();
            if (string.IsNullOrWhiteSpace(spec)) return hk;

            foreach (var raw in spec.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;

                switch (p.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        hk.Modifiers |= ModifierKeys.Control;
                        continue;
                    case "alt":
                        hk.Modifiers |= ModifierKeys.Alt;
                        continue;
                    case "shift":
                        hk.Modifiers |= ModifierKeys.Shift;
                        continue;
                    case "win":
                    case "windows":
                    case "meta":
                        hk.Modifiers |= ModifierKeys.Windows;
                        continue;
                }

                if (Enum.TryParse(p, true, out Key k))
                {
                    hk.Key = k;
                }
                else if (p.Length >= 2 && (p[0] == 'F' || p[0] == 'f') &&
                         int.TryParse(p.Substring(1), out int fn) && fn >= 1 && fn <= 24)
                {
                    hk.Key = (Key)((int)Key.F1 + fn - 1);
                }
                else if (p.Length == 1)
                {
                    var conv = new KeyConverter();
                    try
                    {
                        hk.Key = (Key)conv.ConvertFromInvariantString(p.ToUpperInvariant());
                    }
                    catch { }
                }
            }

            return hk;
        }

        /// <summary>从键盘事件构造可读的热键字符串。</summary>
        public static string Describe(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>把颜色字符串解析成 WPF 颜色，#AARRGGBB 或 #RRGGBB 都支持。</summary>
        public static Color ParseColor(string s, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            try
            {
                string t = s.Trim();
                if (!t.StartsWith("#")) t = "#" + t;

                if (t.Length == 7) // #RRGGBB
                {
                    return (Color)ColorConverter.ConvertFromString(t);
                }
                if (t.Length == 9) // #AARRGGBB
                {
                    byte a = byte.Parse(t.Substring(1, 2), NumberStyles.HexNumber);
                    byte r = byte.Parse(t.Substring(3, 2), NumberStyles.HexNumber);
                    byte g = byte.Parse(t.Substring(5, 2), NumberStyles.HexNumber);
                    byte b = byte.Parse(t.Substring(7, 2), NumberStyles.HexNumber);
                    return Color.FromArgb(a, r, g, b);
                }
                return (Color)ColorConverter.ConvertFromString(t);
            }
            catch
            {
                return fallback;
            }
        }

        public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
