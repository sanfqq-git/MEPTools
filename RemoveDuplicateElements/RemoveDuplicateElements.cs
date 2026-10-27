using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.RemoveDuplicateElements
{
    [Transaction(TransactionMode.Manual)]
    public class Command : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;

            // 1. 获取用户预选元素或手动选择
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                try
                {
                    // 使用扩展方法：支持空格键结束选择
                    IList<Reference> pickedRefs = uidoc.PickObjectsWithSpaceFinish(
                        ObjectType.Element,
                        "请选择要处理的元素（可多选），按空格键结束选择");

                    selectedIds = pickedRefs.Select(r => r.ElementId).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                {
                    return Result.Cancelled; // 用户取消选择
                }
            }

            if (selectedIds.Count == 0)
            {
                message = "没有选择任何元素。";
                return Result.Cancelled;
            }

            // 2. 定义要处理的类别
            HashSet<BuiltInCategory> targetCategories = new HashSet<BuiltInCategory>
            {
                BuiltInCategory.OST_DataDevices,
                BuiltInCategory.OST_NurseCallDevices,
                BuiltInCategory.OST_ElectricalFixtures,
                BuiltInCategory.OST_CommunicationDevices
            };

            // 3. 收集元素信息：元素、位置、族名称
            var elementInfos = new List<(Element Elem, XYZ Point, string FamilyName)>();
            foreach (ElementId id in selectedIds)
            {
                Element elem = doc.GetElement(id);
                if (elem?.Category == null) continue;

                if (!targetCategories.Contains((BuiltInCategory)elem.Category.Id.IntegerValue))
                    continue;

                if (!(elem.Location is LocationPoint locPoint))
                    continue;

                string familyName;
                if (elem is FamilyInstance fi)
                {
                    familyName = fi.Symbol.Family.Name;
                }
                else
                {
                    familyName = elem.Category.Name;
                }

                elementInfos.Add((elem, locPoint.Point, familyName));
            }

            if (elementInfos.Count == 0)
            {
                message = "选中的元素中没有符合条件的可处理元素。";
                return Result.Failed;
            }

            // 4. 设置容差（0.001 英尺 ≈ 0.3 毫米）
            double tolerance = 0.001;

            // 按族名称分组
            var familyGroups = elementInfos.GroupBy(info => info.FamilyName);
            List<ElementId> toDeleteIds = new List<ElementId>();

            foreach (var familyGroup in familyGroups)
            {
                var keyedItems = familyGroup.Select(info => new
                {
                    Info = info,
                    Key = (
                        X: (long)Math.Round(info.Point.X / tolerance),
                        Y: (long)Math.Round(info.Point.Y / tolerance),
                        Z: (long)Math.Round(info.Point.Z / tolerance)
                    )
                });

                var positionGroups = keyedItems.GroupBy(x => x.Key);

                foreach (var group in positionGroups)
                {
                    if (group.Count() <= 1) continue;

                    bool first = true;
                    foreach (var item in group)
                    {
                        if (first)
                        {
                            first = false;
                            continue;
                        }
                        toDeleteIds.Add(item.Info.Elem.Id);
                    }
                }
            }

            // 5. 执行删除事务
            if (toDeleteIds.Count > 0)
            {
                using (Transaction trans = new Transaction(doc, "删除重复元素"))
                {
                    trans.Start();
                    try
                    {
                        doc.Delete(toDeleteIds);
                        trans.Commit();
                    }
                    catch (Exception ex)
                    {
                        trans.RollBack();
                        message = $"删除失败：{ex.Message}";
                        return Result.Failed;
                    }
                }
            }
            else
            {
                // 无重复时输出诊断信息
                StringBuilder sb = new StringBuilder("未找到位置重复的元素。选中元素信息（前10个）及分组键：");
                foreach (var info in elementInfos.Take(10))
                {
                    long xKey = (long)Math.Round(info.Point.X / tolerance);
                    long yKey = (long)Math.Round(info.Point.Y / tolerance);
                    long zKey = (long)Math.Round(info.Point.Z / tolerance);
                    sb.Append($"\n族：{info.FamilyName}，原始坐标：({info.Point.X:F10}, {info.Point.Y:F10}, {info.Point.Z:F10})，分组键：({xKey}, {yKey}, {zKey})");
                }
                if (elementInfos.Count > 10)
                    sb.Append($"\n……等共 {elementInfos.Count} 个元素。");
                message = sb.ToString();
                return Result.Failed;
            }

            return Result.Succeeded;
        }
    }
}