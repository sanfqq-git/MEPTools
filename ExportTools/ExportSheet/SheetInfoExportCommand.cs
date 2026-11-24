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

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 图纸信息导出 → Excel，供批量改名使用。
    /// 可见列：主键(ElementId，勿改) | 图纸编号(危险列，导入需勾选) | 图纸名称(可改)
    /// 隐藏列：原始编号 / 原始名称 / 来源批次 / 来源文档（导入端的校验基线，请勿修改）
    /// 只读，不开启写事务。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SheetInfoExportCommand : IExternalCommand
    {
        public const string SheetNameForExport = "图纸信息";
        public const string ColumnKey = "主键";
        public const string ColumnNumber = "图纸编号";
        public const string ColumnName = "图纸名称";

        // ---- 以下四列在 Excel 里是隐藏列：只给导入端校验用 ----
        public const string ColumnOriginalNumber = "原始编号";
        public const string ColumnOriginalName = "原始名称";
        public const string ColumnBatch = "来源批次";
        public const string ColumnDocFp = "来源文档";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导出图纸信息");

            try
            {
                List<ViewSheet> sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber)
                    .ToList();

                if (sheets.Count == 0)
                {
                    TaskDialog.Show("导出图纸信息", "当前文档里没有图纸。");
                    return Result.Cancelled;
                }

                List<ExportListItem> items = new List<ExportListItem>();
                foreach (ViewSheet sheet in sheets)
                {
                    items.Add(new ExportListItem
                    {
                        Key = sheet.Id.IntegerValue.ToString(),
                        Name = sheet.SheetNumber + "  " + sheet.Name
                    });
                }

                List<string> keys;
                string folder;
                using (var form = new ExportListForm(items, "选择要导出的图纸",
                    "导出后在 Excel 里改「图纸名称」（必要时在导入里勾选编号开关再改「图纸编号」），再用「导入图纸信息」写回模型；" +
                    "「主键」列请勿修改，右侧隐藏列是校验基线也请勿修改",
                    true, LinkSelectionForm.GetDefaultOutputFolder()))
                {
                    if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
                    keys = form.SelectedKeys;
                    folder = form.OutputFolder;
                }

                SimpleTable table = new SimpleTable(ColumnKey, ColumnNumber, ColumnName);
                table.AddColumn(ColumnOriginalNumber, true);
                table.AddColumn(ColumnOriginalName, true);
                table.AddColumn(ColumnBatch, true);
                table.AddColumn(ColumnDocFp, true);
                table.SheetName = SheetNameForExport;

                // 一次导出 = 一个批次；文档指纹标识「这份表是从哪个模型导出的」
                string batch = Guid.NewGuid().ToString("N");
                string docFingerprint = SheetInfoSafety.GetDocumentFingerprint(doc);
                if (string.IsNullOrEmpty(docFingerprint)) docFingerprint = "未知";

                foreach (string key in keys)
                {
                    int idValue;
                    if (!int.TryParse(key, out idValue)) continue;
                    ViewSheet sheet = doc.GetElement(new ElementId(idValue)) as ViewSheet;
                    if (sheet == null) continue;
                    table.AddRow(key, sheet.SheetNumber, sheet.Name,
                        sheet.SheetNumber, sheet.Name, batch, docFingerprint);
                }

                if (table.Rows.Count == 0)
                {
                    TaskDialog.Show("导出图纸信息", "没有取到任何图纸。");
                    return Result.Cancelled;
                }

                string fileName = "图纸信息_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                string path = ExportFileUtils.GetUniquePath(folder, fileName);
                XlsxTableStore.Save(path, table);

                MEPLogger.Instance.AppendLine("[导出] 批次=" + batch + " 指纹=" + docFingerprint + " 行数=" + table.Rows.Count);
                MEPLogger.Instance.EndSession("Done: Sheets=" + table.Rows.Count);
                TaskDialog.Show("导出图纸信息",
                    "已导出 " + table.Rows.Count + " 张图纸。\n\n📁 " + path + "\n\n" +
                    "只有「图纸名称」列会被直接写回；「图纸编号」要在导入时勾选才生效。\n" +
                    "右侧 4 个隐藏列（原始编号 / 原始名称 / 来源批次 / 来源文档）是导入校验用的基线，请勿修改——\n" +
                    "改了会导致导入整份拒绝，届时重新导出即可。");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SheetInfoExport", ex);
                InfoCenterTip.Show("图纸信息导出失败，请查看日志。");
                return Result.Failed;
            }
        }
    }
}
