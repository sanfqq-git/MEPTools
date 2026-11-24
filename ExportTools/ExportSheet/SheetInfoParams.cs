using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using MEPTools.ExportTools.Common.Excel;

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 图纸参数列的“扩宽”基础层（只做参数搬运，不改任何既有规则）：
    ///
    ///  导出：把图纸上所有「可写文本参数」自动变成可见列，并给每个参数补一列隐藏基线「原始_参数名」。
    ///        列名不是写死的，跟着模型上真实存在的参数走 —— 项目参数 / 共享参数（Tag_Description、CAT CODE、
    ///        项目代号…）自动进来，换项目不用改代码。
    ///  导入：按列名配对「表内值 / 导出时原值 / 模型现值」，只允许覆盖“模型没被别人改过”的参数。
    ///
    /// 固定三列（主键 / 图纸编号 / 图纸名称）的既有规则完全不动，参数列是叠加在它们右边的。
    /// </summary>
    public static class SheetInfoParams
    {
        /// <summary>参数基线的隐藏列前缀：原始_参数名。</summary>
        public const string OrigPrefix = "原始_";

        /// <summary>参数快照编码：名称 旧 新（\u001F 分隔），参数之间用 \u001E 分隔。</summary>
        public const char FieldSep = '\u001F';
        public const char ParamSep = '\u001E';

        /// <summary>一张图纸的导入/导出快照：主键 + 编号 + 名称 + 可写文本参数值。</summary>
        public class SheetSnapshot
        {
            public string Key { get; set; }
            public string Number { get; set; }
            public string Name { get; set; }

            public Dictionary<string, string> Values { get; private set; }

            public SheetSnapshot()
            {
                Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>一处参数改动：名称 + 导入前值 + 导入后值。</summary>
        public class ParamChange
        {
            public string Name { get; set; }
            public string OldValue { get; set; }
            public string NewValue { get; set; }
        }

        private static readonly string[] ReservedNames =
        {
            SheetInfoExportCommand.ColumnKey,
            SheetInfoExportCommand.ColumnNumber,
            SheetInfoExportCommand.ColumnName,
            SheetInfoExportCommand.ColumnOriginalNumber,
            SheetInfoExportCommand.ColumnOriginalName,
            SheetInfoExportCommand.ColumnBatch,
            SheetInfoExportCommand.ColumnDocFp
        };

        /// <summary>是不是固定列（主键 / 编号 / 名称 / 基线 / 来源）—— 这些不算“参数列”。</summary>
        public static bool IsReserved(string columnName)
        {
            if (string.IsNullOrWhiteSpace(columnName)) return true;
            string name = columnName.Trim();
            foreach (string reserved in ReservedNames)
            {
                if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>把单元格文本归一：去掉首尾空白、换行压成空格（CSV/xlsx 单元格都容不下换行）。</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            string text = value.Replace("\r\n", " ").Replace("\r", " ").Replace("\n", " ");
            return text.Trim();
        }

        /// <summary>
        /// 单张图纸的「可写文本参数」：StorageType=String 且非只读，排除固定列名与“原始_”前缀。
        /// 同名参数只取第一个；取不到定义名的跳过。
        /// </summary>
        public static Dictionary<string, string> GetWritableValues(ViewSheet sheet)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sheet == null) return values;

            try
            {
                foreach (Parameter parameter in sheet.Parameters)
                {
                    try
                    {
                        if (parameter == null || parameter.Definition == null) continue;

                        string name = (parameter.Definition.Name ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        if (IsReserved(name)) continue;
                        if (name.StartsWith(OrigPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                        if (parameter.StorageType != StorageType.String) continue;  // 只收文本参数（日期/编号这类在 Revit 里也是 String）
                        if (parameter.IsReadOnly) continue;                          // 只读的（Edited by、修订签发对象…）导出也不给改
                        if (values.ContainsKey(name)) continue;                      // 同名参数只取第一个

                        values[name] = Normalize(parameter.AsString());
                    }
                    catch
                    {
                        // 单个参数取值异常不影响整表
                    }
                }
            }
            catch
            {
                // 极端情况下 Parameters 枚举失败，按“没有参数”处理
            }
            return values;
        }

        /// <summary>多张图纸的参数列并集，按名称排序（保证同一模型每次导出的列顺序稳定）。</summary>
        public static List<string> ResolveColumnNames(IEnumerable<Dictionary<string, string>> perSheetValues)
        {
            HashSet<string> all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (perSheetValues != null)
            {
                foreach (Dictionary<string, string> values in perSheetValues)
                {
                    if (values == null) continue;
                    foreach (KeyValuePair<string, string> pair in values) all.Add(pair.Key);
                }
            }

            List<string> names = new List<string>(all);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>快照一张图纸（含全部可写文本参数）。</summary>
        public static SheetSnapshot Capture(ViewSheet sheet)
        {
            if (sheet == null) return null;

            SheetSnapshot snapshot = new SheetSnapshot
            {
                Key = sheet.Id.IntegerValue.ToString(),
                Number = sheet.SheetNumber,
                Name = sheet.Name
            };

            foreach (KeyValuePair<string, string> pair in GetWritableValues(sheet))
                snapshot.Values[pair.Key] = pair.Value;

            return snapshot;
        }

        /// <summary>取参数当前值；图纸上没有这个参数返回 null。</summary>
        public static string GetValue(ViewSheet sheet, string name)
        {
            try
            {
                Parameter parameter = sheet.LookupParameter(name);
                if (parameter == null) return null;
                if (parameter.StorageType != StorageType.String) return null;
                return Normalize(parameter.AsString());
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写参数；成功返回 null，失败返回原因。</summary>
        public static string SetValue(ViewSheet sheet, string name, string value)
        {
            try
            {
                Parameter parameter = sheet.LookupParameter(name);
                if (parameter == null) return "图纸上没有参数「" + name + "」";
                if (parameter.IsReadOnly) return "参数「" + name + "」是只读的";
                if (parameter.StorageType != StorageType.String) return "参数「" + name + "」不是文本类型";

                parameter.Set(value ?? string.Empty);
                return null;
            }
            catch (Exception ex)
            {
                return "参数「" + name + "」写入失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 组表：可见列 = 主键 / 图纸编号 / 图纸名称 / 各参数列；
        /// 隐藏列 = 原始编号 / 原始名称 / 原始_各参数 / 来源批次 / 来源文档。
        /// </summary>
        public static SimpleTable BuildTable(IList<string> columnNames, IEnumerable<SheetSnapshot> rows,
            string batch, string docFingerprint)
        {
            List<string> names = columnNames != null ? new List<string>(columnNames) : new List<string>();

            SimpleTable table = new SimpleTable(SheetInfoExportCommand.ColumnKey,
                SheetInfoExportCommand.ColumnNumber, SheetInfoExportCommand.ColumnName);

            // 可见参数列
            foreach (string name in names) table.AddColumn(name, false);

            // 隐藏基线
            table.AddColumn(SheetInfoExportCommand.ColumnOriginalNumber, true);
            table.AddColumn(SheetInfoExportCommand.ColumnOriginalName, true);
            foreach (string name in names) table.AddColumn(OrigPrefix + name, true);
            table.AddColumn(SheetInfoExportCommand.ColumnBatch, true);
            table.AddColumn(SheetInfoExportCommand.ColumnDocFp, true);

            table.SheetName = SheetInfoExportCommand.SheetNameForExport;

            if (rows != null)
            {
                foreach (SheetSnapshot row in rows)
                {
                    if (row == null) continue;

                    List<string> values = new List<string>(names.Count * 2 + 7);
                    values.Add(row.Key);
                    values.Add(row.Number);
                    values.Add(row.Name);

                    foreach (string name in names) values.Add(TryGet(row.Values, name));

                    values.Add(row.Number);   // 原始编号
                    values.Add(row.Name);     // 原始名称
                    foreach (string name in names) values.Add(TryGet(row.Values, name));

                    values.Add(batch ?? string.Empty);
                    values.Add(docFingerprint ?? string.Empty);

                    table.AddRow(values.ToArray());
                }
            }

            return table;
        }

        private static string TryGet(Dictionary<string, string> values, string name)
        {
            if (values == null) return string.Empty;
            string value;
            return values.TryGetValue(name, out value) ? (value ?? string.Empty) : string.Empty;
        }

        /// <summary>参数改动 → 快照/日志里的一格文本（名称 旧 新，参数之间 \u001E）。</summary>
        public static string EncodeParamChanges(IList<ParamChange> changes)
        {
            if (changes == null || changes.Count == 0) return string.Empty;

            List<string> parts = new List<string>(changes.Count);
            foreach (ParamChange change in changes)
            {
                if (change == null || string.IsNullOrEmpty(change.Name)) continue;
                parts.Add(Clean(change.Name) + FieldSep + Clean(change.OldValue) + FieldSep + Clean(change.NewValue));
            }
            return string.Join(ParamSep.ToString(), parts);
        }

        /// <summary>解码快照里的参数格；空串或格式不合法的条目直接丢掉。</summary>
        public static List<ParamChange> DecodeParamChanges(string text)
        {
            List<ParamChange> changes = new List<ParamChange>();
            if (string.IsNullOrEmpty(text)) return changes;

            foreach (string part in text.Split(ParamSep))
            {
                if (string.IsNullOrEmpty(part)) continue;
                string[] fields = part.Split(FieldSep);
                if (fields.Length < 3) continue;

                changes.Add(new ParamChange
                {
                    Name = fields[0],
                    OldValue = fields[1],
                    NewValue = fields[2]
                });
            }
            return changes;
        }

        private static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            // 编码分隔符不能出现在值里，替换掉避免解析错位
            return value.Replace(FieldSep.ToString(), " ")
                        .Replace(ParamSep.ToString(), " ")
                        .Replace("\r", " ")
                        .Replace("\n", " ");
        }
    }
}
