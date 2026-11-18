using System.Collections.Generic;

namespace MEPTools.FilterCopy.Models
{
    /// <summary>批量执行结果：成功计数 + 明细日志（含模板跳过）。</summary>
    public class TransferResult
    {
        public int SuccessCount { get; set; }
        public int FailCount { get; set; }
        public List<string> Log { get; } = new List<string>();
    }
}
