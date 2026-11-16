using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI; // 修复：添加 UIApplication 所在的命名空间
using MEPTools.Shared;

namespace MEPTools.ExportTools
{
    public class ExportResult
    {
        public int Success { get; set; }
        public int Fail { get; set; }
        public int Skip { get; set; }
    }

    public static class Exporters
    {
        // ================= 1. 导出模型 (LINK + 本档案) =================
        public static ExportResult ExportModels(Document doc, UIApplication uiapp, string rootFolder, List<ElementId> selectedLinkIds)
        {
            var result = new ExportResult();
            string linkedFolder = Path.Combine(rootFolder, "01_LinkedModels_链接模型");
            string mainFolder = Path.Combine(rootFolder, "02_MainModel_本档案");
            Directory.CreateDirectory(linkedFolder);
            Directory.CreateDirectory(mainFolder);

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
                    string baseName = GetDocumentBaseName(linkType.Name);
                    string destination = GetUniquePath(linkedFolder, SafeFileName(baseName) + ".rvt");

                    try
                    {
                        string sourcePath = null;
                        string sourceType = "";

                        if (linkDoc != null)
                        {
                            if (linkDoc.IsModelInCloud)
                            {
                                sourcePath = GetCloudModelLocalCacheFilepath(linkDoc, uiapp.Application.VersionNumber);
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
                                File.Copy(sourcePath, destination, true);
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

            // 1.2 导出本档案
            string mainBaseName = GetDocumentBaseName(string.IsNullOrWhiteSpace(doc.Title) ? "MainModel" : doc.Title);
            string mainDestination = GetUniquePath(mainFolder, SafeFileName(mainBaseName) + ".rvt");
            try
            {
                string sourceMainPath = doc.IsModelInCloud
                    ? GetCloudModelLocalCacheFilepath(doc, uiapp.Application.VersionNumber)
                    : doc.PathName;

                if (!string.IsNullOrWhiteSpace(sourceMainPath) && File.Exists(sourceMainPath))
                {
                    File.Copy(sourceMainPath, mainDestination, true);
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
                            string destination = GetUniquePath(cadFolder, fileName);
                            
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
                            string destination = GetUniquePath(imgFolder, fileName);
                            
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

        // ================= 辅助方法 =================
        public static string GetCloudModelLocalCacheFilepath(Document doc, string versionNumber)
        {
            try
            {
                if (!doc.IsModelInCloud) return null;
                ModelPath cloudPath = doc.GetCloudModelPath();
                if (cloudPath == null) return null;

                string projectGuid = cloudPath.GetProjectGUID().ToString();
                string modelGuid = cloudPath.GetModelGUID().ToString();

                string baseRevitFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Autodesk", "Revit", $"Autodesk Revit {versionNumber}", "CollaborationCache");

                string exactPath = Path.Combine(baseRevitFolder, projectGuid, modelGuid + ".rvt");
                if (File.Exists(exactPath)) return exactPath;

                string projectFolder = Path.Combine(baseRevitFolder, projectGuid);
                if (Directory.Exists(projectFolder))
                {
                    string[] files = Directory.GetFiles(projectFolder, modelGuid + "*.rvt", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0) return files[0];
                }

                if (Directory.Exists(baseRevitFolder))
                {
                    var allFiles = Directory.GetFiles(baseRevitFolder, "*" + modelGuid + "*.rvt", SearchOption.AllDirectories);
                    if (allFiles.Length > 0)
                        return allFiles.Where(f => !f.Contains("CentralCache")).OrderBy(f => f.Length).FirstOrDefault() ?? allFiles[0];
                }
            }
            catch { }
            return null;
        }

        public static string GetDocumentBaseName(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "Model";
            string trimmed = title.Trim();
            if (trimmed.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed.Substring(0, trimmed.Length - 4);
            return trimmed;
        }

        public static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Model";
            char[] chars = name.Trim().ToCharArray();
            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                if (invalidChars.Contains(chars[i])) chars[i] = '_';
            }
            string result = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(result) ? "Model" : result;
        }

        public static string GetUniquePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return path;
            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int index = 1;
            do
            {
                path = Path.Combine(folder, $"{name}_{index}{extension}");
                index++;
            } while (File.Exists(path));
            return path;
        }
    }
}