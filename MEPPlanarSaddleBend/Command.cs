using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;

namespace MEPTools.MEPPlanarSaddleBend
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPSaddleBend 平面翻弯");
            var log = MEPLogger.Instance;

            try
            {
                var elemIds = new List<ElementId>();
                var selIds = uiDoc.Selection.GetElementIds();
                if (selIds != null)
                {
                    foreach (var id in selIds)
                    {
                        var e = doc.GetElement(id);
                        if (e is Pipe || e is Duct || e is CableTray) elemIds.Add(id);
                    }
                }

                if (elemIds.Count < 1)
                {
                    message = "请先选中至少 1 个管道 / 风管 / 线槽！";
                    return Result.Failed;
                }

                var dlg = new SaddleBendDialog(45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed) return Result.Cancelled;
                double angleDeg = dlg.AngleDeg;

                Reference ref1;
                try
                {
                    ref1 = uiDoc.Selection.PickObject(ObjectType.PointOnElement, new MEPCurveSelectionFilter(),
                        "【第 1/3 步】在参考管上拾取 P1（第一个拐点）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                Element refElem = doc.GetElement(ref1.ElementId);
                Curve refCurve = (refElem?.Location as LocationCurve)?.Curve;
                if (refCurve == null) { message = "参考管无定位曲线"; return Result.Failed; }
                XYZ p1 = ref1.GlobalPoint;

                XYZ p2;
                try
                {
                    p2 = uiDoc.Selection.PickPoint("【第 2/3 步】拾取 P2（翻弯终点）\n其横向距离将作为翻弯高度 H");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }

                // 1. 基础 Frame 计算（初始高度由 P2 点决定）
                var frame = SaddleFrameCalculator.Compute(refCurve, p1, p2, angleDeg);
                if (!frame.IsValid)
                {
                    message = frame.ErrorMessage;
                    return Result.Failed;
                }

                // =====================================================================
                // ★ 全局自适应预计算：如果空间不够，全局统一缩小 H，保证所有管整齐对齐
                // =====================================================================
                double tanA = Math.Tan(frame.AngleRad);
                double minMidLen = UnitUtils.ConvertToInternalUnits(100.0, UnitTypeId.Millimeters); // 最小保留 100mm
                double globalMaxH = frame.AbsOffset; // 初始允许最大 H 即为点击的距离

                foreach (var eid in elemIds)
                {
                    Element el = doc.GetElement(eid);
                    var mc = el as MEPCurve;
                    var line = (mc?.Location as LocationCurve)?.Curve as Line;
                    if (line == null) continue;

                    // 计算该管在当前分布线下的可用长度 D
                    XYZ p0, p2p;
                    if (MEPSaddleBendCalculator.TryIntersectCurveWithLine(line, frame.P1, frame.P1LineDir, true, out p0) &&
                        MEPSaddleBendCalculator.TryIntersectCurveWithLine(line, frame.P2, frame.P2LineDir, true, out p2p))
                    {
                        double D = (p2p - p0).DotProduct(frame.AlongDir);
                        // 根据 D 反推该管能容忍的最大高度：D = 2*(H/tanA) + 100 → H = (D-100)*tanA/2
                        double maxHForThisPipe = (D - minMidLen) * tanA / 2.0;
                        if (maxHForThisPipe < globalMaxH)
                        {
                            globalMaxH = maxHForThisPipe;
                        }
                    }
                }

                if (globalMaxH < 0) globalMaxH = 0;
                
                if (globalMaxH < frame.AbsOffset)
                {
                    log.AppendLine(string.Format("⚠ 空间不足，整体缩小翻弯高度：{0:F1}mm → {1:F1}mm",
                        frame.AbsOffset * SaddleFrameCalculator.FtToMm, globalMaxH * SaddleFrameCalculator.FtToMm));
                    frame.AbsOffset = globalMaxH;
                    frame.ProjLen = globalMaxH / tanA; // 同步更新斜段投影长度
                }
                // =====================================================================

                int successCount = 0, failCount = 0;
                using (var tx = new Transaction(doc, "创建 MEP 平面翻弯"))
                {
                    tx.Start();

                    foreach (var eid in elemIds)
                    {
                        Element el = doc.GetElement(eid);
                        log.AppendLine("\n--- 处理元素 ID=" + eid.IntegerValue + " ---");

                        // 此时 frame 里的 AbsOffset 已经是全局统一缩减后的值
                        var r = MEPSaddleBendCalculator.ComputePipeGeometry(el, frame);
                        if (!r.IsValid)
                        {
                            log.AppendLine("✗ 计算失败: " + r.ErrorMessage);
                            failCount++;
                            continue;
                        }

                        try
                        {
                            var svc = new MEPSaddleBendService(log);
                            if (svc.ProcessSingleElement(doc, eid, r)) successCount++;
                            else failCount++;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine("✗ 处理失败: " + ex.Message);
                            failCount++;
                        }
                    }
                    tx.Commit();
                }

                var td = new TaskDialog("MEP 平面翻弯");
                td.MainInstruction = string.Format("处理完成：成功 {0}, 失败 {1}", successCount, failCount);
                td.MainContent = log.GetFullText();
                td.Show();

                return successCount > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("Execute", ex);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }
    }

    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e) { return e is Pipe || e is Duct || e is CableTray; }
        public bool AllowReference(Reference r, XYZ position) { return true; }
    }
}