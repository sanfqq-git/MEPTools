using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MEPTools.Shared;              // ← SpaceKeyFinisher 所在命名空间
using System.Collections.Generic;

namespace MEPTools.RemoveDuplicateElements
{
    public static class UIDocumentExtensions
    {
        /// <summary>
        /// 在 PickObjects 过程中支持按空格键结束选择（相当于点击"完成"按钮）。
        /// </summary>
        public static IList<Reference> PickObjectsWithSpaceFinish(
            this UIDocument uidoc,
            ObjectType objectType,
            string prompt)
        {
            using (var finisher = new SpaceKeyFinisher())
            {
                finisher.Enable();
                return uidoc.Selection.PickObjects(objectType, prompt);
            }
            // using 块退出 → finisher.Dispose() → Disable()，钩子一定释放；
            // 无论 PickObjects 正常返回、抛 OperationCanceledException 还是其它异常。
        }
    }
}