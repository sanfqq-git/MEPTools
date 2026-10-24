using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RemoveDuplicateTags
{
    [Transaction(TransactionMode.Manual)]
    public class RemoveDuplicateTagsCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            // 1. 获取用户选择（可能包含目标元素，也可能包含想保留的 Tag）
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("提示", "请先在视图中选择要检查的 Generic Models。\n(如果你有想保留的 Tag，也可以一起选中，程序会优先保留它)。");
                return Result.Cancelled;
            }

            // 区分：目标元素 与 用户指定保留的 Tag
            HashSet<ElementId> targetHostIds = new HashSet<ElementId>();
            HashSet<ElementId> userPriorityTagIds = new HashSet<ElementId>();

            foreach (var id in selectedIds)
            {
                Element elem = doc.GetElement(id);
                if (elem is IndependentTag tag)
                {
                    userPriorityTagIds.Add(id);
                    // 如果用户只选了 Tag 没选设备，自动把 Tag 的宿主也加入处理目标
                    var hostIds = tag.GetTaggedLocalElementIds();
                    if (hostIds != null && hostIds.Count > 0)
                    {
                        targetHostIds.Add(hostIds.First());
                    }
                }
                else
                {
                    targetHostIds.Add(id);
                }
            }

            if (targetHostIds.Count == 0)
            {
                TaskDialog.Show("提示", "没有找到有效的目标元素，请重新选择。");
                return Result.Cancelled;
            }

            // 2. 收集当前活动视图中的所有独立标签 (IndependentTag)
            List<IndependentTag> allTags = new FilteredElementCollector(doc, activeView.Id)
                .OfClass(typeof(IndependentTag))
                .Cast<IndependentTag>()
                .ToList();

            // 3. 建立字典：Key = (宿主ID, 标签类型ID)，Value = 标签列表
            var tagDict = new Dictionary<(ElementId, ElementId), List<IndependentTag>>();

            foreach (var tag in allTags)
            {
                if (tag.IsHidden(activeView)) continue;

                ISet<ElementId> hostIds = tag.GetTaggedLocalElementIds();
                if (hostIds == null || hostIds.Count == 0) continue;

                ElementId hostId = hostIds.First();

                // 只处理目标宿主
                if (!targetHostIds.Contains(hostId)) continue;

                ElementId tagTypeId = tag.GetTypeId();
                if (tagTypeId == ElementId.InvalidElementId) continue;

                var key = (hostId, tagTypeId);

                if (!tagDict.ContainsKey(key))
                {
                    tagDict[key] = new List<IndependentTag>();
                }
                tagDict[key].Add(tag);
            }

            // 4. 筛选重复标签，并找出多余的
            List<IndependentTag> extraTags = new List<IndependentTag>();
            int duplicateHostCount = 0;
            
            StringBuilder diagInfo = new StringBuilder();
            int debugPrintCount = 0; 

            foreach (var kvp in tagDict)
            {
                var tagsOnHost = kvp.Value;
                if (tagsOnHost.Count > 1) // 发现重复
                {
                    duplicateHostCount++;
                    Element hostElem = doc.GetElement(kvp.Key.Item1);

                    // 使用带原因的排序
                    var sortedTags = tagsOnHost
                        .OrderBy(t => GetTagPriorityInfo(t, hostElem, activeView, userPriorityTagIds).Priority)
                        .ToList();

                    // 保留第 0 个，其余全部加入删除列表
                    for (int i = 1; i < sortedTags.Count; i++)
                    {
                        extraTags.Add(sortedTags[i]);
                    }

                    // 生成诊断信息（限制前 5 个元素）
                    if (debugPrintCount < 5)
                    {
                        string hostName = GetElementFriendlyName(hostElem);
                        diagInfo.AppendLine($"\n▶ 宿主: {hostName}");
                        
                        var keepTag = sortedTags[0];
                        var keepInfo = GetTagPriorityInfo(keepTag, hostElem, activeView, userPriorityTagIds);
                        diagInfo.AppendLine($"  [保留] ID: {keepTag.Id} (原因: {keepInfo.Reason})");
                        
                        for (int i = 1; i < sortedTags.Count; i++)
                        {
                            var delTag = sortedTags[i];
                            var delInfo = GetTagPriorityInfo(delTag, hostElem, activeView, userPriorityTagIds);
                            diagInfo.AppendLine($"  [删除] ID: {delTag.Id} (原因: {delInfo.Reason})");
                        }
                        debugPrintCount++;
                    }
                }
            }

            // 5. 结果处理与高亮选中
            if (extraTags.Count > 0)
            {
                List<ElementId> extraIds = extraTags.Select(t => t.Id).ToList();
                uidoc.Selection.SetElementIds(extraIds);

                StringBuilder msg = new StringBuilder();
                msg.AppendLine($"发现 {duplicateHostCount} 个元素存在重复标签。");
                msg.AppendLine($"已为您高亮选中 {extraTags.Count} 个多余的标签。");
                msg.AppendLine("请检查确认后，按键盘上的 [Delete] 键进行删除。\n");
                
                msg.AppendLine("====================");
                msg.AppendLine("详细诊断信息（仅显示前5个）：");
                msg.Append(diagInfo.ToString());
                
                if (duplicateHostCount > 5)
                {
                    msg.AppendLine($"\n... 还有 {duplicateHostCount - 5} 个元素存在重复标签，不再逐一显示。");
                }

                TaskDialog.Show("检查完成", msg.ToString());
                return Result.Succeeded;
            }
            else
            {
                TaskDialog.Show("检查完成", "在选中的元素中，未发现重复的标签。");
                return Result.Cancelled;
            }
        }

        /// <summary>
        /// 返回优先级权重与中文原因（值越小越优先保留）
        /// </summary>
        private (double Priority, string Reason) GetTagPriorityInfo(IndependentTag tag, Element hostElem, View activeView, HashSet<ElementId> userPriorityTagIds)
        {
            // 优先级 0：用户手动选中的 Tag（绝对白名单）
            if (userPriorityTagIds.Contains(tag.Id))
            {
                return (-1.0, "用户指定保留");
            }

            // 优先级 1：拥有水平引线
            if (tag.HasLeader && tag.LeaderEndCondition == LeaderEndCondition.Free)
            {
                try
                {
                    IList<Reference> refs = tag.GetTaggedReferences();
                    if (refs != null)
                    {
                        foreach (var reference in refs)
                        {
                            XYZ head = tag.TagHeadPosition;
                            XYZ elbow = tag.GetLeaderElbow(reference);
                            
                            if (elbow == null) continue;

                            XYZ vec = elbow - head;
                            if (vec.GetLength() > 0.001)
                            {
                                XYZ dir = vec.Normalize();
                                // 稍微放宽一点容差到 0.98 (允许轻微捕捉误差，约 11 度以内)
                                if (Math.Abs(dir.DotProduct(activeView.RightDirection)) > 0.98)
                                {
                                    return (0.0, "拥有水平引线");
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 优先级 2：按距离排序
            double dist = CalculateDistance(tag, hostElem);
            return (100.0 + dist, $"距离最近 ({dist * 304.8:F1} mm)");
        }

        private double CalculateDistance(IndependentTag tag, Element hostElem)
        {
            if (hostElem == null) return double.MaxValue;

            XYZ tagPosition = tag.TagHeadPosition;
            XYZ hostCenter = GetElementCenter(hostElem);

            if (hostCenter == null) return double.MaxValue;

            return tagPosition.DistanceTo(hostCenter);
        }

        private XYZ GetElementCenter(Element elem)
        {
            if (elem.Location is LocationPoint locPoint)
            {
                return locPoint.Point;
            }
            else if (elem.Location is LocationCurve locCurve)
            {
                Curve curve = locCurve.Curve;
                if (curve != null)
                {
                    return curve.Evaluate(0.5, true);
                }
            }
            return null;
        }

        private string GetElementFriendlyName(Element elem)
        {
            if (elem == null) return "未知元素";
            if (elem is FamilyInstance fi)
                return $"族: {fi.Symbol.Family.Name}, 类型: {fi.Name}";
            if (elem.Category != null)
                return $"类别: {elem.Category.Name}";
            return $"ID: {elem.Id}";
        }
    }
}