using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;

namespace MEPTools.MEPPlanarLateralBend
{
    /// <summary>0~90° 任意角度平面拐弯（弹参数窗输入）。</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class LateralBendFreeCommand : MEPLateralBendCommandBase
    {
        protected override string CommandTitle => "任意角度平面拐弯";

        protected override bool TryGetAngleDeg(UIDocument uidoc, out double angleDeg)
        {
            var dlg = new LateralBendDialog(45.0);
            if (dlg.ShowDialog() != true || !dlg.Confirmed)
            {
                angleDeg = 0;
                return false;
            }
            angleDeg = dlg.AngleDeg;
            return true;
        }
    }
}