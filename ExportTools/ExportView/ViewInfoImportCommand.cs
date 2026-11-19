﻿
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
using View = Autodesk.Revit.DB.View;

namespace MEPTools.ExportTools.ExportView
{
    /// <summary>
    /// 视图信息导入（Excel → 回写模型），只改白名单列「视图名称」。
    ///
    /// 安全规则：
    ///  - 白名单外的列一律忽略（视图类型、所属图纸编号只作参考）
    ///  - 主键匹配不到 / 不是可改名视图（图纸、明细表、浏览器视图）→ 跳过并记日志
    ///  - 名称为空 / 与当前相同 → 跳过
    ///  - 批内目标重名、或与「本次不改的视图」重名 → 跳过（Revit 不允许视图重名）
    ///  - 二次确认 + 变更日志 + 单事务逐条容错
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ViewInfoImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导入视图信息");

            try
            {
                string filePath = PickFile();
                if (string.IsNullOrWhiteSpace(filePath)) return Result.Cancelled;

                SimpleTable table = LoadViewTable(filePath);
                if (table == null)
                {
                    TaskDialog.Show("导入视图信息", "没有找到合适的表：需要「" + ViewInfoExportCommand.ColumnKey + "」「" + ViewInfoExportCommand.ColumnName + "」两列。");
                    return Result.Cancelled;
                }

                int keyIndex = table.ColumnIndex(ViewInfoExportCommand.ColumnKey);
                int nameIndex = table.ColumnIndex(ViewInfoExportCommand.ColumnName);
                if (keyIndex < 0 || nameIndex < 0)
                {
                    TaskDialog.Show("导入视图信息", "表格结构不对：缺少「" + ViewInfoExportCommand.ColumnKey + "」或「" + ViewInfoExportCommand.ColumnName + "」列。");
                    return Result.Cancelled;
                }

                if (!IsViewWritable(ViewInfoExportCommand.ColumnName))
                {
                    TaskDialog.Show("导入视图信息",
                        "当前安全策略里「" + ViewInfoExportCommand.ColumnName + "」不是可写列，已停止导入。\n请检查 ImportSafetySettings。");
                    return Result.Cancelled;
                }

                // ---------- 第一步：预校验 ----------
                List<PendingRename> pendings = new List<PendingRename>();
                List<string> errors = new List<string>();
                HashSet<ElementId> touched = new HashSet<ElementId>();

                for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    string keyText = table.Cell(rowIndex, keyIndex).Trim();
                    string newName = table.Cell(rowIndex, nameIndex).Trim();

                    int idValue;
                    if (!int.TryParse(keyText, out idValue))
                    {
                        errors.Add($"第 {rowIndex + 2} 行：主键无效「{keyText}」");
                        continue;
                    }

                    View view = doc.GetElement(new ElementId(idValue)) as View;
                    if (view == null)
                    {
                        errors.Add($"第 {rowIndex + 2} 行：主键 {idValue} 不是视图");
                        continue;
                    }

                    if (!ViewInfoExportCommand.IsRenameable(view))
                    {
                        errors.Add($"第 {rowIndex + 2} 行：{view.ViewType} 类型的视图不参与改名");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        errors.Add($"第 {rowIndex + 2} 行：视图名称为空");
                        continue;
                    }

                    if (string.Equals(newName, view.Name, StringComparison.Ordinal)) continue;

                    touched.Add(view.Id);
                    pendings.Add(new PendingRename { View = view, NewName = newName, NameBefore = view.Name, RowIndex = rowIndex + 2 });
                }

