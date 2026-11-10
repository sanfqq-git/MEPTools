using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace MEPTools.MEPPlanarLateralBend
{
    /// <summary>90° 平面拐弯（快捷命令，不弹参数窗）。</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LateralBend90Command : MEPLateralBendCommandBase
    {
        protected override string CommandTitle => "90°平面拐弯";

        protected override bool TryGetAngleDeg(UIDocument uidoc, out double angleDeg)
        {
            angleDeg = 90.0;
            return true;
        }
    }
}