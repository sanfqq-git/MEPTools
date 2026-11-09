using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using MEPTools.MEPConnector.Planar.Core;

namespace MEPTools.MEPConnector.Planar.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CollinearCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            // ---------- 1. 读取预选 ----------
            var preselectedIds = uidoc.Selection.GetElementIds();
            var preselectedCurves = preselectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e is MEPCurve)
                .ToList();

            List<Element> aList = new List<Element>();
            List<Element> bList = new List<Element>();

            var filter = new MEPCurveSelectionFilter();

            using (var spaceFinisher = new SpaceKeyFinisher(Keys.Space, "完成", "Finish", "Complete"))
            {
                spaceFinisher.Enable();

                try
                {
                    if (preselectedCurves.Count > 0)
                    {
                        aList = preselectedCurves;
                        uidoc.Selection.SetElementIds(new List<ElementId>());
                        MEPLogger.Instance.AppendLine(
                            $"[Collinear] 使用预选 A 组 {aList.Count} 个元素。");
                        InfoCenterTip.Show(
                            $"已识别预选 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }
                    else
                    {
                        InfoCenterTip.Show("请选择 A 组 MEP 元素（按空格键结束）");
                        IList<Reference> refsA = uidoc.Selection.PickObjects(
                            ObjectType.Element, filter, "第一步：请选择 A 组元素（按空格键结束）");
                        aList = refsA.Select(r => doc.GetElement(r)).ToList();
                        uidoc.Selection.SetElementIds(new List<ElementId>());
                        InfoCenterTip.Show(
                            $"已选择 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }

                    IList<Reference> refsB = uidoc.Selection.PickObjects(
                        ObjectType.Element, filter, "第二步：请选择 B 组元素（按空格键结束）");
                    bList = refsB.Select(r => doc.GetElement(r)).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled;
                }
                finally
                {
                    spaceFinisher.Disable();
                }
            }

            // ---------- 4. 校验 ----------
            if (!aList.Any() || !bList.Any())
            {
                InfoCenterTip.Show("A 组或 B 组未选择任何有效管道，操作取消。");
                return Result.Cancelled;
            }

            var pairs = PlanarConnectorCalculator.MatchPairs(aList, bList, false);

            if (pairs.Count == 0)
            {
                InfoCenterTip.Show("未能匹配到任何一对合法元素，请检查是否选错了模式。");
                return Result.Cancelled;
            }

            // ---------- 前置水平对齐检测 ----------
            int nonCollinearCount = 0;
            double minDeviationMm = 1.0;
            var pairsWithDeviation = new List<ConnectorPair>();

            foreach (var pair in pairs)
            {
                Element elA = doc.GetElement(pair.AId);
                Element elB = doc.GetElement(pair.BId);
                if (elA == null || elB == null) continue;

                double dev = PlanarConnectorCalculator.GetHorizontalDeviation(elA, elB);
                if (dev != double.MaxValue && dev * PlanarConnectorCalculator.FtToMm > minDeviationMm)
                {
                    nonCollinearCount++;
                    pairsWithDeviation.Add(pair);
                }
            }

            bool doAlign = false;
            if (nonCollinearCount > 0)
            {
                var dialog = new TaskDialog("检测到共线偏差");
                dialog.MainInstruction = $"检测到 {nonCollinearCount} 对元素存在水平偏差（> {minDeviationMm}mm）";
                dialog.MainContent = "强行合并会导致管线扭曲。\n\n是否自动在水平面上平移 B 组元素，使其与 A 组完全共线？\n\n选择“是”将为您执行前置准备，消除偏差后再合并。\n选择“否”将取消本次操作。";
                dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                dialog.DefaultButton = TaskDialogResult.Yes;

                if (dialog.Show() == TaskDialogResult.Yes) doAlign = true;
                else
                {
                    InfoCenterTip.Show("已取消：元素存在水平偏差且未纠偏");
                    return Result.Cancelled;
                }
            }

            // ---------- 5. 执行 ----------
            MEPLogger.Instance.StartSession("MEP 共线延长");
            var service = new PlanarConnectorService();
            int success = 0, fail = 0;

            using (Transaction trans = new Transaction(doc, "MEP 共线延长"))
            {
                trans.Start();

                if (doAlign)
                {
                    int alignCount = 0, alignFail = 0;
                    foreach (var pair in pairsWithDeviation)
                    {
                        Element elA = doc.GetElement(pair.AId);
                        Element elB = doc.GetElement(pair.BId);
                        if (elA == null || elB == null) continue;

                        if (service.AlignPair(doc, elA, elB)) alignCount++;
                        else
                        {
                            alignFail++;
                            MEPLogger.Instance.AppendLine(
                                $"⚠ 对齐失败: {pair.AId} → {pair.BId}（不平行或元素无效）");
                        }
                    }
                    doc.Regenerate();
                    MEPLogger.Instance.AppendLine(
                        $"已自动水平对齐 {alignCount} 对元素（失败 {alignFail} 对）。\n");
                }

                foreach (var pair in pairs)
                {
                    Element elA = doc.GetElement(pair.AId);
                    Element elB = doc.GetElement(pair.BId);

                    if (elA == null || elB == null)
                    {
                        MEPLogger.Instance.AppendLine($"✗ 跳过 {pair.AId} & {pair.BId}: 元素已在之前的合并中被删除");
                        fail++;
                        continue;
                    }

                    if (!PlanarConnectorCalculator.IsSameType(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine($"✗ 跳过 {elA.Id} & {elB.Id}: 元素类型不同");
                        fail++;
                        continue;
                    }

                    if (service.ProcessCollinear(doc, elA, elB)) success++;
                    else fail++;
                }
                trans.Commit();
            }

            MEPLogger.Instance.EndSession($"成功 {success} 对，失败 {fail} 对");

            // ---------- 6. 结果展示 ----------
            string hint = (success == 0 && fail > 0)
                ? "（若两管垂直交叉，请用【转角连接】命令）"
                : "";
            InfoCenterTip.Show($"共线连接完成：成功 {success} 对，失败 {fail} 对{hint}");

            // 【新增】失败详情用 TaskDialog 展开，让用户知道"为什么没合上"
            if (fail > 0)
            {
                var td = new TaskDialog("MEP 共线连接结果");
                td.MainInstruction = $"成功 {success} 对，失败 {fail} 对";
                td.MainContent = "部分配对失败，日志详情见展开内容：";
                td.ExpandedContent = MEPLogger.Instance.GetFullText();
                td.CommonButtons = TaskDialogCommonButtons.Ok;
                td.Show();
            }

            return Result.Succeeded;
        }
    }
}