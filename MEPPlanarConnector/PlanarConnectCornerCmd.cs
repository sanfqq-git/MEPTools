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
    public class CornerCommand : IExternalCommand
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
                    // ---------- 2. A 组 ----------
                    if (preselectedCurves.Count > 0)
                    {
                        aList = preselectedCurves;
                        uidoc.Selection.SetElementIds(new List<ElementId>());
                        MEPLogger.Instance.AppendLine(
                            $"[Corner] 使用预选 A 组 {aList.Count} 个元素。");
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

            var pairs = PlanarConnectorCalculator.MatchPairs(aList, bList, true);

            if (pairs.Count == 0)
            {
                InfoCenterTip.Show("未能匹配到任何一对合法元素，请检查是否选错了模式。");
                return Result.Cancelled;
            }

            // ---------- 5. 执行 ----------
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

                    if (!IsSystemTypeCompatible(elA, elB))
                    {
                        MEPLogger.Instance.AppendLine(
                            $"⚠ 警告: {elA.Id} & {elB.Id} 系统类型不一致，仍尝试连接");
                    }

                    if (service.ProcessCorner(doc, elA, elB)) success++;
                    else fail++;
                }
                trans.Commit();
            }

            MEPLogger.Instance.EndSession($"成功 {success} 对，失败 {fail} 对");

            // ---------- 6. 结果用 InfoCenter 显示 ----------
            string hint = (success == 0 && fail > 0)
                ? "（若两管平行错开，请用【共线连接】命令）"
                : "";
            InfoCenterTip.Show($"转角连接完成：成功 {success} 对，失败 {fail} 对{hint}");

            return Result.Succeeded;
        }

        private bool IsSystemTypeCompatible(Element a, Element b)
        {
            var ta = PlanarConnectorCalculator.GetElementType(a);
            var tb = PlanarConnectorCalculator.GetElementType(b);

            if (ta == MEPElementType.CableTray) return true;

            var sysA = PlanarConnectorCalculator.GetSystemTypeId(a, ta);
            var sysB = PlanarConnectorCalculator.GetSystemTypeId(b, tb);
            return sysA == sysB;
        }
    }

    // 全局管道选择过滤器
    public class MEPCurveSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) { return elem is MEPCurve; }
        public bool AllowReference(Reference reference, XYZ position) { return false; }
    }
}