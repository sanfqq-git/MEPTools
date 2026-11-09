using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.MEPHighLowUpDown
{
    /// <summary>
    /// 45° 抬升/下降连接（等角 45° 弯头）
    /// 水平偏移 = |h|，过渡段与水平管道成 45°
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command45 : MEPUpDownCommandBase
    {
        protected override string Title => "45° 抬升/下降";

        protected override bool TryGetAngleDeg(out double angleDeg)
        {
            angleDeg = 45.0;
            return true;
        }
    }
}