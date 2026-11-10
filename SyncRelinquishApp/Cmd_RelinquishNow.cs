using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.SyncRelinquish
{
    /// <summary>
    /// 立即放弃全部权限（不等待同步）
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Cmd_RelinquishNow : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;
            
            if (doc == null || !doc.IsWorkshared)
            {
                InfoCenterTip.Show("当前文档不是工作共享模型");
                return Result.Cancelled;
            }

            try
            {
                RelinquishOptions options = new RelinquishOptions(true);
                WorksharingUtils.RelinquishOwnership(doc, options, new TransactWithCentralOptions());
                InfoCenterTip.Show("已成功放弃全部权限");
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                MEPLogger.Instance.LogException("Cmd_RelinquishNow", ex);
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}