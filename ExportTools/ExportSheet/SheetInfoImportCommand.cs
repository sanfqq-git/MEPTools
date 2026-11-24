
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

namespace MEPTools.ExportTools.ExportSheet
{
    /// <summary>
    /// 图纸信息导入（Excel → 回写模型）。
    ///
    /// 安全策略：可编辑列白名单 + 危险列显式解锁 + 来源/基线校验 + 差异确认 + 写前回滚快照。
    ///
    /// 九道闸：
    ///  0) 共享读 + 内存快照：Excel 开着也能读，且读到的一定是完整文件
    ///  1) 结构体检：必须有「主键」「图纸名称」列
    ///  2) 来源校验：来源批次唯一、来源文档 == 当前文档（不符则整份拒绝）
    ///  3) 基线校验：每行 原始编号/原始名称 必须等于模型现值（不符则整份拒绝并列原因）
    ///  4) 行级校验：主键可解析、必须是图纸、名称非空（不符则整份拒绝）
    ///  5) 危险列开关：勾选才允许改「图纸编号」
    ///  6) 唯一性预检：批内重名重号、与本次不改的图纸撞名撞号 → 该行跳过
    ///  7) 差异清单：TaskDialog 逐行「旧→新」列出，人确认后才写
    ///  8) 写前快照：旧值→新值 落盘 CSV，写不成功就中止（保证可回退）
    ///  9) 单事务写回 + 变更日志，逐行容错
    /// 全部改动在单事务内，失败不影响其它行。
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SheetInfoImportCommand : IExternalCommand
    {
        private const int MaxDiffRows = 15;
        private const int MaxReasonRows = 20;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiapp = commandData.Application;
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            string filePath = PickFile();
            if (string.IsNullOrWhiteSpace(filePath)) return Result.Cancelled;

            return Run(uiapp, filePath);
        }

        /// <summary>
        /// 按指定文件执行导入（九道闸全开），供「图纸信息同步」窗体直接复用。
        /// 与命令入口走的是完全相同的代码路径，只是文件不由这里挑。
        /// </summary>
        public static Result Run(UIApplication uiapp, string filePath)
        {
            Document doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null) return Result.Cancelled;

            MEPLogger.Instance.StartSession("导入图纸信息");

