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
using MEPTools.MEPConnector.HighLow; 

using WinForms = System.Windows.Forms;

namespace MEPTools.MEPConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class AlignMEPCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;

            MEPLogger.Instance.StartSession("Align MEP 对齐两组元素");
            var log = MEPLogger.Instance;

            try
            {
                var aList = GetPreselectedCurves(uiDoc, doc);
                if (aList.Count > 0)
                {
                    InfoCenterTip.Show($"已使用预选的 {aList.Count} 个 A 组元素");
                    log.AppendLine($"A 组来自预选，数量：{aList.Count}");
                }
                else
                {
                    InfoCenterTip.Show("未检测到预选，请选择 A 组元素（按空格确认）");
                    aList = PickCurves(uiDoc, "请选择 A 组元素（可框选多个，按空格确认）");
                }

                if (aList.Count < 1)
                {
                    message = "请先选中至少 1 个 A 组元素";
                    return Result.Failed;
                }

                InfoCenterTip.Show("请选择 B 组元素（按空格确认）");
                var bList = PickCurves(uiDoc, "请选择 B 组元素（可框选多个，按空格确认）");
                if (bList.Count < 1)
                {
                    message = "用户取消 B 组拾取";
                    return Result.Failed;
                }

                if (aList.Count != bList.Count)
                {
                    message = $"A 组 ({aList.Count}) 与 B 组 ({bList.Count}) 数量不一致";
                    InfoCenterTip.Show(message);
                    return Result.Failed;
                }

                var aElems = new List<Element>();
                foreach (var id in aList) aElems.Add(doc.GetElement(id));
                var bElems = new List<Element>();
                foreach (var id in bList) bElems.Add(doc.GetElement(id));

                var pairs = MEPConnectorCalculator.MatchPairs(aElems, bElems);
                if (pairs.Count == 0)
                {
                    message = "找不到有效的配对。";
                    InfoCenterTip.Show(message);
                    return Result.Failed;
                }

                int success = 0, fail = 0;
                using (var tx = new Transaction(doc, "B组对齐到A组"))
                {
                    tx.Start();
                    foreach (var p in pairs)
                    {
                        var a = doc.GetElement(p.AId);
                        var b = doc.GetElement(p.BId);

                        // 【修改】改为 HighLow 专用 Aligner（保持原 1e-3 行为）
                        if (HighLowMEPAligner.AlignPair(doc, a, b))
                        {
                            success++;
                            log.AppendLine($"✓ 配对 {a.Id} → {b.Id}: 对齐成功");
                        }
                        else
                        {
                            fail++;
                            log.AppendLine($"✗ 配对 {a.Id} → {b.Id}: 对齐失败(不平行或竖直)");
                        }
                    }
                    tx.Commit();
                }

                InfoCenterTip.Show($"对齐完成：成功 {success}，失败 {fail}");
                return success > 0 ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                log.LogException("AlignMEPCommand", ex);
                return Result.Failed;
            }
            finally
            {
                MEPLogger.Instance.EndSession("完成");
            }
        }

        private static List<ElementId> GetPreselectedCurves(UIDocument uiDoc, Document doc)
        {
            var list = new List<ElementId>();
            var selIds = uiDoc.Selection.GetElementIds();
            if (selIds == null) return list;
            foreach (var id in selIds)
            {
                var e = doc.GetElement(id);
                if (e is Pipe || e is Duct || e is CableTray) list.Add(id);
            }
            return list;
        }

        private static List<ElementId> PickCurves(UIDocument uiDoc, string prompt)
        {
            var list = new List<ElementId>();
            try
            {
                using (var finisher = new SpaceKeyFinisher(WinForms.Keys.Space, "完成", "Finish"))
                {
                    finisher.Enable();
                    var refs = uiDoc.Selection.PickObjects(ObjectType.Element, new MEPCurveSelectionFilter(), prompt);
                    if (refs != null)
                        foreach (var r in refs) list.Add(r.ElementId);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            catch (Exception ex) { MEPLogger.Instance.LogException("PickCurves", ex); }
            return list;
        }
    }
}