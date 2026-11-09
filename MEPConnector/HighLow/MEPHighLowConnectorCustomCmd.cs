using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.MEPConnector.HighLow
{
    /// <summary>
    /// 高差连接 — 自定义角度。弹窗输入 1~90 度。
    /// 说明：0° 会让 ComputeGeometry 中的 dX = |Δz| / tan(0°) 趋于无穷，进而触发"角度过小"错误，
    ///      故有效输入范围仍为 1~90（90° 走垂直分支）。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CommandCustomAngle : HighLowConnectorCmdBase
    {
        private const double DefaultAngleDeg = 45.0;

        protected override string SessionTitle => "MEP 高差连接（自定义角度）";

        protected override Result ResolveAngle(out double angleDeg, ref string message)
        {
            angleDeg = DefaultAngleDeg;

            string input = ShowInputBox(
                "请输入连接角度（1 ~ 90 度）：\n\n" +
                "注：输入 90 度将直接垂直连接（无斜段）。",
                "MEP 斜段连接角度",
                DefaultAngleDeg.ToString());

            if (input == null) return Result.Cancelled;

            if (!double.TryParse(input, out angleDeg)
                || angleDeg < 1.0
                || angleDeg > 90.0)
            {
                message = "输入的角度无效，请输入 1 到 90 之间的数字。";
                return Result.Failed;
            }
            return Result.Succeeded;
        }
    }
}