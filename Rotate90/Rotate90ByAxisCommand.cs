using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;
using MEPTools.Rotate90;   // 复用上一版的 MEPTargetSelectionFilter

namespace MEPTools.Rotate90ByAxis
{
    /// <summary>
    /// 90° 旋转物体（XYZ 轴版）。
    /// 与 MEPTools.Rotate90.Rotate90Command（参考轴版）并存：
    /// 本版不依赖参考元素，旋转轴固定为过整体包围盒中心的 X / Y / Z 世界轴方向。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class Rotate90ByAxisCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null) return Result.Failed;

            Document doc = uidoc.Document;
            MEPLogger.Instance.StartSession("Rotate90ByAxis");

            try
            {
                // ---------- 1. 选择要旋转的 MEP 元素 ----------
                var targetFilter = new MEPTargetSelectionFilter();
                List<Element> targets = MEPSelectionHelper.GetElements(
                    uidoc,
                    targetFilter,
                    "请选择要旋转的 MEP 元素",
                    infoTipNoPreselection: "请选择要旋转的 MEP 元素，按空格键结束",
                    minCount: 1,
                    useSpaceKey: true,
                    singlePick: false);

                if (targets.Count == 0)
                    return Result.Cancelled;

                MEPLogger.Instance.AppendLine($"目标元素数量：{targets.Count}");

                // ---------- 2. 计算旋转中心（整体包围盒中心） ----------
                XYZ center = GetCombinedCenter(targets);
                if (center == null)
                {
                    InfoCenterTip.Show("无法计算目标元素的包围盒中心");
                    return Result.Failed;
                }

                MEPLogger.Instance.AppendLine(
                    $"旋转中心：({center.X:F3}, {center.Y:F3}, {center.Z:F3}) ft");

                // ---------- 3. 打开 XYZ 轴旋转窗体 ----------
                using (var form = new Rotate90AxisForm(uidoc, targets, center))
                {
                    form.ShowDialog();
                }

                InfoCenterTip.Show("旋转操作结束");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Rotate90ByAxisCommand", ex);
                InfoCenterTip.Show("旋转命令发生错误，请查看日志");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("Rotate90ByAxis completed.");
            }
        }

        private static XYZ GetCombinedCenter(List<Element> targets)
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            bool hasBoundingBox = false;

            foreach (var elem in targets)
            {
                BoundingBoxXYZ bb = elem.get_BoundingBox(null);
                if (bb == null) continue;
                hasBoundingBox = true;
                minX = Math.Min(minX, bb.Min.X);
                minY = Math.Min(minY, bb.Min.Y);
                minZ = Math.Min(minZ, bb.Min.Z);
                maxX = Math.Max(maxX, bb.Max.X);
                maxY = Math.Max(maxY, bb.Max.Y);
                maxZ = Math.Max(maxZ, bb.Max.Z);
            }

            if (hasBoundingBox)
                return new XYZ((minX + maxX) * 0.5,
                               (minY + maxY) * 0.5,
                               (minZ + maxZ) * 0.5);

            var loc = targets[0].Location;
            if (loc is LocationPoint lp) return lp.Point;
            if (loc is LocationCurve lc)
                return (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) * 0.5;

            return XYZ.Zero;
        }
    }
}