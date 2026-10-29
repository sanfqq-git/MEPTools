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
    public class CornerCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc?.Document;
            if (doc == null) return Result.Cancelled;

            var validElems = GetSelectedCurves(uidoc, doc);
            if (validElems.Count < 2)
            {
                TaskDialog.Show("提示", "请选中至少 2 个 MEP 元素。\n\n（转角模式：延长并生成弯头）");
                return Result.Cancelled;
            }

            var (aList, bList) = SplitGroups(validElems);
            var pairs = PlanarConnectorCalculator.MatchPairs(aList, bList);

            MEPLogger.Instance.StartSession("MEP 转角连接");
            var service = new PlanarConnectorService();
            int success = 0, fail = 0;

            using (Transaction trans = new Transaction(doc, "MEP 转角连接"))
            {
                trans.Start();
                foreach (var pair in pairs)
                {
                    Element elA = doc.GetElement(pair.AId);
                    Element elB = doc.GetElement(pair.BId);

                    if (!PlanarConnectorCalculator.IsSameType(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine($"✗ 跳过 {elA.Id} & {elB.Id}: 元素类型不同");
                        fail++;
                        continue;
                    }

                    // 转角连接做系统警告提示，但不阻断
                    if (!IsSystemTypeCompatible(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine($"⚠ 警告: {elA.Id} & {elB.Id} 系统类型不一致，仍尝试连接");
                    }

                    if (service.ProcessCorner(doc, elA, elB)) success++;
                    else fail++;
                }
                trans.Commit();
            }

            MEPLogger.Instance.EndSession($"成功 {success} 对，失败 {fail} 对");
            TaskDialog.Show("转角连接", $"完成！\n成功：{success} 对\n失败：{fail} 对");
            return Result.Succeeded;
        }

        private bool IsSystemTypeCompatible(Element a, Element b)
        {
            var ta = PlanarConnectorCalculator.GetElementType(a);
            var tb = PlanarConnectorCalculator.GetElementType(b);
            
            // 修复：直接使用 MEPElementType 枚举，而不是 PlanarConnectorCalculator.MEPElementType
            if (ta == MEPElementType.CableTray) return true;

            var sysA = PlanarConnectorCalculator.GetSystemTypeId(a, ta);
            var sysB = PlanarConnectorCalculator.GetSystemTypeId(b, tb);
            return sysA == sysB;
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