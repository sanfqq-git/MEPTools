using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.Rotate90
{
    /// <summary>
    /// 参考元素过滤器：排除指定的目标元素，其余元素允许选择。
    /// 实际能否提取轴线由 TryGetRotationAxis 再验证。
    /// </summary>
    public class ReferenceElementSelectionFilter : ISelectionFilter
    {
        private readonly HashSet<ElementId> _excludeIds;

        public ReferenceElementSelectionFilter(IEnumerable<Element> excludeElements = null)
        {
            _excludeIds = new HashSet<ElementId>();
            if (excludeElements != null)
            {
                foreach (var e in excludeElements)
                    if (e != null) _excludeIds.Add(e.Id);
            }
        }

        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;
            return !_excludeIds.Contains(elem.Id);
        }

        public bool AllowReference(Reference reference, XYZ position) => true;
    }

    /// <summary>
    /// 目标元素过滤器：只允许 MEP 模型/注释元素。
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