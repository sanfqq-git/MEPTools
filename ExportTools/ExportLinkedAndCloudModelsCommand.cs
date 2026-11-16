using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.ExportTools
{
    [Transaction(TransactionMode.Manual)]
    public class ExportLinkedAndCloudModelsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("单独导出模型");

            try
            {
                var allLinkTypes = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                    .Select(li => li.GetTypeId()).Distinct()
                    .Select(id => doc.GetElement(id) as RevitLinkType)
                    .Where(lt => lt != null).OrderBy(lt => lt.Name).ToList();

                List<ElementId> selectedLinkIds = new List<ElementId>();
                if (allLinkTypes.Count > 0)
                {
                    using (var form = new LinkSelectionForm(allLinkTypes))
                    {
                        if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                        selectedLinkIds = form.SelectedTypeIds;
                    }
                }

                string rootFolder;
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "请选择模型输出根目录";
                    if (dialog.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    rootFolder = dialog.SelectedPath;
                }

                var result = Exporters.ExportModels(doc, uiapp, rootFolder, selectedLinkIds);

                string summary = $"模型导出完成。\n成功: {result.Success} | 跳过: {result.Skip} | 失败: {result.Fail}\n\n📁 {rootFolder}";
                MEPLogger.Instance.EndSession($"Done: Success={result.Success}");
                TaskDialog.Show("模型导出", summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportModels", ex);
                return Result.Failed;
            }
        }
    }
}