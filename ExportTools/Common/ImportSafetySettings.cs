
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MEPTools.ExportTools.Common
{
    /// <summary>
    /// 导入安全策略：定义「哪些列允许写回模型」以及「哪些列属于危险列」。
    ///
    ///  - 白名单里的列 → 正常写回（当前只有名称类）
    ///  - 危险列 → 默认禁止，必须用户在导入弹窗里显式勾选才生效（当前是图纸编号）
    ///  - 以后要放开「审图状态 / 专业 / 设计人」这类图纸上的项目参数，
    ///    把参数名加进对应白名单即可（写回方式与名称一致，只改参数值，不动结构）
    /// </summary>
    public static class ImportSafetySettings
    {
        /// <summary>图纸导入：允许写回的列（只写这些，其它列一律忽略）。</summary>
        public static readonly List<string> SheetWritableColumns = new List<string>
        {
            "图纸名称"
        };

        /// <summary>视图导入：允许写回的列。</summary>
        public static readonly List<string> ViewWritableColumns = new List<string>
        {
            "视图名称"
        };

        /// <summary>危险列：默认不允许，需要用户每次导入时显式勾选。</summary>
        public static readonly List<string> SheetDangerousColumns = new List<string>
        {
            "图纸编号"
        };

        public static bool IsSheetWritable(string columnName)
        {
            foreach (string name in SheetWritableColumns)
                if (string.Equals(name, columnName, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool IsSheetDangerous(string columnName)
        {
            foreach (string name in SheetDangerousColumns)
                if (string.Equals(name, columnName, StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// 导入变更日志：只在本批真的有改动时才落盘，
    /// 文件名形如「图纸信息导入_变更日志_20261119_1712.txt」，UTF-8(BOM) 便于记事本直接看。
    /// </summary>
    public class ImportChangeLog
    {
        private readonly List<string> _lines = new List<string>();
        private readonly string _path;

        public ImportChangeLog(string folder, string filePrefix)
        {
            string fileName = filePrefix + "_变更日志_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".txt";
            _path = ExportFileUtils.GetUniquePath(folder, fileName);
        }

        public string Path
        {
            get { return _path; }
        }

        public void Log(string line)
        {
            _lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                       " | " + Environment.UserName + " | " + line);
        }

        /// <summary>有内容才写盘；返回是否写成功。</summary>
        public bool SaveIfAny()
        {
            if (_lines.Count == 0) return false;
            try
            {
                File.WriteAllLines(_path, _lines, new UTF8Encoding(true));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
