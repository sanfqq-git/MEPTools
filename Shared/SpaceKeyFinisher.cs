using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Forms;

namespace MEPTools.Shared
{
    /// <summary>
    /// 运行级别枚举
    /// </summary>
    public enum SpaceKeyLevel
    {
        None = 0,
        Level1_WindowHook = 1, // 窗口消息拦截
        Level2_HotKey = 2      // 系统热键
    }

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
            public const int WM_KEYDOWN = 0x0100;
            public const int WM_SYSKEYDOWN = 0x0104;
        }

        private readonly Keys _targetKey;
        private readonly string[] _targetButtonTexts;
        
        private IntPtr _revitWindowHandle = IntPtr.Zero;
        private HwndSource _hwndSource;
        private readonly int _hotKeyId;
        private bool _disposed;
        
        private bool _isHookActive;
        private bool _isHotKeyActive;

        /// <summary>
        /// 当前生效的级别（如果在 WndProc 里触发，可在此处得知来源）
        /// </summary>
        public SpaceKeyLevel ActiveLevel { get; private set; } = SpaceKeyLevel.None;

        public SpaceKeyFinisher(Keys targetKey = Keys.Space, params string[] buttonTexts)
        {
            _targetKey = targetKey;
            _targetButtonTexts = buttonTexts?.Length > 0
                ? buttonTexts
                : new[] { "完成", "Finish", "Complete" };
            
            _hotKeyId = this.GetHashCode();
        }

        public void Enable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpaceKeyFinisher));
            if (_isHookActive || _isHotKeyActive) return;

            _revitWindowHandle = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (_revitWindowHandle == IntPtr.Zero) 
            {
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 无法获取 Revit 主窗口句柄。");
                return;
            }

            // === 第一级：窗口消息拦截 (WM_KEYDOWN) ===
            var source = HwndSource.FromHwnd(_revitWindowHandle);
            if (source != null)
            {
                _hwndSource = source;
                _hwndSource.AddHook(WndProc);
                _isHookActive = true;
                ActiveLevel = SpaceKeyLevel.Level1_WindowHook;
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] ✅ 第一级：窗口键盘消息拦截已启用。");
            }

            // === 第二级：系统热键兜底 ===
            bool registered = NativeMethods.RegisterHotKey(
                _revitWindowHandle, 
                _hotKeyId, 
                0, 
                (uint)_targetKey);

            if (registered)
            {
                _isHotKeyActive = true;
                // 如果一级没成功，二级作为主级别
                if (!_isHookActive) ActiveLevel = SpaceKeyLevel.Level2_HotKey;
                
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] ✅ 第二级：系统热键注册成功（作为兜底）。");
            }
            else
            {
                MEPLogger.Instance.AppendLine($"[SpaceKeyFinisher] ⚠️ 第二级：注册系统热键失败(可能被占用)，完全依赖第一级。错误码: {Marshal.GetLastWin32Error()}");
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
                _isHookActive = false;
            }

            if (_isHotKeyActive && _revitWindowHandle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_revitWindowHandle, _hotKeyId);
                _isHotKeyActive = false;
            }

            _revitWindowHandle = IntPtr.Zero;
            ActiveLevel = SpaceKeyLevel.None;
        }

        public void Dispose() => Disable();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 【级别 1】 窗口键盘消息触发
            if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
            {
                int vkCode = wParam.ToInt32();
                if (vkCode == (int)_targetKey)
                {
                    bool isRepeat = (lParam.ToInt64() & (1 << 30)) != 0;
                    if (!isRepeat)
                    {
                        OnKeyTriggered("第一级(窗口消息)");
                        handled = true;
                    }
                }
            }
            // 【级别 2】 系统热键触发
            else if (msg == NativeMethods.WM_HOTKEY)
            {
                if (wParam.ToInt32() == _hotKeyId)
                {
                    OnKeyTriggered("第二级(系统热键)");
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private void OnKeyTriggered(string triggerSource)
        {
            try
            {
                if (ClickRevitButton())
                    MEPLogger.Instance.AppendLine($"[SpaceKeyFinisher] 触发按键 {_targetKey} → 来源: {triggerSource}，已点击完成按钮。");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SpaceKeyFinisher.OnKeyTriggered", ex);
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