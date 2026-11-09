using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.MEPHighLowUpDown
{
    /// <summary>
    /// 90° 抬升/下降连接（等角 90° 弯头 / 纯垂直过渡）
    /// 水平偏移 = 0，过渡段完全垂直
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command90 : MEPUpDownCommandBase
    {
        protected override string Title => "90° 抬升/下降";

        protected override bool TryGetAngleDeg(out double angleDeg)
        {
            angleDeg = 90.0;
            return true;
        }
    }
}