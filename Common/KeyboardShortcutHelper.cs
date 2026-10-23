using Gma.System.MouseKeyHook;
using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RemoveDuplicateElements
{
    public class KeyboardShortcutHelper : IDisposable
    {
        private IKeyboardMouseEvents _globalHook;
        private Thread _hookThread;
        private bool _disposed;

        /// <summary>
        /// 启动监听，在新 STA 线程中运行消息循环。
        /// </summary>
        public void StartListening()
        {
            if (_hookThread != null && _hookThread.IsAlive)
                return;

            _hookThread = new Thread(() =>
            {
                _globalHook = Hook.GlobalEvents();
                _globalHook.KeyUp += OnKeyUp;
                Application.Run(); // 进入消息循环，阻塞
            })
            {
                IsBackground = true,
                Name = "KeyboardHookThread"
            };
            _hookThread.SetApartmentState(ApartmentState.STA); // 必须为 STA
            _hookThread.Start();
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space)
            {
                ClickRevitFinishButton();
            }
        }

        private void ClickRevitFinishButton()
        {
            IntPtr revitWindow = Autodesk.Windows.ComponentManager.ApplicationWindow;
            if (revitWindow == IntPtr.Zero) return;

            IntPtr finishButton = IntPtr.Zero;
            WindowsHelper.EnumChildWindows(revitWindow, (hwnd, lParam) =>
            {
                StringBuilder text = new StringBuilder(256);
                WindowsHelper.GetWindowText(hwnd, text, text.Capacity);
                StringBuilder cls = new StringBuilder(256);
                WindowsHelper.GetClassName(hwnd, cls, cls.Capacity);

                string buttonText = text.ToString();
                string className = cls.ToString();

                // 根据 Revit 语言版本匹配按钮文本（可在此添加更多）
                if ((buttonText == "完成" || buttonText == "Finish" || buttonText == "Complete")
                    && className.Contains("Button"))
                {
                    finishButton = hwnd;
                    return false; // 停止枚举
                }
                return true;
            }, 0);

            if (finishButton != IntPtr.Zero)
            {
                WindowsHelper.SendMessage(finishButton, WindowsHelper.BM_CLICK, 0, 0);
            }
        }

        public void StopListening()
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

        public void Dispose() => StopListening();
    }
}