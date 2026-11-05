using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using Forms = System.Windows.Forms;

namespace MEPTools.MEPBatchBreak
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        private readonly StringBuilder debugInfo = new StringBuilder();

        private void Log(string text)
        {
            debugInfo.AppendLine(text);
            MEPLogger.Instance.AppendLine(text);
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("MEP 批量打断");

            try
            {
                List<ElementId> elemIdList = GetValidMEPElementIds(doc, uidoc);

                // 1. 选择要打断的管线
                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("请选择至少 1 个管道/风管/线槽元素");
                    try
                    {
                        using (SpaceKeyFinisher finisher = new SpaceKeyFinisher(
                            Forms.Keys.Space, "完成", "Finish", "Complete"))
                        {
                            try { finisher.Enable(); } catch { }

                            IList<Reference> refs = uidoc.Selection.PickObjects(
                                ObjectType.Element,
                                new MEPCurveSelectionFilter(),
                                "选择需要打断的 MEP 管线（ESC 结束 / 空格键完成选择）");

                            foreach (Reference r in refs)
                            {
                                Element e = doc.GetElement(r.ElementId);
                                if (e is Pipe || e is Duct || e is CableTray)
                                    elemIdList.Add(r.ElementId);
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                    {
                        InfoCenterTip.Show("已取消元素选择");
                        MEPLogger.Instance.EndSession("用户取消选择");
                        return Result.Cancelled;
                    }
                }
                else
                {
                    InfoCenterTip.Show($"使用已选中 {elemIdList.Count} 个 MEP 元素");
                }

                if (elemIdList.Count < 1)
                {
                    InfoCenterTip.Show("✗ 未包含有效的 MEP 元素");
                    MEPLogger.Instance.EndSession("无有效元素");
                    return Result.Failed;
                }

                // 2. 拾取打断点（点线拾取）
                InfoCenterTip.Show("请在需要打断的管线上点击基准位置");
                Reference pickRef;
                try
                {
                    pickRef = uidoc.Selection.PickObject(ObjectType.PointOnElement, 
                        new MEPCurveSelectionFilter(), 
                        "请在管线上点击打断基准点");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    InfoCenterTip.Show("已取消拾取点");
                    MEPLogger.Instance.EndSession("用户取消拾取点");
                    return Result.Cancelled;
                }

                XYZ pickPt = pickRef.GlobalPoint; 
                ElementId clickElemId = pickRef.ElementId; 

                int successCount = 0, failCount = 0;
                double safeOffset = UnitUtils.ConvertToInternalUnits(2.0, UnitTypeId.Millimeters);
                double tol = UnitUtils.ConvertToInternalUnits(5.0, UnitTypeId.Millimeters);

                using (Transaction trans = new Transaction(doc, "批量打断 MEP 管线"))
                {
                    trans.Start();

                    foreach (ElementId eid in elemIdList)
                    {
                        Element elem = doc.GetElement(eid);
                        LocationCurve lc = elem?.Location as LocationCurve;
                        if (lc == null || lc.Curve == null) 
                        { 
                            Log($"✗ 元素 {eid.IntegerValue} 无定位曲线，跳过");
                            failCount++; 
                            continue; 
                        }

                        Curve curve = lc.Curve;
                        IntersectionResult proj = curve.Project(pickPt);

                        if (proj == null)
                        {
                            Log($"✗ 元素 {eid.IntegerValue} 投影失败，跳过");
                            failCount++;
                            continue;
                        }

                        // 检查投影点是否在管线的物理线段内
                        if (!curve.IsInside(proj.Parameter))
                        {
                            Log($"✗ 元素 {eid.IntegerValue} 投影点在线段外（管子太短或离点击点太远），跳过");
                            failCount++;
                            continue;
                        }

                        // 【修复】取消 maxProjectDist 限制。只要用户点选了，就强制打断。
                        // 但增加日志提示：如果不是直接点中的那根，且距离超过 500mm，提示一下用户。
                        double maxProjectDist = UnitUtils.ConvertToInternalUnits(500.0, UnitTypeId.Millimeters);
                        if (proj.Distance > maxProjectDist && eid != clickElemId)
                        {
                            Log($"ℹ 元素 {eid.IntegerValue} 距离点击点较远 ({proj.Distance * 304.8:F0}mm)，但仍按选中意图执行打断。");
                        }

                        XYZ breakPt = proj.XYZPoint;
                        XYZ start = curve.GetEndPoint(0);
                        XYZ end = curve.GetEndPoint(1);
                        XYZ dir = (end - start).Normalize();

                        // 端点防呆处理
                        if (breakPt.DistanceTo(start) < safeOffset)
                            breakPt = start + dir * safeOffset;
                        else if (breakPt.DistanceTo(end) < safeOffset)
                            breakPt = end - dir * safeOffset;

                        if (breakPt.DistanceTo(start) <= 0.001 || breakPt.DistanceTo(end) <= 0.001)
                        {
                            Log($"✗ 元素 {eid.IntegerValue} 太短，无法打断");
                            failCount++;
                            continue;
                        }

                        try
                        {
                            // 第一步：断开原连接
                            List<Connector> startExtConns = GetExternalConnectors(elem, start, tol);
                            List<Connector> endExtConns = GetExternalConnectors(elem, end, tol);

                            Connector cA_start = GetConnectorNear(elem, start, tol);
                            Connector cA_end = GetConnectorNear(elem, end, tol);

                            if (cA_start != null) DisconnectAll(cA_start);
                            if (cA_end != null) DisconnectAll(cA_end);

                            // 第二步：原地克隆（继承 Workset）
                            ICollection<ElementId> copyIds = ElementTransformUtils.CopyElement(doc, eid, XYZ.Zero);
                            Element newElem = doc.GetElement(copyIds.FirstOrDefault());
                            if (newElem == null) throw new Exception("克隆新元素失败");

                            if (doc.IsWorkshared)
                            {
                                Parameter oldWsParam = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                Parameter newWsParam = newElem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                                if (oldWsParam != null && newWsParam != null && !newWsParam.IsReadOnly)
                                {
                                    try { newWsParam.Set(oldWsParam.AsInteger()); } catch { }
                                }
                            }

                            // 第三步：截断
                            lc.Curve = Line.CreateBound(start, breakPt);
                            
                            LocationCurve newLc = newElem.Location as LocationCurve;
                            if (newLc != null) newLc.Curve = Line.CreateBound(breakPt, end);

                            doc.Regenerate();

                            // 第四步：物归原主（P1点不连）
                            Connector newCA_start = GetConnectorNear(elem, start, tol);
                            Connector newCB_end = GetConnectorNear(newElem, end, tol);

                            if (newCA_start != null && startExtConns.Count > 0)
                            {
                                foreach (var extConn in startExtConns)
                                {
                                    try { newCA_start.ConnectTo(extConn); break; } catch { }
                                }
                            }

                            if (newCB_end != null && endExtConns.Count > 0)
                            {
                                foreach (var extConn in endExtConns)
                                {
                                    try { newCB_end.ConnectTo(extConn); break; } catch { }
                                }
                            }

                            Log($"✓ 元素 {eid.IntegerValue} 拆分成功，新元素ID: {newElem.Id.IntegerValue}");
                            successCount++;
                        }
                        catch (Exception ex)
                        {
                            Log($"✗ 元素 {eid.IntegerValue} 打断异常: {ex.Message}");
                            failCount++;
                        }
                    }

                    trans.Commit();
                }

                if (failCount == 0)
                    InfoCenterTip.Show($"✓ 处理完成：成功 {successCount} 个元素");
                else
                    InfoCenterTip.Show($"⚠ 处理完成：成功 {successCount}，失败 {failCount}");

                MEPLogger.Instance.EndSession($"成功 {successCount}，失败 {failCount}");
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                InfoCenterTip.Show("已取消操作");
                MEPLogger.Instance.EndSession("用户取消");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                Log($"异常：{ex.Message}");
                InfoCenterTip.Show($"✗ 操作失败：{ex.Message}");
                MEPLogger.Instance.EndSession("异常终止");
                return Result.Failed;
            }
        }

        private List<Connector> GetExternalConnectors(Element elem, XYZ targetPt, double tol)
        {
            List<Connector> res = new List<Connector>();
            Connector c = GetConnectorNear(elem, targetPt, tol);
            if (c != null && c.IsConnected)
            {
                foreach (Connector rc in c.AllRefs)
                {
                    if (rc.Owner != null && rc.Owner.Id != elem.Id)
                        res.Add(rc);
                }
            }
            return res;
        }

        private void DisconnectAll(Connector c)
        {
            if (c == null || !c.IsConnected) return;
            foreach (Connector rc in c.AllRefs)
            {
                if (rc.Owner != null && rc.Owner.Id != c.Owner.Id)
                {
                    try { c.DisconnectFrom(rc); } catch { }
                }
            }
        }

        private Connector GetConnectorNear(Element elem, XYZ pt, double tol)
        {
            ConnectorManager cm = (elem as MEPCurve)?.ConnectorManager;
            if (cm == null) return null;

            Connector best = null;
            double minDist = double.MaxValue;
            foreach (Connector c in cm.Connectors)
            {
                double d = c.Origin.DistanceTo(pt);
                if (d < minDist && d <= tol)
                {
                    minDist = d;
                    best = c;
                }
            }
            return best;
        }

        private List<ElementId> GetValidMEPElementIds(Document doc, UIDocument uidoc)
        {
            List<ElementId> result = new List<ElementId>();
            ICollection<ElementId> preSelected = uidoc.Selection.GetElementIds();
            if (preSelected == null) return result;

            foreach (ElementId id in preSelected)
            {
                Element elem = doc.GetElement(id);
                if (elem is Pipe || elem is Duct || elem is CableTray)
                    result.Add(id);
            }
            return result;
        }

        private class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is Pipe || elem is Duct || elem is CableTray;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}