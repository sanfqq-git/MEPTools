using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

            var validElems = GetSelectedCurves(uidoc, doc);
            if (validElems.Count < 2)
            {
                TaskDialog.Show("提示", "请选中至少 2 个 MEP 元素。\n\n（共线模式：用于同轴管道接长）");
                return Result.Cancelled;
            }

            var (aList, bList) = SplitGroups(validElems);
            var pairs = PlanarConnectorCalculator.MatchPairs(aList, bList);

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

                    // 仅检查大类是否一致
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
            TaskDialog.Show("共线连接", $"完成！\n成功：{success} 对\n失败：{fail} 对");
            return Result.Succeeded;
        }

        private List<Element> GetSelectedCurves(UIDocument uidoc, Document doc)
        {
            return uidoc.Selection.GetElementIds()
                .Select(id => doc.GetElement(id))
                .Where(el => el is MEPCurve)
                .ToList();
        }

        private (List<Element>, List<Element>) SplitGroups(List<Element> all)
        {
            if (all.Count == 2) return (new List<Element> { all[0] }, new List<Element> { all[1] });
            int half = all.Count / 2;
            return (all.Take(half).ToList(), all.Skip(half).ToList());
        }
    }
}