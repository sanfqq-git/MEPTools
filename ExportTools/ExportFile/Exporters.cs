using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportFile
{
    /// <summary>
    /// 文件类导出（模型 / CAD 底图 / 外部图片）。
    /// 公共的复制、路径、命名工具在 MEPTools.ExportTools.Common.ExportFileUtils 中。
    /// </summary>
    public static class Exporters
    {
        // ================= 1. 导出模型 (LINK + 本档案) =================
        public static ExportResult ExportModels(Document doc, UIApplication uiapp, string rootFolder, List<ElementId> selectedLinkIds,
            bool includeMainModel = true, string mainFilePrefix = "")
        {
            var result = new ExportResult();
            string linkedFolder = Path.Combine(rootFolder, "01_LinkedModels_链接模型");
            string mainFolder = Path.Combine(rootFolder, "02_MainModel_本档案");
            Directory.CreateDirectory(linkedFolder);
            if (includeMainModel) Directory.CreateDirectory(mainFolder);

            // 1.1 导出选择的 LINK
            if (selectedLinkIds != null && selectedLinkIds.Count > 0)
            {
                foreach (ElementId typeId in selectedLinkIds)
                {
                    RevitLinkType linkType = doc.GetElement(typeId) as RevitLinkType;
                    if (linkType == null) continue;

                    RevitLinkInstance linkInstance = new FilteredElementCollector(doc)
                        .OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>()
                        .FirstOrDefault(li => li.GetTypeId() == typeId);

                    Document linkDoc = linkInstance?.GetLinkDocument();
                    string baseName = ExportFileUtils.GetDocumentBaseName(linkType.Name);
                    string destination = ExportFileUtils.GetUniquePath(linkedFolder, ExportFileUtils.SafeFileName(baseName) + ".rvt");

                    try
                    {
                        string sourcePath = null;
                        string sourceType = "";

                        if (linkDoc != null)
                        {
                            if (linkDoc.IsModelInCloud)
                            {
                                sourcePath = ExportFileUtils.GetCloudModelLocalCacheFilepath(linkDoc, uiapp.Application.VersionNumber);
                                sourceType = "云缓存";
                            }
                            else
                            {
                                sourcePath = linkDoc.PathName;
                                sourceType = "内存文档路径";
                            }
                        }

                        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                        {
                            ExternalFileReference extRef = linkType.GetExternalFileReference();
                            if (extRef != null && extRef.GetAbsolutePath() != null)
                            {
                                sourcePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
                                sourceType = "外部引用指针";
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
                        {
                            if (!Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                            {
                                ExportFileUtils.CopyFileShared(sourcePath, destination);
                                MEPLogger.Instance.AppendLine($"[链接-复制] {linkType.Name} ({sourceType})\n源: {sourcePath}\n目标: {destination}");
                                result.Success++;
                            }
                            else result.Skip++;
                        }
                        else result.Skip++;
                    }
                    catch (Exception ex)
                    {
                        result.Fail++;
                        MEPLogger.Instance.LogException("LINK导出失败: " + linkType.Name, ex);
                    }
                }
            }

            // 1.2 导出本档案（勾选后才做纯文件复制，文件名加【当前文件】- 前缀）
            if (includeMainModel)
            {
                string mainBaseName = ExportFileUtils.GetDocumentBaseName(string.IsNullOrWhiteSpace(doc.Title) ? "MainModel" : doc.Title);
                string mainDestination = ExportFileUtils.GetUniquePath(mainFolder, ExportFileUtils.SafeFileName(mainFilePrefix + mainBaseName) + ".rvt");
                try
                {
                    string sourceMainPath = doc.IsModelInCloud
                        ? ExportFileUtils.GetCloudModelLocalCacheFilepath(doc, uiapp.Application.VersionNumber)
                        : doc.PathName;

                    if (!string.IsNullOrWhiteSpace(sourceMainPath) && File.Exists(sourceMainPath))
                    {
                        ExportFileUtils.CopyFileShared(sourceMainPath, mainDestination);
                        MEPLogger.Instance.AppendLine($"[主模型-复制]\n源: {sourceMainPath}\n目标: {mainDestination}");
                        result.Success++;
                    }
                    else result.Skip++;
                }
                catch (Exception ex)
                {
                    result.Fail++;
                    MEPLogger.Instance.LogException("主模型导出失败", ex);
                }
            }

            return result;
        }

        // ================= 2. 导出 CAD 底图 =================
        public static ExportResult ExportCad(Document doc, string rootFolder)
        {
            var result = new ExportResult();
            string cadFolder = Path.Combine(rootFolder, "03_CAD底图");
            Directory.CreateDirectory(cadFolder);

            var importInstances = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance));
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ImportInstance cadInst in importInstances)
            {
                try
                {
                    Element cadType = doc.GetElement(cadInst.GetTypeId());
                    ExternalFileReference extRef = cadType?.GetExternalFileReference();
                    if (extRef != null && extRef.GetAbsolutePath() != null)
                    {
                        string sourcePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
                        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath) && processedPaths.Add(sourcePath))
                        {
                            string fileName = Path.GetFileName(sourcePath);
                            string destination = ExportFileUtils.GetUniquePath(cadFolder, fileName);

                            File.Copy(sourcePath, destination, true);
                            MEPLogger.Instance.AppendLine($"[CAD-复制] 源: {sourcePath} -> 目标: {destination}");
                            result.Success++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Fail++;
                    MEPLogger.Instance.LogException("CAD导出失败", ex);
                }
            }
            return result;
        }

        // ================= 3. 导出 外部图片 =================
        public static ExportResult ExportImages(Document doc, string rootFolder)
        {
            var result = new ExportResult();
            string imgFolder = Path.Combine(rootFolder, "04_外部图片");
            Directory.CreateDirectory(imgFolder);

            var imageInstances = new FilteredElementCollector(doc).OfClass(typeof(ImageInstance));
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ImageInstance imgInst in imageInstances)
            {
                try
                {
                    ImageType imgType = doc.GetElement(imgInst.GetTypeId()) as ImageType;
                    ExternalFileReference extRef = imgType?.GetExternalFileReference();
                    if (extRef != null && extRef.GetAbsolutePath() != null)
                    {
                        string sourcePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(extRef.GetAbsolutePath());
                        if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath) && processedPaths.Add(sourcePath))
                        {
                            string fileName = Path.GetFileName(sourcePath);
                            string destination = ExportFileUtils.GetUniquePath(imgFolder, fileName);

                            File.Copy(sourcePath, destination, true);
                            MEPLogger.Instance.AppendLine($"[图片-复制] 源: {sourcePath} -> 目标: {destination}");
                            result.Success++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Fail++;
                    MEPLogger.Instance.LogException("图片导出失败", ex);
                }
            }
            return result;
        }
    }
}
