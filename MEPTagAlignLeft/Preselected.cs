using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace TagAlignLeft
{
    /// <summary>
    /// 预选辅助类：从当前选择集读取符合条件的选择。
    /// </summary>
    public static class Preselected
    {
        /// <summary>
        /// 获取当前选择集中，属于指定视图的 IndependentTag。
        /// </summary>
        public static List<IndependentTag> GetSelectedIndependentTags(
            UIDocument uidoc, View activeView)
        {
            List<IndependentTag> tags = new List<IndependentTag>();
            Document doc = uidoc.Document;
            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();

            foreach (ElementId id in selectedIds)
            {
                Element elem = doc.GetElement(id);
                if (elem is IndependentTag tag && tag.OwnerViewId == activeView.Id)
                {
                    tags.Add(tag);
                }
            }
            return tags;
        }
    }
}