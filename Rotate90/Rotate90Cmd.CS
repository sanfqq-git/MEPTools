using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.Rotate90
{
    [Transaction(TransactionMode.Manual)]
    public class Rotate90Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
                              ref string message,
                              ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("Rotate90");

            try
            {
                // ---------- 1. 选择参考元素（用于提取旋转轴） ----------
                var refFilter = new ReferenceElementSelectionFilter();
                List<Element> refList = MEPSelectionHelper.GetElements(
                    uidoc,
                    refFilter,
                    "请选择参考元素（其轴线将作为旋转轴）",
                    infoTipNoPreselection: "请选择管道、风管、墙或设备等可作为旋转参考的元素",
                    minCount: 1,
                    useSpaceKey: false,
                    singlePick: true);

                if (refList.Count == 0)
                    return Result.Cancelled;

                Element referenceElem = refList[0];
                Line rotationAxis = TryGetRotationAxis(referenceElem);

                if (rotationAxis == null)
                {
                    InfoCenterTip.Show("无法从所选参考元素提取旋转轴，请选择线性 MEP 元素或设备。");
                    return Result.Failed;
                }

                MEPLogger.Instance.AppendLine($"旋转轴：{rotationAxis.Origin} → {rotationAxis.Direction}");

                // ---------- 2. 选择要旋转的 MEP 元素 ----------
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

                // ---------- 3. 进入旋转窗体（空格键连续触发） ----------
                using (var form = new Rotate90Form(uidoc, targets, rotationAxis))
                {
                    form.ShowDialog();
                }

                InfoCenterTip.Show("旋转操作结束");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("Rotate90Command", ex);
                InfoCenterTip.Show("旋转命令发生错误，请查看日志");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("Rotate90 completed.");
            }
        }

        /// <summary>
        /// 从参考元素提取可用的旋转轴。
        /// 线性元素用其位置线；点式元素用通过该点的垂直轴。
        /// </summary>
        private static Line TryGetRotationAxis(Element elem)
        {
            if (elem.Location is LocationCurve locCurve)
            {
                Curve curve = locCurve.Curve;
                if (curve is Line line)
                    return line;

                // 非直线曲线取首尾点构造直线，保证 RotateElement 可用
                XYZ p0 = curve.GetEndPoint(0);
                XYZ p1 = curve.GetEndPoint(1);
                if (!p0.IsAlmostEqualTo(p1))
                    return Line.CreateBound(p0, p1);
            }

            if (elem.Location is LocationPoint locPoint)
            {
                // 点式元素默认绕 Z 轴旋转（垂直轴）
                return Line.CreateUnbound(locPoint.Point, XYZ.BasisZ);
            }

            return null;
        }
    }
}