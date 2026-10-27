using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using MEPTools.Shared; // 👈 引入公共日志

namespace MEPTools.MEPPlanarConnector
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            // 1. 获取当前选中的元素
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count < 2)
            {
                TaskDialog.Show("提示", "请至少选中 2 个 MEP 元素（如 2 根管道/风管/线槽）后再运行此命令。\n\n" +
                                        "如果是多对多连接，请按配对顺序交替选中（A1, B1, A2, B2...）。");
                return Result.Cancelled;
            }

            // 2. 过滤并提取有效的 MEP 元素
            List<Element> validElems = new List<Element>();
            foreach (ElementId id in selectedIds)
            {
                Element el = doc.GetElement(id);
                if (el is MEPCurve)
                {
                    validElems.Add(el);
                }
            }

            if (validElems.Count < 2)
            {
                TaskDialog.Show("提示", "选中的元素中未包含至少 2 个有效的 MEP 元素（管道、风管或线槽）。");
                return Result.Cancelled;
            }

            // 3. 将选中的元素分成 A、B 两组进行配对
            List<Element> aList = new List<Element>();
            List<Element> bList = new List<Element>();

            if (validElems.Count == 2)
            {
                // 刚好选了两个，直接 A[0] 和 B[0]
                aList.Add(validElems[0]);
                bList.Add(validElems[1]);
            }
            else
            {
                // 选了多个（偶数个），按选择顺序对半劈开
                int half = validElems.Count / 2;
                aList.AddRange(validElems.Take(half));
                bList.AddRange(validElems.Skip(half));
            }

            // 配对校验
            List<ConnectorPair> pairs = MEPPlanarConnectorCalculator.MatchPairs(aList, bList);
            if (pairs.Count == 0)
            {
                TaskDialog.Show("提示", "无法计算出有效的配对，请检查选择。");
                return Result.Cancelled;
            }

            // 4. 开启日志会话
            MEPLogger.Instance.StartSession("MEP 平面连接");

            // 5. 执行连接（开启事务）
            MEPPlanarConnectorService service = new MEPPlanarConnectorService();
            int successCount = 0;
            int failCount = 0;

            using (Transaction trans = new Transaction(doc, "MEP 平面连接"))
            {
                trans.Start();

                foreach (var pair in pairs)
                {
                    Element elA = doc.GetElement(pair.AId);
                    Element elB = doc.GetElement(pair.BId);

                    if (elA == null || elB == null)
                    {
                        MEPLogger.Instance.AppendLine($"✗ 配对元素丢失 (A:{pair.AId}, B:{pair.BId})");
                        failCount++;
                        continue;
                    }

                    // 校验系统类型是否兼容
                    if (!MEPPlanarConnectorCalculator.HasCompatibleSystemType(elA, elB, out string reason))
                    {
                        MEPLogger.Instance.AppendLine($"✗ 跳过配对 {elA.Id} & {elB.Id}: {reason}");
                        failCount++;
                        continue;
                    }

                    // 调用核心服务类进行处理
                    if (service.ProcessPair(doc, pair.AId, pair.BId))
                    {
                        successCount++;
                    }
                    else
                    {
                        failCount++;
                    }
                }

                trans.Commit();
            }

            // 6. 结束日志会话
            MEPLogger.Instance.EndSession($"成功 {successCount} 对，失败 {failCount} 对");

            // 7. 结果反馈
            string resultMsg = $"平面连接完成！\n\n成功：{successCount} 对\n失败：{failCount} 对";

            if (failCount > 0)
            {
                TaskDialog td = new TaskDialog("处理结果");
                td.MainInstruction = "部分或全部配对处理失败";
                td.MainContent = resultMsg + $"\n\n详细日志请查看：\n{MEPLogger.Instance.LogFilePath}";
                td.Show();
            }
            else if (successCount > 0)
            {
                // 全部成功也可以静默，或弹个简单的 TaskDialog
                TaskDialog.Show("平面连接", resultMsg);
            }

            return Result.Succeeded;
        }
    }
}