                // 批内重名
                HashSet<string> duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IGrouping<string, PendingRename> group in pendings.GroupBy(p => p.NewName, StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Count() > 1)
                    {
                        duplicated.Add(group.Key);
                        errors.Add("批内目标名称重复：" + group.Key);
                    }
                }

                List<PendingRename> valid = pendings.Where(p => !duplicated.Contains(p.NewName)).ToList();
                if (valid.Count == 0)
                {
                    TaskDialog.Show("导入视图信息",
                        "没有需要改名的视图。" + (errors.Count > 0 ? $"\n\n跳过 {errors.Count} 条：\n" + Preview(errors) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第二步：与「不参与本次改名」的既有视图重名检查 ----------
                HashSet<string> occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate))
                {
                    if (!touched.Contains(view.Id)) occupiedNames.Add(view.Name);
                }

                List<PendingRename> finalList = new List<PendingRename>();
                foreach (PendingRename pending in valid)
                {
                    if (occupiedNames.Contains(pending.NewName))
                    {
                        errors.Add($"第 {pending.RowIndex} 行：名称「{pending.NewName}」已被其它视图占用");
                        continue;
                    }
                    finalList.Add(pending);
                }

                if (finalList.Count == 0)
                {
                    TaskDialog.Show("导入视图信息",
                        "没有可以安全改名的视图。" + (errors.Count > 0 ? "\n\n" + Preview(errors) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第三步：二次确认 ----------
                TaskDialog confirm = new TaskDialog("导入视图信息");
                confirm.MainInstruction = "确认批量更新视图名称？";
                confirm.MainContent =
                    $"将修改 {finalList.Count} 个视图的名称" +
                    (errors.Count > 0 ? $"，{errors.Count} 条已被预检跳过" : string.Empty) +
                    "。\n\n只写回「视图名称」列；类型 / 所属图纸编号等列仅作参考，不会写入模型。";
                confirm.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
                if (confirm.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                // ---------- 第四步：单事务写回 + 变更日志 ----------
                string logFolder = Path.GetDirectoryName(filePath) ?? LinkSelectionForm.GetDefaultOutputFolder();
                ImportChangeLog log = new ImportChangeLog(logFolder, "视图信息导入");

                int applied = 0;
                using (Transaction transaction = new Transaction(doc, "批量更新视图名称"))
                {
                    transaction.Start();
                    foreach (PendingRename pending in finalList)
                    {
                        try
                        {
                            pending.View.Name = pending.NewName;
                            applied++;
                            log.Log($"[名称] 主键={pending.View.Id.IntegerValue} 旧={pending.NameBefore} 新={pending.NewName} OK");
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"第 {pending.RowIndex} 行改名失败：{ex.Message}");
                            log.Log($"[名称] 主键={pending.View.Id.IntegerValue} 旧={pending.NameBefore} 新={pending.NewName} 失败：{ex.Message}");
                        }
                    }
                    transaction.Commit();
                }

                bool logSaved = log.SaveIfAny();
                MEPLogger.Instance.EndSession($"Done: Renamed={applied}");

                TaskDialog dialog = new TaskDialog("导入视图信息");
                dialog.MainInstruction = "导入完成";
                dialog.MainContent =
                    $"改名 {applied} 个" + (errors.Count > 0 ? $"，跳过/失败 {errors.Count} 条" : "") +
                    (logSaved ? $"\n\n变更日志：{log.Path}" : "") +
                    (errors.Count > 0 ? "\n\n" + Preview(errors) : string.Empty);
                dialog.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("ViewInfoImport", ex);
                InfoCenterTip.Show("导入视图信息失败，请查看日志。");
                return Result.Failed;
            }
        }

        private static bool IsViewWritable(string columnName)
        {
            foreach (string name in ImportSafetySettings.ViewWritableColumns)
                if (string.Equals(name, columnName, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string Preview(List<string> errors)
        {
            string text = string.Join("\n", errors.Take(10));
            if (errors.Count > 10) text += $"\n… 其余 {errors.Count - 10} 条见日志";
            return text;
        }

        private static string PickFile()
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要导入的视图信息表格";
                dialog.Filter = "Excel 表格 (*.xlsx)|*.xlsx";
                dialog.InitialDirectory = LinkSelectionForm.GetDefaultOutputFolder();
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private static SimpleTable LoadViewTable(string filePath)
        {
            List<SimpleTable> tables = XlsxTableStore.LoadAll(filePath);
            if (tables.Count == 0) return null;

            SimpleTable byName = tables.FirstOrDefault(t => string.Equals(t.SheetName, ViewInfoExportCommand.SheetNameForExport, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            return tables.FirstOrDefault(t => t.ColumnIndex(ViewInfoExportCommand.ColumnKey) >= 0);
        }

        private class PendingRename
        {
            public View View { get; set; }
            public string NewName { get; set; }
            public string NameBefore { get; set; }
            public int RowIndex { get; set; }
        }
    }
}
