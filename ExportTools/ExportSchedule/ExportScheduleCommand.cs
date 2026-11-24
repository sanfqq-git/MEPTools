
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MEPTools.ExportTools.Common;
using MEPTools.ExportTools.Common.Excel;
using MEPTools.Shared;

namespace MEPTools.ExportTools.ExportSchedule
{
    /// <summary>
    /// 明细表内容导出 → Excel（.xlsx），一次性导出、不做回写。
    /// 勾选哪些明细表就导哪些，一个明细表 = 一个 Excel 文件（文件名跟随明细表名，里面就一个同名 Sheet）。
    /// 注意：明细表数据由 Revit 计算生成，导出的是只读快照，改 Excel 不会（也不该）返回模型；
    /// 需要"改完导回模型"的是图纸 / 视图这两条线（对应各自的导入命令）。
    /// 取值直接读 ViewSchedule 的表体数据，不经过 Revit 自带的 .txt/.csv 导出。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ExportScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导出明细表");

            try
            {
                List<ViewSchedule> schedules = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .Where(s => !s.IsTemplate && s.ViewType == ViewType.Schedule)
                    .OrderBy(s => s.Name)
                    .ToList();

                if (schedules.Count == 0)
                {
                    TaskDialog.Show("导出明细表", "当前文档里没有可导出的明细表（不含图纸明细表）。");
                    return Result.Cancelled;
                }

                List<ExportListItem> items = new List<ExportListItem>();
                foreach (ViewSchedule schedule in schedules)
                {
                    // 性能关键：这里只取名字，绝不调用 GetTableData()
                    // —— 之前弹窗前给每张明细表算 "N 行 × M 列"，等于让 Revit 把所有明细表都算一遍，所以卡。
                    items.Add(new ExportListItem
                    {
                        Key = schedule.Id.IntegerValue.ToString(),
                        Name = schedule.Name,
                        Info = string.Empty
                    });
                }

                // 项目浏览器里预选的明细表 → 直接带进窗体勾选（Revit 把浏览器中选中的视图也放进 Selection）
                List<string> preselectedKeys = GetPreselectedScheduleKeys(uiapp.ActiveUIDocument, schedules);

                string formHint = preselectedKeys.Count > 0
                    ? $"已带入项目浏览器预选的 {preselectedKeys.Count} 张明细表，可继续勾选其它；文件名跟随明细表名"
                    : "勾选后每个明细表导出一个 Excel，文件名跟随明细表名（只读快照，不回写模型）";

                List<string> keys;
                string folder;

                using (var form = new ExportListForm(items, "选择要导出的明细表", formHint,
                    true, LinkSelectionForm.GetDefaultOutputFolder(), preselectedKeys))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    keys = form.SelectedKeys;
                    folder = form.OutputFolder;
                }

                int skipped = 0;
                int failed = 0;
                int saved = 0;

