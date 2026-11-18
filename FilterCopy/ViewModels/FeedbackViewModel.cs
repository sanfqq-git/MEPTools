using System.Collections.ObjectModel;
using MEPTools.FilterCopy.Models;

namespace MEPTools.FilterCopy.ViewModels
{
    /// <summary>执行结果反馈 VM。</summary>
    public class FeedbackViewModel : ObservableObject
    {
        public int SuccessCount { get; }
        public ObservableCollection<string> Logs { get; }

        public string Summary =>
            $"成功复制到 {SuccessCount} 个视图；跳过 / 失败 {Logs.Count} 项（详见日志）。";

        public FeedbackViewModel(TransferResult result)
        {
            SuccessCount = result.SuccessCount;
            Logs = new ObservableCollection<string>(result.Log);
        }
    }
}
