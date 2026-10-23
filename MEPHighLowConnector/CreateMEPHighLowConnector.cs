using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

// 解决 Connector 命名冲突
using Connector = Autodesk.Revit.DB.Connector;

namespace MEPTools.MEPHighLowConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class CreateMEPConnector : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData,
            ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("CreateMEPConnector 连接两组 MEP");
            var log = MEPLogger.Instance;

            try
            {
                // ---- 1. 读 A 组（预选）----
                var aList = new List<ElementId>();
                var selIds = uiDoc.Selection.GetElementIds();
                if (selIds != null)
                {
                    foreach (var id in selIds)
                    {
                        var e = doc.GetElement(id);
                        if (e is Pipe || e is Duct || e is CableTray) aList.Add(id);
                    }
                }
                if (aList.Count < 1)
                {
                    message = "请先选中至少 1 个 A 组元素（管道/风管/线槽）";
                    return Result.Failed;
                }
                log.AppendLine("A 组数量：" + aList.Count);

                // ---- 2. 参数对话框 ----
                var dlg = new ConnectorDialog(45.0);
                if (dlg.ShowDialog() != true || !dlg.Confirmed)
                {
                    message = "用户取消参数输入";
                    return Result.Cancelled;
                }
                double thetaDeg = dlg.AngleDeg;
                log.AppendLine("角度 θ = " + thetaDeg.ToString("F1") + "°");

                // ---- 3. 拾取 B 组 ----
                IList<Reference> refs;
                try
                {
                    refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new MEPCurveSelectionFilter(),
                        "请选取 B 组元素（可框选多个）");
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
                var bList = new List<ElementId>();
                foreach (var r in refs) bList.Add(r.ElementId);
                log.AppendLine("B 组数量：" + bList.Count);

                // ---- 4. 数量校验 ----
                if (aList.Count != bList.Count)
                {
                    message = string.Format("A 组 ({0}) 与 B 组 ({1}) 数量不一致", aList.Count, bList.Count);
                    return Result.Failed;
                }

                // ---- 5. 配对 ----
                var aElems = new List<Element>();
                foreach (var id in aList) aElems.Add(doc.GetElement(id));
                var bElems = new List<Element>();
                foreach (var id in bList) bElems.Add(doc.GetElement(id));

                var pairs = MEPConnectorCalculator.MatchPairs(aElems, bElems);

                // ---- 6. 逐对校验 ----
                int idx = 0;
                foreach (var p in pairs)
                {
                    idx++;
                    var a = doc.GetElement(p.AId);
                    var b = doc.GetElement(p.BId);
                    log.AppendLine(string.Format(
                        "\n--- 配对 {0}: A={1} ↔ B={2}（匹配距离 {3:F1}mm）---",
                        idx, p.AId.IntegerValue, p.BId.IntegerValue, p.Distance * 304.8));

                    string reason;
                    if (!MEPConnectorCalculator.IsHorizontallyCollinear(a, b, out reason))
                    {
                        message = string.Format("配对 {0} 共线校验失败：{1}", idx, reason);
                        return Result.Failed;
                    }
                    if (!MEPConnectorCalculator.HasCompatibleSystemType(a, b, out reason))
                    {
                        message = string.Format("配对 {0} 系统类型校验失败：{1}", idx, reason);
                        return Result.Failed;
                    }
                }
                log.AppendLine("\n所有配对通过共线/系统类型校验");

                // ---- 7. 处理 ----
                int success = 0, fail = 0;
                using (var tx = new Transaction(doc, "连接两组 MEP"))
                {
                    tx.Start();
                    var svc = new MEPConnectorService(log);
                    foreach (var p in pairs)
                    {
                        try
                        {
                            if (svc.ProcessPair(doc, p.AId, p.BId, thetaDeg)) success++;
                            else fail++;
                        }
                        catch (Exception ex)
                        {
                            log.AppendLine("✗ 处理失败：" + ex.Message);
                            log.AppendLine("  堆栈：" + ex.StackTrace);
                            fail++;
                        }
                    }
                    tx.Commit();
                }

                log.AppendLine(string.Format("\n完成：成功 {0}, 失败 {1}", success, fail));

                var td = new TaskDialog("MEP 连接");
                td.MainInstruction = string.Format("处理完成：成功 {0}, 失败 {1}", success, fail);
                td.MainContent = log.GetFullText();
                td.CommonButtons = TaskDialogCommonButtons.Ok;
                td.Show();

                return success > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
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

    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element e)
        { return e is Pipe || e is Duct || e is CableTray; }
        public bool AllowReference(Reference r, XYZ position) { return true; }
    }
}