using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.SaddleBend
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateMEPSaddleBend : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPSaddleBend 平面翻弯");
            var log = MEPLogger.Instance;

            try
            {
                // ---- 1. 收集预选元素 ----
                var elemIds = new List<ElementId>();
                var selIds = uiDoc.Selection.GetElementIds();
                if (selIds != null)
                {
                    foreach (var id in selIds)
                    {
                        var e = doc.GetElement(id);
                        if (e is Pipe || e is Duct || e is CableTray)
                            elemIds.Add(id);
                    }
                }

                if (elemIds.Count < 1)
                {
                    message = "请先选中至少 1 个管道 / 风管 / 线槽！";
                    return Result.Failed;
                }
                log.AppendLine("选中 " + elemIds.Count + " 个元素");

                // ---- 2. 参数对话框 ----
                var dlg = new SaddleBendDialog(200.0, 45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    message = "用户取消参数输入";
                    return Result.Cancelled;
                }

                double offsetMm = dlg.OffsetMm;
                double angleDeg = dlg.AngleDeg;
                double offsetFt = UnitUtils.ConvertToInternalUnits(offsetMm, UnitTypeId.Millimeters);
                log.AppendLine(string.Format("参数：H={0:F1}mm, θ={1:F1}°", offsetMm, angleDeg));

                // ---- 3. 拾取 P1（参考管上的点）----
                Reference ref1;
                try
                {
                    ref1 = uiDoc.Selection.PickObject(ObjectType.PointOnElement,
                        new MEPCurveSelectionFilter(),
                        "【第 1/3 步】在参考管上拾取 P1（第一个拐点，避开弯头/三通）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }

                Element refElem = doc.GetElement(ref1.ElementId);
                var refLoc = (refElem == null) ? null : (refElem.Location as LocationCurve);
                Curve refCurve = (refLoc == null) ? null : refLoc.Curve;
                if (refCurve == null)
                {
                    message = "参考管无定位曲线";
                    return Result.Failed;
                }

                XYZ p1 = ref1.GlobalPoint;
                log.AppendLine("P1=" + SaddleFrameCalculator.Fmt(p1) +
                               ", 参考管 ID=" + ref1.ElementId.IntegerValue);

                // ---- 4. 拾取 P2（任意点，投影到参考管轴线）----
                XYZ p2;
                try
                {
                    p2 = uiDoc.Selection.PickPoint(
                        "【第 2/3 步】拾取 P2（第二个拐点）\n" +
                        "⚠ 请把 P2 点在参考管的水平侧方（左或右），\n" +
                        "   不要点在管轴线的延伸方向上（斜管容易被视觉误导）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
                log.AppendLine("P2=" + SaddleFrameCalculator.Fmt(p2));

                // ---- 5. 计算全局 frame ----
                var frame = SaddleFrameCalculator.Compute(refCurve, p1, p2, offsetFt, angleDeg);
                if (!frame.IsValid)
                {
                    message = frame.ErrorMessage;
                    log.AppendLine("✗ " + frame.ErrorMessage);
                    return Result.Failed;
                }

                if (!string.IsNullOrEmpty(frame.Warning))
                    log.AppendLine("⚠ " + frame.Warning);

                log.AppendLine(string.Format(
                    "Frame: AlongDir=({0:F3},{1:F3}) LatDir=({2:F3},{3:F3})",
                    frame.AlongDir.X, frame.AlongDir.Y,
                    frame.LatDir.X, frame.LatDir.Y));
                log.AppendLine(string.Format(
                    "  ProjLen={0:F1}mm AlongDist={1:F1}mm AlongSign={2} LatSign={3}",
                    frame.ProjLen * SaddleFrameCalculator.FtToMm,
                    frame.AlongDist * SaddleFrameCalculator.FtToMm,
                    frame.AlongSign, frame.LatSign));
                log.AppendLine(string.Format(
                    "  P1LineDir=({0:F3},{1:F3})",
                    frame.P1LineDir.X, frame.P1LineDir.Y));
                log.AppendLine(string.Format(
                    "  P2LineDir=({0:F3},{1:F3})",
                    frame.P2LineDir.X, frame.P2LineDir.Y));

                // ---- 6. 逐元素处理 ----
                int successCount = 0, failCount = 0;
                using (var tx = new Transaction(doc, "创建 MEP 平面翻弯"))
                {
                    tx.Start();

                    foreach (var eid in elemIds)
                    {
                        Element el = doc.GetElement(eid);
                        log.AppendLine("\n--- 处理元素 ID=" + eid.IntegerValue + " ---");

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
                            if (svc.ProcessSingleElement(doc, eid, r))
                                successCount++;
                            else
                                failCount++;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine("✗ 处理失败: " + ex.Message);
                            log.AppendLine("  堆栈: " + ex.StackTrace);
                            failCount++;
                        }
                    }

                    tx.Commit();
                }

                log.AppendLine(string.Format("\n完成：成功 {0}, 失败 {1}", successCount, failCount));

                var td = new TaskDialog("MEP 平面翻弯");
                td.MainInstruction = string.Format("处理完成：成功 {0}, 失败 {1}",
                    successCount, failCount);
                td.MainContent = log.GetFullText();
                td.CommonButtons = TaskDialogCommonButtons.Ok;
                td.Show();

                return successCount > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                log.AppendLine("用户取消");
                return Result.Cancelled;
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

    /// <summary>仅允许 Pipe / Duct / CableTray 被选中</summary>
    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        {
            return e is Pipe || e is Duct || e is CableTray;
        }

        public bool AllowReference(Reference r, XYZ position)
        {
            return true;
        }
    }
}