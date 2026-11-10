using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Forms;

namespace MEPTools.Shared
{
    /// <summary>
    /// 监听指定按键，按下时自动点击 Revit 当前模态对话框中的“完成”按钮。
    /// 使用系统级 RegisterHotKey 替代全局钩子，彻底避免多插件环境下的钩子冲突崩溃。
    /// </summary>
    public class SpaceKeyFinisher : IDisposable
    {
        private static class NativeMethods
        {
            public delegate bool EnumWindowsProc(IntPtr hWnd, int lParam);

            [DllImport("user32.dll")]
            public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, int lParam);

            [DllImport("user32.dll", CharSet = CharSet.Auto)]
            public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

            [DllImport("user32.dll", EntryPoint = "SendMessage")]
            public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            public const uint BM_CLICK = 0x00F5;
            public const int WM_HOTKEY = 0x0312;
        }

        private readonly Keys _targetKey;
        private readonly string[] _targetButtonTexts;
        
        private IntPtr _revitWindowHandle = IntPtr.Zero;
        private HwndSource _hwndSource;
        private readonly int _hotKeyId;
        private bool _disposed;
        private bool _isRegistered;

        public SpaceKeyFinisher(Keys targetKey = Keys.Space, params string[] buttonTexts)
        {
            _targetKey = targetKey;
            _targetButtonTexts = buttonTexts?.Length > 0
                ? buttonTexts
                : MEPConfig.GetStringArray("SpaceKey.ButtonTexts", ';', new[] { "完成", "Finish", "Complete" });
            
            _hotKeyId = this.GetHashCode();
        }

        public void Enable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpaceKeyFinisher));
            if (_isRegistered) return;

            _revitWindowHandle = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (_revitWindowHandle == IntPtr.Zero) 
            {
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 无法获取 Revit 主窗口句柄。");
                return;
            }

            bool registered = NativeMethods.RegisterHotKey(
                _revitWindowHandle, 
                _hotKeyId, 
                0, 
                (uint)_targetKey);

            if (!registered)
            {
                MEPLogger.Instance.AppendLine($"[SpaceKeyFinisher] 注册热键失败。可能被其他程序占用。错误码: {Marshal.GetLastWin32Error()}");
                return;
            }
            _isRegistered = true;

            var source = HwndSource.FromHwnd(_revitWindowHandle);
            if (source == null)
            {
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 无法获取 HwndSource。");
                Disable();
                return;
            }
            _hwndSource = source;
            _hwndSource.AddHook(WndProc);
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

            if (_isRegistered && _revitWindowHandle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_revitWindowHandle, _hotKeyId);
                _isRegistered = false;
            }

            _revitWindowHandle = IntPtr.Zero;
        }

        public void Dispose() => Disable();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                if (wParam.ToInt32() == _hotKeyId)
                {
                    OnHotKeyPressed();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private void OnHotKeyPressed()
        {
            try
            {
                if (ClickRevitButton())
                    MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 热键触发 → 已点击完成按钮。");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SpaceKeyFinisher.OnHotKeyPressed", ex);
            }
        }

        private bool ClickRevitButton()
        {
            if (_revitWindowHandle == IntPtr.Zero) return false;

            IntPtr targetButton = IntPtr.Zero;
            NativeMethods.EnumChildWindows(_revitWindowHandle, (hwnd, lParam) =>
            {
                var text = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, text, text.Capacity);
                var cls = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, cls, cls.Capacity);

                if (cls.ToString().IndexOf("Button", StringComparison.OrdinalIgnoreCase) < 0)
                    return true;

                string buttonText = text.ToString();
                foreach (var candidate in _targetButtonTexts)
                {
                    if (buttonText.Equals(candidate, StringComparison.Ordinal))
                    {
                        targetButton = hwnd;
                        return false;
                    }
                }
                return true;
            }, 0);

            if (targetButton == IntPtr.Zero) return false;
            NativeMethods.SendMessage(targetButton, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
    }
}