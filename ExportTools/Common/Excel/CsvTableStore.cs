
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MEPTools.ExportTools.Common.Excel
{
    /// <summary>
    /// CSV 落地实现（UTF-8 带 BOM，Excel 双击即可打开、可直接编辑、可另存）。
    ///
    /// 为什么先用 CSV：项目当前没有任何 Excel 组件依赖（无 EPPlus/NPOI/Interop）。
    /// 需要真 .xlsx 时，只要在同一命名空间下加一个实现（如 XlsxTableStore），
    /// 调用方（各 Command）只改一行入口，不用动业务代码。
    ///
    /// 已知限制：不支持单元格内的换行符（CSV 引号内跨行的写法未实现），
    ///          参数值里出现换行时建议先替换为空格（在各 Command 里处理）。
    /// </summary>
    public static class CsvTableStore
    {
        public static void Save(string path, SimpleTable table)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            if (table == null) throw new ArgumentNullException("table");

            StringBuilder sb = new StringBuilder();
            for (int c = 0; c < table.ColumnCount; c++)
            {
                if (c > 0) sb.Append(',');
                sb.Append(Escape(table.Headers[c]));
            }
            sb.Append("\r\n");

            foreach (List<string> row in table.Rows)
            {
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(Escape(c < row.Count ? row[c] : string.Empty));
                }
                sb.Append("\r\n");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static SimpleTable Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException(path);

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0) return new SimpleTable();

            string[] headers = ParseLine(lines[0]).ToArraySafe();
            SimpleTable table = new SimpleTable(headers);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                List<string> cells = ParseLine(lines[i]);
                table.AddRow(cells.ToArraySafe());
            }
            return table;
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            bool needQuote = value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0 ||
                             value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0 ||
                             value.StartsWith(" ") || value.EndsWith(" ");
            if (!needQuote) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static List<string> ParseLine(string line)
        {
            List<string> cells = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(c);
                }
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }

    internal static class ListExtensions
    {
        /// <summary>把 List&lt;string&gt; 转成 string[]（net48 / C# 7.3 无 List.ToArray 扩展已存在，这里做空值保护）。</summary>
        public static string[] ToArraySafe(this List<string> list)
        {
            return list == null ? new string[0] : list.ToArray();
        }
    }
}
