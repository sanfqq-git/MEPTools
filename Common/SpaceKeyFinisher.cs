using Gma.System.MouseKeyHook;
using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace RevitPlugins.Services
{
    /// <summary>
    /// 监听指定按键，按下时自动点击 Revit 当前模态对话框中的“完成”按钮（或自定义按钮）。
    /// 通常用于在 PickObjects 等操作中用按键结束选择，代替鼠标点击。
    /// </summary>
    public class SpaceKeyFinisher : IDisposable
    {
        // ---------- 私有 Windows API 辅助类 ----------
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
            public static extern int SendMessage(IntPtr hWnd, uint Msg, int wParam, int lParam);

            public const uint BM_CLICK = 0x00F5;
        }

        // ---------- 字段 ----------
        private IKeyboardMouseEvents _globalHook;
        private Thread _hookThread;
        private bool _disposed;
        private readonly Keys _targetKey;
        private readonly string[] _targetButtonTexts;

        // ---------- 构造函数 ----------
        /// <summary>
        /// 创建按键监听器实例。
        /// </summary>
        /// <param name="targetKey">要监听的按键，默认为空格键。</param>
        /// <param name="buttonTexts">要点击的按钮可能显示的文本（用于多语言匹配）。默认尝试“完成”、“Finish”、“Complete”。</param>
        public SpaceKeyFinisher(Keys targetKey = Keys.Space, params string[] buttonTexts)
        {
            _targetKey = targetKey;
            _targetButtonTexts = buttonTexts?.Length > 0 ? buttonTexts : new[] { "完成", "Finish", "Complete" };
        }

        // ---------- 公共方法 ----------
        /// <summary>
        /// 启动监听。应在需要支持按键结束的操作前调用（例如 PickObjects 之前）。
        /// </summary>
        public void Enable()
        {
            if (_hookThread != null && _hookThread.IsAlive)
                return;

            _hookThread = new Thread(() =>
            {
                _globalHook = Hook.AppEvents();
                _globalHook.KeyUp += OnKeyUp;
                Application.Run(); // 进入消息循环，阻塞直到调用 Application.ExitThread()
            })
            {
                IsBackground = true,
                Name = "KeyboardHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA); // 必须为 STA
            _hookThread.Start();
        }

        /// <summary>
        /// 停止监听并释放资源。应在操作结束后调用（例如 PickObjects 之后）。
        /// </summary>
        public void Disable()
        {
            if (_disposed) return;
            if (_hookThread != null && _hookThread.IsAlive)
            {
                Application.ExitThread();   // 退出消息循环
                _hookThread.Join(1000);      // 等待线程结束
                _hookThread = null;
            }
            _globalHook?.Dispose();
            _globalHook = null;
            _disposed = true;
        }

        // ---------- IDisposable 实现 ----------
        public void Dispose() => Disable();

        // ---------- 私有方法 ----------
        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == _targetKey)
            {
                ClickRevitButton();
            }
        }

        private void ClickRevitButton()
        {
            // 获取 Revit 主窗口句柄（需要引用 AdWindows.dll）
            IntPtr revitWindow = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (revitWindow == IntPtr.Zero) return;

            IntPtr targetButton = IntPtr.Zero;
            NativeMethods.EnumChildWindows(revitWindow, (hwnd, lParam) =>
            {
                StringBuilder text = new StringBuilder(256);
                NativeMethods.GetWindowText(hwnd, text, text.Capacity);
                StringBuilder cls = new StringBuilder(256);
                NativeMethods.GetClassName(hwnd, cls, cls.Capacity);

                string buttonText = text.ToString();
                string className = cls.ToString();

                // 检查是否匹配任一目标文本，且类名包含 "Button"
                bool textMatches = false;
                foreach (var candidate in _targetButtonTexts)
                {
                    if (buttonText.Equals(candidate, StringComparison.Ordinal))
                    {
                        textMatches = true;
                        break;
                    }
                }

                if (textMatches && className.Contains("Button"))
                {
                    targetButton = hwnd;
                    return false; // 停止枚举
                }
                return true;
            }, 0);

            if (targetButton != IntPtr.Zero)
            {
                NativeMethods.SendMessage(targetButton, NativeMethods.BM_CLICK, 0, 0);
            }
        }
    }
}