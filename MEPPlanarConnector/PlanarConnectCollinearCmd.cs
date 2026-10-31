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

            var preselectedIds = uidoc.Selection.GetElementIds();
            var preselectedCurves = preselectedIds.Select(id => doc.GetElement(id)).Where(e => e is MEPCurve).ToList();

            List<Element> aList = new List<Element>();
            List<Element> bList = new List<Element>();
            
            var filter = new MEPCurveSelectionFilter();

            using (var spaceFinisher = new SpaceKeyFinisher(Keys.Space, "完成", "Finish", "Complete"))
            {
                // 修复 Bug：无条件先启用热键
                spaceFinisher.Enable(); 
                
                try
                {
                    if (preselectedCurves.Count > 0)
                    {
                        aList = preselectedCurves;
                        uidoc.Selection.SetElementIds(new List<ElementId>()); // 清空当前选择，避免框选 B 组时混淆
                        MEPLogger.Instance.AppendLine($"[Collinear] 已获取预选 A 组元素 {aList.Count} 个。");
                    }
                    else
                    {
                        IList<Reference> refsA = uidoc.Selection.PickObjects(
                            ObjectType.Element, filter, "第一步：请选择 A 组元素（按空格键结束）");
                        aList = refsA.Select(r => doc.GetElement(r)).ToList();
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

            if (!aList.Any() || !bList.Any())
            {
                TaskDialog.Show("提示", "A 组或 B 组未选择任何有效管道，操作取消。");
                return Result.Cancelled;
            }

            var pairs = PlanarConnectorCalculator.MatchPairs(aList, bList, false);

            if (pairs.Count == 0)
            {
                TaskDialog.Show("提示", "未能匹配到任何一对合法元素。\n请检查是否选错了模式。");
                return Result.Cancelled;
            }

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
            
            string hint = success == 0 && fail > 0 
                ? "\n\n提示：若两根管道是垂直交叉的，请使用【转角连接】命令。" 
                : "";
            TaskDialog.Show("共线连接", $"完成！\n成功：{success} 对\n失败：{fail} 对{hint}");
            
            return Result.Succeeded;
        }
    }
}