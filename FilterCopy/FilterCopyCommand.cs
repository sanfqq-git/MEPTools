using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;   // ← 新增
using MEPTools.FilterCopy.Services;
using MEPTools.FilterCopy.ViewModels;
using MEPTools.FilterCopy.Views;

namespace MEPTools.FilterCopy.Commands
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class FilterCopyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            Reference picked;
            try
            {
                // ObjectType.Element 是 Autodesk.Revit.UI.Selection.ObjectType
                picked = uiDoc.Selection.PickObject(ObjectType.Element,
                                                  new ViewSelectionFilter(), "请选择源视图");
            }
            catch
            {
                return Result.Cancelled;
            }

            if (!(doc.GetElement(picked) is View sourceView) || !ViewFilterSupport.IsSupported(sourceView))
            {
                TaskDialog.Show("批量复制视图过滤器", "所选源视图不支持过滤器。");
                return Result.Cancelled;
            }

            var targets = new ViewSelectionService(doc).CollectTargets(sourceView.Id);
            if (targets.Count == 0)
            {
                TaskDialog.Show("批量复制视图过滤器", "项目中没有其他视图可作为目标。");
                return Result.Cancelled;
            }

            var vm = new MainViewModel(doc, sourceView, targets);
            var win = new MainWindow(vm);
            win.ShowDialog();

            return Result.Succeeded;
        }
    }

    internal class ViewSelectionFilter : ISelectionFilter   // ← 改为 ISelectionFilter
    {
        public bool AllowElement(Element elem) => elem is View;
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}