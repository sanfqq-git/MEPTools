﻿
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 勾选哪些明细表就导哪些，一个明细表 = 一个 Sheet。
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

                List<string> keys;
                string folder;

                using (var form = new ExportListForm(items, "选择要导出的明细表",
                    "勾选后导出为 Excel：一个明细表一个 Sheet（只读快照，不回写模型）",
                    true, LinkSelectionForm.GetDefaultOutputFolder()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    keys = form.SelectedKeys;
                    folder = form.OutputFolder;
                }

                List<SimpleTable> sheets = new List<SimpleTable>();
                int skipped = 0;

                foreach (string key in keys)
                {
                    int elementIdValue;
                    if (!int.TryParse(key, out elementIdValue)) { skipped++; continue; }

                    ViewSchedule schedule = doc.GetElement(new ElementId(elementIdValue)) as ViewSchedule;
                    if (schedule == null) { skipped++; continue; }

                    SimpleTable table = BuildTable(schedule);
                    if (table == null) { skipped++; continue; }

                    MEPLogger.Instance.AppendLine($"[明细表] {schedule.Name} 已读取 {table.Rows.Count} 行");
                    sheets.Add(table);
                }

                if (sheets.Count == 0)
                {
                    TaskDialog.Show("导出明细表", "没有取到任何明细表内容。");
                    return Result.Cancelled;
                }

                string fileName = "明细表_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                XlsxTableStore.Save(path, sheets);

                MEPLogger.Instance.EndSession($"Done: Sheets={sheets.Count}");
                TaskDialog.Show("导出明细表",
                    $"明细表导出完成。\n导出 {sheets.Count} 个" + (skipped > 0 ? $"，跳过 {skipped} 个" : "") +
                    $"\n\n📁 {path}\n\n💡 每个明细表对应一个 Sheet（只读快照，改 Excel 不会回写模型）。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ExportSchedule", ex);
                InfoCenterTip.Show("明细表导出失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>表头 + 表体 + 汇总各段依次取出，原样拼成一张表（合并单元格只取左上角文本）。</summary>
        private static SimpleTable BuildTable(ViewSchedule schedule)
        {
            TableData data = schedule.GetTableData();
            List<List<string>> rows = new List<List<string>>();

            AppendSection(data, SectionType.Header, rows);
            AppendSection(data, SectionType.Body, rows);
            AppendSection(data, SectionType.Summary, rows);

            if (rows.Count == 0)
            {
                rows.Add(new List<string> { "(空表)" });
            }

            SimpleTable table = SimpleTable.FromRows(rows);
            table.SheetName = schedule.Name;
            return table;
        }

        private static void AppendSection(TableData data, SectionType sectionType, List<List<string>> rows)
        {
            TableSectionData section;
            try
            {
                section = data.GetSectionData(sectionType);
            }
            catch
            {
                section = null;
            }

            if (section == null || section.NumberOfRows <= 0 || section.NumberOfColumns <= 0) return;

            for (int r = 0; r < section.NumberOfRows; r++)
            {
                List<string> row = new List<string>();
                for (int c = 0; c < section.NumberOfColumns; c++)
                {
                    string text = string.Empty;
                    try
                    {
                        text = section.GetCellText(r, c);
                    }
                    catch
                    {
                        text = string.Empty;
                    }
                    row.Add(text ?? string.Empty);
                }
                rows.Add(row);
            }
        }
    }
}
