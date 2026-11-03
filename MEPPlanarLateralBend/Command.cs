using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEPPlanarLateralBend
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private readonly MEPLogger debugInfo = MEPLogger.Instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPLateralBend 平面拐弯");
            debugInfo.AppendLine($"活动开始，当前选中元素数：{uidoc.Selection.GetElementIds()?.Count ?? 0}");

            try
            {
                List<ElementId> elemIdList = new List<ElementId>();
                HashSet<ElementId> addedIds = new HashSet<ElementId>();

                // 1. 利用 Preselected 提取选中的 Tag，反查出被标记的管线
                var selectedTags = Preselected.GetSelectedIndependentTags(uidoc, doc.ActiveView);
                foreach (var tag in selectedTags)
                {
                    ICollection<ElementId> taggedIds = GetTaggedElementIds(tag);
                    foreach (var taggedId in taggedIds)
                    {
                        Element elem = doc.GetElement(taggedId);
                        if (IsValidMEPElement(elem) && addedIds.Add(elem.Id))
                            elemIdList.Add(elem.Id);
                    }
                }

                // 2. 同时检查用户直接选中的 MEP 元素
                foreach (ElementId id in uidoc.Selection.GetElementIds())
                {
                    Element elem = doc.GetElement(id);
                    if (IsValidMEPElement(elem) && addedIds.Add(id))
                        elemIdList.Add(id);
                }

                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("请至少选中 1 个管道/风管/线槽（或其对应的标记）！");
                    return Result.Failed;
                }

                // ---- 参数对话框（H + θ）----
                var dlg = new LateralBendDialog(200.0, 45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    return Result.Cancelled;
                }
                double offset_mm = dlg.OffsetMm;
                double angleDeg = dlg.AngleDeg;

                double offset_ft = UnitUtils.ConvertToInternalUnits(Math.Abs(offset_mm), UnitTypeId.Millimeters);
                double angleRad = angleDeg * Math.PI / 180.0;
                double projLen_mm = Math.Abs(offset_mm) * Math.Cos(angleRad) / Math.Sin(angleRad);

                debugInfo.AppendLine($"参数：H = {Math.Abs(offset_mm):F2} mm, θ = {angleDeg:F1}°");
                debugInfo.AppendLine($"斜段沿管投影 = {projLen_mm:F2} mm（= H/tan(θ)）");

                // ---- 拾取 P1 ----
                Reference ref1 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 1/3 步】在参考管上拾取 P1（拐弯起点，避开弯头/三通）");

                Element refElem = doc.GetElement(ref1.ElementId);
                LocationCurve refLocCurve = refElem?.Location as LocationCurve;
                if (refLocCurve == null)
                {
                    InfoCenterTip.Show("参考元素无定位曲线！");
                    return Result.Failed;
                }
                Curve refCurve = refLocCurve.Curve;
                XYZ breakPt = ref1.GlobalPoint;

                // ---- 拾取 P2 ----
                XYZ sidePt;
                try
                {
                    sidePt = uidoc.Selection.PickPoint(
                        "【第 2/3 步】拾取 P2（方向点）\n" +
                        "⚠ 请把 P2 点在参考管的水平侧方（左或右），\n" +
                        "   不要点在管轴线的延伸方向上（斜管容易被视觉误导）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    debugInfo.AppendLine("用户取消了 P2 拾取");
                    return Result.Cancelled;
                }

                // ---- 计算全局 frame ----
                GlobalFrame frame = MEPLateralBendCalculator.ComputeGlobalFrame(
                    refCurve, breakPt, sidePt, offset_ft, angleDeg);

                if (frame == null || !frame.IsValid)
                {
                    string frameErr = frame?.ErrorMessage ?? "全局坐标系计算失败";
                    InfoCenterTip.Show($"计算失败: {frameErr}");
                    return Result.Failed;
                }

                MEPLateralBendService service = new MEPLateralBendService(debugInfo);
                int successCount = 0, failCount = 0;

                using (Transaction trans = new Transaction(doc, "平面拐弯"))
                {
                    trans.Start();
                    foreach (ElementId eid in elemIdList)
                    {
                        try
                        {
                            bool ok = service.ProcessSingleElement(doc, eid, frame);
                            if (ok) successCount++; else failCount++;
                        }
                        catch (Exception ex)
                        {
                            debugInfo.LogException($"元素 {eid.IntegerValue} 处理失败", ex);
                            trans.RollBack();
                            InfoCenterTip.Show($"处理中止: 元素 {eid.IntegerValue} 失败，已回滚");
                            return Result.Failed;
                        }
                    }
                    trans.Commit();
                }

                debugInfo.AppendLine($"\n处理完成：成功 {successCount}，失败 {failCount}");
                InfoCenterTip.Show($"平面拐弯完成: 成功 {successCount}, 失败 {failCount}");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                debugInfo.AppendLine("用户取消了操作");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                debugInfo.LogException("全局异常", ex);
                InfoCenterTip.Show($"操作失败: {ex.Message}");
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("See log file above");
            }
        }

        /// <summary>
        /// 兼容 Revit 2022+ 和旧版本的获取标记引用的方法
        /// </summary>
        private ICollection<ElementId> GetTaggedElementIds(IndependentTag tag)
        {
            try
            {
                // Revit 2022 及以上
                var refs = tag.GetTaggedReferences();
                return refs.Select(r => r.ElementId).ToList();
            }
            catch (Exception)
            {
                // 兜底兼容 Revit 2021 及以下
                var linkIds = tag.GetTaggedElementIds();
                if (linkIds == null) return new List<ElementId>();

                var res = new List<ElementId>();
                foreach (var linkId in linkIds)
                {
                    if (linkId.HostElementId != ElementId.InvalidElementId)
                        res.Add(linkId.HostElementId);
                    else if (linkId.LinkedElementId != ElementId.InvalidElementId)
                        res.Add(linkId.LinkedElementId);
                }
                return res;
            }
        }

        private bool IsValidMEPElement(Element elem)
        {
            if (elem == null) return false;
            return elem is Pipe || elem is Duct || elem is CableTray;
        }

        private class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe || elem is Duct || elem is CableTray;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}