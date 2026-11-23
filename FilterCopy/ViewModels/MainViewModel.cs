using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Windows.Input;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.FilterCopy.Models;
using MEPTools.FilterCopy.Services;
using MEPTools.FilterCopy.Views;
using MEPTools.Shared;

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

        /// <summary>执行完成后的回调（结果先抛给外部记日志，再弹反馈窗）。</summary>
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

                // 先把结果交给外部（命令里立即写日志），再做任何界面动作
                Executed?.Invoke(this, result);

                // 反馈窗：纯展示，任何异常都吞掉并降级，
                // 绝不能因为“窗口打不开”把已提交的复制报成执行失败。
                ShowResultSafely(result);

                RequestClose?.Invoke(this, true);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// 弹执行结果窗；失败则降级为 TaskDialog 摘要，永不外抛。
        /// </summary>
        private static void ShowResultSafely(TransferResult result)
        {
            try
            {
                FeedbackForm.ShowResult(result, GetRevitMainWindowHandle());
                return;
            }
            catch (Exception ex)
            {
                try { MEPLogger.Instance.AppendLine("[警告] 反馈窗口打开失败：" + ex.Message); }
                catch { }
            }

            // 兜底：用 Revit 原生 TaskDialog 至少把摘要和关键明细给出来
            try
            {
                TaskDialog.Show("执行结果", BuildFallbackText(result));
            }
            catch { }
        }

        private static string BuildFallbackText(TransferResult result)
        {
            int successCount = result == null ? 0 : result.SuccessCount;
            var logs = result == null ? new System.Collections.Generic.List<string>() : result.Log;

            var sb = new StringBuilder();
            sb.AppendLine("成功复制到 " + successCount + " 个视图；跳过 / 失败 " + logs.Count + " 项。");

            const int maxLines = 20;
            foreach (string line in logs.Take(maxLines))
                sb.AppendLine(line);

            if (logs.Count > maxLines)
                sb.AppendLine("……（其余 " + (logs.Count - maxLines) + " 条见日志文件）");

            return sb.ToString();
        }

        /// <summary>
        /// 取 Revit 主窗口句柄（AdWindows 的 ComponentManager.ApplicationWindow）。
        /// Revit 里没有 WPF Application 实例，绝不能用 Application.Current.Windows 取 Owner。
        /// </summary>
        private static IntPtr GetRevitMainWindowHandle()
        {
            try
            {
                return Autodesk.Windows.ComponentManager.ApplicationWindow;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }
    }
}
