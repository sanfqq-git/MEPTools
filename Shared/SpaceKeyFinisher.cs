using Gma.System.MouseKeyHook;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MEPTools.Shared
{
    /// <summary>
    /// 监听指定按键，按下时自动点击 Revit 当前模态对话框中的“完成”按钮。
    /// 典型用法：
    ///   using (var f = new SpaceKeyFinisher()) { f.Enable(); try { PickObjects... } finally { f.Disable(); } }
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
            public static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll")]
            public static extern int GetCurrentThreadId();

            public const uint BM_CLICK = 0x00F5;
            public const uint WM_QUIT  = 0x0012;
        }

        private IKeyboardMouseEvents _globalHook;
        private Thread _hookThread;
        private readonly ManualResetEventSlim _threadReady = new ManualResetEventSlim(false);
        private int _hookThreadId;                 // 由 _threadReady.Set/Wait 提供可见性
        private bool _disposed;

        private readonly Keys _targetKey;
        private readonly string[] _targetButtonTexts;

        public SpaceKeyFinisher(Keys targetKey = Keys.Space, params string[] buttonTexts)
        {
            _targetKey = targetKey;
            _targetButtonTexts = buttonTexts?.Length > 0
                ? buttonTexts
                : new[] { "完成", "Finish", "Complete" };
        }

        public void Enable()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SpaceKeyFinisher));
            if (_hookThread != null && _hookThread.IsAlive) return;

            _threadReady.Reset();
            _hookThread = new Thread(() =>
            {
                try
                {
                    _hookThreadId = NativeMethods.GetCurrentThreadId();
                    _globalHook = Hook.AppEvents();     // AppEvents 而非 GlobalEvents
                    _globalHook.KeyUp += OnKeyUp;
                    _threadReady.Set();
                    Application.Run();                   // 阻塞，直到本线程收到 WM_QUIT
                }
                finally
                {
                    try { _globalHook?.Dispose(); } catch { }
                    _globalHook = null;
                }
            })
            {
                IsBackground = true,
                Name = "SpaceKeyFinisherHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA);
            _hookThread.Start();

            if (!_threadReady.Wait(1000))
                MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 钩子线程启动超时。");
        }

        public void Disable()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hookThread != null && _hookThread.IsAlive && _hookThreadId != 0)
            {
                // 在“钩子线程”自己的消息循环里退出 —— 而不是当前线程
                NativeMethods.PostThreadMessage(
                    _hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);

                if (!_hookThread.Join(2000))
                    MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 钩子线程未按时退出。");
            }

            _hookThread = null;
            _hookThreadId = 0;
        }

        public void Dispose() => Disable();

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != _targetKey) return;

            try
            {
                if (ClickRevitButton())
                    MEPLogger.Instance.AppendLine("[SpaceKeyFinisher] 空格键 → 已点击完成按钮。");
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SpaceKeyFinisher.ClickRevitButton", ex);
            }
        }

        private bool ClickRevitButton()
        {
            IntPtr revitWindow = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (revitWindow == IntPtr.Zero) return false;

            IntPtr targetButton = IntPtr.Zero;
            NativeMethods.EnumChildWindows(revitWindow, (hwnd, lParam) =>
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
                        return false;   // 停止枚举
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