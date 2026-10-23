using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.AlignBottomElevation
{
    [Transaction(TransactionMode.Manual)]
    public class AlignMEPBottomElevationCommand : IExternalCommand
    {
        // 嵌套过滤器，避免命名空间/文件遗漏问题
        public class MepElementSelectionFilter : ISelectionFilter
        {
            public bool AllowElement(Element elem) => elem is MEPCurve;
            public bool AllowReference(Reference reference, XYZ position) => true;
        }

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            // ---------- 步骤 1：获取用户预选元素 ----------
            ICollection<ElementId> preSelectedIds = uidoc.Selection.GetElementIds();
            if (preSelectedIds.Count == 0)
            {
                TaskDialog.Show("提示", "请先在视图中预选需要底部平齐的 MEP 元素。");
                return Result.Cancelled;
            }

            List<Element> validMepElements = preSelectedIds
                .Select(id => doc.GetElement(id))
                .Where(e => e is MEPCurve)
                .ToList();

            if (validMepElements.Count == 0)
            {
                TaskDialog.Show("提示", "预选元素中没有有效的 MEP 元素（管道/风管/桥架）。");
                return Result.Cancelled;
            }

            // ---------- 步骤 1b：检测 Reference Level 是否一致 ----------
            Level firstLevel = GetReferenceLevel(validMepElements[0]);
            bool allSameLevel = true;
            List<string> inconsistentInfo = new List<string>();

            foreach (Element elem in validMepElements)
            {
                Level currentLevel = GetReferenceLevel(elem);
                if (currentLevel == null || firstLevel == null)
                {
                    allSameLevel = false;
                    inconsistentInfo.Add($"{elem.Id.IntegerValue}: 无法获取参照标高");
                    continue;
                }

                if (currentLevel.Id != firstLevel.Id)
                {
                    allSameLevel = false;
                    inconsistentInfo.Add(
                        $"{elem.Id.IntegerValue}: 参照标高为 \"{currentLevel.Name}\"，" +
                        $"与首个元素的 \"{firstLevel.Name}\" 不一致");
                }
            }

            if (!allSameLevel)
            {
                string msg = "预选元素的参照标高不一致，无法执行底部平齐操作。\n\n" +
                             string.Join("\n", inconsistentInfo);
                TaskDialog.Show("参照标高不一致", msg);
                return Result.Cancelled;
            }

            // ---------- 步骤 2：让用户点选固定元素 ----------
            Reference pickedRef;
            try
            {
                pickedRef = uidoc.Selection.PickObject(
                    ObjectType.Element,
                    new MepElementSelectionFilter(),
                    "请点选固定元素（作为底部标高的基准）");
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }

            Element fixedElement = doc.GetElement(pickedRef);
            if (fixedElement == null)
            {
                TaskDialog.Show("错误", "未能获取到固定元素。");
                return Result.Cancelled;
            }

            double? targetBottomElevation = GetBottomElevation(fixedElement);
            if (!targetBottomElevation.HasValue)
            {
                TaskDialog.Show("错误",
                    $"无法获取元素 {fixedElement.Id.IntegerValue} 的底部标高。" +
                    "请确认该元素为管道、风管或电缆桥架。");
                return Result.Cancelled;
            }

            // ---------- 步骤 3：批量设置底部标高 ----------
            using (Transaction tx = new Transaction(doc, "MEP 底部平齐"))
            {
                tx.Start();

                int successCount = 0;
                List<string> failMessages = new List<string>();

                foreach (Element elem in validMepElements)
                {
                    if (SetBottomElevation(elem, targetBottomElevation.Value))
                        successCount++;
                    else
                        failMessages.Add($"元素 {elem.Id.IntegerValue} 设置失败");
                }

                tx.Commit();

                string resultMsg = $"底部平齐完成。\n成功：{successCount} 个";
                if (failMessages.Count > 0)
                {
                    resultMsg += $"\n失败：{failMessages.Count} 个\n" +
                                 string.Join("\n", failMessages);
                }
                TaskDialog.Show("执行结果", resultMsg);
            }

            return Result.Succeeded;
        }

        // ==================== 辅助方法 ====================

        private static Level GetReferenceLevel(Element element)
        {
            if (element is MEPCurve mepCurve)
                return mepCurve.ReferenceLevel;

            Parameter levelParam = element.get_Parameter(
                BuiltInParameter.RBS_START_LEVEL_PARAM);
            if (levelParam != null && levelParam.HasValue)
            {
                ElementId levelId = levelParam.AsElementId();
                if (levelId != ElementId.InvalidElementId)
                    return element.Document.GetElement(levelId) as Level;
            }

            return null;
        }

        private static double? GetBottomElevation(Element element)
        {
            BuiltInParameter? bottomParam = GetBottomElevationParameter(element);
            if (!bottomParam.HasValue) return null;

            Parameter param = element.get_Parameter(bottomParam.Value);
            if (param == null || !param.HasValue) return null;

            return param.AsDouble();
        }

        private static bool SetBottomElevation(Element element, double elevation)
        {
            BuiltInParameter? bottomParam = GetBottomElevationParameter(element);
            if (!bottomParam.HasValue) return false;

            Parameter param = element.get_Parameter(bottomParam.Value);
            if (param == null || param.IsReadOnly) return false;

            return param.Set(elevation);
        }

        private static BuiltInParameter? GetBottomElevationParameter(Element element)
        {
            if (element is Duct)
                return BuiltInParameter.RBS_DUCT_BOTTOM_ELEVATION;
            if (element is Pipe)
                return BuiltInParameter.RBS_PIPE_BOTTOM_ELEVATION;
            if (element is CableTray)
                return BuiltInParameter.RBS_CTC_BOTTOM_ELEVATION;

            if (element is MEPCurve)
            {
                int catId = element.Category?.Id.IntegerValue ?? -1;
                if (catId == (int)BuiltInCategory.OST_DuctCurves)
                    return BuiltInParameter.RBS_DUCT_BOTTOM_ELEVATION;
                if (catId == (int)BuiltInCategory.OST_PipeCurves)
                    return BuiltInParameter.RBS_PIPE_BOTTOM_ELEVATION;
                if (catId == (int)BuiltInCategory.OST_CableTray)
                    return BuiltInParameter.RBS_CTC_BOTTOM_ELEVATION;
            }

            return null;
        }
    }
}