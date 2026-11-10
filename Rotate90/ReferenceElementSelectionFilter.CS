using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.Rotate90
{
    /// <summary>
    /// 参考元素过滤器：允许选择任意元素，后续再验证能否提取轴线。
    /// </summary>
    public class ReferenceElementSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem) => true;

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>
    /// 目标元素过滤器：只允许 MEP 模型元素。
    /// </summary>
    public class MEPTargetSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem?.Category == null) return false;
            CategoryType type = elem.Category.CategoryType;
            return type == CategoryType.Model || type == CategoryType.Annotation;
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }
}