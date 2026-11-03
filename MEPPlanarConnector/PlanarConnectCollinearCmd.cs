using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;
using MEPTools.MEPPlanarConnector.Core;

namespace MEPTools.MEPPlanarConnector.Commands
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
                // 先启用热键（确保选择过程中空格键能触发“完成”按钮）
                spaceFinisher.Enable();

                try
                {
                    // ---------- 2. A 组 ----------
                    if (preselectedCurves.Count > 0)
                    {
                        // 有预选：直接用预选作为 A 组
                        aList = preselectedCurves;
                        uidoc.Selection.SetElementIds(new List<ElementId>());
                        MEPLogger.Instance.AppendLine(
                            $"[Collinear] 使用预选 A 组 {aList.Count} 个元素。");
                        InfoCenterTip.Show(
                            $"已识别预选 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }
                    else
                    {
                        // 无预选：InfoCenter 提示 + 弹出选择
                        InfoCenterTip.Show("请选择 A 组 MEP 元素（按空格键结束）");

                        IList<Reference> refsA = uidoc.Selection.PickObjects(
                            ObjectType.Element, filter, "第一步：请选择 A 组元素（按空格键结束）");
                        aList = refsA.Select(r => doc.GetElement(r)).ToList();

                        // 清空当前选择，避免选 B 组时混淆
                        uidoc.Selection.SetElementIds(new List<ElementId>());

                        InfoCenterTip.Show(
                            $"已选择 A 组 {aList.Count} 个，请选择 B 组（空格键确认）");
                    }

                    // ---------- 3. B 组 ----------
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

            // ---------- 5. 执行 ----------
            MEPLogger.Instance.StartSession("MEP 共线延长");
            var service = new PlanarConnectorService();
            int success = 0, fail = 0;

            using (Transaction trans = new Transaction(doc, "MEP 共线延长"))
            {
                trans.Start();
                foreach (var pair in pairs)
                {
                    Element elA = doc.GetElement(pair.AId);
                    Element elB = doc.GetElement(pair.BId);

                    // 【关键修复】：如果在之前的配对中，B 已经被吞噬删除了，这里会拿到 null，直接跳过
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

            // ---------- 6. 结果用 InfoCenter 显示 ----------
            string hint = (success == 0 && fail > 0)
                ? "（若两管垂直交叉，请用【转角连接】命令）"
                : "";
            InfoCenterTip.Show($"共线连接完成：成功 {success} 对，失败 {fail} 对{hint}");

            return Result.Succeeded;
        }
    }
}