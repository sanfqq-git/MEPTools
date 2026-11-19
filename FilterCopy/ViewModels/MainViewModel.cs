using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.FilterCopy.Models;
using MEPTools.FilterCopy.Services;
using MEPTools.FilterCopy.Views;

namespace MEPTools.FilterCopy.ViewModels
{
    /// <summary>
    /// 主窗口 VM：目标视图列表、覆盖模式、批量执行。
    /// 安全策略全部下沉到 ViewFilterTransferService。
    /// </summary>
    public class MainViewModel : ObservableObject
    {
        private readonly Document _doc;
        private readonly View _sourceView;
        private readonly ViewFilterExtractor _extractor;
        private readonly ViewFilterTransferService _transferService;

        private bool _overrideMode = true;
        private bool _isBusy;

        public ObservableCollection<ViewTargetItem> Targets { get; }

        public string SourceViewName => _sourceView == null ? string.Empty : _sourceView.Name;

        public bool OverrideMode
        {
            get => _overrideMode;
            set => SetProperty(ref _overrideMode, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                    ((RelayCommand)ExecuteCommand).RaiseCanExecuteChanged();
            }
        }

        public ICommand ExecuteCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand SelectNoneCommand { get; }
        public ICommand SelectNoTemplateCommand { get; }

        /// <summary>执行完成后的回调（由 View 订阅，用于弹反馈框并关闭主窗）。</summary>
        public event EventHandler<TransferResult> Executed;
        /// <summary>请求关闭窗口；参数表示是否作为“成功执行”关闭。</summary>
        public event EventHandler<bool> RequestClose;

        public MainViewModel(Document doc, View sourceView, System.Collections.Generic.IEnumerable<ViewTargetItem> targets)
        {
            _doc = doc;
            _sourceView = sourceView;
            _extractor = new ViewFilterExtractor(doc);
            _transferService = new ViewFilterTransferService(doc);
            Targets = new ObservableCollection<ViewTargetItem>(targets);

            ExecuteCommand = new RelayCommand(ExecuteTransfer, () => !IsBusy);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(this, false));
            SelectAllCommand = new RelayCommand(() => SetSelection(t => true));
            SelectNoneCommand = new RelayCommand(() => SetSelection(t => false));
            SelectNoTemplateCommand = new RelayCommand(() => SetSelection(t => !t.HasTemplate));
        }

        private void SetSelection(Func<ViewTargetItem, bool> predicate)
        {
            foreach (var t in Targets) t.IsSelected = predicate(t);
        }

        private void ExecuteTransfer()
        {
            var selected = Targets.Where(t => t.IsSelected).ToList();
            if (selected.Count == 0)
            {
                TaskDialog.Show("提示", "请先勾选至少一个目标视图。");
                return;
            }

            IsBusy = true;
            try
            {
                var snapshots = _extractor.Extract(_sourceView);

                // 单一事务：成功则整体可一次撤销；内部已做视图级/过滤器级异常隔离
                using (var tx = new Transaction(_doc, "批量复制视图过滤器"))
                {
                    tx.Start();
                    foreach (var item in selected)
                    {
                        if (!(_doc.GetElement(item.ViewId) is View target)) continue;
                        _transferService.ApplyToTarget(target, snapshots, OverrideMode);
                    }
                    tx.Commit();
                }

                var result = _transferService.Result;
                Executed?.Invoke(this, result);

                // 弹出反馈对话框：成功数量 + 模板跳过等日志
                var feedback = new FeedbackWindow(new FeedbackViewModel(result));

                // ★ 修复崩溃点：Revit 自身不创建 WPF Application 实例，
                //   System.Windows.Application.Current 在 Revit 里恒为 null，
                //   原写法 Application.Current.Windows 必然抛 NullReferenceException。
                //   改为挂 Revit 主窗口句柄（ComponentManager.ApplicationWindow）。
                AttachRevitOwner(feedback);

                feedback.ShowDialog();

                RequestClose?.Invoke(this, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 把 WPF 窗口挂到 Revit 主窗口上。
        /// Revit 里没有 WPF Application 实例，绝不能用 Application.Current.Windows 取 Owner。
        /// </summary>
        private static void AttachRevitOwner(System.Windows.Window window)
        {
            if (window == null) return;

            try
            {
                // 1) 首选：Revit 主窗口句柄（AdWindows 的 ComponentManager.ApplicationWindow）
                IntPtr hwnd = IntPtr.Zero;
                try
                {
                    hwnd = Autodesk.Windows.ComponentManager.ApplicationWindow;
                }
                catch
                {
                    hwnd = IntPtr.Zero;
                }

                if (hwnd != IntPtr.Zero)
                {
                    new System.Windows.Interop.WindowInteropHelper(window).Owner = hwnd;
                    return;
                }

                // 2) 兜底：只有确实存在 WPF Application 时，才去找主窗口当 Owner
                var app = System.Windows.Application.Current;
                if (app == null) return;

                var owner = app.Windows.OfType<Views.MainWindow>().FirstOrDefault();
                if (owner != null && !ReferenceEquals(owner, window)) window.Owner = owner;
            }
            catch
            {
                // 挂不上 Owner 不影响功能，静默忽略
            }
        }
    }
}
