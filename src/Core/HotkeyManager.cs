using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AyuTranslate.Core
{
    /// <summary>全局热键注册（RegisterHotKey），需要窗口句柄接收 WM_HOTKEY。</summary>
    public sealed class HotkeyManager : IDisposable
    {
        public const int WM_HOTKEY = 0x0312;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        private readonly IntPtr _hwnd;
        private readonly Dictionary<int, string> _registered = new Dictionary<int, string>();
        private int _nextId = 0xA000;
        private bool _disposed;

        /// <summary>热键触发回调：参数是注册时给的 action 名称。</summary>
        public event Action<string> HotkeyPressed;

        public HotkeyManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        /// <summary>已成功注册的热键说明。</summary>
        public IReadOnlyDictionary<int, string> Registered => _registered;

        public List<string> RegistrationFailures { get; } = new List<string>();

        /// <summary>尝试注册一个热键，返回热键 id（失败返回 -1）。</summary>
        public int Register(string spec, string action)
        {
            var hk = HotkeyUtil.Parse(spec);
            if (!hk.IsValid) return -1;

            uint mods = 0;
            if ((hk.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0) mods |= MOD_ALT;
            if ((hk.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0) mods |= MOD_CONTROL;
            if ((hk.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0) mods |= MOD_SHIFT;
            if ((hk.Modifiers & System.Windows.Input.ModifierKeys.Windows) != 0) mods |= MOD_WIN;
            mods |= MOD_NOREPEAT;

            uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(hk.Key);

            int id = _nextId++;
            bool ok = RegisterHotKey(_hwnd, id, mods, vk);
            if (ok)
            {
                _registered[id] = action;
                Log.Info($"已注册热键 {hk} -> {action}");
                return id;
            }

            string msg = $"热键 {hk} 注册失败（可能已被其他程序占用）";
            RegistrationFailures.Add(msg);
            Log.Warn(msg);
            return -1;
        }

        /// <summary>处理 WM_HOTKEY 消息。</summary>
        public bool HandleMessage(int msg, IntPtr wParam)
        {
            if (msg != WM_HOTKEY) return false;
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out string action))
            {
                try { HotkeyPressed?.Invoke(action); }
                catch (Exception ex) { Log.Exception("热键回调异常 " + action, ex); }
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var id in _registered.Keys)
            {
                try { UnregisterHotKey(_hwnd, id); } catch { }
            }
            _registered.Clear();
        }
    }
}
