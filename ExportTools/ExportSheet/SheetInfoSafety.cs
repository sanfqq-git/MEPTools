using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using MEPTools.ExportTools.Common;
using MEPTools.ExportTools.Common.Excel;

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 图纸信息导入 / 撤销的安全底座（只读写文本文件，绝不碰模型）：
    ///  1) 文档指纹：区分「这个模型」和「别的模型」；
    ///  2) 回滚快照：导入前把「旧值→新值」落盘，之后任何时刻都能按主键回退；
    ///  3) 老日志兼容：能读 11-19 版导入写的「图纸信息导入_变更日志_*.txt」。
    /// </summary>
    public static class SheetInfoSafety
    {
        public const string SnapshotColumnKey = "主键";
        public const string SnapshotColumnOldNumber = "旧编号";
        public const string SnapshotColumnOldName = "旧名称";
        public const string SnapshotColumnNewNumber = "新编号";
        public const string SnapshotColumnNewName = "新名称";
        public const string SnapshotColumnSource = "来源";
        public const string SnapshotColumnTime = "时间";

        public const string SnapshotFilePrefix = "图纸信息回滚快照_";
        public const string LegacyLogFilePrefix = "图纸信息导入_变更日志_";

        private const string LastImportFolderName = "MEPTools";
        private const string LastImportFileName = "SheetInfoLastImport.txt";

        /// <summary>
        /// 当前文档指纹。用 ProjectInformation 元素唯一 ID（跟着文件走、另存为后仍一致），
        /// 叠加项目编号方便人眼核对。取不到就返回空串，导入端会降级为「不做文档比对」。
        /// </summary>
        public static string GetDocumentFingerprint(Document doc)
        {
            try
            {
                if (doc == null || doc.ProjectInformation == null) return string.Empty;
                string uid = doc.ProjectInformation.UniqueId ?? string.Empty;
                string number = (doc.ProjectInformation.Number ?? string.Empty).Trim();
                if (uid.Length == 0 && number.Length == 0) return string.Empty;
                return uid + " / " + number;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>快照/日志里的一行：主键 + 导入前值 + 导入后值。</summary>
        public class SnapshotRow
        {
            public string Key { get; set; }
            public string OldNumber { get; set; }
            public string OldName { get; set; }
            public string NewNumber { get; set; }
            public string NewName { get; set; }
        }

        /// <summary>
        /// 写回滚快照（CSV，UTF-8 带 BOM，Excel 双击可看）。返回落地路径；失败返回 null。
        /// 调用方必须「写成功才允许改模型」。
        /// </summary>
        public static string WriteSnapshot(string folder, IList<SnapshotRow> rows, string source)
        {
            if (rows == null || rows.Count == 0) return null;

            try
            {
                if (string.IsNullOrWhiteSpace(folder)) folder = Path.GetTempPath();
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

                SimpleTable table = new SimpleTable(
                    SnapshotColumnKey, SnapshotColumnOldNumber, SnapshotColumnOldName,
                    SnapshotColumnNewNumber, SnapshotColumnNewName,
                    SnapshotColumnSource, SnapshotColumnTime);

                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                foreach (SnapshotRow row in rows)
                {
                    table.AddRow(
                        row.Key ?? string.Empty,
                        row.OldNumber ?? string.Empty,
                        row.OldName ?? string.Empty,
                        row.NewNumber ?? string.Empty,
                        row.NewName ?? string.Empty,
                        source ?? string.Empty,
                        time);
                }

                string fileName = SnapshotFilePrefix + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                CsvTableStore.Save(path, table);
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读 CSV 回滚快照。缺少必要列时抛 InvalidDataException。</summary>
        public static List<SnapshotRow> ReadSnapshot(string path)
        {
            SimpleTable table = CsvTableStore.Load(path);

            int keyIndex = table.ColumnIndex(SnapshotColumnKey);
            int oldNumberIndex = table.ColumnIndex(SnapshotColumnOldNumber);
            int oldNameIndex = table.ColumnIndex(SnapshotColumnOldName);
            int newNumberIndex = table.ColumnIndex(SnapshotColumnNewNumber);
            int newNameIndex = table.ColumnIndex(SnapshotColumnNewName);

            if (keyIndex < 0 || oldNameIndex < 0 || newNameIndex < 0)
                throw new InvalidDataException("不是有效的回滚快照：缺少「" + SnapshotColumnKey + "」「" +
                                               SnapshotColumnOldName + "」「" + SnapshotColumnNewName + "」列。\n" + path);

            List<SnapshotRow> rows = new List<SnapshotRow>();
            for (int i = 0; i < table.Rows.Count; i++)
            {
                string key = table.Cell(i, keyIndex).Trim();
                if (key.Length == 0) continue;

                rows.Add(new SnapshotRow
                {
                    Key = key,
                    OldNumber = oldNumberIndex >= 0 ? table.Cell(i, oldNumberIndex) : string.Empty,
                    OldName = table.Cell(i, oldNameIndex),
                    NewNumber = newNumberIndex >= 0 ? table.Cell(i, newNumberIndex) : string.Empty,
                    NewName = table.Cell(i, newNameIndex)
                });
            }
            return rows;
        }

        /// <summary>
        /// 兼容 11-19 版导入写下的纯文本变更日志，格式形如：
        ///   2026-11-24 09:10:06 | User | [名称] 主键=4059259 编号=XX-0001 旧=Unnamed 新=1 OK
        ///   2026-11-24 09:10:06 | User | [编号] 主键=4059259 旧=XX-0001 新=CSD-0001 OK
        /// 只取成功（OK）行，失败行跳过。同一主键的名称行与编号行合并成一行。
        /// </summary>
        public static List<SnapshotRow> ReadChangeLog(string path)
        {
            List<SnapshotRow> rows = new List<SnapshotRow>();
            Dictionary<string, SnapshotRow> map = new Dictionary<string, SnapshotRow>(StringComparer.Ordinal);
            Dictionary<string, string> numberHint = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> order = new List<string>();

            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                int pipe = raw.LastIndexOf('|');
                if (pipe < 0) continue;
                string payload = raw.Substring(pipe + 1).Trim();
                if (!payload.EndsWith("OK", StringComparison.Ordinal)) continue;   // 只认成功行

                bool isName = payload.StartsWith("[名称]", StringComparison.Ordinal);
                bool isNumber = payload.StartsWith("[编号]", StringComparison.Ordinal);
                if (!isName && !isNumber) continue;

                string key = ExtractKey(payload);
                if (string.IsNullOrEmpty(key)) continue;

                int oldIndex = payload.IndexOf(" 旧=", StringComparison.Ordinal);
                int newIndex = payload.IndexOf(" 新=", StringComparison.Ordinal);
                if (oldIndex < 0 || newIndex < 0 || newIndex <= oldIndex) continue;

                string oldValue = payload.Substring(oldIndex + 3, newIndex - oldIndex - 3).Trim();
                string newValue = payload.Substring(newIndex + 3).Trim();
                if (newValue.EndsWith(" OK", StringComparison.Ordinal))
                    newValue = newValue.Substring(0, newValue.Length - 3).Trim();
                else if (newValue.EndsWith("OK", StringComparison.Ordinal))
                    newValue = newValue.Substring(0, newValue.Length - 2).Trim();

                SnapshotRow row;
                if (!map.TryGetValue(key, out row))
                {
                    row = new SnapshotRow { Key = key };
                    map[key] = row;
                    order.Add(key);
                }

                if (isName)
                {
                    row.OldName = oldValue;
                    row.NewName = newValue;

                    int numberIndex = payload.IndexOf(" 编号=", StringComparison.Ordinal);
                    if (numberIndex >= 0 && oldIndex > numberIndex)
                        numberHint[key] = payload.Substring(numberIndex + 4, oldIndex - numberIndex - 4).Trim();
                }
                else
                {
                    row.OldNumber = oldValue;
                    row.NewNumber = newValue;
                }
            }

            foreach (string key in order)
            {
                SnapshotRow row = map[key];
                if ((row.OldNumber == null || row.NewNumber == null) && numberHint.ContainsKey(key))
                {
                    // 只改了名称的行：编号在导入时没动，旧值=新值
                    row.OldNumber = numberHint[key];
                    row.NewNumber = numberHint[key];
                }
                if ((row.OldName == null || row.OldName.Length == 0) &&
                    (row.OldNumber == null || row.OldNumber.Length == 0))
                    continue;   // 没有可回退的信息

                rows.Add(row);
            }
            return rows;
        }

        private static string ExtractKey(string payload)
        {
            int index = payload.IndexOf("主键=", StringComparison.Ordinal);
            if (index < 0) return null;
            int start = index + 3;
            int end = start;
            while (end < payload.Length && (payload[end] >= '0' && payload[end] <= '9')) end++;
            if (end == start) return null;
            return payload.Substring(start, end - start);
        }

        /// <summary>只取前 max 条，附「其余 N 条」提示。</summary>
        public static string FormatLines(IList<string> lines, int max)
        {
            if (lines == null || lines.Count == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            int count = Math.Min(max, lines.Count);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.AppendLine();
                sb.Append(lines[i]);
            }
            if (lines.Count > count) sb.AppendLine().Append("… 其余 " + (lines.Count - count) + " 条见日志");
            return sb.ToString();
        }

        /// <summary>Excel 打开工作簿时会在同目录生成「~$文件名」属主文件，用它做轻量判断（只提示，不阻断）。</summary>
        public static bool IsLikelyOpenInExcel(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath)) return false;
                string folder = Path.GetDirectoryName(filePath);
                if (string.IsNullOrWhiteSpace(folder)) return false;
                return File.Exists(Path.Combine(folder, "~$" + Path.GetFileName(filePath)));
            }
            catch
            {
                return false;
            }
        }

        /// <summary>长串（指纹）在弹窗里做截断，日志里保留全文。</summary>
        public static string Shorten(string text, int keep)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= keep) return text ?? string.Empty;
            return text.Substring(0, keep) + "…";
        }

        // ================= 记住「上次导入的表格」，方便撤销时定位 =================

        public static void RememberLastImport(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath)) return;
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LastImportFolderName);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, LastImportFileName), filePath, new UTF8Encoding(false));
            }
            catch
            {
                // 记不住不影响导入
            }
        }

        public static string GetLastImportFolder()
        {
            try
            {
                string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LastImportFolderName, LastImportFileName);
                if (!File.Exists(state)) return null;

                string path = File.ReadAllText(state, Encoding.UTF8).Trim();
                if (path.Length == 0) return null;

                string folder = Path.GetDirectoryName(path);
                return (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) ? folder : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在 root 下（限深 maxDepth）找最新的快照/老日志，用于撤销时预定位。
        /// 目录数超过 2000 就停（防止扫到大工程目录卡死）。
        /// </summary>
        public static string FindNewestArtifact(string root, int maxDepth)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;

                List<string> found = new List<string>();
                int visited = 0;
                CollectArtifacts(root, 0, maxDepth, found, ref visited);
                if (found.Count == 0) return null;

                return found.OrderByDescending(SafeLastWrite).First();
            }
            catch
            {
                return null;
            }
        }

        private static void CollectArtifacts(string dir, int depth, int maxDepth, List<string> found, ref int visited)
        {
            if (visited > 2000) return;
            visited++;

            try
            {
                foreach (string file in Directory.GetFiles(dir, SnapshotFilePrefix + "*.csv", SearchOption.TopDirectoryOnly))
                    found.Add(file);
                foreach (string file in Directory.GetFiles(dir, LegacyLogFilePrefix + "*.txt", SearchOption.TopDirectoryOnly))
                    found.Add(file);
            }
            catch
            {
                // 单个目录读不了就跳过
            }

            if (depth >= maxDepth) return;

            try
            {
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)) continue;
                    CollectArtifacts(sub, depth + 1, maxDepth, found, ref visited);
                }
            }
            catch
            {
                // 跳过
            }
        }

        private static DateTime SafeLastWrite(string path)
        {
            try
            {
                return File.GetLastWriteTime(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
