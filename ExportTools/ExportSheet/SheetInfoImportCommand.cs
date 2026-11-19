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

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 图纸信息导入（Excel → 回写模型）。
    ///
    /// 安全策略（可编辑列白名单 + 危险列显式解锁）：
    ///  - 白名单列「图纸名称」→ 正常写回
    ///  - 危险列「图纸编号」→ 默认不写；用户在弹窗里勾选「允许修改图纸编号」才生效
    ///  - 未勾选时，改了编号的行会被跳过并提示（模型不动）
    ///
    /// 三道闸：
    ///  1) 唯一性预检：批内名称/编号重复、与「本次不改的图纸」重名/重号 → 该行跳过
    ///  2) 二次确认：先告诉用户"将改名称 X 张、编号 Y 张"，用户确认后才写
    ///  3) 变更日志：时间/用户/主键/旧值→新值，落到 Excel 同目录 .txt
    /// 全部改动在单事务内逐条容错，失败不影响其它行。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SheetInfoImportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导入图纸信息");

            try
            {
                string filePath = PickFile();
                if (string.IsNullOrWhiteSpace(filePath)) return Result.Cancelled;

                SimpleTable table = LoadSheetTable(filePath);
                if (table == null)
                {
                    TaskDialog.Show("导入图纸信息", "没有找到合适的表：需要「" + SheetInfoExportCommand.ColumnKey + "」「" + SheetInfoExportCommand.ColumnName + "」两列。");
                    return Result.Cancelled;
                }

                int keyIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnKey);
                int nameIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnName);
                int numberIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnNumber);

                if (keyIndex < 0 || nameIndex < 0)
                {
                    TaskDialog.Show("导入图纸信息", "表格结构不对：缺少「" + SheetInfoExportCommand.ColumnKey + "」或「" + SheetInfoExportCommand.ColumnName + "」列。");
                    return Result.Cancelled;
                }

                // 白名单：只认「图纸名称」；危险列「图纸编号」走开关
                if (!ImportSafetySettings.IsSheetWritable(SheetInfoExportCommand.ColumnName))
                {
                    TaskDialog.Show("导入图纸信息",
                        "当前安全策略里「" + SheetInfoExportCommand.ColumnName + "」不是可写列，已停止导入。\n请检查 ImportSafetySettings。");
                    return Result.Cancelled;
                }

                // ---------- 第一步：粗扫数量，用于弹窗确认 ----------
                int roughNameChanges = 0;
                int roughNumberChanges = 0;
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    int idValue;
                    if (!int.TryParse(table.Cell(i, keyIndex).Trim(), out idValue)) continue;
                    ViewSheet sheet = doc.GetElement(new ElementId(idValue)) as ViewSheet;
                    if (sheet == null) continue;

                    string newName = table.Cell(i, nameIndex).Trim();
                    if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, sheet.Name, StringComparison.Ordinal))
                        roughNameChanges++;

                    if (numberIndex >= 0)
                    {
                        string newNumber = table.Cell(i, numberIndex).Trim();
                        if (!string.IsNullOrWhiteSpace(newNumber) && !string.Equals(newNumber, sheet.SheetNumber, StringComparison.Ordinal))
                            roughNumberChanges++;
                    }
                }

                if (roughNameChanges == 0 && roughNumberChanges == 0)
                {
                    TaskDialog.Show("导入图纸信息", "表格里的名称/编号与模型完全一致，没有需要改动的行。");
                    return Result.Cancelled;
                }

                // ---------- 第二步：确认 + 危险列解锁 ----------
                bool allowNumber = ConfirmAndAskDangerousColumn(roughNameChanges, roughNumberChanges,
                    SheetInfoExportCommand.ColumnNumber);

                // ---------- 第三步：逐行预校验 ----------
                List<SheetChange> changes = new List<SheetChange>();
                List<string> errors = new List<string>();
                HashSet<ElementId> touched = new HashSet<ElementId>();

                for (int rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    string keyText = table.Cell(rowIndex, keyIndex).Trim();
                    string newName = table.Cell(rowIndex, nameIndex).Trim();
                    string newNumber = numberIndex >= 0 ? table.Cell(rowIndex, numberIndex).Trim() : string.Empty;

                    int idValue;
                    if (!int.TryParse(keyText, out idValue))
                    {
                        errors.Add($"第 {rowIndex + 2} 行：主键无效「{keyText}」");
                        continue;
                    }

                    ViewSheet sheet = doc.GetElement(new ElementId(idValue)) as ViewSheet;
                    if (sheet == null)
                    {
                        errors.Add($"第 {rowIndex + 2} 行：主键 {idValue} 不是图纸");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(newName))
                    {
                        errors.Add($"第 {rowIndex + 2} 行：图纸名称为空");
                        continue;
                    }

                    string targetNumber = string.IsNullOrWhiteSpace(newNumber) ? sheet.SheetNumber : newNumber;
                    bool nameChanged = !string.Equals(newName, sheet.Name, StringComparison.Ordinal);
                    bool numberChanged = !string.Equals(targetNumber, sheet.SheetNumber, StringComparison.Ordinal);

                    if (numberChanged && !allowNumber)
                    {
                        errors.Add($"第 {rowIndex + 2} 行：改动了图纸编号「{sheet.SheetNumber}→{targetNumber}」，本次未勾选允许修改编号，已跳过");
                        continue;
                    }

                    if (!nameChanged && !numberChanged) continue; // 没有变化

                    changes.Add(new SheetChange
                    {
                        Sheet = sheet,
                        NewName = newName,
                        NewNumber = targetNumber,
                        NameChanged = nameChanged,
                        NumberChanged = numberChanged,
                        RowIndex = rowIndex + 2,
                        NameBefore = sheet.Name
                    });
                    touched.Add(sheet.Id);
                }

                if (changes.Count == 0)
                {
                    TaskDialog.Show("导入图纸信息",
                        "没有可安全写回的行。" + (errors.Count > 0 ? $"\n\n跳过 {errors.Count} 条：\n" + Preview(errors) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第四步：唯一性预检（批内 + 与本次不改的图纸） ----------
                HashSet<string> duplicatedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IGrouping<string, SheetChange> group in changes.Where(c => c.NameChanged)
                             .GroupBy(c => c.NewName, StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Count() > 1)
                    {
                        duplicatedNames.Add(group.Key);
                        errors.Add("批内目标名称重复：" + group.Key);
                    }
                }

                HashSet<string> duplicatedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IGrouping<string, SheetChange> group in changes.Where(c => c.NumberChanged)
                             .GroupBy(c => c.NewNumber, StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Count() > 1)
                    {
                        duplicatedNumbers.Add(group.Key);
                        errors.Add("批内目标编号重复：" + group.Key);
                    }
                }

                HashSet<string> occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> occupiedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsTemplate))
                {
                    if (touched.Contains(sheet.Id)) continue;
                    occupiedNames.Add(sheet.Name);
                    occupiedNumbers.Add(sheet.SheetNumber);
                }

                List<SheetChange> valid = new List<SheetChange>();
                foreach (SheetChange change in changes)
                {
                    if (change.NameChanged && duplicatedNames.Contains(change.NewName)) continue;
                    if (change.NumberChanged && duplicatedNumbers.Contains(change.NewNumber)) continue;
                    if (change.NameChanged && occupiedNames.Contains(change.NewName))
                    {
                        errors.Add($"第 {change.RowIndex} 行：名称「{change.NewName}」已被其它图纸占用");
                        continue;
                    }
                    if (change.NumberChanged && occupiedNumbers.Contains(change.NewNumber))
                    {
                        errors.Add($"第 {change.RowIndex} 行：编号「{change.NewNumber}」已被其它图纸占用");
                        continue;
                    }
                    valid.Add(change);
                }

                if (valid.Count == 0)
                {
                    TaskDialog.Show("导入图纸信息",
                        "没有可安全写回的行（唯一性预检未通过）。" + (errors.Count > 0 ? "\n\n" + Preview(errors) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第五步：单事务写回 + 变更日志 ----------
                string logFolder = System.IO.Path.GetDirectoryName(filePath) ?? LinkSelectionForm.GetDefaultOutputFolder();
                ImportChangeLog log = new ImportChangeLog(logFolder, "图纸信息导入");

                int renamed = 0, renumbered = 0;
                using (Transaction transaction = new Transaction(doc, "批量更新图纸信息"))
                {
                    transaction.Start();
                    foreach (SheetChange change in valid)
                    {
                        if (change.NameChanged)
                        {
                            try
                            {
                                change.Sheet.Name = change.NewName;
                                renamed++;
                                log.Log($"[名称] 主键={change.Sheet.Id.IntegerValue} 编号={change.Sheet.SheetNumber} 旧={change.NameBefore} 新={change.NewName} OK");
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"第 {change.RowIndex} 行改名失败：{ex.Message}");
                                log.Log($"[名称] 主键={change.Sheet.Id.IntegerValue} 新={change.NewName} 失败：{ex.Message}");
                            }
                        }

                        if (change.NumberChanged && allowNumber)
                        {
                            string oldNumber = change.Sheet.SheetNumber;
                            try
                            {
                                change.Sheet.SheetNumber = change.NewNumber;
                                renumbered++;
                                log.Log($"[编号] 主键={change.Sheet.Id.IntegerValue} 旧={oldNumber} 新={change.NewNumber} OK");
                            }
                            catch (Exception ex)
                            {
                                errors.Add($"第 {change.RowIndex} 行改编号失败：{ex.Message}");
                                log.Log($"[编号] 主键={change.Sheet.Id.IntegerValue} 旧={oldNumber} 新={change.NewNumber} 失败：{ex.Message}");
                            }
                        }
                    }
                    transaction.Commit();
                }

                bool logSaved = log.SaveIfAny();
                MEPLogger.Instance.EndSession($"Done: Renamed={renamed} Renumbered={renumbered} AllowNumber={allowNumber}");

                TaskDialog dialog = new TaskDialog("导入图纸信息");
                dialog.MainInstruction = "导入完成";
                dialog.MainContent =
                    $"名称改名 {renamed} 张" + (allowNumber ? $"，编号修改 {renumbered} 张" : "，编号未改动") +
                    (errors.Count > 0 ? $"\n跳过/失败 {errors.Count} 条" : "") +
                    (logSaved ? $"\n\n变更日志：{log.Path}" : "") +
                    (errors.Count > 0 ? "\n\n" + Preview(errors) : string.Empty);
                dialog.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                MEPLogger.Instance.LogException("SheetInfoImport", ex);
                InfoCenterTip.Show("导入图纸信息失败，请查看日志。");
                return Result.Failed;
            }
        }

        /// <summary>二次确认弹窗：主按钮决定是否继续导入，勾选框决定是否解锁危险列。</summary>
        private static bool ConfirmAndAskDangerousColumn(int nameCount, int numberCount, string dangerousColumnName)
        {
            TaskDialog dialog = new TaskDialog("导入图纸信息");
            dialog.MainInstruction = "确认批量更新图纸信息？";
            dialog.MainContent =
                $"名称将修改 {nameCount} 张；编号在表格里有 {numberCount} 处与模型不同。\n\n" +
                "默认只写回「图纸名称」——编号的差异会被跳过。\n" +
                "勾选下方选项才会同时写入「" + dangerousColumnName + "」。\n\n" +
                "风险提示：Revit 内部数据结构不会因此损坏（编号只是一个字符串标识，ElementId 与图纸参照关系不变），" +
                "但已出版的 PDF / 施工图目录 / 外部台账按旧编号命名，可能会出现对不上的情况。";
            dialog.VerificationText = "允许修改「" + dangerousColumnName + "」（危险，默认关闭）";
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

            TaskDialogResult result = dialog.Show();
            return result == TaskDialogResult.Yes && dialog.WasVerificationChecked();
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
                dialog.Title = "选择要导入的图纸信息表格";
                dialog.Filter = "Excel 表格 (*.xlsx)|*.xlsx";
                dialog.InitialDirectory = LinkSelectionForm.GetDefaultOutputFolder();
                return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private static SimpleTable LoadSheetTable(string filePath)
        {
            List<SimpleTable> tables = XlsxTableStore.LoadAll(filePath);
            if (tables.Count == 0) return null;

            SimpleTable byName = tables.FirstOrDefault(t => string.Equals(t.SheetName, SheetInfoExportCommand.SheetNameForExport, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;

            return tables.FirstOrDefault(t => t.ColumnIndex(SheetInfoExportCommand.ColumnKey) >= 0);
        }

        private class SheetChange
        {
            public ViewSheet Sheet { get; set; }
            public string NewName { get; set; }
            public string NewNumber { get; set; }
            public bool NameChanged { get; set; }
            public bool NumberChanged { get; set; }
            public int RowIndex { get; set; }
            public string NameBefore { get; set; }
        }
    }
}