            try
            {
                // ---------- 第 0 步：读文件（单独兜底，避免一律报“导入失败”） ----------
                SimpleTable table;
                try
                {
                    table = LoadSheetTable(filePath);
                }
                catch (Exception ex)
                {
                    MEPLogger.Instance.LogException("SheetInfoImport/Load", ex);
                    TaskDialog.Show("导入图纸信息", DescribeLoadFailure(ex, filePath));
                    return Result.Cancelled;
                }

                if (table == null)
                {
                    TaskDialog.Show("导入图纸信息",
                        "没有找到合适的表：需要「" + SheetInfoExportCommand.ColumnKey + "」「" + SheetInfoExportCommand.ColumnName + "」两列。\n\n文件：" + filePath);
                    return Result.Cancelled;
                }

                // ---------- 第 1 步：结构体检 ----------
                int keyIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnKey);
                int nameIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnName);
                int numberIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnNumber);
                int origNumberIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnOriginalNumber);
                int origNameIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnOriginalName);
                int batchIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnBatch);
                int docFpIndex = table.ColumnIndex(SheetInfoExportCommand.ColumnDocFp);

                if (keyIndex < 0 || nameIndex < 0)
                {
                    TaskDialog.Show("导入图纸信息",
                        "表格结构不对：缺少「" + SheetInfoExportCommand.ColumnKey + "」或「" + SheetInfoExportCommand.ColumnName + "」列。");
                    return Result.Cancelled;
                }

                if (!ImportSafetySettings.IsSheetWritable(SheetInfoExportCommand.ColumnName))
                {
                    TaskDialog.Show("导入图纸信息",
                        "当前安全策略里「" + SheetInfoExportCommand.ColumnName + "」不是可写列，已停止导入。\n请检查 ImportSafetySettings。");
                    return Result.Cancelled;
                }

                bool hasBaseline = origNumberIndex >= 0 && origNameIndex >= 0;
                bool hasSource = batchIndex >= 0 && docFpIndex >= 0;

                // ---------- 参数列（可写文本参数，叠加在固定三列右边） ----------
                // 只看“可见列”：隐藏列全是基线（原始编号 / 原始名称 / 原始_参数名 / 来源…）。
                List<ParamColumn> paramColumns = new List<ParamColumn>();
                for (int c = 0; c < table.ColumnCount; c++)
                {
                    string header = table.Headers[c];
                    if (table.IsColumnHidden(c)) continue;
                    if (SheetInfoParams.IsReserved(header)) continue;
                    paramColumns.Add(new ParamColumn
                    {
                        Name = header,
                        ValueIndex = c,
                        BaselineIndex = table.ColumnIndex(SheetInfoParams.OrigPrefix + header)
                    });
                }

                // ---------- 第 2/3/4 步：整份校验（任何一行不过 → 整份拒绝） ----------
                List<ImportRow> rows = ReadRows(table, doc, keyIndex, nameIndex, numberIndex,
                    origNumberIndex, origNameIndex, batchIndex, docFpIndex, hasBaseline);

                if (rows.Count == 0)
                {
                    TaskDialog.Show("导入图纸信息", "表格里没有有效数据行。\n\n文件：" + filePath);
                    return Result.Cancelled;
                }

                List<string> rejectReasons = new List<string>();
                foreach (ImportRow row in rows) rejectReasons.AddRange(row.Reasons);
                if (hasSource) CollectSourceReasons(rows, doc, rejectReasons);

                if (rejectReasons.Count > 0)
                {
                    foreach (string reason in rejectReasons) MEPLogger.Instance.AppendLine("[拒绝] " + reason);

                    TaskDialog rejectDialog = new TaskDialog("导入图纸信息");
                    rejectDialog.MainInstruction = "表格与当前模型对不上，已整份拒绝（共 " + rejectReasons.Count + " 处）";
                    rejectDialog.MainContent =
                        "模型没有做任何修改。\n\n" +
                        SheetInfoSafety.FormatLines(rejectReasons, MaxReasonRows) + "\n\n" +
                        "修正方式：重新用「导出图纸信息」导出新表，只改「图纸名称」（必要时再改「图纸编号」），D~G 隐藏列不要动。";
                    rejectDialog.Show();
                    return Result.Cancelled;
                }

                // 老表 / 非本工具导出的表：强警告 + 显式勾选才放行
                if (!hasBaseline || !hasSource)
                {
                    if (!ConfirmLegacyTable(hasBaseline, hasSource)) return Result.Cancelled;
                }

                // ---------- 第 5 步：逐行差异预扫（弹窗确认用；只算不写） ----------
                // 名称/编号：沿用老规则（名称可写、编号需勾选解锁）。
                // 参数列：只有「导出时的原值 == 模型现值」才允许覆盖，否则说明模型被别人改过 → 跳过并报出来。
                int roughNameChanges = 0;
                int roughNumberChanges = 0;
                int roughParamChanges = 0;

                foreach (ImportRow row in rows)
                {
                    if (!row.HasSheet) continue;
                    ViewSheet sheet = row.Sheet;

                    row.NameChanged = !string.IsNullOrWhiteSpace(row.NewName) &&
                                      !string.Equals(row.NewName, sheet.Name, StringComparison.Ordinal);
                    row.NumberChanged = row.NewNumber.Length > 0 &&
                                        !string.Equals(row.NewNumber, sheet.SheetNumber, StringComparison.Ordinal);

                    if (row.NameChanged) roughNameChanges++;
                    if (row.NumberChanged) roughNumberChanges++;

                    foreach (ParamColumn column in paramColumns)
                    {
                        string want = SheetInfoParams.Normalize(table.Cell(row.TableRowIndex, column.ValueIndex));
                        string now = SheetInfoParams.GetValue(sheet, column.Name);

                        if (now == null)
                        {
                            row.ParamNotes.Add("第 " + row.RowIndex + " 行：图纸上没有参数「" + column.Name + "」，跳过该参数");
                            continue;
                        }
                        if (string.Equals(want, now, StringComparison.Ordinal)) continue;   // 没变化

                        if (column.BaselineIndex < 0)
                        {
                            row.ParamNotes.Add("第 " + row.RowIndex + " 行：参数「" + column.Name +
                                               "」没有基线列（老表 / 手工表），跳过该参数");
                            continue;
                        }

                        string baseline = SheetInfoParams.Normalize(table.Cell(row.TableRowIndex, column.BaselineIndex));
                        if (!string.Equals(baseline, now, StringComparison.Ordinal))
                        {
                            row.ParamNotes.Add("第 " + row.RowIndex + " 行：参数「" + column.Name +
                                               "」模型现值「" + Shorten(now) + "」与导出时「" + Shorten(baseline) +
                                               "」不同（可能被别人改过），跳过该参数");
                            continue;
                        }

                        row.ParamChanges.Add(new SheetInfoParams.ParamChange
                        {
                            Name = column.Name,
                            OldValue = now,
                            NewValue = want
                        });
                        roughParamChanges++;
                    }
                }

                if (roughNameChanges == 0 && roughNumberChanges == 0 && roughParamChanges == 0)
                {
                    TaskDialog.Show("导入图纸信息", "表格里的名称/编号/参数与模型完全一致，没有需要改动的行。");
                    return Result.Cancelled;
                }

                // ---------- 危险列：图纸编号 ----------
                bool allowNumber = ConfirmAndAskDangerousColumn(roughNameChanges, roughNumberChanges,
                    roughParamChanges, SheetInfoExportCommand.ColumnNumber);

                // ---------- 逐行差异计算 ----------
                List<SheetChange> changes = new List<SheetChange>();
                List<string> errors = new List<string>();
                HashSet<ElementId> touched = new HashSet<ElementId>();

                foreach (ImportRow row in rows)
                {
                    if (!row.HasSheet) continue; // 保险：前面已整份拒绝

                    foreach (string note in row.ParamNotes) errors.Add(note);

                    ViewSheet sheet = row.Sheet;
                    string targetNumber = row.NewNumber.Length == 0 ? sheet.SheetNumber : row.NewNumber;

                    if (row.NumberChanged && !allowNumber)
                    {
                        errors.Add("第 " + row.RowIndex + " 行：改动了图纸编号「" + sheet.SheetNumber + "→" + targetNumber +
                                   "」，本次未勾选允许修改编号，已跳过整行");
                        continue;
                    }

                    if (!row.NameChanged && !row.NumberChanged && row.ParamChanges.Count == 0) continue; // 没有变化

                    changes.Add(new SheetChange
                    {
                        Sheet = sheet,
                        NewName = row.NewName,
                        NewNumber = targetNumber,
                        NameChanged = row.NameChanged,
                        NumberChanged = row.NumberChanged,
                        RowIndex = row.RowIndex,
                        NameBefore = sheet.Name,
                        NumberBefore = sheet.SheetNumber,
                        ParamChanges = row.ParamChanges
                    });
                    touched.Add(sheet.Id);
                }

                if (changes.Count == 0)
                {
                    TaskDialog.Show("导入图纸信息",
                        "没有可安全写回的行。" + (errors.Count > 0 ? "\n\n跳过 " + errors.Count + " 条：\n" + Preview(errors, 10) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第 6 步：唯一性预检（批内 + 与本次不改的图纸） ----------
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
                        errors.Add("第 " + change.RowIndex + " 行：名称「" + change.NewName + "」已被其它图纸占用");
                        continue;
                    }
                    if (change.NumberChanged && occupiedNumbers.Contains(change.NewNumber))
                    {
                        errors.Add("第 " + change.RowIndex + " 行：编号「" + change.NewNumber + "」已被其它图纸占用");
                        continue;
                    }
                    valid.Add(change);
                }

                if (valid.Count == 0)
                {
                    TaskDialog.Show("导入图纸信息",
                        "没有可安全写回的行（唯一性预检未通过）。" + (errors.Count > 0 ? "\n\n" + Preview(errors, 10) : ""));
                    return Result.Cancelled;
                }

                // ---------- 第 7 步：差异清单确认 ----------
                if (!ConfirmDiff(valid, errors, filePath)) return Result.Cancelled;

                // ---------- 第 8 步：写前回滚快照（写不成功就不写模型） ----------
                string folder = Path.GetDirectoryName(filePath) ?? LinkSelectionForm.GetDefaultOutputFolder();
                List<SheetInfoSafety.SnapshotRow> snapshotRows = new List<SheetInfoSafety.SnapshotRow>();
                foreach (SheetChange change in valid)
                {
                    snapshotRows.Add(new SheetInfoSafety.SnapshotRow
                    {
                        Key = change.Sheet.Id.IntegerValue.ToString(),
                        OldNumber = change.NumberBefore,
                        OldName = change.NameBefore,
                        NewNumber = change.NumberChanged ? change.NewNumber : change.NumberBefore,
                        NewName = change.NameChanged ? change.NewName : change.NameBefore,
                        Params = SheetInfoParams.EncodeParamChanges(change.ParamChanges)
                    });
                }

                string snapshotPath = SheetInfoSafety.WriteSnapshot(folder, snapshotRows, Path.GetFileName(filePath));
                if (snapshotPath == null)
                {
                    TaskDialog.Show("导入图纸信息",
                        "回滚快照写不进磁盘（目录只读 / 被占用 / 无权限）：\n" + folder +
                        "\n\n为保证能回退，本次导入已中止，模型未做任何修改。\n可以换一个可写目录重新导出后再导入。");
                    return Result.Cancelled;
                }
                MEPLogger.Instance.AppendLine("[快照] " + snapshotPath);

                // ---------- 第 9 步：单事务写回 + 变更日志 ----------
                ImportChangeLog log = new ImportChangeLog(folder, "图纸信息导入");

                int renamed = 0, renumbered = 0, paramsWritten = 0;
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
                                log.Log("[名称] 主键=" + change.Sheet.Id.IntegerValue + " 编号=" + change.Sheet.SheetNumber +
                                        " 旧=" + change.NameBefore + " 新=" + change.NewName + " OK");
                            }
                            catch (Exception ex)
                            {
                                errors.Add("第 " + change.RowIndex + " 行改名失败：" + ex.Message);
                                log.Log("[名称] 主键=" + change.Sheet.Id.IntegerValue + " 新=" + change.NewName + " 失败：" + ex.Message);
                            }
                        }

                        if (change.NumberChanged && allowNumber)
                        {
                            string oldNumber = change.Sheet.SheetNumber;
                            try
                            {
                                change.Sheet.SheetNumber = change.NewNumber;
                                renumbered++;
                                log.Log("[编号] 主键=" + change.Sheet.Id.IntegerValue + " 旧=" + oldNumber + " 新=" + change.NewNumber + " OK");
                            }
                            catch (Exception ex)
                            {
                                errors.Add("第 " + change.RowIndex + " 行改编号失败：" + ex.Message);
                                log.Log("[编号] 主键=" + change.Sheet.Id.IntegerValue + " 旧=" + oldNumber + " 新=" + change.NewNumber + " 失败：" + ex.Message);
                            }
                        }

                        // 文本参数：逐条容错，写不进的单独报出来（不影响其它参数）
                        foreach (SheetInfoParams.ParamChange param in change.ParamChanges)
                        {
                            string error = SheetInfoParams.SetValue(change.Sheet, param.Name, param.NewValue);
                            if (error == null)
                            {
                                paramsWritten++;
                                log.Log("[参数] 主键=" + change.Sheet.Id.IntegerValue + " 名=" + param.Name +
                                        " 旧=" + param.OldValue + " 新=" + param.NewValue + " OK");
                            }
                            else
                            {
                                errors.Add("第 " + change.RowIndex + " 行：" + error);
                                log.Log("[参数] 主键=" + change.Sheet.Id.IntegerValue + " 名=" + param.Name + " 失败：" + error);
                            }
                        }
                    }
                    transaction.Commit();
                }

                SheetInfoSafety.RememberLastImport(filePath);

                bool logSaved = log.SaveIfAny();
                MEPLogger.Instance.EndSession("Done: Renamed=" + renamed + " Renumbered=" + renumbered +
                                              " Params=" + paramsWritten +
                                              " AllowNumber=" + allowNumber + " Snapshot=" + snapshotPath);

                TaskDialog dialog = new TaskDialog("导入图纸信息");
                dialog.MainInstruction = "导入完成";
                dialog.MainContent =
                    "名称改名 " + renamed + " 张" + (allowNumber ? "，编号修改 " + renumbered + " 张" : "，编号未改动") +
                    (paramsWritten > 0 ? "，参数写入 " + paramsWritten + " 处" : "") +
                    (errors.Count > 0 ? "\n跳过/失败 " + errors.Count + " 条" : "") +
                    (logSaved ? "\n\n变更日志：" + log.Path : string.Empty) +
                    "\n\n回滚快照：" + snapshotPath +
                    "\n（要退回导入前状态，运行「撤销图纸导入」并选这份快照）" +
                    (errors.Count > 0 ? "\n\n" + Preview(errors, 10) : string.Empty);
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

        /// <summary>读行 + 行级校验（主键/图纸/名称/基线）。任何一行有 Reasons 都会被整份拒绝。</summary>
        private static List<ImportRow> ReadRows(SimpleTable table, Document doc,
            int keyIndex, int nameIndex, int numberIndex,
            int origNumberIndex, int origNameIndex, int batchIndex, int docFpIndex, bool checkBaseline)
        {
            List<ImportRow> rows = new List<ImportRow>();

            for (int i = 0; i < table.Rows.Count; i++)
            {
                string keyText = table.Cell(i, keyIndex).Trim();
                string newName = table.Cell(i, nameIndex).Trim();
                string newNumber = numberIndex >= 0 ? table.Cell(i, numberIndex).Trim() : string.Empty;
                string baseNumber = origNumberIndex >= 0 ? table.Cell(i, origNumberIndex).Trim() : string.Empty;
                string baseName = origNameIndex >= 0 ? table.Cell(i, origNameIndex).Trim() : string.Empty;

                // 整行空白忽略（容忍用户删行后留下的空行）
                if (keyText.Length == 0 && newName.Length == 0 && newNumber.Length == 0 &&
                    baseNumber.Length == 0 && baseName.Length == 0)
                    continue;

                ImportRow row = new ImportRow
                {
                    RowIndex = i + 2,
                    TableRowIndex = i,
                    NewName = newName,
                    NewNumber = newNumber,
                    BaselineNumber = baseNumber,
                    BaselineName = baseName,
                    Batch = batchIndex >= 0 ? table.Cell(i, batchIndex).Trim() : string.Empty,
                    DocFp = docFpIndex >= 0 ? table.Cell(i, docFpIndex).Trim() : string.Empty
                };

                int idValue;
                if (!int.TryParse(keyText, out idValue))
                {
                    row.Reasons.Add("第 " + (i + 2) + " 行：主键无效「" + keyText + "」");
                    rows.Add(row);
                    continue;
                }

                ViewSheet sheet = doc.GetElement(new ElementId(idValue)) as ViewSheet;
                if (sheet == null)
                {
                    row.Reasons.Add("第 " + (i + 2) + " 行：主键 " + idValue + " 不是图纸");
                    rows.Add(row);
                    continue;
                }

                row.Sheet = sheet;

                if (string.IsNullOrWhiteSpace(newName))
                    row.Reasons.Add("第 " + (i + 2) + " 行：图纸名称为空");

                if (checkBaseline)
                {
                    if (baseNumber.Length == 0 || baseName.Length == 0)
                    {
                        row.Reasons.Add("第 " + (i + 2) + " 行：缺少基线值（原始编号/原始名称被清空），无法校验");
                    }
                    else if (!string.Equals(baseNumber, sheet.SheetNumber, StringComparison.Ordinal) ||
                             !string.Equals(baseName, sheet.Name, StringComparison.Ordinal))
                    {
                        row.Reasons.Add("第 " + (i + 2) + " 行：基线不符——表内「" + baseNumber + " / " + baseName +
                                        "」，模型现在「" + sheet.SheetNumber + " / " + sheet.Name +
                                        "」。这份表可能是旧导出，或主键被改动过");
                    }
                }

                rows.Add(row);
            }
            return rows;
        }

        /// <summary>来源校验：批次唯一 + 文档指纹一致。</summary>
        private static void CollectSourceReasons(List<ImportRow> rows, Document doc, List<string> reasons)
        {
            HashSet<string> batches = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> docFps = new HashSet<string>(StringComparer.Ordinal);

            foreach (ImportRow row in rows)
            {
                if (!row.HasSheet) continue; // 硬错误已在 Reasons 里

                if (row.Batch.Length == 0)
                    reasons.Add("第 " + row.RowIndex + " 行：缺少「来源批次」（隐藏列被清空，或不是本工具导出的表）");
                else
                    batches.Add(row.Batch);

                if (row.DocFp.Length == 0)
                    reasons.Add("第 " + row.RowIndex + " 行：缺少「来源文档」（隐藏列被清空）");
                else
                    docFps.Add(row.DocFp);
            }

            if (batches.Count > 1)
                reasons.Add("这份表混入了 " + batches.Count + " 个不同的导出批次（不同批次的行不能拼在一张表里）");

            string currentFp = SheetInfoSafety.GetDocumentFingerprint(doc);
            if (currentFp.Length == 0)
            {
                MEPLogger.Instance.AppendLine("[来源] 当前文档取不到指纹，跳过文档比对");
                return;
            }

            foreach (string fp in docFps)
            {
                if (string.Equals(fp, currentFp, StringComparison.Ordinal)) continue;
                reasons.Add("这份表来自另一个文档：表内指纹「" + SheetInfoSafety.Shorten(fp, 12) +
                            "」，当前文档指纹「" + SheetInfoSafety.Shorten(currentFp, 12) +
                            "」。请在本模型里重新导出后再导入");
                break;
            }
        }

        /// <summary>老表 / 缺基线：强警告 + 必须勾选才放行。</summary>
        private static bool ConfirmLegacyTable(bool hasBaseline, bool hasSource)
        {
            TaskDialog dialog = new TaskDialog("导入图纸信息");
            dialog.MainInstruction = "这份表格缺少来源 / 基线信息";

            StringBuilder sb = new StringBuilder();
            if (!hasBaseline)
            {
                sb.AppendLine("· 缺隐藏列「" + SheetInfoExportCommand.ColumnOriginalNumber + "」「" + SheetInfoExportCommand.ColumnOriginalName + "」（基线）");
            }
            if (!hasSource)
            {
                sb.AppendLine("· 缺隐藏列「" + SheetInfoExportCommand.ColumnBatch + "」「" + SheetInfoExportCommand.ColumnDocFp + "」（来源）");
            }
            sb.AppendLine();
            sb.AppendLine("这样无法校验：这份表是不是当前模型导出的、主键有没有被改动、模型在导出后有没有变过。");
            sb.AppendLine("继续导入的风险：可能把名称/编号写到错误的图纸上，或用旧数据覆盖新数据。");
            sb.AppendLine();
            sb.AppendLine("如果这张表是手工整理的，请逐行核对「主键」是否指向正确的图纸。");
            dialog.MainContent = sb.ToString();

            dialog.VerificationText = "我已知晓：该表未通过来源校验，仍要导入";
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;

            return dialog.Show() == TaskDialogResult.Yes && dialog.WasVerificationChecked();
        }

        /// <summary>二次确认弹窗：主按钮决定是否继续导入，勾选框决定是否解锁危险列。</summary>
        private static bool ConfirmAndAskDangerousColumn(int nameCount, int numberCount, int paramCount, string dangerousColumnName)
        {
            TaskDialog dialog = new TaskDialog("导入图纸信息");
            dialog.MainInstruction = "确认批量更新图纸信息？";
            dialog.MainContent =
                "名称将修改 " + nameCount + " 张；编号在表格里有 " + numberCount + " 处与模型不同；" +
                "文本参数有 " + paramCount + " 处会被写入。\n\n" +
                "默认只写回「图纸名称」和文本参数——编号的差异会被跳过。\n" +
                "勾选下方选项才会同时写入「" + dangerousColumnName + "」。\n\n" +
                "参数写入规则：只有「导出时的原值 == 模型现值」才允许覆盖；模型在导出后被别人改过的参数一律跳过并报出来。\n\n" +
                "风险提示：Revit 内部数据结构不会因此损坏（编号只是一个字符串标识，ElementId 与图纸参照关系不变），" +
                "但已出版的 PDF / 施工图目录 / 外部台账按旧编号命名，可能会出现对不上的情况。";
            dialog.VerificationText = "允许修改「" + dangerousColumnName + "」（危险，默认关闭）";
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;

            TaskDialogResult result = dialog.Show();
            return result == TaskDialogResult.Yes && dialog.WasVerificationChecked();
        }

        /// <summary>差异清单：逐行「旧→新」，含将被跳过的行，人确认后才写。</summary>
        private static bool ConfirmDiff(List<SheetChange> valid, List<string> errors, string filePath)
        {
            int nameCount = 0, numberCount = 0, paramCount = 0, shownRows = 0;
            StringBuilder sb = new StringBuilder();

            foreach (SheetChange change in valid)
            {
                if (change.NameChanged) nameCount++;
                if (change.NumberChanged) numberCount++;
                paramCount += change.ParamChanges.Count;

                StringBuilder line = new StringBuilder();
                line.Append("第 ").Append(change.RowIndex).Append(" 行  主键 ").Append(change.Sheet.Id.IntegerValue);
                if (change.NumberChanged) line.Append("  |  编号 ").Append(change.NumberBefore).Append(" → ").Append(change.NewNumber);
                if (change.NameChanged) line.Append("  |  名称 ").Append(change.NameBefore).Append(" → ").Append(change.NewName);
                if (change.ParamChanges.Count > 0) line.Append("  |  参数 ").Append(change.ParamChanges.Count).Append(" 处");

                MEPLogger.Instance.AppendLine("[差异] " + line);
                foreach (SheetInfoParams.ParamChange param in change.ParamChanges)
                {
                    MEPLogger.Instance.AppendLine("[差异]     参数 " + param.Name + "：" +
                                                  Shorten(param.OldValue) + " → " + Shorten(param.NewValue));
                }

                if (shownRows < MaxDiffRows)
                {
                    sb.AppendLine(line.ToString());
                    int shownParams = 0;
                    foreach (SheetInfoParams.ParamChange param in change.ParamChanges)
                    {
                        if (shownParams >= 3)
                        {
                            sb.AppendLine("        … 其余 " + (change.ParamChanges.Count - 3) + " 处参数见日志");
                            break;
                        }
                        sb.AppendLine("        └ " + param.Name + "：" +
                                      Shorten(param.OldValue) + " → " + Shorten(param.NewValue));
                        shownParams++;
                    }
                    shownRows++;
                }
            }
            if (valid.Count > shownRows) sb.AppendLine("… 其余 " + (valid.Count - shownRows) + " 行已写入日志");

            MEPLogger.Instance.AppendLine("[差异] 合计待写入 " + valid.Count + " 行：名称 " + nameCount + " 张，编号 " +
                                          numberCount + " 张，参数 " + paramCount + " 处");

            if (errors.Count > 0)
            {
                foreach (string error in errors) MEPLogger.Instance.AppendLine("[跳过] " + error);
                sb.AppendLine();
                sb.AppendLine("另有 " + errors.Count + " 条会被跳过：");
                sb.AppendLine(Preview(errors, 10));
            }

            if (SheetInfoSafety.IsLikelyOpenInExcel(filePath))
            {
                sb.AppendLine();
                sb.AppendLine("⚠ 该表格看起来仍在 Excel 中打开：本次读到的是磁盘上已保存的内容，Excel 里未保存的修改不会生效。");
            }

            TaskDialog dialog = new TaskDialog("导入图纸信息");
            dialog.MainInstruction = "确认写入 " + valid.Count + " 张图纸？（名称 " + nameCount + " 张 / 编号 " +
                                     numberCount + " 张 / 参数 " + paramCount + " 处）";
            dialog.MainContent = sb.ToString();
            dialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
            dialog.DefaultButton = TaskDialogResult.No;

            return dialog.Show() == TaskDialogResult.Yes;
        }

        /// <summary>读取失败的分类提示：告诉用户下一步该做什么。</summary>
        private static string DescribeLoadFailure(Exception ex, string filePath)
        {
            string reason;
            if (ex is FileNotFoundException)
                reason = "找不到文件，可能已被移动、改名或删除。";
            else if (ex is UnauthorizedAccessException)
                reason = "没有读取权限（文件可能被加密、被策略锁定，或位于不可访问的目录）。";
            else if (ex is InvalidDataException)
                reason = "文件不是有效的 .xlsx，或内容不完整（常见于 Excel 正在保存那一刻，或拷了半截文件）。\n请等一秒再试，或另存为一个新的 .xlsx 再导入。";
            else if (ex is IOException)
                reason = "文件被别的程序独占占用，读不出来（常见于 Excel 不允许读、或杀毒软件正在扫描）。\n请在 Excel 里保存后关闭该工作簿，再重新导入。";
            else
                reason = ex.Message;

            return "读取表格失败：\n" + filePath + "\n\n" + reason;
        }

        private static string Preview(List<string> errors, int max)
        {
            return SheetInfoSafety.FormatLines(errors, max);
        }

        /// <summary>挑一份 xlsx；传了 initialPath 就把目录和文件名预置成它。</summary>
        public static string PickFile(string initialPath = null)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择要导入的图纸信息表格";
                dialog.Filter = "Excel 表格 (*.xlsx)|*.xlsx";

                string dir = LinkSelectionForm.GetDefaultOutputFolder();
                if (!string.IsNullOrWhiteSpace(initialPath))
                {
                    string presetDir = Path.GetDirectoryName(initialPath);
                    if (!string.IsNullOrWhiteSpace(presetDir) && Directory.Exists(presetDir)) dir = presetDir;
                }
                dialog.InitialDirectory = dir;

                if (!string.IsNullOrWhiteSpace(initialPath) && File.Exists(initialPath))
                    dialog.FileName = Path.GetFileName(initialPath);

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

        private class ImportRow
        {
            public int RowIndex { get; set; }        // Excel 里的行号（含表头偏移），报错用
            public int TableRowIndex { get; set; }   // SimpleTable 的 0 基行号，取单元格用
            public ViewSheet Sheet { get; set; }
            public bool HasSheet { get { return Sheet != null; } }
            public string NewName { get; set; }
            public string NewNumber { get; set; }
            public string BaselineNumber { get; set; }
            public string BaselineName { get; set; }
            public string Batch { get; set; }
            public string DocFp { get; set; }

            // 预扫阶段算出来的差异（后面只读，不再重算）
            public bool NameChanged { get; set; }
            public bool NumberChanged { get; set; }
            public List<SheetInfoParams.ParamChange> ParamChanges { get; private set; }
            public List<string> ParamNotes { get; private set; }
            public List<string> Reasons { get; private set; }

            public ImportRow()
            {
                ParamChanges = new List<SheetInfoParams.ParamChange>();
                ParamNotes = new List<string>();
                Reasons = new List<string>();
            }
        }

        private class ParamColumn
        {
            public string Name { get; set; }
            public int ValueIndex { get; set; }
            public int BaselineIndex { get; set; }
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
            public string NumberBefore { get; set; }
            public List<SheetInfoParams.ParamChange> ParamChanges { get; set; }

            public SheetChange()
            {
                ParamChanges = new List<SheetInfoParams.ParamChange>();
            }
        }

        /// <summary>弹窗里用的短文本（太长会把对话框撑爆）。</summary>
        private static string Shorten(string value)
        {
            if (string.IsNullOrEmpty(value)) return "（空）";
            return value.Length <= 24 ? value : value.Substring(0, 24) + "…";
        }
    }
}
