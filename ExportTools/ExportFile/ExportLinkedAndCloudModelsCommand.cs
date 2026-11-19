using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportFile
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

                // 勾选窗体：链接默认不勾选、本档案备份默认勾选；输出位置也在这里确定，不再二次弹目录框
                List<ElementId> selectedLinkIds;
                bool includeMainModel;
                string rootFolder;

                using (var form = new LinkSelectionForm(allLinkTypes, doc.Title, LinkSelectionForm.GetDefaultOutputFolder()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    selectedLinkIds = form.SelectedTypeIds;
                    includeMainModel = form.IncludeMainModel;
                    rootFolder = form.OutputFolder;
                }

                if (string.IsNullOrWhiteSpace(rootFolder)) return Result.Cancelled;

                // 本档案备份文件名加前缀【当前文件】-
                var result = Exporters.ExportModels(doc, uiapp, rootFolder, selectedLinkIds, includeMainModel, ExportFileUtils.CurrentDocNamePrefix);

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
