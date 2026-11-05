using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace MEPTools.WorksetManager // ⭐ 命名空间必须与 Command 保持一致
{
    public class MEPElementSelectionFilter : ISelectionFilter
    {
        public bool AllowElement(Element elem)
        {
            if (elem == null) return false;
            
            // 获取工作集参数
            Parameter param = elem.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            
            // 只要有工作集参数且非只读，就允许选择
            return param != null && !param.IsReadOnly;
        }

        public bool AllowReference(Reference reference, XYZ position)
        {
            return false; // 只允许选择整个图元，不允许选择面或边
        }
    }
}