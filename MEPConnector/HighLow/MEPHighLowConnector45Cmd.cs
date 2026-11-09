using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.MEPConnector.HighLow
{
    /// <summary>高差连接 — 固定 45° 斜段。</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command45Degree : HighLowConnectorCmdBase
    {
        protected override string SessionTitle => "MEP 高差连接（45°）";

        protected override Result ResolveAngle(out double angleDeg, ref string message)
        {
            angleDeg = 45.0;
            return Result.Succeeded;
        }
    }
}