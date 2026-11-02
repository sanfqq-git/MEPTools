using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

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
    }
}