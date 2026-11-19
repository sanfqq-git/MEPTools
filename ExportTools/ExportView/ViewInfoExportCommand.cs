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
using View = Autodesk.Revit.DB.View;

namespace MEPTools.ExportTools.ExportView
{
    /// <summary>
    /// 视图信息导出 → Excel，供批量改名使用。
    /// 列：主键(ElementId，勿改) | 视图名称(可改) | 视图类型(只读) | 所属图纸编号(只读)
    /// 只读，不开启写事务。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ViewInfoExportCommand : IExternalCommand
    {
        public const string SheetNameForExport = "视图信息";
        public const string ColumnKey = "主键";
        public const string ColumnName = "视图名称";
        public const string ColumnType = "视图类型";
        public const string ColumnSheet = "所属图纸编号";

        /// <summary>这些视图不参与改名（图纸、明细表、浏览器内部视图）。</summary>
        public static bool IsRenameable(View view)
        {
            if (view == null || view.IsTemplate) return false;
            switch (view.ViewType)
            {
                case ViewType.DrawingSheet:
                case ViewType.Schedule:
                case ViewType.ProjectBrowser:
                case ViewType.SystemBrowser:
                case ViewType.Internal:
                case ViewType.Undefined:
                case ViewType.Report:
                case ViewType.CostReport:
                case ViewType.LoadsReport:
                case ViewType.PresureLossReport:
                case ViewType.SystemsAnalysisReport:
                    return false;
                default:
                    return true;
            }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导出视图信息");

            try
            {
                List<View> views = new FilteredElementCollector(doc)
                    .OfClass(typeof(View)).Cast<View>()
                    .Where(IsRenameable)
                    .OrderBy(v => v.Name)
                    .ToList();

                if (views.Count == 0)
                {
                    TaskDialog.Show("导出视图信息", "当前文档里没有可改名的视图。");
                    return Result.Cancelled;
                }

                Dictionary<int, List<string>> sheetNumbersByViewId = GetSheetNumbersByViewId(doc);

                List<ExportListItem> items = new List<ExportListItem>();
                foreach (View view in views)
                {
                    items.Add(new ExportListItem
                    {
                        Key = view.Id.IntegerValue.ToString(),
                        Name = view.Name,
                        Info = view.ViewType.ToString()
                    });
                }

                List<string> keys;
                string folder;
                using (var form = new ExportListForm(items, "选择要导出的视图",
                    "导出后在 Excel 里改「视图名称」，再用「导入视图信息」写回模型；主键列请勿修改",
                    true, LinkSelectionForm.GetDefaultOutputFolder()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    keys = form.SelectedKeys;
                    folder = form.OutputFolder;
                }

                SimpleTable table = new SimpleTable(ColumnKey, ColumnName, ColumnType, ColumnSheet);
                table.SheetName = SheetNameForExport;

                foreach (string key in keys)
                {
                    int idValue;
                    if (!int.TryParse(key, out idValue)) continue;
                    View view = doc.GetElement(new ElementId(idValue)) as View;
                    if (view == null) continue;

                    List<string> sheets = sheetNumbersByViewId.ContainsKey(idValue) ? sheetNumbersByViewId[idValue] : new List<string>();
                    table.AddRow(key, view.Name, view.ViewType.ToString(), string.Join("、", sheets));
                }

                if (table.Rows.Count == 0)
                {
                    TaskDialog.Show("导出视图信息", "没有取到任何视图。");
                    return Result.Cancelled;
                }

                string fileName = "视图信息_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                XlsxTableStore.Save(path, table);

                MEPLogger.Instance.EndSession($"Done: Views={table.Rows.Count}");
                TaskDialog.Show("导出视图信息",
                    $"已导出 {table.Rows.Count} 个视图。\n\n📁 {path}\n\n" +
                    "只有「视图名称」列会被导入写回；「主键」仅作匹配依据，请勿修改。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ViewInfoExport", ex);
                InfoCenterTip.Show("视图信息导出失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>视图 → 所在图纸编号（一个视图可能被放在多张图纸上，用「、」连接）。</summary>
        private static Dictionary<int, List<string>> GetSheetNumbersByViewId(Document doc)
        {
            Dictionary<int, List<string>> map = new Dictionary<int, List<string>>();
            try
            {
                var viewports = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>();
                foreach (Viewport viewport in viewports)
                {
                    if (viewport.ViewId == null) continue;
                    ViewSheet sheet = doc.GetElement(viewport.SheetId) as ViewSheet;
                    if (sheet == null) continue;

                    int viewId = viewport.ViewId.IntegerValue;
                    if (!map.ContainsKey(viewId)) map[viewId] = new List<string>();
                    if (!map[viewId].Contains(sheet.SheetNumber)) map[viewId].Add(sheet.SheetNumber);
                }
            }
            catch
            {
                // 取不到不影响主流程
            }
            return map;
        }
    }
}
