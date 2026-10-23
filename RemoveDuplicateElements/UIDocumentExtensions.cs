using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using System.Collections.Generic;

namespace RemoveDuplicateElements
{
    public static class UIDocumentExtensions
    {
        /// <summary>
        /// 在 PickObjects 过程中支持按空格键结束选择（相当于点击“完成”按钮）。
        /// </summary>
        public static IList<Reference> PickObjectsWithSpaceFinish(this UIDocument uidoc, ObjectType objectType, string prompt)
        {
            using (var shortcut = new KeyboardShortcutHelper())
            {
                shortcut.StartListening();
                return uidoc.Selection.PickObjects(objectType, prompt);
            }
        }
    }
}