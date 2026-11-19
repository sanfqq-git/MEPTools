namespace MEPTools.ExportTools.Common
{
    /// <summary>导出结果计数（成功 / 跳过 / 失败）。各导出模块共用。</summary>
    public class ExportResult
    {
        public int Success { get; set; }
        public int Fail { get; set; }
        public int Skip { get; set; }
    }
}
