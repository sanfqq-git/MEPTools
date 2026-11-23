using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.Shared
{
    public static class Preselected
    {
        public static List<IndependentTag> GetSelectedIndependentTags(
            UIDocument uidoc, View activeView)
        {
            List<IndependentTag> tags = new List<IndependentTag>();
            if (uidoc == null || activeView == null) return tags;

            Document doc = uidoc.Document;
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            
            // 使用 HashSet 进行去重，防止同时选中 Group 和内部的 Tag 导致重复添加
            HashSet<ElementId> addedTagIds = new HashSet<ElementId>();

            foreach (ElementId id in selectedIds)
            {
                Element elem = doc.GetElement(id);
                if (elem == null) continue;

                // 1. 直接选中的独立标记
                if (elem is IndependentTag tag)
                {
                    if (addedTagIds.Add(tag.Id))
                        tags.Add(tag);
                }
                // 2. 如果选中了组（Group），提取组内的标记
                else if (elem is Group group)
                {
                    var memberIds = group.GetMemberIds();
                    foreach (ElementId memberId in memberIds)
                    {
                        Element member = doc.GetElement(memberId);
                        if (member is IndependentTag memberTag)
                        {
                            if (addedTagIds.Add(memberTag.Id))
                                tags.Add(memberTag);
                        }
                    }
                }
                // 3. 如果选中了多类别标记（MultiReferenceAnnotation）
                else if (elem is MultiReferenceAnnotation mra)
                {
                    ElementId tagId = mra.TagId;
                    if (tagId != null && tagId != ElementId.InvalidElementId)
                    {
                        Element mraTag = doc.GetElement(tagId);
                        if (mraTag is IndependentTag innerTag)
                        {
                            if (addedTagIds.Add(innerTag.Id))
                                tags.Add(innerTag);
                        }
                    }
                }
            }
            return tags;
        }

        /// <summary>
        /// 通用取图元（只新增，不改上面 Tag 的方法）：
        /// 有预选直接用预选（按 filter 过滤，不符合的自动忽略并气泡提示数量）；
        /// 无预选时用气泡提示，并进入交互拾取 —— 拾取过程中由 SpaceKeyFinisher 负责“空格结束”。
        /// 具体的提示文案、取消处理、日志记录全部沿用 MEPSelectionHelper 的统一实现。
        /// </summary>
        /// <param name="uidoc">当前文档</param>
        /// <param name="filter">选择过滤器，null 表示不限类别</param>
        /// <param name="pickPrompt">进入拾取时显示在 Revit 状态栏的提示</param>
        /// <param name="tipNoPreselection">无预选时右上角气泡提示的文案</param>
        /// <param name="singlePick">true = 只允许选一个（此时不启用空格结束）</param>
        public static List<Element> GetSelectedElements(
            UIDocument uidoc,
            ISelectionFilter filter = null,
            string pickPrompt = null,
            string tipNoPreselection = null,
            bool singlePick = false)
        {
            if (uidoc == null) return new List<Element>();

            return MEPSelectionHelper.GetElements(
                uidoc,
                filter,
                pickPrompt ?? "请在视图中选择图元，按空格键结束",
                tipNoPreselection,
                1,
                true,
                singlePick);
        }
    }
}