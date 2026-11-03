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

                // 1. 尝试从预选中提取
                // 1.1 利用 Preselected 提取选中的 Tag，反查出被标记的管线
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

                // 1.2 检查用户直接选中的 MEP 元素
                foreach (ElementId id in uidoc.Selection.GetElementIds())
                {
                    Element elem = doc.GetElement(id);
                    if (IsValidMEPElement(elem) && addedIds.Add(id))
                        elemIdList.Add(id);
                }

                // 2. 无有效预选，提示用户并进入交互拾取
                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("未检测到预选元素，请去选择要处理的 MEP 元素（管道/风管/线槽）。");
                    
                    try
                    {
                        IList<Reference> refs = uidoc.Selection.PickObjects(ObjectType.Element,
                            new MEPCurveSelectionFilter(),
                            "请选择要处理的 MEP 元素（可框选）");

                        foreach (var r in refs)
                        {
                            Element elem = doc.GetElement(r.ElementId);
                            if (IsValidMEPElement(elem) && addedIds.Add(elem.Id))
                                elemIdList.Add(elem.Id);
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        debugInfo.AppendLine("用户取消了元素选择");
                        return Result.Cancelled;
                    }
                }

                // 3. 最终校验
                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("未选择到任何有效的 MEP 元素，操作终止。");
                    return Result.Failed;
                }

                // ---- 参数对话框（只输入角度 θ）----
                var dlg = new LateralBendDialog(45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    return Result.Cancelled;
                }
                double angleDeg = dlg.AngleDeg;
                double angleRad = angleDeg * Math.PI / 180.0;

                debugInfo.AppendLine($"参数：θ = {angleDeg:F1}°");

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
                        "【第 2/3 步】拾取 P2（决定偏移距离与方向）\n" +
                        "⚠ P2 到 P1 的横向垂直距离将作为偏移量 H，\n" +
                        "   请点在参考管的侧方（左或右），不要点在轴线上。");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    debugInfo.AppendLine("用户取消了 P2 拾取");
                    return Result.Cancelled;
                }

                // ---- 计算全局 frame ----
                GlobalFrame frame = MEPLateralBendCalculator.ComputeGlobalFrame(
                    refCurve, breakPt, sidePt, angleDeg);

                if (frame == null || !frame.IsValid)
                {
                    string frameErr = frame?.ErrorMessage ?? "全局坐标系计算失败";
                    InfoCenterTip.Show($"计算失败: {frameErr}");
                    return Result.Failed;
                }

                double offset_mm = frame.AbsOffset * 304.8;
                double projLen_mm = frame.ProjLen * 304.8;
                debugInfo.AppendLine($"自动计算偏移量 H = {offset_mm:F1} mm");
                debugInfo.AppendLine($"斜段沿管投影 = {projLen_mm:F1} mm（= H/tan(θ)）");

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

        private ICollection<ElementId> GetTaggedElementIds(IndependentTag tag)
        {
            try
            {
                var refs = tag.GetTaggedReferences();
                return refs.Select(r => r.ElementId).ToList();
            }
            catch (Exception)
            {
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