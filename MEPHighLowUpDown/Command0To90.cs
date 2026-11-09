using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.MEPHighLowUpDown
{
    /// <summary>
    /// 任意角度（0°~90°）抬升/下降连接
    /// 水平偏移 = |h| / tan(θ)；θ 由用户输入
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command0To90 : MEPUpDownCommandBase
    {
        protected override string Title => "0~90° 抬升/下降";

        protected override bool TryGetAngleDeg(out double angleDeg)
        {
            angleDeg = 0.0;
            double input = ShowInputDialog(
                "连接角度",
                "输入过渡段与水平管道的夹角 θ\n范围：(0°, 90°]\n单位：度\n示例：30 / 45 / 60 / 90",
                "45");

            if (double.IsNaN(input)) return false;

            if (input <= 0.0 || input > 90.0)
            {
                InfoCenterTip.Show($"✗ 角度 {input}° 无效，必须在 (0°, 90°] 范围内");
                return false;
            }

            angleDeg = input;
            return true;
        }
    }
}