using System;
using System.IO;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace MEPTools.BatchLink
{
    [Transaction(TransactionMode.Manual)]
    public class SimpleBatchLinkCommand : IExternalCommand
    {
        private static string MemFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "MEPTools", "LastDir.txt");

        public Result Execute(ExternalCommandData cd, ref string message, ElementSet elements)
        {
            Document doc = cd.Application.ActiveUIDocument.Document;

            // 1. 读取上次打开的目录
            string lastDir = "";
            if (File.Exists(MemFile)) lastDir = File.ReadAllText(MemFile);
            if (!Directory.Exists(lastDir)) lastDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            // 2. 弹出原生的多选文件框（重点是 Multiselect = true）
            var dlg = new OpenFileDialog
            {
                Title = "请选择要批量链接的模型 (可多选)",
                Filter = "Revit 模型 (*.rvt)|*.rvt",
                Multiselect = true, 
                InitialDirectory = lastDir
            };

            if (dlg.ShowDialog() != DialogResult.OK) return Result.Cancelled;

            // 保存记忆路径
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(MemFile));
                File.WriteAllText(MemFile, Path.GetDirectoryName(dlg.FileNames[0]));
            }
            catch { }

            // 3. 执行批量链接
            int success = 0;
            var errors = new List<string>();

            using (Transaction tx = new Transaction(doc, "极简批量链接"))
            {
                tx.Start();
                foreach (string path in dlg.FileNames)
                {
                    try
                    {
                        // 转换本地路径
                        ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(path);
                        
                        // 默认使用“原点到原点”自动定位
                        var loadResult = RevitLinkType.Create(doc, mp, new RevitLinkOptions(false));
                        
                        if (loadResult != null && loadResult.ElementId != ElementId.InvalidElementId)
                        {
                            RevitLinkInstance.Create(doc, loadResult.ElementId, ImportPlacement.Origin);
                            success++;
                        }
                        else
                        {
                            errors.Add($"失败: {Path.GetFileName(path)} (可能已加载或文件损坏)");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"异常: {Path.GetFileName(path)} - {ex.Message}");
                    }
                }
                tx.Commit();
            }

            // 4. 提示结果
            string msg = $"成功链接了 {success} 个模型。";
            if (errors.Count > 0)
            {
                msg += $"\n\n失败 {errors.Count} 个：\n" + string.Join("\n", errors);
            }
            TaskDialog.Show("批量链接完成", msg);

            return Result.Succeeded;
        }
    }
}