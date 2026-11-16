using System;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.Shared;

namespace MEPTools.ExportTools
{
    [Transaction(TransactionMode.Manual)]
    public class ExportImageCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("单独导出外部图片");

            try
            {
                string rootFolder;
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "请选择外部图片输出目录";
                    if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) 
                        return Result.Cancelled;
                    rootFolder = dialog.SelectedPath;
                }

                var result = Exporters.ExportImages(doc, rootFolder);

                string summary = $"外部图片导出完成。\n成功: {result.Success} | 失败: {result.Fail}\n\n📁 {rootFolder}";
                MEPLogger.Instance.EndSession($"Done: Success={result.Success}");
                TaskDialog.Show("导出外部图片", summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportImages", ex);
                InfoCenterTip.Show("图片导出失败，请查看日志。");
                return Result.Failed;
            }
        }
    }
}