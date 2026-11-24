using System;
using System.Collections.Generic;
using System.IO;
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
    /// 图纸信息「导出 + 回读」单入口：
    ///   一个窗口完成两步 —— ① 勾选图纸导出 Excel；② 在 Excel 里改完，点一下就把这份表回读写入模型。
    ///
    /// 稳定性设计（关键）：
    ///  - 打开窗口之前，把所有图纸的 编号/名称/文档指纹 快照进内存；窗口里的“导出”只是把快照写成 xlsx，
    ///    全程不碰 Revit API —— 模态窗口里不调 API、不开事务。
    ///  - 真正的写模型发生在窗口关闭之后，直接复用 SheetInfoImportCommand.Run，
    ///    也就是说 来源/基线校验、差异预览、写前快照、单事务 这九道闸一行不差。
    ///  - 只导出不回读时，命令正常结束并提示文件位置，不影响原有的「导出图纸信息 / 导入图纸信息」两条老命令。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SheetInfoSyncCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("图纸信息同步");

            try
            {
                List<ViewSheet> sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !s.IsTemplate)
                    .OrderBy(s => s.SheetNumber)
                    .ToList();

                if (sheets.Count == 0)
                {
                    MEPLogger.Instance.EndSession("Done: 文档里没有图纸");
                    TaskDialog.Show("图纸信息同步", "当前文档里没有图纸。");
                    return Result.Cancelled;
                }

                // ---------- 先把数据快照下来（窗口里从此不再读模型） ----------
                SyncState state = new SyncState();
                state.Batch = Guid.NewGuid().ToString("N");
                state.DocFingerprint = SheetInfoSafety.GetDocumentFingerprint(doc);
                if (string.IsNullOrEmpty(state.DocFingerprint)) state.DocFingerprint = "未知";
                MEPLogger.Instance.AppendLine("[同步] 批次=" + state.Batch + " 指纹=" + state.DocFingerprint +
                                              " 图纸数=" + sheets.Count);

                List<ExportListItem> items = new List<ExportListItem>();
                foreach (ViewSheet sheet in sheets)
                {
                    string key = sheet.Id.IntegerValue.ToString();
                    state.Rows[key] = new SheetRecord
                    {
                        Key = key,
                        Number = sheet.SheetNumber,
                        Name = sheet.Name
                    };
                    items.Add(new ExportListItem
                    {
                        Key = key,
                        Name = "[" + sheet.SheetNumber + "]  " + sheet.Name
                    });
                }

                SheetInfoSyncAction action = SheetInfoSyncAction.None;
                string importFile = null;

                using (var form = new SheetInfoSyncForm(items, LinkSelectionForm.GetDefaultOutputFolder(), state.Export, null))
                {
                    form.ShowDialog();
                    action = form.Action;
                    importFile = form.ImportFile;
                }

                // ---------- 只导出、未回读 ----------
                if (action != SheetInfoSyncAction.Import || string.IsNullOrWhiteSpace(importFile))
                {
                    if (!string.IsNullOrEmpty(state.LastPath))
                    {
                        MEPLogger.Instance.EndSession("Done: 仅导出 Sheets=" + state.LastCount + " | " + state.LastPath);

                        TaskDialog dialog = new TaskDialog("图纸信息同步");
                        dialog.MainInstruction = "已导出，未回读";
                        dialog.MainContent =
                            "📁 " + state.LastPath + "\n\n" +
                            "在 Excel 里改完并保存后，再运行「图纸信息同步」即可回读；也可以运行「导入图纸信息」。\n\n" +
                            "提示：本次没有写模型，所以没有产生回滚快照；回读时才会生成。";
                        dialog.Show();
                        return Result.Succeeded;
                    }

                    MEPLogger.Instance.EndSession("Done: 未操作");
                    return Result.Cancelled;
                }

                // ---------- 回读：写模型在窗口关闭之后执行，九道闸全开 ----------
                MEPLogger.Instance.EndSession("Done: 导出完成 Sheets=" + state.LastCount +
                                              " | " + state.LastPath + " → 回读 " + importFile);

                Result result = SheetInfoImportCommand.Run(uiapp, importFile);
                return result;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SheetInfoSync", ex);
                InfoCenterTip.Show("图纸信息同步失败，请查看日志。");
                return Result.Failed;
            }
        }

        private class SheetRecord
        {
            public string Key { get; set; }
            public string Number { get; set; }
            public string Name { get; set; }
        }

        /// <summary>窗口用的数据 + 导出实现：只写文件，不碰 Revit API。</summary>
        private class SyncState
        {
            public Dictionary<string, SheetRecord> Rows = new Dictionary<string, SheetRecord>();
            public string Batch;
            public string DocFingerprint;
            public string LastPath;
            public int LastCount;

            public SheetInfoSyncExportResult Export(List<string> keys, string folder)
            {
                SheetInfoSyncExportResult result = new SheetInfoSyncExportResult();
                try
                {
                    SimpleTable table = new SimpleTable(SheetInfoExportCommand.ColumnKey,
                        SheetInfoExportCommand.ColumnNumber, SheetInfoExportCommand.ColumnName);
                    table.AddColumn(SheetInfoExportCommand.ColumnOriginalNumber, true);
                    table.AddColumn(SheetInfoExportCommand.ColumnOriginalName, true);
                    table.AddColumn(SheetInfoExportCommand.ColumnBatch, true);
                    table.AddColumn(SheetInfoExportCommand.ColumnDocFp, true);
                    table.SheetName = SheetInfoExportCommand.SheetNameForExport;

                    foreach (string key in keys)
                    {
                        SheetRecord record;
                        if (!Rows.TryGetValue(key, out record)) continue;
                        table.AddRow(key, record.Number, record.Name,
                            record.Number, record.Name, Batch, DocFingerprint);
                    }

                    if (table.Rows.Count == 0)
                    {
                        result.Success = false;
                        result.Error = "没有取到任何图纸";
                        return result;
                    }

                    string fileName = "图纸信息_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".xlsx";
                    string path = ExportFileUtils.GetUniquePath(folder, fileName);
                    XlsxTableStore.Save(path, table);

                    LastPath = path;
                    LastCount = table.Rows.Count;

                    MEPLogger.Instance.AppendLine("[同步导出] 行数=" + table.Rows.Count + " 文件=" + path);

                    result.Success = true;
                    result.Path = path;
                    result.Count = table.Rows.Count;
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException("SheetInfoSync/Export", ex);
                    result.Success = false;
                    result.Error = ex.Message;
                }
                return result;
            }
        }
    }
}
