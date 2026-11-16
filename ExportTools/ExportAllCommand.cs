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
    public class ExportAllCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("一键打包项目资源");

            try
            {
                // 1. 勾选 LINK 模型
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

                // 2. 选择总目录
                string rootFolder;
                using (var dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "请选择一键打包输出目录";
                    if (dialog.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    rootFolder = dialog.SelectedPath;
                }

                // 3. 依次执行打包
                var modelResult = Exporters.ExportModels(doc, uiapp, rootFolder, selectedLinkIds);
                var cadResult = Exporters.ExportCad(doc, rootFolder);
                var imgResult = Exporters.ExportImages(doc, rootFolder);

                // 4. 汇总
                string summary = $"🎉 一键打包完成！\n\n" +
                                 $"📦 模型资源：成功 {modelResult.Success} | 跳过 {modelResult.Skip} | 失败 {modelResult.Fail}\n" +
                                 $"📐 CAD底图：成功 {cadResult.Success} | 失败 {cadResult.Fail}\n" +
                                 $"🖼️ 外部图片：成功 {imgResult.Success} | 失败 {imgResult.Fail}\n\n" +
                                 $"📁 打包目录：\n{rootFolder}\n\n" +
                                 $"💡 提示：提取的云端文件在单独打开时，请勾选“从中心分离”。";

                MEPLogger.Instance.EndSession($"All Done: Model={modelResult.Success}, Cad={cadResult.Success}, Img={imgResult.Success}");
                TaskDialog.Show("一键打包完成", summary);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportAll", ex);
                InfoCenterTip.Show("打包失败，请查看日志。");
                return Result.Failed;
            }
        }
    }
}