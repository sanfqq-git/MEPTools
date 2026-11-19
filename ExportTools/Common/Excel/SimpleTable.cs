
using System.Collections.Generic;

namespace MEPTools.ExportTools.Common.Excel
{
    /// <summary>
    /// 各导出模块统一使用的轻量表格模型：一行表头 + 若干数据行。
    /// 只负责在内存里组织数据，落地格式由 ITableStore 的实现决定（CSV / 未来 xlsx）。
    /// </summary>
    public class SimpleTable
    {
        public List<string> Headers { get; private set; }
        public List<List<string>> Rows { get; private set; }

        /// <summary>该表在 Excel 里对应的 Sheet 名（可空，空则自动取名）。</summary>
        public string SheetName { get; set; }

        public SimpleTable(params string[] headers)
        {
            Headers = new List<string>(headers ?? new string[0]);
            Rows = new List<List<string>>();
        }

        public int ColumnCount
        {
            get { return Headers.Count; }
        }

        /// <summary>按表头名取列号；找不到返回 -1。</summary>
        public int ColumnIndex(string header)
        {
            if (string.IsNullOrEmpty(header)) return -1;
            return Headers.FindIndex(h => string.Equals(h, header, System.StringComparison.OrdinalIgnoreCase));
        }

        public void AddColumn(string header)
        {
            Headers.Add(header);
        }

        public void AddRow(params string[] values)
        {
            Rows.Add(new List<string>(values ?? new string[0]));
        }

        /// <summary>用「第一行作表头」的行集合构造表格（明细表/图纸这类整块取值的场景用）。</summary>
        public static SimpleTable FromRows(IList<List<string>> rows)
        {
            SimpleTable table = new SimpleTable();
            if (rows == null || rows.Count == 0) return table;

            foreach (string value in rows[0]) table.Headers.Add(value ?? string.Empty);
            for (int i = 1; i < rows.Count; i++)
            {
                List<string> row = new List<string>();
                foreach (string value in rows[i]) row.Add(value ?? string.Empty);
                table.Rows.Add(row);
            }
            return table;
        }

        public string Cell(int rowIndex, int columnIndex)
        {
            if (rowIndex < 0 || rowIndex >= Rows.Count) return string.Empty;
            List<string> row = Rows[rowIndex];
            if (columnIndex < 0 || columnIndex >= row.Count) return string.Empty;
            return row[columnIndex];
        }
    }
}
