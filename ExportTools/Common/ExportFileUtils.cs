using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace MEPTools.ExportTools.Common
{
    /// <summary>导出模块共用的文件/路径/命名工具（纯函数，不做任何文档写操作）。</summary>
    public static class ExportFileUtils
    {
        /// <summary>本档案备份文件名的固定前缀。</summary>
        public const string CurrentDocNamePrefix = "【当前文件】-";

        public static string GetCloudModelLocalCacheFilepath(Document doc, string versionNumber)
        {
            try
            {
                if (!doc.IsModelInCloud) return null;
                ModelPath cloudPath = doc.GetCloudModelPath();
                if (cloudPath == null) return null;

                string projectGuid = cloudPath.GetProjectGUID().ToString();
                string modelGuid = cloudPath.GetModelGUID().ToString();

                string collaborationCache = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Autodesk", "Revit", $"Autodesk Revit {versionNumber}", "CollaborationCache");

                if (!Directory.Exists(collaborationCache)) return null;

                // 1) 现行布局：CollaborationCache\<hub>\<projectGuid>\<modelGuid>.rvt
                foreach (string hubFolder in Directory.GetDirectories(collaborationCache))
                {
                    string exactPath = Path.Combine(hubFolder, projectGuid, modelGuid + ".rvt");
                    if (File.Exists(exactPath)) return exactPath;
                }

                // 2) 旧布局：CollaborationCache\<projectGuid>\<modelGuid>.rvt
                string legacyPath = Path.Combine(collaborationCache, projectGuid, modelGuid + ".rvt");
                if (File.Exists(legacyPath)) return legacyPath;

                // 3) 兜底：在 <projectGuid> 目录下按 GUID 前缀匹配（不进入 LinkedModels / CentralCache）
                foreach (string hubFolder in Directory.GetDirectories(collaborationCache))
                {
                    string projectFolder = Path.Combine(hubFolder, projectGuid);
                    if (!Directory.Exists(projectFolder)) continue;
                    string[] files = Directory.GetFiles(projectFolder, modelGuid + "*.rvt", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0) return files.OrderBy(f => f.Length).First();
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 纯复制文件：以共享读打开源文件，避免源文件正被 Revit 打开时触发"文件被占用"而跳过。
        /// 只读源文件，不修改源文件任何内容。
        /// </summary>
        public static void CopyFileShared(string sourcePath, string destinationPath)
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }
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
