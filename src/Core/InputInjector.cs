using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AyuTranslate.Core
{
    /// <summary>
    /// 把文本写进 AyuGram 的输入框。
    ///
    /// 首选「剪贴板 + Ctrl+V」：Qt 控件对 WM_CHAR 的接收并不可靠，而粘贴几乎总是有效。
    /// 备选 WM_CHAR 直投（不需要动剪贴板，但成功率低一些）。
    /// </summary>
    internal static class InputInjector
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern short VkKeyScan(char ch);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const byte VK_CONTROL = 0x11;
        private const byte VK_RETURN = 0x0D;
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int SW_RESTORE = 9;

        private static readonly object ClipboardGate = new object();

        internal static bool Inject(TargetWindow tw, AppConfig cfg, string text)
        {
            if (tw == null || !tw.IsWindowAlive()) return false;

            try
            {
                FocusTarget(tw);

                bool ok;
                if (cfg.InputMethod == InputMethod.ClipboardPaste)
                    ok = PasteViaClipboard(text);
                else
                    ok = SendChars(text);

                if (!ok) return false;

                if (cfg.InputSendMode == InputSendMode.FillAndSend)
                {
                    Thread.Sleep(120);
                    SendVirtualKey(VK_RETURN);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("写入输入框失败", ex);
                return false;
            }
        }

        private static void FocusTarget(TargetWindow tw)
        {
            try
            {
                if (Native.IsIconic(tw.Handle)) ShowWindow(tw.Handle, SW_RESTORE);

                var fg = GetForegroundWindow();
                uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint curThread = GetCurrentThreadId();

                bool attached = false;
                if (fgThread != curThread && fgThread != 0)
                {
                    attached = AttachThreadInput(curThread, fgThread, true);
                }

                try
                {
                    BringWindowToTop(tw.Handle);
                    SetForegroundWindow(tw.Handle);
                    SetFocus(tw.Handle);
                }
                finally
                {
                    if (attached) AttachThreadInput(curThread, fgThread, false);
                }

                Thread.Sleep(90);
            }
            catch (Exception ex)
            {
                Log.Debug("聚焦目标窗口失败：" + ex.Message);
            }
        }

        private static bool PasteViaClipboard(string text)
        {
            string saved = null;
            bool hadText = false;

            try
            {
                var app = Application.Current;
                if (app?.Dispatcher != null)
                {
                    app.Dispatcher.Invoke(() =>
                    {
                        try
                        {
                            if (Clipboard.ContainsText())
                            {
                                saved = Clipboard.GetText();
                                hadText = true;
                            }
                            Clipboard.SetText(text);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("写剪贴板失败：" + ex.Message);
                        }
                    });
                }
                else
                {
                    Clipboard.SetText(text);
                }
            }
            catch (Exception ex)
            {
                Log.Debug("剪贴板操作异常：" + ex.Message);
            }

            Thread.Sleep(80);
            SendCombo(VK_CONTROL, VK_V);
            Thread.Sleep(140);

            // 尽力恢复原剪贴板内容
            try
            {
                var app = Application.Current;
                if (app?.Dispatcher != null)
                {
                    app.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (hadText && saved != null) Clipboard.SetText(saved);
                        }
                        catch { }
                    }));
                }
            }
            catch { }

            return true;
        }

        private static bool SendChars(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool any = false;

            foreach (char raw in text)
            {
                char c = raw == '\n' ? '\r' : raw;
                bool ok = Native.PostMessageW(GetFocusTarget(), Native.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                if (ok) any = true;
                Thread.Sleep(6);
            }
            return any;
        }

        private static IntPtr GetFocusTarget()
        {
            var fg = GetForegroundWindow();
            return fg;
        }

        private static void SendCombo(byte modifier, byte key)
        {
            keybd_event(modifier, 0, 0, UIntPtr.Zero);
            Thread.Sleep(25);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(35);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(modifier, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static void SendVirtualKey(byte vk)
        {
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(30);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }
}
