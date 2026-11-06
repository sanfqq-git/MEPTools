using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Forms;

namespace MEPTools.Shared
{
    public class SpaceKeyFinisher : IDisposable
    {
        private static class NativeMethods
        {
            // 引入 keybd_event 模拟真实系统按键
            [DllImport("user32.dll")]
            public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

            public const int WM_KEYDOWN = 0x0100;
            public const int WM_SYSKEYDOWN = 0x0104;
            
            public const byte VK_RETURN = 0x0D; // 回车键
            public const uint KEYEVENTF_KEYUP = 0x0002;
        }

        private readonly Keys _targetKey;
        private IntPtr _revitWindowHandle = IntPtr.Zero;
        private HwndSource _hwndSource;
        private bool _disposed;

        public SpaceKeyFinisher(Keys targetKey = Keys.Space, params string[] buttonTexts)
        {
            _targetKey = targetKey;
        }

        public void Enable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpaceKeyFinisher));
            if (_hwndSource != null) return;

            _revitWindowHandle = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (_revitWindowHandle == IntPtr.Zero) return;

            var source = HwndSource.FromHwnd(_revitWindowHandle);
            if (source != null)
            {
                _hwndSource = source;
                _hwndSource.AddHook(WndProc);
            }
        }

        public void Disable()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            _revitWindowHandle = IntPtr.Zero;
        }

        public void Dispose() => Disable();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                int vkCode = wParam.ToInt32();
                if (vkCode == (int)_targetKey)
                {
                    bool isRepeat = (lParam.ToInt64() & (1 << 30)) != 0;
                    if (!isRepeat)
                    {
                        TriggerEnterKey();
                        handled = true; // 吃掉空格
                    }
                }
            }
            return IntPtr.Zero;
        }

        private void TriggerEnterKey()
        {
            try
            {
                // 使用底层 API 模拟一次真实的回车按下和抬起
                NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(10); // 极短的延迟，确保系统识别
                NativeMethods.keybd_event(NativeMethods.VK_RETURN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
                
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 拦截到空格 → 已模拟发送 Enter (keybd_event)。");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SpaceKeyFinisher.TriggerEnterKey", ex);
            }
        }
    }
}