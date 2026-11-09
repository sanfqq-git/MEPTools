using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.MEPConnector.HighLow
{
    /// <summary>高差连接 — 固定 90°（直接垂直连接，无斜段）。</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command90Degree : HighLowConnectorCmdBase
    {
        protected override string SessionTitle => "MEP 高差连接（90°）";

        protected override Result ResolveAngle(out double angleDeg, ref string message)
        {
            angleDeg = 90.0;
            return Result.Succeeded;
        }
    }
}