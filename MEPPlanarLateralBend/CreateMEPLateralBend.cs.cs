using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.LateralBend
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateMEPLateralBend : IExternalCommand
    {
        private readonly MEPLogger debugInfo = MEPLogger.Instance;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPLateralBend 平面拐弯");
            debugInfo.AppendLine($"活动开始，选中元素数（原始）：{uidoc.Selection.GetElementIds()?.Count ?? 0}");

            try
            {
                ICollection<ElementId> elemIds = uidoc.Selection.GetElementIds();
                if (elemIds == null || elemIds.Count < 1)
                {
                    message = "请至少选中 1 个管道/风管/线槽元素！";
                    return Result.Failed;
                }

                List<ElementId> elemIdList = new List<ElementId>();
                foreach (ElementId id in elemIds)
                {
                    Element elem = doc.GetElement(id);
                    MEPElementType t = MEPLateralBendCalculator.GetElementType(elem);
                    if (t == MEPElementType.Pipe
                        || t == MEPElementType.Duct
                        || t == MEPElementType.CableTray)
                        elemIdList.Add(id);
                }

                if (elemIdList.Count < 1)
                {
                    message = "选中的元素中未包含有效的管道、风管或线槽！";
                    return Result.Failed;
                }

                // ---- 参数对话框（H + θ）----
                var dlg = new LateralBendDialog(200.0, 45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    message = "用户取消参数输入，操作终止";
                    return Result.Cancelled;
                }
                double offset_mm = dlg.OffsetMm;
                double angleDeg = dlg.AngleDeg;

                if (Math.Abs(offset_mm) < 1)
                {
                    message = "偏移量绝对值不能小于 1mm";
                    return Result.Failed;
                }

                double offset_ft = UnitUtils.ConvertToInternalUnits(
                    Math.Abs(offset_mm), UnitTypeId.Millimeters);

                double angleRad = angleDeg * Math.PI / 180.0;
                double projLen_mm = Math.Abs(offset_mm) * Math.Cos(angleRad) / Math.Sin(angleRad);

                debugInfo.AppendLine($"参数：H = {Math.Abs(offset_mm):F2} mm, θ = {angleDeg:F1}°");
                debugInfo.AppendLine($"斜段沿管投影 = {projLen_mm:F2} mm（= H/tan(θ)）");

                // ---- 拾取 P1（参考管上的点）----
                Reference ref1 = uidoc.Selection.PickObject(ObjectType.PointOnElement,
                    new MEPCurveSelectionFilter(),
                    "【第 1/3 步】在参考管上拾取 P1（拐弯起点，避开弯头/三通）");

                Element refElem = doc.GetElement(ref1.ElementId);
                LocationCurve refLocCurve = refElem?.Location as LocationCurve;
                if (refLocCurve == null)
                {
                    message = "参考元素无定位曲线！";
                    return Result.Failed;
                }
                Curve refCurve = refLocCurve.Curve;
                XYZ breakPt = ref1.GlobalPoint;

                debugInfo.AppendLine($"P1 打断点: ({breakPt.X:F3},{breakPt.Y:F3},{breakPt.Z:F3})");
                debugInfo.AppendLine($"参考管 ID: {ref1.ElementId.IntegerValue}");

                // ---- 拾取 P2（方向点）----
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

                debugInfo.AppendLine($"P2 方向点: ({sidePt.X:F3},{sidePt.Y:F3},{sidePt.Z:F3})");

                // ---- 计算全局 frame ----
                GlobalFrame frame = MEPLateralBendCalculator.ComputeGlobalFrame(
                    refCurve, breakPt, sidePt, offset_ft, angleDeg);

                if (frame == null || !frame.IsValid)
                {
                    string frameErr = frame?.ErrorMessage ?? "全局坐标系计算失败";
                    message = frameErr;
                    debugInfo.AppendLine($"✗ {frameErr}");
                    return Result.Failed;
                }

                debugInfo.AppendLine($"参考方向 RefDir: ({frame.RefDir.X:F3},{frame.RefDir.Y:F3})");
                debugInfo.AppendLine($"斜段方向 DiagDir: ({frame.DiagDir.X:F3},{frame.DiagDir.Y:F3})");
                debugInfo.AppendLine($"P0 分布方向 P0LineDir: ({frame.P0LineDir.X:F3},{frame.P0LineDir.Y:F3})");
                debugInfo.AppendLine(
                    $"横向平移向量: ({frame.LateralVector.X:F3},{frame.LateralVector.Y:F3},{frame.LateralVector.Z:F3})");
                debugInfo.AppendLine(
                    $"平移侧: {(frame.AlongSign > 0 ? "下游" : "上游")}, " +
                    $"横向: {(frame.LatSign > 0 ? "左拐" : "右拐")}");

                MEPLateralBendService service = new MEPLateralBendService(debugInfo);
                int successCount = 0, failCount = 0;

                using (Transaction trans = new Transaction(doc, "平面拐弯"))
                {
                    trans.Start();

                    foreach (ElementId eid in elemIdList)
                    {
                        debugInfo.AppendLine($"\n--- 处理元素 (ID: {eid.IntegerValue}) ---");
                        try
                        {
                            bool ok = service.ProcessSingleElement(doc, eid, frame);
                            if (ok) successCount++; else failCount++;
                        }
                        catch (Exception ex)
                        {
                            debugInfo.AppendLine($"✗ 元素 {eid.IntegerValue} 处理失败：{ex.Message}");
                            debugInfo.AppendLine("⚠ 单事务模式，已回滚所有更改");
                            trans.RollBack();
                            ShowDebugInfo("处理中止",
                                $"元素 {eid.IntegerValue} 处理失败，所有更改已回滚");
                            return Result.Failed;
                        }
                    }

                    trans.Commit();
                }

                debugInfo.AppendLine($"\n处理完成：成功 {successCount}，失败 {failCount}");
                ShowDebugInfo("平面拐弯 - 批量处理完成",
                    $"成功: {successCount}, 失败: {failCount}");
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
                debugInfo.AppendLine($"异常：{ex.Message}");
                debugInfo.AppendLine($"堆栈：{ex.StackTrace}");
                ShowDebugInfo("操作失败", null);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("See log file above");
            }
        }

        private void ShowDebugInfo(string title, string mainInstruction)
        {
            TaskDialog td = new TaskDialog(title);
            td.MainInstruction = mainInstruction ?? string.Empty;
            td.MainContent = debugInfo.GetFullText();
            td.CommonButtons = TaskDialogCommonButtons.Ok;
            td.Show();
        }

        private class MEPCurveSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem)
            {
                return elem is Pipe || elem is Duct || elem is CableTray;
            }
            public bool AllowReference(Reference reference, XYZ position) => true;
        }
    }
}