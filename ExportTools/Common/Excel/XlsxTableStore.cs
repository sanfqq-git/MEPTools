
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MEPTools.ExportTools.Common.Excel
{
    /// <summary>
    /// 零依赖的 .xlsx 读写（只用 .NET Framework 自带的 System.IO.Compression + System.Xml.Linq）。
    ///
    /// 为什么这样做：
    ///   - 不引任何 NuGet（NPOI/EPPlus/ClosedXML 都有版本冲突风险，历史上有把 Revit 拖崩的案例）；
    ///   - 也不用 Office Interop（依赖本机 Excel + COM，最不稳）；
    ///   - OOXML 本质是一个 zip 包 + 几个 xml，这里只写「多 Sheet + 文本单元格」的最小合法结构，
    ///     写入时用 inlineStr（不生成 sharedStrings），Excel 可直接打开；用户用 Excel 编辑保存后，
    ///     Excel 会改写成 sharedStrings 结构，所以读取端同时兼容 sharedStrings / inlineStr / 数字 / 布尔。
    ///
    /// 限制（够用且可控）：只处理单元格文本，不写样式、公式、合并单元格、日期格式。
    /// </summary>
    public static class XlsxTableStore
    {
        private const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string NsPkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

        // ================= 写 =================

        public static void Save(string path, SimpleTable sheet)
        {
            Save(path, new List<SimpleTable> { sheet });
        }

        public static void Save(string path, IList<SimpleTable> sheets)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path");
            if (sheets == null || sheets.Count == 0) sheets = new List<SimpleTable> { new SimpleTable("说明") };

            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) Directory.CreateDirectory(folder);

            if (File.Exists(path)) File.Delete(path);

            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml", BuildContentTypes(sheets.Count));
                WriteEntry(zip, "_rels/.rels", BuildRootRels());
                WriteEntry(zip, "xl/workbook.xml", BuildWorkbook(sheets));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(sheets.Count));
                WriteEntry(zip, "xl/styles.xml", BuildStyles());

                for (int i = 0; i < sheets.Count; i++)
                {
                    WriteEntry(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", BuildSheet(sheets[i]));
                }
            }
        }

        private static void WriteEntry(ZipArchive zip, string entryName, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static string BuildContentTypes(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + i + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRels()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"" + NsPkgRel + "\">" +
                   "<Relationship Id=\"rId1\" Type=\"" + NsRel + "/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildWorkbook(IList<SimpleTable> sheets)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"" + NsMain + "\" xmlns:r=\"" + NsRel + "\"><sheets>");
            for (int i = 0; i < sheets.Count; i++)
            {
                string name = GetSafeSheetName(sheets, i);
                sb.Append("<sheet name=\"" + Xml(name) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            }
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string BuildWorkbookRels(int sheetCount)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"" + NsPkgRel + "\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId" + i + "\" Type=\"" + NsRel + "/worksheet\" Target=\"worksheets/sheet" + i + ".xml\"/>");
            sb.Append("<Relationship Id=\"rId" + (sheetCount + 1) + "\" Type=\"" + NsRel + "/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildStyles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<styleSheet xmlns=\"" + NsMain + "\">" +
                   "<fonts count=\"1\"><font><sz val=\"11\"/><name val=\"宋体\"/></font></fonts>" +
                   "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                   "<borders count=\"1\"><border/></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
                   "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                   "</styleSheet>";
        }

        private static string BuildSheet(SimpleTable table)
        {
            StringBuilder sb = new StringBuilder();

            int width = table.ColumnCount;
            foreach (List<string> row in table.Rows)
                if (row.Count > width) width = row.Count;

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"" + NsMain + "\">");
            sb.Append(BuildColumns(table, width));
            sb.Append("<sheetData>");

            for (int r = -1; r < table.Rows.Count; r++)
            {
                sb.Append("<row r=\"" + (r + 2) + "\">");
                for (int c = 0; c < width; c++)
                {
                    string value = r < 0
                        ? (c < table.Headers.Count ? table.Headers[c] : string.Empty)
                        : (c < table.Rows[r].Count ? table.Rows[r][c] : string.Empty);
                    if (string.IsNullOrEmpty(value)) continue;

                    sb.Append("<c r=\"" + ColumnName(c) + (r + 2) + "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
                    sb.Append(Xml(value));
                    sb.Append("</t></is></c>");
                }
                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        /// <summary>隐藏列声明：连续隐藏列合并为一段 &lt;col min max hidden="1"/&gt;。</summary>
        private static string BuildColumns(SimpleTable table, int width)
        {
            if (table == null || table.HiddenColumns == null) return string.Empty;

            StringBuilder sb = new StringBuilder();
            int start = -1;
            for (int c = 0; c < width; c++)
            {
                bool hidden = c < table.HiddenColumns.Count && table.HiddenColumns[c];
                if (hidden)
                {
                    if (start < 0) start = c;
                }
                else if (start >= 0)
                {
                    AppendCol(sb, start, c - 1);
                    start = -1;
                }
            }
            if (start >= 0) AppendCol(sb, start, width - 1);

            return sb.Length == 0 ? string.Empty : "<cols>" + sb.ToString() + "</cols>";
        }

        private static void AppendCol(StringBuilder sb, int fromIndex, int toIndex)
        {
            sb.Append("<col min=\"" + (fromIndex + 1) + "\" max=\"" + (toIndex + 1) + "\" width=\"9\" hidden=\"1\" customWidth=\"1\"/>");
        }

        private static string GetSafeSheetName(IList<SimpleTable> sheets, int index)
        {
            // Excel 限制：≤31 字符、不能含 : \ / ? * [ ]、不能为空、不能重名
            string baseName = "Sheet" + (index + 1);
            string candidate = baseName;
            if (index < sheets.Count && sheets[index] != null && !string.IsNullOrWhiteSpace(sheets[index].SheetName))
                candidate = sheets[index].SheetName;

            char[] invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            StringBuilder sb = new StringBuilder();
            foreach (char ch in (candidate ?? string.Empty))
                sb.Append(invalid.Contains(ch) ? '_' : ch);
            string name = sb.ToString().Trim();
            if (name.Length == 0) name = baseName;
            if (name.Length > 31) name = name.Substring(0, 31);

            // 与已用名去重
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < index; i++)
            {
                string n = sheets[i] != null && !string.IsNullOrWhiteSpace(sheets[i].SheetName) ? sheets[i].SheetName : "Sheet" + (i + 1);
                used.Add(n);
            }
            if (used.Contains(name))
            {
                int suffix = 2;
                while (used.Contains(name + "_" + suffix)) suffix++;
                string withSuffix = name + "_" + suffix;
                name = withSuffix.Length > 31 ? withSuffix.Substring(0, 31) : withSuffix;
            }
            return name;
        }

        private static string ColumnName(int index)
        {
            StringBuilder sb = new StringBuilder();
            int value = index + 1;
            while (value > 0)
            {
                int remainder = (value - 1) % 26;
                sb.Insert(0, (char)('A' + remainder));
                value = (value - 1) / 26;
            }
            return sb.ToString();
        }

        private static string Xml(string value)
        {
            if (value == null) return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                        .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        // ================= 读 =================

        /// <summary>
        /// 打开 .xlsx 包（只读）。
        ///
        /// 为什么不用 ZipFile.OpenRead：它内部请求 FileShare.Read（我打开时别人只许读），
        /// Excel 打开工作簿时一直持有可写句柄，协商失败 → IOException「文件正被另一进程使用」。
        /// 这里改用 FileShare.ReadWrite，允许与 Excel 共存（读的是磁盘上已保存的内容）。
        ///
        /// 为什么先整包读成内存快照：ZipArchive 是懒加载，解析期间会反复读底层流，
        /// 若 Excel 恰在此时保存（整包重写）就会读到半新半旧的数据；先复制一份再解析最稳。
        /// </summary>
        private static ZipArchive OpenPackage(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException("找不到表格文件：" + path, path);

            MemoryStream snapshot = new MemoryStream();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.CopyTo(snapshot);
            }
            snapshot.Position = 0;
            return new ZipArchive(snapshot, ZipArchiveMode.Read);
        }

        public static List<string> GetSheetNames(string path)
        {
            using (ZipArchive zip = OpenPackage(path))
            {
                XDocument workbook = LoadXml(zip, "xl/workbook.xml");
                if (workbook == null) return new List<string>();
                XNamespace ns = NsMain;
                return workbook.Descendants(ns + "sheet")
                               .Select(s => (string)s.Attribute("name"))
                               .Where(n => n != null)
                               .ToList();
            }
        }

        public static SimpleTable Load(string path, int sheetIndex)
        {
            List<SimpleTable> tables = LoadAll(path);
            if (sheetIndex < 0 || sheetIndex >= tables.Count) return new SimpleTable();
            return tables[sheetIndex];
        }

        public static List<SimpleTable> LoadAll(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException(path);

            List<SimpleTable> result = new List<SimpleTable>();
            using (ZipArchive zip = OpenPackage(path))
            {
                XDocument workbook = LoadXml(zip, "xl/workbook.xml");
                if (workbook == null) return result;

                XNamespace ns = NsMain;
                XNamespace relNs = NsRel;

                Dictionary<string, string> relTargets = LoadWorkbookRels(zip);
                List<string> sharedStrings = LoadSharedStrings(zip);

                foreach (XElement sheet in workbook.Descendants(ns + "sheet"))
                {
                    string relId = (string)sheet.Attribute(relNs + "id");
                    string target = relId != null && relTargets.ContainsKey(relId) ? relTargets[relId] : null;
                    if (target == null) continue;

                    string entryName = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                    XDocument sheetXml = LoadXml(zip, entryName);
                    if (sheetXml == null) continue;

                    SimpleTable parsed = ParseSheet(sheetXml, sharedStrings);
                    parsed.SheetName = (string)sheet.Attribute("name");
                    result.Add(parsed);
                }
            }
            return result;
        }

        private static Dictionary<string, string> LoadWorkbookRels(ZipArchive zip)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            XDocument rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
            if (rels == null) return map;

            XNamespace ns = NsPkgRel;
            foreach (XElement rel in rels.Descendants(ns + "Relationship"))
            {
                string id = (string)rel.Attribute("Id");
                string target = (string)rel.Attribute("Target");
                if (id != null && target != null) map[id] = target;
            }
            return map;
        }

        private static List<string> LoadSharedStrings(ZipArchive zip)
        {
            List<string> list = new List<string>();
            XDocument doc = LoadXml(zip, "xl/sharedStrings.xml");
            if (doc == null) return list;

            XNamespace ns = NsMain;
            foreach (XElement si in doc.Descendants(ns + "si"))
            {
                StringBuilder sb = new StringBuilder();
                foreach (XElement t in si.Descendants(ns + "t")) sb.Append(t.Value);
                list.Add(sb.ToString());
            }
            return list;
        }

        private static SimpleTable ParseSheet(XDocument sheetXml, List<string> sharedStrings)
        {
            XNamespace ns = NsMain;
            List<List<string>> rows = new List<List<string>>();

            XElement sheetData = sheetXml.Descendants(ns + "sheetData").FirstOrDefault();
            if (sheetData == null) return new SimpleTable();

            foreach (XElement rowElement in sheetData.Elements(ns + "row"))
            {
                List<string> row = new List<string>();
                foreach (XElement cell in rowElement.Elements(ns + "c"))
                {
                    int columnIndex = ColumnIndexFromRef((string)cell.Attribute("r"));
                    if (columnIndex < 0) continue;
                    while (row.Count <= columnIndex) row.Add(string.Empty);

                    string type = (string)cell.Attribute("t");
                    string value = string.Empty;

                    if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
                    {
                        XElement v = cell.Element(ns + "v");
                        int index = v == null ? -1 : ParseIntSafe(v.Value);
                        if (index >= 0 && index < sharedStrings.Count) value = sharedStrings[index];
                    }
                    else if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
                    {
                        StringBuilder sb = new StringBuilder();
                        foreach (XElement t in cell.Descendants(ns + "t")) sb.Append(t.Value);
                        value = sb.ToString();
                    }
                    else
                    {
                        XElement v = cell.Element(ns + "v");
                        value = v == null ? string.Empty : v.Value;
                        if (string.Equals(type, "b", StringComparison.OrdinalIgnoreCase))
                            value = value == "1" ? "TRUE" : "FALSE";
                    }

                    row[columnIndex] = value;
                }
                rows.Add(row);
            }

            return SimpleTable.FromRows(rows);
        }

        private static int ColumnIndexFromRef(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int index = 0;
            foreach (char ch in cellRef)
            {
                if (ch >= 'A' && ch <= 'Z') index = index * 26 + (ch - 'A' + 1);
                else if (ch >= 'a' && ch <= 'z') index = index * 26 + (ch - 'a' + 1);
                else break;
            }
            return index - 1;
        }

        private static int ParseIntSafe(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : -1;
        }

        private static XDocument LoadXml(ZipArchive zip, string entryName)
        {
            ZipArchiveEntry entry = zip.GetEntry(entryName);
            if (entry == null) return null;
            using (Stream stream = entry.Open())
            {
                return XDocument.Load(stream);
            }
        }
    }
}