                foreach (string key in keys)
                {
                    int elementIdValue;
                    if (!int.TryParse(key, out elementIdValue)) { skipped++; continue; }

                    ViewSchedule schedule = doc.GetElement(new ElementId(elementIdValue)) as ViewSchedule;
                    if (schedule == null) { skipped++; continue; }

                    SimpleTable table = BuildTable(schedule);
                    if (table == null) { skipped++; continue; }

                    MEPLogger.Instance.AppendLine($"[明细表] {schedule.Name} 已读取 {table.Rows.Count} 行");

                    // 一个明细表 = 一个 Excel 文件；文件名 = 明细表名（非法字符换 _，
                    // 同名已存在时由 GetUniquePath 补 _1/_2，不覆盖已有文件）
                    string path = ExportFileUtils.GetUniquePath(folder, BuildFileName(schedule));
                    try
                    {
                        XlsxTableStore.Save(path, table);
                        saved++;
                        MEPLogger.Instance.AppendLine($"[明细表] {schedule.Name} → {Path.GetFileName(path)}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        MEPLogger.Instance.LogException("ExportSchedule.Save", ex);
                        MEPLogger.Instance.AppendLine(
                            $"[明细表] {schedule.Name} 写文件失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }

                if (saved == 0)
                {
                    TaskDialog.Show("导出明细表", failed > 0
                        ? "明细表内容读到了，但写 Excel 全部失败，请查看日志。"
                        : "没有取到任何明细表内容。");
                    MEPLogger.Instance.EndSession($"Failed: Keys={keys.Count}, Skipped={skipped}, Failed={failed}");
                    return failed > 0 ? Result.Failed : Result.Cancelled;
                }

                MEPLogger.Instance.EndSession($"Done: Files={saved}, Skipped={skipped}, Failed={failed}");
                TaskDialog.Show("导出明细表",
                    $"明细表导出完成。\n共导出 {saved} 个 Excel 文件" +
                    (skipped > 0 ? $"，跳过 {skipped} 个明细表" : "") +
                    (failed > 0 ? $"，{failed} 个写文件失败（见日志）" : "") +
                    $"\n\n📁 {folder}\n\n💡 文件名=明细表名（只读快照，改 Excel 不会回写模型）。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportSchedule", ex);
                InfoCenterTip.Show("明细表导出失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>
        /// 取"项目浏览器里预选的明细表"。
        /// Revit 会把项目浏览器中选中的视图同样放进 Selection.GetElementIds()，所以直接读当前选择，
        /// 只保留本次列表里那批（非样板、ViewType=Schedule）的明细表，其它类别/样板一律忽略。
        /// 返回空 = 没有预选，窗体照旧默认全选。
        /// </summary>
        private static List<string> GetPreselectedScheduleKeys(UIDocument uidoc, List<ViewSchedule> schedules)
        {
            List<string> keys = new List<string>();
            if (uidoc == null || schedules == null || schedules.Count == 0) return keys;

            ICollection<ElementId> selectedIds;
            try
            {
                selectedIds = uidoc.Selection.GetElementIds();
            }
            catch
            {
                // 选择读不到就当没有预选，不影响后续出窗体
                return keys;
            }

            if (selectedIds == null || selectedIds.Count == 0) return keys;

            HashSet<int> scheduleIdValues = new HashSet<int>();
            foreach (ViewSchedule schedule in schedules) scheduleIdValues.Add(schedule.Id.IntegerValue);

            HashSet<int> added = new HashSet<int>();
            foreach (ElementId id in selectedIds)
            {
                int value = id.IntegerValue;
                if (!scheduleIdValues.Contains(value)) continue;
                if (!added.Add(value)) continue;
                keys.Add(value.ToString());
            }

            MEPLogger.Instance.AppendLine(
                $"[预选] 当前选中 {selectedIds.Count} 个图元，其中明细表 {keys.Count} 张已带入勾选");

            return keys;
        }

        /// <summary>
        /// 输出文件名 = 明细表名 + .xlsx（不再带时间戳）。
        /// 非法字符（\ / : * ? " &lt; &gt; |）由 SafeFileName 换成 _；过长截断，避免超 Windows 路径长度。
        /// </summary>
        private static string BuildFileName(ViewSchedule schedule)
        {
            string rawName = (schedule != null && !string.IsNullOrWhiteSpace(schedule.Name))
                ? schedule.Name
                : "明细表";

            string name = ExportFileUtils.SafeFileName(rawName);
            if (string.IsNullOrWhiteSpace(name) || name == "Model") name = "明细表";
            if (name.Length > 120) name = name.Substring(0, 120).Trim();

            return name + ".xlsx";
        }

        /// <summary>表头 + 表体 + 汇总各段依次取出，原样拼成一张表（合并单元格只取左上角文本）。</summary>
        private static SimpleTable BuildTable(ViewSchedule schedule)
        {
            TableData data = schedule.GetTableData();
            List<List<string>> rows = new List<List<string>>();
            CellReadStats stats = new CellReadStats();

            int headerRows = AppendSection(schedule, data, SectionType.Header, rows, stats);
            int bodyRows = AppendSection(schedule, data, SectionType.Body, rows, stats);
            int summaryRows = AppendSection(schedule, data, SectionType.Summary, rows, stats);

            // 表头段里"整行全空"的行（分组表头那类）不留，否则 Excel 第一行是空白行；
            // 只丢整行全空的行，任何有内容的单元格都不动。
            while (headerRows > 0 && rows.Count > 1 && IsRowEmpty(rows[0]))
            {
                rows.RemoveAt(0);
                headerRows--;
            }

            if (rows.Count == 0)
            {
                rows.Add(new List<string> { "(空表)" });
            }

            SimpleTable table = SimpleTable.FromRows(rows);
            table.SheetName = schedule.Name;

            LogTableDiagnostics(schedule, table, headerRows, bodyRows, summaryRows, stats);
            return table;
        }

        private static int AppendSection(ViewSchedule schedule, TableData data, SectionType sectionType,
                                         List<List<string>> rows, CellReadStats stats)
        {
            TableSectionData section;
            try
            {
                section = data.GetSectionData(sectionType);
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.AppendLine(
                    $"[明细表] {schedule.Name} 取 {sectionType} 段失败：{ex.GetType().Name}: {ex.Message}");
                section = null;
            }

            if (section == null || section.NumberOfRows <= 0 || section.NumberOfColumns <= 0) return 0;

            int columns = section.NumberOfColumns;
            for (int r = 0; r < section.NumberOfRows; r++)
            {
                List<string> row = new List<string>(columns);
                for (int c = 0; c < columns; c++)
                {
                    row.Add(ReadCellText(schedule, section, sectionType, r, c, stats));
                }
                rows.Add(row);
            }

            // 表体首行的单元格类型写进日志：以后再"丢数据"，一眼就能看出是第几列、什么类型。
            if (sectionType == SectionType.Body)
            {
                LogColumnCellTypes(schedule, section);
            }

            return section.NumberOfRows;
        }

        /// <summary>
        /// 读一个格子的文字。
        /// 主通道是 TableView.GetCellText(SectionType, row, column)：明细表读格子文字就该用它，
        /// 它返回"表上显示的文字"，与单元格类型无关（Parameter / CombinedParameter /
        /// CalculatedValue / Inherited 都能取到）。
        /// 原来用的 TableSectionData.GetCellText(r, c) 只对 Text / ParameterText 返回文字，
        /// 其它类型一律返回空串 —— 这正是 Excel 里"整列整行不见了"的根因。
        /// </summary>
        private static string ReadCellText(ViewSchedule schedule, TableSectionData section, SectionType sectionType,
                                           int row, int column, CellReadStats stats)
        {
            stats.Total++;

            string text = null;
            Exception error = null;

            CellType cellType = CellType.Text;
            try
            {
                cellType = section.GetCellType(row, column);
            }
            catch
            {
                // 取不到类型就当文本处理，走下面的老接口
            }

            // 快速通道：文本类格子沿用老接口，结果和以前完全一致，速度最省。
            if (cellType == CellType.Text || cellType == CellType.ParameterText)
            {
                try
                {
                    text = section.GetCellText(row, column);
                }
                catch (Exception ex)
                {
                    error = ex;
                    text = null;
                }
            }

            // 其它类型（Parameter / CombinedParameter / CalculatedValue / Inherited …）走格式化接口。
            if (string.IsNullOrEmpty(text))
            {
                try
                {
                    text = schedule.GetCellText(sectionType, row, column);
                }
                catch (Exception ex)
                {
                    if (error == null) error = ex;
                    text = null;
                }
            }

            // 兜底：合并单元格里非左上角的格子可能取不到文字，按 GetMergedCell 找左上角再取一次。
            // 只回退一层，不递归，也不改表结构。
            if (string.IsNullOrEmpty(text))
            {
                try
                {
                    TableMergedCell merged = section.GetMergedCell(row, column);
                    if (merged != null
                        && (merged.Top != row || merged.Left != column)
                        && merged.Top >= 0 && merged.Left >= 0
                        && merged.Top < section.NumberOfRows && merged.Left < section.NumberOfColumns)
                    {
                        string mergedText = schedule.GetCellText(sectionType, merged.Top, merged.Left);
                        if (!string.IsNullOrEmpty(mergedText))
                        {
                            text = mergedText;
                            stats.MergedRecovered++;
                        }
                    }
                }
                catch
                {
                    // 合并信息取不到就按空处理，不影响其它格子
                }
            }

            if (error != null)
            {
                stats.Errors++;
                if (stats.FirstError == null)
                {
                    stats.FirstError = error.GetType().Name + ": " + error.Message;
                }
            }

            return text ?? string.Empty;
        }

        private static void LogColumnCellTypes(ViewSchedule schedule, TableSectionData section)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                for (int c = 0; c < section.NumberOfColumns; c++)
                {
                    string typeName = "?";
                    try
                    {
                        typeName = section.GetCellType(0, c).ToString();
                    }
                    catch
                    {
                        typeName = "?";
                    }
                    if (c > 0) sb.Append("，");
                    sb.Append($"第{c + 1}列={typeName}");
                }
                MEPLogger.Instance.AppendLine($"[明细表] {schedule.Name} 表体首行单元格类型：{sb}");
            }
            catch
            {
                // 纯日志，出错不影响导出
            }
        }

        private static void LogTableDiagnostics(ViewSchedule schedule, SimpleTable table,
                                                int headerRows, int bodyRows, int summaryRows,
                                                CellReadStats stats)
        {
            try
            {
                int width = table.ColumnCount;
                foreach (List<string> row in table.Rows)
                {
                    if (row.Count > width) width = row.Count;
                }

                MEPLogger.Instance.AppendLine(
                    $"[明细表] {schedule.Name} 结构：表头 {headerRows} 行 / 表体 {bodyRows} 行 / 汇总 {summaryRows} 行 / 共 {width} 列，读取单元格 {stats.Total} 个");

                if (stats.Errors > 0)
                {
                    MEPLogger.Instance.AppendLine(
                        $"[明细表] {schedule.Name} 读取异常 {stats.Errors} 个单元格，首个异常：{stats.FirstError}");
                }

                if (stats.MergedRecovered > 0)
                {
                    MEPLogger.Instance.AppendLine(
                        $"[明细表] {schedule.Name} 合并单元格按左上角补回 {stats.MergedRecovered} 个");
                }

                List<string> emptyColumns = new List<string>();
                for (int c = 0; c < width; c++)
                {
                    bool hasValue = false;
                    for (int r = 0; r < table.Rows.Count; r++)
                    {
                        if (!string.IsNullOrEmpty(table.Cell(r, c)))
                        {
                            hasValue = true;
                            break;
                        }
                    }

                    if (!hasValue)
                    {
                        string header = c < table.ColumnCount ? table.Headers[c] : string.Empty;
                        emptyColumns.Add(string.IsNullOrEmpty(header)
                            ? $"第{c + 1}列"
                            : $"第{c + 1}列「{header}」");
                    }
                }

                if (emptyColumns.Count > 0)
                {
                    MEPLogger.Instance.AppendLine(
                        $"[明细表] {schedule.Name} 警告：整列无数据 -> {string.Join("、", emptyColumns)}");
                }
            }
            catch
            {
                // 纯日志，出错不影响导出
            }
        }

        private static bool IsRowEmpty(List<string> row)
        {
            if (row == null) return true;
            foreach (string value in row)
            {
                if (!string.IsNullOrEmpty(value)) return false;
            }
            return true;
        }

        /// <summary>本次明细表读取的统计，只用于写日志。</summary>
        private sealed class CellReadStats
        {
            public int Total;
            public int Errors;
            public int MergedRecovered;
            public string FirstError;
        }
    }
}
